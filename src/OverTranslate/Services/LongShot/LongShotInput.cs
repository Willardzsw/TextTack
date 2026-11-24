using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services.LongShot;

/// <summary>
/// The wheel turns a 長截圖 sends when it scrolls the page itself, and keeping the pointer over the
/// region while it does: a turn goes to the window under the pointer.
/// </summary>
internal static class LongShotInput
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Scrolls by <paramref name="units"/> (120 is one notch of the wheel): positive is down, or
    /// right when <paramref name="axis"/> is horizontal.
    /// </summary>
    public static void Scroll(StitchAxis axis, int units)
    {
        if (units == 0) return;
        var input = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = axis == StitchAxis.Vertical
                    ? new MOUSEINPUT { mouseData = -units, dwFlags = MOUSEEVENTF_WHEEL }
                    : new MOUSEINPUT { mouseData = units, dwFlags = MOUSEEVENTF_HWHEEL },
            },
        };
        if (SendInput(1, input, Marshal.SizeOf<INPUT>()) != 1)
            Log.Debug("Long screenshot: sending a scroll failed (win32 error {Error})", Marshal.GetLastWin32Error());
    }

    public static System.Drawing.Point Cursor
    {
        get
        {
            GetCursorPos(out var p);
            return new System.Drawing.Point(p.X, p.Y);
        }
        set => SetCursorPos(value.X, value.Y);
    }

    private const uint INPUT_MOUSE = 0, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x01000;

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

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
