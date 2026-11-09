using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OverTranslate.Services;

public class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    /// <summary>
    /// Identifies this registration to Windows, which keys them per window handle.
    /// </summary>
    /// <remarks>
    /// Every instance needs its own: the application registers more than one combination against
    /// the same hidden window, and a shared id would have the second registration refused and the
    /// first one's messages answered by both hooks. The number itself means nothing beyond being
    /// distinct — see the constants below for the ones in use.
    /// </remarks>
    private readonly int _id;
    private readonly Func<IntPtr, int, uint, uint, bool> _register;
    private readonly Func<IntPtr, int, bool> _unregister;

    /// <summary>The screenshot capture shortcut, the application's original and main one.</summary>
    public const int CaptureId = 9001;

    /// <summary>The shortcut that opens the translation window.</summary>
    public const int TranslationWindowId = 9002;

    /// <summary>The 即時翻譯 shortcut: starts framing, then translating, then pauses and resumes (#324).</summary>
    public const int RealtimePauseId = 9004;

    /// <summary>The shortcut that summons 取詞翻譯's popup.</summary>
    public const int QuickLookupId = 9005;

    /// <summary>The shortcut that replaces the selection with its translation.</summary>
    public const int QuickTranslateId = 9006;

    /// <summary>The 貼圖 shortcut (F3 by default): pins the clipboard under the pointer.</summary>
    public const int PinId = 9007;

    /// <summary>The 便利貼 shortcut (F2 by default): makes a note from the clipboard.</summary>
    public const int StickyNoteId = 9008;

    /// <summary>The shortcut that makes a blank 便利貼 (Alt+F2 by default).</summary>
    public const int StickyNoteNewId = 9009;

    /// <summary>The 追加翻譯 shortcut (unbound by default): pastes the translation after the selection.</summary>
    public const int QuickAppendId = 9010;

    public GlobalHotkey(int id) : this(id, RegisterHotKey, UnregisterHotKey) { }

    internal GlobalHotkey(int id, Func<IntPtr, int, uint, uint, bool> register,
        Func<IntPtr, int, bool> unregister)
    {
        _id = id;
        _register = register;
        _unregister = unregister;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event EventHandler? HotkeyPressed;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _registered;

    public void Register(IntPtr hwnd, uint modifiers, uint virtualKey)
    {
        Unregister();
        _hwnd = hwnd;

        // MOD_NOREPEAT added here rather than stored: it is how the shortcut is registered, not part
        // of what the user chose, and settings.json should go on saying only what they picked.
        // Without it Windows repeats WM_HOTKEY at the keyboard's repeat rate while the key is held,
        // and every one of these shortcuts is a toggle or a session start — held for half a second,
        // a single key would switch the one-shot view back and forth a dozen times.
        _registered = _register(hwnd, _id, modifiers | MOD_NOREPEAT, virtualKey);
        if (_registered)
        {
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);
        }
    }

    public void Unregister()
    {
        if (_registered)
        {
            _unregister(_hwnd, _id);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();

    // Modifier constants for display/recording
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    // Registration only, never recorded or displayed: see Register.
    private const uint MOD_NOREPEAT = 0x4000;

    public static string ModifiersToString(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        return string.Join("+", parts);
    }
}
