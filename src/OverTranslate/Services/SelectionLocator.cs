using System.Drawing;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Where on the screen the selected text is, for 簡單顯示 to put its label at the selection's
/// top-right corner.
/// </summary>
/// <remarks>
/// <para>Asked of UI Automation: the focused element's text pattern, its selection, and the boxes
/// the selection's lines take up. Word, Notepad, most browser pages and most WPF and WinUI text
/// answer; many others do not (no text pattern, or an empty answer). The caller then falls back to
/// the pointer.</para>
///
/// <para>On a thread of its own and with a time limit: the question crosses into the other process
/// and an application that is busy can take far longer to answer than a label is worth waiting for.
/// A thread left waiting on such an application is a background thread and ends with its query.</para>
/// </remarks>
internal static class SelectionLocator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>How long the answer is waited for before the pointer is used instead.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The box around the selection in the focused element, in physical screen pixels; null when it
    /// cannot be told in time. Start it before anything of ours takes the screen or the focus.
    /// </summary>
    public static Task<Rectangle?> FindAsync()
    {
        var done = new TaskCompletionSource<Rectangle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Read()); }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read where the selection is");
                done.TrySetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "Selection locator",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        return WithTimeout(done.Task);
    }

    private static async Task<Rectangle?> WithTimeout(Task<Rectangle?> read)
    {
        var first = await Task.WhenAny(read, Task.Delay(Timeout)).ConfigureAwait(false);
        if (first == read) return await read.ConfigureAwait(false);
        Log.Debug("Where the selection is was not answered within {Ms} ms", Timeout.TotalMilliseconds);
        return null;
    }

    private static Rectangle? Read()
    {
        var focused = AutomationElement.FocusedElement;
        if (focused is null) return null;
        if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;

        TextPatternRange[] ranges = ((TextPattern)pattern).GetSelection();
        var boxes = new List<Rectangle>();
        foreach (var range in ranges)
        {
            foreach (var r in range.GetBoundingRectangles())
            {
                if (r.IsEmpty || double.IsInfinity(r.Width) || double.IsInfinity(r.Height)) continue;
                boxes.Add(Rectangle.FromLTRB(
                    (int)Math.Floor(r.Left), (int)Math.Floor(r.Top),
                    (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom)));
            }
        }
        return SelectionLabelPlacement.Union(boxes);
    }
}

/// <summary>Where a 簡單顯示 label goes. Kept apart from the window so it can be tested.</summary>
internal static class SelectionLabelPlacement
{
    /// <summary>
    /// The box around all of <paramref name="lineBoxes"/>, or null when there are none worth using —
    /// a selection with no extent, which some applications report for a caret.
    /// </summary>
    public static Rectangle? Union(IReadOnlyList<Rectangle> lineBoxes)
    {
        var usable = lineBoxes.Where(b => b.Width >= 1 && b.Height >= 1).ToList();
        if (usable.Count == 0) return null;
        var all = usable[0];
        foreach (var b in usable.Skip(1)) all = Rectangle.Union(all, b);
        return all;
    }

    /// <summary>
    /// The label's top-left corner, in physical pixels: its bottom-left just above and to the right
    /// of the selection's top-right corner — or of the pointer, when where the selection is is not
    /// known. Where there is no room above, it goes below instead; it is always kept inside
    /// <paramref name="area"/> (the monitor's working area).
    /// </summary>
    /// <param name="selection">The selection's box, or null to go by <paramref name="pointer"/>.</param>
    /// <param name="gap">Space between the label and what it points at.</param>
    public static Point Place(Rectangle? selection, Point pointer, Size label, Rectangle area, int gap)
    {
        // Above: the label's bottom edge sits gap above the corner. Below: the selection's bottom
        // edge, or far enough under the pointer to clear the cursor arrow.
        int anchorX, aboveBottom, belowTop;
        if (selection is { } s)
        {
            anchorX = s.Right;
            aboveBottom = s.Top - gap;
            belowTop = s.Bottom + gap;
        }
        else
        {
            anchorX = pointer.X;
            aboveBottom = pointer.Y - gap;
            belowTop = pointer.Y + gap * 3;
        }

        int left = anchorX + gap;
        int top = aboveBottom - label.Height;
        if (top < area.Top) top = belowTop;

        left = Math.Max(area.Left, Math.Min(left, area.Right - label.Width));
        top = Math.Max(area.Top, Math.Min(top, area.Bottom - label.Height));
        return new Point(left, top);
    }

    /// <summary>
    /// Whether a box reported for the selection is believable: on the desktop, and not so large it
    /// must be the whole document rather than what is selected in it.
    /// </summary>
    public static bool IsPlausible(Rectangle box, Rectangle desktop) =>
        box.Width > 0 && box.Height > 0 && box.IntersectsWith(desktop) &&
        box.Height <= desktop.Height && box.Width <= desktop.Width;
}
