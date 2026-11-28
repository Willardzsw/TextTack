using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>總開關: stands every shortcut and the mouse tools down, greys the tray icon.</summary>
public class MasterSwitchTests
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        [StringsParityTests.ProjectDirectory(), .. parts]));

    [Fact]
    public void It_is_on_by_default()
    {
        Assert.True(new AppSettings().MasterEnabled);
    }

    [Fact]
    public void Turning_it_off_leaves_each_features_own_switch_alone()
    {
        var s = new AppSettings { MasterEnabled = false };

        Assert.All(FeatureToggles.All.Where(t => t != FeatureToggle.QuickLookupSimple && t != FeatureToggle.QuickAppendHotkey),
            t => Assert.True(FeatureToggles.Get(s, t)));
    }

    [Fact]
    public void Registration_and_the_mouse_tools_both_check_it()
    {
        Assert.Contains("if (!settings.MasterEnabled)", Source("Views", "MainWindow.xaml.cs"));
        Assert.Contains("Current.MasterEnabled && MouseEnhanceRules.IsActive", Source("Services", "MouseEnhanceService.cs"));
    }

    /// <summary>
    /// The menu's own item, not one of the switches 設定 can hide: with everything off it is the way back.
    /// </summary>
    [Fact]
    public void The_tray_menu_always_has_it_outside_the_hideable_switches()
    {
        var xaml = Source("Views", "Shell", "TrayMenuWindow.xaml");
        int master = xaml.IndexOf("x:Name=\"MasterBtn\"", StringComparison.Ordinal);
        int group = xaml.IndexOf("x:Name=\"TogglesGroup\"", StringComparison.Ordinal);

        Assert.True(master >= 0 && master < group);
        Assert.DoesNotContain(FeatureToggles.All, t => t.ToString().Contains("Master"));
    }

    [Fact]
    public void The_paused_tray_icon_is_grey()
    {
        using var icon = AppIconService.CreateDisabledTrayIcon();
        using var bmp = icon.ToBitmap();

        bool anyVisible = false;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.A < 16) continue;
            anyVisible = true;
            Assert.True(Math.Abs(c.R - c.G) <= 2 && Math.Abs(c.G - c.B) <= 2, $"pixel {x},{y} is {c}");
        }
        Assert.True(anyVisible);
    }
}
