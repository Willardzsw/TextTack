using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class ScreenshotSaveTests
{
    [Fact]
    public async Task SimultaneousSavesWithTheSameTimestampKeepEveryImageAndExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "overtranslate-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var time = new DateTime(2026, 11, 4, 12, 0, 0);
            var existing = Path.Combine(directory, "OverTranslate_20261104_120000000.png");
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(existing, original);
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 10, 20, 30, 255 }, 4);
            image.Freeze();
            var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Task.Run(() => ScreenshotSaveService.Save(image, directory, time))));
            Assert.Equal(8, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(original, File.ReadAllBytes(existing));
            foreach (var path in paths)
            {
                using var stream = File.OpenRead(path);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Assert.Equal(1, decoder.Frames[0].PixelWidth);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("a.png", 2, "png")]
    [InlineData("a.JPEG", 1, "jpg")]
    [InlineData("a.jpg", 1, "jpg")]
    [InlineData("a.tiff", 1, "tif")]
    [InlineData("a", 3, "bmp")]
    [InlineData("a.txt", 4, "tif")]
    [InlineData("a", 0, "png")]
    public void SaveAsFormatFollowsTheExtensionElseTheChosenType(string name, int filterIndex, string format)
        => Assert.Equal(format, ScreenshotSaveService.FormatFor(name, filterIndex));

    [Theory]
    [InlineData("png", typeof(PngBitmapDecoder))]
    [InlineData("jpg", typeof(JpegBitmapDecoder))]
    [InlineData("bmp", typeof(BmpBitmapDecoder))]
    [InlineData("tif", typeof(TiffBitmapDecoder))]
    public void EveryFormatWritesAPictureThatReadsBack(string format, Type decoderType)
    {
        var pixels = new byte[8 * 4 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 100; pixels[i + 2] = 50; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
        image.Freeze();

        using var stream = new MemoryStream();
        ScreenshotSaveService.EncoderFor(format, image).Save(stream);
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.IsType(decoderType, decoder);
        Assert.Equal(8, decoder.Frames[0].PixelWidth);
        Assert.Equal(4, decoder.Frames[0].PixelHeight);
    }
}
