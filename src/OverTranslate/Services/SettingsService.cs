using System.Collections;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

public class SettingsService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Only this build's independent profile is eligible to supply settings.
    private static readonly string SettingsDirectory = AppDataPaths.Root;

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "appsettings.json");

    /// <summary>
    /// Where the settings actually are, for the one caller that needs the file rather than the
    /// values in it: <see cref="DiagnosticBundleService"/> copies it into a problem report.
    /// </summary>
    public static string FilePath => SettingsPath;

    /// <summary>
    /// How the file is written: indented, and with the whole of Unicode left as itself.
    /// </summary>
    /// <remarks>
    /// The default encoder escapes everything outside a conservative ASCII set, which writes a
    /// prompt someone named 「測試」 as an unbroken run of <c>\uXXXX</c>. This is a file a user can
    /// open — see <see cref="Services.Realtime.RealtimeSubtitleColors"/>, which is written on the
    /// same assumption — and now that it holds the prompt library, the names and the prose in it are
    /// the user's own words rather than paths and key codes.
    ///
    /// The same encoder <see cref="DiagnosticBundleService"/> uses for its redacted copy, and for
    /// the same reason it gives: this text goes into a file, never into a web page, so the escaping
    /// of <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> that guards against being embedded in HTML buys
    /// nothing here. Widening only the character range was tried first and is not enough — it
    /// leaves <c>+</c> escaped, which writes every shortcut in the file as
    /// <c>Ctrl\u002BAlt\u002BA</c>. Half a readable file is not the point.
    ///
    /// The two copies also then read alike, which is worth something on the one occasion both are
    /// in front of the same person: a bug report held against the file it came from.
    ///
    /// Cosmetic by construction: nothing reads this file as text, both forms parse to the same
    /// string, and <see cref="Parse"/> goes through a JSON reader either way.
    /// </remarks>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    public AppSettings Current { get; private set; } = new();

    private readonly string _settingsPath;
    private bool _savingEnabled;

    private SettingsService() : this(SettingsPath)
    {
    }

    // An explicit path keeps file-failure tests entirely inside their synthetic profile.
    internal SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
        Load();
    }

    public void Load()
    {
        _savingEnabled = false;
        try
        {
            Current = Load(_settingsPath, SettingsBackup.CurrentVersion, settings =>
            {
                Current = settings;
                _savingEnabled = true;
                Save();
            });
            _savingEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A sharing violation or access failure is not a new profile. Keep the file intact,
            // even if startup or a later UI edit calls Save after the external lock disappears.
            Log.Error(ex, "Could not load settings from {0}; saving is disabled until a successful reload", _settingsPath);
        }
    }

    /// <summary>
    /// <see cref="Load()"/> with the paths, the running version and the save passed in, for a test
    /// that cannot use the real ones.
    /// </summary>
    /// <remarks>
    /// The backup comes before every save: once anything is saved, the file it would copy is gone.
    /// </remarks>
    internal static AppSettings Load(string path, string version, Action<AppSettings> save)
    {
        var json = ReadSettings(path);
        if (json is null)
        {
            var fresh = new AppSettings { LastRunVersion = version };
            save(fresh);
            return fresh;
        }

        AppSettings settings;
        try
        {
            settings = ParseDocument(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Defaults are usable for this session, but must never be saved over a damaged file.
            // Preserve the original in place so it can be repaired and loaded again.
            throw new InvalidDataException("The settings document could not be safely loaded.", ex);
        }
        if (settings.LastRunVersion != version)
        {
            // The first start after an update (or after 2.9.0 or earlier, which never wrote a
            // version). Anything else a later version has to do once on its way in goes here too,
            // before the version is written: after that this branch is not taken again.
            if (SettingsBackup.Needed(settings.LastRunVersion))
                SettingsBackup.Copy(path, SettingsBackup.PathFor(path, version));

            settings.LastRunVersion = version;
            save(settings);
        }
        return settings;
    }

    /// <summary>
    /// Reads the settings one field at a time so a single bad value cannot cost the user everything
    /// else. A file that is not JSON at all still falls back to defaults, but an individual field
    /// that cannot be read — an enum value a later build dropped, a type that changed, an explicit
    /// null — costs only that field. The previous all-or-nothing catch reset API keys and hotkeys
    /// alike whenever any one value went bad.
    /// </summary>
    public static AppSettings Parse(string json)
    {
        try
        {
            return ParseDocument(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            Log.Warn("appsettings.json could not be parsed ({0}); falling back to in-memory defaults", ex.GetType().Name);
            return new AppSettings();
        }
    }

    /// <summary>What a position-numbered GPU choice becomes. Replaceable by tests.</summary>
    internal static Func<string> LegacyGpuReplacement = Ocr.OcrExecution.PreferredGpu;

    private static AppSettings ParseDocument(string json)
    {
        var settings = new AppSettings();
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("The settings root must be an object.");

        Apply(settings, root, "");

        // Retired options, read as the ones that replaced them — the pickers no longer list them.
        settings.Provider = LanguageData.CurrentProvider(settings.Provider);
        settings.Realtime.Provider = LanguageData.CurrentProvider(settings.Realtime.Provider);
        if (settings.QuickTranslate.Provider is { } quickProvider)
            settings.QuickTranslate.Provider = LanguageData.CurrentProvider(quickProvider);
        // Retired/unrecognized OCR service choices safely return to installed language routing.
        settings.OcrModels.Selection = Ocr.OcrModelCatalog.Normalize(settings.OcrModels.Selection);
        settings.OcrModels.DetectorSize = Ocr.OcrModelCatalog.DetectorSize(settings.OcrModels);
        settings.OcrModels.RecognizerSize = Ocr.OcrModelCatalog.RecognizerSize(settings.OcrModels);
        settings.OcrModels.Device = Ocr.OcrModelCatalog.Device(settings.OcrModels);
        // The old position-numbered GPU choice, which pointed at whichever card Windows listed
        // there that boot: replaced once by the strongest GPU's name.
        if (Ocr.OcrModelCatalog.IsLegacyIndex(settings.OcrModels.Device))
            settings.OcrModels.Device = LegacyGpuReplacement();

        // 替換翻譯's languages (QuickTranslate, its name before #277). The grouped section wins.
        // Older files used flat QuickTranslate* keys, and before that shared the text-translation
        // pair. Migrate only when the group is absent.
        if (!root.ContainsKey(nameof(AppSettings.QuickTranslate)))
        {
            string LegacyLanguage(string key, string shared) =>
                !root.TryGetPropertyValue(key, out var value) ? shared :
                value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";

            settings.QuickTranslate.SourceLanguage = LanguageData.GetValidSourceCode(
                LegacyLanguage("QuickTranslateSourceLanguage", settings.SourceLanguage));
            var legacyTarget = LegacyLanguage("QuickTranslateTargetLanguage",
                root.ContainsKey(nameof(AppSettings.TargetLanguage)) ? settings.TargetLanguage : "");
            var target = LanguageData.TargetLanguages.FirstOrDefault(
                language => language.Code.Equals(legacyTarget, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
                settings.QuickTranslate.TargetLanguage = target.Code;
        }
        return settings;
    }

    /// <summary>
    /// Copies one object's worth of JSON onto <paramref name="target"/>, field by field, descending
    /// into grouped sections.
    /// </summary>
    /// <remarks>
    /// The descent is what keeps the promise above once settings are grouped. Handing a whole group
    /// to <c>Deserialize</c> would make it the unit that fails: one unreadable value inside
    /// <see cref="AppSettings.Realtime"/> would throw, and every other value in that group would go
    /// back to its default with it. Read one at a time all the way down, and the blast radius stays
    /// one value however deep it sits.
    /// </remarks>
    /// <param name="path">Where in the file this object sits, for the log to name a field properly.</param>
    private static void Apply(object target, JsonObject source, string path)
    {
        foreach (var property in target.GetType().GetProperties())
        {
            if (!property.CanWrite)
                continue;

            // Absent and explicitly-null both leave the property on its initialiser, which keeps a
            // hand-edited "ApiKey": null from turning into a null string the rest of the app trips on.
            if (!source.TryGetPropertyValue(property.Name, out var node) || node is null)
                continue;

            var name = path + property.Name;

            if (node is JsonObject group && IsSettingsGroup(property.PropertyType))
            {
                // Never null: a group is always initialised by the class that declares it, so there
                // is something to write onto whatever the file says.
                if (property.GetValue(target) is { } child)
                    Apply(child, group, name + ".");
                continue;
            }

            // A list of settings needs the same field-by-field treatment as a grouped section.
            // Deserializing it whole admitted null profiles/null strings and let one bad value
            // discard every other model profile.
            if (node is JsonArray array && property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>)
                && property.PropertyType.GetGenericArguments()[0] is { } itemType
                && IsSettingsGroup(itemType))
            {
                var list = (IList)Activator.CreateInstance(property.PropertyType)!;
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is not JsonObject item) continue;
                    var child = Activator.CreateInstance(itemType)!;
                    Apply(child, item, $"{name}[{i}].");
                    list.Add(child);
                }
                property.SetValue(target, list);
                continue;
            }

            try
            {
                var value = node.Deserialize(property.PropertyType);
                // JSON permits exponent notation that can overflow a floating-point value.
                // Non-null nullable numbers box as their underlying type, so this covers them
                // too. Keeping Infinity would make the next save of the entire profile fail.
                if (value is double number && !double.IsFinite(number)
                    || value is float single && !float.IsFinite(single))
                {
                    Log.Warn("Ignoring non-finite setting '{0}'; keeping its default", name);
                    continue;
                }
                if (value is not null)
                    property.SetValue(target, value);
            }
            catch (JsonException ex)
            {
                Log.Warn(ex, "Ignoring unreadable setting '{0}'; keeping its default", name);
            }
        }
    }

    /// <summary>
    /// Whether a property is a grouped section of the settings rather than a value.
    /// </summary>
    /// <remarks>
    /// Decided by where the type is declared, not by a list to keep in step: anything this
    /// application defines alongside <see cref="AppSettings"/> is a group of settings, and anything
    /// from the framework — string above all, which is a class and would otherwise qualify — is a
    /// value.
    /// </remarks>
    private static bool IsSettingsGroup(Type type) =>
        type is { IsClass: true, IsArray: false }
        && type != typeof(string)
        && type.Namespace == typeof(AppSettings).Namespace;

    /// <summary>The exact bytes <see cref="Save"/> writes, for a test that has no file to read.</summary>
    internal static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, WriteOptions);

    public event EventHandler? OcrDebugChanged;

    public void UpdateOcrDebug(bool? showLines = null, bool? showGroups = null, bool? showOnTranslation = null)
    {
        var debug = Current.OcrDebug;
        var lines = showLines ?? debug.ShowLineBoxes;
        var groups = showGroups ?? debug.ShowGroupBoxes;
        var onTranslation = showOnTranslation ?? debug.ShowOnTranslation;
        if (lines == debug.ShowLineBoxes && groups == debug.ShowGroupBoxes && onTranslation == debug.ShowOnTranslation) return;
        debug.ShowOnTranslation = onTranslation;
        debug.ShowLineBoxes = lines;
        debug.ShowGroupBoxes = groups;
        Save();
        OcrDebugChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when 自動翻譯, 儲存截圖 or 截圖後自動關閉 changes. The first two are set from two
    /// places — the settings page and the capture toolbar's 設定 panel — and each has to show
    /// what the other just wrote.
    /// </summary>
    public event EventHandler? CaptureOptionsChanged;

    /// <summary>
    /// A feature switch was flipped from the tray menu, so an open settings page shows it.
    /// </summary>
    public event EventHandler? FeatureTogglesChanged;

    /// <summary>Sets one feature switch, saves, and tells whoever shows it.</summary>
    public void SetFeatureToggle(OverTranslate.Models.FeatureToggle toggle, bool on)
    {
        OverTranslate.Models.FeatureToggles.Set(Current, toggle, on);
        Save();
        FeatureTogglesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateCaptureOptions(
        bool? autoTranslate = null, bool? saveScreenshot = null, bool? closeAfterCopy = null,
        bool? doubleClickToFinish = null, bool? rightClickToCancel = null,
        bool? closeAfterCopyText = null, bool? closeAfterCopyTranslation = null,
        bool? autoSelectWindow = null, bool? autoSelectElement = null)
    {
        var current = Current;
        var auto = autoTranslate ?? current.AutoTranslateAfterSelection;
        var save = saveScreenshot ?? current.SaveScreenshotToDisk;
        var close = closeAfterCopy ?? current.Capture.CloseAfterCopy;
        var doubleClick = doubleClickToFinish ?? current.Capture.DoubleClickToFinish;
        var rightClick = rightClickToCancel ?? current.Capture.RightClickToCancel;
        var closeText = closeAfterCopyText ?? current.Capture.CloseAfterCopyText;
        var closeTranslation = closeAfterCopyTranslation ?? current.Capture.CloseAfterCopyTranslation;
        var pickWindow = autoSelectWindow ?? current.Capture.AutoSelectWindow;
        var pickElement = autoSelectElement ?? current.Capture.AutoSelectElement;
        if (auto == current.AutoTranslateAfterSelection && save == current.SaveScreenshotToDisk &&
            close == current.Capture.CloseAfterCopy && doubleClick == current.Capture.DoubleClickToFinish &&
            rightClick == current.Capture.RightClickToCancel &&
            closeText == current.Capture.CloseAfterCopyText &&
            closeTranslation == current.Capture.CloseAfterCopyTranslation &&
            pickWindow == current.Capture.AutoSelectWindow &&
            pickElement == current.Capture.AutoSelectElement) return;
        current.AutoTranslateAfterSelection = auto;
        current.SaveScreenshotToDisk = save;
        current.Capture.CloseAfterCopy = close;
        current.Capture.DoubleClickToFinish = doubleClick;
        current.Capture.RightClickToCancel = rightClick;
        current.Capture.CloseAfterCopyText = closeText;
        current.Capture.CloseAfterCopyTranslation = closeTranslation;
        current.Capture.AutoSelectWindow = pickWindow;
        current.Capture.AutoSelectElement = pickElement;
        Save();
        CaptureOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the services the menus offer change. Each open menu rebuilds its list from it,
    /// keeping whatever it has selected.
    /// </summary>
    public event EventHandler? ProviderVisibilityChanged;

    /// <summary>Stores which services the menus leave out.</summary>
    /// <returns>Whether anything changed, and so was saved.</returns>
    public bool UpdateHiddenProviders(IEnumerable<TranslationProvider> hidden)
    {
        var stored = ProviderVisibility.ToStored(Current.HiddenProviders, hidden);
        if (stored.SequenceEqual(Current.HiddenProviders)) return false;
        Current.HiddenProviders = stored;
        Save();
        ProviderVisibilityChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Save()
    {
        if (!_savingEnabled)
        {
            Log.Warn("Settings save skipped because the profile could not be loaded: {0}", _settingsPath);
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);

            // Write-then-rename: losing power mid-write leaves the previous file intact instead of a
            // truncated one, which the next launch would read as corrupt and replace with defaults.
            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, Serialize(Current));
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {0}", _settingsPath);
        }
    }

    /// <returns>The profile text, or null only when no profile exists yet.</returns>
    private static string? ReadSettings(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
