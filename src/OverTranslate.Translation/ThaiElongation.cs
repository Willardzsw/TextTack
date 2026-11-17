using System.Text.RegularExpressions;

namespace OverTranslate.Translation;

/// <summary>
/// Thai stretched for emphasis — ได้ written ไดดดดดด้, มาก written มากกกกก — brought back to the word.
/// </summary>
/// <remarks>
/// <para>Thai chat lengthens a word by repeating its last consonant, as English writes "sooo". A
/// translator does not read that as the word: measured on gpt-6-luna, ไดดดด…ด้ came back copied
/// unchanged six times in ten and four times in ten as 哒哒哒… with no end, until the request
/// timed out; ได้ came back as 可以 every time.</para>
///
/// <para>Only a Thai consonant repeated three or more times in a row is collapsed, to one, and any
/// tone mark or vowel after the last of them stays. No Thai word has the same consonant three times
/// in a row, so nothing that is not stretching is touched. Digits (555, Thai laughter), vowels and
/// other scripts are left alone.</para>
/// </remarks>
public static class ThaiElongation
{
    // ก (U+0E01) to ฮ (U+0E2E): the consonants.
    private static readonly Regex Stretched = new(
        @"([ก-ฮ])\1{2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary><paramref name="text"/> with stretched consonants collapsed; the same instance when there are none.</summary>
    public static string Collapse(string text) =>
        text.Length < 3 || !Stretched.IsMatch(text) ? text : Stretched.Replace(text, "$1");
}
