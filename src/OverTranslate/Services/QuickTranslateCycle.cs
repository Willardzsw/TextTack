using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// 替換翻譯's language switch: pressing its shortcut (or 追加翻譯's) twice in quick succession moves
/// the target language on to the next of the ones the user picked for it.
/// </summary>
/// <remarks>
/// Only the target changes; the source stays whatever it is (自動 by default). Off while 雙語互譯 is
/// on, because there the direction is chosen per selection and a fixed target means nothing, and off
/// with fewer than two languages picked, where every press is an ordinary one as before.
/// </remarks>
public static class QuickTranslateCycle
{
    /// <summary>The most languages the switch goes round.</summary>
    public const int MaxLanguages = 5;

    /// <summary>How close the second press has to follow the first to count as a double press.</summary>
    public static readonly TimeSpan DoublePressWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>The picked languages that are still known target languages, in order, without repeats.</summary>
    public static List<string> Languages(QuickTranslateSettings settings)
    {
        var result = new List<string>();
        foreach (var code in settings.CycleLanguages ?? [])
        {
            var known = LanguageData.TargetLanguages.FirstOrDefault(l =>
                l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Code;
            if (known == null || result.Contains(known)) continue;
            result.Add(known);
            if (result.Count == MaxLanguages) break;
        }
        return result;
    }

    /// <summary>Whether a double press switches the language rather than translating twice.</summary>
    public static bool IsActive(QuickTranslateSettings settings) =>
        !settings.BilingualEnabled && Languages(settings).Count >= 2;

    /// <summary>
    /// The language after <paramref name="current"/>, or the first one when the current target is not
    /// among them (it was set from the settings page to something else).
    /// </summary>
    public static string Next(IReadOnlyList<string> languages, string? current)
    {
        if (languages.Count == 0) throw new ArgumentException("No languages to switch between.", nameof(languages));

        for (var i = 0; i < languages.Count; i++)
            if (languages[i].Equals(current, StringComparison.OrdinalIgnoreCase))
                return languages[(i + 1) % languages.Count];

        return languages[0];
    }
}

/// <summary>
/// Tells a double press of one shortcut from two single ones. Times are in milliseconds from any
/// fixed point; pure, so it can be tested without a clock.
/// </summary>
/// <remarks>
/// A double press is used up by being recognised: a third press right after it starts over as a
/// first press rather than pairing with the second. Presses of the other shortcut (替換翻譯 against
/// 追加翻譯) never pair with each other.
/// </remarks>
public sealed class DoublePressDetector
{
    private long? _firstAt;
    private bool _firstAppend;

    /// <summary>Records a press; true when it completes a double press.</summary>
    public bool Register(long nowMs, bool append, long windowMs)
    {
        if (_firstAt is { } first && _firstAppend == append && nowMs - first <= windowMs)
        {
            _firstAt = null;
            return true;
        }

        _firstAt = nowMs;
        _firstAppend = append;
        return false;
    }

    /// <summary>Forgets the pending first press.</summary>
    public void Reset() => _firstAt = null;
}
