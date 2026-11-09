using System.Drawing;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>取詞翻譯's 簡單顯示 (1.10.0): where the label goes, and its settings.</summary>
public class SelectionLabelPlacementTests
{
    private static readonly Rectangle Area = new(0, 0, 2560, 1552);
    private static readonly Size Label = new(200, 30);
    private const int Gap = 4;

    [Fact]
    public void LabelSitsAboveTheSelectionsTopRightCorner()
    {
        var selection = new Rectangle(400, 500, 300, 40);
        var at = SelectionLabelPlacement.Place(selection, new Point(10, 10), Label, Area, Gap);
        Assert.Equal(new Point(selection.Right + Gap, selection.Top - Gap - Label.Height), at);
    }

    [Fact]
    public void WithoutTheSelectionTheLabelGoesByThePointer()
    {
        var pointer = new Point(800, 600);
        var at = SelectionLabelPlacement.Place(null, pointer, Label, Area, Gap);
        Assert.Equal(new Point(pointer.X + Gap, pointer.Y - Gap - Label.Height), at);
    }

    [Fact]
    public void NoRoomAboveMovesTheLabelBelow()
    {
        var selection = new Rectangle(400, 10, 300, 40);
        var at = SelectionLabelPlacement.Place(selection, default, Label, Area, Gap);
        Assert.Equal(selection.Bottom + Gap, at.Y);
    }

    [Fact]
    public void LabelIsKeptInsideTheWorkingArea()
    {
        var selection = new Rectangle(2450, 1500, 100, 40);
        var at = SelectionLabelPlacement.Place(selection, default, Label, Area, Gap);
        Assert.Equal(Area.Right - Label.Width, at.X);
        Assert.True(at.Y + Label.Height <= Area.Bottom);
    }

    [Fact]
    public void UnionCoversEveryLineAndIgnoresEmptyOnes()
    {
        var lines = new[] { new Rectangle(100, 100, 300, 20), new Rectangle(80, 122, 200, 20), new Rectangle(500, 500, 0, 0) };
        Assert.Equal(Rectangle.FromLTRB(80, 100, 400, 142), SelectionLabelPlacement.Union(lines));
        Assert.Null(SelectionLabelPlacement.Union([new Rectangle(5, 5, 0, 10)]));
    }

    [Fact]
    public void ImplausibleSelectionBoxesAreRejected()
    {
        var desktop = new Rectangle(0, 0, 2560, 1600);
        Assert.True(SelectionLabelPlacement.IsPlausible(new Rectangle(100, 100, 300, 20), desktop));
        Assert.False(SelectionLabelPlacement.IsPlausible(new Rectangle(-5000, -5000, 100, 20), desktop));
        Assert.False(SelectionLabelPlacement.IsPlausible(new Rectangle(0, 0, 2560, 90000), desktop));
    }

    [Fact]
    public void SimpleDisplayIsOffByDefaultAndIsATraySwitch()
    {
        var s = new AppSettings();
        Assert.False(s.QuickLookup.SimpleDisplay);
        Assert.False(s.QuickLookup.SimpleAutoHide);
        Assert.Contains(QuickLookupSettings.DefaultAutoHideSeconds, QuickLookupSettings.AutoHideChoices);

        Assert.Contains(FeatureToggle.QuickLookupSimple, FeatureToggles.All);
        FeatureToggles.Set(s, FeatureToggle.QuickLookupSimple, true);
        Assert.True(s.QuickLookup.SimpleDisplay);
        Assert.True(FeatureToggles.Get(s, FeatureToggle.QuickLookupSimple));
    }
}
