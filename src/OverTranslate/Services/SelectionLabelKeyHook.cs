using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// The keys of 取詞翻譯's 簡單顯示 labels: Ctrl+C with the pointer on a label copies its translation,
/// and Esc anywhere closes them all.
/// </summary>
/// <remarks>
/// <para>A low-level hook because the labels never take the keyboard — the user goes on typing where
/// they were — so no key handler of theirs would see either key. On a thread of its own, as
/// <see cref="GlobalEscapeHook"/> is, and installed only while a label is up.</para>
///
/// <para>Ctrl+C is taken only while the pointer is on a label, and then swallowed: the application in
/// front would otherwise copy its own selection over the translation a moment later. Esc is only
/// listened to; it still reaches whatever the user is working in. A Ctrl+C this application injected
/// itself — reading a selection, see <see cref="SelectedTextReader"/> — always passes.</para>
/// </remarks>
internal sealed class SelectionLabelKeyHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_C = 0x43;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int LLKHF_INJECTED = 0x10;
    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Func<IReadOnlyCollection<IntPtr>> _labels;
    private readonly Action<IntPtr> _copy;
    private readonly Action _closeAll;
    private readonly HookThread _thread = new("OverTranslate label key hook");
    private IntPtr _hookId;

    private SelectionLabelKeyHook(Func<IReadOnlyCollection<IntPtr>> labels, Action<IntPtr> copy, Action closeAll)
    {
        _labels = labels;
        _copy = copy;
        _closeAll = closeAll;
        _proc = HookCallback;
    }

    /// <param name="labels">The labels' window handles; read on the hook thread, so a concurrent collection's.</param>
    /// <param name="copy">Copies the translation of the label with this handle; run on the UI thread.</param>
    /// <param name="closeAll">Closes every label; run on the UI thread.</param>
    public static SelectionLabelKeyHook Install(
        Func<IReadOnlyCollection<IntPtr>> labels, Action<IntPtr> copy, Action closeAll)
    {
        var hook = new SelectionLabelKeyHook(labels, copy, closeAll);
        hook._thread.Start();
        hook._thread.Invoke(() =>
        {
            hook._hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hook._proc, GetModuleHandle(null), 0);
            if (hook._hookId == IntPtr.Zero)
                Log.Warn("Label key hook install failed (win32 error {Error}) — Ctrl+C and Esc will not reach the labels",
                    Marshal.GetLastWin32Error());
        });
        return hook;
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || wParam != (IntPtr)WM_KEYDOWN || TypingWindows.HaveForeground)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (key.VkCode == VK_ESCAPE)
        {
            // Post, never wait: a hook that outruns LowLevelHooksTimeout is dropped by Windows.
            dispatcher?.BeginInvoke(_closeAll);
        }
        else if (key.VkCode == VK_C && (key.Flags & LLKHF_INJECTED) == 0 &&
                 IsDown(VK_CONTROL) && !IsDown(VK_SHIFT) && !IsDown(VK_MENU) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN) &&
                 LabelUnderPointer() is { } label)
        {
            dispatcher?.BeginInvoke(() => _copy(label));
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private IntPtr? LabelUnderPointer()
    {
        if (!GetCursorPos(out var p)) return null;
        var under = WindowFromPoint(p);
        if (under == IntPtr.Zero) return null;
        var root = GetAncestor(under, GA_ROOT);
        return _labels().Contains(root) ? root : null;
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
