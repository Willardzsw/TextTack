using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// 鼠標增強: modifier + wheel fades the window under the pointer, modifier + wheel scrolls it
/// sideways, and modifier + drag resizes a window from its nearest edge or corner.
/// </summary>
/// <remarks>
/// <para>Built the way AltSnap (GPL-3.0, github.com/RamonUnch/AltSnap) does these three things,
/// reimplemented here rather than copied, since this program is MIT-licensed:</para>
/// <list type="bullet">
/// <item>A low-level keyboard hook tracks which modifiers are physically held. The mouse hook is
/// installed only while a configured combination is held (or a resize is under way) and removed
/// after, so it is a fresh hook every time — Windows silently drops a low-level hook whose callback
/// once runs past its timeout, which a managed callback can do during a garbage collection, and a
/// hook installed once at startup would then stay dead for the session.</item>
/// <item>A watchdog notices a keyboard hook that has stopped hearing keys (a modifier is down but
/// the hook saw no press) and installs it again.</item>
/// <item>Transparency sets WS_EX_LAYERED plus an alpha, starting from the window's current alpha,
/// and drops the layering again at 100%.</item>
/// <item>Horizontal scroll sends WM_MOUSEHWHEEL straight to the window under the pointer.</item>
/// <item>Resize tells the window a size/move loop has begun and ended (WM_ENTERSIZEMOVE /
/// WM_EXITSIZEMOVE), moves it with an asynchronous SetWindowPos and waits for its repaint.</item>
/// </list>
/// <para>Both hooks live on one <see cref="HookThread"/>, so their callbacks never race; anything
/// that touches another program's window runs on a separate worker thread, because that can block
/// on the program and a hook callback must not.</para>
/// </remarks>
internal sealed class MouseEnhanceService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static MouseEnhanceService Instance { get; } = new();

    private readonly HookThread _hookThread = new("OverTranslate mouse-enhance hooks");
    private readonly HookThread _worker = new("OverTranslate mouse-enhance worker");
    private readonly LowLevelProc _mouseProc;
    private readonly LowLevelProc _keyboardProc;
    private readonly Func<MouseModifierKeys>? _modifiersOverride;

    // Hook thread only.
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private DispatcherTimer? _watchdog;
    private readonly HashSet<int> _keysDown = [];

    // Modifiers held by a program sending keys (our own Ctrl+C for 取詞 and 替換翻譯, a macro tool):
    // not a combination of the user's, but the keyboard's state shows them held, and the watchdog
    // must not take that for a hook that has stopped hearing.
    private readonly HashSet<int> _injectedDown = [];
    private MouseModifierKeys _held;
    private int _wheelRemainder;
    private int _horizontalRemainder;
    private ResizeDrag? _drag;

    private volatile MouseEnhanceSettings? _settings;

    /// <summary>The rectangle the worker should apply next, coalescing pointer moves.</summary>
    private (IntPtr Hwnd, int L, int T, int R, int B)? _pendingRect;
    private readonly object _pendingLock = new();
    private bool _applyQueued;

    /// <summary>Windows this service made layered, so it can undo that. Worker thread only.</summary>
    private readonly HashSet<IntPtr> _madeLayered = [];

    private MouseEnhanceService()
    {
        _mouseProc = MouseCallback;
        _keyboardProc = KeyboardCallback;
    }

    /// <summary>For tests: an instance whose held keys are whatever the test says.</summary>
    internal MouseEnhanceService(Func<MouseModifierKeys> modifiers) : this()
    {
        _modifiersOverride = modifiers;
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    /// <summary>Takes up <paramref name="settings"/>, installing or removing the hooks as needed.</summary>
    public void Apply(MouseEnhanceSettings? settings)
    {
        var snapshot = Copy(settings ?? new MouseEnhanceSettings());
        bool active = MouseEnhanceRules.IsActive(snapshot);
        _settings = active ? snapshot : null;

        if (active)
        {
            StartWorker();
            _hookThread.Start();
            _hookThread.Invoke(() =>
            {
                PerMonitorDpi();
                InstallKeyboardHook();
                _watchdog ??= NewWatchdog();
                _watchdog.Start();
                SyncMouseHook();
            });
        }
        else
        {
            _hookThread.Invoke(RemoveAllHooks);
        }
    }

    /// <summary>
    /// Hook coordinates are physical pixels. Both threads say so explicitly, so WindowFromPoint,
    /// GetWindowRect and SetWindowPos read them as such whatever the process default happens to be.
    /// </summary>
    private static void PerMonitorDpi() => SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    private void StartWorker()
    {
        _worker.Start();
        _worker.Invoke(PerMonitorDpi);
    }

    private DispatcherTimer NewWatchdog()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Watchdog();
        return timer;
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
            Log.Warn("Mouse-enhance keyboard hook install failed (win32 error {Error})", Marshal.GetLastWin32Error());
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
    }

    /// <summary>
    /// Has the mouse hook installed exactly while it can be needed: a configured combination is
    /// held, or a resize is still being dragged.
    /// </summary>
    private void SyncMouseHook()
    {
        var s = _settings;
        bool wanted = s is not null && (_drag is not null || MouseEnhanceRules.IsConfigured(s, _held));
        if (wanted && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
                Log.Warn("Mouse-enhance mouse hook install failed (win32 error {Error})", Marshal.GetLastWin32Error());
        }
        else if (!wanted && _mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _wheelRemainder = 0;
        }
    }

    private void RemoveAllHooks()
    {
        _watchdog?.Stop();
        _drag = null;
        _keysDown.Clear();
        _injectedDown.Clear();
        _held = MouseModifierKeys.None;
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        RemoveKeyboardHook();
    }

    /// <summary>
    /// Once a second: compares the keys the hook believes are held with the keyboard's own state,
    /// and repairs a hook that has gone deaf, or a release it never heard.
    /// </summary>
    private void Watchdog()
    {
        if (_settings is null) return;
        var actual = AsyncModifiers();

        if (actual == MouseModifierKeys.None) _injectedDown.Clear();

        if (actual != MouseModifierKeys.None && _held == MouseModifierKeys.None && _keysDown.Count == 0 && _injectedDown.Count == 0)
        {
            // A modifier is down and the hook heard no press: it has been dropped by Windows (or the
            // press went to a program run as administrator, which no hook here can hear).
            Log.Info("Mouse-enhance keyboard hook stopped receiving keys; reinstalling");
            RemoveKeyboardHook();
            InstallKeyboardHook();
            _held = actual;
            SyncMouseHook();
        }
        else if (actual == MouseModifierKeys.None && _held != MouseModifierKeys.None)
        {
            // A release went to a window this hook cannot hear (one run as administrator).
            _keysDown.Clear();
            _held = MouseModifierKeys.None;
            SyncMouseHook();
        }
        else if (_keyboardHook == IntPtr.Zero)
        {
            InstallKeyboardHook();
        }
    }

    // ── Keyboard hook ────────────────────────────────────────────────────────

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int vk = Marshal.ReadInt32(lParam);
                uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                if ((flags & LLKHF_INJECTED) != 0 && ModifierFor(vk) != MouseModifierKeys.None)
                {
                    int sent = (int)wParam;
                    if (sent is WM_KEYDOWN or WM_SYSKEYDOWN) _injectedDown.Add(vk);
                    else _injectedDown.Remove(vk);
                }
                else if ((flags & LLKHF_INJECTED) == 0 && ModifierFor(vk) != MouseModifierKeys.None)
                {
                    int message = (int)wParam;
                    bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                    bool changed = down ? _keysDown.Add(vk) : _keysDown.Remove(vk);
                    if (changed)
                    {
                        var held = MouseModifierKeys.None;
                        foreach (var key in _keysDown) held |= ModifierFor(key);
                        _held = held;
                        SyncMouseHook();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Mouse-enhance keyboard callback failed");
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static MouseModifierKeys ModifierFor(int vk) => vk switch
    {
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => MouseModifierKeys.Ctrl,
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT => MouseModifierKeys.Shift,
        VK_MENU or VK_LMENU or VK_RMENU => MouseModifierKeys.Alt,
        VK_LWIN or VK_RWIN => MouseModifierKeys.Win,
        _ => MouseModifierKeys.None,
    };

    private static MouseModifierKeys AsyncModifiers()
    {
        var keys = MouseModifierKeys.None;
        if (Down(VK_CONTROL)) keys |= MouseModifierKeys.Ctrl;
        if (Down(VK_SHIFT)) keys |= MouseModifierKeys.Shift;
        if (Down(VK_MENU)) keys |= MouseModifierKeys.Alt;
        if (Down(VK_LWIN) || Down(VK_RWIN)) keys |= MouseModifierKeys.Win;
        return keys;

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    // ── Mouse hook ───────────────────────────────────────────────────────────

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _settings is { } s)
            {
                if (Handle(s, (int)wParam, Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)))
                    return 1;
            }
        }
        catch (Exception ex)
        {
            // Never let one bad event take the pointer down with it.
            Log.Warn(ex, "Mouse-enhance mouse callback failed");
            _drag = null;
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private MouseModifierKeys CurrentModifiers() => _modifiersOverride?.Invoke() ?? _held;

    /// <summary>True when the event is taken and must not reach anything else.</summary>
    private bool Handle(MouseEnhanceSettings s, int message, MSLLHOOKSTRUCT data)
    {
        if (_drag is { } drag)
        {
            if (message == WM_MOUSEMOVE)
            {
                var rect = MouseEnhanceRules.Resize(drag.Start, drag.Edges, data.x - drag.StartX, data.y - drag.StartY,
                    drag.MinWidth, drag.MinHeight);
                QueueRect(drag.Hwnd, rect);
                return false;
            }

            if (message == drag.UpMessage)
            {
                _drag = null;
                var hwnd = drag.Hwnd;
                _worker.Dispatcher?.BeginInvoke(() => EndResize(hwnd));
                // After this callback returns: the hook is not removed from inside its own call.
                _hookThread.Dispatcher?.BeginInvoke(SyncMouseHook);
                return true;
            }

            // A second press of the same button while dragging cannot start anything new.
            return message == drag.DownMessage;
        }

        if ((data.flags & LLMHF_INJECTED) != 0) return false;

        return message switch
        {
            WM_MOUSEWHEEL => HandleWheel(s, data),
            WM_LBUTTONDOWN => TryStartResize(s, data, MouseResizeButton.Left, WM_LBUTTONDOWN, WM_LBUTTONUP),
            WM_RBUTTONDOWN => TryStartResize(s, data, MouseResizeButton.Right, WM_RBUTTONDOWN, WM_RBUTTONUP),
            WM_MBUTTONDOWN => TryStartResize(s, data, MouseResizeButton.Middle, WM_MBUTTONDOWN, WM_MBUTTONUP),
            _ => false,
        };
    }

    private bool HandleWheel(MouseEnhanceSettings s, MSLLHOOKSTRUCT data)
    {
        var held = CurrentModifiers();
        var action = MouseEnhanceRules.ForWheel(s, held);
        int delta = (short)(data.mouseData >> 16);

        if (action == MouseWheelAction.Transparency)
        {
            var hwnd = TargetWindow(data.x, data.y);
            if (hwnd == IntPtr.Zero)
            {
                _wheelRemainder = 0;
                return false;
            }

            int notches = MouseEnhanceRules.Accumulate(ref _wheelRemainder, delta);
            int step = s.TransparencyStep, min = s.MinimumOpacity;
            if (notches != 0)
                _worker.Dispatcher?.BeginInvoke(() => ChangeOpacity(hwnd, notches, step, min));
            MaskMenuKey(held);
            return true;
        }

        _wheelRemainder = 0;

        if (action == MouseWheelAction.HorizontalScroll)
        {
            int x = data.x, y = data.y;
            // Wheel down (negative) scrolls right (positive) unless reversed.
            int horizontal = MouseEnhanceRules.ScaleHorizontal(
                ref _horizontalRemainder, s.HorizontalScrollReversed ? delta : -delta);
            if (horizontal != 0)
                _worker.Dispatcher?.BeginInvoke(() => ScrollSideways(x, y, horizontal));
            MaskMenuKey(held);
            return true;
        }

        return false;
    }

    private bool TryStartResize(MouseEnhanceSettings s, MSLLHOOKSTRUCT data, MouseResizeButton button, int down, int up)
    {
        var held = CurrentModifiers();
        if (!MouseEnhanceRules.StartsResize(s, held, button)) return false;

        var hwnd = TargetWindow(data.x, data.y);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(hwnd)) return false;

        // Like AltSnap, any ordinary window, including frameless ones that draw their own border —
        // only small tool popups without a sizing border are left alone.
        int style = GetWindowLong(hwnd, GWL_STYLE);
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((style & WS_THICKFRAME) == 0 && (exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        RECT r;
        bool maximized = IsZoomed(hwnd);
        if (maximized)
        {
            // Let down to fill the work area, so the drag starts from where the window visibly is.
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            r = info.rcWork;
        }
        else if (!GetWindowRect(hwnd, out r))
        {
            return false;
        }

        var edges = MouseEnhanceRules.EdgesFor(r.Left, r.Top, r.Right, r.Bottom, data.x, data.y);
        var start = r;
        _worker.Dispatcher?.BeginInvoke(() => BeginResize(hwnd, maximized, start, edges));

        _drag = new ResizeDrag(
            hwnd,
            (r.Left, r.Top, r.Right, r.Bottom),
            edges,
            data.x, data.y,
            Math.Max(GetSystemMetrics(SM_CXMINTRACK), 80),
            Math.Max(GetSystemMetrics(SM_CYMINTRACK), 40),
            down, up);
        MaskMenuKey(held);
        return true;
    }

    /// <summary>
    /// The top-level window under the point, or zero for the desktop, the taskbar and the like —
    /// nothing anyone means to fade or resize.
    /// </summary>
    private static IntPtr TargetWindow(int x, int y)
    {
        var hit = WindowFromPoint(new POINT { X = x, Y = y });
        if (hit == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero || root == GetShellWindow() || root == GetDesktopWindow()) return IntPtr.Zero;
        return BlockedClasses.Contains(ClassOf(root)) ? IntPtr.Zero : root;
    }

    private static readonly HashSet<string> BlockedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(128);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    /// <summary>
    /// A held Alt or Win released with nothing typed in between opens the menu bar or Start. The
    /// taken wheel or click never reached anything, so an unassigned key is pressed to say the
    /// modifier was used.
    /// </summary>
    private void MaskMenuKey(MouseModifierKeys held)
    {
        if ((held & (MouseModifierKeys.Alt | MouseModifierKeys.Win)) == 0) return;
        _worker.Dispatcher?.BeginInvoke(() =>
            KeyboardInput.Inject([KeyboardInput.Key(VK_MASK, up: false), KeyboardInput.Key(VK_MASK, up: true)]));
    }

    // ── Worker-thread actions ────────────────────────────────────────────────

    private void ChangeOpacity(IntPtr hwnd, int notches, int step, int minimum)
    {
        if (!IsWindow(hwnd)) { _madeLayered.Remove(hwnd); return; }

        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        bool layered = (ex & WS_EX_LAYERED) != 0;
        uint key = 0, flags = 0;
        byte alpha = 255;
        if (layered && GetLayeredWindowAttributes(hwnd, out key, out alpha, out flags))
        {
            if ((flags & LWA_ALPHA) == 0) alpha = 255;
        }
        else if (layered)
        {
            // Layered through UpdateLayeredWindow: the window draws its own translucency (as a
            // 貼圖 does) and has no single alpha to change; setting one would break it.
            return;
        }

        byte next = MouseEnhanceRules.NextAlpha(alpha, notches, step, minimum);
        bool ok = true;
        int error = 0;

        if (next == 255)
        {
            // Back to solid: drop the layering, which costs the window speed.
            if (layered && (_madeLayered.Remove(hwnd) || flags == LWA_ALPHA))
            {
                SetLastError(0);
                if (SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED) == 0 && Marshal.GetLastWin32Error() != 0)
                {
                    ok = false;
                    error = Marshal.GetLastWin32Error();
                }
            }
        }
        else
        {
            if (!layered)
            {
                SetLastError(0);
                if (SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED) == 0 && Marshal.GetLastWin32Error() != 0)
                {
                    error = Marshal.GetLastWin32Error();
                    return;
                }
                _madeLayered.Add(hwnd);
            }

            if (!SetLayeredWindowAttributes(hwnd, key, next, flags | LWA_ALPHA))
            {
                ok = false;
                error = Marshal.GetLastWin32Error();
            }
        }

        if (!ok) Log.Debug("Transparency failed on {Hwnd} (win32 error {Error})", hwnd, error);
    }

    /// <summary>WM_MOUSEHWHEEL to the window under the pointer, as AltSnap does.</summary>
    private void ScrollSideways(int x, int y, int delta)
    {
        var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // The mouse buttons and keys held, which the hook does not pass on with the wheel.
        int keys = 0;
        if ((GetAsyncKeyState(0x01) & 0x8000) != 0) keys |= 0x0001; // MK_LBUTTON
        if ((GetAsyncKeyState(0x02) & 0x8000) != 0) keys |= 0x0002; // MK_RBUTTON
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) keys |= 0x0004; // MK_SHIFT
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) keys |= 0x0008; // MK_CONTROL
        if ((GetAsyncKeyState(0x04) & 0x8000) != 0) keys |= 0x0010; // MK_MBUTTON

        var wParam = (IntPtr)((delta << 16) | (keys & 0xFFFF));
        var lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
        var sent = SendMessageTimeout(hwnd, WM_MOUSEHWHEEL, wParam, lParam, SMTO_ABORTIFHUNG, 300, out _);
        int error = sent == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
    }

    private void BeginResize(IntPtr hwnd, bool maximized, RECT start, ResizeEdges edges)
    {
        if (!IsWindow(hwnd)) return;
        if (maximized)
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetWindowPos(hwnd, IntPtr.Zero, start.Left, start.Top, start.Right - start.Left, start.Bottom - start.Top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        SendMessageTimeout(hwnd, WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out _);
    }

    private void EndResize(IntPtr hwnd)
    {
        ApplyPendingRect();
        if (!IsWindow(hwnd)) return;
        SendMessageTimeout(hwnd, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out _);
        GetWindowRect(hwnd, out var r);
    }

    private void QueueRect(IntPtr hwnd, (int L, int T, int R, int B) rect)
    {
        lock (_pendingLock)
        {
            _pendingRect = (hwnd, rect.L, rect.T, rect.R, rect.B);
            if (_applyQueued) return;
            _applyQueued = true;
        }

        _worker.Dispatcher?.BeginInvoke(ApplyPendingRect);
    }

    /// <summary>
    /// Asynchronous SetWindowPos, then waiting (bounded) for the window to repaint: a fast drag is
    /// throttled to what the window can draw, the moves that arrive meanwhile collapse into the
    /// latest, and a hung window cannot hold the worker for long.
    /// </summary>
    private void ApplyPendingRect()
    {
        (IntPtr Hwnd, int L, int T, int R, int B)? next;
        lock (_pendingLock)
        {
            next = _pendingRect;
            _pendingRect = null;
            _applyQueued = false;
        }

        if (next is not { } r || !IsWindow(r.Hwnd)) return;
        if (!SetWindowPos(r.Hwnd, IntPtr.Zero, r.L, r.T, r.R - r.L, r.B - r.T,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS))
        {
            int error = Marshal.GetLastWin32Error();
            return;
        }

        SendMessageTimeout(r.Hwnd, WM_SYNCPAINT, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 120, out _);
    }

    /// <summary>Puts every window this service faded back to solid.</summary>
    private void RestoreFaded()
    {
        foreach (var hwnd in _madeLayered)
        {
            if (!IsWindow(hwnd)) continue;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
        }

        _madeLayered.Clear();
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Feeds one pointer event through the same decisions the hook makes, for tests: real input
    /// cannot be injected without being marked as injected, which the hook passes on.
    /// </summary>
    internal bool Simulate(MouseEnhanceSettings settings, int message, int x, int y, int wheelDelta = 0)
    {
        StartWorker();
        var data = new MSLLHOOKSTRUCT { x = x, y = y, mouseData = unchecked((uint)(wheelDelta << 16)) };
        return Handle(Copy(settings), message, data);
    }

    /// <summary>Waits for the worker to finish what it has been given. For tests.</summary>
    internal void DrainWorker() => _worker.Invoke(() => { });

    internal const int MessageMove = WM_MOUSEMOVE, MessageRightDown = WM_RBUTTONDOWN, MessageRightUp = WM_RBUTTONUP,
        MessageWheel = WM_MOUSEWHEEL;

    private static MouseEnhanceSettings Copy(MouseEnhanceSettings s) => new()
    {
        Enabled = s.Enabled,
        TransparencyEnabled = s.TransparencyEnabled,
        TransparencyModifier = s.TransparencyModifier,
        TransparencyStep = s.TransparencyStep,
        MinimumOpacity = s.MinimumOpacity,
        HorizontalScrollEnabled = s.HorizontalScrollEnabled,
        HorizontalScrollModifier = s.HorizontalScrollModifier,
        HorizontalScrollReversed = s.HorizontalScrollReversed,
        ResizeEnabled = s.ResizeEnabled,
        ResizeModifier = s.ResizeModifier,
        ResizeButton = s.ResizeButton,
    };

    public void Dispose()
    {
        _settings = null;
        _hookThread.Stop(RemoveAllHooks);
        _worker.Stop(RestoreFaded);
    }

    private sealed record ResizeDrag(
        IntPtr Hwnd,
        (int Left, int Top, int Right, int Bottom) Start,
        ResizeEdges Edges,
        int StartX,
        int StartY,
        int MinWidth,
        int MinHeight,
        int DownMessage,
        int UpMessage);

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    private const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, WM_SYNCPAINT = 0x0088;
    private const uint LLMHF_INJECTED = 0x01, LLKHF_INJECTED = 0x10;

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    /// <summary>Unassigned by Windows; the usual key for marking Alt or Win as used.</summary>
    private const ushort VK_MASK = 0xE8;

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint LWA_ALPHA = 0x2;
    private const uint GA_ROOT = 2;
    private const int SM_CXMINTRACK = 34, SM_CYMINTRACK = 35;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_RESTORE = 9;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(int error);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
