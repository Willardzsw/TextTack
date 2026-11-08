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
/// </remarks>
internal static class IdleMemory
{
    /// <summary>Quiet time before collecting; another request inside it starts the wait again.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);

    private static readonly System.Threading.Timer Timer = new(_ => Collect(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Collects once <see cref="Delay"/> has passed without another request.</summary>
    public static void ReleaseSoon() => Timer.Change(Delay, Timeout.InfiniteTimeSpan);

    private static void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        // Native images (WIC bitmaps behind a BitmapSource) are freed by finalizers.
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
