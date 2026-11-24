using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace OverTranslate.Services;

/// <summary>
/// Saves screenshots to a local folder. The image handed in is the same composited bitmap the
/// "copy screenshot" flow puts on the clipboard, so the saved file always matches what was copied.
/// </summary>
public static class ScreenshotSaveService
{
    /// <summary>圖片\OverTranslate — used whenever the user hasn't picked a custom folder.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OverTranslate");

    /// <summary>The folder currently in effect: the user's choice, or the default when unset/blank.</summary>
    public static string ResolveDirectory(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultDirectory : configured.Trim();

    /// <summary>
    /// Writes <paramref name="image"/> as a PNG into <paramref name="directory"/>, creating the
    /// folder when missing. Colliding timestamps get a suffix and never overwrite an older capture.
    /// Returns the full path of the written file.
    /// </summary>
    public static string Save(BitmapSource image, string? directory = null)
        => Save(image, directory, DateTime.Now);

    internal static string Save(BitmapSource image, string? directory, DateTime timestamp)
    {
        var dir = ResolveDirectory(directory);
        Directory.CreateDirectory(dir);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var stem = $"OverTranslate_{timestamp:yyyyMMdd_HHmmssfff}";
        for (var suffix = 0; ; suffix++)
        {
            var name = suffix == 0 ? stem : $"{stem}_{suffix}";
            var path = Path.Combine(dir, name + ".png");
            FileStream stream;
            try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; }

            try
            {
                using (stream) encoder.Save(stream);
                return path;
            }
            catch
            {
                // Only this call created this file. Do not leave a partial image after a failed save.
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    // ---- 另存為 ----

    /// <summary>The formats 另存為 offers, in the dialog's order: key, filter, extensions.</summary>
    internal static readonly (string Key, string Filter, string[] Extensions)[] SaveAsFormats =
    [
        ("png", "PNG (*.png)|*.png", [".png"]),
        ("jpg", "JPEG (*.jpg)|*.jpg;*.jpeg", [".jpg", ".jpeg"]),
        ("bmp", "BMP (*.bmp)|*.bmp", [".bmp"]),
        ("tif", "TIFF (*.tif)|*.tif;*.tiff", [".tif", ".tiff"]),
    ];

    /// <summary>
    /// The format for a chosen file: by the extension typed, else the type picked in the dialog
    /// (1-based, as the dialog counts), else PNG.
    /// </summary>
    internal static string FormatFor(string fileName, int filterIndex)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        foreach (var f in SaveAsFormats)
            if (f.Extensions.Contains(ext)) return f.Key;
        return filterIndex >= 1 && filterIndex <= SaveAsFormats.Length ? SaveAsFormats[filterIndex - 1].Key : "png";
    }

    /// <summary>An encoder for the format. JPEG and BMP have no alpha: the picture is made opaque first.</summary>
    internal static BitmapEncoder EncoderFor(string format, BitmapSource image)
    {
        BitmapEncoder encoder = format switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = 90 },
            "bmp" => new BmpBitmapEncoder(),
            "tif" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new PngBitmapEncoder(),
        };
        BitmapSource frame = format is "jpg" or "bmp" && image.Format != System.Windows.Media.PixelFormats.Bgr24
            ? new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr24, null, 0)
            : image;
        encoder.Frames.Add(BitmapFrame.Create(frame));
        return encoder;
    }

    /// <summary>
    /// 另存為: asks where and in which format (PNG, JPEG, BMP, TIFF), starting from the last ones
    /// used, and writes the picture there. False when the dialog was cancelled or the write failed
    /// (said in a message box).
    /// </summary>
    public static bool SaveAs(System.Windows.Window? owner, BitmapSource image, string titleKey)
    {
        var settings = SettingsService.Instance.Current;
        int index = Array.FindIndex(SaveAsFormats, f => f.Key == settings.SaveAsFormat);
        if (index < 0) index = 0;
        var folder = string.IsNullOrWhiteSpace(settings.SaveAsFolder) || !Directory.Exists(settings.SaveAsFolder)
            ? ResolveDirectory(settings.ScreenshotSavePath)
            : settings.SaveAsFolder;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = string.Join("|", SaveAsFormats.Select(f => f.Filter)),
            FilterIndex = index + 1,
            DefaultExt = SaveAsFormats[index].Extensions[0],
            AddExtension = true,
            FileName = $"TextTack_{DateTime.Now:yyyyMMdd_HHmmss}",
            InitialDirectory = folder,
        };
        // With no window of ours on screen (the capture has just closed, and the main window lives
        // in the tray) a dialog of its own may open behind the program in front. A stand-in owner,
        // invisible, made the foreground while ours still is, brings it up where the pointer is.
        System.Windows.Window? standIn = null;
        if (owner is null)
        {
            standIn = StandInOwner();
            owner = standIn;
        }
        try
        {
            if (dialog.ShowDialog(owner) != true) return false;
            return WriteChosen(owner, dialog, image, titleKey, settings);
        }
        finally
        {
            standIn?.Close();
        }
    }

    /// <summary>An invisible, activated window on the pointer's screen, to own a dialog.</summary>
    private static System.Windows.Window StandInOwner()
    {
        var window = new System.Windows.Window
        {
            WindowStyle = System.Windows.WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            Width = 1,
            Height = 1,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
        };
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var cursor = System.Windows.Forms.Cursor.Position;
        ScreenGeometry.MoveToPhysical(window, cursor.X, cursor.Y);
        window.Show();
        window.Activate();
        return window;
    }

    private static bool WriteChosen(System.Windows.Window owner, Microsoft.Win32.SaveFileDialog dialog,
        BitmapSource image, string titleKey, Models.AppSettings settings)
    {

        var format = FormatFor(dialog.FileName, dialog.FilterIndex);
        try
        {
            var encoder = EncoderFor(format, image);
            using (var stream = File.Create(dialog.FileName)) encoder.Save(stream);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Saving a picture as {Format} failed", format);
            System.Windows.MessageBox.Show(owner, ex.Message, LocalizationService.Get(titleKey));
            return false;
        }

        settings.SaveAsFormat = format;
        settings.SaveAsFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        SettingsService.Instance.Save();
        return true;
    }

    /// <summary>Opens the folder in File Explorer, creating it first so the window isn't empty-handed.</summary>
    public static void OpenFolder(string? directory = null)
    {
        var dir = ResolveDirectory(directory);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
