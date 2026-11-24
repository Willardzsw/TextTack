namespace OverTranslate.Services.LongShot;

/// <summary>What became of one frame given to <see cref="LongShotStitcher.Add"/>.</summary>
public enum StitchResult
{
    /// <summary>Nothing moved: the frame tells nothing new and was left out.</summary>
    Unchanged,

    /// <summary>The frame was placed, and whatever it showed past the end of the picture was added.</summary>
    Placed,

    /// <summary>The page moved but where to could not be told — scrolled too far between two looks.</summary>
    Lost,

    /// <summary>The picture has reached its largest size; nothing more is added.</summary>
    Full,
}

/// <summary>Which way a long screenshot grows: the user picks one, and moves the other way are not read.</summary>
public enum StitchAxis { Vertical, Horizontal }

/// <summary>
/// 長截圖: the frames of a region, read while the page under it scrolls, put together into one
/// picture that grows downwards (or to the right).
/// </summary>
/// <remarks>
/// <para>One direction at a time, <see cref="Axis"/>: reading both at once let a move be taken for the
/// other kind now and then, and the picture came out an odd shape.</para>
///
/// <para>Each frame is placed against the one before. First by exact hashes: every row of a frame
/// gets one hash per vertical strip, and each changed strip whose hash is rare in the last frame
/// votes for the shift that would put it on the row with that hash (columns and horizontal bands
/// for a left-or-right move). A vote is then checked against a coarser look — the mean brightness
/// of a couple of dozen cells along each row — and where hashes find nothing (smooth scrolling and
/// anti-aliasing redraw text a little differently each time) that coarser look alone is searched
/// for the shift where the two frames agree best, taken only when it is clearly better than any
/// other. What stays in place while the page moves (a header, a footer, frozen panes) is left out
/// of both.</para>
///
/// <para>Only what lies past the end of the picture is ever painted: the page may be scrolled back
/// and forth and the position is followed, but what was stitched once stays as it was. A wrong
/// placement then costs at most a little of what follows, never what is already there.</para>
///
/// <para>When the caller scrolls the page itself it knows roughly how far it went and passes that
/// as the expected shift: the search keeps near it, forwards only, and a page that moved yet
/// cannot be matched (a blank stretch, a picture that changed) is placed where it was sent.
/// Without one, a stretch of blank page taller than what two looks share is shortened instead:
/// nothing in it says how far the page went.</para>
///
/// <para>The hash vote follows sniplite's scroll.c (MIT); the code is written for this project.</para>
/// </remarks>
public sealed class LongShotStitcher
{
    public const int Strips = 4;

    // Cells per line in the coarse look, and the mean difference per cell (0-255 brightness) under
    // which two stretches count as the same: a found shift must be below MatchCost, a voted one
    // below VerifyCost.
    private const int Cells = 24;
    private const double MatchCost = 6.0, VerifyCost = 12.0;

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

    /// <summary>Whether the first frame has been taken.</summary>
    public bool HasStarted => _last is not null;

    /// <summary>How far the page moved at the last placed frame, in pixels along <see cref="Axis"/>; down or right is positive.</summary>
    public int LastShift { get; private set; }

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
    private Lines? _lastLines;
    private StitchAxis _lastAxis;
    private int _posX, _posY;
    private bool _full;

    // Along the axis, the canvas line up to which the picture is final: only what is past it is
    // painted. Until the page first moves the first frame's last third may be a footer.
    private int _solid;

    // The edge that stood still the last time the page moved: added once at the end.
    private int _footer, _rightEdge;
    private byte[]? _edgeFrame;

    /// <summary>One frame's lines along the axis: exact hashes, plain or not, and the coarse look.</summary>
    private sealed record Lines(ulong[] Hashes, bool[] Plain, int[] Profile, int CellsPerLine, int Count);

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
    /// <param name="frame">The frame.</param>
    /// <param name="expected">
    /// How far the page was scrolled forwards since the last placed frame, when the caller scrolled
    /// it itself and can tell; null when the user scrolls.
    /// </param>
    /// <param name="forwards">
    /// The page is being scrolled forwards (by the caller, in small steps): no shift backwards is
    /// looked for, so a page repeating itself is not matched the wrong way.
    /// </param>
    public StitchResult Add(byte[] frame, int? expected = null, bool forwards = false)
    {
        if (frame.Length < FrameWidth * FrameHeight * 4) throw new ArgumentException("Frame too small", nameof(frame));
        if (_full) return StitchResult.Full;

        bool vertical = Axis == StitchAxis.Vertical;
        var lines = vertical ? RowLines(frame) : ColumnLines(frame);

        if (_last is null)
        {
            _canvas.Paint(frame, FrameWidth, 0, 0, FrameWidth, FrameHeight, 0, 0);
            Remember(frame, lines);
            return StitchResult.Placed;
        }

        if (_lastLines is null || _lastAxis != Axis) _lastLines = vertical ? RowLines(_last) : ColumnLines(_last);
        var last = _lastLines;
        int n = lines.Count;

        int changed = 0, informative = 0;
        for (int l = 0; l < n; l++)
        {
            bool plain = LinePlain(lines.Plain, l);
            if (!plain) informative++;
            if (!plain && !LineSame(lines.Hashes, last.Hashes, l)) changed++;
        }
        if (changed == 0) return StitchResult.Unchanged;

        int head = StillLines(lines.Hashes, last.Hashes, n, fromStart: true);
        int tail = StillLines(lines.Hashes, last.Hashes, n, fromStart: false);

        // Shifts that leave enough of the moving part shared to be told apart; with an expected
        // shift, only forwards and near it.
        int reach = Math.Max(0, n - head - tail - Math.Max(16, n / 8));
        int lo = -reach, hi = reach;
        if (expected is > 0)
        {
            int e = expected.Value;
            lo = Math.Max(1, e / 2 - 8);
            hi = Math.Min(reach, e * 2 + 16);
        }
        else if (forwards) lo = 0;

        // Columns that stood still while the page moved (a side bar, a table of contents, a margin)
        // say nothing of how far it went, and compared at any shift but none they only add.
        var still = StillCells(lines, last, head, tail);
        int? shift = Vote(lines, last, lo, hi);
        if (shift is { } voted && Cost(lines, last, head, tail, voted, still, out _) > VerifyCost) shift = null;
        shift ??= BestFit(lines, last, head, tail, lo, hi, still);
        if (shift is { } s) return s == 0 ? InPlace(frame, lines) : Move(frame, lines, s, head, tail);

        // Much of what there is to see changed and none of it can be found: the page went further
        // than one frame. A little that changed in place (a caret, a hover, a spinner) is not that,
        // however little else there is to see.
        bool moved = changed >= Math.Max(8, n / 10) && changed * 2 > informative;
        if (moved && expected is > 0) return Move(frame, lines, expected.Value, head, tail);
        if (vertical && TryBridgeBlank(frame, lines, last, head, tail)) return _full ? StitchResult.Full : StitchResult.Placed;
        return moved ? StitchResult.Lost : StitchResult.Unchanged;
    }

    private StitchResult InPlace(byte[] frame, Lines lines)
    {
        // Something on the page changed, the page did not move. Nothing to add.
        Remember(frame, lines);
        return StitchResult.Unchanged;
    }

    private StitchResult Move(byte[] frame, Lines lines, int shift, int head, int tail)
    {
        BeginMoving();
        LastShift = shift;
        if (Axis == StitchAxis.Vertical) { _posY += shift; _footer = tail; }
        else { _posX += shift; _rightEdge = tail; }
        _edgeFrame = frame;
        Append(frame, head, tail);
        Remember(frame, lines);
        return _full ? StitchResult.Full : StitchResult.Placed;
    }

    private void BeginMoving()
    {
        if (HasMoved) return;
        HasMoved = true;
        _solid = (Axis == StitchAxis.Vertical ? FrameHeight : FrameWidth) * 2 / 3;
    }

    /// <summary>
    /// What the frame, at its position, shows past the final end of the picture: painted there, and
    /// the end moved to the frame's own end less its <paramref name="tail"/> that stood still.
    /// Anything painted past the old end before (the first frame's last third, or the footer added
    /// by <see cref="Finish"/>) is given up to it. Nothing at all when the page went back.
    /// </summary>
    private void Append(byte[] frame, int head, int tail)
    {
        bool vertical = Axis == StitchAxis.Vertical;
        int length = vertical ? FrameHeight : FrameWidth;
        int pos = vertical ? _posY : _posX;
        int end = pos + length - tail;
        if (end <= _solid) return;

        int from = Math.Max(head, _solid - pos);
        if (vertical)
        {
            _canvas.TrimBottom(_solid);
            Paint(frame, 0, from, FrameWidth, length - tail - from);
        }
        else
        {
            _canvas.TrimRight(_solid);
            Paint(frame, from, 0, length - tail - from, FrameHeight);
        }
        _solid = end;
    }

    /// <summary>
    /// A stretch of blank page taller than what two looks share: the last frame ends in blank, the
    /// new one starts in blank, and nothing between them can be matched. The new content is put a
    /// short blank under the old — the blank shortened, since how tall it really was cannot be
    /// seen. Not done when either side has content at that edge: that is a page scrolled too far,
    /// which is <see cref="StitchResult.Lost"/>, not a gap.
    /// </summary>
    private bool TryBridgeBlank(byte[] frame, Lines lines, Lines last, int head, int tail)
    {
        int top = head, bottom = FrameHeight - tail; // the moving part, [top, bottom)
        if (bottom - top < 3) return false;

        int lastBlankBelow = 0;
        while (bottom - 1 - lastBlankBelow >= top && LinePlain(last.Plain, bottom - 1 - lastBlankBelow)) lastBlankBelow++;
        int lastBlankAbove = 0;
        while (top + lastBlankAbove < bottom && LinePlain(last.Plain, top + lastBlankAbove)) lastBlankAbove++;
        int newBlankAbove = 0;
        while (top + newBlankAbove < bottom && LinePlain(lines.Plain, top + newBlankAbove)) newBlankAbove++;

        // Nothing but blank in the moving part of either: wait for content to come into view.
        if (lastBlankAbove >= bottom - top || newBlankAbove >= bottom - top) return false;

        int need = Math.Max(16, FrameHeight / 5);
        int gap = Math.Clamp(FrameHeight / 10, 24, 80);
        if (lastBlankBelow < need || newBlankAbove < need) return false;

        BeginMoving();
        int lastContentEnd = _posY + bottom - lastBlankBelow;
        int content = top + newBlankAbove;

        // The picture is final only up to the last content; the new content goes a gap under it.
        _solid = Math.Min(_solid, lastContentEnd);
        int pos = lastContentEnd + gap - content;
        LastShift = pos - _posY;
        _posY = pos;
        _footer = tail;
        _edgeFrame = frame;
        Append(frame, Math.Max(top, content - gap), tail);
        Remember(frame, lines);
        return true;
    }

    /// <summary>
    /// The whole picture, BGRA, rows packed — with the edge that stood still put back once at the
    /// picture's end: the footer under it, or a frozen column to its right.
    /// </summary>
    public (byte[] Pixels, int Width, int Height) Finish()
    {
        if (_edgeFrame is { } edges && !_full)
        {
            if (Axis == StitchAxis.Vertical && _footer > 0)
                _canvas.Paint(edges, FrameWidth, 0, FrameHeight - _footer, FrameWidth, _footer, _posX, _canvas.MaxY);
            if (Axis == StitchAxis.Horizontal && _rightEdge > 0)
                _canvas.Paint(edges, FrameWidth, FrameWidth - _rightEdge, 0, _rightEdge, FrameHeight, _canvas.MaxX, _posY);
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
    /// The shift in [lo, hi], in lines, that puts the most changed lines of the new frame on lines
    /// of the last one with the same hash; null when no shift is clear of the rest.
    /// </summary>
    private static int? Vote(Lines now, Lines last, int lo, int hi)
    {
        if (lo > hi) return null;
        int lines = now.Count;

        // Where each lane's hash is in the last frame; past five places a hash counts as common.
        var index = new Dictionary<ulong, List<int>>();
        for (int line = 0; line < lines; line++)
            for (int s = 0; s < Strips; s++)
            {
                ulong key = Key(last.Hashes[line * Strips + s], s);
                if (!index.TryGetValue(key, out var at)) index[key] = at = [];
                if (at.Count <= 4) at.Add(line);
            }

        var votes = new Dictionary<int, int>();
        for (int line = 0; line < lines; line++)
            for (int s = 0; s < Strips; s++)
            {
                int i = line * Strips + s;
                if (now.Plain[i] || now.Hashes[i] == last.Hashes[i]) continue;
                if (!index.TryGetValue(Key(now.Hashes[i], s), out var at)) continue;

                // Common lines (a repeated border, a list of identical items) would vote everywhere.
                if (at.Count > 4) continue;
                foreach (int match in at)
                {
                    int shift = match - line;
                    if (shift < lo || shift > hi) continue;
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

    /// <summary>
    /// The shift in [lo, hi] at which the coarse looks of the two frames agree best, if they agree
    /// well there and clearly worse at every shift not next to it.
    /// </summary>
    private static int? BestFit(Lines now, Lines last, int head, int tail, int lo, int hi, bool[] still)
    {
        if (lo > hi) return null;
        int minUsed = Math.Max(8, now.Count / 10);
        double best = double.MaxValue;
        int bestShift = 0;
        var costs = new double[hi - lo + 1];
        for (int s = lo; s <= hi; s++)
        {
            double c = Cost(now, last, head, tail, s, still, out int used);
            if (used < minUsed) c = double.MaxValue;
            costs[s - lo] = c;
            if (c < best) { best = c; bestShift = s; }
        }
        if (best > MatchCost) return null;

        // A list of identical rows fits at every period of it: then nothing says which.
        double second = double.MaxValue;
        for (int s = lo; s <= hi; s++)
            if (Math.Abs(s - bestShift) > 4) second = Math.Min(second, costs[s - lo]);
        return second >= best * 1.5 + 2 ? bestShift : null;
    }

    /// <summary>
    /// The mean difference per cell between the new frame's moving lines and the last frame's
    /// lines <paramref name="shift"/> further on, lines blank in both and cells that stood still
    /// not counted.
    /// </summary>
    private static double Cost(Lines now, Lines last, int head, int tail, int shift, bool[] still, out int used)
    {
        int n = now.Count, k = now.CellsPerLine;
        int from = Math.Max(head, head - shift), to = Math.Min(n - tail, n - tail - shift);
        int cells = 0;
        for (int c = 0; c < k; c++) if (!still[c]) cells++;
        long sum = 0;
        used = 0;
        if (cells == 0) return double.MaxValue;
        for (int r = from; r < to; r++)
        {
            if (LinePlain(now.Plain, r) && LinePlain(last.Plain, r + shift)) continue;
            int a = r * k, b = (r + shift) * k;
            for (int c = 0; c < k; c++)
                if (!still[c]) sum += Math.Abs(now.Profile[a + c] - last.Profile[b + c]);
            used++;
        }
        return used == 0 ? double.MaxValue : (double)sum / (used * cells);
    }

    /// <summary>
    /// The cells across a line that look the same in both frames on nearly every line between the
    /// still ends — a part of the window that does not scroll with the page. All of them false
    /// when that would leave fewer than a quarter of the cells to go by.
    /// </summary>
    private static bool[] StillCells(Lines now, Lines last, int head, int tail)
    {
        int n = now.Count, k = now.CellsPerLine;
        var still = new bool[k];
        int lines = n - head - tail;
        if (lines < 8) return still;
        int kept = 0;
        for (int c = 0; c < k; c++)
        {
            int same = 0;
            for (int r = head; r < n - tail; r++)
                if (Math.Abs(now.Profile[r * k + c] - last.Profile[r * k + c]) <= 1) same++;
            still[c] = same * 20 >= lines * 19;
            if (!still[c]) kept++;
        }
        if (kept * 4 < k) Array.Clear(still);
        return still;
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

    private void Remember(byte[] frame, Lines lines)
    {
        _last = (byte[])frame.Clone();
        _lastLines = lines;
        _lastAxis = Axis;
    }

    private static ulong Key(ulong hash, int lane) => hash ^ ((ulong)(lane + 1) * 0x9E3779B97F4A7C15UL);

    // ---- looking at a frame ----

    private static int Brightness(byte[] f, int i) => (f[i] * 29 + f[i + 1] * 150 + f[i + 2] * 77) >> 8;

    /// <summary>Per row: a hash and plainness per strip, and the mean brightness of each cell across the strips.</summary>
    private Lines RowLines(byte[] frame)
    {
        var hashes = new ulong[FrameHeight * Strips];
        var plain = new bool[FrameHeight * Strips];
        int span = Strips * _stripWidth, k = Math.Min(Cells, span);
        var profile = new int[FrameHeight * k];
        int stride = FrameWidth * 4;
        for (int r = 0; r < FrameHeight; r++)
        {
            for (int s = 0; s < Strips; s++)
            {
                int start = r * stride + (_stripLeft + s * _stripWidth) * 4;
                (hashes[r * Strips + s], plain[r * Strips + s]) = HashRun(frame, start, 4, _stripWidth);
            }
            for (int c = 0; c < k; c++)
            {
                int x0 = _stripLeft + c * span / k, x1 = _stripLeft + (c + 1) * span / k, sum = 0;
                for (int x = x0; x < x1; x++) sum += Brightness(frame, r * stride + x * 4);
                profile[r * k + c] = sum / Math.Max(1, x1 - x0);
            }
        }
        return new Lines(hashes, plain, profile, k, FrameHeight);
    }

    /// <summary>Per column and band, the same.</summary>
    private Lines ColumnLines(byte[] frame)
    {
        var hashes = new ulong[FrameWidth * Strips];
        var plain = new bool[FrameWidth * Strips];
        int span = Strips * _bandHeight, k = Math.Min(Cells, span);
        var profile = new int[FrameWidth * k];
        int stride = FrameWidth * 4;
        for (int col = 0; col < FrameWidth; col++)
        {
            for (int s = 0; s < Strips; s++)
            {
                int start = (_bandTop + s * _bandHeight) * stride + col * 4;
                (hashes[col * Strips + s], plain[col * Strips + s]) = HashRun(frame, start, stride, _bandHeight);
            }
            for (int c = 0; c < k; c++)
            {
                int y0 = _bandTop + c * span / k, y1 = _bandTop + (c + 1) * span / k, sum = 0;
                for (int y = y0; y < y1; y++) sum += Brightness(frame, y * stride + col * 4);
                profile[col * k + c] = sum / Math.Max(1, y1 - y0);
            }
        }
        return new Lines(hashes, plain, profile, k, FrameWidth);
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

    /// <summary>
    /// Gives up every row from <paramref name="y"/> on: they are left out of the picture until
    /// painted again. The pixels stay in the tiles, so a later paint must start at y.
    /// </summary>
    public void TrimBottom(int y)
    {
        if (Any && MaxY > y) MaxY = Math.Max(MinY, y);
    }

    /// <summary>The same for every column from <paramref name="x"/> on.</summary>
    public void TrimRight(int x)
    {
        if (Any && MaxX > x) MaxX = Math.Max(MinX, x);
    }

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
