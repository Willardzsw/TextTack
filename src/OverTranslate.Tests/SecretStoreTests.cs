using System.IO;
using System.Text.Json.Nodes;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Controls;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// API keys are written to the settings file encrypted and read back as they were typed. Every key
/// here is synthetic, and every file is in a fresh temporary folder.
/// </summary>
public class SecretStoreTests
{
    private const string Key = "sk-synthetic-0123456789abcdef";

    [Fact]
    public void Protect_HidesTheKeyAndUnprotectGivesItBack()
    {
        var stored = SecretStore.Protect(Key);

        Assert.StartsWith(SecretStore.Prefix, stored);
        Assert.DoesNotContain("synthetic", stored);
        Assert.Equal(Key, SecretStore.Unprotect(stored));
    }

    [Fact]
    public void Protect_LeavesNoKeyAsEmpty()
    {
        Assert.Equal("", SecretStore.Protect(""));
        Assert.Equal("", SecretStore.Unprotect(""));
    }

    [Fact]
    public void Unprotect_ReadsAKeyFromBeforeEncryptionAsItIs()
    {
        Assert.Equal(Key, SecretStore.Unprotect(Key));
    }

    [Fact]
    public void Unprotect_ReadsWhatWillNotDecryptAsNoKey()
    {
        Assert.Equal("", SecretStore.Unprotect(SecretStore.Prefix + "AAAAAAAAAAAAAAAA"));
        Assert.Equal("", SecretStore.Unprotect(SecretStore.Prefix + "not base64!"));
    }

    [Fact]
    public void SettingsFile_HoldsEveryKeyEncrypted_AndReadsThemBack()
    {
        var settings = new AppSettings
        {
            ApiKey = Key + "-deepl",
            OpenAiApiKey = Key + "-openai",
            DeepSeekApiKey = Key + "-deepseek",
        };

        var json = SettingsService.Serialize(settings);

        Assert.DoesNotContain("synthetic", json);
        var read = SettingsService.Parse(json);
        Assert.Equal(Key + "-deepl", read.ApiKey);
        Assert.Equal(Key + "-openai", read.OpenAiApiKey);
        Assert.Equal(Key + "-deepseek", read.DeepSeekApiKey);

        // Serializing does not touch the settings in memory.
        Assert.Equal(Key + "-deepseek", settings.DeepSeekApiKey);
    }

    [Fact]
    public void Load_WritesAReadableKeyBackEncryptedAtOnce()
    {
        var folder = TempFolder();
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            File.WriteAllText(path, $$"""{"LastRunVersion":"9.9.9","DeepSeekApiKey":"{{Key}}"}""");

            AppSettings? saved = null;
            var settings = SettingsService.Load(path, "9.9.9", s => saved = s);

            Assert.Equal(Key, settings.DeepSeekApiKey);
            Assert.Same(settings, saved);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_LeavesAFileThatIsAlreadyEncryptedAlone()
    {
        var folder = TempFolder();
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            File.WriteAllText(path, $$"""{"LastRunVersion":"9.9.9","DeepSeekApiKey":"{{SecretStore.Protect(Key)}}"}""");

            var savedAgain = false;
            var settings = SettingsService.Load(path, "9.9.9", _ => savedAgain = true);

            Assert.Equal(Key, settings.DeepSeekApiKey);
            Assert.False(savedAgain);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ProtectBackups_EncryptsOnlyTheKeysInTheCopies()
    {
        var folder = TempFolder();
        try
        {
            var backup = Path.Combine(folder, "appsettings.before-9.9.9.json");
            File.WriteAllText(backup, $$"""{"ApiKey":"{{Key}}","OpenAiApiKey":"","TargetLanguage":"TH"}""");
            var other = Path.Combine(folder, "other.json");
            File.WriteAllText(other, $$"""{"ApiKey":"{{Key}}"}""");

            SecretStore.ProtectBackups(folder);

            var root = JsonNode.Parse(File.ReadAllText(backup))!.AsObject();
            Assert.True(SecretStore.IsProtected(root["ApiKey"]!.GetValue<string>()));
            Assert.Equal(Key, SecretStore.Unprotect(root["ApiKey"]!.GetValue<string>()));
            Assert.Equal("", root["OpenAiApiKey"]!.GetValue<string>());
            Assert.Equal("TH", root["TargetLanguage"]!.GetValue<string>());

            // Only the update copies: anything else in the folder is not ours to rewrite.
            Assert.Contains("synthetic", File.ReadAllText(other));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("sk-0123456789abcdef3f2a", "sk-••••••••3f2a")]
    [InlineData("short-key", "••••••••")]
    public void SecretBox_ShowsASavedKeyOnlyMasked(string secret, string shown)
    {
        Assert.Equal(shown, SecretBox.Mask(secret));
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TextTack-SecretStoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
