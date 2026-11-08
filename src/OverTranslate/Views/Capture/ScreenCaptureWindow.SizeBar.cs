using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Services;
using WPoint = System.Windows.Point;
using WSize = System.Windows.Size;

namespace OverTranslate.Views.Capture;

/// <summary>
/// The size bar over the box (寬 × 高, px or dip, square or rounded, 鎖定長寬比, 重新截圖), the
/// 調整截圖區域 dialog behind its size, Ctrl+A and F5.
/// </summary>
public partial class ScreenCaptureWindow
{
    private static readonly Geometry SquareCornerIcon = FrozenGeometry("M2,13 V2 H13");
    private static readonly Geometry RoundCornerIcon = FrozenGeometry("M2,13 V8 A6,6 0 0 1 8,2 H13");

    private static Geometry FrozenGeometry(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static Models.CaptureSettings CaptureOptions => SettingsService.Instance.Current.Capture;

    /// <summary>The box in physical pixels, the way <see cref="Selection"/> sizes it.</summary>
    private (int Left, int Top, int Width, int Height) PhysicalOf(Rect r) => (
        (int)Math.Round(_physBounds.Left + r.X * _dpiX),
        (int)Math.Round(_physBounds.Top + r.Y * _dpiY),
        Math.Max(1, (int)(r.Width * _dpiX)),
        Math.Max(1, (int)(r.Height * _dpiY)));

    /// <summary>The monitor the box is mostly on (its centre), and that monitor's scale.</summary>
    private (System.Windows.Forms.Screen Screen, double Scale) MonitorOf(Rect r)
    {
        var (left, top, width, height) = PhysicalOf(r);
        int cx = left + width / 2, cy = top + height / 2;
        return (System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy)), ScreenGeometry.ScaleAt(cx, cy));
    }

    /// <summary>
    /// Height, in DIP at the monitor's scale, the size bar takes above the box with its gap — what
    /// the capture toolbar leaves free when it has to go above the box too.
    /// </summary>
    public const double SizeBarReserveDip = 40;

    /// <summary>Pixel width of the rounded corner in the pictures the box gives, matching what is on screen.</summary>
    public double CornerRadiusPixels => RoundedCornerDip * _dpiX;

    /// <summary>The bar's buttons and tooltips for the options as they are now.</summary>
    private void RenderSizeBarOptions()
    {
        var options = CaptureOptions;
        UnitPxBtn.Tag = options.SizeInDip ? null : "On";
        UnitDipBtn.Tag = options.SizeInDip ? "On" : null;
        CornerIcon.Data = options.RoundedCorners ? RoundCornerIcon : SquareCornerIcon;
        CornerBtn.SetResourceReference(ToolTipProperty,
            options.RoundedCorners ? "S.Capture.CornerRoundTip" : "S.Capture.CornerSquareTip");
        AspectIcon.Text = options.LockAspectRatio ? "" : "";
        AspectBtn.Tag = options.LockAspectRatio ? "On" : null;
        AspectBtn.SetResourceReference(ToolTipProperty,
            options.LockAspectRatio ? "S.Capture.AspectOnTip" : "S.Capture.AspectOffTip");
    }

    /// <summary>
    /// Shows the box's size over its top-left corner, or hides the bar when the box is not the
    /// user's to change.
    /// </summary>
    /// <remarks>
    /// Above the corner, where a box is started from; inside it only when the monitor has no room
    /// above. The toolbar leaves this space free when it goes above the box (see
    /// <see cref="SizeBarReserveDip"/>). While the box is being dragged out only the size shows.
    /// </remarks>
    private void UpdateSizeBar()
    {
        bool show = SelectionRect.Visibility == Visibility.Visible && !_processingStarted &&
                    !_annotationHoldsSelection && _selectionWpfRect.Width > 0 && _selectionWpfRect.Height > 0;
        if (!show)
        {
            SizeBar.Visibility = Visibility.Collapsed;
            return;
        }

        SizeBarTools.Visibility = _isDragging ? Visibility.Collapsed : Visibility.Visible;

        var r = _selectionWpfRect;
        var (_, _, pw, ph) = PhysicalOf(r);
        var (screen, scale) = MonitorOf(r);
        SizeText.Text = CaptureOptions.SizeInDip
            ? $"{Math.Round(pw / scale)} × {Math.Round(ph / scale)}"
            : $"{pw} × {ph}";

        // This window renders at one DPI across the desktop; the bar is scaled to the monitor it is on.
        double rel = scale / _dpiX;
        SizeBarScale.ScaleX = rel;
        SizeBarScale.ScaleY = rel;
        SizeBar.Visibility = Visibility.Visible;
        SizeBar.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
        var size = SizeBar.DesiredSize;

        double monLeft = (screen.Bounds.Left - _physBounds.Left) / _dpiX;
        double monRight = (screen.Bounds.Right - _physBounds.Left) / _dpiX;
        double monTop = (screen.Bounds.Top - _physBounds.Top) / _dpiY;

        double gap = 4 * rel;
        double x = r.Left, y = r.Top - size.Height - gap;
        if (y < monTop)
        {
            x = r.Left + 10 * rel;
            y = r.Top + 10 * rel;
        }
        x = Math.Clamp(x, monLeft, Math.Max(monLeft, monRight - size.Width));
        Canvas.SetLeft(SizeBar, x);
        Canvas.SetTop(SizeBar, y);
    }

    private void UnitPx_Click(object sender, RoutedEventArgs e) => SetUnit(dip: false);

    private void UnitDip_Click(object sender, RoutedEventArgs e) => SetUnit(dip: true);

    private void SetUnit(bool dip)
    {
        if (CaptureOptions.SizeInDip == dip) return;
        CaptureOptions.SizeInDip = dip;
        SettingsService.Instance.Save();
        RenderSizeBarOptions();
        UpdateSizeBar();
    }

    private void CornerBtn_Click(object sender, RoutedEventArgs e)
    {
        CaptureOptions.RoundedCorners = !CaptureOptions.RoundedCorners;
        SettingsService.Instance.Save();
        RenderSizeBarOptions();
        PlaceFrame(_selectionWpfRect);
        UpdateDimLayer();
    }

    private void AspectBtn_Click(object sender, RoutedEventArgs e)
    {
        CaptureOptions.LockAspectRatio = !CaptureOptions.LockAspectRatio;
        SettingsService.Instance.Save();
        // Locked to the box as it is now.
        if (CaptureOptions.LockAspectRatio) _aspectRatio = AspectOf(_selectionWpfRect);
        RenderSizeBarOptions();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>調整截圖區域: the box's corner and size typed in, in the unit the bar shows.</summary>
    private void SizeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasSelection || _processingStarted || _annotationHoldsSelection) return;

        var r = _selectionWpfRect;
        var (left, top, pw, ph) = PhysicalOf(r);
        var (screen, scale) = MonitorOf(r);
        bool dip = CaptureOptions.SizeInDip;
        double unit = dip ? scale : 1;

        var dialog = new CaptureRegionDialog(
            (int)Math.Round((left - screen.Bounds.Left) / unit),
            (int)Math.Round((top - screen.Bounds.Top) / unit),
            (int)Math.Round(pw / unit),
            (int)Math.Round(ph / unit),
            dip ? "dip" : "px")
        {
            Owner = this,
        };

        // Just under the bar, on the bar's monitor.
        var barCorner = SizeBar.TranslatePoint(new WPoint(0, SizeBar.ActualHeight), this);
        dialog.PlaceAtPhysical(
            (int)Math.Round(_physBounds.Left + barCorner.X * _dpiX),
            (int)Math.Round(_physBounds.Top + barCorner.Y * _dpiY + 6 * scale),
            screen.WorkingArea);

        if (dialog.ShowDialog() != true || !_hasSelection || _processingStarted) return;

        double newLeft = left, newTop = top, newW = pw, newH = ph;
        if (dialog.UsePosition)
        {
            newLeft = screen.Bounds.Left + dialog.RegionX * unit;
            newTop = screen.Bounds.Top + dialog.RegionY * unit;
        }
        if (dialog.UseSize)
        {
            newW = Math.Max(4, dialog.RegionWidth * unit);
            newH = Math.Max(4, dialog.RegionHeight * unit);
        }

        var target = new Rect(
            (newLeft - _physBounds.Left) / _dpiX, (newTop - _physBounds.Top) / _dpiY,
            newW / _dpiX, newH / _dpiY);
        SetSelectionFromCode(ClampToWindow(target));
    }

    /// <summary>Keeps a box typed or chosen in code on the desktop, shrinking it only if it is larger.</summary>
    private Rect ClampToWindow(Rect r)
    {
        double w = Math.Min(r.Width, ActualWidth), h = Math.Min(r.Height, ActualHeight);
        double x = Math.Clamp(r.X, 0, Math.Max(0, ActualWidth - w));
        double y = Math.Clamp(r.Y, 0, Math.Max(0, ActualHeight - h));
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// Puts the box where the code says, as though it had been drawn there: the first box of the
    /// capture finishes the selection, a later one is a move like any other.
    /// </summary>
    private void SetSelectionFromCode(Rect r)
    {
        if (_processingStarted || _annotationHoldsSelection || r.Width < 1 || r.Height < 1) return;
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
        }

        bool had = _hasSelection;
        _selectionWpfRect = r;
        _hasSelection = true;
        _aspectRatio = AspectOf(r);
        HintHost.Visibility = Visibility.Collapsed;
        SetFrameVisibility(true);
        UpdateSelectionMetadata();
        UpdateSelectionVisuals();
        if (had) RaiseAdjusted();
        else _selectionTcs.TrySetResult(true);
    }

    /// <summary>
    /// Ctrl+A: the whole monitor under the pointer; pressed again on a box that already is that
    /// monitor, every monitor.
    /// </summary>
    public void SelectScreenUnderPointer()
    {
        if (_processingStarted || _annotationHoldsSelection) return;

        var cursor = System.Windows.Forms.Cursor.Position;
        var bounds = System.Windows.Forms.Screen.FromPoint(cursor).Bounds;
        bounds.Intersect(_physBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var monitor = new Rect(
            (bounds.Left - _physBounds.Left) / _dpiX, (bounds.Top - _physBounds.Top) / _dpiY,
            bounds.Width / _dpiX, bounds.Height / _dpiY);
        var target = _hasSelection && IsSameBox(_selectionWpfRect, monitor)
            ? new Rect(0, 0, ActualWidth, ActualHeight)
            : monitor;
        SetSelectionFromCode(target);
    }

    // Within a pixel each way: the box went through physical pixels on its way to DIP.
    private bool IsSameBox(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) * _dpiX < 1.5 && Math.Abs(a.Top - b.Top) * _dpiY < 1.5 &&
        Math.Abs(a.Right - b.Right) * _dpiX < 1.5 && Math.Abs(a.Bottom - b.Bottom) * _dpiY < 1.5;

    /// <summary>
    /// F5 / 重新截圖: a fresh picture of the same desktop in place of the frozen one, the box kept.
    /// The session takes the picture, with its own windows out of the way, and hands it over here.
    /// </summary>
    public void ReplaceScreenshot(Bitmap screenshot)
    {
        var old = _screenshot;
        _screenshot = screenshot;
        old.Dispose();

        ScreenshotImage.Source = BitmapToDisplaySource(_screenshot, 96.0 * _dpiX);
        if (_brightBrush is not null) _brightBrush.ImageSource = ScreenshotImage.Source;
        CroppedBitmap?.Dispose();
        CroppedBitmap = null;
    }
}
