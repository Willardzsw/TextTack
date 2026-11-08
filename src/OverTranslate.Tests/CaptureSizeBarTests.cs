using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using OverTranslate.Models;
using OverTranslate.Views.Capture;
using Xunit;
using WPoint = System.Windows.Point;

namespace OverTranslate.Tests;

/// <summary>
/// The capture's size bar and what came with it (1.8.0): its remembered options and presets, the
/// locked aspect ratio, and the frame drawn without the costs that made dragging stutter.
/// </summary>
public class CaptureSizeBarTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument CaptureXaml() => XDocument.Load(Path.Combine(
        StringsParityTests.ProjectDirectory(), "Views", "Capture", "ScreenCaptureWindow.xaml"));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void OptionsDefaultToPxSquareUnlockedWithFourPresets()
    {
        var capture = new CaptureSettings();
        Assert.False(capture.SizeInDip);
        Assert.False(capture.RoundedCorners);
        Assert.False(capture.LockAspectRatio);
        Assert.Equal(new[] { "100x100", "500x500", "640x480", "1024x768" }, capture.SizePresets);
    }

    [Fact]
    public void OptionsAndPresetsRoundTripWithoutDuplicating()
    {
        var capture = new CaptureSettings
        {
            SizeInDip = true,
            RoundedCorners = true,
            LockAspectRatio = true,
            SizePresets = ["800x600", "320x240"],
        };
        var back = JsonSerializer.Deserialize<CaptureSettings>(JsonSerializer.Serialize(capture))!;
        Assert.True(back.SizeInDip);
        Assert.True(back.RoundedCorners);
        Assert.True(back.LockAspectRatio);
        Assert.Equal(new[] { "800x600", "320x240" }, back.SizePresets);
    }

    [Theory]
    [InlineData("640x480", 640, 480)]
    [InlineData(" 640 × 480 ", 640, 480)]
    [InlineData("640*480", 640, 480)]
    [InlineData("640,480", 640, 480)]
    [InlineData("640 480", 640, 480)]
    [InlineData("1024X768", 1024, 768)]
    public void PresetsReadInTheUsualSpellings(string text, int w, int h) =>
        Assert.Equal((w, h), CaptureRegionDialog.ParsePreset(text));

    [Theory]
    [InlineData("")]
    [InlineData("640")]
    [InlineData("0x480")]
    [InlineData("abc")]
    [InlineData("640x480x2")]
    public void NonSizesAreNotPresets(string text) => Assert.Null(CaptureRegionDialog.ParsePreset(text));

    [Fact]
    public void EditedPresetListKeepsSizesOnceAndDropsTheRest() =>
        Assert.Equal(new[] { "640x480", "100x100" },
            CaptureRegionDialog.ParsePresetList("640 × 480\r\nnonsense\r\n\r\n100x100\r\n640x480"));

    [Fact]
    public void LockedCornerKeepsTheRatioFollowingTheLongerMove()
    {
        var fixedPoint = new WPoint(100, 100);
        // Moved 200 across and 50 down with a 2:1 box: the width leads, height follows.
        var p = ScreenCaptureWindow.KeepAspect(fixedPoint, new WPoint(300, 150), 2, 2000, 2000);
        Assert.Equal(new WPoint(300, 200), p);
        // Up and to the left the same way.
        p = ScreenCaptureWindow.KeepAspect(fixedPoint, new WPoint(90, 0), 2, 2000, 2000);
        // 10 left and 100 up: the height leads, the width follows until the left edge stops it.
        Assert.Equal(new WPoint(0, 50), p);
    }

    [Fact]
    public void LockedCornerShrinksBackToStayOnTheDesktop()
    {
        // 2:1 from (100,100) towards the bottom-right of a 500x300 desktop: height runs out first.
        var p = ScreenCaptureWindow.KeepAspect(new WPoint(100, 100), new WPoint(1000, 900), 2, 500, 300);
        Assert.Equal(new WPoint(500, 300), p);
        Assert.True(p.X <= 500 && p.Y <= 300);
        Assert.Equal(2, (p.X - 100) / (p.Y - 100), 6);
    }

    [Fact]
    public void FrameCarriesNoEffectAndNoDashes()
    {
        var doc = CaptureXaml();
        // Re-blurring a shadow over the whole box on every move was most of a frame's time.
        Assert.Empty(Named(doc, "SelectionRect").Descendants(Wpf + "DropShadowEffect"));
        Assert.Null(Named(doc, "SelectionRect").Attribute("StrokeDashArray"));
        var code = File.ReadAllText(Path.Combine(
            StringsParityTests.ProjectDirectory(), "Views", "Capture", "ScreenCaptureWindow.xaml.cs"));
        Assert.DoesNotContain("StrokeDashArray", code);
    }

    [Fact]
    public void DimIsOneStillLayerAndTheBoxIsLaidBackOverIt()
    {
        var doc = CaptureXaml();
        // No hole cut per move: a full-screen shape changing every frame repainted the whole desktop.
        Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute(X + "Name") == "DimPath");
        Assert.Equal("Rectangle", Named(doc, "DimLayer").Name.LocalName);
        Assert.Equal("Rectangle", Named(doc, "SelectionBright").Name.LocalName);
    }

    [Fact]
    public void SizeBarButtonsAreAllThere()
    {
        var doc = CaptureXaml();
        foreach (var name in new[] { "SizeBtn", "UnitPxBtn", "UnitDipBtn", "CornerBtn", "AspectBtn", "RefreshBtn" })
            Assert.Equal("Button", Named(doc, name).Name.LocalName);
    }
}
