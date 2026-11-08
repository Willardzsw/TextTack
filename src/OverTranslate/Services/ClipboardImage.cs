using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverTranslate.Services;

/// <summary>Puts a captured picture on the clipboard.</summary>
internal static class ClipboardImage
{
    /// <summary>
    /// Copies <paramref name="image"/>; one with transparent parts (圓角) goes on as a PNG as well.
    /// </summary>
    /// <remarks>
    /// The ordinary clipboard bitmap has no transparency, and most programs reading it show what was
    /// see-through as black. So a picture with transparent corners is put on twice: as a PNG, which
    /// keeps them and is what browsers, chat programs and Office read first, and as a bitmap laid on
    /// white for everything that only reads that.
    /// </remarks>
    public static void Set(BitmapSource image, bool hasTransparency)
    {
        if (!hasTransparency)
        {
            System.Windows.Clipboard.SetImage(image);
            return;
        }

        var data = new System.Windows.DataObject();
        data.SetImage(OnWhite(image));

        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);
        png.Position = 0;
        data.SetData("PNG", png, autoConvert: false);

        System.Windows.Clipboard.SetDataObject(data, copy: true);
    }

    private static BitmapSource OnWhite(BitmapSource image)
    {
        var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, bounds);
            dc.DrawImage(image, bounds);
        }
        var flat = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        flat.Render(dv);
        flat.Freeze();
        return flat;
    }
}
