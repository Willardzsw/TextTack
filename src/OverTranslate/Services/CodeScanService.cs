using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NLog;
using ZXing;
using ZXing.Common;

namespace OverTranslate.Services;

/// <summary>One code found in a picture: what kind it is and what it says.</summary>
public sealed record ScannedCode(BarcodeFormat Format, string Text)
{
    /// <summary>QR and the other square codes, as opposed to the striped ones.</summary>
    public bool IsTwoDimensional => Format is BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX
        or BarcodeFormat.AZTEC or BarcodeFormat.PDF_417 or BarcodeFormat.MAXICODE;

    /// <summary>The text as a web address the browser can open, or null when it is not one.</summary>
    public Uri? Link =>
        Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
}

/// <summary>
/// 二維碼識別: QR codes, and the bar codes ZXing.Net knows, read from a picture in-process — nothing
/// leaves the machine.
/// </summary>
public static class CodeScanService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Every code in the picture, in the order found; empty when there is none.</summary>
    public static IReadOnlyList<ScannedCode> Scan(Bitmap bitmap)
    {
        var (pixels, width, height) = ReadBgra(bitmap);
        var found = Decode(pixels, width, height);

        // A code drawn small on the screen has modules of one or two pixels, which the finder
        // patterns of the detector cannot be told apart at. Doubled, they can.
        if (found.Count == 0 && Math.Max(width, height) <= 1200)
        {
            var (big, bigWidth, bigHeight) = Double(pixels, width, height);
            found = Decode(big, bigWidth, bigHeight);
        }

        Log.Info("Code scan of a {Width}x{Height} picture found {Count} code(s)", width, height, found.Count);
        return found;
    }

    private static List<ScannedCode> Decode(byte[] pixels, int width, int height)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                // No CharacterSet: forcing UTF-8 garbles the GBK and Shift_JIS codes still printed;
                // ZXing reads the ECI where there is one and guesses the encoding where not.
            },
        };

        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        // Several codes in one selection are all reported; the single read is the fallback for the
        // few pictures the multiple reader's tiling misses.
        var results = reader.DecodeMultiple(source) is { Length: > 0 } many
            ? many
            : reader.Decode(source) is { } one ? [one] : [];
        return results
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .Select(r => new ScannedCode(r.BarcodeFormat, r.Text))
            .DistinctBy(c => (c.Format, c.Text))
            .ToList();
    }

    private static (byte[] Pixels, int Width, int Height) ReadBgra(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
            return (pixels, bitmap.Width, bitmap.Height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Every pixel made four, nearest-neighbour: sharp edges are what the detector reads.</summary>
    private static (byte[] Pixels, int Width, int Height) Double(byte[] pixels, int width, int height)
    {
        int w2 = width * 2, h2 = height * 2;
        var big = new byte[w2 * h2 * 4];
        for (int y = 0; y < h2; y++)
        {
            int src = (y / 2) * width * 4, dst = y * w2 * 4;
            for (int x = 0; x < w2; x++)
                Buffer.BlockCopy(pixels, src + (x / 2) * 4, big, dst + x * 4, 4);
        }
        return (big, w2, h2);
    }
}
