using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>The tray menu's feature switches and which of them it shows.</summary>
public class FeatureTogglesTests
{
    [Fact]
    public void EverySwitch_ReadsAndWritesItsOwnSetting()
    {
        foreach (var toggle in FeatureToggles.All)
        {
            // Every switch on first: 追加翻譯 is the one that starts off.
            var s = new AppSettings();
            foreach (var each in FeatureToggles.All) FeatureToggles.Set(s, each, true);
            Assert.True(FeatureToggles.Get(s, toggle));

            FeatureToggles.Set(s, toggle, false);
            Assert.False(FeatureToggles.Get(s, toggle));
            Assert.All(FeatureToggles.All.Where(t => t != toggle), other => Assert.True(FeatureToggles.Get(s, other)));
        }
    }

    [Fact]
    public void AllSwitchesAreListedOnce_WithAName()
    {
        Assert.Equal(FeatureToggles.All.Distinct(), FeatureToggles.All);
        Assert.Contains(FeatureToggle.MouseEnhance, FeatureToggles.All);
        Assert.False(FeatureToggles.Get(new AppSettings(), FeatureToggle.QuickAppendHotkey));
        Assert.DoesNotContain(FeatureToggle.MouseResize, FeatureToggles.All);
        Assert.All(FeatureToggles.All, t => Assert.StartsWith("S.Settings.", FeatureToggles.NameKey(t)));
    }

    [Fact]
    public void Menu_ShowsEverythingByDefault_AndLeavesOutHidden()
    {
        var s = new AppSettings();
        Assert.Equal(FeatureToggles.All, FeatureToggles.Shown(s));

        s.TrayMenu.HiddenToggles.Add(FeatureToggle.PinHotkey);
        Assert.DoesNotContain(FeatureToggle.PinHotkey, FeatureToggles.Shown(s));
        Assert.Equal(FeatureToggles.All.Length - 1, FeatureToggles.Shown(s).Count());
    }

    [Fact]
    public void HiddenSwitches_SurviveTheSettingsFile()
    {
        var s = new AppSettings();
        s.TrayMenu.HiddenToggles.Add(FeatureToggle.MouseResize);
        s.CaptureHotkeyEnabled = false;

        var json = SettingsService.Serialize(s);
        var back = SettingsService.Parse(json);

        Assert.Equal([FeatureToggle.MouseResize], back.TrayMenu.HiddenToggles);
        Assert.True(back.MouseEnhance.Enabled);
        Assert.False(back.CaptureHotkeyEnabled);
        Assert.Contains("\"MouseResize\"", json);
    }

    [Fact]
    public void MouseMasterSwitch_TurnsEverythingOff_WithoutLosingTheToolSwitches()
    {
        var s = new AppSettings();
        FeatureToggles.Set(s, FeatureToggle.MouseEnhance, false);

        Assert.False(MouseEnhanceRules.IsActive(s.MouseEnhance));
        Assert.True(s.MouseEnhance.ResizeEnabled);
        Assert.Equal(MouseWheelAction.None, MouseEnhanceRules.ForWheel(s.MouseEnhance, MouseModifierKeys.Alt));
        Assert.False(MouseEnhanceRules.StartsResize(s.MouseEnhance, MouseModifierKeys.Alt, MouseResizeButton.Right));
    }

    [Fact]
    public void CaptureSwitchedOff_LeavesItsTriggerToOthers()
    {
        var s = new AppSettings { CaptureHotkeyEnabled = false };

        var capture = HotkeyBindings.Resolve(s).Single(b => b.Action == HotkeyAction.Capture);
        Assert.False(capture.IsActive);
    }
}
