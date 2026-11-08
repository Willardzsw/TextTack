using System.IO;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>便利貼: the archive log's format and numbering, settings, and the shortcut's place.</summary>
public class StickyNoteTests
{
    [Fact]
    public void ArchiveLine_IsNumberDateTimeText_WithLineBreaksAsArrows()
    {
        var when = new DateTime(2026, 11, 7, 10, 30, 15);

        Assert.Equal("12 2026-11-07 10:30:15 第一行 ↵ 第二行", StickyNoteArchive.FormatLine(12, when, "第一行\r\n第二行"));
        Assert.Equal("1 2026-11-07 10:30:15 a ↵ ↵ b", StickyNoteArchive.FormatLine(1, when, "a  \n\nb"));
        Assert.Equal("ข้อความ", StickyNoteArchive.OneLine("ข้อความ"));
    }

    [Fact]
    public void Archive_AppendsOneLinePerSave_AndKeepsCounting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tt-notes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var when = new DateTime(2026, 1, 2, 3, 4, 5);
            StickyNoteArchive.Append(folder, "one", when);
            StickyNoteArchive.Append(folder, "two\nlines", when);
            StickyNoteArchive.Append(folder, "three", when);

            var lines = File.ReadAllLines(Path.Combine(folder, StickyNoteArchive.FileName));
            Assert.Equal(
                new[] { "1 2026-01-02 03:04:05 one", "2 2026-01-02 03:04:05 two ↵ lines", "3 2026-01-02 03:04:05 three" },
                lines);
            Assert.Equal(4, StickyNoteArchive.NextNumber(Path.Combine(folder, StickyNoteArchive.FileName)));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DefaultFolder_MovesTheFolderFromBeforeTheRename()
    {
        var documents = Path.Combine(Path.GetTempPath(), "tt-docs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(Path.Combine(documents, "TextTack"), StickyNoteArchive.ResolveDefaultFolder(documents));

            var legacy = Directory.CreateDirectory(Path.Combine(documents, "TT Tools")).FullName;
            File.WriteAllText(Path.Combine(legacy, StickyNoteArchive.FileName), "1 2026-01-02 03:04:05 old");

            var folder = StickyNoteArchive.ResolveDefaultFolder(documents);

            Assert.Equal(Path.Combine(documents, "TextTack"), folder);
            Assert.False(Directory.Exists(legacy));
            Assert.Equal("1 2026-01-02 03:04:05 old", File.ReadAllText(Path.Combine(folder, StickyNoteArchive.FileName)));
        }
        finally
        {
            if (Directory.Exists(documents)) Directory.Delete(documents, recursive: true);
        }
    }

    [Fact]
    public void NextNumber_StartsAtOne_ForAMissingOrEmptyLog()
    {
        var path = Path.Combine(Path.GetTempPath(), "tt-missing-" + Guid.NewGuid().ToString("N") + ".log");
        Assert.Equal(1, StickyNoteArchive.NextNumber(path));
    }

    [Fact]
    public void Defaults_F2_SmallWidth_ArchiveOn_InDocuments()
    {
        var s = new AppSettings();

        Assert.Equal(0x71u, s.StickyNoteHotkeyVirtualKey);
        Assert.Equal(0u, s.StickyNoteHotkeyModifiers);
        Assert.Equal("F2", s.StickyNoteHotkeyDisplay);
        Assert.True(s.StickyNoteHotkeyEnabled);
        Assert.Equal(200, s.StickyNote.Width);
        Assert.True(s.StickyNote.ArchiveEnabled);
        Assert.Equal(StickyNoteArchive.DefaultFolder, StickyNoteArchive.FolderFor(s.StickyNote));
        Assert.True(HotkeyBindings.Resolve(s).Single(b => b.Action == HotkeyAction.StickyNote).IsActive);
    }

    [Fact]
    public void NewBlankNote_DefaultsToAltF2_AndFollowsTheStickyNoteSwitch()
    {
        var s = new AppSettings();

        Assert.Equal(0x71u, s.StickyNoteNewHotkeyVirtualKey);
        Assert.Equal(1u, s.StickyNoteNewHotkeyModifiers);
        Assert.Equal("Alt+F2", s.StickyNoteNewHotkeyDisplay);
        Assert.True(HotkeyBindings.Resolve(s).Single(b => b.Action == HotkeyAction.StickyNoteNew).IsActive);

        FeatureToggles.Set(s, FeatureToggle.StickyNoteHotkey, false);
        Assert.False(HotkeyBindings.Resolve(s).Single(b => b.Action == HotkeyAction.StickyNoteNew).Enabled);
    }

    [Fact]
    public void NewBlankNote_RebindingRoundTripsThroughTheFile()
    {
        var s = new AppSettings
        {
            StickyNoteNewHotkeyModifiers = 3,
            StickyNoteNewHotkeyVirtualKey = 0x4E,
            StickyNoteNewHotkeyDisplay = "Ctrl+Alt+N",
        };

        var back = SettingsService.Parse(SettingsService.Serialize(s));

        Assert.Equal(ShortcutTrigger.Keyboard(3, 0x4E), HotkeyBindings.TriggerFor(back, HotkeyAction.StickyNoteNew));
        Assert.Equal("Ctrl+Alt+N", back.StickyNoteNewHotkeyDisplay);
    }

    [Fact]
    public void Settings_RoundTripThroughTheFile()
    {
        var s = new AppSettings();
        s.StickyNote.PaperColor = "#FFD9E2";
        s.StickyNote.TapeColor = "#123456";
        s.StickyNote.OcrLanguage = "TH";
        s.StickyNote.ArchiveFolder = @"D:\Notes";
        s.StickyNoteHotkeyEnabled = false;

        var back = SettingsService.Parse(SettingsService.Serialize(s));

        Assert.Equal("#FFD9E2", back.StickyNote.PaperColor);
        Assert.Equal("#123456", back.StickyNote.TapeColor);
        Assert.Equal("TH", back.StickyNote.OcrLanguage);
        Assert.Equal(@"D:\Notes", StickyNoteArchive.FolderFor(back.StickyNote));
        Assert.False(back.StickyNoteHotkeyEnabled);
    }

    [Fact]
    public void NoteColours_FollowTheDefaultUntilSet_AndRoundTrip()
    {
        var note = new StickyNoteData();
        Assert.Null(note.PaperColor);
        Assert.Null(note.TapeColor);

        note.PaperColor = "#D9F2CF";
        var json = System.Text.Json.JsonSerializer.Serialize(note);
        var back = System.Text.Json.JsonSerializer.Deserialize<StickyNoteData>(json)!;
        Assert.Equal("#D9F2CF", back.PaperColor);
        Assert.Null(back.TapeColor);

        var old = System.Text.Json.JsonSerializer.Deserialize<StickyNoteData>("""{"Text":"x"}""")!;
        Assert.Null(old.PaperColor);
    }

    [Fact]
    public void Colors_FallBackWhenUnreadable()
    {
        var fallback = StickyNoteColors.Parse(null, StickyNoteColors.DefaultPaper);
        Assert.Equal(fallback, StickyNoteColors.Parse("not a colour", StickyNoteColors.DefaultPaper));
        Assert.Equal("#FBF1A9", StickyNoteColors.ToHex(fallback));
    }

    [Fact]
    public void TrayMenu_OffersTheStickyNoteSwitch()
    {
        var s = new AppSettings();
        Assert.Contains(FeatureToggle.StickyNoteHotkey, FeatureToggles.Shown(s));
        FeatureToggles.Set(s, FeatureToggle.StickyNoteHotkey, false);
        Assert.False(s.StickyNoteHotkeyEnabled);
    }
}
