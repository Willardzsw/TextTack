namespace OverTranslate.Services.LongShot;

/// <summary>What became of one frame given to <see cref="LongShotStitcher.Add"/>.</summary>
public enum StitchResult
{
    /// <summary>Nothing moved: the frame tells nothing new and was left out.</summary>
    Unchanged,

    /// <summary>The frame was placed, and whatever it showed that the picture lacked was added.</summary>
    Placed,

    /// <summary>The page moved but where to could not be told — scrolled too far between two looks.</summary>
    Lost,

    /// <summary>The picture has reached its largest size; nothing more is added.</summary>
    Full,
}

/// <summary>Which way a long screenshot grows: the user picks one, and moves the other way are not read.</summary>
public enum StitchAxis { Vertical, Horizontal }

/// <summary>
/// 長截圖: the frames of a region, read while the user scrolls the page under it — up, down, left or
/// right — put together into one picture.
/// </summary>
/// <remarks>
/// <para>One direction at a time, <see cref="Axis"/>: reading both at once let a move be taken for the
/// other kind now and then, and the picture came out an odd shape.</para>
///
/// <para>Each frame is placed against the one before. For an up-or-down move every row of a frame
/// gets one hash per vertical strip; each changed strip whose hash is rare in the last frame votes for
/// the shift that would put it on the row with that hash. A left-or-right move is the same with
/// columns and horizontal bands. Plain background votes for nothing, and neither does anything that
/// looks as it did in the frame before: what stays in place while the page moves (a header, a side
/// bar, frozen panes) knows nothing about where the page went. The shift with the most votes wins if
/// it is clear of the runner-up; otherwise the frame is left out until one fits again.</para>
///
/// <para>The picture is a canvas in page coordinates, the first frame at (0, 0), that grows in any
/// direction. What moved is painted at its place; what stood still at the edges of the frame (a
/// header, a footer, a frozen column) is not, so it is not repeated through the picture, and is
/// added once at the picture's edge by <see cref="Finish"/>.</para>
///
/// <para>A stretch of blank page taller than what two looks share cannot be measured — nothing in it
/// says how far the page went — so it is shortened: the content after it is put a short blank away
/// from the content before it. This is an office tool, and a long empty gap carries nothing.</para>
///
/// <para>The method follows sniplite's scroll.c (MIT), extended to four directions; the code is
/// written for this project.</para>
/// </remarks>
public sealed class LongShotStitcher
{
    public const int Strips = 4;

    /// <summary>The most pixels the picture may cover (about 256 MB): past it, <see cref="StitchResult.Full"/>.</summary>
    public const long MaxArea = 64L * 1024 * 1024;

    /// <summary>The longest side the picture may have, in pixels.</summary>
    public int MaxSize { get; }

    /// <summary>
    /// Which moves are read. Fixed once the page has moved and something was stitched that way: a
    /// picture grown down and then sideways is an L, not a page.
    /// </summary>
    public StitchAxis Axis
    {
        get => _axis;
        set
        {
            if (HasMoved && value != _axis) throw new InvalidOperationException("The direction is fixed once stitching has begun.");
            _axis = value;
        }
    }

    /// <summary>Whether anything past the first frame has been stitched — from then on <see cref="Axis"/> is fixed.</summary>
    public bool HasMoved { get; private set; }

    private StitchAxis _axis = StitchAxis.Vertical;

    public int FrameWidth { get; }
    public int FrameHeight { get; }

    /// <summary>The picture so far, its held-back edges not counted.</summary>
    public int Width => _canvas.Width;

    /// <inheritdoc cref="Width"/>
    public int Height => _canvas.Height;

    private readonly int _stripLeft, _stripWidth, _bandTop, _bandHeight;
    private readonly TileCanvas _canvas = new();

    private byte[]? _last;
    private ulong[]? _lastRowHashes, _lastColHashes;
    private bool[]? _lastRowPlain;
    private int _posX, _posY;
    private bool _full;

    // The edges that stood still the last time the page moved that way: added once at the end.
    private int _header, _footer, _leftEdge, _rightEdge;
    private byte[]? _edgeFrame;

    public LongShotStitcher(int frameWidth, int frameHeight, int maxSize = 20000)
    {
        if (frameWidth <= 0 || frameHeight <= 0) throw new ArgumentOutOfRangeException(nameof(frameWidth));
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        MaxSize = Math.Max(maxSize, Math.Max(frameWidth, frameHeight));

        // Strips across for rows: a few per cent off the left, enough off the right for a scroll bar
        // at 200%. Bands down for columns: the same at the top and bottom.
        (_stripLeft, _stripWidth) = Lanes(frameWidth);
        (_bandTop, _bandHeight) = Lanes(frameHeight);
    }

    private static (int Start, int Size) Lanes(int length)
    {
        int start = length >= 100 ? length / 25 : 0;
        int end = length >= 200 ? Math.Max(length / 25, 40) : 0;
        int usable = Math.Max(Strips, length - start - end);
        return (start, Math.Max(1, usable / Strips));
    }

    /// <summary>One frame of the region, BGRA, rows packed.</summary>
    public StitchResult Add(byte[] frame)
    {
        if (frame.Length < FrameWidth * FrameHeight * 4) throw new ArgumentException("Frame too small", nameof(frame));
        if (_full) return StitchResult.Full;

        var (rows, rowPlain) = HashRows(frame);

        if (_last is null)
        {
            _canvas.Paint(frame, FrameWidth, 0, 0, FrameWidth, FrameHeight, 0, 0);
            Remember(frame, rows, rowPlain, null);
            return StitchResult.Placed;
        }

        int changed = 0, informative = 0;
        for (int r = 0; r < FrameHeight; r++)
        {
            bool plainRow = LinePlain(rowPlain, r);
            if (!plainRow) informative++;
            if (!plainRow && !LineSame(rows, _lastRowHashes!, r)) changed++;
        }
        if (changed == 0) return StitchResult.Unchanged;

        if (Axis == StitchAxis.Vertical)
        {
            if (Vote(rows, rowPlain, _lastRowHashes!, FrameHeight) is { } dy)
                return dy == 0 ? InPlace(frame, rows, rowPlain) : MoveVertically(frame, rows, rowPlain, dy);
            if (TryBridgeBlank(frame, rows, rowPlain)) return _full ? StitchResult.Full : StitchResult.Placed;
        }
        else
        {
            var (cols, colPlain) = HashColumns(frame);
            _lastColHashes ??= HashColumns(_last).Hashes;
            if (Vote(cols, colPlain, _lastColHashes, FrameWidth) is { } dx)
                return dx == 0 ? InPlace(frame, rows, rowPlain) : MoveHorizontally(frame, rows, rowPlain, cols, dx);
        }

        // Much of what there is to see changed and none of it can be found: the page went further
        // than one frame. A little that changed in place (a caret, a hover, a spinner) is not that,
        // however little else there is to see.
        int enough = Math.Max(8, FrameHeight / 10);
        return changed >= enough && changed * 2 > informative ? StitchResult.Lost : StitchResult.Unchanged;
    }

    private StitchResult InPlace(byte[] frame, ulong[] rows, bool[] rowPlain)
    {
        // Something on the page changed, the page did not move. Nothing to add.
        Remember(frame, rows, rowPlain, null);
        return StitchResult.Unchanged;
    }

    private StitchResult MoveVertically(byte[] frame, ulong[] rows, bool[] rowPlain, int dy)
    {
        _header = StillLines(rows, _lastRowHashes!, FrameHeight, fromStart: true);
        _footer = StillLines(rows, _lastRowHashes!, FrameHeight, fromStart: false);
        _edgeFrame = frame;

        _posY += dy;
        HasMoved = true;
        Paint(frame, 0, _header, FrameWidth, FrameHeight - _footer - _header);
        Remember(frame, rows, rowPlain, null);
        return _full ? StitchResult.Full : StitchResult.Placed;
    }

    private StitchResult MoveHorizontally(byte[] frame, ulong[] rows, bool[] rowPlain, ulong[] cols, int dx)
    {
        _leftEdge = StillLines(cols, _lastColHashes!, FrameWidth, fromStart: true);
        _rightEdge = StillLines(cols, _lastColHashes!, FrameWidth, fromStart: false);
        _edgeFrame = frame;

        _posX += dx;
        HasMoved = true;
        Paint(frame, _leftEdge, 0, FrameWidth - _leftEdge - _rightEdge, FrameHeight);
        Remember(frame, rows, rowPlain, cols);
        return _full ? StitchResult.Full : StitchResult.Placed;
    }

    /// <summary>
    /// A stretch of blank page taller than what two looks share: the last frame ends in blank, the
    /// new one starts in blank (or the other way round, scrolling up), and nothing between them can
    /// be matched. The new content is put a short blank away from the old — the blank shortened,
    /// since how tall it really was cannot be seen. Not done when either side has content at that
    /// edge: that is a page scrolled too far, which is <see cref="StitchResult.Lost"/>, not a gap.
    /// </summary>
    private bool TryBridgeBlank(byte[] frame, ulong[] rows, bool[] rowPlain)
    {
        int header = StillLines(rows, _lastRowHashes!, FrameHeight, fromStart: true);
        int footer = StillLines(rows, _lastRowHashes!, FrameHeight, fromStart: false);
        int top = header, bottom = FrameHeight - footer; // the moving part, [top, bottom)
        if (bottom - top < 3) return false;

        int lastBlankBelow = 0;
        while (bottom - 1 - lastBlankBelow >= top && LinePlain(_lastRowPlain!, bottom - 1 - lastBlankBelow)) lastBlankBelow++;
        int lastBlankAbove = 0;
        while (top + lastBlankAbove < bottom && LinePlain(_lastRowPlain!, top + lastBlankAbove)) lastBlankAbove++;
        int newBlankAbove = 0;
        while (top + newBlankAbove < bottom && LinePlain(rowPlain, top + newBlankAbove)) newBlankAbove++;
        int newBlankBelow = 0;
        while (bottom - 1 - newBlankBelow >= top && LinePlain(rowPlain, bottom - 1 - newBlankBelow)) newBlankBelow++;

        // Nothing but blank in the moving part of either: wait for content to come into view.
        if (lastBlankAbove >= bottom - top || newBlankAbove >= bottom - top) return false;

        int need = Math.Max(16, FrameHeight / 5);
        int gap = Math.Clamp(FrameHeight / 10, 24, 80);

        if (lastBlankBelow >= need && newBlankAbove >= need)
        {
            // Down across a gap: the new content a short blank under the last.
            int lastContentEnd = _posY + bottom - lastBlankBelow;
            int content = top + newBlankAbove;
            _posY = lastContentEnd + gap - content;
            int from = Math.Max(top, content - gap);
            Paint(frame, 0, from, FrameWidth, bottom - from);
        }
        else if (lastBlankAbove >= need && newBlankBelow >= need)
        {
            // Up across a gap: the new content a short blank over the last.
            int lastContentStart = _posY + top + lastBlankAbove;
            int contentEnd = bottom - newBlankBelow;
            _posY = lastContentStart - gap - contentEnd;
            int to = Math.Min(bottom, contentEnd + gap);
            Paint(frame, 0, top, FrameWidth, to - top);
        }
        else
        {
            return false;
        }

        _header = header;
        _footer = footer;
        _edgeFrame = frame;
        HasMoved = true;
        Remember(frame, rows, rowPlain, null);
        return true;
    }

    /// <summary>
    /// The whole picture, BGRA, rows packed — with the edges that stood still put back once at the
    /// picture's own edges: the footer under it, a frozen column beside it, a header over it when
    /// the page was scrolled above where it started.
    /// </summary>
    public (byte[] Pixels, int Width, int Height) Finish()
    {
        if (_edgeFrame is { } edges && !_full)
        {
            if (_footer > 0)
                _canvas.Paint(edges, FrameWidth, 0, FrameHeight - _footer, FrameWidth, _footer, _posX, _canvas.MaxY);
            if (_header > 0 && _canvas.MinY < 0)
                _canvas.Paint(edges, FrameWidth, 0, 0, FrameWidth, _header, _posX, _canvas.MinY - _header);
            if (_rightEdge > 0)
                _canvas.Paint(edges, FrameWidth, FrameWidth - _rightEdge, 0, _rightEdge, FrameHeight, _canvas.MaxX, _posY);
            if (_leftEdge > 0 && _canvas.MinX < 0)
                _canvas.Paint(edges, FrameWidth, 0, 0, _leftEdge, FrameHeight, _canvas.MinX - _leftEdge, _posY);
        }
        return _canvas.Export();
    }

    /// <summary>
    /// The picture around where the page is now, scaled to <paramref name="width"/> by at most
    /// <paramref name="height"/> pixels — the preview beside the region. BGRA, rows packed.
    /// </summary>
    public (byte[] Pixels, int Width, int Height) Preview(int width, int height) =>
        Axis == StitchAxis.Vertical
            ? _canvas.Preview(width, height, _posY + FrameHeight)
            : _canvas.PreviewAcross(width, height, _posX + FrameWidth);

    // ---- painting, within the size allowed ----

    /// <summary>Part of the frame onto the canvas at the frame's position, cut short at the size limit.</summary>
    private void Paint(byte[] frame, int sx, int sy, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int dx = _posX + sx, dy = _posY + sy;

        // Rows: what would make the picture taller than allowed is cut from the side it grows on.
        int minX = Math.Min(_canvas.MinX, dx), maxX = Math.Max(_canvas.MaxX, dx + w);
        long allowedH = Math.Min(MaxSize, MaxArea / Math.Max(1, maxX - minX));
        if (Math.Max(_canvas.MaxY, dy + h) - Math.Min(_canvas.MinY, dy) > allowedH)
        {
            _full = true;
            if (dy + h > _canvas.MaxY) h = (int)Math.Max(0, _canvas.MinY + allowedH - dy);
            else { int cut = (int)Math.Clamp(_canvas.MaxY - allowedH - dy, 0, h); sy += cut; dy += cut; h -= cut; }
        }

        // Columns, the same.
        long allowedW = Math.Min(MaxSize, MaxArea / Math.Max(1, Math.Max(_canvas.MaxY, dy + h) - Math.Min(_canvas.MinY, dy)));
        if (Math.Max(_canvas.MaxX, dx + w) - Math.Min(_canvas.MinX, dx) > allowedW)
        {
            _full = true;
            if (dx + w > _canvas.MaxX) w = (int)Math.Max(0, _canvas.MinX + allowedW - dx);
            else { int cut = (int)Math.Clamp(_canvas.MaxX - allowedW - dx, 0, w); sx += cut; dx += cut; w -= cut; }
        }

        if (w > 0 && h > 0) _canvas.Paint(frame, FrameWidth, sx, sy, w, h, dx, dy);
    }

    // ---- placing ----

    /// <summary>
    /// The shift, in lines (rows or columns), that puts the most changed lines of the new frame on
    /// lines of the last one with the same hash; null when no shift is clear of the rest.
    /// </summary>
    private static int? Vote(ulong[] hashes, bool[] plain, ulong[] last, int lines)
    {
        // Where each lane's hash is in the last frame; past five places a hash counts as common.
        var index = new Dictionary<ulong, List<int>>();
        for (int line = 0; line < lines; line++)
            for (int s = 0; s < Strips; s++)
            {
                ulong key = Key(last[line * Strips + s], s);
                if (!index.TryGetValue(key, out var at)) index[key] = at = [];
                if (at.Count <= 4) at.Add(line);
            }

        var votes = new Dictionary<int, int>();
        for (int line = 0; line < lines; line++)
            for (int s = 0; s < Strips; s++)
            {
                int i = line * Strips + s;
                if (plain[i] || hashes[i] == last[i]) continue;
                if (!index.TryGetValue(Key(hashes[i], s), out var at)) continue;

                // Common lines (a repeated border, a list of identical items) would vote everywhere.
                if (at.Count > 4) continue;
                foreach (int match in at)
                {
                    int shift = match - line;
                    votes[shift] = votes.GetValueOrDefault(shift) + 1;
                }
            }

        if (votes.Count == 0) return null;
        int best = 0, bestVotes = 0, second = 0;
        foreach (var (shift, v) in votes)
        {
            if (v > bestVotes) { second = bestVotes; bestVotes = v; best = shift; }
            else if (v > second) second = v;
        }
        return bestVotes >= 3 && bestVotes >= second * 2 ? best : null;
    }

    /// <summary>Lines at one end that look exactly as they did while the page moved, up to a third.</summary>
    private static int StillLines(ulong[] hashes, ulong[] last, int lines, bool fromStart)
    {
        int n = 0;
        while (n < lines / 3 && LineSame(hashes, last, fromStart ? n : lines - 1 - n)) n++;
        return n;
    }

    private static bool LineSame(ulong[] a, ulong[] b, int line)
    {
        for (int s = 0; s < Strips; s++)
            if (a[line * Strips + s] != b[line * Strips + s]) return false;
        return true;
    }

    private static bool LinePlain(bool[] plain, int line)
    {
        for (int s = 0; s < Strips; s++)
            if (!plain[line * Strips + s]) return false;
        return true;
    }

    private void Remember(byte[] frame, ulong[] rows, bool[] rowPlain, ulong[]? cols)
    {
        _last = (byte[])frame.Clone();
        _lastRowHashes = rows;
        _lastRowPlain = rowPlain;
        // Columns are hashed only when a frame needs them; otherwise the next look hashes the last.
        _lastColHashes = cols;
    }

    private static ulong Key(ulong hash, int lane) => hash ^ ((ulong)(lane + 1) * 0x9E3779B97F4A7C15UL);

    // ---- hashing ----

    /// <summary>Per row and strip: an FNV-1a hash of the colours, and whether the strip is one colour.</summary>
    private (ulong[] Hashes, bool[] Plain) HashRows(byte[] frame)
    {
        var hashes = new ulong[FrameHeight * Strips];
        var plain = new bool[FrameHeight * Strips];
        int stride = FrameWidth * 4;
        for (int r = 0; r < FrameHeight; r++)
            for (int s = 0; s < Strips; s++)
            {
                int start = r * stride + (_stripLeft + s * _stripWidth) * 4;
                (hashes[r * Strips + s], plain[r * Strips + s]) = HashRun(frame, start, 4, _stripWidth);
            }
        return (hashes, plain);
    }

    /// <summary>Per column and band, the same.</summary>
    private (ulong[] Hashes, bool[] Plain) HashColumns(byte[] frame)
    {
        var hashes = new ulong[FrameWidth * Strips];
        var plain = new bool[FrameWidth * Strips];
        int stride = FrameWidth * 4;
        for (int c = 0; c < FrameWidth; c++)
            for (int s = 0; s < Strips; s++)
            {
                int start = (_bandTop + s * _bandHeight) * stride + c * 4;
                (hashes[c * Strips + s], plain[c * Strips + s]) = HashRun(frame, start, stride, _bandHeight);
            }
        return (hashes, plain);
    }

    private static (ulong Hash, bool Plain) HashRun(byte[] frame, int start, int step, int count)
    {
        ulong h = 14695981039346656037UL;
        bool same = true;
        byte b0 = frame[start], g0 = frame[start + 1], r0 = frame[start + 2];
        for (int k = 0, i = start; k < count; k++, i += step)
        {
            byte b = frame[i], g = frame[i + 1], r = frame[i + 2];
            if (b != b0 || g != g0 || r != r0) same = false;
            h = (h ^ b) * 1099511628211UL;
            h = (h ^ g) * 1099511628211UL;
            h = (h ^ r) * 1099511628211UL;
        }
        return (h, same);
    }
}

/// <summary>
/// A picture that grows in every direction: 256-pixel tiles keyed by where they are, white until
/// painted, and the box around everything painted so far.
/// </summary>
internal sealed class TileCanvas
{
    private const int T = 256;
    private readonly Dictionary<(int, int), byte[]> _tiles = [];

    public bool Any { get; private set; }
    public int MinX { get; private set; }
    public int MinY { get; private set; }
    public int MaxX { get; private set; }
    public int MaxY { get; private set; }
    public int Width => MaxX - MinX;
    public int Height => MaxY - MinY;

    /// <summary>The w by h block at (sx, sy) of a BGRA picture srcWidth wide, to (dx, dy).</summary>
    public void Paint(byte[] src, int srcWidth, int sx, int sy, int w, int h, int dx, int dy)
    {
        if (w <= 0 || h <= 0) return;
        for (int y = 0; y < h; y++)
        {
            int cy = dy + y, srcRow = ((sy + y) * srcWidth + sx) * 4;
            int x = 0;
            while (x < w)
            {
                int cx = dx + x;
                var tile = TileAt(cx >> 8, cy >> 8);
                int lx = cx & (T - 1), run = Math.Min(w - x, T - lx);
                Buffer.BlockCopy(src, srcRow + x * 4, tile, ((cy & (T - 1)) * T + lx) * 4, run * 4);
                x += run;
            }
        }

        if (!Any) { MinX = dx; MinY = dy; MaxX = dx + w; MaxY = dy + h; Any = true; }
        else
        {
            MinX = Math.Min(MinX, dx); MinY = Math.Min(MinY, dy);
            MaxX = Math.Max(MaxX, dx + w); MaxY = Math.Max(MaxY, dy + h);
        }
    }

    private byte[] TileAt(int tx, int ty)
    {
        if (!_tiles.TryGetValue((tx, ty), out var tile))
        {
            tile = new byte[T * T * 4];
            Array.Fill(tile, (byte)255);
            _tiles[(tx, ty)] = tile;
        }
        return tile;
    }

    /// <summary>The pixel at canvas (x, y) into dst at offset; white where nothing was painted.</summary>
    private void Read(int x, int y, byte[] dst, int offset)
    {
        if (_tiles.TryGetValue((x >> 8, y >> 8), out var tile))
            Buffer.BlockCopy(tile, ((y & (T - 1)) * T + (x & (T - 1))) * 4, dst, offset, 4);
        else
            dst[offset] = dst[offset + 1] = dst[offset + 2] = dst[offset + 3] = 255;
    }

    public (byte[] Pixels, int Width, int Height) Export()
    {
        if (!Any) return ([], 0, 0);
        int w = Width, h = Height;
        var pixels = new byte[(long)w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int cy = MinY + y, x = 0;
            while (x < w)
            {
                int cx = MinX + x, lx = cx & (T - 1), run = Math.Min(w - x, T - lx);
                int dst = (y * w + x) * 4;
                if (_tiles.TryGetValue((cx >> 8, cy >> 8), out var tile))
                    Buffer.BlockCopy(tile, ((cy & (T - 1)) * T + lx) * 4, pixels, dst, run * 4);
                else
                    Array.Fill(pixels, (byte)255, dst, run * 4);
                x += run;
            }
        }
        return (pixels, w, h);
    }

    /// <summary>The whole height scaled to <paramref name="height"/>; the columns that end at canvas column endX.</summary>
    public (byte[] Pixels, int Width, int Height) PreviewAcross(int width, int height, int endX)
    {
        if (!Any || width <= 0 || height <= 0) return ([], 0, 0);
        double scale = Math.Max((double)Height / height, (double)Width / (width * 8.0));
        int rows = Math.Min(height, (int)(Height / scale));
        int cols = Math.Min(width, (int)(Width / scale));
        if (rows <= 0 || cols <= 0) return ([], 0, 0);

        int span = (int)(cols * scale);
        int end = Math.Clamp(endX, MinX + span, MaxX);
        int first = end - span;
        var pixels = new byte[cols * rows * 4];
        for (int y = 0; y < rows; y++)
        {
            int cy = Math.Clamp(MinY + (int)(y * scale), MinY, MaxY - 1);
            for (int x = 0; x < cols; x++)
                Read(Math.Clamp(first + (int)(x * scale), MinX, MaxX - 1), cy, pixels, (y * cols + x) * 4);
        }
        return (pixels, cols, rows);
    }

    /// <summary>The whole width scaled to <paramref name="width"/>; the rows that end at canvas row endY.</summary>
    public (byte[] Pixels, int Width, int Height) Preview(int width, int height, int endY)
    {
        if (!Any || width <= 0 || height <= 0) return ([], 0, 0);
        double scale = (double)Width / width;
        int rows = Math.Min(height, (int)(Height / scale));
        if (rows <= 0) return ([], 0, 0);

        int span = (int)(rows * scale);
        int end = Math.Clamp(endY, MinY + span, MaxY);
        int first = end - span;
        var pixels = new byte[width * rows * 4];
        for (int y = 0; y < rows; y++)
        {
            int cy = Math.Clamp(first + (int)(y * scale), MinY, MaxY - 1);
            for (int x = 0; x < width; x++)
                Read(Math.Clamp(MinX + (int)(x * scale), MinX, MaxX - 1), cy, pixels, (y * width + x) * 4);
        }
        return (pixels, width, rows);
    }
}
