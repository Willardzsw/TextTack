using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Views.StickyNote;

/// <summary>
/// Every 便利貼 on the desktop: making them, keeping them between runs, writing the archive log,
/// and the pass-through way back. The 便利貼 page lists what is here.
/// </summary>
internal static class StickyNoteManager
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly List<StickyNoteWindow> Open = [];
    private static DispatcherTimer? _saveTimer;
    private static bool _restored;
    private static bool _shuttingDown;

    /// <summary>Where the notes are kept between runs: beside the settings, in the profile.</summary>
    internal static string StorePath => Path.Combine(AppDataPaths.Root, "sticky-notes.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A note was made, changed, moved to the top or bottom, or deleted.</summary>
    public static event EventHandler? Changed;

    /// <summary>The notes on screen, newest save first.</summary>
    public static IReadOnlyList<StickyNoteWindow> Notes => Open.OrderByDescending(n => n.Data.Saved).ToList();

    public static StickyNoteSettings Settings => SettingsService.Instance.Current.StickyNote ??= new StickyNoteSettings();

    internal static void RaiseChanged()
    {
        Changed?.Invoke(null, EventArgs.Empty);
        ScheduleSave();
    }

    // ── Keeping them between runs ────────────────────────────────────────────

    /// <summary>Puts back the notes there were when the program last closed. Once per run.</summary>
    public static void RestoreAll()
    {
        if (_restored) return;
        _restored = true;
        if (System.Windows.Application.Current is { } app)
            app.SessionEnding += (_, _) => Shutdown();
        try
        {
            if (!File.Exists(StorePath)) return;
            var notes = JsonSerializer.Deserialize<List<StickyNoteData>>(File.ReadAllText(StorePath)) ?? [];
            foreach (var data in notes) Show(data, startEditing: false);
            Log.Info("Restored {Count} sticky notes", notes.Count);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not restore the sticky notes");
        }
    }

    public static void ScheduleSave()
    {
        _saveTimer ??= NewSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static DispatcherTimer NewSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveAll();
        };
        return timer;
    }

    /// <summary>Saves every note for next time and stops recording changes. Called as the program closes.</summary>
    public static void Shutdown()
    {
        if (_shuttingDown) return;
        SaveAll();
        _shuttingDown = true;
    }

    /// <summary>Writes every note to the profile now.</summary>
    public static void SaveAll()
    {
        _saveTimer?.Stop();
        if (!_restored || _shuttingDown) return; // never overwrite notes that were not read back yet
        try
        {
            Directory.CreateDirectory(AppDataPaths.Root);
            var json = JsonSerializer.Serialize(Open.Select(n => n.Data).ToList(), JsonOptions);
            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not save the sticky notes");
        }
    }

    // ── Making one ───────────────────────────────────────────────────────────

    private static StickyNoteWindow Show(StickyNoteData data, bool startEditing)
    {
        var note = new StickyNoteWindow(data, startEditing);
        Open.Add(note);
        TopmostKeeper.Register(note);
        note.Closed += (_, _) =>
        {
            // Windows closing because the program is: the notes stay for next time.
            if (_shuttingDown) return;
            Open.Remove(note);
            TopmostKeeper.Unregister(note);
            RaiseChanged();
        };
        note.Show();
        return note;
    }

    /// <summary>新建便利貼 (Alt+F2, or the tray menu): a blank note under the pointer, ready to type into.</summary>
    public static StickyNoteWindow CreateBlank() => Create("", startEditing: true);

    /// <summary>A new note under the pointer (or where given), saved to the archive if it has text.</summary>
    public static StickyNoteWindow Create(string text, bool startEditing, System.Drawing.Point? at = null)
    {
        _restored = true;
        var point = at ?? System.Windows.Forms.Cursor.Position;
        var data = new StickyNoteData
        {
            Text = text.TrimEnd(),
            X = point.X,
            Y = point.Y,
            Opacity = Math.Clamp(Settings.Opacity, 0.4, 1),
        };
        var note = Show(data, startEditing);
        if (!string.IsNullOrWhiteSpace(data.Text)) Archive(data.Text);
        RaiseChanged();
        return note;
    }

    /// <summary>
    /// The 便利貼 shortcut: copied text becomes a note; a copied picture is read with the 便利貼
    /// recognition language and opened for checking; nothing on the clipboard gives a blank note to
    /// type into.
    /// </summary>
    public static async Task CreateFromClipboardAsync(Action<string> notify)
    {
        string? text = null;
        IReadOnlyList<System.Windows.Media.Imaging.BitmapSource> pictures = [];
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) pictures = Pin.ClipboardPictures.Read();
        }
        catch (Exception ex)
        {
            // The clipboard is shared with every other program and can be locked for a moment.
            Log.Warn(ex, "Reading the clipboard for a sticky note failed");
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            Create(text.Length > 20000 ? text[..20000] : text, startEditing: false);
            return;
        }

        if (pictures.Count == 0)
        {
            Create("", startEditing: true);
            return;
        }

        var cursor = System.Windows.Forms.Cursor.Position;
        var note = Create("", startEditing: false, cursor);
        note.ShowBusy(LocalizationService.Get("S.Note.Recognising"));
        try
        {
            var read = await RecognizeAsync(pictures, Settings.OcrLanguage);
            if (string.IsNullOrWhiteSpace(read))
            {
                note.ShowBusy(null);
                notify(LocalizationService.Get("S.Note.NothingRead"));
                note.BeginEdit();
                return;
            }

            note.ShowBusy(null);
            note.SetText(read);
            Archive(read);
            note.BeginEdit();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Reading the copied picture for a sticky note failed");
            note.ShowBusy(null);
            notify(LocalizationService.Format("S.Note.ReadFailed", ex.Message));
            note.BeginEdit();
        }
    }

    /// <summary>
    /// The CPU stand-in for the configured recognizer: the same models, on the CPU.
    /// </summary>
    /// <remarks>
    /// Measured on this project's test machine, copied-picture sizes: CPU 0.04–0.3 s at any size;
    /// RTX 4060 0.5 s for a size it has not seen yet, 0.05 s after; Intel Iris Xe about 3.5 s for
    /// every new size even when warm, and 33 s for its very first run. A copied picture is a new
    /// size almost every time, so on a GPU that is slow to prepare the CPU joins in — see
    /// <see cref="RecognizeOneAsync"/>.
    /// </remarks>
    private static readonly Lazy<OcrService> CpuOcr = new(() => new OcrService(() =>
    {
        var settings = SettingsService.Instance.Current.OcrModels?.Copy() ?? new OcrModelSettings();
        settings.Device = "cpu";
        return settings;
    }));

    /// <summary>How long the configured device has before the CPU starts on the same picture.</summary>
    internal static readonly TimeSpan CpuJoinsAfter = TimeSpan.FromSeconds(3);

    /// <summary>How long a note waits for the picture to be read before giving up.</summary>
    internal static readonly TimeSpan RecognitionLimit = TimeSpan.FromSeconds(30);

    /// <summary>How long after startup the GPU recognizer is warmed up.</summary>
    internal static readonly TimeSpan WarmUpDelay = TimeSpan.FromSeconds(60);

    private static bool ConfiguredIsCpu =>
        string.Equals(SettingsService.Instance.Current.OcrModels?.Device ?? "cpu", "cpu", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the configured GPU recognizer once, in the background, a minute after the program
    /// starts — only its first run is slow (models built for the GPU; 33 s on an Intel Iris Xe),
    /// and the driver keeps what it built for the next time.
    /// </summary>
    /// <remarks>
    /// Not on the CPU: it loads in about 0.13 s, and the engine lets go of any model after a minute
    /// unused anyway, so warming it only cost memory and a burst of work while Windows was still
    /// starting up. Not straight after startup either, for that same burst.
    /// </remarks>
    public static void WarmUp()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(WarmUpDelay);
            if (ConfiguredIsCpu) return;
            await Warm(AppServices.Ocr, Settings.OcrLanguage, "configured");
        });

        static async Task Warm(OcrService service, string language, string name)
        {
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                using var sample = WarmUpSample();
                await service.RecognizeAsync(sample, language);
                Log.Info("Sticky-note OCR warmed up ({Name}) in {Ms} ms", name, started.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Info(ex, "Sticky-note OCR warm-up ({Name}) failed", name);
            }
        }
    }

    /// <summary>A small line of text, so the warm-up runs the recognizer as well as the detector.</summary>
    private static System.Drawing.Bitmap WarmUpSample()
    {
        var bitmap = new System.Drawing.Bitmap(320, 64);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.White);
        using var font = new System.Drawing.Font("Segoe UI", 20);
        g.DrawString("Note 123", font, System.Drawing.Brushes.Black, 8, 12);
        return bitmap;
    }

    private static async Task<string> RecognizeAsync(
        IReadOnlyList<System.Windows.Media.Imaging.BitmapSource> pictures, string language)
    {
        var parts = new List<string>();
        foreach (var picture in pictures)
        {
            var blocks = await RecognizeOneAsync(picture, language);
            var text = string.Join("\n", blocks.Select(b => b.Text.Trim()).Where(t => t.Length > 0));
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// The configured device first; if it has not answered within <see cref="CpuJoinsAfter"/> (or
    /// fails), the CPU reads the same picture alongside it and whichever finishes first is used.
    /// Each recognizer reads its own copy of the picture, released only once that one is done.
    /// </summary>
    private static async Task<List<OcrTextBlock>> RecognizeOneAsync(
        System.Windows.Media.Imaging.BitmapSource picture, string language)
    {
        using var limit = new CancellationTokenSource(RecognitionLimit);
        var deadline = Task.Delay(RecognitionLimit + TimeSpan.FromSeconds(2));

        var primary = Run(AppServices.Ocr, picture, language, limit.Token);
        if (ConfiguredIsCpu) return await WithLimit(primary, deadline);

        var early = await Task.WhenAny(primary, Task.Delay(CpuJoinsAfter));
        if (early == primary && primary.IsCompletedSuccessfully) return primary.Result;

        Log.Info("Sticky-note OCR: configured device {State}; the CPU joins in",
            primary.IsCompleted ? "failed" : $"still busy after {CpuJoinsAfter.TotalSeconds:0} s");
        var fallback = Run(CpuOcr.Value, picture, language, limit.Token);

        var pending = new List<Task<List<OcrTextBlock>>> { primary, fallback };
        Exception? failure = null;
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(Task.WhenAny(pending), deadline);
            if (finished == deadline) throw new TimeoutException(LocalizationService.Get("S.Note.ReadTimeout"));
            var done = await (Task<Task<List<OcrTextBlock>>>)finished;
            if (done.IsCompletedSuccessfully)
            {
                limit.Cancel(); // the other one may stop now
                return done.Result;
            }
            failure = done.Exception?.InnerException ?? failure;
            pending.Remove(done);
        }
        throw failure ?? new InvalidOperationException("OCR failed");

        static Task<List<OcrTextBlock>> Run(OcrService service, System.Windows.Media.Imaging.BitmapSource source,
            string language, CancellationToken token)
        {
            var bitmap = BitmapInterop.ToGdiBitmap(source);
            var task = service.RecognizeAsync(bitmap, language, token);
            task.ContinueWith(_ => bitmap.Dispose(), TaskScheduler.Default);
            return task;
        }

        static async Task<List<OcrTextBlock>> WithLimit(Task<List<OcrTextBlock>> work, Task deadline)
        {
            if (await Task.WhenAny(work, deadline) != work)
                throw new TimeoutException(LocalizationService.Get("S.Note.ReadTimeout"));
            return await work;
        }
    }

    // ── The archive log ──────────────────────────────────────────────────────

    /// <summary>One line in the archive log for <paramref name="text"/>, when archiving is on.</summary>
    public static void Archive(string text)
    {
        var s = Settings;
        if (!s.ArchiveEnabled || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            StickyNoteArchive.Append(StickyNoteArchive.FolderFor(s), text, DateTime.Now);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not write the sticky-note archive");
        }
    }

    // ── All of them at once ──────────────────────────────────────────────────

    /// <summary>The 便利貼設定 colours and font, on every note now showing.</summary>
    public static void ApplyAppearance()
    {
        foreach (var note in Open) note.ApplyAppearance();
    }

    public static bool AnyPassThrough => Open.Any(n => n.Data.PassThrough);

    public static void DisableAllPassThrough()
    {
        foreach (var note in Open.Where(n => n.Data.PassThrough).ToList()) note.SetPassThrough(false);
    }

    /// <summary>
    /// The 便利貼 shortcut over a note: one that lets clicks through gets them back, any other
    /// starts letting them through. A note that lets clicks through wins over one that does not,
    /// since it cannot be reached any other way under the pointer. False when no note is there.
    /// </summary>
    public static bool TryTogglePassThroughUnderPointer()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        StickyNoteWindow? solid = null;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var note = Open[i];
            var r = ScreenGeometry.PhysicalBounds(note);
            if (r.IsEmpty || !r.Contains(cursor)) continue;
            if (note.Data.PassThrough)
            {
                note.SetPassThrough(false);
                return true;
            }
            solid ??= note;
        }
        if (solid is null) return false;
        solid.SetPassThrough(true);
        return true;
    }
}
