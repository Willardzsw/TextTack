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

    [Fact]
    public void SmallestChildContainingThePointIsGoneInto()
    {
        var panel = new Rectangle(0, 0, 400, 300);
        var button = new Rectangle(10, 10, 80, 24);
        var elsewhere = new Rectangle(200, 200, 50, 50);
        Assert.Equal(1, CaptureHoverRules.PickChild([panel, button, elsewhere], new Point(20, 20)));
        Assert.Equal(0, CaptureHoverRules.PickChild([panel, button, elsewhere], new Point(150, 150)));
        Assert.Equal(-1, CaptureHoverRules.PickChild([button], new Point(300, 300)));
        Assert.Equal(-1, CaptureHoverRules.PickChild([Rectangle.Empty], new Point(0, 0)));
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
