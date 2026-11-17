using System.Runtime.InteropServices;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services.LongShot;

/// <summary>
/// While a 長截圖 stitches left and right, the plain wheel over the region scrolls the page sideways:
/// the turn is taken and sent on as a horizontal one, at half its size like 鼠標增強's. Wheel down is
/// right, wheel up is left. Outside the region the wheel is left alone.
/// </summary>
/// <remarks>
/// No key to hold: in this mode sideways is the only way the page can usefully move, and many
/// programs have no way at all to scroll sideways from the wheel.
/// </remarks>
internal sealed class LongShotWheelHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_MOUSE_LL = 14, WM_MOUSEWHEEL = 0x020A;
    private const uint LLMHF_INJECTED = 0x1;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate.
    private readonly LowLevelMouseProc _proc;
    private readonly HookThread _thread = new("TextTack long-screenshot wheel hook");
    private readonly System.Drawing.Rectangle _region;
    private IntPtr _hookId;
    private int _remainder;

    private LongShotWheelHook(System.Drawing.Rectangle region)
    {
        _region = region;
        _proc = HookCallback;
    }

    /// <param name="region">Where the wheel is turned sideways, in physical pixels.</param>
    public static LongShotWheelHook Install(System.Drawing.Rectangle region)
    {
        var hook = new LongShotWheelHook(region);
        hook._thread.Start();
        hook._thread.Invoke(() =>
        {
            hook._hookId = SetWindowsHookEx(WH_MOUSE_LL, hook._proc, GetModuleHandle(null), 0);
            if (hook._hookId == IntPtr.Zero)
                Log.Warn("Long screenshot wheel hook install failed (win32 error {Error})", Marshal.GetLastWin32Error());
        });
        return hook;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL)
        {
            // MSLLHOOKSTRUCT: pt.x, pt.y, mouseData, flags.
            int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
            uint flags = (uint)Marshal.ReadInt32(lParam, 12);
            if ((flags & LLMHF_INJECTED) == 0 && _region.Contains(x, y))
            {
                int delta = (short)(Marshal.ReadInt32(lParam, 8) >> 16);
                int sideways = MouseEnhanceRules.ScaleHorizontal(ref _remainder, -delta);

                // Sent after the hook returns: input injected from inside the hook would come
                // straight back through it before this turn has been swallowed.
                if (sideways != 0)
                    _thread.Dispatcher?.BeginInvoke(DispatcherPriority.Send, () => SendHorizontal(sideways));
                return 1;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static void SendHorizontal(int delta)
    {
        var input = new[]
        {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { mouseData = delta, dwFlags = MOUSEEVENTF_HWHEEL } },
        };
        if (SendInput(1, input, Marshal.SizeOf<INPUT>()) != 1)
            Log.Debug("Long screenshot: sending a horizontal scroll failed (win32 error {Error})", Marshal.GetLastWin32Error());
    }

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

    private const uint INPUT_MOUSE = 0, MOUSEEVENTF_HWHEEL = 0x01000;

    // INPUT is a type and a union; the mouse member is the only one used, and the largest on x64
    // after the keyboard and hardware ones, so the struct is the right size as it stands.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy, mouseData;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
