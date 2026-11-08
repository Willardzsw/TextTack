using System.Globalization;
using System.IO;
using System.Text;

namespace OverTranslate.Models;

/// <summary>
/// 便利貼設定: how new notes look and what they do. The shortcut that makes one is with the other
/// shortcuts, flat in <see cref="AppSettings"/>.
/// </summary>
public class StickyNoteSettings
{
    /// <summary>A new note's width in DIP. Its height follows the text.</summary>
    public double Width { get; set; } = 200;

    public double FontSize { get; set; } = 14;

    /// <summary>A new note's opacity, 0.4–1. Each note keeps its own once made.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>The paper, as #RRGGBB.</summary>
    public string PaperColor { get; set; } = StickyNoteColors.DefaultPaper;

    /// <summary>The tape across the top, as #RRGGBB. Drawn semi-transparent.</summary>
    public string TapeColor { get; set; } = StickyNoteColors.DefaultTape;

    /// <summary>
    /// What a copied picture is read as. Its own choice rather than the capture page's: AUTO reads
    /// only Chinese, Japanese, Korean and English, and Thai has to be asked for by name.
    /// </summary>
    public string OcrLanguage { get; set; } = LanguageData.DefaultOcrSourceLanguage;

    /// <summary>Whether every saved note is also written to the archive log.</summary>
    public bool ArchiveEnabled { get; set; } = true;

    /// <summary>The archive log's folder; empty means <see cref="StickyNoteArchive.DefaultFolder"/>.</summary>
    public string ArchiveFolder { get; set; } = "";
}

/// <summary>One note as it is kept between runs.</summary>
public class StickyNoteData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";

    /// <summary>Top-left of the window, in physical pixels.</summary>
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Set once the user has resized it; null means the default width and a height that follows the text.</summary>
    public double? Width { get; set; }
    public double? Height { get; set; }

    public bool Topmost { get; set; } = true;
    public bool PassThrough { get; set; }

    /// <summary>This note's opacity, 0.4–1; set from the 便利貼設定 default when it is made.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>This note's own paper colour (#RRGGBB), or null to follow the 便利貼設定 one.</summary>
    public string? PaperColor { get; set; }

    /// <summary>This note's own tape colour (#RRGGBB), or null to follow the 便利貼設定 one.</summary>
    public string? TapeColor { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Saved { get; set; } = DateTime.Now;
}

public static class StickyNoteColors
{
    public const string DefaultPaper = "#FBF1A9";
    public const string DefaultTape = "#9DBBD6";

    public static readonly string[] Papers = ["#FBF1A9", "#FFD9E2", "#D9F2CF", "#D6EAFB", "#FFE3C2", "#FFFFFF"];
    public static readonly string[] Tapes = ["#9DBBD6", "#E9A9B9", "#A9D49F", "#E3CF7D", "#B9B9B9", "#C3B2E0"];

    /// <summary>Reads #RRGGBB (or #AARRGGBB), falling back to <paramref name="fallback"/>.</summary>
    public static System.Windows.Media.Color Parse(string? value, string fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                System.Windows.Media.ColorConverter.ConvertFromString(value) is System.Windows.Media.Color c)
                return c;
        }
        catch (FormatException) { }
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback);
    }

    public static string ToHex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>
/// The archive log: one line per save, "序號 日期時間 內容", and nothing else. Never trimmed.
/// </summary>
public static class StickyNoteArchive
{
    public const string FileName = "StickyNotes.log";

    /// <summary>
    /// Documents\TextTack. The folder from before the rename (Documents\TT Tools) is moved here the
    /// first time it is needed; if that move fails, the old folder keeps being used so no note is lost.
    /// </summary>
    public static string DefaultFolder => DefaultFolderLazy.Value;

    private static readonly Lazy<string> DefaultFolderLazy = new(() =>
        ResolveDefaultFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));

    internal static string ResolveDefaultFolder(string documents)
    {
        var current = Path.Combine(documents, "TextTack");
        var legacy = Path.Combine(documents, "TT Tools");
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
        try
        {
            Directory.Move(legacy, current);
            return current;
        }
        catch
        {
            return legacy;
        }
    }

    public static string FolderFor(StickyNoteSettings s) =>
        string.IsNullOrWhiteSpace(s.ArchiveFolder) ? DefaultFolder : s.ArchiveFolder;

    /// <summary>The note's text on one line: every line break becomes " ↵ ".</summary>
    public static string OneLine(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new StringBuilder(lines[0].TrimEnd());
        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.TrimEnd();
            result.Append(trimmed.Length == 0 ? " ↵" : " ↵ " + trimmed);
        }
        return result.ToString();
    }

    public static string FormatLine(int number, DateTime when, string text) =>
        $"{number} {when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {OneLine(text)}";

    /// <summary>The number the next line gets: one more than the last line's, or 1.</summary>
    public static int NextNumber(string path)
    {
        if (!File.Exists(path)) return 1;
        string? last = null;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
            if (!string.IsNullOrWhiteSpace(line)) last = line;
        if (last is null) return 1;
        int space = last.IndexOf(' ');
        return space > 0 && int.TryParse(last[..space], out int n) ? n + 1 : 1;
    }

    /// <summary>Appends one line for <paramref name="text"/>. Returns the line written.</summary>
    public static string Append(string folder, string text, DateTime when)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var line = FormatLine(NextNumber(path), when, text);
        File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        return line;
    }
}
