using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Keys of a capture in progress: Ctrl+C copies the screenshot while the toolbar is up (the same as
/// its 截圖 button, 截圖後自動關閉 included); F5 takes a fresh screenshot and Ctrl+A selects the
/// whole screen for as long as the capture is up.
/// </summary>
/// <remarks>
/// A process-wide low-level hook for the reason <see cref="AnnotationShortcutHook"/> is one: none of
/// the capture's windows take the keyboard focus, so no ordinary key handler would see the key. It
/// lives only as long as what it serves, and its keys are swallowed so the application behind the
/// capture does not act on them as well. Typing into one of our boxes keeps the keys the box's.
/// </remarks>
internal sealed class CaptureKeyHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    public const int VK_C = 0x43;
    public const int VK_A = 0x41;
    public const int VK_F5 = 0x74;

    /// <summary>One key this hook takes: the virtual key, whether Ctrl is part of it, what it does.</summary>
    public readonly record struct Binding(int VirtualKey, bool Ctrl, Action Action);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Binding[] _bindings;
    private IntPtr _hookId;

    // Its own thread, as GlobalEscapeHook has: a low-level hook runs on the thread that installed
    // it, so on the interface thread every key pressed anywhere on the desktop waited for whatever
    // the capture's interface was busy with — a stutter in other programs on a slow machine.
    private readonly HookThread _thread = new("OverTranslate capture key hook");

    private CaptureKeyHook(Binding[] bindings)
    {
        _bindings = bindings;
        _proc = HookCallback;
    }

    public static CaptureKeyHook Install(params Binding[] bindings)
    {
        var hook = new CaptureKeyHook(bindings);
        hook._thread.Start();
        hook._thread.Invoke(() =>
        {
            hook._hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hook._proc, GetModuleHandle(null), 0);
            if (hook._hookId == IntPtr.Zero)
                Log.Warn("Capture key hook install failed (win32 error {Error}) — its keys stay on the buttons only",
                    Marshal.GetLastWin32Error());
        });
        return hook;
    }

    // The asynchronous state: the hook thread receives no keyboard input of its own, so its
    // per-thread key state would never see the modifiers go down.
    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || wParam != (IntPtr)WM_KEYDOWN)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int vk = Marshal.ReadInt32(lParam);
        foreach (var binding in _bindings)
        {
            if (binding.VirtualKey != vk) continue;
            if (IsDown(VK_CONTROL) != binding.Ctrl || IsDown(VK_SHIFT) || IsDown(VK_MENU) ||
                IsDown(VK_LWIN) || IsDown(VK_RWIN) || TypingWindows.HaveForeground)
                break;

            // Post, never wait: a hook that outruns LowLevelHooksTimeout is dropped by Windows.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(binding.Action);
            return (IntPtr)1;
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        // On the hook thread: a hook is removed from the thread that installed it.
        _thread.Stop(() =>
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        });
    }
}
