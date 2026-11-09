using System.Drawing;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 自動選取視窗 / 自動選取介面元素 (1.9.0): which window and element a point lands on, and the
/// boxes the wheel steps through.
/// </summary>
public class CaptureAutoSelectTests
{
    [Fact]
    public void BothSwitchesAreOnByDefault()
    {
        var capture = new CaptureSettings();
        Assert.True(capture.AutoSelectWindow);
        Assert.True(capture.AutoSelectElement);
    }

    [Fact]
    public void TopmostWindowContainingThePointWins()
    {
        var front = new Rectangle(100, 100, 200, 200);
        var back = new Rectangle(0, 0, 1000, 800);
        Assert.Equal(0, CaptureHoverRules.TopmostAt([front, back], new Point(150, 150)));
        Assert.Equal(1, CaptureHoverRules.TopmostAt([front, back], new Point(500, 500)));
        Assert.Equal(-1, CaptureHoverRules.TopmostAt([front, back], new Point(1500, 500)));
    }

    // A 2560x1600 monitor with a 48px taskbar along the bottom.
    private static readonly Rectangle MonitorBox = new(0, 0, 2560, 1600);
    private static readonly Rectangle WorkArea = new(0, 0, 2560, 1552);

    [Fact]
    public void WorkAreaIsOfferedAroundAWindowInsideIt()
    {
        var window = new Rectangle(200, 100, 1200, 900);
        Assert.True(CaptureHoverRules.OffersWorkArea(MonitorBox, WorkArea, window, new Point(500, 500)));
        Assert.True(CaptureHoverRules.OffersWorkArea(MonitorBox, WorkArea, null, new Point(500, 500)));
        // A maximised window fills the working area exactly.
        Assert.True(CaptureHoverRules.OffersWorkArea(MonitorBox, WorkArea, WorkArea, new Point(500, 500)));
    }

    [Fact]
    public void WorkAreaIsNotOfferedOnTheTaskbar()
    {
        Assert.False(CaptureHoverRules.OffersWorkArea(MonitorBox, WorkArea, null, new Point(500, 1580)));
    }

    [Fact]
    public void WorkAreaIsNotOfferedUnderAFullScreenWindow()
    {
        // A game or video over the taskbar would be cut down to the working area otherwise.
        Assert.False(CaptureHoverRules.OffersWorkArea(MonitorBox, WorkArea, MonitorBox, new Point(500, 500)));
    }

    [Fact]
    public void WorkAreaIsNotOfferedWithoutATaskbar()
    {
        // Taskbar on another monitor or auto-hidden: the working area is the screen, one box already.
        Assert.False(CaptureHoverRules.OffersWorkArea(MonitorBox, MonitorBox, null, new Point(500, 500)));
    }

    [Fact]
    public void WorkAreaSitsBetweenMonitorAndWindowInTheChain()
    {
        var window = new Rectangle(200, 100, 1200, 900);
        var chain = CaptureHoverRules.Chain([MonitorBox, WorkArea, window], MonitorBox);
        Assert.Equal([MonitorBox, WorkArea, window], chain);
    }

    /// <summary>An element in a made-up tree: its box and its children (null: not read yet).</summary>
    private sealed record El(string Name, Rectangle Box, El[]? Kids = null);

    private static List<string> Deepest(El root, Point p, out bool incomplete) =>
        CaptureHoverRules.DeepestPath(root, p, e => e.Kids, e => e.Box, out incomplete)
            .Select(e => e.Name).ToList();

    [Fact]
    public void SmallestChildContainingThePointIsGoneInto()
    {
        var window = new El("window", new Rectangle(0, 0, 400, 300), [
            new El("panel", new Rectangle(0, 0, 400, 300), []),
            new El("button", new Rectangle(10, 10, 80, 24), []),
            new El("elsewhere", new Rectangle(200, 200, 50, 50), []),
        ]);

        Assert.Equal(["button"], Deepest(window, new Point(20, 20), out var incomplete));
        Assert.False(incomplete);
        Assert.Equal(["panel"], Deepest(window, new Point(150, 150), out _));
        Assert.Empty(Deepest(window, new Point(500, 500), out _));
    }

    [Fact]
    public void AnEmptyLayerAsLargeAsTheContentDoesNotEndTheSearch()
    {
        // Chromium: beside the content, an empty "Intermediate D3D Window" exactly as large, listed first.
        var bounds = new Rectangle(2560, 0, 1920, 1032);
        var window = new El("window", bounds, [
            new El("d3d", bounds, []),
            new El("root", bounds, [
                new El("view", bounds, [
                    new El("empty view", bounds, []),
                    new El("document", bounds, [
                        new El("sidebar", new Rectangle(2560, 0, 288, 1032), [
                            new El("item", new Rectangle(2570, 280, 260, 40), []),
                        ]),
                    ]),
                ]),
            ]),
        ]);

        Assert.Equal(["root", "view", "document", "sidebar", "item"],
            Deepest(window, new Point(2700, 300), out _));
    }

    [Fact]
    public void ChildrenNotReadYetAreReportedAndTheSearchGoesOnWithTheRest()
    {
        var window = new El("window", new Rectangle(0, 0, 400, 300), [
            new El("toolbar", new Rectangle(0, 0, 400, 40), null),
            new El("list", new Rectangle(0, 40, 400, 260), []),
        ]);

        Assert.Equal(["toolbar"], Deepest(window, new Point(20, 20), out var incomplete));
        Assert.True(incomplete);
        Assert.Equal(["list"], Deepest(window, new Point(20, 100), out incomplete));
        Assert.False(incomplete);
    }

    [Fact]
    public void ChainDropsBoxesThatChangeNothing()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var window = new Rectangle(100, 100, 800, 600);
        var client = new Rectangle(101, 101, 799, 599); // the window's own pane, a pixel in
        var button = new Rectangle(120, 140, 80, 24);

        var chain = CaptureHoverRules.Chain([screen, window, client, button], screen);

        Assert.Equal([screen, window, button], chain);
    }

    [Fact]
    public void ChainClipsEachBoxToTheOneOutsideIt()
    {
        var desktop = new Rectangle(0, 0, 1920, 1080);
        var window = new Rectangle(100, 100, 400, 300);
        var rowScrolledHalfOut = new Rectangle(120, 380, 300, 60);

        var chain = CaptureHoverRules.Chain([window, rowScrolledHalfOut], desktop);

        Assert.Equal(Rectangle.FromLTRB(120, 380, 420, 400), chain[^1]);
    }

    [Fact]
    public void ChainSkipsBoxesOutsideOrTooSmall()
    {
        var desktop = new Rectangle(0, 0, 1920, 1080);
        var window = new Rectangle(100, 100, 400, 300);
        var outside = new Rectangle(1000, 1000, 50, 50);
        var line = new Rectangle(120, 120, 300, 1);

        var chain = CaptureHoverRules.Chain([window, outside, line], desktop);

        Assert.Equal([window], chain);
    }
}
