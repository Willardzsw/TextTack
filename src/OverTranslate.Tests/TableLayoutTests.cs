using System.Windows;
using OverTranslate.Services.Ocr;
using Xunit;
using Line = OverTranslate.Services.Ocr.TableLayout.Line;

namespace OverTranslate.Tests;

public class TableLayoutTests
{
    private static Line L(string text, double x, double y, double w, double h = 20) => new(text, new Rect(x, y, w, h));

    [Fact]
    public void ColumnsBecomeTabSeparatedRows()
    {
        var lines = new[]
        {
            L("Name", 10, 10, 60), L("Qty", 200, 10, 40), L("Price", 320, 10, 60),
            L("Apple", 10, 40, 70), L("3", 200, 40, 12), L("1.20", 320, 40, 50),
            L("Banana", 10, 70, 80), L("12", 200, 70, 24), L("0.50", 320, 70, 50),
        };

        Assert.Equal("Name\tQty\tPrice\r\nApple\t3\t1.20\r\nBanana\t12\t0.50", TableLayout.TryFormat(lines));
    }

    [Fact]
    public void EmptyCellKeepsItsTab()
    {
        var lines = new[]
        {
            L("A", 10, 10, 40), L("B", 200, 10, 40),
            L("a1", 10, 40, 40), L("b1", 200, 40, 40),
            L("b2", 200, 70, 40), L("a3", 10, 100, 40), L("b3", 200, 100, 40),
        };

        Assert.Equal("A\tB\r\na1\tb1\r\n\tb2\r\na3\tb3", TableLayout.TryFormat(lines));
    }

    [Fact]
    public void ProseIsNotATable()
    {
        var lines = new[]
        {
            L("The first line of a paragraph", 10, 10, 400),
            L("goes on here and then", 10, 40, 380),
            L("wraps again to a third", 10, 70, 390),
            L("and ends.", 10, 100, 120),
        };

        Assert.Null(TableLayout.TryFormat(lines));
    }

    [Fact]
    public void OneRowWithAGapIsNotATable()
    {
        var lines = new[]
        {
            L("Title", 10, 10, 100), L("Date", 400, 10, 60),
            L("Some prose under it", 10, 40, 380),
            L("and more prose", 10, 70, 300),
        };

        Assert.Null(TableLayout.TryFormat(lines));
    }
}
