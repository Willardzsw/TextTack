using System.Windows;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// 表格識別: recognised lines that stand in columns, read as a table — the cells of each row
/// separated by tabs and the rows by line breaks, which is what a spreadsheet takes from the
/// clipboard.
/// </summary>
/// <remarks>
/// <para>No model: the recognizer already says where every line is, and two things make a table
/// of that. Rows are lines side by side. A column boundary is a vertical strip that no line of any
/// row with several lines crosses; it counts when enough rows have text on both sides of it, and
/// the fewer rows vouch for it, the wider it has to be (a narrow gap between two rows is chance,
/// between four it is a column). The idea follows sniplite's ocr_table.c (MIT); the code is ours.</para>
///
/// <para>Text without columns is not a table and gets null, so the caller copies it as before. A
/// cell whose text wraps over two lines becomes two rows here: the rows come from the text alone,
/// not from the grid lines of the picture.</para>
/// </remarks>
internal static class TableLayout
{
    /// <summary>One recognised line: its text and where it is.</summary>
    internal readonly record struct Line(string Text, Rect Bounds);

    /// <summary>The lines as tab-separated rows, or null when they do not form a table.</summary>
    public static string? TryFormat(IReadOnlyList<OcrTextBlock> lines) =>
        TryFormat(lines.Select(l => new Line(l.Text, l.Bounds)).ToList());

    internal static string? TryFormat(IReadOnlyList<Line> input)
    {
        var lines = input
            .Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.Bounds.Width > 0 && l.Bounds.Height > 0)
            .Select(l => l with { Text = l.Text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim() })
            .ToList();
        if (lines.Count < 4) return null;

        var rows = Rows(lines);
        double usual = Median(lines.Select(l => l.Bounds.Height));
        var boundaries = ColumnBoundaries(rows, usual);
        if (boundaries.Count == 0) return null;

        var table = rows.Select(row => Cells(row, boundaries)).ToList();

        // A table has at least two rows that actually use more than one column.
        if (table.Count(cells => cells.Count(c => c.Length > 0) >= 2) < 2) return null;

        return string.Join("\r\n", table.Select(cells => string.Join("\t", cells).TrimEnd('\t')));
    }

    /// <summary>Lines side by side, top to bottom; each row left to right.</summary>
    private static List<List<Line>> Rows(List<Line> lines)
    {
        var rows = new List<List<Line>>();
        var bands = new List<(double Top, double Bottom)>();
        foreach (var line in lines.OrderBy(l => l.Bounds.Top + l.Bounds.Height / 2))
        {
            int found = -1;
            for (int i = rows.Count - 1; i >= 0 && found < 0; i--)
            {
                var (top, bottom) = bands[i];
                double overlap = Math.Min(bottom, line.Bounds.Bottom) - Math.Max(top, line.Bounds.Top);
                double shorter = Math.Min(bottom - top, line.Bounds.Height);
                if (overlap >= shorter * 0.5) found = i;
            }

            if (found < 0)
            {
                rows.Add([line]);
                bands.Add((line.Bounds.Top, line.Bounds.Bottom));
            }
            else
            {
                rows[found].Add(line);
                bands[found] = (Math.Min(bands[found].Top, line.Bounds.Top), Math.Max(bands[found].Bottom, line.Bounds.Bottom));
            }
        }

        foreach (var row in rows) row.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
        return rows;
    }

    /// <summary>The middles of the strips between columns, left to right.</summary>
    private static List<double> ColumnBoundaries(List<List<Line>> rows, double usual)
    {
        // Only rows with several lines decide where the columns are: a title across the whole
        // table, or a note under it, would cover every gap.
        var spans = rows.Where(r => r.Count >= 2)
            .SelectMany(r => r.Select(l => (l.Bounds.Left, l.Bounds.Right)))
            .OrderBy(s => s.Left)
            .ToList();
        if (spans.Count == 0) return [];

        var gaps = new List<(double Left, double Right)>();
        double reach = spans[0].Right;
        foreach (var (left, right) in spans.Skip(1))
        {
            if (left > reach) gaps.Add((reach, left));
            reach = Math.Max(reach, right);
        }

        var boundaries = new List<double>();
        foreach (var (left, right) in gaps)
        {
            int vouching = rows.Count(r =>
                r.Count >= 2 &&
                r.Any(l => l.Bounds.Right <= left) &&
                r.Any(l => l.Bounds.Left >= right));
            if (vouching < 2) continue;

            double need = usual * (vouching >= 4 ? 0.5 : vouching == 3 ? 1.0 : 1.5);
            if (right - left >= need) boundaries.Add((left + right) / 2);
        }
        return boundaries;
    }

    /// <summary>One row's text, one entry per column; lines in the same cell joined by a space.</summary>
    private static string[] Cells(List<Line> row, List<double> boundaries)
    {
        var cells = new List<string>[boundaries.Count + 1];
        for (int i = 0; i < cells.Length; i++) cells[i] = [];

        foreach (var line in row)
        {
            double middle = line.Bounds.Left + line.Bounds.Width / 2;
            int column = 0;
            while (column < boundaries.Count && middle > boundaries[column]) column++;
            cells[column].Add(line.Text);
        }
        return cells.Select(c => string.Join(" ", c)).ToArray();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
