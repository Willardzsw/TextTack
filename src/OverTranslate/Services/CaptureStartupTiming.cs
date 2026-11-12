using System.Diagnostics;
using System.Globalization;

namespace OverTranslate.Services;

/// <summary>
/// How long each step between the capture shortcut and the frozen desktop appearing took, written
/// to the log as one line per capture so a slow start can be traced to the step that was slow.
/// The way out of a capture (copy, close) is timed with it as well.
/// </summary>
/// <remarks>Used on the UI thread only.</remarks>
internal sealed class CaptureStartupTiming
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(string Step, double Ms)> _steps = [];
    private readonly double _queuedMs;
    private double _last;

    /// <param name="queuedMs">
    /// How long the shortcut's message waited before the UI thread got to it, when known: the start
    /// counts from the key press, not from the handler.
    /// </param>
    public CaptureStartupTiming(double? queuedMs = null)
    {
        if (queuedMs is not { } q) return;
        _queuedMs = Math.Max(0, q);
        _steps.Add(("queued", _queuedMs));
        _last = _queuedMs;
    }

    /// <summary>Milliseconds since the shortcut was pressed (or handled, when that is not known).</summary>
    public double ElapsedMs => _queuedMs + _clock.Elapsed.TotalMilliseconds;

    /// <summary>Records the time since the previous step (or the start) under <paramref name="step"/>.</summary>
    public void Mark(string step)
    {
        double now = ElapsedMs;
        _steps.Add((step, now - _last));
        _last = now;
    }

    /// <summary>"total 123 ms: step 4, step 56, …".</summary>
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture,
            $"total {_last:F0} ms: {string.Join(", ", _steps.Select(s => $"{s.Step} {s.Ms:F0}"))}");
}
