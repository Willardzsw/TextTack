using System.Text.Json.Serialization;

namespace OverTranslate.Models;

/// <summary>A switch the tray menu can show: every shortcut's on/off, and 鼠標增強's master switch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureToggle
{
    CaptureHotkey,
    TranslationWindowHotkey,
    RealtimePauseHotkey,
    QuickLookupHotkey,
    QuickTranslateHotkey,
    PinHotkey,
    /// <summary>鼠標增強's master switch.</summary>
    MouseEnhance,
    // The three tools' own switches. Kept so settings files that hid them still read; the menu
    // offers the master switch instead.
    MouseTransparency,
    MouseHorizontalScroll,
    MouseResize,
    StickyNoteHotkey,
    QuickAppendHotkey,
    /// <summary>取詞翻譯's 簡單顯示: a label beside the selection instead of the popup.</summary>
    QuickLookupSimple,
}

/// <summary>What the tray icon's right-click menu shows.</summary>
public class TrayMenuSettings
{
    /// <summary>
    /// The switches left out of the menu. Stored as the ones hidden, not the ones shown, so a switch
    /// added later appears without anyone having to tick it.
    /// </summary>
    public List<FeatureToggle> HiddenToggles { get; set; } = [];
}

/// <summary>Reads and writes each <see cref="FeatureToggle"/> on the settings it lives in.</summary>
public static class FeatureToggles
{
    /// <summary>Menu order: the shortcuts as the settings page lists them, then the mouse tools.</summary>
    public static readonly FeatureToggle[] All =
    [
        FeatureToggle.CaptureHotkey,
        FeatureToggle.QuickLookupHotkey,
        FeatureToggle.QuickLookupSimple,
        FeatureToggle.QuickTranslateHotkey,
        FeatureToggle.QuickAppendHotkey,
        FeatureToggle.TranslationWindowHotkey,
        FeatureToggle.RealtimePauseHotkey,
        FeatureToggle.PinHotkey,
        FeatureToggle.StickyNoteHotkey,
        FeatureToggle.MouseEnhance,
    ];

    public static bool IsMouse(FeatureToggle toggle) =>
        toggle is FeatureToggle.MouseEnhance or FeatureToggle.MouseTransparency or FeatureToggle.MouseHorizontalScroll or FeatureToggle.MouseResize;

    /// <summary>The string key naming the switch — the same name its settings row uses.</summary>
    public static string NameKey(FeatureToggle toggle) => toggle switch
    {
        FeatureToggle.CaptureHotkey => "S.Settings.CaptureHotkey",
        FeatureToggle.TranslationWindowHotkey => "S.Settings.WindowHotkey",
        FeatureToggle.RealtimePauseHotkey => "S.Settings.RealtimePauseHotkey",
        FeatureToggle.QuickLookupHotkey => "S.Settings.QuickLookupHotkey",
        FeatureToggle.QuickLookupSimple => "S.Settings.QuickLookupSimple",
        FeatureToggle.QuickTranslateHotkey => "S.Settings.QuickTranslateHotkey",
        FeatureToggle.QuickAppendHotkey => "S.Settings.QuickAppendHotkey",
        FeatureToggle.PinHotkey => "S.Settings.PinHotkey",
        FeatureToggle.StickyNoteHotkey => "S.Settings.StickyNoteHotkey",
        FeatureToggle.MouseEnhance => "S.Settings.MouseEnhance",
        FeatureToggle.MouseTransparency => "S.Settings.MouseFade",
        FeatureToggle.MouseHorizontalScroll => "S.Settings.MouseScroll",
        FeatureToggle.MouseResize => "S.Settings.MouseResize",
        _ => throw new ArgumentOutOfRangeException(nameof(toggle)),
    };

    public static bool Get(AppSettings s, FeatureToggle toggle) => toggle switch
    {
        FeatureToggle.CaptureHotkey => s.CaptureHotkeyEnabled,
        FeatureToggle.TranslationWindowHotkey => s.TranslationWindowHotkeyEnabled,
        FeatureToggle.RealtimePauseHotkey => s.RealtimePauseHotkeyEnabled,
        FeatureToggle.QuickLookupHotkey => s.QuickLookupHotkeyEnabled,
        FeatureToggle.QuickLookupSimple => s.QuickLookup.SimpleDisplay,
        FeatureToggle.QuickTranslateHotkey => s.QuickTranslateHotkeyEnabled,
        FeatureToggle.QuickAppendHotkey => s.QuickAppendHotkeyEnabled,
        FeatureToggle.PinHotkey => s.PinClipboardHotkeyEnabled,
        FeatureToggle.StickyNoteHotkey => s.StickyNoteHotkeyEnabled,
        FeatureToggle.MouseEnhance => Mouse(s).Enabled,
        FeatureToggle.MouseTransparency => Mouse(s).TransparencyEnabled,
        FeatureToggle.MouseHorizontalScroll => Mouse(s).HorizontalScrollEnabled,
        FeatureToggle.MouseResize => Mouse(s).ResizeEnabled,
        _ => false,
    };

    public static void Set(AppSettings s, FeatureToggle toggle, bool on)
    {
        switch (toggle)
        {
            case FeatureToggle.CaptureHotkey: s.CaptureHotkeyEnabled = on; break;
            case FeatureToggle.TranslationWindowHotkey: s.TranslationWindowHotkeyEnabled = on; break;
            case FeatureToggle.RealtimePauseHotkey: s.RealtimePauseHotkeyEnabled = on; break;
            case FeatureToggle.QuickLookupHotkey: s.QuickLookupHotkeyEnabled = on; break;
            case FeatureToggle.QuickLookupSimple: s.QuickLookup.SimpleDisplay = on; break;
            case FeatureToggle.QuickTranslateHotkey: s.QuickTranslateHotkeyEnabled = on; break;
            case FeatureToggle.QuickAppendHotkey: s.QuickAppendHotkeyEnabled = on; break;
            case FeatureToggle.PinHotkey: s.PinClipboardHotkeyEnabled = on; break;
            case FeatureToggle.StickyNoteHotkey: s.StickyNoteHotkeyEnabled = on; break;
            case FeatureToggle.MouseEnhance: Mouse(s).Enabled = on; break;
            case FeatureToggle.MouseTransparency: Mouse(s).TransparencyEnabled = on; break;
            case FeatureToggle.MouseHorizontalScroll: Mouse(s).HorizontalScrollEnabled = on; break;
            case FeatureToggle.MouseResize: Mouse(s).ResizeEnabled = on; break;
        }
    }

    /// <summary>The switches the tray menu shows, in menu order.</summary>
    public static IEnumerable<FeatureToggle> Shown(AppSettings s)
    {
        var hidden = s.TrayMenu?.HiddenToggles ?? [];
        return All.Where(t => !hidden.Contains(t));
    }

    private static MouseEnhanceSettings Mouse(AppSettings s) => s.MouseEnhance ??= new MouseEnhanceSettings();
}
