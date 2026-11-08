using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Ctrl+C while a capture's toolbar is up: copies the screenshot, the same as the toolbar's 截圖
/// button, including whether 截圖後自動關閉 then ends the capture.
/// </summary>
/// <remarks>
/// A process-wide low-level hook for the reason <see cref="AnnotationShortcutHook"/> is one: none of
/// the capture's windows take the keyboard focus, so no ordinary key handler would see the key. It
/// lives only as long as the toolbar, and the key is swallowed so the application behind the capture
/// does not copy something of its own as well. Typing into one of our boxes keeps Ctrl+C the box's.
/// </remarks>
internal sealed class CaptureCopyHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_C = 0x43;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action _onCopy;
    private IntPtr _hookId;

    private CaptureCopyHook(Action onCopy)
    {
        _onCopy = onCopy;
        _proc = HookCallback;
    }

    public static CaptureCopyHook Install(Action onCopy)
    {
        var hook = new CaptureCopyHook(onCopy);
        hook._hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hook._proc, GetModuleHandle(null), 0);
        if (hook._hookId == IntPtr.Zero)
            Log.Warn("Capture Ctrl+C hook install failed (win32 error {Error}) — 截圖 stays on the toolbar only",
                Marshal.GetLastWin32Error());
        return hook;
    }

    private static bool IsDown(int key) => (GetKeyState(key) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || wParam != (IntPtr)WM_KEYDOWN || Marshal.ReadInt32(lParam) != VK_C ||
            !IsDown(VK_CONTROL) || IsDown(VK_SHIFT) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN) ||
            TypingWindows.HaveForeground)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        // Post, never wait: a hook that outruns LowLevelHooksTimeout is dropped by Windows.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(_onCopy);
        return (IntPtr)1;
    }

    public void Dispose()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }
}
