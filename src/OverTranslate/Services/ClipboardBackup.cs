using System.Runtime.InteropServices;
using NLog;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Services;

/// <summary>
/// The clipboard's contents, taken away and put back.
/// </summary>
/// <remarks>
/// Two features borrow the clipboard because Windows offers no other way to reach into the
/// application the user is reading: <see cref="SelectedTextReader"/> copies out of it, and
/// <see cref="SelectionReplacer"/> pastes into it. Neither was asked to change what the user has
/// on their clipboard, so both bracket the borrowing with this — a snapshot before, and the same
/// contents back afterwards whether or not anything worked.
///
/// A failure anywhere leaves the caller with an empty snapshot rather than an exception. The
/// clipboard is one system-wide resource and any process can have it locked; that is a state to
/// give up on, not to crash over.
/// </remarks>
internal static class ClipboardBackup
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Keeps the managed data object alive while this process owns the restored clipboard.
    /// </summary>
    private static System.Windows.DataObject? _restoredOwner;

    /// <summary>
    /// Everything the clipboard holds, in a form <see cref="Restore"/> can put back.
    /// </summary>
    /// <remarks>
    /// Format by format rather than by handing the live data object back later: the object the
    /// clipboard gives out is owned by whichever process put it there, and by the time it would be
    /// restored that process may have exited and taken its handles with it.
    ///
    /// A format that will not come out is skipped rather than failing the snapshot. The alternative
    /// is to abandon the whole operation whenever anything exotic is on the clipboard, which in
    /// practice means abandoning it for anyone who has recently copied out of Office.
    /// </remarks>
    public static object? Capture()
    {
        if (HeldByAGoneProcess())
        {
            // Reading it would be an OLE call into a process that no longer exists, which Windows
            // only gives up on after about twenty seconds — with the UI thread stuck the whole time
            // (seen 2026-11-23: the first 替換翻譯 after restarting TextTack hung). There is nothing
            // to save anyway; restoring "nothing" clears the dead entry, which also unsticks every
            // other program's paste.
            Log.Info("The clipboard belongs to a process that has exited; it is not read");
            return null;
        }

        var source = Clipboard.GetDataObject();
        if (source is null) return null;

        var copy = new System.Windows.DataObject();
        var kept = 0;

        foreach (var format in source.GetFormats())
        {
            try
            {
                var data = source.GetData(format);
                if (data is null) continue;
                copy.SetData(format, data);
                kept++;
            }
            catch
            {
                // See the remarks: one unreadable format is not a reason to lose the others.
            }
        }

        return kept > 0 ? copy : null;
    }

    /// <summary>
    /// Whether the clipboard was published through OLE by a process whose clipboard window is gone,
    /// so that any read of it waits for an RPC timeout.
    /// </summary>
    private static bool HeldByAGoneProcess()
    {
        try
        {
            if (OlePrivateData == 0 || !IsClipboardFormatAvailable(OlePrivateData)) return false;
            var owner = GetClipboardOwner();
            return owner == IntPtr.Zero || !IsWindow(owner);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Called on exit. If the clipboard still holds a snapshot this process put back, it is only
    /// lent out through OLE (see <see cref="Restore"/>), and once the process is gone every program
    /// that pastes would wait out a timeout. So the plain parts of it — text of every kind, streams,
    /// pictures — are written onto the clipboard for real, and anything else is let go.
    /// </summary>
    public static void HandOverOnExit()
    {
        var owner = _restoredOwner;
        if (owner is null) return;

        try
        {
            if (!Clipboard.IsCurrent(owner)) return;

            var plain = new System.Windows.DataObject();
            var kept = 0;
            foreach (var format in owner.GetFormats(false))
            {
                try
                {
                    var data = owner.GetData(format, false);
                    if (data is string or System.IO.MemoryStream or System.Windows.Media.Imaging.BitmapSource)
                    {
                        plain.SetData(format, data, false);
                        kept++;
                    }
                }
                catch
                {
                    // One format that will not come out is not a reason to lose the rest.
                }
            }

            if (kept > 0) Clipboard.SetDataObject(plain, copy: true);
            else Clipboard.Clear();
            _restoredOwner = null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not hand the restored clipboard over on exit");
        }
    }

    private static readonly uint OlePrivateData = RegisterClipboardFormat("Ole Private Data");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    /// <remarks>
    /// An empty snapshot clears the clipboard rather than leaving the borrowed text on it. The user
    /// never asked for that text to be on their clipboard, and leaving it there is a surprise they
    /// would find later, in whatever they next paste into.
    /// </remarks>
    public static void Restore(object? snapshot)
    {
        try
        {
            if (snapshot is System.Windows.DataObject data)
            {
                // copy:true calls OleFlushClipboard after publishing every format. Some third-party
                // clipboard data providers make that native call access-violate inside ole32.dll;
                // a corrupted-state exception cannot be caught here and terminates the process.
                // Keep the data object alive instead so OLE can request its formats lazily without
                // taking that unsafe flush path.
                Clipboard.SetDataObject(data, copy: false);
                _restoredOwner = data;
            }
            else
            {
                Clipboard.Clear();
                _restoredOwner = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not restore the clipboard after borrowing it");
        }
    }
}
