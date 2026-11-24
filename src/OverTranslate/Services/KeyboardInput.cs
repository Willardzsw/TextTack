using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Synthesised keystrokes, sent to whatever application has the foreground.
/// </summary>
/// <remarks>
/// Two features borrow the foreground application's own editing shortcuts rather than asking it
/// anything: <see cref="SelectedTextReader"/> sends Ctrl+C to find out what is selected, and
/// <see cref="SelectionReplacer"/> sends Ctrl+V to put a translation in its place. Windows offers no
/// way to do either of those things directly to another process, so the chord is the mechanism, and
/// this is the one copy of it.
///
/// Everything here is about a chord surviving the trip. The modifiers the user is still holding —
/// the ones that fired the shortcut — are released first, so what arrives is a plain Ctrl+key rather
/// than the Ctrl+Alt+key the reader actually has under their fingers. The chord is then held down
/// briefly instead of being pressed and released in one batch: ordinary controls consume every key
/// event, but a game may sample keyboard state once per frame and miss a chord whose complete
/// lifetime falls between two samples.
/// </remarks>
internal static class KeyboardInput
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <inheritdoc cref="KeyboardInput"/>
    private static readonly TimeSpan ChordHold = TimeSpan.FromMilliseconds(40);

    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_C = 0x43;
    public const ushort VK_V = 0x56;
    public const ushort VK_RIGHT = 0x27;

    /// <summary>Sends Ctrl+<paramref name="virtualKey"/> to whatever has the foreground.</summary>
    /// <remarks>
    /// The user's own key releases arrive afterwards as repeats of an up state, which every
    /// application already tolerates.
    /// </remarks>
    public static Task SendControlChordAsync(ushort virtualKey)
    {
        var press = new List<INPUT>();

        press.AddRange(ReleaseModifiers(IsKeyDown));

        press.Add(Key(VK_CONTROL, up: false));
        press.Add(Key(virtualKey, up: false));

        return HoldChordAsync(
            () => Inject(press.ToArray()),
            () => Task.Delay(ChordHold),
            () => Inject([Key(virtualKey, up: true), Key(VK_CONTROL, up: true)]));
    }

    /// <summary>Sends one plain key, with every modifier the user may still hold released first.</summary>
    /// <remarks>
    /// The arrow keys are extended keys; without the flag some applications read the numeric-pad
    /// arrow, which with Num Lock on types a digit instead of moving the caret.
    /// </remarks>
    public static Task SendKeyAsync(ushort virtualKey)
    {
        var press = new List<INPUT>();

        press.AddRange(ReleaseModifiers(IsKeyDown));

        var extended = virtualKey is >= 0x21 and <= 0x28;
        press.Add(Key(virtualKey, up: false, extended));

        return HoldChordAsync(
            () => Inject(press.ToArray()),
            () => Task.Delay(ChordHold),
            () => Inject([Key(virtualKey, up: true, extended)]));
    }

    /// <summary>
    /// An unassigned virtual key, pressed while Alt or Win is still down so that releasing them is
    /// not read as a lone tap.
    /// </summary>
    internal const ushort VK_MASK = 0xE8;

    /// <summary>The key events that release every modifier the user may still be holding.</summary>
    /// <remarks>
    /// The shortcut's own letter was taken by RegisterHotKey, so all the foreground application saw
    /// was Alt going down. Releasing it now would complete a lone Alt tap, which a window with a menu
    /// bar (Claude's desktop app, classic Win32 programs) answers by entering its menu — and the
    /// Ctrl+C or Ctrl+V sent next then lands on the menu instead of the text. A lone Win tap opens
    /// the Start menu the same way. Tapping a key nobody uses while they are still down makes the
    /// release an ordinary one; the user's own later release then follows that key, not a lone Alt,
    /// so it does not open the menu either.
    /// </remarks>
    internal static IEnumerable<INPUT> ReleaseModifiers(Func<ushort, bool> isDown)
    {
        if (isDown(VK_MENU) || isDown(VK_LWIN) || isDown(VK_RWIN))
        {
            yield return Key(VK_MASK, up: false);
            yield return Key(VK_MASK, up: true);
        }

        foreach (var modifier in new ushort[] { VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN, VK_CONTROL })
            yield return Key(modifier, up: true);
    }

    private static bool IsKeyDown(ushort virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>Keeps an injected chord down long enough to cross a frame boundary.</summary>
    /// <remarks>
    /// The release is in a finally block: a chord left down would be a stuck Ctrl key, which is the
    /// whole keyboard broken for the application underneath rather than one failed translation.
    /// </remarks>
    internal static async Task HoldChordAsync(Action press, Func<Task> hold, Action release)
    {
        press();
        try
        {
            await hold();
        }
        finally
        {
            release();
        }
    }

    public static void Inject(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != (uint)inputs.Length)
            Log.Debug("SendInput accepted {SentCount} of {RequestedCount} keyboard events", sent, inputs.Length);
    }

    /// <summary>
    /// Tags every key sent from here, so <see cref="GlobalAuxiliaryHotkeys"/>' keyboard hook can tell
    /// them from the user's. The Ctrl in a synthesised copy is otherwise indistinguishable from a
    /// real one, and could complete a Ctrl twice nobody pressed.
    /// </summary>
    public static readonly IntPtr InjectedMarker = new(0x4F544B49); // "OTKI"

    public static INPUT Key(ushort virtualKey, bool up, bool extended = false) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
                dwExtraInfo = InjectedMarker,
            },
        },
    };

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;

        // SendInput rejects a structure that is not the size it expects, and the size it expects is
        // that of the widest member of the union — so MOUSEINPUT has to be declared even though
        // nothing here sends a mouse event.
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
