using System.Runtime.InteropServices;
using NLog;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Services;

/// <summary>What became of an attempt to replace the selection.</summary>
public enum PasteOutcome
{
    /// <summary>The paste was sent. See the remarks on <see cref="SelectionReplacer"/>.</summary>
    Pasted,

    /// <summary>The user moved to another window while the translation was still being fetched.</summary>
    FocusMoved,

    /// <summary>The clipboard could not be borrowed, so nothing was sent.</summary>
    ClipboardUnavailable,
}

/// <summary>
/// Puts text where the user's selection is, in whatever application they are working in.
/// </summary>
/// <remarks>
/// 替換翻譯's second half. <see cref="SelectedTextReader"/> borrows the copy shortcut to find out
/// what is selected; this borrows the paste one to replace it, for the same reason — no process can
/// edit another's text, so the only lever available is the editing shortcut the application already
/// implements for its own user.
///
/// Which is the whole of what this can promise. A paste sent to a read-only surface — a web page, a
/// PDF viewer, a chat transcript — does nothing at all, and the sending application is told nothing
/// about it. So <see cref="PasteOutcome.Pasted"/> means the keystroke went out, never that anything
/// changed on the other end.
/// </remarks>
internal static class SelectionReplacer
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// How long the paste is given before the user's own clipboard goes back on.
    /// </summary>
    /// <remarks>
    /// The keystroke is delivered asynchronously and the application reads the clipboard when it
    /// gets round to handling it, so restoring immediately is a race this would lose — and losing it
    /// means the user's previous clipboard contents land on their selection instead of the
    /// translation, which is worse than any failure this feature could otherwise produce. Long
    /// enough for an application that is merely busy, short enough that the clipboard is not held
    /// while the user moves on to something else.
    /// </remarks>
    private static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Replaces the foreground application's selection with <paramref name="text"/>.
    /// </summary>
    /// <param name="expectedForeground">
    /// The window the selection was read from. A paste is a destructive edit and it lands wherever
    /// the foreground happens to be, so if the user has moved on — to another document, to a chat
    /// box — while the translation was being fetched, it is not sent at all: overwriting a selection
    /// they made somewhere else is not something they could have asked for.
    /// </param>
    public static Task<PasteOutcome> ReplaceSelectionAsync(string text, IntPtr expectedForeground) =>
        ReplaceSelectionAsync(text, expectedForeground, GetForegroundWindow, ClipboardBackup.Capture,
            TryPut, StillOwnsClipboard, () => KeyboardInput.SendControlChordAsync(KeyboardInput.VK_V),
            () => Task.Delay(PasteSettle), ClipboardBackup.Restore);

    /// <summary>
    /// Pastes <paramref name="text"/> after the foreground application's selection, keeping it:
    /// 追加翻譯.
    /// </summary>
    /// <remarks>
    /// Right arrow first, which in an ordinary text field collapses a selection to its end whichever
    /// way it was made; the paste then lands there. Sent with the user's modifiers released, as the
    /// paste is, so a Ctrl or Shift still held from the shortcut cannot turn it into a word jump or a
    /// wider selection. The same foreground checks as a replacement guard both keystrokes.
    /// </remarks>
    public static Task<PasteOutcome> AppendAfterSelectionAsync(string text, IntPtr expectedForeground) =>
        ReplaceSelectionAsync(text, expectedForeground, GetForegroundWindow, ClipboardBackup.Capture,
            TryPut, StillOwnsClipboard, async () =>
            {
                await KeyboardInput.SendKeyAsync(KeyboardInput.VK_RIGHT);
                await KeyboardInput.SendControlChordAsync(KeyboardInput.VK_V);
            },
            () => Task.Delay(PasteSettle), ClipboardBackup.Restore);

    internal static async Task<PasteOutcome> ReplaceSelectionAsync(string text, IntPtr expectedForeground,
        Func<IntPtr> foreground, Func<object?> capture, Func<string, object?> put, Func<object, bool> isCurrent,
        Func<Task> paste, Func<Task> settle, Action<object?> restore)
    {
        if (expectedForeground == IntPtr.Zero || foreground() != expectedForeground)
        {
            Log.Info("替換翻譯 did not paste: the foreground moved to another window while translating");
            return PasteOutcome.FocusMoved;
        }

        object? snapshot;

        try
        {
            snapshot = capture();
        }
        catch (Exception ex)
        {
            // With no snapshot there is nothing to put back, so the clipboard is not taken at all:
            // the user's own contents are worth more than this one translation.
            Log.Info(ex, "替換翻譯 could not snapshot the clipboard, so nothing was pasted");
            return PasteOutcome.ClipboardUnavailable;
        }

        object? borrowed = null;
        try
        {
            // Reading/publishing clipboard formats may block on another process. Focus must be
            // checked again immediately before injecting the destructive paste, not just on entry.
            if (foreground() != expectedForeground) return PasteOutcome.FocusMoved;
            borrowed = put(text);
            if (borrowed is null) return PasteOutcome.ClipboardUnavailable;
            if (foreground() != expectedForeground) return PasteOutcome.FocusMoved;

            await paste();
            await settle();
            return PasteOutcome.Pasted;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "替換翻譯 could not paste the translation");
            return PasteOutcome.ClipboardUnavailable;
        }
        finally
        {
            // A user copy during the settle interval belongs to them. Do not overwrite it with
            // the old snapshot, or restore anything when this call never borrowed the clipboard.
            if (borrowed is not null && isCurrent(borrowed)) restore(snapshot);
        }
    }

    /// <remarks>
    /// WPF already retries for about a second before it throws, so a throw here means the clipboard
    /// stayed locked for that whole second — and a paste sent anyway would put whatever was on it
    /// before over the user's selection.
    ///
    /// Not flushed (copy:false): the translation only has to last until the paste, after which
    /// <see cref="ClipboardBackup.Restore"/> replaces it, so outliving this process is no use. The
    /// flush reopens the clipboard straight after the copy shortcut and the snapshot, and on
    /// Windows 11 a clipboard listener still holding it makes that fail with CLIPBRD_E_CANT_OPEN.
    /// </remarks>
    private static object? TryPut(string text)
    {
        try
        {
            var data = new System.Windows.DataObject();
            data.SetText(text);
            Clipboard.SetDataObject(data, copy: false);
            return data;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "替換翻譯 could not put the translation on the clipboard");
            return null;
        }
    }

    private static bool StillOwnsClipboard(object data)
    {
        try
        {
            // A delayed render can advance the clipboard sequence without changing ownership.
            // IsCurrent identifies our exact DataObject even after the paste has requested text.
            return Clipboard.IsCurrent((System.Windows.IDataObject)data);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not check ownership of the borrowed clipboard");
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

}
