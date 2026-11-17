using System.Drawing;
using OverTranslate.Services;
using Xunit;
using ZXing;
using ZXing.Common;

namespace OverTranslate.Tests;

/// <summary>Synthetic codes drawn here and read back — no screenshots involved.</summary>
public class CodeScanServiceTests
{
    private static Bitmap Draw(BarcodeFormat format, string text, int width, int height, int pad = 30)
    {
        var matrix = new MultiFormatWriter().encode(text, format, width, height,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 1, [EncodeHintType.CHARACTER_SET] = "UTF-8" });
        var bitmap = new Bitmap(matrix.Width + pad * 2, matrix.Height + pad * 2);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.White);
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                if (matrix[x, y]) bitmap.SetPixel(x + pad, y + pad, Color.Black);
        return bitmap;
    }

    [Fact]
    public void ReadsALink()
    {
        using var bitmap = Draw(BarcodeFormat.QR_CODE, "https://example.com/a?b=1", 200, 200);
        var code = Assert.Single(CodeScanService.Scan(bitmap));
        Assert.Equal(BarcodeFormat.QR_CODE, code.Format);
        Assert.Equal("https://example.com/a?b=1", code.Link?.AbsoluteUri);
    }

    [Fact]
    public void ReadsChineseText()
    {
        using var bitmap = Draw(BarcodeFormat.QR_CODE, "你好，世界", 200, 200);
        var code = Assert.Single(CodeScanService.Scan(bitmap));
        Assert.Equal("你好，世界", code.Text);
        Assert.Null(code.Link);
    }

    [Fact]
    public void ReadsABarcode()
    {
        using var bitmap = Draw(BarcodeFormat.CODE_128, "TT-12345", 300, 80);
        var code = Assert.Single(CodeScanService.Scan(bitmap));
        Assert.Equal("TT-12345", code.Text);
        Assert.False(code.IsTwoDimensional);
    }

    [Fact]
    public void NothingInABlankPicture()
    {
        using var bitmap = new Bitmap(200, 200);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.White);
        Assert.Empty(CodeScanService.Scan(bitmap));
    }
}
