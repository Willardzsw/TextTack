using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using SystemFonts = System.Windows.SystemFonts;

namespace OverTranslate.Views.Controls;

/// <summary>
/// One line of text with an outline around each glyph, for text laid straight over an arbitrary
/// picture (the screenshot hint) with no card behind it: the outline keeps it legible over bright
/// and dark content alike, where a plain TextBlock disappears into one or the other.
/// </summary>
/// <remarks>
/// The outline is drawn first with a pen twice <see cref="StrokeThickness"/> wide and the fill laid
/// over it, so only the outer half of the pen shows and the letters keep their full weight. Font
/// family, size and weight inherit like a TextBlock's.
/// </remarks>
public sealed class OutlinedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((OutlinedText)d)._geometry = null));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(OutlinedText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(OutlinedText), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, _) => ((OutlinedText)d)._geometry = null));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(OutlinedText), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, _) => ((OutlinedText)d)._geometry = null));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(OutlinedText), new FrameworkPropertyMetadata(FontWeights.Normal,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, _) => ((OutlinedText)d)._geometry = null));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    private Geometry? _geometry;
    private Size _textSize;

    private void EnsureGeometry()
    {
        if (_geometry != null) return;
        var ft = new FormattedText(
            Text ?? "",
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal),
            FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _textSize = new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
        _geometry = ft.BuildGeometry(new Point(0, 0));
        _geometry.Freeze();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _geometry = null;
        base.OnDpiChanged(oldDpi, newDpi);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureGeometry();
        double s = StrokeThickness;
        return new Size(_textSize.Width + 2 * s, _textSize.Height + 2 * s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsureGeometry();
        double s = StrokeThickness;
        dc.PushTransform(new TranslateTransform(s, s));
        if (s > 0 && Stroke != null)
        {
            var pen = new Pen(Stroke, 2 * s) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, _geometry);
        }
        dc.DrawGeometry(Fill, null, _geometry);
        dc.Pop();
    }
}
