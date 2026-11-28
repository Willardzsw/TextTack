using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverTranslate.Services;

public static class AppIconService
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = GetStream("icons.app-compact.ico");
        return new System.Drawing.Icon(stream, 32, 32);
    }

    /// <summary>
    /// The tray icon greyed out and faded, for while 總開關 is off: the same shape, so it still reads
    /// as this application, but plainly not listening.
    /// </summary>
    public static System.Drawing.Icon CreateDisabledTrayIcon()
    {
        using var colour = CreateTrayIcon();
        using var source = colour.ToBitmap();
        using var grey = new System.Drawing.Bitmap(source.Width, source.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
        {
            var c = source.GetPixel(x, y);
            int l = (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
            l = 96 + l * 112 / 255;                       // a mid grey that shows on light and dark taskbars
            grey.SetPixel(x, y, System.Drawing.Color.FromArgb(c.A * 3 / 4, l, l, l));
        }

        var handle = grey.GetHicon();
        try
        {
            using var temp = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static ImageSource CreateMainIcon()
        => CreateImageSource("icons.app.ico", 256);

    public static ImageSource CreateCompactIcon()
        => CreateImageSource("icons.app-compact.ico", 64);

    private static ImageSource CreateImageSource(string relativeName, int size)
    {
        using var stream = GetStream(relativeName);
        using var icon   = new System.Drawing.Icon(stream, size, size);
        var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }

    private static System.IO.Stream GetStream(string relativeName)
    {
        var name = $"OverTranslate.{relativeName}";
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
    }
}
