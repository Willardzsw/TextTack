using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>追加翻譯's shortcut and 替換翻譯's own service choice.</summary>
public class QuickAppendTests
{
    [Fact]
    public void AppendIsOffAndUnboundByDefault()
    {
        var settings = new AppSettings();
        var binding = HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.QuickAppend);

        Assert.False(binding.Enabled);
        Assert.False(binding.IsActive);
        Assert.True(HotkeyBindings.IsUnset(binding.Trigger));
        Assert.Equal("", settings.QuickAppendHotkeyDisplay);
    }

    [Fact]
    public void SwitchedOnButUnsetClaimsNothing()
    {
        var settings = new AppSettings { QuickAppendHotkeyEnabled = true };

        var resolved = HotkeyBindings.Resolve(settings);

        Assert.All(resolved, b => Assert.Null(b.ShadowedBy));
        Assert.True(HotkeyBindings.IsUnset(HotkeyBindings.TriggerFor(settings, HotkeyAction.QuickAppend)));
        Assert.False(HotkeyBindings.IsBindable(HotkeyBindings.TriggerFor(settings, HotkeyAction.QuickAppend)));
    }

    [Fact]
    public void ABoundAppendShortcutIsActiveAndLosesToReplace()
    {
        var settings = new AppSettings
        {
            QuickAppendHotkeyEnabled = true,
            QuickAppendHotkeyModifiers = 3,
            QuickAppendHotkeyVirtualKey = 0x52, // Ctrl+Alt+R
            QuickAppendHotkeyDisplay = "Ctrl+Alt+R",
        };
        Assert.True(HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.QuickAppend).IsActive);

        // The same combination as 替換翻譯 (Ctrl+Alt+E): the older shortcut keeps it.
        settings.QuickAppendHotkeyVirtualKey = 0x45;
        var append = HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.QuickAppend);
        Assert.Equal(HotkeyAction.QuickTranslate, append.ShadowedBy);
    }

    [Fact]
    public void AppendSwitchIsOfferedInTheTrayMenu()
    {
        var settings = new AppSettings();
        Assert.Contains(FeatureToggle.QuickAppendHotkey, FeatureToggles.All);

        FeatureToggles.Set(settings, FeatureToggle.QuickAppendHotkey, true);
        Assert.True(settings.QuickAppendHotkeyEnabled);
        Assert.True(FeatureToggles.Get(settings, FeatureToggle.QuickAppendHotkey));
    }

    [Fact]
    public void ReplaceProviderFollowsGlobalByDefaultAndRoundTrips()
    {
        Assert.Null(new AppSettings().QuickTranslate.Provider);

        var settings = new AppSettings();
        settings.QuickTranslate.Provider = TranslationProvider.DeepL;
        settings.QuickAppendHotkeyEnabled = true;

        var json = SettingsService.Serialize(settings);
        var back = SettingsService.Parse(json);

        Assert.Equal(TranslationProvider.DeepL, back.QuickTranslate.Provider);
        Assert.True(back.QuickAppendHotkeyEnabled);
    }

    [Fact]
    public void ARetiredReplaceProviderIsReadAsItsReplacement()
    {
        var json = "{\"QuickTranslate\":{\"Provider\":" + (int)TranslationProvider.Google2 + "}}";
        var back = SettingsService.Parse(json);

        Assert.Equal(TranslationProvider.Google, back.QuickTranslate.Provider);
    }
}
