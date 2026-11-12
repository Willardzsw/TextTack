using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>The 標記 tool remembered between captures, and the 文字 描邊.</summary>
public class AnnotationToolMemoryTests
{
    [Fact]
    public void A_fresh_profile_starts_on_the_rectangle()
    {
        Assert.Equal(AnnotationTool.Rectangle, new CaptureAnnotationSettings().StartingTool());
    }

    [Theory]
    [InlineData(AnnotationTool.Pen)]
    [InlineData(AnnotationTool.Text)]
    [InlineData(AnnotationTool.Mosaic)]
    [InlineData(AnnotationTool.Arrow)]
    public void The_last_tool_is_where_the_next_capture_starts(AnnotationTool tool)
    {
        Assert.Equal(tool, new CaptureAnnotationSettings { LastTool = tool }.StartingTool());
    }

    [Fact]
    public void The_eraser_is_never_where_a_capture_starts()
    {
        Assert.Equal(AnnotationTool.Rectangle,
            new CaptureAnnotationSettings { LastTool = AnnotationTool.Eraser }.StartingTool());
    }

    [Fact]
    public void The_tool_and_outline_survive_the_settings_file()
    {
        var saved = new CaptureAnnotationSettings
        {
            LastTool = AnnotationTool.Highlighter,
            TextOutline = true,
            TextOutlineColor = "#EF4444",
            TextOutlineWidth = 7,
        };

        var json = JsonSerializer.Serialize(saved);
        var read = JsonSerializer.Deserialize<CaptureAnnotationSettings>(json)!;

        Assert.Contains("\"Highlighter\"", json);
        Assert.Equal(AnnotationTool.Highlighter, read.LastTool);
        Assert.True(read.TextOutline);
        Assert.Equal("#EF4444", read.TextOutlineColor);
        Assert.Equal(7, read.TextOutlineWidth);
    }

    [Fact]
    public void An_outlined_text_mark_is_bounded_wider_by_the_outline()
    {
        AnnotationStroke Mark(Color? outline, double width) => new()
        {
            Tool      = AnnotationTool.Text,
            Color     = Colors.Black,
            Thickness = 0,
            Opacity   = 1,
            Points    = [new Point(100, 100)],
            Text      = "Hello",
            TextStyle = new AnnotationTextStyle(new FontFamily("Segoe UI"), 24, false, false, false, false, outline, width),
        };

        var plain    = Mark(null, 0).Bounds;
        var outlined = Mark(Colors.White, 6).Bounds;

        Assert.Equal(plain.Left - 6, outlined.Left, 3);
        Assert.Equal(plain.Width + 12, outlined.Width, 3);
    }
}
