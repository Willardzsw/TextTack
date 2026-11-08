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
    /// The elements under <paramref name="p"/>, outermost first, going down from <paramref name="root"/>
    /// (not included).
    /// </summary>
    /// <remarks>
    /// <para>Where several children contain the point, each is followed and the one leading deepest
    /// wins; on a tie, the smaller. Following only the smallest went wrong in Chromium (Chrome, Edge,
    /// Electron apps): beside its content every window has an empty layer exactly as large, and going
    /// into that one ended the search at the whole window.</para>
    ///
    /// <para><paramref name="childrenOf"/> returns null for children not read yet; the search then
    /// goes on with what it has and sets <paramref name="incomplete"/>. At most
    /// <paramref name="maxNodes"/> elements are looked at.</para>
    /// </remarks>
    public static List<T> DeepestPath<T>(
        T root, Point p, Func<T, IReadOnlyList<T>?> childrenOf, Func<T, Rectangle> boundsOf,
        out bool incomplete, int maxNodes = 600, int maxDepth = 48)
    {
        bool missing = false;
        int budget = maxNodes;

        List<T> Below(T node, int depth)
        {
            if (depth >= maxDepth || budget-- <= 0) return [];
            var kids = childrenOf(node);
            if (kids is null)
            {
                missing = true;
                return [];
            }

            var best = new List<T>();
            foreach (var kid in kids
                .Where(k => boundsOf(k) is { Width: > 0, Height: > 0 } r && r.Contains(p))
                .OrderBy(k => (long)boundsOf(k).Width * boundsOf(k).Height))
            {
                var path = Below(kid, depth + 1);
                if (path.Count + 1 > best.Count)
                {
                    path.Insert(0, kid);
                    best = path;
                }
            }
            return best;
        }

        var result = Below(root, 0);
        incomplete = missing;
        return result;
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
        if (!_roots.TryGetValue(hwnd, out var root)) return false;
        var nodes = CaptureHoverRules.DeepestPath<Node>(
            root, p, n => n.Children is { } kids && !n.RereadDue ? kids : null, n => n.Bounds,
            out bool incomplete, maxDepth: MaxDepth);
        path.AddRange(nodes.Select(n => n.Bounds));
        return !incomplete;
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
        (IntPtr Hwnd, Point Point)? job = null;
        while (!_disposed)
        {
            // With a job kept for another look (see below), the wait is only until that look is due
            // or the pointer goes somewhere else.
            if (job is null) _wake.WaitOne();
            lock (_pendingLock)
            {
                if (_pending is not null) job = _pending;
                _pending = null;
            }
            if (job is not { } j || _disposed) continue;

            bool mayGrow = false;
            try
            {
                bool changed = Load(j.Hwnd, j.Point, out mayGrow);
                if (changed && !_disposed)
                    _dispatcher.BeginInvoke(() => { if (!_disposed) Updated?.Invoke(this, EventArgs.Empty); });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Reading elements for the capture failed");
            }

            if (!mayGrow)
            {
                job = null;
                continue;
            }
            // Chromium builds its tree only once asked, so what came back empty is asked again
            // shortly — unless something new is asked first, which the loop then takes instead.
            _wake.WaitOne(RereadAfterMs);
        }
    }

    /// <summary>
    /// Reads, on this thread, whatever is missing along the path to <paramref name="p"/>, and reads
    /// again the empty elements due another look. <paramref name="mayGrow"/>: some element on the way
    /// is still empty and will be due again.
    /// </summary>
    private bool Load(IntPtr hwnd, Point p, out bool mayGrow)
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

        bool grow = false;
        CaptureHoverRules.DeepestPath<Node>(node, p, n =>
        {
            if (_disposed) return [];
            var kids = n.Children;
            if (kids is null || n.RereadDue)
            {
                bool again = kids is not null;
                kids = ReadChildren(n.Element);
                n.ReadAt = Environment.TickCount64;
                if (again) n.Rereads++;
                if (!again || kids.Length > 0) changed = true;
                n.Children = kids;
            }
            if (n.MayFillIn) grow = true;
            return kids;
        }, n => n.Bounds, out _, maxDepth: MaxDepth);
        mayGrow = grow && !_disposed;
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

    /// <summary>How long after an element came back empty it is asked again.</summary>
    private const int RereadAfterMs = 350;

    /// <summary>How many times an empty element is asked again before it is taken to be empty.</summary>
    private const int MaxRereads = 5;

    private sealed class Node(Rectangle bounds, AutomationElement? element)
    {
        public Rectangle Bounds { get; } = bounds;
        public AutomationElement? Element { get; } = element;

        /// <summary>Null until read; replaced whole, so a reader on another thread sees none or all of them.</summary>
        public volatile Node[]? Children;

        /// <summary>When <see cref="Children"/> was last read (Environment.TickCount64). Written by the worker only.</summary>
        public long ReadAt;

        /// <summary>How many times it has been read again after coming back empty. Written by the worker only.</summary>
        public int Rereads;

        /// <summary>
        /// Read and empty, but possibly only because the application had not built that part yet —
        /// Chromium answers its first question with an empty shell and fills it in afterwards.
        /// </summary>
        public bool MayFillIn => Element is not null && Children is { Length: 0 } && Rereads < MaxRereads;

        /// <summary>Empty, worth another look, and that look is due.</summary>
        public bool RereadDue => MayFillIn && Environment.TickCount64 - ReadAt >= RereadAfterMs;
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
