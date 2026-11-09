using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using OverTranslate.Services;
using WPoint = System.Windows.Point;
using WRect = System.Windows.Rect;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace OverTranslate.Views.Capture;

public partial class ScreenCaptureWindow : Window
{
    private const int WM_NCHITTEST  = 0x0084;
    private const int HTTRANSPARENT = -1;
    private static readonly Uri CrosshairCursorUri = new("pack://application:,,,/Resources/Cursors/capture_crosshair.cur", UriKind.Absolute);

    /// <summary>Corner radius, in DIP, of a box set to 圓角 — and of the picture it gives.</summary>
    public const double RoundedCornerDip = 8;

    /// <summary>
    /// Corner radius of the frame and of the uncovered area under it: square or rounded as the size
    /// bar says, so what is framed is the shape the copied picture will have.
    /// </summary>
    private static double FrameCorner =>
        SettingsService.Instance.Current.Capture.RoundedCorners ? RoundedCornerDip : 0;

    /// <summary>The frozen picture, mapped back over the box — see SelectionBright in the XAML.</summary>
    private ImageBrush? _brightBrush;

    /// <summary>Width over height the box keeps while 鎖定長寬比 is on, taken when it was last set.</summary>
    private double _aspectRatio = 1;

    /// <summary>A move or resize waiting to be announced with the next frame — see RaiseAdjusted.</summary>
    private bool _adjustPending;

    /// <summary>Whether a right press was taken by this window, so its release is swallowed too.</summary>
    private bool _rightPressed;

    private HwndSource? _hwndSource;
    private bool _inBackgroundMode;
    private bool _cloaked;

    private Bitmap _screenshot;
    private readonly System.Drawing.Rectangle _physBounds;
    private TaskCompletionSource<bool> _selectionTcs = new();

    private WPoint _startPoint;
    private Rect _selectionWpfRect;
    private bool _isDragging;
    private bool _processingStarted;
    private bool _hasSelection;

    /// <summary>
    /// Whether 標記 has the box, so its handles and its move area are out of the way.
    /// </summary>
    /// <remarks>
    /// The same withdrawal a running translation causes, for the same reason and by a separate flag:
    /// both mean the box is not the user's to rearrange right now, but they start and end
    /// independently — a translation can finish while the pen is still in hand, and the handles must
    /// not come back underneath it.
    /// </remarks>
    private bool _annotationHoldsSelection;

    public Rect Selection { get; private set; }
    public Bitmap? CroppedBitmap { get; private set; }
    public bool HasSelection => _hasSelection;

    /// <summary>Whether the box is the user's to change: drawn or not, and no translation holding it.</summary>
    public bool IsEditable => !_processingStarted;

    /// <summary>The desktop rectangle, in physical pixels, the frozen picture was taken of.</summary>
    public System.Drawing.Rectangle PhysicalBounds => _physBounds;

    /// <summary>
    /// The frozen desktop as this window shows it: pixel (0,0) at the window's DIP (0,0), at the
    /// window's scale. What the overlay's 馬賽克 and 模糊 are made from.
    /// </summary>
    public BitmapSource? ScreenshotSource => ScreenshotImage.Source as BitmapSource;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    public ScreenCaptureWindow(Bitmap screenshot, System.Drawing.Rectangle physBounds)
    {
        _screenshot = screenshot;
        _physBounds = physBounds;
        InitializeComponent();

        // Provisional: OnSourceInitialized replaces this with the pixel rect the screenshot was
        // captured from. Needed only so the window has a size before its handle exists.
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Cursor = LoadCrosshairCursor();

        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= AnnounceAdjusted;
            UseHoverPicker(null);
            _selectionTcs.TrySetResult(false);
            CroppedBitmap?.Dispose();
            CroppedBitmap = null;
            _screenshot.Dispose();
        };
    }

    public Task<bool> WaitForSelectionAsync() => _selectionTcs.Task;

    private System.Drawing.Rectangle? _preset;

    /// <summary>
    /// Starts the capture with the box already drawn over <paramref name="physical"/> — a pinned
    /// picture being edited or translated — so it goes straight to the toolbar. Call before Show.
    /// </summary>
    public void PresetSelection(System.Drawing.Rectangle physical) => _preset = physical;

    /// <summary>Puts the preset box up as if it had just been dragged out. Needs the window's scale.</summary>
    private void ApplyPreset(System.Drawing.Rectangle r)
    {
        _selectionWpfRect = new Rect(
            (r.Left - _physBounds.Left) / _dpiX, (r.Top - _physBounds.Top) / _dpiY,
            r.Width / _dpiX, r.Height / _dpiY);
        _hasSelection = true;
        _aspectRatio = AspectOf(_selectionWpfRect);
        HintHost.Visibility = Visibility.Collapsed;
        UpdateSelectionMetadata();
        UpdateSelectionVisuals();
        _selectionTcs.TrySetResult(true);
    }

    /// <summary>
    /// Raised with the new selection, in physical pixels, whenever the user moves or resizes a
    /// settled box — so the toolbar anchored to it can follow rather than sit next to where the box
    /// used to be. Never raised while the box is first being drawn: nothing is anchored to it yet.
    /// </summary>
    public event EventHandler<Rect>? SelectionAdjusted;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // The first frame has been drawn behind the cloak, so this puts up the frozen desktop in
        // one step. See OnSourceInitialized.
        if (_cloaked)
        {
            WindowScreenPresence.SetCloaked(new WindowInteropHelper(this).Handle, cloaked: false);
            _cloaked = false;
        }

        // Claimed only once the first frame is on screen. Activating before that forces a
        // foreground switch while the window is still empty, which flashes its black background.
        Activate();

        // The pointer is already somewhere; what it is on is offered without waiting for it to move.
        RefreshHover();
    }

    // The dim layer's outer edge is the window's, so it is laid down with the first layout rather
    // than after the first frame: that frame is the one uncloaking reveals, and it has to be the
    // finished picture, dim included.
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_processingStarted) UpdateDimLayer();
    }

    // One card per monitor, positioned at each screen's bottom-left corner (anchored by its bottom
    // edge, so the card's own height never needs measuring). This window spans the whole virtual
    // desktop, so a single corner-anchored card would land on whichever monitor happens to hold
    // that corner — often not the one being looked at.
    private List<HintSpot> BuildHintSpots()
    {
        const double margin = 12;

        return System.Windows.Forms.Screen.AllScreens
            .Select(screen =>
            {
                // This window renders at a single DPI across the whole desktop, so a card sitting on
                // a monitor at another scale comes out the wrong physical size. Scaling it by the
                // ratio between the two restores the size the monitor's own DPI would have given.
                double relScale = ScreenGeometry.ScaleAt(
                    screen.Bounds.Left + screen.Bounds.Width  / 2,
                    screen.Bounds.Top  + screen.Bounds.Height / 2) / _dpiX;

                return new HintSpot(
                    (screen.Bounds.Left - _physBounds.Left) / _dpiX + margin * relScale,
                    // Above the taskbar: the working area's bottom is the taskbar's top when it is
                    // docked at the bottom, and the screen's own bottom otherwise.
                    (_physBounds.Bottom - screen.WorkingArea.Bottom) / _dpiY + margin * relScale,
                    relScale);
            })
            .ToList();
    }

    // Window-local DIP position of one hint card (left edge, and distance of its bottom edge from
    // the window's bottom), and the scale that makes it read the same physical size as the monitor
    // it sits on would give it.
    private sealed record HintSpot(double X, double Bottom, double Scale);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);

        // Hidden from the screen until OnContentRendered. Between being shown and presenting its
        // first frame a window has nothing to put up but black, for somewhere between 40ms and, on
        // the first capture after launch, 100ms — the flash users see. Hiding it with Opacity does
        // not help: this window is not layered, so a transparent one is still a black one. Cloaked,
        // it goes through all of that off screen and appears already painted.
        _cloaked = WindowScreenPresence.SetCloaked(_hwndSource.Handle, cloaked: true);

        // Before the DPI is read: pinning the window settles which monitor it belongs to, and that
        // is what the DPI below describes.
        ScreenGeometry.PinPhysicalBounds(this, _physBounds);

        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            _dpiX = src.CompositionTarget.TransformToDevice.M11;
            _dpiY = src.CompositionTarget.TransformToDevice.M22;
        }

        // Tag the capture with the DPI that makes its DIP size equal this window's, so WPF maps it
        // 1:1 instead of rescaling a full virtual-desktop image on the first frame. At 96 DPI a
        // 5120px-wide capture claims to be 5120 DIP while the window is only ~4130 DIP, and that
        // mismatch is paid for on every render of the largest visual in the app.
        ScreenshotImage.Source = BitmapToDisplaySource(_screenshot, 96.0 * _dpiX);
        _brightBrush = new ImageBrush(ScreenshotImage.Source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        SelectionBright.Fill = _brightBrush;
        RenderSizeBarOptions();

        // Filled as soon as the DPI is known, so the cards take part in the window's first layout
        // pass and are already painted when it becomes visible. (They used to be deferred to
        // OnContentRendered so their reveal animation would not play against a hidden window; with
        // no animation left, deferring only adds work between the first frame and the reveal.)
        HintHost.ItemsSource = BuildHintSpots();

        if (_preset is { } preset) ApplyPreset(preset);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_inBackgroundMode && msg == WM_NCHITTEST)
        {
            handled = true;
            return (IntPtr)HTTRANSPARENT;
        }
        return IntPtr.Zero;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Esc must cancel at any stage, including after processing started. This is the fallback
        // path for when OverlayWindow's low-level keyboard hook is not in play (not yet installed,
        // already torn down, or dropped by Windows) — without it, a session that fails mid-flight
        // leaves this full-screen dim window on top of everything with no way to close it.
        if (e.Key == Key.Escape)
        {
            _selectionTcs.TrySetResult(false);
            Close();
            return;
        }

        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && CanPin)
        {
            var trigger = SettingsService.Instance.Current.Capture.PinSelectionTrigger;
            if ((e.Key == Key.T && trigger == Models.PinSelectionTrigger.CtrlT) ||
                (e.Key == Key.P && trigger == Models.PinSelectionTrigger.CtrlP))
            {
                e.Handled = true;
                PinRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // Same cancellation as Esc. The button lives inside the hint cards, which are collapsed the
    // moment a drag begins, so this only ever runs while no selection exists.
    private void CancelCaptureBtn_Click(object sender, RoutedEventArgs e)
    {
        _selectionTcs.TrySetResult(false);
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_processingStarted || _hasSelection) return;
        base.OnMouseLeftButtonDown(e);
        _startPoint  = e.GetPosition(this);
        // Asked before the drag starts: a press on an offered box is a click on it until it moves.
        bool onHover = BeginPressOnHover();
        _isDragging  = true;
        if (onHover)
        {
            CaptureMouse();
            return;
        }
        HintHost.Visibility      = Visibility.Collapsed;
        SetFrameVisibility(true);
        CaptureMouse();
        DrawRect(_startPoint, _startPoint);
    }

    /// <summary>
    /// Raised when a double-click inside the settled box asks for the picture to be copied and the
    /// capture ended (Snipaste's way of finishing). Only raised while 雙擊完成 is on.
    /// </summary>
    public event EventHandler? FinishRequested;

    /// <summary>
    /// Raised when a right-click on a drawn box asks for it to go, the picture staying frozen for
    /// another one (Snipaste's way). Only raised while 右鍵取消 is on. Raised rather than handled
    /// here, because the session also owns a toolbar and an overlay that have to go with the box;
    /// it calls <see cref="ClearSelection"/> once they have.
    /// </summary>
    public event EventHandler? ReselectRequested;

    /// <summary>Raised by the size bar's 重新截圖 button, the same as F5.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the 貼圖 gesture set for captures asks for the selection to be pinned.</summary>
    public event EventHandler? PinRequested;

    private bool CanPin => _hasSelection && !_isDragging;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == System.Windows.Input.MouseButton.Middle && CanPin &&
            SettingsService.Instance.Current.Capture.PinSelectionTrigger == Models.PinSelectionTrigger.MiddleClick)
        {
            e.Handled = true;
            PinRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // A double-click on the box itself, while it is still the user's to edit. Asked in the
        // preview pass because the move thumb under the pointer takes the ordinary press for itself.
        if (e.ClickCount == 2 && _hasSelection && !_processingStarted && !_annotationHoldsSelection &&
            !SizeBar.IsMouseOver &&
            SettingsService.Instance.Current.Capture.DoubleClickToFinish &&
            _selectionWpfRect.Contains(e.GetPosition(this)))
        {
            e.Handled = true;
            FinishRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    // The press is only taken; what it does happens on the release. Closing on the press left the
    // release with nowhere to go but the desktop under the capture, which then opened its own
    // context menu as the capture vanished.
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_processingStarted) return;
        base.OnMouseRightButtonDown(e);
        if (!SettingsService.Instance.Current.Capture.RightClickToCancel) return;

        e.Handled = true;
        _rightPressed = true;

        // A box still being dragged out is dropped and nothing else: the right button pressed
        // mid-drag is a change of mind about that box.
        if (_isDragging)
        {
            _isDragging = false;
            CancelPressOnHover();
            ReleaseMouseCapture();
            SetFrameVisibility(false);
            SetHandlesVisibility(false);
            UpdateDimLayer();
            HintHost.Visibility = Visibility.Visible;
            _rightPressed = false;
            _swallowRightUp = true;
            RefreshHover();
        }
    }

    // Set when the press already did its work (a drag dropped), so its release does nothing more.
    private bool _swallowRightUp;

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_swallowRightUp)
        {
            _swallowRightUp = false;
            e.Handled = true;
            return;
        }
        if (!_rightPressed) return;
        _rightPressed = false;
        e.Handled = true;
        if (_processingStarted) return;

        // A drawn box goes and the picture stays for another one; with none drawn, the capture ends.
        if (_hasSelection)
        {
            ReselectRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _selectionTcs.TrySetResult(false);
        Close();
    }

    /// <summary>
    /// Takes the box away and starts over on the same frozen picture, as before the first drag.
    /// </summary>
    /// <remarks>
    /// The session has closed the toolbar and the overlay by now; their marks went with the overlay,
    /// so the layers hung here go too. A fresh wait is set up for <see cref="WaitForSelectionAsync"/>.
    /// </remarks>
    public void ClearSelection()
    {
        if (_processingStarted) return;
        _hasSelection = false;
        _isDragging = false;
        _annotationHoldsSelection = false;
        OverlayContentHost.Children.Clear();
        CroppedBitmap?.Dispose();
        CroppedBitmap = null;
        SetFrameVisibility(false);
        SetHandlesVisibility(false);
        UpdateDimLayer();
        HintHost.Visibility = Visibility.Visible;
        _selectionTcs = new TaskCompletionSource<bool>();
        RefreshHover();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        if (!_isDragging)
        {
            if (_picker is not null) UpdateHover(pos);
            return;
        }
        if (ContinuePressOnHover(pos)) return;
        DrawRect(_startPoint, pos);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isDragging) return;
        if (EndPressOnHover()) return;
        _isDragging = false;
        ReleaseMouseCapture();
        // Releasing the capture makes WPF send a mouse move straight away, while no box is kept yet,
        // so the auto-select offer has just framed whatever is under the pointer again. That offer
        // is dropped here, as SetSelectionFromCode does: left standing it kept the size bar's tools
        // hidden until the box was next moved.
        _hovering = false;
        _hoverChain = [];
        StopGlide();

        var rect = Normalize(_startPoint, e.GetPosition(this));
        _selectionWpfRect = rect;
        if (rect.Width < 4 || rect.Height < 4)
        {
            _hasSelection = false;
            SetFrameVisibility(false);
            SetHandlesVisibility(false);
            UpdateDimLayer();
            HintHost.Visibility = Visibility.Visible;
            RefreshHover();
            return;
        }

        _hasSelection = true;
        _aspectRatio = AspectOf(rect);
        UpdateSelectionMetadata();
        UpdateSelectionVisuals();

        _selectionTcs.TrySetResult(true);
    }

    /// <summary>
    /// Hands the box to 標記, or takes it back.
    /// </summary>
    /// <remarks>
    /// While the pen is in hand a drag inside the box is a stroke, so the thumb that would otherwise
    /// read that drag as "move the whole selection" has to go — one gesture cannot mean two things.
    /// The corner handles go with it rather than staying live: they sit on the edge, which is exactly
    /// where someone drawing to the boundary of the box puts the pointer, and a resize triggered by
    /// an over-run stroke is a worse outcome than having to leave the mode to resize.
    /// </remarks>
    public void SetAnnotationHold(bool held)
    {
        if (_annotationHoldsSelection == held) return;
        _annotationHoldsSelection = held;
        if (!_processingStarted) UpdateSelectionVisuals();
    }

    /// <summary>
    /// Crops the selection and locks the frame so the caller can work on a fixed region.
    /// </summary>
    /// <param name="lockedByThisCall">
    /// True when this call is what locked the frame, so only that caller may hand editing back —
    /// a frame already locked by an earlier run belongs to whoever locked it
    /// </param>
    public bool PrepareForProcessing(out bool lockedByThisCall)
    {
        lockedByThisCall = false;
        if (!_hasSelection) return false;

        if (_processingStarted)
            return CroppedBitmap != null;

        if (!TryCreateCroppedBitmap())
            return false;

        _processingStarted = true;
        lockedByThisCall = true;
        SwitchToBackgroundMode();
        return true;
    }

    /// <summary>
    /// Hands the frame back to the user after an action that locked it has nothing to show for it —
    /// a copy that only needed the text, or a pass that found none and leaves the box to be redrawn.
    /// </summary>
    public void RestoreSelectionEditing()
    {
        if (!_processingStarted) return;

        _processingStarted = false;
        _inBackgroundMode = false;
        _hwndSource?.RemoveHook(WndProc);
        Cursor = LoadCrosshairCursor();
        UpdateSelectionVisuals();
    }

    // Returns a clean crop of the ORIGINAL capture for the current selection, as a frozen
    // BitmapSource. Unlike a live screen grab, this never contains the selection rectangle or
    // resize handles (those are this window's chrome, not part of _screenshot). Returns null when
    // there is no usable selection.
    public BitmapSource? CreateSelectionImage()
    {
        if (!_hasSelection) return null;

        int bmpX = Math.Clamp((int)(Selection.X - _physBounds.Left), 0, _screenshot.Width - 1);
        int bmpY = Math.Clamp((int)(Selection.Y - _physBounds.Top),  0, _screenshot.Height - 1);
        int bmpW = Math.Min(Math.Max(1, (int)Selection.Width),  _screenshot.Width  - bmpX);
        int bmpH = Math.Min(Math.Max(1, (int)Selection.Height), _screenshot.Height - bmpY);
        if (bmpW <= 0 || bmpH <= 0) return null;

        using var crop = _screenshot.Clone(
            new System.Drawing.Rectangle(bmpX, bmpY, bmpW, bmpH),
            _screenshot.PixelFormat);
        return BitmapToDisplaySource(crop);
    }

    public void SwitchToBackgroundMode()
    {
        SetFrameVisibility(false);
        SetHandlesVisibility(false);

        Cursor = null;
        _inBackgroundMode = true;
        _hwndSource?.AddHook(WndProc);

        // Square-cornered from here on: the rounded area matched the frame's corners, and the frame
        // has just gone. What is left is the area the translation is painted into, a plain rectangle.
        UpdateDimLayer();
    }

    private static System.Windows.Input.Cursor LoadCrosshairCursor()
    {
        var streamInfo = System.Windows.Application.GetResourceStream(CrosshairCursorUri);
        if (streamInfo?.Stream == null)
            return System.Windows.Input.Cursors.Cross;

        return new System.Windows.Input.Cursor(streamInfo.Stream);
    }

    private void DrawRect(WPoint p1, WPoint p2)
    {
        var r = Normalize(p1, p2);
        _selectionWpfRect = r;
        PlaceFrame(r);
        UpdateDimLayer();
        UpdateSizeBar();
    }

    // The tint, the edge and the line outside it — see the XAML — are always given the same box.
    private void PlaceFrame(Rect r)
    {
        double corner = FrameCorner;
        PlacePart(SelectionFill, r, corner);
        PlacePart(SelectionRect, r, corner);
        var outside = r;
        outside.Inflate(1, 1);
        PlacePart(SelectionOutline, outside, corner > 0 ? corner + 1 : 0);

        static void PlacePart(System.Windows.Shapes.Rectangle part, Rect r, double corner)
        {
            System.Windows.Controls.Canvas.SetLeft(part, r.X);
            System.Windows.Controls.Canvas.SetTop(part,  r.Y);
            part.Width  = r.Width;
            part.Height = r.Height;
            part.RadiusX = corner;
            part.RadiusY = corner;
        }
    }

    private void SetFrameVisibility(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SelectionFill.Visibility = visibility;
        SelectionRect.Visibility = visibility;
        SelectionOutline.Visibility = visibility;
        UpdateSizeBar();
    }

    /// <summary>
    /// Uncovers the box being framed: the frozen picture laid back over the dim, exactly there.
    /// </summary>
    /// <remarks>
    /// Only this rectangle and its brush change as the pointer moves, so WPF repaints the box's old
    /// and new places and nothing else — see DimLayer in the XAML for what this replaced.
    /// </remarks>
    private void UpdateDimLayer()
    {
        var r = _selectionWpfRect;
        bool show = (_isDragging || _hasSelection || _hovering) && r.Width > 0 && r.Height > 0 && _brightBrush is not null;
        SelectionBright.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        _brightBrush!.Viewbox = r;
        System.Windows.Controls.Canvas.SetLeft(SelectionBright, r.X);
        System.Windows.Controls.Canvas.SetTop(SelectionBright, r.Y);
        SelectionBright.Width = r.Width;
        SelectionBright.Height = r.Height;
        double corner = _inBackgroundMode ? 0 : FrameCorner;
        SelectionBright.RadiusX = corner;
        SelectionBright.RadiusY = corner;
    }

    private void UpdateSelectionMetadata()
    {
        double absPhysX = _physBounds.Left + _selectionWpfRect.X * _dpiX;
        double absPhysY = _physBounds.Top  + _selectionWpfRect.Y * _dpiY;
        int bmpW = Math.Max(1, (int)(_selectionWpfRect.Width  * _dpiX));
        int bmpH = Math.Max(1, (int)(_selectionWpfRect.Height * _dpiY));
        Selection = new Rect(absPhysX, absPhysY, bmpW, bmpH);
    }

    private bool TryCreateCroppedBitmap()
    {
        UpdateSelectionMetadata();

        double absPhysX = Selection.X;
        double absPhysY = Selection.Y;
        int bmpW = Math.Max(1, (int)Selection.Width);
        int bmpH = Math.Max(1, (int)Selection.Height);

        int bmpX = Math.Clamp((int)(absPhysX - _physBounds.Left), 0, _screenshot.Width - 1);
        int bmpY = Math.Clamp((int)(absPhysY - _physBounds.Top),  0, _screenshot.Height - 1);
        bmpW = Math.Min(bmpW, _screenshot.Width  - bmpX);
        bmpH = Math.Min(bmpH, _screenshot.Height - bmpY);
        if (bmpW <= 0 || bmpH <= 0)
            return false;

        CroppedBitmap?.Dispose();
        CroppedBitmap = _screenshot.Clone(
            new System.Drawing.Rectangle(bmpX, bmpY, bmpW, bmpH),
            _screenshot.PixelFormat);
        Selection = new Rect(absPhysX, absPhysY, bmpW, bmpH);
        return true;
    }

    private void UpdateSelectionVisuals()
    {
        PlaceFrame(_selectionWpfRect);
        SetFrameVisibility(_hasSelection);

        System.Windows.Controls.Canvas.SetLeft(SelectionBody, _selectionWpfRect.X);
        System.Windows.Controls.Canvas.SetTop(SelectionBody,  _selectionWpfRect.Y);
        SelectionBody.Width  = _selectionWpfRect.Width;
        SelectionBody.Height = _selectionWpfRect.Height;

        const double halfHandle = 7;
        System.Windows.Controls.Canvas.SetLeft(TopLeftHandle, _selectionWpfRect.Left - halfHandle);
        System.Windows.Controls.Canvas.SetTop(TopLeftHandle, _selectionWpfRect.Top - halfHandle);
        System.Windows.Controls.Canvas.SetLeft(TopRightHandle, _selectionWpfRect.Right - halfHandle);
        System.Windows.Controls.Canvas.SetTop(TopRightHandle, _selectionWpfRect.Top - halfHandle);
        System.Windows.Controls.Canvas.SetLeft(BottomLeftHandle, _selectionWpfRect.Left - halfHandle);
        System.Windows.Controls.Canvas.SetTop(BottomLeftHandle, _selectionWpfRect.Bottom - halfHandle);
        System.Windows.Controls.Canvas.SetLeft(BottomRightHandle, _selectionWpfRect.Right - halfHandle);
        System.Windows.Controls.Canvas.SetTop(BottomRightHandle, _selectionWpfRect.Bottom - halfHandle);

        SetHandlesVisibility(_hasSelection && !_processingStarted && !_annotationHoldsSelection);
        UpdateDimLayer();
        UpdateSizeBar();
    }

    private void SetHandlesVisibility(bool visible)
    {
        var handleVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TopLeftHandle.Visibility = handleVisibility;
        TopRightHandle.Visibility = handleVisibility;
        BottomLeftHandle.Visibility = handleVisibility;
        BottomRightHandle.Visibility = handleVisibility;
        // Movable exactly as long as it is resizable: once translation has started the window goes
        // click-through and the box is no longer the user's to rearrange.
        SelectionBody.Visibility = handleVisibility;
    }

    /// <summary>
    /// Hangs the overlay's own layers in this window, bottom first, clipped to the box.
    /// </summary>
    /// <remarks>
    /// The elements come from the overlay and keep belonging to it — this window only gives them
    /// somewhere opaque to be drawn. Both windows are pinned to the same physical bounds, so a point
    /// means the same thing in either and nothing needs converting on the way across.
    /// </remarks>
    public void ShowOverlayContent(IReadOnlyList<UIElement> layers)
    {
        bool same = OverlayContentHost.Children.Count == layers.Count;
        if (same)
        {
            for (int i = 0; i < layers.Count; i++)
                if (!ReferenceEquals(OverlayContentHost.Children[i], layers[i])) { same = false; break; }
        }
        if (same) return;

        OverlayContentHost.Children.Clear();

        foreach (var layer in layers)
        {
            // An element belongs to one parent, and these start out in the overlay's own tree.
            // UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings,
            // so Panel here has to say which one it means.
            if (layer is FrameworkElement { Parent: System.Windows.Controls.Panel owner })
                owner.Children.Remove(layer);
            OverlayContentHost.Children.Add(layer);
        }
    }

    /// <summary>
    /// Moves the whole selection, clamped to the desktop rather than rubber-banded — a part hanging
    /// off the edge would be a region the crop cannot include.
    /// </summary>
    private void SelectionBody_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_processingStarted || _annotationHoldsSelection || !_hasSelection) return;

        double x = Math.Clamp(
            _selectionWpfRect.X + e.HorizontalChange, 0, Math.Max(0, ActualWidth - _selectionWpfRect.Width));
        double y = Math.Clamp(
            _selectionWpfRect.Y + e.VerticalChange, 0, Math.Max(0, ActualHeight - _selectionWpfRect.Height));

        _selectionWpfRect = new Rect(x, y, _selectionWpfRect.Width, _selectionWpfRect.Height);
        UpdateSelectionMetadata();
        UpdateSelectionVisuals();
        RaiseAdjusted();
    }

    private void TopLeftHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSelectionCorner(
            new WPoint(_selectionWpfRect.Right, _selectionWpfRect.Bottom),
            new WPoint(_selectionWpfRect.Left + e.HorizontalChange, _selectionWpfRect.Top + e.VerticalChange));

    private void TopRightHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSelectionCorner(
            new WPoint(_selectionWpfRect.Left, _selectionWpfRect.Bottom),
            new WPoint(_selectionWpfRect.Right + e.HorizontalChange, _selectionWpfRect.Top + e.VerticalChange));

    private void BottomLeftHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSelectionCorner(
            new WPoint(_selectionWpfRect.Right, _selectionWpfRect.Top),
            new WPoint(_selectionWpfRect.Left + e.HorizontalChange, _selectionWpfRect.Bottom + e.VerticalChange));

    private void BottomRightHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSelectionCorner(
            new WPoint(_selectionWpfRect.Left, _selectionWpfRect.Top),
            new WPoint(_selectionWpfRect.Right + e.HorizontalChange, _selectionWpfRect.Bottom + e.VerticalChange));

    private void ResizeSelectionCorner(WPoint fixedPoint, WPoint movingPoint)
    {
        if (_processingStarted || _annotationHoldsSelection || !_hasSelection) return;

        movingPoint.X = Math.Clamp(movingPoint.X, 0, ActualWidth);
        movingPoint.Y = Math.Clamp(movingPoint.Y, 0, ActualHeight);

        const double minSize = 4;
        if (Math.Abs(movingPoint.X - fixedPoint.X) < minSize)
            movingPoint.X = fixedPoint.X + (movingPoint.X >= fixedPoint.X ? minSize : -minSize);
        if (Math.Abs(movingPoint.Y - fixedPoint.Y) < minSize)
            movingPoint.Y = fixedPoint.Y + (movingPoint.Y >= fixedPoint.Y ? minSize : -minSize);

        movingPoint.X = Math.Clamp(movingPoint.X, 0, ActualWidth);
        movingPoint.Y = Math.Clamp(movingPoint.Y, 0, ActualHeight);

        if (SettingsService.Instance.Current.Capture.LockAspectRatio)
            movingPoint = KeepAspect(fixedPoint, movingPoint, _aspectRatio, ActualWidth, ActualHeight);

        _selectionWpfRect = Normalize(movingPoint, fixedPoint);
        UpdateSelectionMetadata();
        UpdateSelectionVisuals();
        RaiseAdjusted();
    }

    /// <summary>
    /// Where the dragged corner goes for the box to keep <paramref name="ratio"/>: the side that
    /// moved further leads and the other follows, both shrunk back if that would leave the desktop.
    /// </summary>
    internal static WPoint KeepAspect(WPoint fixedPoint, WPoint moving, double ratio, double maxX, double maxY)
    {
        if (!(ratio > 0) || double.IsInfinity(ratio)) return moving;
        double dx = moving.X - fixedPoint.X, dy = moving.Y - fixedPoint.Y;
        double sx = dx < 0 ? -1 : 1, sy = dy < 0 ? -1 : 1;
        double w = Math.Abs(dx), h = Math.Abs(dy);
        if (w / ratio >= h) h = w / ratio; else w = h * ratio;

        // Room in the direction the corner is going.
        double roomX = sx > 0 ? maxX - fixedPoint.X : fixedPoint.X;
        double roomY = sy > 0 ? maxY - fixedPoint.Y : fixedPoint.Y;
        if (w > roomX) { w = roomX; h = w / ratio; }
        if (h > roomY) { h = roomY; w = h * ratio; }
        return new WPoint(fixedPoint.X + sx * w, fixedPoint.Y + sy * h);
    }

    private static double AspectOf(Rect r) => r.Height > 0 ? r.Width / r.Height : 1;

    /// <summary>
    /// Tells the session the box moved, once per frame however many pointer moves that frame had:
    /// what follows the box — the toolbar, the overlay's bounds, the 標記 panel — moves windows, and
    /// a mouse reporting several hundred times a second was making it do so far more often than
    /// anything is drawn.
    /// </summary>
    private void RaiseAdjusted()
    {
        if (_adjustPending) return;
        _adjustPending = true;
        CompositionTarget.Rendering += AnnounceAdjusted;
    }

    private void AnnounceAdjusted(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= AnnounceAdjusted;
        if (!_adjustPending) return;
        _adjustPending = false;
        if (_hasSelection) SelectionAdjusted?.Invoke(this, Selection);
    }

    private static Rect Normalize(WPoint a, WPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private static BitmapSource BitmapToDisplaySource(Bitmap bmp, double dpi = 96) =>
        BitmapInterop.ToBitmapSource(bmp, dpi);
}
