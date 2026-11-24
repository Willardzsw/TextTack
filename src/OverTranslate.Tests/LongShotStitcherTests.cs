using OverTranslate.Services.LongShot;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 長截圖 stitching on a synthetic page: every row a different pattern, read through a window
/// that scrolls, with a fixed header and footer drawn over it as a real page would have them.
/// </summary>
public class LongShotStitcherTests
{
    private const int W = 240, H = 100, PageH = 600, Header = 12, Footer = 10;

    /// <summary>A page row: a pseudo-random pattern unique to the row, on a white background.</summary>
    private static void PageRow(byte[] buffer, int offset, int pageRow)
    {
        var rng = new Random(pageRow * 7919 + 17);
        for (int x = 0; x < W; x++)
        {
            byte v = (byte)(x % 9 == 0 ? 255 : rng.Next(256));
            buffer[offset + x * 4] = v;
            buffer[offset + x * 4 + 1] = (byte)(v ^ 0x5A);
            buffer[offset + x * 4 + 2] = (byte)(pageRow & 0xFF);
            buffer[offset + x * 4 + 3] = 255;
        }
    }

    private static void FixedRow(byte[] buffer, int offset, byte shade, int row)
    {
        for (int x = 0; x < W; x++)
        {
            byte v = (byte)(shade + (x * 3 + row * 5) % 40);
            buffer[offset + x * 4] = v;
            buffer[offset + x * 4 + 1] = v;
            buffer[offset + x * 4 + 2] = (byte)(v / 2);
            buffer[offset + x * 4 + 3] = 255;
        }
    }

    private static byte[] Frame(int scroll, bool chrome)
    {
        var frame = new byte[W * H * 4];
        for (int r = 0; r < H; r++)
        {
            int offset = r * W * 4;
            if (chrome && r < Header) FixedRow(frame, offset, 20, r);
            else if (chrome && r >= H - Footer) FixedRow(frame, offset, 120, r);
            else PageRow(frame, offset, scroll + r);
        }
        return frame;
    }

    private static void AssertRowIsPage(byte[] pixels, int row, int pageRow)
    {
        var expected = new byte[W * 4];
        PageRow(expected, 0, pageRow);
        Assert.Equal(expected, pixels.AsSpan(row * W * 4, W * 4).ToArray());
    }

    [Fact]
    public void ScrollingDownStitchesThePage()
    {
        var stitcher = new LongShotStitcher(W, H);
        foreach (int scroll in new[] { 0, 30, 30, 75, 140, 200, 260, 330, 400, 470 })
            Assert.NotEqual(StitchResult.Lost, stitcher.Add(Frame(scroll, chrome: false)));

        var (pixels, _, height) = stitcher.Finish();
        Assert.Equal(570, height);
        for (int row = 0; row < height; row += 7) AssertRowIsPage(pixels, row, row);
    }

    [Fact]
    public void FixedHeaderOnceAndFooterOnceAtTheEnd()
    {
        var stitcher = new LongShotStitcher(W, H);
        foreach (int scroll in new[] { 0, 40, 90, 150, 210 })
            Assert.NotEqual(StitchResult.Lost, stitcher.Add(Frame(scroll, chrome: true)));

        var (pixels, _, height) = stitcher.Finish();

        // Header (12) + the page from row 12 to the bottom of the last view above the footer
        // (210 + 90 = 300) + footer (10).
        Assert.Equal(Header + (210 + H - Footer - Header) + Footer, height);
        for (int row = Header; row < height - Footer; row += 5) AssertRowIsPage(pixels, row, row);
    }

    [Fact]
    public void TooFarIsLostThenFitsAgain()
    {
        var stitcher = new LongShotStitcher(W, H);
        Assert.Equal(StitchResult.Placed, stitcher.Add(Frame(0, false)));
        Assert.Equal(StitchResult.Lost, stitcher.Add(Frame(300, false)));
        Assert.Equal(StitchResult.Placed, stitcher.Add(Frame(60, false)));
        Assert.Equal(160, stitcher.Height);
    }

    [Fact]
    public void StopsAtTheMaximum()
    {
        var stitcher = new LongShotStitcher(W, H, maxSize: 250);
        stitcher.Add(Frame(0, false));
        stitcher.Add(Frame(80, false));
        Assert.Equal(StitchResult.Full, stitcher.Add(Frame(160, false)));
        Assert.Equal(250, stitcher.Height);
    }
    // ---- blank stretches ----

    private const int GapFrom = 150, GapTo = 450;

    /// <summary>The page with rows 150 to 449 left blank (white), as between two sections of a document.</summary>
    private static byte[] GapFrame(int scroll)
    {
        var frame = new byte[W * H * 4];
        for (int r = 0; r < H; r++)
        {
            int pageRow = scroll + r, offset = r * W * 4;
            if (pageRow >= GapFrom && pageRow < GapTo) Array.Fill(frame, (byte)255, offset, W * 4);
            else PageRow(frame, offset, pageRow);
        }
        return frame;
    }

    [Fact]
    public void BlankTallerThanTheRegionIsBridgedAndShortened()
    {
        var stitcher = new LongShotStitcher(W, H);
        foreach (int scroll in new[] { 0, 40, 80, 120, 160, 220, 300, 380, 420, 460, 500 })
            Assert.NotEqual(StitchResult.Lost, stitcher.Add(GapFrame(scroll)));

        var (pixels, _, height) = stitcher.Finish();

        // Rows 0-149 of content, 24 rows of the blank kept, then the content from row 450 on.
        const int kept = 24;
        Assert.Equal(GapFrom + kept + (600 - GapTo), height);
        for (int row = 0; row < GapFrom; row += 5) AssertRowIsPage(pixels, row, row);
        Assert.All(pixels.AsSpan(GapFrom * W * 4, kept * W * 4).ToArray(), b => Assert.Equal(255, b));
        for (int row = GapFrom + kept; row < height; row += 5)
            AssertRowIsPage(pixels, row, row - GapFrom - kept + GapTo);
    }

    [Fact]
    public void FastScrollOverContentIsStillLost()
    {
        // The last frame ends in content, so a jump is not a blank stretch and must not be bridged.
        var stitcher = new LongShotStitcher(W, H);
        Assert.Equal(StitchResult.Placed, stitcher.Add(GapFrame(0)));
        Assert.Equal(StitchResult.Lost, stitcher.Add(GapFrame(460)));
    }

    [Fact]
    public void ACaretInABlankPageIsNotLost()
    {
        var frame = new byte[W * H * 4];
        Array.Fill(frame, (byte)255);
        for (int r = 0; r < 3; r++) PageRow(frame, r * W * 4, r);

        var stitcher = new LongShotStitcher(W, H);
        Assert.Equal(StitchResult.Placed, stitcher.Add(frame));

        // A caret two rows tall blinks on in the blank part.
        var blink = (byte[])frame.Clone();
        for (int r = 50; r < 52; r++)
            for (int x = 120; x < 122; x++) blink[(r * W + x) * 4] = 0;
        Assert.Equal(StitchResult.Unchanged, stitcher.Add(blink));
    }
    // ---- four directions, on a page wider and taller than the region ----

    private const int PageW = 900, PageH2 = 700;

    private static void PagePixel(byte[] buffer, int offset, int x, int y)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        buffer[offset] = (byte)h;
        buffer[offset + 1] = (byte)(h >> 8);
        buffer[offset + 2] = (byte)(h >> 16);
        buffer[offset + 3] = 255;
    }

    private static byte[] View(int left, int top)
    {
        var frame = new byte[W * H * 4];
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
                PagePixel(frame, (r * W + c) * 4, left + c, top + r);
        return frame;
    }

    /// <summary>Every pixel of the picture inside the box it covers is the page's pixel there.</summary>
    private static void AssertMatchesPage(byte[] pixels, int width, int left, int top, int x0, int y0, int x1, int y1)
    {
        var expected = new byte[4];
        for (int y = y0; y < y1; y += 3)
            for (int x = x0; x < x1; x += 3)
            {
                PagePixel(expected, 0, left + x, top + y);
                Assert.Equal(expected, pixels.AsSpan((y * width + x) * 4, 4).ToArray());
            }
    }

    [Fact]
    public void ScrollingSidewaysGrowsSideways()
    {
        var stitcher = new LongShotStitcher(W, H) { Axis = StitchAxis.Horizontal };
        foreach (int left in new[] { 300, 360, 430, 500, 600, 520, 400, 250, 180 })
            Assert.Equal(StitchResult.Placed, stitcher.Add(View(left, 0)));

        // Back to the left the position is followed, but the picture only ever grows to the right.
        var (pixels, width, height) = stitcher.Finish();
        Assert.Equal(H, height);
        Assert.Equal(600 + W - 300, width);
        AssertMatchesPage(pixels, width, 300, 0, 0, 0, width, height);
    }

    [Fact]
    public void DirectionIsFixedOnceStitchingBegins()
    {
        var stitcher = new LongShotStitcher(W, H);
        Assert.Equal(StitchResult.Placed, stitcher.Add(View(0, 0)));

        // Before anything is stitched the direction can still change; after, it cannot.
        stitcher.Axis = StitchAxis.Horizontal;
        stitcher.Axis = StitchAxis.Vertical;
        Assert.False(stitcher.HasMoved);

        Assert.Equal(StitchResult.Placed, stitcher.Add(View(0, 50)));
        Assert.True(stitcher.HasMoved);
        Assert.Throws<InvalidOperationException>(() => stitcher.Axis = StitchAxis.Horizontal);
        Assert.Equal(StitchAxis.Vertical, stitcher.Axis);
    }

    [Fact]
    public void SidewaysMovesAreNotReadWhileStitchingUpAndDown()
    {
        var stitcher = new LongShotStitcher(W, H);
        Assert.Equal(StitchResult.Placed, stitcher.Add(View(0, 0)));
        Assert.NotEqual(StitchResult.Placed, stitcher.Add(View(60, 0)));
        Assert.Equal(W, stitcher.Width);
    }

    [Fact]
    public void UpAndDownMovesAreNotReadWhileStitchingSideways()
    {
        var stitcher = new LongShotStitcher(W, H) { Axis = StitchAxis.Horizontal };
        Assert.Equal(StitchResult.Placed, stitcher.Add(View(0, 0)));
        Assert.NotEqual(StitchResult.Placed, stitcher.Add(View(0, 40)));
        Assert.Equal(H, stitcher.Height);
    }

    /// <summary>Regions as short as the ones picked in practice (72 to 200 px), at several steps.</summary>
    [Theory]
    [InlineData(460, 81, 20)]
    [InlineData(460, 81, 50)]
    [InlineData(230, 72, 30)]
    [InlineData(765, 168, 60)]
    public void ShortRegionsStitchWhileTheyOverlap(int width, int height, int step)
    {
        var stitcher = new LongShotStitcher(width, height);
        byte[] ViewOf(int top)
        {
            var f = new byte[width * height * 4];
            for (int r = 0; r < height; r++)
                for (int c = 0; c < width; c++) PagePixel(f, (r * width + c) * 4, c, top + r);
            return f;
        }
        for (int top = 0; top <= step * 8; top += step)
            Assert.Equal(StitchResult.Placed, stitcher.Add(ViewOf(top)));
        Assert.Equal(step * 8 + height, stitcher.Height);
    }

    [Theory]
    [InlineData(765, 168, 60)]
    [InlineData(201, 88, 40)]
    public void ShortRegionsStitchSideways(int width, int height, int step)
    {
        var stitcher = new LongShotStitcher(width, height) { Axis = StitchAxis.Horizontal };
        byte[] ViewOf(int left)
        {
            var f = new byte[width * height * 4];
            for (int r = 0; r < height; r++)
                for (int c = 0; c < width; c++) PagePixel(f, (r * width + c) * 4, left + c, r);
            return f;
        }
        for (int left = 0; left <= step * 8; left += step)
            Assert.Equal(StitchResult.Placed, stitcher.Add(ViewOf(left)));
        Assert.Equal(step * 8 + width, stitcher.Width);
    }

    // ---- what is stitched stays, redrawn text, scrolling done by the app ----

    [Fact]
    public void ScrollingBackNeverPaintsOverWhatIsStitched()
    {
        var stitcher = new LongShotStitcher(W, H);
        foreach (int top in new[] { 0, 60, 120, 180, 100, 40, 110, 170, 230 })
            Assert.Equal(StitchResult.Placed, stitcher.Add(View(0, top)));

        var (pixels, width, height) = stitcher.Finish();
        Assert.Equal(230 + H, height);
        AssertMatchesPage(pixels, width, 0, 0, 0, 0, width, height);
    }

    /// <summary>The view at top, every channel off by up to 3 at random, as text redrawn while scrolling.</summary>
    private static byte[] Redrawn(int top, Random noise)
    {
        var frame = View(0, top);
        for (int i = 0; i < frame.Length; i++)
            if (i % 4 != 3) frame[i] = (byte)Math.Clamp(frame[i] + noise.Next(-3, 4), 0, 255);
        return frame;
    }

    [Fact]
    public void FramesRedrawnSlightlyDifferentlyStillFit()
    {
        var noise = new Random(5);
        var stitcher = new LongShotStitcher(W, H);
        foreach (int top in new[] { 0, 0, 40, 85, 130, 170 })
            Assert.NotEqual(StitchResult.Lost, stitcher.Add(Redrawn(top, noise)));

        var (pixels, width, height) = stitcher.Finish();
        Assert.Equal(170 + H, height);
        var expected = new byte[4];
        for (int y = 0; y < height; y += 3)
            for (int x = 0; x < width; x += 5)
            {
                PagePixel(expected, 0, x, y);
                for (int c = 0; c < 3; c++)
                    Assert.InRange(pixels[(y * width + x) * 4 + c], expected[c] - 3, expected[c] + 3);
            }
    }

    [Fact]
    public void ScrolledByTheAppABlankStretchKeepsItsHeight()
    {
        // The app scrolls 40 px at a time and says how far it went since the last placed frame.
        var stitcher = new LongShotStitcher(W, H);
        int lastPlaced = 0;
        for (int scroll = 0; scroll <= 500; scroll += 20)
        {
            int? expected = scroll == 0 ? null : scroll - lastPlaced;
            var result = stitcher.Add(GapFrame(scroll), expected);
            Assert.NotEqual(StitchResult.Lost, result);
            if (result == StitchResult.Placed) lastPlaced = scroll;
        }

        var (pixels, _, height) = stitcher.Finish();
        Assert.Equal(PageH, height);
        for (int row = 0; row < height; row += 3)
        {
            if (row >= GapFrom && row < GapTo)
                Assert.All(pixels.AsSpan(row * W * 4, W * 4).ToArray(), b => Assert.Equal(255, b));
            else
                AssertRowIsPage(pixels, row, row);
        }
    }

    /// <summary>
    /// A frame with a side bar down its left that does not scroll, and the page beside it redrawn a
    /// little differently each time (so no exact hash matches).
    /// </summary>
    private static byte[] SideBarFrame(int scroll, int seed)
    {
        var frame = Frame(scroll, false);
        var noise = new Random(seed);
        for (int r = 0; r < H; r++)
        {
            // Each line of the bar one grey, as lines of text and spacing differ.
            byte v = (byte)new Random(r * 31 + 5).Next(256);
            for (int x = 0; x < W; x++)
            {
                int i = (r * W + x) * 4;
                if (x < 80) frame[i] = frame[i + 1] = frame[i + 2] = v;
                else frame[i] = (byte)Math.Clamp(frame[i] + noise.Next(-1, 2), 0, 255);
            }
        }
        return frame;
    }

    [Fact]
    public void ASideBarThatStaysDoesNotStopThePageBeingFollowed()
    {
        var s = new LongShotStitcher(W, H);
        s.Add(SideBarFrame(0, 1));
        int top = 0, seed = 2;
        foreach (int step in new[] { 9, 14, 20, 11, 17, 25 })
        {
            top += step;
            Assert.Equal(StitchResult.Placed, s.Add(SideBarFrame(top, seed++), forwards: true));
            Assert.Equal(step, s.LastShift);
        }
        Assert.Equal(H + top, s.Finish().Height);
    }

    [Fact]
    public void ScrollingItselfInSmallStepsStitchesEveryStep()
    {
        var s = new LongShotStitcher(W, H);
        s.Add(Frame(0, false));
        int top = 0;
        foreach (int step in new[] { 7, 12, 3, 18, 9, 25, 11 })
        {
            top += step;
            Assert.Equal(StitchResult.Placed, s.Add(Frame(top, false), forwards: true));
        }
        var (pixels, _, height) = s.Finish();
        Assert.Equal(H + top, height);
        for (int row = 0; row < height; row += 3) AssertRowIsPage(pixels, row, row);
    }

    [Fact]
    public void AnExpectedShiftKeepsTheSearchForwards()
    {
        // Rows that repeat every 30 px: alone they fit 30 px back as well as 30 px on.
        var stitcher = new LongShotStitcher(W, H);
        byte[] Striped(int top)
        {
            var f = new byte[W * H * 4];
            for (int r = 0; r < H; r++) PageRow(f, r * W * 4, (top + r) % 30);
            return f;
        }
        Assert.Equal(StitchResult.Placed, stitcher.Add(Striped(0)));
        stitcher.Add(Striped(30), expected: 30);
        Assert.True(stitcher.LastShift >= 0);
        Assert.True(stitcher.Height >= H);
    }
}
