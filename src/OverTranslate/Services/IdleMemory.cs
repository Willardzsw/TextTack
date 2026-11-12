using System.Runtime;

namespace OverTranslate.Services;

/// <summary>
/// Hands memory a finished piece of work left behind back to Windows, once things have gone quiet.
/// </summary>
/// <remarks>
/// A tray program sitting idle allocates nothing, so .NET never has a reason to collect: the
/// full-desktop screenshot of the last capture (two copies, 28 MB each on a 4480x1600 desktop) and
/// the OCR's tensors stay counted against the process for hours. One compacting collection after a
/// capture session ends or an OCR model is let go returns them. Debounced, so a burst of captures
/// pays for one collection at the end rather than one each, and run off the interface thread.
///
/// Off the interface thread, but not without it: a blocking collection stops every thread for as
/// long as it takes, so one that lands on a capture being started or used is a stutter in it. Hence
/// the long wait — a capture is usually followed by another within seconds, not most of a minute —
/// and the collection puts itself off while <see cref="Busy"/> says something is on screen.
/// </remarks>
internal static class IdleMemory
{
    /// <summary>Quiet time before collecting; another request inside it starts the wait again.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Whether something the user is working with would feel the pause (a capture on screen). Asked
    /// on a timer thread; a collection due while it says so waits another <see cref="Delay"/>.
    /// </summary>
    public static Func<bool>? Busy { get; set; }

    private static readonly System.Threading.Timer Timer = new(_ => Collect(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Collects once <see cref="Delay"/> has passed without another request.</summary>
    public static void ReleaseSoon() => Timer.Change(Delay, Timeout.InfiniteTimeSpan);

    private static void Collect()
    {
        if (ShouldWait(Busy))
        {
            ReleaseSoon();
            return;
        }

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        // Native images (WIC bitmaps behind a BitmapSource) are freed by finalizers.
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>Whether <paramref name="busy"/> asks for the collection to wait; one that throws does not.</summary>
    internal static bool ShouldWait(Func<bool>? busy)
    {
        try { return busy?.Invoke() == true; }
        catch (Exception) { return false; }
    }
}
