using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>鼠標增強: defaults, the settings file, and the pure decisions the hook relies on.</summary>
public class MouseEnhanceTests
{
    [Fact]
    public void Defaults_CtrlAltWheelFades_AltWheelScrolls_AltRightDragResizes()
    {
        var s = new AppSettings().MouseEnhance;

        Assert.True(s.TransparencyEnabled);
        Assert.True(s.HorizontalScrollEnabled);
        Assert.True(s.ResizeEnabled);
        Assert.Equal(MouseWheelAction.Transparency, MouseEnhanceRules.ForWheel(s, MouseModifierKeys.Ctrl | MouseModifierKeys.Alt));
        Assert.Equal(MouseWheelAction.HorizontalScroll, MouseEnhanceRules.ForWheel(s, MouseModifierKeys.Alt));
        Assert.Equal(MouseWheelAction.None, MouseEnhanceRules.ForWheel(s, MouseModifierKeys.Shift));
        Assert.True(MouseEnhanceRules.StartsResize(s, MouseModifierKeys.Alt, MouseResizeButton.Right));
    }

    [Fact]
    public void Modifiers_MatchExactly()
    {
        var s = new MouseEnhanceSettings();

        Assert.Equal(MouseWheelAction.None, MouseEnhanceRules.ForWheel(s, MouseModifierKeys.None));
        Assert.Equal(MouseWheelAction.None, MouseEnhanceRules.ForWheel(s, MouseModifierKeys.Ctrl));
        Assert.False(MouseEnhanceRules.StartsResize(s, MouseModifierKeys.Alt | MouseModifierKeys.Shift, MouseResizeButton.Right));
        Assert.False(MouseEnhanceRules.StartsResize(s, MouseModifierKeys.Alt, MouseResizeButton.Left));
    }

    [Fact]
    public void SwitchedOff_DoesNothing_AndSharedKeysPreferTransparency()
    {
        var s = new MouseEnhanceSettings
        {
            TransparencyModifier = MouseModifierKeys.Alt,
            HorizontalScrollModifier = MouseModifierKeys.Alt,
            ResizeEnabled = false,
        };

        Assert.Equal(MouseWheelAction.Transparency, MouseEnhanceRules.ForWheel(s, MouseModifierKeys.Alt));
        s.TransparencyEnabled = false;
        Assert.Equal(MouseWheelAction.HorizontalScroll, MouseEnhanceRules.ForWheel(s, MouseModifierKeys.Alt));
        Assert.False(MouseEnhanceRules.StartsResize(s, MouseModifierKeys.Alt, MouseResizeButton.Right));
    }

    [Theory]
    [InlineData(255, -1, 10, 20, 230)]
    [InlineData(255, 1, 10, 20, 255)]
    [InlineData(64, -5, 10, 20, 51)]
    [InlineData(51, 1, 10, 20, 76)]
    public void NextAlpha_StepsByPercent_WithinMinimumAndSolid(byte current, int notches, int step, int min, byte expected)
    {
        Assert.Equal(expected, MouseEnhanceRules.NextAlpha(current, notches, step, min));
    }

    [Theory]
    [InlineData(10, 10, ResizeEdges.Left | ResizeEdges.Top)]
    [InlineData(290, 190, ResizeEdges.Right | ResizeEdges.Bottom)]
    [InlineData(150, 10, ResizeEdges.Top)]
    [InlineData(10, 100, ResizeEdges.Left)]
    [InlineData(150, 100, ResizeEdges.Right | ResizeEdges.Bottom)]
    public void EdgesFor_UsesThirdsOfTheWindow(int x, int y, ResizeEdges expected)
    {
        Assert.Equal(expected, MouseEnhanceRules.EdgesFor(0, 0, 300, 200, x, y));
    }

    [Fact]
    public void Resize_MovesOnlyChosenEdges_AndKeepsMinimum()
    {
        var start = (0, 0, 300, 200);

        Assert.Equal((-20, 0, 300, 200), MouseEnhanceRules.Resize(start, ResizeEdges.Left, -20, 50, 100, 50));
        Assert.Equal((0, 0, 340, 230), MouseEnhanceRules.Resize(start, ResizeEdges.Right | ResizeEdges.Bottom, 40, 30, 100, 50));
        Assert.Equal((200, 0, 300, 200), MouseEnhanceRules.Resize(start, ResizeEdges.Left, 500, 0, 100, 50));
        Assert.Equal((0, 0, 300, 50), MouseEnhanceRules.Resize(start, ResizeEdges.Bottom, 0, -500, 100, 50));
    }

    [Fact]
    public void SettingsFile_RoundTripsChoices_AndOlderFilesGetDefaults()
    {
        var settings = new AppSettings();
        settings.MouseEnhance.TransparencyModifier = MouseModifierKeys.Ctrl | MouseModifierKeys.Shift;
        settings.MouseEnhance.ResizeButton = MouseResizeButton.Middle;
        settings.MouseEnhance.HorizontalScrollEnabled = false;

        var json = SettingsService.Serialize(settings);
        var back = SettingsService.Parse(json).MouseEnhance;

        Assert.Equal(MouseModifierKeys.Ctrl | MouseModifierKeys.Shift, back.TransparencyModifier);
        Assert.Equal(MouseResizeButton.Middle, back.ResizeButton);
        Assert.False(back.HorizontalScrollEnabled);
        Assert.Contains("\"Middle\"", json);

        var older = SettingsService.Parse("""{"Provider":"Google"}""").MouseEnhance;
        Assert.True(older.ResizeEnabled);
        Assert.Equal(MouseModifierKeys.Ctrl | MouseModifierKeys.Alt, older.TransparencyModifier);
        Assert.Equal(MouseModifierKeys.Alt, older.HorizontalScrollModifier);
    }

    [Fact]
    public void Display_NamesKeysInOrder()
    {
        Assert.Equal("Ctrl+Alt", MouseEnhanceRules.Display(MouseModifierKeys.Alt | MouseModifierKeys.Ctrl));
        Assert.Equal("Shift+Alt", MouseEnhanceRules.Display(MouseModifierKeys.Shift | MouseModifierKeys.Alt));
        Assert.DoesNotContain(MouseModifierKeys.None, MouseEnhanceRules.Choices);
    }
    [Fact]
    public void SidewaysScrollSendsHalfOfEachTurn()
    {
        int remainder = 0;
        Assert.Equal(-60, MouseEnhanceRules.ScaleHorizontal(ref remainder, -120));
        Assert.Equal(60, MouseEnhanceRules.ScaleHorizontal(ref remainder, 120));

        // A touchpad's small pieces add up rather than rounding to nothing.
        remainder = 0;
        int sent = 0;
        for (int i = 0; i < 30; i++) sent += MouseEnhanceRules.ScaleHorizontal(ref remainder, 3);
        Assert.Equal(45, sent);
    }
}
