using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// How the user's API keys are kept in the settings file: encrypted with Windows DPAPI for the
/// current Windows user, never as the key itself.
/// </summary>
/// <remarks>
/// <para>What this stops is the key being read out of the file — opened in Notepad, or the profile
/// copied to another computer or account, where it cannot be decrypted at all. It does not stop a
/// program running as the same user on the same computer: DPAPI decrypts for whoever is logged
/// in, which is exactly how this application reads the key back. Nothing on the user's own
/// computer can keep a key from someone who controls that computer; this is about casual
/// copying.</para>
///
/// <para>A stored value is <see cref="Prefix"/> followed by base64. A value without the prefix is
/// a key from before this, read as it is and written back encrypted on the next save. A value
/// that will not decrypt — the profile came from another user or computer — reads as no key, so
/// the service asks for one again rather than sending gibberish.</para>
/// </remarks>
public static class SecretStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>What marks a stored value as encrypted.</summary>
    internal const string Prefix = "dpapi:";

    // Ties the ciphertext to this purpose: another program's DPAPI blob is not one of ours.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TextTack.ApiKey.v1");

    /// <summary>The settings that hold a key the user typed in, by their name in the file.</summary>
    /// <remarks>
    /// Not GoogleChromeApiKey: that is the public key Google's own page hands every visitor, read
    /// by the application rather than typed by the user.
    /// </remarks>
    internal static readonly IReadOnlyList<string> KeyProperties =
    [
        nameof(AppSettings.ApiKey),
        nameof(AppSettings.OpenAiApiKey),
        nameof(AppSettings.DeepSeekApiKey),
        nameof(AppSettings.CloudApiKey),
    ];

    /// <summary>The stored form of <paramref name="plain"/>: encrypted, or empty for no key.</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// The key a stored value stands for: decrypted, passed through when it is a key from before
    /// encryption, or empty when it cannot be decrypted here.
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!IsProtected(stored)) return stored;

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Never the value: only that a key had to be dropped.
            Log.Warn("An API key in the settings could not be decrypted for this Windows user ({0}); it reads as empty", ex.GetType().Name);
            return "";
        }
    }

    /// <summary>Whether a stored value is in the encrypted form.</summary>
    public static bool IsProtected(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Replaces every key in <paramref name="settings"/>'s JSON with its stored form.</summary>
    internal static void ProtectAll(JsonObject settings)
    {
        foreach (var name in KeyProperties)
            if (settings[name] is JsonValue value && value.TryGetValue<string>(out var text))
                settings[name] = IsProtected(text) ? text : Protect(text);
    }

    /// <summary>Puts the readable keys back into <paramref name="settings"/> after it was read.</summary>
    internal static void UnprotectAll(AppSettings settings)
    {
        settings.ApiKey = Unprotect(settings.ApiKey);
        settings.OpenAiApiKey = Unprotect(settings.OpenAiApiKey);
        settings.DeepSeekApiKey = Unprotect(settings.DeepSeekApiKey);
        settings.CloudApiKey = Unprotect(settings.CloudApiKey);
    }

    /// <summary>Whether a settings document still holds a key in the readable form.</summary>
    internal static bool HasPlainKey(JsonObject settings) =>
        KeyProperties.Any(name =>
            settings[name] is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            text.Length > 0 &&
            !IsProtected(text));

    /// <summary>
    /// Encrypts the keys in the copies of the settings kept from before each update, which were
    /// written as the file was then — the keys readable.
    /// </summary>
    /// <remarks>
    /// Only the key values change; everything else in a copy is left as it was, so it still
    /// restores what it was kept for. A copy that is not readable JSON is left alone.
    /// </remarks>
    internal static void ProtectBackups(string directory)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "appsettings.before-*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root || !HasPlainKey(root)) continue;

                ProtectAll(root);
                var temp = file + ".tmp";
                File.WriteAllText(temp, root.ToJsonString(SettingsService.FileWriteOptions));
                File.Move(temp, file, overwrite: true);
                Log.Info("API keys encrypted in settings backup {0}", Path.GetFileName(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log.Warn("Could not encrypt the API keys in {0} ({1})", Path.GetFileName(file), ex.GetType().Name);
            }
        }
    }
}
