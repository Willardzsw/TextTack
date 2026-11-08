using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// The pure part of 自動選取 — which window and which element a point lands on, and the chain of
/// boxes the wheel steps through. Kept apart from the Win32 and UI Automation calls so it can be
/// tested without a desktop.
/// </summary>
internal static class CaptureHoverRules
{
    /// <summary>Index of the first rectangle, front to back, that contains <paramref name="p"/>; -1 for none.</summary>
    public static int TopmostAt(IReadOnlyList<Rectangle> zOrdered, Point p)
    {
        for (int i = 0; i < zOrdered.Count; i++)
            if (zOrdered[i].Contains(p)) return i;
        return -1;
    }

    /// <summary>
    /// The child to go down into: the smallest one containing <paramref name="p"/>, so a panel laid
    /// behind its siblings never wins over the button on top of it. -1 when none contains it.
    /// </summary>
    public static int PickChild(IReadOnlyList<Rectangle> children, Point p)
    {
        int best = -1;
        long bestArea = long.MaxValue;
        for (int i = 0; i < children.Count; i++)
        {
            var r = children[i];
            if (r.Width <= 0 || r.Height <= 0 || !r.Contains(p)) continue;
            long area = (long)r.Width * r.Height;
            if (area < bestArea)
            {
                best = i;
                bestArea = area;
            }
        }
        return best;
    }

    /// <summary>
    /// The boxes the wheel steps through, outermost first: each clipped to the one outside it (an
    /// element scrolled half out of its list is framed where it shows), and a box that is the same
    /// as the one outside it, within <paramref name="tolerance"/> pixels on every edge, dropped —
    /// a window and its client pane would otherwise be two wheel steps that change nothing.
    /// </summary>
    public static List<Rectangle> Chain(IEnumerable<Rectangle> outerToInner, Rectangle clip, int tolerance = 2)
    {
        var chain = new List<Rectangle>();
        var outer = clip;
        foreach (var raw in outerToInner)
        {
            var r = Rectangle.Intersect(raw, outer);
            if (r.Width < 2 || r.Height < 2) continue;
            if (chain.Count > 0 && Near(chain[^1], r, tolerance)) continue;
            chain.Add(r);
            outer = r;
        }
        return chain;
    }

    private static bool Near(Rectangle a, Rectangle b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Right - b.Right) <= tolerance && Math.Abs(a.Bottom - b.Bottom) <= tolerance;
}

/// <summary>
/// 自動選取視窗 / 自動選取介面元素: what lies under the pointer on the frozen desktop — the monitor,
/// the window, and the UI Automation elements inside it — as the boxes the capture offers to frame.
/// </summary>
/// <remarks>
/// <para>The windows are listed the moment the desktop is frozen, before the capture window covers
/// it: asked afterwards, every point would land on the capture itself.</para>
///
/// <para>Elements are not hit-tested for the same reason. Each window's element tree is walked down
/// from its root instead, one level at a time and only along the path the pointer is on, on a
/// background thread: an element query crosses into the other process and can take anything from a
/// millisecond to a hung application's timeout. What has been read is kept, so moving within a
/// window already explored is answered from memory, on the UI thread, without waiting.</para>
/// </remarks>
internal sealed class CaptureElementPicker : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Deeper than any real interface; a guard against a provider that reports itself as its own child.</summary>
    private const int MaxDepth = 48;

    private readonly IReadOnlyList<(IntPtr Hwnd, Rectangle Bounds)> _windows;
    private readonly Rectangle[] _windowBounds;
    private readonly Rectangle[] _monitors;
    private readonly Rectangle _desktop;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<IntPtr, Node> _roots = new();

    private readonly AutoResetEvent _wake = new(false);
    private (IntPtr Hwnd, Point Point)? _pending;
    private readonly object _pendingLock = new();
    private Thread? _worker;
    private volatile bool _disposed;

    /// <summary>Raised on the UI thread when more of a window's elements have been read.</summary>
    public event EventHandler? Updated;

    private CaptureElementPicker(
        IReadOnlyList<(IntPtr, Rectangle)> windows, Rectangle[] monitors, Rectangle desktop, Dispatcher dispatcher)
    {
        _windows = windows;
        _windowBounds = windows.Select(w => w.Item2).ToArray();
        _monitors = monitors;
        _desktop = desktop;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Lists the windows on the desktop as it is now. Call straight after the screenshot and before
    /// anything of the capture is shown, on the UI thread.
    /// </summary>
    public static CaptureElementPicker Snapshot(Rectangle desktop)
    {
        var monitors = System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).ToArray();
        return new CaptureElementPicker(ListWindows(desktop), monitors, desktop, Dispatcher.CurrentDispatcher);
    }

    /// <summary>
    /// The boxes at <paramref name="p"/> (physical pixels), outermost first, from what is known now.
    /// Where a window's elements have not been read that far yet, they are asked for, and
    /// <see cref="Updated"/> says when to ask again.
    /// </summary>
    public List<Rectangle> Resolve(Point p, bool windows, bool elements)
    {
        var path = new List<Rectangle>();
        if (windows)
        {
            foreach (var m in _monitors)
                if (m.Contains(p)) { path.Add(m); break; }
        }

        int w = CaptureHoverRules.TopmostAt(_windowBounds, p);
        if (w >= 0)
        {
            var (hwnd, bounds) = _windows[w];
            if (windows) path.Add(bounds);
            if (elements && !Walk(hwnd, p, path)) Request(hwnd, p);
        }

        return CaptureHoverRules.Chain(path, _desktop);
    }

    /// <summary>Adds the known elements under <paramref name="p"/>; false where the path runs into what has not been read.</summary>
    private bool Walk(IntPtr hwnd, Point p, List<Rectangle> path)
    {
        if (!_roots.TryGetValue(hwnd, out var node)) return false;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            var kids = node.Children;
            if (kids is null) return false;
            int i = CaptureHoverRules.PickChild(kids.Select(k => k.Bounds).ToArray(), p);
            if (i < 0) return true;
            node = kids[i];
            path.Add(node.Bounds);
        }
        return true;
    }

    private void Request(IntPtr hwnd, Point p)
    {
        if (_disposed) return;
        lock (_pendingLock) _pending = (hwnd, p);
        if (_worker is null)
        {
            _worker = new Thread(Work) { IsBackground = true, Name = "Capture element picker" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }
        _wake.Set();
    }

    private void Work()
    {
        while (!_disposed)
        {
            _wake.WaitOne();
            (IntPtr Hwnd, Point Point)? job;
            lock (_pendingLock)
            {
                job = _pending;
                _pending = null;
            }
            if (job is not { } j || _disposed) continue;

            try
            {
                if (Load(j.Hwnd, j.Point) && !_disposed)
                    _dispatcher.BeginInvoke(() => { if (!_disposed) Updated?.Invoke(this, EventArgs.Empty); });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Reading elements for the capture failed");
            }
        }
    }

    /// <summary>Reads, on this thread, whatever is missing along the path to <paramref name="p"/>.</summary>
    private bool Load(IntPtr hwnd, Point p)
    {
        bool changed = false;
        if (!_roots.TryGetValue(hwnd, out var node))
        {
            AutomationElement? root = null;
            try { root = AutomationElement.FromHandle(hwnd); }
            catch (Exception ex) { Log.Debug(ex, "No automation element for window {Hwnd}", hwnd); }
            var bounds = _windows.FirstOrDefault(w => w.Hwnd == hwnd).Bounds;
            node = new Node(bounds, root);
            if (root is null) node.Children = [];
            node = _roots.GetOrAdd(hwnd, node);
            changed = true;
        }

        for (int depth = 0; depth < MaxDepth && !_disposed; depth++)
        {
            var kids = node.Children;
            if (kids is null)
            {
                kids = ReadChildren(node.Element);
                node.Children = kids;
                changed = true;
            }
            int i = CaptureHoverRules.PickChild(kids.Select(k => k.Bounds).ToArray(), p);
            if (i < 0) break;
            node = kids[i];
        }
        return changed;
    }

    private static readonly CacheRequest ChildCache = MakeCache();

    private static CacheRequest MakeCache()
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        return cache;
    }

    private static Node[] ReadChildren(AutomationElement? element)
    {
        if (element is null) return [];
        try
        {
            AutomationElementCollection found;
            using (ChildCache.Activate())
                found = element.FindAll(TreeScope.Children, Automation.ControlViewCondition);

            var kids = new List<Node>(found.Count);
            foreach (AutomationElement child in found)
            {
                try
                {
                    if (child.Cached.IsOffscreen) continue;
                    var r = child.Cached.BoundingRectangle;
                    if (r.IsEmpty || r.Width < 1 || r.Height < 1 ||
                        double.IsInfinity(r.Width) || double.IsInfinity(r.Height)) continue;
                    kids.Add(new Node(
                        Rectangle.FromLTRB((int)Math.Round(r.Left), (int)Math.Round(r.Top),
                            (int)Math.Round(r.Right), (int)Math.Round(r.Bottom)),
                        child));
                }
                catch (Exception)
                {
                    // Gone between the query and the read; the others still count.
                }
            }
            return kids.ToArray();
        }
        catch (Exception ex)
        {
            // An element that went away, an application at a higher integrity level, or one that does
            // not answer: framed as far as it got, the window itself at least.
            Log.Debug(ex, "Could not read child elements");
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        // The worker is not waited for: it may be inside a query to an application that does not
        // answer, and it is a background thread that ends with its loop.
    }

    private sealed class Node(Rectangle bounds, AutomationElement? element)
    {
        public Rectangle Bounds { get; } = bounds;
        public AutomationElement? Element { get; } = element;

        /// <summary>Null until read; replaced whole, so a reader on another thread sees none or all of them.</summary>
        public volatile Node[]? Children;
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The windows a pointer can land on, front to back: visible, not minimised, not cloaked (on
    /// another virtual desktop, or a suspended app), not click-through or fully transparent, and not
    /// the desktop itself — hovering the wallpaper frames the monitor instead.
    /// </summary>
    private static List<(IntPtr, Rectangle)> ListWindows(Rectangle desktop)
    {
        var found = new List<(IntPtr, Rectangle)>();
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (Describe(hwnd, desktop) is { } bounds) found.Add((hwnd, bounds));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not finish listing windows for the capture");
        }
        return found;
    }

    private static Rectangle? Describe(IntPtr hwnd, Rectangle desktop)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return null;

        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TRANSPARENT) != 0) return null;
        if ((ex & WS_EX_LAYERED) != 0 &&
            GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) &&
            (flags & LWA_ALPHA) != 0 && alpha == 0)
            return null;

        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return null;

        // The frame as drawn: GetWindowRect includes the invisible resize borders Windows 10 and 11
        // put around a window, which would frame a few pixels of whatever is behind it.
        RECT r;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0 &&
            !GetWindowRect(hwnd, out r))
            return null;

        var bounds = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), desktop);
        if (bounds.Width < 4 || bounds.Height < 4) return null;
        return bounds;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
