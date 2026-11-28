using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OverTranslate.Services;

/// <summary>
/// The extended window styles this application's floating layers need, in one place.
/// </summary>
/// <remarks>
/// Every overlay here wants some combination of the same three bits, and each one used to declare
/// its own copy of the two imports and the four constants. The cost of that was not the duplicated
/// lines: it was that "stop the realtime layers taking focus" looked like a one-file change in four
/// different files, so fixing it in the one that was reported left the other three as they were.
///
/// Call from <c>OnSourceInitialized</c> or later — before that the window has no handle and there is
/// nothing to set the style on.
/// </remarks>
internal static class WindowStyles
{
    private const int GWL_EXSTYLE = -20;

    /// <summary>Clicks pass through to whatever is underneath.</summary>
    private const int WS_EX_TRANSPARENT = 0x20;

    /// <summary>Required for WS_EX_TRANSPARENT to behave; also what AllowsTransparency renders into.</summary>
    private const int WS_EX_LAYERED = 0x80000;

    /// <summary>The window never takes activation, so showing it cannot pull focus off a game.</summary>
    private const int WS_EX_NOACTIVATE = 0x8000000;

    /// <summary>Kept out of Alt+Tab and the task view.</summary>
    private const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    /// <summary>
    /// Makes the window unable to take focus while staying clickable. For chrome the user operates —
    /// a floating bar, an edit layer — which must not steal activation from the application it is
    /// sitting on top of.
    /// </summary>
    public static void ApplyNoActivate(Window window) => Add(window, WS_EX_NOACTIVATE);

    /// <summary>
    /// Keeps the window out of Alt+Tab and the task view. WPF's ShowInTaskbar=false only hides the
    /// taskbar button (through a hidden owner); an activatable window still shows up in Alt+Tab.
    /// </summary>
    public static void ApplyToolWindow(Window window) => Add(window, WS_EX_TOOLWINDOW);

    /// <summary>
    /// Turns <see cref="ApplyNoActivate"/> off or back on, for a window that takes the keyboard only
    /// for as long as something on it is being typed into: Windows will not hand the foreground to a
    /// window carrying the bit, however it is asked.
    /// </summary>
    public static void SetNoActivate(Window window, bool enabled)
    {
        if (enabled) Add(window, WS_EX_NOACTIVATE);
        else Remove(window, WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// Makes the window invisible to the mouse entirely: clicks land on the application underneath.
    /// For layers that only ever display something.
    /// </summary>
    /// <param name="noActivate">
    /// Also refuse activation. A click-through window is rarely activated by the mouse anyway, but it
    /// can still be handed focus when it is shown — which over a full-screen game is exactly the
    /// thing not to do.
    /// </param>
    public static void ApplyClickThrough(Window window, bool noActivate = false)
    {
        var style = WS_EX_TRANSPARENT | WS_EX_LAYERED;
        if (noActivate) style |= WS_EX_NOACTIVATE;
        Add(window, style);
    }

    /// <summary>
    /// Turns click-through on or off on a window that is already on screen, for a layer that has to
    /// hand the mouse back to the application underneath and later take it again.
    /// </summary>
    /// <remarks>
    /// WS_EX_LAYERED is only ever added, never taken away: it is also what AllowsTransparency renders
    /// through, so clearing it along with the transparency bit would leave the layer unable to draw
    /// itself at all.
    /// </remarks>
    public static void SetClickThrough(Window window, bool enabled)
    {
        if (enabled) Add(window, WS_EX_TRANSPARENT | WS_EX_LAYERED);
        else Remove(window, WS_EX_TRANSPARENT);
    }

    /// <summary>
    /// For a small hint shown over another program while the user types there: no activation, and
    /// if Windows activates it anyway, the foreground goes straight back to the window it was taken
    /// from.
    /// </summary>
    /// <remarks>
    /// WS_EX_NOACTIVATE alone was not enough: the focus trace (2026-11-23) showed WeChat (not Edge)
    /// losing the foreground to the 替換翻譯 label each time it appeared — usually taken back at once
    /// (the window's backdrop flickering), once not at all, leaving the user's typing going nowhere —
    /// and, when the label closed, the foreground going to its hidden owner. Dropping Topmost did not
    /// stop it (and let WeChat cover the label), so the window keeps Topmost="True" in XAML and this
    /// hands the foreground back. Call from <c>OnSourceInitialized</c>.
    /// </remarks>
    /// <param name="returnTo">
    /// The window to hand the foreground back to, when the caller knows it (替換翻譯 does: it read it
    /// after the copy). Without it, the window named in WM_ACTIVATE is used — which Windows leaves
    /// empty when the activation comes from another program, as the trace of 2026-11-27 showed in
    /// WeChat: the label kept the foreground and the paste was refused.
    /// </param>
    public static void ApplyHintWindow(Window window, Func<IntPtr>? returnTo = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        Add(window, WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    handled = true;
                    return new IntPtr(MA_NOACTIVATE);

                case WM_ACTIVATE when (wParam.ToInt64() & 0xFFFF) != WA_INACTIVE:
                {
                    // lParam is the window being deactivated: the one the user was typing in — when
                    // Windows says. The caller's own record comes first.
                    var known = returnTo?.Invoke() ?? IntPtr.Zero;
                    var target = known != IntPtr.Zero ? known : lParam;
#if LOCAL_DIAGNOSTICS
                    LocalDiagnostics.ForegroundTrail.Note(
                        $"提示標籤被啟用，WM_ACTIVATE 帶來的視窗：{(lParam == IntPtr.Zero ? "空" : $"0x{lParam:X}")}，" +
                        $"呼叫方記下的：{(known == IntPtr.Zero ? "無" : $"0x{known:X}")}");
#endif
                    if (target == IntPtr.Zero || !IsWindow(target)) break;

                    // Not from inside WM_ACTIVATE: the activation is still under way there, and a
                    // SetForegroundWindow made in the middle of it can be overtaken by it. Once it has
                    // finished, and only if the label still holds the foreground.
                    window.Dispatcher.BeginInvoke(() =>
                    {
                        if (GetForegroundWindow() != hwnd || !IsWindow(target)) return;
                        var ok = SetForegroundWindow(target);
#if LOCAL_DIAGNOSTICS
                        LocalDiagnostics.ForegroundTrail.Note(
                            $"提示標籤交還前台給 0x{target:X}：{(ok ? "成功" : "失敗")}，之後前台 0x{GetForegroundWindow():X}");
#endif
                    }, System.Windows.Threading.DispatcherPriority.Send);
                    break;
                }
            }
            return IntPtr.Zero;
        });
    }

    private const int WM_ACTIVATE = 0x0006;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int WA_INACTIVE = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Adds bits to the existing extended style rather than replacing it: WPF has already put its own
    /// there — WS_EX_LAYERED from AllowsTransparency, for one — and
    /// overwriting them would undo settings made in XAML.
    /// </summary>
    private static void Add(Window window, int exStyle)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | exStyle);
    }

    /// <summary>Clears bits from the extended style, leaving everything else where it was.</summary>
    private static void Remove(Window window, int exStyle)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~exStyle);
    }
}
