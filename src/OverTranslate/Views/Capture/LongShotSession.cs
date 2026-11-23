using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NLog;
using OverTranslate.Services;
using OverTranslate.Services.LongShot;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;

namespace OverTranslate.Views.Capture;

/// <summary>
/// 長截圖: the selection keeps growing while the user scrolls the page under it.
/// </summary>
/// <remarks>
/// <para>The capture is gone; a thin frame marks the region, and a small panel beside it shows the
/// end of the picture so far, what is happening, and the buttons. The user scrolls the page
/// themselves, with the wheel or the keyboard, in whatever program it is: nothing is sent to that
/// program. Several times a second the region is read from the screen and handed to
/// <see cref="LongShotStitcher"/>.</para>
///
/// <para>Neither window takes the keyboard — it belongs to the page being scrolled — so Enter (copy)
/// and Esc (cancel) are hotkeys for as long as this runs. Both windows ask to be left out of screen
/// captures, so even where the panel has to sit over the region it is not stitched in.</para>
/// </remarks>
public sealed class LongShotSession
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>The one running, if any: a capture started meanwhile would be read into the picture.</summary>
    public static LongShotSession? Current { get; private set; }

    private const int FastTickMs = 120, SlowTickMs = 300, IdleTicksBeforeSlow = 8;
    private const int MaxHeight = 20000;
    private const int HotkeyEnter = 0x7A01, HotkeyEscape = 0x7A02;

    private Rectangle _region;
    private LongShotStitcher _stitcher;
    private Bitmap _grab;
    private readonly DispatcherTimer _timer;
    private readonly Window _frame;
    private readonly Window _panel;
    private readonly Image _preview = new() { Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly int _previewWidth;
    private int _previewHeight;
    private Border? _previewBox;
    private readonly double _scale;

    // 預選: until something is stitched the frame has grips and the region can be resized.
    private Grid? _grips;
    private IntPtr _frameHwnd;
    private bool _frameLocked;
    private (System.Drawing.Point Cursor, Rectangle Region, int Edges)? _drag;
    private HwndSource? _panelSource;
    private readonly Button _axisButton;
    private LongShotWheelHook? _wheel;

    // Looks in a row that could not be placed: past a few, the region is likely too small for one
    // turn of the wheel, and the panel says so.
    private int _lostRun;
    private int _idleTicks;
    private bool _full, _ended, _lostShown;

    private LongShotSession(Rectangle region)
    {
        _region = region;
        _stitcher = new LongShotStitcher(region.Width, region.Height, MaxHeight);
        _grab = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);

        double scale = ScreenGeometry.ScaleAt(region.Left, region.Top);
        _scale = scale;
        _previewWidth = (int)Math.Round(150 * scale);
        _previewHeight = PreviewHeightFor(region);

        _axisButton = new Button
        {
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 30,
        };
        _axisButton.SetResourceReference(FrameworkElement.StyleProperty, "FlatButton");
        ToolTipService.SetShowOnDisabled(_axisButton, true);
        _axisButton.Click += (_, _) => ToggleAxis();
        RenderAxis();

        _frame = BuildFrame();
        _panel = BuildPanel(scale);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(FastTickMs) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Starts on <paramref name="region"/> (physical pixels) once the capture's windows are off the screen.</summary>
    public static void Start(Rectangle region)
    {
        if (Current is not null || region.Width < 16 || region.Height < 16) return;
        var session = new LongShotSession(region);
        Current = session;
        Log.Info("Long screenshot started on {Region}", region);

        session._frame.Show();
        session._panel.Show();
        session.ShowProgress();
        session._timer.Start();
    }

    // ---- reading the screen ----

    private void Tick()
    {
        if (_ended || _full) return;
        byte[] pixels;
        try
        {
            using (var g = Graphics.FromImage(_grab))
                g.CopyFromScreen(_region.Left, _region.Top, 0, 0, _region.Size, CopyPixelOperation.SourceCopy);
            pixels = ReadPixels(_grab);
        }
        catch (Exception ex)
        {
            // The secure desktop (a UAC prompt, the lock screen) cannot be read; the next tick may.
            Log.Debug(ex, "Long screenshot could not read the screen this tick");
            return;
        }

        var result = _stitcher.Add(pixels);
        switch (result)
        {
            case StitchResult.Unchanged:
                // The page has settled: a "scroll back" that no longer applies should not stay up.
                if (_lostShown && _idleTicks >= 2) ShowProgress();
                if (++_idleTicks == IdleTicksBeforeSlow) _timer.Interval = TimeSpan.FromMilliseconds(SlowTickMs);
                return;
            case StitchResult.Lost:
                _lostRun++;
                SetStatus(LocalizationService.Get(_lostRun >= 3 || TooSmall ? "S.LongShot.TooSmall" : "S.LongShot.Lost"));
                _lostShown = true;
                break;
            case StitchResult.Placed:
                _lostRun = 0;
                if (_axisButton.IsEnabled && _stitcher.HasMoved) RenderAxis();
                if (_stitcher.HasMoved) LockFrame();
                ShowProgress();
                UpdatePreview();
                break;
            case StitchResult.Full:
                _full = true;
                _timer.Stop();
                RenderAxis();
                LockFrame();
                UpdatePreview();
                SetStatus(LocalizationService.Get("S.LongShot.Full"));
                Log.Info("Long screenshot reached its maximum height");
                break;
        }

        _idleTicks = 0;
        _timer.Interval = TimeSpan.FromMilliseconds(FastTickMs);
    }

    private static byte[] ReadPixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bitmap.Width * 4;
            var pixels = new byte[stride * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private int PreviewHeightFor(Rectangle region) =>
        (int)Math.Round(Math.Clamp(region.Height / _scale, 160, 420) * _scale);

    private void UpdatePreview()
    {
        var (pixels, width, height) = _stitcher.Preview(_previewWidth, _previewHeight);
        if (height == 0) return;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        _preview.Source = source;
        // At the panel's own scale, so a physical pixel of preview is one on the screen.
        var dpi = VisualTreeHelper.GetDpi(_panel);
        _preview.Width = width / dpi.DpiScaleX;
        _preview.Height = height / dpi.DpiScaleY;
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>上下 ⇄ 左右: which way the picture grows. Either way, what is already stitched stays.</summary>
    private void ToggleAxis()
    {
        if (_stitcher.HasMoved) return;
        _stitcher.Axis = _stitcher.Axis == StitchAxis.Vertical ? StitchAxis.Horizontal : StitchAxis.Vertical;

        // Sideways, the plain wheel over the region scrolls left and right; up and down, it is the wheel.
        _wheel?.Dispose();
        _wheel = _stitcher.Axis == StitchAxis.Horizontal ? LongShotWheelHook.Install(_region) : null;
        Log.Info("Long screenshot now stitching {Axis}", _stitcher.Axis);
        RenderAxis();
        ShowProgress();
        UpdatePreview();
    }

    private void RenderAxis()
    {
        bool vertical = _stitcher.Axis == StitchAxis.Vertical;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            // Segoe's up-down and left-right arrows.
            Text = vertical ? "" : "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get(vertical ? "S.LongShot.Vertical" : "S.LongShot.Horizontal"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _axisButton.Content = row;

        // Once something is stitched the direction stays; the button says why it no longer moves.
        _axisButton.IsEnabled = !_stitcher.HasMoved;
        _axisButton.SetResourceReference(FrameworkElement.ToolTipProperty,
            _stitcher.HasMoved ? "S.LongShot.AxisLocked" : "S.LongShot.AxisTip");
    }

    /// <summary>
    /// Whether the region is shorter, the way it grows, than one turn of the wheel commonly moves a
    /// page (about 150 px at 100%): then two looks rarely share anything to be placed by.
    /// </summary>
    private bool TooSmall
    {
        get
        {
            double scale = ScreenGeometry.ScaleAt(_region.Left, _region.Top);
            int span = _stitcher.Axis == StitchAxis.Vertical ? _region.Height : _region.Width;
            return span < 150 * scale;
        }
    }

    private void ShowProgress()
    {
        _lostShown = false;
        if (!_stitcher.HasMoved && TooSmall)
        {
            SetStatus(LocalizationService.Get("S.LongShot.TooSmall"));
            return;
        }
        SetStatus(_stitcher.Height > _stitcher.FrameHeight || _stitcher.Width > _stitcher.FrameWidth
            ? LocalizationService.Format("S.LongShot.Progress", _stitcher.Width, _stitcher.Height)
            : LocalizationService.Get(_stitcher.Axis == StitchAxis.Vertical ? "S.LongShot.Scroll" : "S.LongShot.ScrollAcross"));
    }

    // ---- the ends ----

    private enum Ending { Copy, Save, Pin, Cancel }

    private void End(Ending ending)
    {
        if (_ended) return;

        BitmapSource? picture = null;
        if (ending != Ending.Cancel)
        {
            // One last look, so whatever was scrolled into view just before the press is in.
            if (!_full) Tick();
            var (pixels, width, height) = _stitcher.Finish();
            picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            picture.Freeze();
        }

        if (ending == Ending.Save && picture is not null && !SaveWithDialog(picture))
            return; // The dialog was cancelled: still running, nothing lost.

        _ended = true;
        _timer.Stop();
        UnregisterHotkeys();
        _wheel?.Dispose();
        _wheel = null;
        _frame.Close();
        _panel.Close();
        _grab.Dispose();
        Current = null;
        Log.Info("Long screenshot ended ({Ending}), {Width}x{Height}px",
            ending, picture?.PixelWidth ?? 0, picture?.PixelHeight ?? 0);

        if (picture is null) return;
        try
        {
            switch (ending)
            {
                case Ending.Copy:
                    System.Windows.Clipboard.SetImage(picture);
                    if (SettingsService.Instance.Current.SaveScreenshotToDisk)
                        ScreenshotSaveService.Save(picture, SettingsService.Instance.Current.ScreenshotSavePath);
                    break;
                case Ending.Pin:
                    Pin.PinWindow.Pin(picture, _region.Left, _region.Top);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Finishing the long screenshot ({Ending}) failed", ending);
        }
    }

    private bool SaveWithDialog(BitmapSource picture)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG|*.png",
            FileName = $"TextTack_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = ScreenshotSaveService.ResolveDirectory(SettingsService.Instance.Current.ScreenshotSavePath),
        };

        _timer.Stop();
        _panel.Topmost = false;
        try
        {
            if (dialog.ShowDialog(_panel) != true) return false;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(picture));
            using var stream = System.IO.File.Create(dialog.FileName);
            encoder.Save(stream);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Saving the long screenshot failed");
            System.Windows.MessageBox.Show(_panel, ex.Message, LocalizationService.Get("S.Toolbar.LongShot"));
            return false;
        }
        finally
        {
            if (!_ended)
            {
                _panel.Topmost = true;
                if (!_full) _timer.Start();
            }
        }
    }

    // ---- the windows ----

    private const int BorderPx = 2;

    /// <summary>How far the frame window reaches past the region: the grips' width, in physical pixels.</summary>
    private int Grip => (int)Math.Round(8 * _scale);

    private const int EdgeLeft = 1, EdgeTop = 2, EdgeRight = 4, EdgeBottom = 8;

    private Window BuildFrame()
    {
        var window = NewToolWindow();
        double grip = Grip / _scale, line = BorderPx / _scale;

        var root = new Grid();

        // The line just outside the region, at the inner edge of the grip band.
        var border = new Border { BorderThickness = new Thickness(line), Margin = new Thickness(grip - line) };
        border.SetResourceReference(Border.BorderBrushProperty, "AppAccent");
        root.Children.Add(border);

        // The grips: eight cells round the region, all but invisible yet there to the pointer (a
        // layered window lets clicks through only where it is fully clear), each with a small
        // handle drawn on it. The middle is left clear, so the page under it scrolls as ever.
        var grips = new Grid();
        grips.RowDefinitions.Add(new RowDefinition { Height = new GridLength(grip) });
        grips.RowDefinitions.Add(new RowDefinition());
        grips.RowDefinitions.Add(new RowDefinition { Height = new GridLength(grip) });
        grips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(grip) });
        grips.ColumnDefinitions.Add(new ColumnDefinition());
        grips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(grip) });
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                if (row == 1 && col == 1) continue;
                int edges = (col == 0 ? EdgeLeft : col == 2 ? EdgeRight : 0) | (row == 0 ? EdgeTop : row == 2 ? EdgeBottom : 0);
                grips.Children.Add(GripCell(row, col, edges, grip));
            }
        root.Children.Add(grips);
        _grips = grips;

        window.Content = root;
        window.SourceInitialized += (_, _) =>
        {
            _frameHwnd = new WindowInteropHelper(window).Handle;
            SetWindowLong(_frameHwnd, GWL_EXSTYLE, GetWindowLong(_frameHwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowDisplayAffinity(_frameHwnd, WDA_EXCLUDEFROMCAPTURE);
            PlaceFrame(_region);
        };
        // Again once shown: WPF applies its own size when it first lays the window out.
        window.Loaded += (_, _) => PlaceFrame(_region);
        return window;
    }

    private FrameworkElement GripCell(int row, int col, int edges, double grip)
    {
        var cell = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
            Cursor = edges switch
            {
                EdgeLeft or EdgeRight => System.Windows.Input.Cursors.SizeWE,
                EdgeTop or EdgeBottom => System.Windows.Input.Cursors.SizeNS,
                EdgeLeft | EdgeTop or EdgeRight | EdgeBottom => System.Windows.Input.Cursors.SizeNWSE,
                _ => System.Windows.Input.Cursors.SizeNESW,
            },
        };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, col);

        var handle = new System.Windows.Shapes.Rectangle
        {
            Width = grip * 0.8,
            Height = grip * 0.8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 1,
            Stroke = Brushes.White,
        };
        handle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AppAccent");
        cell.Children.Add(handle);

        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (_frameLocked || _stitcher.HasMoved) return;
            GetCursorPos(out var p);
            _drag = (new System.Drawing.Point(p.X, p.Y), _region, edges);
            _timer.Stop();
            cell.CaptureMouse();
            e.Handled = true;
        };
        cell.MouseMove += (_, _) =>
        {
            if (_drag is not { } drag) return;
            GetCursorPos(out var p);
            PlaceFrame(Resized(drag.Region, drag.Edges, p.X - drag.Cursor.X, p.Y - drag.Cursor.Y));
        };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (_drag is not { } drag) return;
            _drag = null;
            cell.ReleaseMouseCapture();
            GetCursorPos(out var p);
            ApplyRegion(Resized(drag.Region, drag.Edges, p.X - drag.Cursor.X, p.Y - drag.Cursor.Y));
        };
        cell.LostMouseCapture += (_, _) =>
        {
            // Taken away mid-drag (another window took the mouse): back to the region as it was.
            if (_drag is not { } drag) return;
            _drag = null;
            ApplyRegion(drag.Region);
        };
        return cell;
    }

    /// <summary>The region with the dragged edges moved, kept at least 32 px and on the desktop.</summary>
    private static Rectangle Resized(Rectangle r, int edges, int dx, int dy)
    {
        const int Min = 32;
        var desk = System.Windows.Forms.SystemInformation.VirtualScreen;
        int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if ((edges & EdgeLeft) != 0) left = Math.Clamp(left + dx, desk.Left, right - Min);
        if ((edges & EdgeRight) != 0) right = Math.Clamp(right + dx, left + Min, desk.Right);
        if ((edges & EdgeTop) != 0) top = Math.Clamp(top + dy, desk.Top, bottom - Min);
        if ((edges & EdgeBottom) != 0) bottom = Math.Clamp(bottom + dy, top + Min, desk.Bottom);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>The frame round <paramref name="region"/>: the grip band outside it, never over it.</summary>
    private void PlaceFrame(Rectangle region)
    {
        if (_frameHwnd == IntPtr.Zero) return;
        int g = Grip;
        SetWindowPos(_frameHwnd, HWND_TOPMOST, region.Left - g, region.Top - g,
            region.Width + 2 * g, region.Height + 2 * g, SWP_NOACTIVATE);
    }

    /// <summary>
    /// A new region, chosen by dragging the frame before anything was stitched: everything that
    /// depends on its size starts over from it.
    /// </summary>
    private void ApplyRegion(Rectangle region)
    {
        if (_ended) return;
        if (region != _region && !_stitcher.HasMoved)
        {
            var axis = _stitcher.Axis;
            _region = region;
            _stitcher = new LongShotStitcher(region.Width, region.Height, MaxHeight) { Axis = axis };
            _grab.Dispose();
            _grab = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            _previewHeight = PreviewHeightFor(region);
            if (_previewBox is not null) _previewBox.Height = _previewHeight / _scale;
            _preview.Source = null;
            if (axis == StitchAxis.Horizontal)
            {
                _wheel?.Dispose();
                _wheel = LongShotWheelHook.Install(region);
            }
            _lostRun = 0;
            Log.Info("Long screenshot region resized to {Region}", region);
        }

        PlaceFrame(_region);
        PlacePanel(_panel, _scale);
        ShowProgress();
        _idleTicks = 0;
        _timer.Interval = TimeSpan.FromMilliseconds(FastTickMs);
        if (!_full) _timer.Start();
    }

    /// <summary>Once something is stitched: the grips go, and the frame lets every click through.</summary>
    private void LockFrame()
    {
        if (_frameLocked) return;
        _frameLocked = true;
        if (_grips is not null) _grips.Visibility = Visibility.Collapsed;
        if (_frameHwnd != IntPtr.Zero)
            SetWindowLong(_frameHwnd, GWL_EXSTYLE, GetWindowLong(_frameHwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT);
    }

    private Window BuildPanel(double scale)
    {
        var window = NewToolWindow();

        var previewBox = _previewBox = new Border
        {
            Height = _previewHeight / scale,
            Width = _previewWidth / scale,
            ClipToBounds = true,
            Child = _preview,
        };
        previewBox.SetResourceReference(Border.BackgroundProperty, "AppButtonBg");

        _status.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        _status.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        _status.Width = _previewWidth / scale;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(IconButton("", "S.LongShot.Copy", () => End(Ending.Copy), accent: true));
        buttons.Children.Add(IconButton("", "S.LongShot.Save", () => End(Ending.Save)));
        buttons.Children.Add(IconButton("", "S.LongShot.Pin", () => End(Ending.Pin)));
        buttons.Children.Add(IconButton("", "S.LongShot.Cancel", () => End(Ending.Cancel)));

        var stack = new StackPanel { Margin = new Thickness(10) };
        stack.Children.Add(previewBox);
        stack.Children.Add(_status);
        stack.Children.Add(_axisButton);
        stack.Children.Add(buttons);

        var card = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.16, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "HintCardBg");
        card.SetResourceReference(Border.BorderBrushProperty, "HintCardBorder");
        window.Content = new Border { Margin = new Thickness(12), Child = card };
        window.SizeToContent = SizeToContent.WidthAndHeight;

        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            _panelSource = HwndSource.FromHwnd(hwnd);
            _panelSource?.AddHook(PanelHook);
            RegisterHotKey(hwnd, HotkeyEnter, MOD_NOREPEAT, VK_RETURN);
            RegisterHotKey(hwnd, HotkeyEscape, MOD_NOREPEAT, VK_ESCAPE);

            PlacePanel(window, scale);
        };
        window.Loaded += (_, _) => PlacePanel(window, scale);
        return window;
    }

    private void PlacePanel(Window window, double scale)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        ((FrameworkElement)window.Content).Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ((FrameworkElement)window.Content).DesiredSize;
        var (x, y) = PanelPosition((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), (int)(12 * scale));
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
    }

    /// <summary>
    /// Beside the region on the right, else the left, else over its bottom-right corner — where it is
    /// still not read, being left out of captures. <paramref name="shadow"/> is the window's own
    /// transparent margin, allowed to hang over the region.
    /// </summary>
    private (int X, int Y) PanelPosition(int width, int height, int shadow)
    {
        var screen = System.Windows.Forms.Screen.FromRectangle(_region).WorkingArea;
        int gap = 6;
        int y = Math.Clamp(_region.Bottom - height + shadow, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        if (_region.Right + gap + width - shadow <= screen.Right) return (_region.Right + gap - shadow, y);
        if (_region.Left - gap - width + shadow >= screen.Left) return (_region.Left - gap - width + shadow, y);
        return (Math.Max(screen.Left, _region.Right - width), y);
    }

    private static Window NewToolWindow() => new()
    {
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        ShowActivated = false,
        Topmost = true,
        UseLayoutRounding = true,
        Left = -32000,
        Top = -32000,
    };

    private static Button IconButton(string glyph, string tipKey, Action click, bool accent = false)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 32,
            Height = 30,
            Margin = new Thickness(2, 0, 2, 0),
            FontSize = 14,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, accent ? "AccentButton" : "FlatButton");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, tipKey);
        System.Windows.Automation.AutomationProperties.SetName(button, LocalizationService.Get(tipKey));
        button.Click += (_, _) => click();
        return button;
    }

    private IntPtr PanelHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyEnter) { handled = true; End(Ending.Copy); }
            else if (id == HotkeyEscape) { handled = true; End(Ending.Cancel); }
        }
        return IntPtr.Zero;
    }

    private void UnregisterHotkeys()
    {
        var hwnd = new WindowInteropHelper(_panel).Handle;
        if (hwnd == IntPtr.Zero) return;
        UnregisterHotKey(hwnd, HotkeyEnter);
        UnregisterHotKey(hwnd, HotkeyEscape);
        _panelSource?.RemoveHook(PanelHook);
    }

    // ---- Win32 ----

    private const int GWL_EXSTYLE = -20, WM_HOTKEY = 0x0312;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
    private const uint MOD_NOREPEAT = 0x4000, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
