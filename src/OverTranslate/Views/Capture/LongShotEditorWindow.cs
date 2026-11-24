using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Overlay;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace OverTranslate.Views.Capture;

/// <summary>
/// 長截圖's editor: the whole picture at its own resolution, zoomed and panned freely, cropped by
/// dragging the edges of what is kept, and marked up with the same 標記 panel and pens as a capture.
/// Then 複製, 另存為 or 貼圖 — always at the picture's own size.
/// </summary>
/// <remarks>
/// <para>A window of its own rather than the capture's, which works in screen coordinates: a long
/// picture laid on the screen has to be shrunk until it cannot be read, and nothing in the capture
/// can be zoomed or moved without the marks parting from what they were drawn on. Here everything
/// lives in the picture's own pixels — the marks, the crop, the eraser — and the view is only a
/// transform onto them.</para>
///
/// <para>Two states, as in a capture: with 標記 off a drag moves the edges of the crop (near one) or
/// the picture (anywhere else); with it on, the tool in hand draws. The wheel scrolls, Ctrl+wheel
/// zooms at the pointer, and Space or the middle button held drags the picture in either state.</para>
/// </remarks>
public sealed class LongShotEditorWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int MinKeep = 16;
    private const double EdgeReach = 8, ScrollZone = 36, MinZoomPercent = 5, MaxZoomPercent = 800;
    private const int EdgeLeft = 1, EdgeTop = 2, EdgeRight = 4, EdgeBottom = 8;
    private const double MinPointDistance = 1.2, MinShapeExtent = 3;
    private const int HistoryLimit = 60;

    private readonly BitmapSource _picture;
    private readonly System.Drawing.Rectangle _region;
    private readonly int _pw, _ph;

    // ---- the view ----
    private readonly Border _view;
    private readonly Canvas _stage = new() { ClipToBounds = false };
    private readonly ScrollBar _vBar = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hBar = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _zoomText = new() { MinWidth = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _toolbar;
    private readonly ToggleButton _annotateButton;

    // DIP of the view per pixel of the picture, and the picture pixel at the view's top-left.
    private double _zoom = 1;
    private double _offX, _offY;
    private bool _fitted, _placed;

    // ---- the crop: [left, right) × [top, bottom) in pixels of the picture ----
    private int _left, _top, _right, _bottom;
    private readonly Rectangle _dimTop = Dim(), _dimBottom = Dim(), _dimLeft = Dim(), _dimRight = Dim();
    private readonly Rectangle _outline = new() { IsHitTestVisible = false };
    private readonly Rectangle[] _handles = new Rectangle[8];

    // ---- what a press is doing ----
    private enum Gesture { None, Pan, Edges, Draw }
    private Gesture _gesture;
    private int _dragEdges;
    private Point _panFrom;           // view DIP
    private double _panOffX, _panOffY;
    private bool _spaceHeld;
    private readonly DispatcherTimer _edgeScroll;

    // ---- 標記 ----
    private AnnotationPanelWindow? _panel;
    private readonly InkSurface _ink = new();
    private readonly WetInkLayer _wetLayer = new();
    private readonly Ellipse _eraserRing = new() { Stroke = Brushes.Black, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<List<AnnotationStroke>> _history = [[]];
    private int _historyIndex;
    private List<Point>? _wetPoints;
    private Point? _shapeStart;
    private Point _shapeEnd;
    private List<Point>? _erasePoints;
    private Point _eraseLast;
    private bool _eraseTookSomething;
    private AnnotationTextEditor? _textEditor;
    private bool _inkReady;

    private bool Annotating => _panel is not null;
    private List<AnnotationStroke> Strokes => _history[_historyIndex];

    private LongShotEditorWindow(BitmapSource picture, System.Drawing.Rectangle region)
    {
        _picture = picture.Format == PixelFormats.Bgra32 || picture.Format == PixelFormats.Pbgra32
            ? picture
            : Frozen(new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0));
        _region = region;
        _pw = picture.PixelWidth;
        _ph = picture.PixelHeight;
        _right = _pw;
        _bottom = _ph;

        double scale = ScreenGeometry.ScaleAt(region.Left + region.Width / 2, region.Top + region.Height / 2);
        var work = System.Windows.Forms.Screen.FromRectangle(region).WorkingArea;
        double workW = work.Width / scale, workH = work.Height / scale;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        Title = LocalizationService.Get("S.LongShot.EditorTitle");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        MinWidth = 520;
        MinHeight = 360;
        Width = Math.Clamp(_pw / scale + 120, MinWidth, workW * 0.92);
        Height = Math.Max(MinHeight, workH * 0.9);

        // ---- the stage: the picture, the marks and the crop, in the picture's own pixels ----
        var image = new System.Windows.Controls.Image { Source = _picture, Width = _pw, Height = _ph, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _stage.Width = _pw;
        _stage.Height = _ph;
        _stage.Children.Add(image);
        _stage.Children.Add(_ink.Element);
        foreach (var dim in new[] { _dimTop, _dimBottom, _dimLeft, _dimRight }) _stage.Children.Add(dim);
        _outline.SetResourceReference(Shape.StrokeProperty, "AppAccent");
        _stage.Children.Add(_outline);
        for (int i = 0; i < _handles.Length; i++)
        {
            var handle = new Rectangle { Fill = Brushes.White, IsHitTestVisible = false };
            handle.SetResourceReference(Shape.StrokeProperty, "AppAccent");
            _handles[i] = handle;
            _stage.Children.Add(handle);
        }
        _stage.Children.Add(_eraserRing);

        _view = new Border { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)), Child = _stage, Focusable = false };
        _view.SizeChanged += (_, _) => { if (!_fitted && _view.ActualWidth > 0) { _fitted = true; FitWidth(); } else ApplyView(); };
        _view.MouseLeftButtonDown += View_LeftDown;
        _view.MouseDown += View_MiddleDown;
        _view.MouseMove += View_Move;
        _view.MouseUp += View_Up;
        _view.LostMouseCapture += (_, _) => EndGesture(commit: true);
        _view.MouseLeave += (_, _) => { if (_gesture == Gesture.None) _eraserRing.Visibility = Visibility.Collapsed; };
        _view.PreviewMouseWheel += View_Wheel;

        _vBar.Scroll += (_, _) => { _offY = _vBar.Value; ApplyView(); };
        _hBar.Scroll += (_, _) => { _offX = _hBar.Value; ApplyView(); };

        var viewGrid = new Grid();
        viewGrid.ColumnDefinitions.Add(new ColumnDefinition());
        viewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        viewGrid.RowDefinitions.Add(new RowDefinition());
        viewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        viewGrid.Children.Add(_view);
        Grid.SetColumn(_vBar, 1);
        viewGrid.Children.Add(_vBar);
        Grid.SetRow(_hBar, 1);
        viewGrid.Children.Add(_hBar);

        _edgeScroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _edgeScroll.Tick += (_, _) => ScrollWhileDragging();

        // ---- the toolbar ----
        _annotateButton = new ToggleButton
        {
            Content = LocalizationService.Get("S.Toolbar.Annotate"),
            MinWidth = 64,
            Height = 30,
            FontSize = 12,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 10, 0),
        };
        _annotateButton.SetResourceReference(StyleProperty, "ToolbarStackToggleButton");
        _annotateButton.Click += (_, _) => SetAnnotating(_annotateButton.IsChecked == true);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_annotateButton);
        left.Children.Add(IconButton("", "S.LongShot.ZoomOut", () => ZoomBy(1 / 1.25, null)));
        left.Children.Add(_zoomText);
        left.Children.Add(IconButton("", "S.LongShot.ZoomIn", () => ZoomBy(1.25, null)));
        left.Children.Add(TextButton("S.LongShot.FitWidth", FitWidth));
        left.Children.Add(TextButton("S.LongShot.ActualSize", ActualSize));

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(IconButton("", "S.LongShot.Copy", Copy, accent: true));
        right.Children.Add(IconButton("", "S.LongShot.Save", SaveAs));
        right.Children.Add(IconButton("", "S.LongShot.Pin", PinIt));
        right.Children.Add(IconButton("", "S.LongShot.Cancel", Close));

        var bar = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(left);
        bar.Children.Add(right);
        _toolbar = new Border { Background = Brushes.Transparent, Child = bar, Padding = new Thickness(0, 0, 0, 8) };
        _toolbar.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or DockPanel or StackPanel) DragMove(); };

        _status.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");
        foreach (var t in new[] { _status, _zoomText }) t.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        var footer = new Border { Child = _status, Padding = new Thickness(0, 8, 18, 0) };

        var layout = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(_toolbar);
        layout.Children.Add(footer);
        layout.Children.Add(viewGrid);

        var card = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Child = layout,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.16, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "HintCardBg");
        card.SetResourceReference(Border.BorderBrushProperty, "HintCardBorder");
        Content = new Border { Margin = new Thickness(14), Child = card };

        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        Deactivated += (_, _) => _spaceHeld = false;
        // Centred on the long screenshot's screen when it opens — again after the move there, if that
        // screen is of another scale — and left wherever the user puts it after that.
        SourceInitialized += (_, _) => Place(scale);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (!_placed) Place(scale); ApplyView(); }, DispatcherPriority.Loaded);
        ContentRendered += (_, _) => _placed = true;
        Closed += (_, _) => OnClosedCleanup();
        LocationChanged += (_, _) => PlacePanel();

        RenderCrop();
    }

    /// <summary>Opens the long screenshot for cropping and marking up, on the screen it was taken on.</summary>
    public static void Open(BitmapSource picture, System.Drawing.Rectangle region)
    {
        var window = new LongShotEditorWindow(picture, region);
        window.Show();
        window.Activate();
        Log.Info("Long screenshot {Width}x{Height}px opened in the editor", picture.PixelWidth, picture.PixelHeight);
    }

    private static BitmapSource Frozen(BitmapSource source)
    {
        source.Freeze();
        return source;
    }

    private static Rectangle Dim() => new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
        IsHitTestVisible = false,
    };

    // ======================================================================== the view

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private double ViewW => Math.Max(1, _view.ActualWidth);
    private double ViewH => Math.Max(1, _view.ActualHeight);

    private double MinZoom => MinZoomPercent / 100 / DpiScale;
    private double MaxZoom => MaxZoomPercent / 100 / DpiScale;

    /// <summary>Keeps the picture in the view (centred along a side where it is smaller) and shows it there.</summary>
    private void ApplyView()
    {
        double visW = ViewW / _zoom, visH = ViewH / _zoom;
        _offX = _pw <= visW ? -(visW - _pw) / 2 : Math.Clamp(_offX, 0, _pw - visW);
        _offY = _ph <= visH ? -(visH - _ph) / 2 : Math.Clamp(_offY, 0, _ph - visH);
        _stage.RenderTransform = new MatrixTransform(_zoom, 0, 0, _zoom, -_offX * _zoom, -_offY * _zoom);

        SetBar(_vBar, _ph, visH, _offY);
        SetBar(_hBar, _pw, visW, _offX);
        _zoomText.Text = $"{Math.Round(_zoom * DpiScale * 100)}%";
        RenderCrop();
        RenderEraserRing();
    }

    private static void SetBar(ScrollBar bar, double content, double visible, double offset)
    {
        bool needed = content > visible + 0.5;
        bar.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        if (!needed) return;
        bar.Minimum = 0;
        bar.Maximum = content - visible;
        bar.ViewportSize = visible;
        bar.LargeChange = visible * 0.9;
        bar.SmallChange = visible * 0.1;
        bar.Value = Math.Clamp(offset, 0, content - visible);
    }

    /// <summary>Zooms by <paramref name="factor"/>, keeping the picture under <paramref name="at"/> (view DIP) where it is; the view's centre by default.</summary>
    private void ZoomBy(double factor, Point? at)
    {
        FinishText();
        var p = at ?? new Point(ViewW / 2, ViewH / 2);
        double px = _offX + p.X / _zoom, py = _offY + p.Y / _zoom;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _offX = px - p.X / _zoom;
        _offY = py - p.Y / _zoom;
        ApplyView();
    }

    private void FitWidth()
    {
        FinishText();
        _zoom = Math.Clamp(Math.Min(ViewW / _pw, 1 / DpiScale), MinZoom, MaxZoom);
        _offX = 0;
        _offY = 0;
        ApplyView();
    }

    private void ActualSize()
    {
        var centre = new Point(ViewW / 2, ViewH / 2);
        ZoomBy(1 / DpiScale / _zoom, centre);
    }

    private void PanBy(double dxView, double dyView)
    {
        _offX += dxView / _zoom;
        _offY += dyView / _zoom;
        ApplyView();
    }

    private void View_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        int notches = e.Delta / 120;
        if (notches == 0) notches = Math.Sign(e.Delta);

        // While a mark is being made or typed, the wheel sizes it — as in a capture (#373).
        if (Annotating && (_gesture == Gesture.Draw || _textEditor is not null))
        {
            _panel!.NudgeSize(notches);
            return;
        }
        FinishText();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ZoomBy(Math.Pow(1.15, notches), e.GetPosition(_view));
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) PanBy(-e.Delta * 0.8, 0);
        else PanBy(0, -e.Delta * 0.8);
        if (_gesture != Gesture.None) Pointer(e.GetPosition(_view));
    }

    // ======================================================================== the crop

    private void RenderCrop()
    {
        double l = _left, t = _top, r = _right, b = _bottom;
        Put(_dimTop, 0, 0, _pw, t);
        Put(_dimBottom, 0, b, _pw, _ph - b);
        Put(_dimLeft, 0, t, l, b - t);
        Put(_dimRight, r, t, _pw - r, b - t);

        // A line and handles of the same size on screen whatever the zoom.
        double px = 1 / _zoom;
        _outline.StrokeThickness = 2 * px;
        Put(_outline, l - px, t - px, r - l + 2 * px, b - t + 2 * px);
        double h = 8 * px, cx = (l + r) / 2, cy = (t + b) / 2;
        var at = new[] { (l, t), (cx, t), (r, t), (r, cy), (r, b), (cx, b), (l, b), (l, cy) };
        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i].StrokeThickness = px;
            Put(_handles[i], at[i].Item1 - h / 2, at[i].Item2 - h / 2, h, h);
            _handles[i].Visibility = Annotating ? Visibility.Collapsed : Visibility.Visible;
        }

        _status.Text = LocalizationService.Format("S.LongShot.EditorHint", _right - _left, _bottom - _top);
    }

    private static void Put(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }

    /// <summary>The crop edges a press at <paramref name="p"/> (picture pixels) takes: those within reach on screen.</summary>
    private int EdgesAt(Point p)
    {
        double reach = EdgeReach / _zoom;
        bool acrossY = p.Y >= _top - reach && p.Y <= _bottom + reach;
        bool acrossX = p.X >= _left - reach && p.X <= _right + reach;
        int edges = 0;
        if (acrossY && Math.Abs(p.X - _left) <= reach) edges |= EdgeLeft;
        else if (acrossY && Math.Abs(p.X - _right) <= reach) edges |= EdgeRight;
        if (acrossX && Math.Abs(p.Y - _top) <= reach) edges |= EdgeTop;
        else if (acrossX && Math.Abs(p.Y - _bottom) <= reach) edges |= EdgeBottom;
        return edges;
    }

    private static Cursor CursorFor(int edges) => edges switch
    {
        EdgeLeft or EdgeRight => Cursors.SizeWE,
        EdgeTop or EdgeBottom => Cursors.SizeNS,
        EdgeLeft | EdgeTop or EdgeRight | EdgeBottom => Cursors.SizeNWSE,
        _ => Cursors.SizeNESW,
    };

    private void DragEdgesTo(Point p)
    {
        int x = (int)Math.Round(Math.Clamp(p.X, 0, _pw));
        int y = (int)Math.Round(Math.Clamp(p.Y, 0, _ph));
        if ((_dragEdges & EdgeLeft) != 0) _left = Math.Clamp(x, 0, _right - MinKeep);
        if ((_dragEdges & EdgeRight) != 0) _right = Math.Clamp(x, _left + MinKeep, _pw);
        if ((_dragEdges & EdgeTop) != 0) _top = Math.Clamp(y, 0, _bottom - MinKeep);
        if ((_dragEdges & EdgeBottom) != 0) _bottom = Math.Clamp(y, _top + MinKeep, _ph);
        RenderCrop();
    }

    // ======================================================================== the pointer

    private Point OnPicture(Point view) => new(_offX + view.X / _zoom, _offY + view.Y / _zoom);

    private void View_LeftDown(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(_view);
        var p = OnPicture(at);
        e.Handled = true;
        Focus();

        if (_spaceHeld)
        {
            BeginPan(at);
            return;
        }

        if (Annotating)
        {
            // A press while words are being typed only finishes them.
            if (_textEditor is not null)
            {
                FinishText();
                return;
            }
            BeginMark(p);
            return;
        }

        int edges = EdgesAt(p);
        if (edges != 0)
        {
            _gesture = Gesture.Edges;
            _dragEdges = edges;
            _view.CaptureMouse();
            _edgeScroll.Start();
            return;
        }
        BeginPan(at);
    }

    private void View_MiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _gesture != Gesture.None) return;
        e.Handled = true;
        BeginPan(e.GetPosition(_view));
    }

    private void BeginPan(Point at)
    {
        FinishText();
        _gesture = Gesture.Pan;
        _panFrom = at;
        _panOffX = _offX;
        _panOffY = _offY;
        _view.Cursor = Cursors.SizeAll;
        _view.CaptureMouse();
    }

    private void View_Move(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(_view);
        if (_gesture == Gesture.None)
        {
            var p = OnPicture(at);
            _lastPointer = p;
            _view.Cursor = _spaceHeld ? Cursors.Hand
                : Annotating ? MarkCursor()
                : EdgesAt(p) is var edges and not 0 ? CursorFor(edges) : Cursors.Hand;
            RenderEraserRing();
            return;
        }
        Pointer(at);
    }

    /// <summary>The pointer at <paramref name="at"/> (view DIP) during a press: what it is doing follows it.</summary>
    private void Pointer(Point at)
    {
        switch (_gesture)
        {
            case Gesture.Pan:
                _offX = _panOffX - (at.X - _panFrom.X) / _zoom;
                _offY = _panOffY - (at.Y - _panFrom.Y) / _zoom;
                ApplyView();
                break;
            case Gesture.Edges:
                DragEdgesTo(OnPicture(at));
                break;
            case Gesture.Draw:
                ExtendMark(OnPicture(at));
                break;
        }
    }

    private void View_Up(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.None) return;
        if (_gesture == Gesture.Pan && e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        if (_gesture != Gesture.Pan && e.ChangedButton != MouseButton.Left) return;
        EndGesture(commit: true);
    }

    private void EndGesture(bool commit)
    {
        var gesture = _gesture;
        if (gesture == Gesture.None) return;
        _gesture = Gesture.None;
        _dragEdges = 0;
        _edgeScroll.Stop();
        if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
        if (gesture == Gesture.Draw)
        {
            if (commit) FinishMark();
            else AbandonMark();
        }
    }

    /// <summary>Dragging an edge or drawing near the side of the view: the view scrolls that way, faster the closer.</summary>
    private void ScrollWhileDragging()
    {
        if (_gesture is not (Gesture.Edges or Gesture.Draw)) return;
        var at = Mouse.GetPosition(_view);
        double Step(double v, double size) =>
            v < ScrollZone ? -(ScrollZone - v) : v > size - ScrollZone ? v - (size - ScrollZone) : 0;
        double dx = Math.Clamp(Step(at.X, ViewW), -ScrollZone, ScrollZone) * 0.6;
        double dy = Math.Clamp(Step(at.Y, ViewH), -ScrollZone, ScrollZone) * 0.6;
        if (dx == 0 && dy == 0) return;
        PanBy(dx, dy);
        Pointer(at);
    }

    // ======================================================================== 標記

    private void SetAnnotating(bool on)
    {
        if (on == Annotating) return;
        EndGesture(commit: true);
        if (on)
        {
            var prefs = SettingsService.Instance.Current.Capture.Annotation;
            var panel = new AnnotationPanelWindow(prefs.StartingTool(), prefs) { Owner = this };
            panel.SettingsChanged += (_, _) =>
            {
                if (panel.Tool != AnnotationTool.Text) FinishText();
                ApplyTextStyle();
                _view.Cursor = MarkCursor();
                RenderEraserRing();
            };
            panel.UndoRequested += (_, _) => Undo();
            panel.RedoRequested += (_, _) => Redo();
            _panel = panel;
            panel.Show();
            PlacePanel();
            panel.SetHistoryState(_historyIndex > 0, _historyIndex < _history.Count - 1);
        }
        else
        {
            FinishText();
            var panel = _panel;
            _panel = null;
            SettingsService.Instance.Save();
            panel?.Close();
            _eraserRing.Visibility = Visibility.Collapsed;
        }
        _annotateButton.IsChecked = on;
        RenderCrop();
    }

    private void PlacePanel()
    {
        if (_panel is null || !_toolbar.IsLoaded) return;
        var topLeft = _toolbar.PointToScreen(new Point(0, 0));
        var bottomRight = _toolbar.PointToScreen(new Point(_toolbar.ActualWidth, _toolbar.ActualHeight));
        _panel.PlaceNear(new Rect(topLeft, bottomRight), DpiScale);
    }

    private AnnotationTool Tool => _panel?.Tool ?? AnnotationTool.Pen;

    private Cursor MarkCursor()
    {
        var tool = Tool;
        return tool == AnnotationTool.Eraser ? Cursors.None
            : AnnotationStroke.IsShapeTool(tool) || AnnotationStroke.IsRegionTool(tool) || tool == AnnotationTool.Number ? Cursors.Cross
            : tool == AnnotationTool.Text ? Cursors.IBeam
            : Cursors.Pen;
    }

    private Point? _lastPointer;

    private double EraserRadius => Math.Max(1, (_panel?.Thickness ?? 20) / 2);

    private void RenderEraserRing()
    {
        if (!Annotating || Tool != AnnotationTool.Eraser || _lastPointer is not { } p || _spaceHeld)
        {
            _eraserRing.Visibility = Visibility.Collapsed;
            return;
        }
        double r = EraserRadius;
        _eraserRing.StrokeThickness = 1 / _zoom;
        Put(_eraserRing, p.X - r, p.Y - r, 2 * r, 2 * r);
        _eraserRing.Visibility = Visibility.Visible;
    }

    /// <summary>The surface the marks are laid on: the whole picture, at its own pixels, made on the first mark.</summary>
    private void EnsureInk()
    {
        if (_inkReady) return;
        _inkReady = true;
        _ink.Backdrop = _picture;
        _ink.Ensure(new Rect(0, 0, _pw, _ph), 1);
    }

    private void BeginMark(Point p)
    {
        var panel = _panel!;
        var tool = panel.Tool;
        if (tool == AnnotationTool.Text)
        {
            BeginText(p);
            return;
        }
        if (tool == AnnotationTool.Number)
        {
            int next = Strokes.Count(s => s.Tool == AnnotationTool.Number) + 1;
            Commit(new AnnotationStroke
            {
                Tool = AnnotationTool.Number,
                Color = panel.InkColor,
                Thickness = panel.Thickness,
                Opacity = 1,
                Points = [p],
                Text = next.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            return;
        }

        _gesture = Gesture.Draw;
        _view.CaptureMouse();
        _edgeScroll.Start();

        if (tool == AnnotationTool.Eraser)
        {
            EnsureInk();
            _erasePoints = [p];
            _eraseLast = p;
            _eraseTookSomething = _ink.Erase(p, p, EraserRadius);
            return;
        }

        _wetLayer.Clear();
        if (!_stage.Children.Contains(_wetLayer)) _stage.Children.Insert(_stage.Children.IndexOf(_ink.Element) + 1, _wetLayer);
        if (AnnotationStroke.IsRegionTool(tool) || AnnotationStroke.IsShapeTool(tool))
        {
            _shapeStart = p;
            _shapeEnd = p;
            if (AnnotationStroke.IsRegionTool(tool))
                _wetLayer.BeginShape(Color.FromArgb(0xD0, 0x80, 0x80, 0x80), 1.5 / _zoom, AnnotationTool.Rectangle, p);
            else
                _wetLayer.BeginShape(panel.InkColor, panel.Thickness, tool, p);
            return;
        }

        _wetPoints = [p];
        _wetLayer.Begin(panel.InkColor, panel.Thickness, StrokeOpacity,
            tool == AnnotationTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round, p);
    }

    private double StrokeOpacity => Tool == AnnotationTool.Highlighter ? _panel!.InkOpacity : 1.0;

    private static bool FarEnough(Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return dx * dx + dy * dy >= MinPointDistance * MinPointDistance;
    }

    private void ExtendMark(Point p)
    {
        _lastPointer = p;
        RenderEraserRing();
        if (_erasePoints is not null)
        {
            if (!FarEnough(_eraseLast, p)) return;
            if (_ink.Erase(_eraseLast, p, EraserRadius)) _eraseTookSomething = true;
            _erasePoints.Add(p);
            _eraseLast = p;
            return;
        }
        if (_shapeStart is { } start)
        {
            _shapeEnd = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? AnnotationStroke.ConstrainShape(Tool, start, p) : p;
            _wetLayer.ReshapeTo(start, _shapeEnd);
            return;
        }
        if (_wetPoints is null || !FarEnough(_wetPoints[^1], p)) return;
        _wetPoints.Add(p);
        _wetLayer.Extend(p);
    }

    private void FinishMark()
    {
        var panel = _panel;
        if (panel is null)
        {
            AbandonMark();
            return;
        }

        if (_erasePoints is { } rubbed)
        {
            bool took = _eraseTookSomething;
            _erasePoints = null;
            _eraseTookSomething = false;
            if (took)
                Commit(new AnnotationStroke
                {
                    Tool = AnnotationTool.Eraser,
                    Color = Colors.Transparent,
                    Thickness = EraserRadius * 2,
                    Opacity = 1,
                    Points = rubbed,
                }, alreadyPainted: true);
            return;
        }

        if (_shapeStart is { } start)
        {
            var end = _shapeEnd;
            ClearWet();
            double dx = Math.Abs(end.X - start.X), dy = Math.Abs(end.Y - start.Y);
            double extent = panel.Tool is AnnotationTool.Line or AnnotationTool.Arrow ? Math.Sqrt(dx * dx + dy * dy) : Math.Max(dx, dy);
            if (extent * _zoom < MinShapeExtent) return;
            Commit(new AnnotationStroke
            {
                Tool = panel.Tool,
                Color = panel.InkColor,
                Thickness = panel.Thickness,
                Opacity = 1,
                Points = [start, end],
            });
            return;
        }

        if (_wetPoints is { } points)
        {
            ClearWet();
            Commit(new AnnotationStroke
            {
                Tool = panel.Tool,
                Color = panel.InkColor,
                Thickness = panel.Thickness,
                Opacity = StrokeOpacity,
                Points = points,
            });
        }
    }

    private void ClearWet()
    {
        _stage.Children.Remove(_wetLayer);
        _wetLayer.Clear();
        _wetPoints = null;
        _shapeStart = null;
    }

    private void AbandonMark()
    {
        bool wasRubbing = _erasePoints is not null;
        _erasePoints = null;
        _eraseTookSomething = false;
        ClearWet();
        if (wasRubbing) Replay();
    }

    private void Commit(AnnotationStroke stroke, bool alreadyPainted = false)
    {
        EnsureInk();
        if (!alreadyPainted) _ink.Lay(stroke);
        if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add([.. Strokes, stroke]);
        if (_history.Count > HistoryLimit) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
        _panel?.SetHistoryState(_historyIndex > 0, false);
    }

    private void Undo()
    {
        if (_historyIndex == 0) return;
        FinishText();
        _historyIndex--;
        Replay();
    }

    private void Redo()
    {
        if (_historyIndex >= _history.Count - 1) return;
        FinishText();
        _historyIndex++;
        Replay();
    }

    private void Replay()
    {
        EnsureInk();
        _ink.Replay(Strokes);
        _panel?.SetHistoryState(_historyIndex > 0, _historyIndex < _history.Count - 1);
    }

    // ---- 文字 ----

    private static FontFamily TextFamily =>
        System.Windows.Application.Current?.TryFindResource("AppFont") as FontFamily ?? new FontFamily("Segoe UI");

    /// <summary>Physical pixels of the screen per pixel of the picture, at the zoom the words are typed at.</summary>
    private double ScreenPerPixel => _zoom * DpiScale;

    private void BeginText(Point p)
    {
        var editor = new AnnotationTextEditor(s => ViewContainsPhysical(s) || _panel?.ContainsPhysical(s) == true);
        editor.Finished += (_, _) => CommitText(editor);
        var tl = _view.PointToScreen(new Point(0, 0));
        var br = _view.PointToScreen(new Point(ViewW, ViewH));
        editor.AnchorBounds = new Rect(tl, br);
        _textEditor = editor;
        ApplyTextStyle();
        editor.ShowAt(_view.PointToScreen(new Point((p.X - _offX) * _zoom, (p.Y - _offY) * _zoom)));
    }

    private bool ViewContainsPhysical(Point physical)
    {
        var local = _view.PointFromScreen(physical);
        return local.X >= 0 && local.Y >= 0 && local.X <= ViewW && local.Y <= ViewH;
    }

    private void ApplyTextStyle()
    {
        if (_textEditor is not { } editor || _panel is not { } panel) return;
        double k = ScreenPerPixel;
        editor.ApplyStyle(TextFamily, Math.Max(1, (int)Math.Round(panel.TextSize * k)), panel.InkColor,
            panel.TextBold, panel.TextItalic, panel.TextUnderline, panel.TextStrikethrough,
            panel.TextOutlineColor, (int)Math.Round(panel.TextOutlineWidth * k));
    }

    private void FinishText() => _textEditor?.Finish();

    private void CommitText(AnnotationTextEditor editor)
    {
        if (!ReferenceEquals(_textEditor, editor)) return;
        _textEditor = null;
        var text = editor.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text) || _panel is not { } panel) return;

        var origin = _view.PointFromScreen(editor.TextOriginPhysical);
        Commit(new AnnotationStroke
        {
            Tool = AnnotationTool.Text,
            Color = panel.InkColor,
            Thickness = 0,
            Opacity = 1,
            Points = [OnPicture(origin)],
            Text = text,
            TextStyle = new AnnotationTextStyle(
                TextFamily, panel.TextSize,
                panel.TextBold, panel.TextItalic, panel.TextUnderline, panel.TextStrikethrough,
                panel.TextOutlineColor, panel.TextOutlineWidth),
        });
    }

    // ======================================================================== keys

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_gesture != Gesture.None) EndGesture(commit: false);
                else Close();
                break;
            case Key.Enter:
                e.Handled = true;
                Copy();
                break;
            case Key.Space:
                e.Handled = true;
                if (!_spaceHeld)
                {
                    _spaceHeld = true;
                    if (_gesture == Gesture.None) _view.Cursor = Cursors.Hand;
                    RenderEraserRing();
                }
                break;
            case Key.Z when ctrl:
                e.Handled = true;
                Undo();
                break;
            case Key.Y when ctrl:
                e.Handled = true;
                Redo();
                break;
            case Key.OemPlus or Key.Add when ctrl:
                e.Handled = true;
                ZoomBy(1.25, null);
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                e.Handled = true;
                ZoomBy(1 / 1.25, null);
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                e.Handled = true;
                ActualSize();
                break;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        e.Handled = true;
        _spaceHeld = false;
        if (_gesture == Gesture.None) _view.Cursor = Annotating ? MarkCursor() : Cursors.Hand;
        RenderEraserRing();
    }

    // ======================================================================== the ends

    /// <summary>What is kept, with the marks on it, at the picture's own pixels.</summary>
    private BitmapSource Compose()
    {
        FinishText();
        _ink.Flush();
        int w = _right - _left, h = _bottom - _top;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            dc.DrawImage(_picture, new Rect(-_left, -_top, _pw, _ph));
            if (Strokes.Count > 0 && _ink.Source is { } ink)
            {
                var b = _ink.Bounds;
                dc.DrawImage(ink, new Rect(b.X - _left, b.Y - _top, b.Width, b.Height));
            }
            dc.Pop();
        }
        var composed = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        composed.Render(visual);
        composed.Freeze();
        return composed;
    }

    private void Copy()
    {
        var picture = Compose();
        try
        {
            System.Windows.Clipboard.SetImage(picture);
            if (SettingsService.Instance.Current.SaveScreenshotToDisk)
                ScreenshotSaveService.Save(picture, SettingsService.Instance.Current.ScreenshotSavePath);
        }
        catch (Exception ex)
        {
            // Another program can hold the clipboard for a moment: still open, so it can be tried again.
            Log.Warn(ex, "Copying the edited long screenshot failed");
            return;
        }
        Log.Info("Edited long screenshot copied, {Width}x{Height}px", picture.PixelWidth, picture.PixelHeight);
        Close();
    }

    private void SaveAs()
    {
        if (ScreenshotSaveService.SaveAs(this, Compose(), "S.LongShot.EditorTitle")) Close();
    }

    private void PinIt()
    {
        var picture = Compose();
        Close();
        Pin.PinWindow.Pin(picture, _region.Left, _region.Top);
    }

    private void OnClosedCleanup()
    {
        _edgeScroll.Stop();
        FinishText();
        if (_panel is { } panel)
        {
            _panel = null;
            SettingsService.Instance.Save();
            panel.Close();
        }
    }

    // ======================================================================== pieces

    private Button IconButton(string glyph, string tipKey, Action click, bool accent = false)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 34,
            Height = 30,
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 14,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        };
        button.SetResourceReference(StyleProperty, accent ? "AccentButton" : "FlatButton");
        button.SetResourceReference(ToolTipProperty, tipKey);
        System.Windows.Automation.AutomationProperties.SetName(button, LocalizationService.Get(tipKey));
        button.Click += (_, _) => click();
        return button;
    }

    private Button TextButton(string key, Action click)
    {
        var button = new Button
        {
            Content = LocalizationService.Get(key),
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(4, 0, 0, 0),
        };
        button.SetResourceReference(StyleProperty, "FlatButton");
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Centred on the screen the long screenshot was taken on, in physical pixels.</summary>
    private void Place(double scale)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var work = System.Windows.Forms.Screen.FromRectangle(_region).WorkingArea;
        int w = (int)Math.Ceiling(Width * scale), h = (int)Math.Ceiling(Height * scale);
        ScreenGeometry.MoveToPhysical(this,
            work.Left + Math.Max(0, (work.Width - w) / 2),
            work.Top + Math.Max(0, (work.Height - h) / 2));
    }
}
