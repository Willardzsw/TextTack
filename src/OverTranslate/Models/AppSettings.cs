using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
// Stored by name, so a new engine can go where it reads best without moving anyone's saved choice.
// Google is 「Google 翻譯 (標準)」 — the Web endpoint backed by the RPC one, which write alike.
// Google2 was 「Google 翻譯 (RPC)」 as a choice of its own; it stays so files that saved it still
// read, and is read as Google (LanguageData.CurrentProvider). GoogleChrome is 「(Beta)」.
public enum TranslationProvider { Google, Google2, GoogleChrome, Bing, Microsoft, DeepL, OpenAI, Youdao, TranSmart, Yandex }

public class AppSettings
{
    /// <summary>
    /// Identifies this installation across the diagnostic reports it sends.
    /// </summary>
    /// <remarks>
    /// First in the file because it is what a maintainer opening a bundle looks for first, and
    /// deliberately named for what it is rather than for the one thing it is used for today — it
    /// identifies an install, and diagnostics is only the first thing that wants to know.
    ///
    /// Empty is a valid state and the one every existing install starts from. See
    /// <see cref="Services.AppIdentityService"/> for what fills it and when.
    /// </remarks>
    public string ID { get; set; } = "";

    public uint HotkeyModifiers { get; set; } = 3;
    public uint HotkeyVirtualKey { get; set; } = 0x41;
    public string HotkeyDisplay { get; set; } = "Ctrl+Alt+A";
    public ShortcutInputKind HotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton HotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <summary>
    /// The shortcut that opens the translation window. Ctrl+Alt+W by default.
    /// </summary>
    /// <remarks>
    /// A convenience, not a headline: unlike the capture shortcut above it is not announced at
    /// startup and nothing in the interface advertises it, because the window it opens is already
    /// one click away in the tray. It opens and only opens — pressing it again brings the window
    /// forward rather than closing it, which is what every other way into this window does.
    /// </remarks>
    public uint TranslationWindowHotkeyModifiers { get; set; } = 3;

    public uint TranslationWindowHotkeyVirtualKey { get; set; } = 0x57;

    public string TranslationWindowHotkeyDisplay { get; set; } = "Ctrl+Alt+W";
    public ShortcutInputKind TranslationWindowHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton TranslationWindowHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <summary>
    /// Whether the translation-window shortcut is registered at all.
    /// </summary>
    /// <remarks>
    /// There is no matching field for the capture shortcut, and deliberately not: that one is the
    /// feature the application exists for, so its checkbox is ticked and disabled rather than backed
    /// by a value. A stored flag that must always be true is a way to end up with it false.
    /// </remarks>
    public bool TranslationWindowHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Whether the capture shortcut is registered. Only the shortcut: the capture button on the
    /// translation page and the tray still start a capture with it off. User-requested switch.
    /// </summary>
    public bool CaptureHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Pauses and resumes a running realtime session. Ctrl+Alt+S by default.
    /// </summary>
    /// <remarks>
    /// Stored as three fields — the modifiers and key Windows is given, plus the text the settings
    /// page shows — because the display string cannot be derived from the other two without a
    /// key-name table, and the recorder already has the user's own spelling of it at the moment they
    /// press the combination.
    ///
    /// Ctrl+Alt+S was the block-framing shortcut's default until that shortcut was removed: a session
    /// now begins by naming what it reads, which is a live window handle chosen from what is open,
    /// and no settings file can answer that. The key it left behind goes to the one realtime shortcut
    /// there still is. Anyone who had already recorded their own combination keeps it — a default
    /// only fills in what nobody has answered.
    /// </remarks>
    public uint RealtimePauseHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="RealtimePauseHotkeyModifiers"/>
    public uint RealtimePauseHotkeyVirtualKey { get; set; } = 0x53;

    /// <inheritdoc cref="RealtimePauseHotkeyModifiers"/>
    public string RealtimePauseHotkeyDisplay { get; set; } = "Ctrl+Alt+S";
    public ShortcutInputKind RealtimePauseHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton RealtimePauseHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool RealtimePauseHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Summons 取詞翻譯's popup over whatever the user is reading. Ctrl+C+C by default (#321).
    /// </summary>
    /// <remarks>
    /// Flat, with every other shortcut: 設定 owns them as one set on one page, and a shortcut filed
    /// under the feature it starts would be the only one of the four the user could not find beside
    /// its siblings.
    ///
    /// Ctrl+C+C because selecting and copying is what someone about to look a word up is already
    /// doing, and the first Ctrl+C is the copy the popup then reads — nothing is synthesised and the
    /// clipboard is left as the user made it. It also claims no combination from anyone. The cost is
    /// programs run as administrator, which a listened-for shortcut cannot hear while they are in
    /// front; the settings row says so. Only new installations get it: every saved settings file
    /// carries the lookup kind, so one that has Ctrl+Alt+Q keeps it.
    ///
    /// The keyboard fields keep Ctrl+Alt+Q, Q being the 取 of 取詞, as what a user who switches the
    /// kind back to the keyboard would have had.
    /// </remarks>
    public uint QuickLookupHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="QuickLookupHotkeyModifiers"/>
    public uint QuickLookupHotkeyVirtualKey { get; set; } = 0x51;

    /// <inheritdoc cref="QuickLookupHotkeyModifiers"/>
    public string QuickLookupHotkeyDisplay { get; set; } = "Ctrl+C+C";
    public ShortcutInputKind QuickLookupHotkeyInputKind { get; set; } = ShortcutInputKind.CtrlCC;
    public GamepadShortcutButton QuickLookupHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool QuickLookupHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// 替換翻譯's shortcut: replaces the selected text with its translation, in place. Ctrl+Alt+E
    /// by default.
    /// </summary>
    /// <remarks>
    /// QuickTranslate in these names is 替換翻譯's name from before the rename; they are settings
    /// file keys and keep it — see <see cref="QuickTranslateSettings"/>.
    ///

    /// E for 取代 — the letters the other four are named after are taken (A, W, S, Q), and this one
    /// is the shortcut people reach for while writing in a language that is not theirs, where what
    /// they want is the replacement rather than a window about it. Ctrl+Alt+E is claimed by nothing
    /// on a stock Windows.
    ///
    /// On by default, like every other optional shortcut here. It costs nothing until it is pressed,
    /// and a feature nobody can find is not a safer one — see
    /// <see cref="Views.QuickTranslate.QuickTranslateFlow"/> for what it does when there is nothing
    /// selected, which is nothing at all.
    /// </remarks>
    public uint QuickTranslateHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="QuickTranslateHotkeyModifiers"/>
    public uint QuickTranslateHotkeyVirtualKey { get; set; } = 0x45;

    /// <inheritdoc cref="QuickTranslateHotkeyModifiers"/>
    public string QuickTranslateHotkeyDisplay { get; set; } = "Ctrl+Alt+E";
    public ShortcutInputKind QuickTranslateHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton QuickTranslateHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool QuickTranslateHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// 貼圖's global shortcut: pins what is on the clipboard (a picture, or text drawn as a card)
    /// under the pointer. F3 by default, the key Snipaste uses for the same thing.
    /// </summary>
    /// <remarks>
    /// Keys of their own (PinClipboard…) rather than the PinHotkey… ones 1.1.0–1.1.4 wrote, whose
    /// default was Ctrl+Alt+P and which also pinned the selection during a capture: that is now a
    /// capture gesture (CaptureSettings.PinSelectionTrigger), and a profile that had saved the old
    /// default would otherwise never get F3. RegisterHotKey takes F3 from every other program, so
    /// with Snipaste also running one of the two loses it.
    /// </remarks>
    public uint PinClipboardHotkeyModifiers { get; set; } = 0;

    /// <inheritdoc cref="PinClipboardHotkeyModifiers"/>
    public uint PinClipboardHotkeyVirtualKey { get; set; } = 0x72;

    /// <inheritdoc cref="PinClipboardHotkeyModifiers"/>
    public string PinClipboardHotkeyDisplay { get; set; } = "F3";
    public ShortcutInputKind PinClipboardHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton PinClipboardHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool PinClipboardHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// 便利貼's shortcut: makes a note from the clipboard — its text, or the text read from a copied
    /// picture — under the pointer. F2 by default. RegisterHotKey takes F2 from every program (rename in Explorer, edit cell in Excel); the user chose it knowing that.
    /// </summary>
    public uint StickyNoteHotkeyModifiers { get; set; } = 0;

    /// <inheritdoc cref="StickyNoteHotkeyModifiers"/>
    public uint StickyNoteHotkeyVirtualKey { get; set; } = 0x71;

    /// <inheritdoc cref="StickyNoteHotkeyModifiers"/>
    public string StickyNoteHotkeyDisplay { get; set; } = "F2";
    public ShortcutInputKind StickyNoteHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton StickyNoteHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool StickyNoteHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// 便利貼's second shortcut: a blank note under the pointer, ready to type into, whatever is on
    /// the clipboard. Alt+F2 by default. Switched on and off with <see cref="StickyNoteHotkeyEnabled"/>.
    /// </summary>
    public uint StickyNoteNewHotkeyModifiers { get; set; } = 1;

    /// <inheritdoc cref="StickyNoteNewHotkeyModifiers"/>
    public uint StickyNoteNewHotkeyVirtualKey { get; set; } = 0x71;

    /// <inheritdoc cref="StickyNoteNewHotkeyModifiers"/>
    public string StickyNoteNewHotkeyDisplay { get; set; } = "Alt+F2";
    public ShortcutInputKind StickyNoteNewHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton StickyNoteNewHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <summary>
    /// Whether a released copy asks the user's own GitHub repository for a newer release when it
    /// starts. Never the upstream project; a source build never asks at all.
    /// </summary>
    public bool AutoCheckUpdates { get; set; } = true;

    public string SourceLanguage { get; set; } = LanguageData.DefaultOcrSourceLanguage;
    public string TargetLanguage { get; set; } = "ZH-HANT";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;

    /// <summary>
    /// The services left out of the translation-service menus, by name. Empty: all offered.
    /// </summary>
    /// <remarks>
    /// Names rather than <see cref="TranslationProvider"/> values, so one this build does not know
    /// costs only itself instead of the whole list. Read through <see cref="ProviderVisibility"/>,
    /// never directly: that is where unknown names, retired ones and a list that hides everything
    /// are settled.
    /// </remarks>
    public List<string> HiddenProviders { get; set; } = [];

    public string ApiKey { get; set; } = "";
    /// <summary>
    /// The OpenAI-compatible server to talk to, or empty for
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.DefaultBaseUrl"/>.
    /// </summary>
    /// <remarks>
    /// Empty rather than a copy of the default: stamping today's value into the settings file would
    /// freeze the user on it if it ever changed.
    ///
    /// The server, not the model — which model to ask it for lives on a profile, so that switching
    /// model does not ask the user to retype an address that has not moved. See
    /// <see cref="OpenAiModelProfile"/>.
    /// </remarks>
    public string OpenAiBaseUrl { get; set; } = "";
    public string OpenAiApiKey { get; set; } = "";

    /// <summary>
    /// How long one OpenAI-compatible request may take, in seconds, or null for
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.DefaultTimeoutSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Null rather than a copy of the default, for the same reason as <see cref="OpenAiBaseUrl"/>.
    /// A value under the minimum — a hand-edited file — is read as the minimum, see
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.TimeoutSecondsFor"/>.
    /// </remarks>
    public int? OpenAiTimeoutSeconds { get; set; }

    /// <summary>
    /// The key 「Google 翻譯 (Beta)」 sends, read from Google's translate element the first time it
    /// is used and again only when Google refuses it. Empty until then.
    /// </summary>
    /// <remarks>
    /// Kept here rather than written into the program, and kept at all so that a key that works is
    /// never looked up twice — see <c>GoogleChromeKeySource</c>.
    /// Named to end in ApiKey so the diagnostic bundle hides it like the user's own keys.
    /// </remarks>
    public string GoogleChromeApiKey { get; set; } = "";

    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// The version that last started with this file, written at every start that finds a different
    /// one. Empty in a file from 2.9.0 or earlier, which never wrote it.
    /// </summary>
    /// <remarks>
    /// Read once, before anything is saved, to decide whether to keep a copy of the file from before
    /// an update that changes what it means — see <see cref="Services.SettingsBackup"/>.
    /// </remarks>
    public string LastRunVersion { get; set; } = "";

    /// <summary>
    /// The interface language — one of the codes in
    /// <see cref="Services.LocalizationService.Options"/>, such as "zh-Hant" or "en".
    /// Empty means "not chosen yet".
    /// </summary>
    /// <remarks>
    /// Empty rather than a hardcoded default so a first run can follow the OS language — see
    /// <see cref="Services.LocalizationService.ResolveSystemDefault"/>. Once the user picks one it
    /// is stored verbatim and the OS is never consulted again, because an explicit choice should
    /// survive someone changing their Windows display language.
    ///
    /// This is the interface language only. It has no bearing on
    /// <see cref="TargetLanguage"/> or <see cref="RealtimeSettings.TargetLanguage"/>: what someone reads the
    /// buttons in and what they want subtitles translated into are unrelated, and a Taiwanese user
    /// running the app in English still wants Chinese output.
    /// </remarks>
    public string UiLanguage { get; set; } = "";
    public bool AutoTranslateAfterSelection { get; set; } = false;
    public bool SaveScreenshotToDisk { get; set; } = false;

    /// <summary>The recognition model choice, shared by screenshot and realtime OCR.</summary>
    public OcrModelSettings OcrModels { get; set; } = new();

    /// <summary>Empty means "use ScreenshotSaveService.DefaultDirectory" (圖片\OverTranslate).</summary>
    public string ScreenshotSavePath { get; set; } = "";
    /// <summary>Off by default: Debug records the recognised text, i.e. the user's screen contents.</summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>What 截圖翻譯 keeps between capture sessions, grouped.</summary>
    /// <remarks>
    /// The first grouped section in declaration order because 截圖翻譯 appears before 即時翻譯 in the
    /// product's feature order. Properties are written in this order, so appsettings.json keeps its
    /// flat legacy half first and its feature-owned groups together afterwards.
    /// </remarks>
    public CaptureSettings Capture { get; set; } = new();

    /// <summary>貼圖操作: how a pinned picture is zoomed, faded, closed and reset.</summary>
    public PinSettings Pin { get; set; } = new();

    /// <summary>鼠標增強: fade, scroll sideways and resize windows with modifier + mouse.</summary>
    public MouseEnhanceSettings MouseEnhance { get; set; } = new();

    /// <summary>便利貼: how new notes look, the picture-reading language, and the archive log.</summary>
    public StickyNoteSettings StickyNote { get; set; } = new();

    /// <summary>Which feature switches the tray icon's right-click menu shows.</summary>
    public TrayMenuSettings TrayMenu { get; set; } = new();

    /// <summary>What 取詞翻譯 keeps between lookups, grouped.</summary>
    public QuickLookupSettings QuickLookup { get; set; } = new();

    /// <summary>
    /// Language preferences used only by 替換翻譯 — named QuickTranslate from before the rename, see
    /// <see cref="QuickTranslateSettings"/>.
    /// </summary>
    public QuickTranslateSettings QuickTranslate { get; set; } = new();

    /// <summary>What 即時翻譯 keeps between sittings, grouped.</summary>
    public RealtimeSettings Realtime { get; set; } = new();

    /// <summary>What the OCR debug overlay draws, grouped.</summary>
    public OcrDebugSettings OcrDebug { get; set; } = new();

    /// <summary>The OpenAI-compatible provider's prompt library, grouped.</summary>
    /// <remarks>
    /// Last, and the only group not named after one of the three features: this belongs to a
    /// translation service rather than to a page. See <see cref="OpenAiSettings"/> for why the
    /// endpoint and the model above it stayed flat.SelectedProfileId
    /// </remarks>
    public OpenAiSettings OpenAi { get; set; } = new();
}
