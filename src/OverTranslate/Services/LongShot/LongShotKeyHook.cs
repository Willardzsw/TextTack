using System.Runtime.InteropServices;
using System.Windows.Threading;
using NLog;

namespace OverTranslate.Services.LongShot;

/// <summary>
/// While a 長截圖 runs, Space belongs to it: held down, the page scrolls on its own; let go, it
/// stops. The key is taken before any program sees it — in a document or a chat box it would type
/// a space, in a browser jump a whole screen, in a player pause.
/// </summary>
/// <remarks>
/// Only Space on its own: with Ctrl, Alt or Win held it is left to whatever it means there (Alt+Space
/// opens the window menu). The key's auto-repeat is swallowed with it; the callback hears one press
/// and one release.
/// </remarks>
internal sealed class LongShotKeyHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_SPACE = 0x20, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate.
    private readonly LowLevelKeyboardProc _proc;
    private readonly HookThread _thread = new("TextTack long-screenshot key hook");
    private readonly Dispatcher _ui;
    private readonly Action<bool> _onSpace;
    private IntPtr _hookId;
    private bool _held;

    /// <summary>While false (a save dialog is open) Space is left alone.</summary>
    public volatile bool Enabled = true;

    private LongShotKeyHook(Dispatcher ui, Action<bool> onSpace)
    {
        _ui = ui;
        _onSpace = onSpace;
        _proc = HookCallback;
    }

    /// <param name="onSpace">Called on the calling thread's dispatcher: true when Space goes down, false when it comes up.</param>
    public static LongShotKeyHook Install(Action<bool> onSpace)
    {
        var hook = new LongShotKeyHook(Dispatcher.CurrentDispatcher, onSpace);
        hook._thread.Start();
        hook._thread.Invoke(() =>
        {
            hook._hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hook._proc, GetModuleHandle(null), 0);
            if (hook._hookId == IntPtr.Zero)
                Log.Warn("Long screenshot key hook install failed (win32 error {Error})", Marshal.GetLastWin32Error());
        });
        return hook;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // KBDLLHOOKSTRUCT: vkCode, scanCode, flags.
            int vk = Marshal.ReadInt32(lParam, 0);
            uint flags = (uint)Marshal.ReadInt32(lParam, 8);
            int message = (int)wParam;
            if (vk == VK_SPACE && (flags & LLKHF_INJECTED) == 0)
            {
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = message is WM_KEYUP or WM_SYSKEYUP;
                if (down && !_held && Enabled && !ModifierHeld())
                {
                    _held = true;
                    _ui.BeginInvoke(() => _onSpace(true));
                    return 1;
                }
                if (down && _held) return 1; // auto-repeat
                if (up && _held)
                {
                    _held = false;
                    _ui.BeginInvoke(() => _onSpace(false));
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool ModifierHeld() =>
        new[] { VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);

    public void Dispose()
    {
        _thread.Stop(() =>
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        });
    }

    // ---- Win32 ----

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
}
