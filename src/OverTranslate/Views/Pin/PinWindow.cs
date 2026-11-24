using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using Separator = System.Windows.Controls.Separator;

namespace OverTranslate.Views.Pin;

/// <summary>
/// 貼圖: a picture pinned to the screen, always on top, until the user closes it (Snipaste-style).
/// </summary>
/// <remarks>
/// <para>Drag to move. The wheel zooms around the picture's centre, Ctrl+wheel changes how
/// see-through it is. Double-click or Esc closes it; Ctrl+C copies it; the right-click menu has the rest.</para>
///
/// <para>Shown at the picture's own pixel size on whichever monitor it sits on, so a pinned capture
/// lands exactly over the place it was taken from and is as sharp as the screen was. The window is
/// a little larger than the picture, for the shadow to fade out in.</para>
/// </remarks>
public sealed class PinWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Every pin on screen, for 關閉全部貼圖.</summary>
    private static readonly List<PinWindow> Open = [];

    /// <summary>Room left round the picture for the shadow, in DIP.</summary>
    private const double ShadowMargin = 10;

    private const double MinZoom = 0.1;
    private const double MaxZoom = 8;
    private const double MinOpacity = 0.2;

    private readonly BitmapSource _image;
    private readonly PinChrome _chrome;
    private readonly System.Windows.Threading.DispatcherTimer _badgeTimer;

    // Where the picture's top-left should land, in physical pixels, until the window is shown.
    private readonly int _physX;
    private readonly int _physY;

    private double _zoom = 1;
    private double _dpi = 1;

    private PinWindow(BitmapSource image, int physX, int physY)
    {
        _image = image;
        _physX = physX;
        _physY = physY;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Title = LocalizationService.Get("S.Pin.Title");

        _chrome = new PinChrome(image);
        _chrome.Surface.Margin = new Thickness(ShadowMargin);
        Content = _chrome.Surface;

        _badgeTimer = new System.Windows.Threading.DispatcherTimer();
        _badgeTimer.Tick += (_, _) =>
        {
            _badgeTimer.Stop();
            _chrome.SetBadge(null);
            _canvas?.Chrome.SetBadge(null);
        };

        Cursor = Cursors.SizeAll;
        ContextMenu = BuildMenu();
    }

    /// <summary>
    /// Pins <paramref name="image"/> with its top-left at the physical point given — where a
    /// selection was, or under the pointer.
    /// </summary>
    public static PinWindow Pin(BitmapSource image, int physX, int physY)
    {
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        var pin = new PinWindow(image, physX, physY);
        Open.Add(pin);
        TopmostKeeper.Register(pin);
        pin.Closed += (_, _) =>
        {
            Open.Remove(pin);
            TopmostKeeper.Unregister(pin);
        };
        pin.Show();
        Log.Info("Pinned a {Width}x{Height} picture at {X},{Y}", image.PixelWidth, image.PixelHeight, physX, physY);
        return pin;
    }

    /// <summary>Raised by 編輯 / 翻譯: open this pin in the capture toolbar.</summary>
    public static event Action<PinWindow>? EditRequested;

    /// <summary>The picture as pinned, at its own size.</summary>
    public BitmapSource Image => _image;

    /// <summary>Where the picture is showing, at the size it is showing, in physical pixels.</summary>
    public System.Drawing.Rectangle PictureBounds => new(
        (int)Math.Round(_picX), (int)Math.Round(_picY),
        (int)Math.Round(PicturePixelWidth), (int)Math.Round(PicturePixelHeight));

    /// <summary>Ends a zoom at once: the pin back in charge, at its final size, nothing pending.</summary>
    public void FinishZoomNow()
    {
        _zoomIdle?.Stop();
        _canvasDragging = false;
        var handingBack = _handingBack;
        StopHandBack();
        handingBack?.Close();
        if (_canvas is { } canvas)
        {
            _canvas = null;
            canvas.Close();
            Place();
        }
        if (_selfCloaked) CloakSelf(false);
    }

    public static void CloseAll()
    {
        foreach (var pin in Open.ToList()) pin.Close();
    }

    // ── 滑鼠穿透 ────────────────────────────────────────────────────────────────────────────
    // A pin that lets every click through to whatever is under it. It cannot be clicked to turn it
    // off, so the 貼圖 shortcut with the pointer over it does that, and so does the tray menu.

    private bool _passThrough;

    /// <summary>Whether any pin on screen is letting clicks through.</summary>
    public static bool AnyPassThrough => Open.Any(pin => pin._passThrough);

    public static void DisableAllPassThrough()
    {
        foreach (var pin in Open.Where(pin => pin._passThrough).ToList()) pin.SetPassThrough(false);
    }

    /// <summary>Turns pass-through off for the pin under the pointer, if it has it on.</summary>
    /// <returns>Whether there was such a pin.</returns>
    public static bool TryDisablePassThroughUnderPointer()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        // Topmost-last: the most recently pinned is the one on top.
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var pin = Open[i];
            if (!pin._passThrough) continue;
            var r = ScreenGeometry.PhysicalBounds(pin);
            if (r.IsEmpty || !r.Contains(cursor)) continue;
            pin.SetPassThrough(false);
            return true;
        }
        return false;
    }

    private void SetPassThrough(bool on)
    {
        if (_passThrough == on) return;
        _passThrough = on;
        WindowStyles.SetClickThrough(this, on);
        if (on) ShowBadge(LocalizationService.Get("S.Pin.PassThroughOn"), TimeSpan.FromSeconds(4));
        else ShowBadge(LocalizationService.Get("S.Pin.PassThroughOff"));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Where the picture's top-left is, in physical pixels. Kept as doubles and the window placed
    /// from them, never read back from WPF's rounded Left/Top while zooming: that round trip is
    /// what made the picture creep a pixel this way and that on every notch.
    /// </summary>
    private double _picX, _picY;

    /// <summary>Set while this window moves itself, so the move is not read back as a drag.</summary>
    private bool _placing;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _picX = _physX;
        _picY = _physY;

        // Measured on the monitor it is about to land on, not the one WPF first created it on.
        _dpi = ScreenGeometry.ScaleAt(_physX, _physY);
        Place();

        var actual = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (actual > 0 && Math.Abs(actual - _dpi) > 1e-9)
        {
            _dpi = actual;
            Place();
        }

        LocationChanged += (_, _) => { if (!_placing) ReadPositionBack(); };
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)
            ?.AddHook(RefuseDpiResize);
    }

    private const int WM_DPICHANGED = 0x02E0;

    /// <summary>
    /// Refuses the resize Windows suggests when the pin is dragged onto a monitor at another scale.
    /// </summary>
    /// <remarks>
    /// The pin is sized in physical pixels, so the picture is pixel for pixel wherever it is.
    /// Letting WPF apply the suggested rectangle scaled the window by the ratio of the two monitors'
    /// scales while the picture inside stayed the size it was — part of it cut off, or a margin of
    /// nothing, until the next zoom set the size again. Refused, WPF keeps the scale it renders at,
    /// and since every size here is worked out from that same scale, the pixels stay right.
    /// </remarks>
    private IntPtr RefuseDpiResize(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_DPICHANGED:
                handled = true;
                break;

            // Asked before a DPI change what size the window wants on the new monitor: the same.
            case WM_GETDPISCALEDSIZE when _sizeW > 0:
                System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 0, _sizeW);
                System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 4, _sizeH);
                handled = true;
                return (IntPtr)1;

            // Whoever else tries to resize the pin — the system on a monitor change, WPF after a DPI
            // change — gets the size the pin set for itself. Only Place changes it.
            case WM_WINDOWPOSCHANGING when !_placing && _sizeW > 0:
                var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((pos.flags & SWP_NOSIZE) == 0 && (pos.cx != _sizeW || pos.cy != _sizeH))
                {
                    pos.cx = _sizeW;
                    pos.cy = _sizeH;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
                }
                break;
        }
        return IntPtr.Zero;
    }

    private const int WM_GETDPISCALEDSIZE = 0x02E4;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x0001;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    /// <summary>The window size, in physical pixels, that Place last gave the pin.</summary>
    private int _sizeW, _sizeH;

    /// <summary>
    /// After a drag: where the pin is, at the scale WPF is really rendering it at, laid out again
    /// — the same thing a zoom does, which is what used to put a pin moved across monitors right.
    /// </summary>
    private void SettleAfterMove()
    {
        var actual = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (actual > 0) _dpi = actual;
        ReadPositionBack();
        Place();
    }

    /// <summary>After a drag: where the picture is now.</summary>
    private void ReadPositionBack()
    {
        var r = ScreenGeometry.PhysicalBounds(this);
        if (r.IsEmpty) return;
        int margin = (int)Math.Round(ShadowMargin * _dpi);
        _picX = r.Left + margin;
        _picY = r.Top + margin;
    }

    private double PicturePixelWidth  => Math.Max(8, _image.PixelWidth  * _zoom);
    private double PicturePixelHeight => Math.Max(8, _image.PixelHeight * _zoom);

    /// <summary>
    /// Moves and sizes the window in one step so the picture is <see cref="_zoom"/> times its own
    /// pixels with its top-left at (<see cref="_picX"/>, <see cref="_picY"/>).
    /// </summary>
    /// <remarks>
    /// One SetWindowPos for position and size together. Setting WPF's Width, Height, Left and Top
    /// one after another is four moves, and between them the window is drawn grown from its old
    /// corner and then shifted back — the shake seen while zooming.
    /// </remarks>
    private void Place()
    {
        int margin = (int)Math.Round(ShadowMargin * _dpi);
        int w = (int)Math.Round(PicturePixelWidth), h = (int)Math.Round(PicturePixelHeight);
        int x = (int)Math.Round(_picX) - margin, y = (int)Math.Round(_picY) - margin;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        _placing = true;
        try
        {
            _sizeW = w + margin * 2;
            _sizeH = h + margin * 2;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, IntPtr.Zero, x, y, _sizeW, _sizeH, SWP_NOZORDER | SWP_NOACTIVATE);

            // The same numbers in DIP, so WPF agrees with the window it now has and does not move it again.
            _chrome.Surface.Margin = new Thickness(margin / _dpi);
            _chrome.View.Width  = w / _dpi;
            _chrome.View.Height = h / _dpi;
            Width  = (w + margin * 2) / _dpi;
            Height = (h + margin * 2) / _dpi;
            UpdateLayout();
        }
        finally
        {
            _placing = false;
        }

        _chrome.SetScaling(_zoom);
    }

    /// <summary>Zooms around the picture's centre, which stays where it is.</summary>
    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 1e-9) return;

        double oldW = PicturePixelWidth, oldH = PicturePixelHeight;
        double centreX = _picX + oldW / 2, centreY = _picY + oldH / 2;
        double oldZoom = _zoom;
        _zoom = zoom;
        _picX = centreX - PicturePixelWidth / 2;
        _picY = centreY - PicturePixelHeight / 2;

        ShowZoomed();
        ShowBadge($"{Math.Round(_zoom * 100)}%");
    }

    // ── Smooth zoom ───────────────────────────────────────────────────────────────────────────
    // While the wheel turns the picture is shown on a PinZoomCanvas, which never moves or resizes,
    // so no frame shows the old picture in the new place. A moment after the last notch the pin is
    // resized behind a cloak, shown again once it has been painted, and the canvas goes.

    private PinZoomCanvas? _canvas;
    private bool _selfCloaked;
    private System.Windows.Threading.DispatcherTimer? _zoomIdle;

    private void ShowZoomed()
    {
        if (TryZoomOnCanvas()) return;
        Place();
    }

    private bool TryZoomOnCanvas()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!IsLoaded || hwnd == IntPtr.Zero) return false;

        if (_canvas is null)
        {
            // The whole desktop, not the pin's monitor: a drag that starts on the canvas (right after
            // a zoom) can carry the picture onto another monitor, and a one-monitor canvas showed
            // nothing there until the button came up. One scale across all of it, like the capture
            // window, so the picture stays pixel for pixel on every monitor.
            var desktop = ScreenGeometry.VirtualDesktopBounds();

            var canvas = new PinZoomCanvas(_image, desktop);
            _canvas = canvas;
            canvas.Ready += (_, _) =>
            {
                // Only if this canvas is still the one in use: a zoom that already ended must not
                // leave the pin hidden behind a canvas that is gone.
                if (!ReferenceEquals(_canvas, canvas)) return;
                if (!CloakSelf(true))
                {
                    // No cloaking on this system: fall back to resizing the pin directly.
                    _canvas = null;
                    canvas.Close();
                    Place();
                }
            };
            canvas.Wheel += OnCanvasWheel;
            canvas.Pressed += OnCanvasPressed;
            canvas.DragMoved += OnCanvasDragMoved;
            canvas.DragEnded += OnCanvasDragEnded;
            UpdateCanvas();
            canvas.Show();
        }
        else
        {
            UpdateCanvas();
        }

        RestartZoomIdle(350);
        return true;
    }

    private void RestartZoomIdle(int milliseconds)
    {
        _zoomIdle ??= CreateZoomIdle();
        _zoomIdle.Stop();
        if (_canvasDragging) return;
        _zoomIdle.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _zoomIdle.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateZoomIdle()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => EndZoomCanvas();
        return timer;
    }

    private bool CloakSelf(bool cloaked)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        bool ok = WindowScreenPresence.SetCloaked(hwnd, cloaked);
        if (ok) _selfCloaked = cloaked;
        return ok;
    }

    /// <summary>
    /// Hands the picture back from the canvas to the pin, which is shown again only once it has
    /// painted the new size, so the swap is not seen.
    /// </summary>
    private void EndZoomCanvas()
    {
        _zoomIdle?.Stop();
        if (_canvas is not { } canvas) return;
        _canvas = null;

        Place();

        // The canvas never got as far as hiding the pin: nothing to swap back.
        if (!_selfCloaked)
        {
            canvas.Close();
            return;
        }

        // A few frames for the layered window to present the new size before it is uncloaked.
        // Until then the canvas is still what is on screen, and a press on it takes it back
        // (TakeBack) rather than racing the swap — the swap racing a quick drag was the flash.
        int frames = 0;
        void OnFrame(object? sender, EventArgs e)
        {
            if (++frames < 3) return;
            StopHandBack();
            if (_canvas is null && _selfCloaked) CloakSelf(false);
            canvas.Close();
        }
        _handingBack = canvas;
        _handBackFrame = OnFrame;
        CompositionTarget.Rendering += OnFrame;
    }

    private PinZoomCanvas? _handingBack;
    private EventHandler? _handBackFrame;

    private void StopHandBack()
    {
        if (_handBackFrame is { } frame) CompositionTarget.Rendering -= frame;
        _handBackFrame = null;
        _handingBack = null;
    }

    /// <summary>
    /// Whether <paramref name="canvas"/> is the one in use, taking it back first if it was about to
    /// hand the picture to the pin. The pin is still cloaked then, so nothing on screen changes.
    /// </summary>
    private bool TakeBack(PinZoomCanvas canvas)
    {
        if (ReferenceEquals(canvas, _handingBack))
        {
            StopHandBack();
            _canvas = canvas;
        }
        return ReferenceEquals(canvas, _canvas);
    }

    // A drag that started on the canvas moves the picture on the canvas, so it never has to hand
    // over to the pin in the middle of a gesture.
    private bool _canvasDragging;
    private System.Drawing.Point _dragStartCursor;
    private double _dragStartX, _dragStartY;

    private void OnCanvasDragMoved(PinZoomCanvas canvas)
    {
        if (!_canvasDragging || !ReferenceEquals(canvas, _canvas)) return;
        var cursor = System.Windows.Forms.Cursor.Position;
        _picX = _dragStartX + (cursor.X - _dragStartCursor.X);
        _picY = _dragStartY + (cursor.Y - _dragStartCursor.Y);
        UpdateCanvas();
    }

    private void OnCanvasDragEnded(PinZoomCanvas canvas)
    {
        if (!_canvasDragging) return;
        _canvasDragging = false;
        if (ReferenceEquals(canvas, _canvas)) RestartZoomIdle(200);
    }

    private void UpdateCanvas() =>
        _canvas?.Present(_picX, _picY, PicturePixelWidth, PicturePixelHeight, Opacity, _zoom);

    private void OnCanvasWheel(PinZoomCanvas canvas, MouseWheelEventArgs e)
    {
        if (!TakeBack(canvas)) return;
        int notches = Math.Sign(e.Delta);
        if (notches != 0) Step(PinGestures.ForWheel(Gestures, Keyboard.Modifiers), notches);
    }

    private void OnCanvasPressed(PinZoomCanvas canvas, MouseButtonEventArgs e)
    {
        if (!TakeBack(canvas)) return;
        e.Handled = true;
        _zoomIdle?.Stop();

        switch (e.ChangedButton)
        {
            case MouseButton.Left when e.ClickCount >= 2:
                if (!Perform(PinGestures.ForClick(Gestures, Keyboard.Modifiers, PinClickGesture.LeftDoubleClick)))
                    RestartZoomIdle(350);
                break;

            case MouseButton.Left:
                _canvasDragging = true;
                _dragStartCursor = System.Windows.Forms.Cursor.Position;
                _dragStartX = _picX;
                _dragStartY = _picY;
                canvas.BeginDrag();
                break;

            case MouseButton.Middle:
                var gesture = e.ClickCount >= 2 ? PinClickGesture.MiddleDoubleClick : PinClickGesture.MiddleClick;
                Perform(PinGestures.ForClick(Gestures, Keyboard.Modifiers, gesture));
                if (_canvas is not null) RestartZoomIdle(350);
                break;

            case MouseButton.Right when ContextMenu is { } menu:
                menu.PlacementTarget = canvas;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
                RestartZoomIdle(350);
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _zoomIdle?.Stop();
        var handingBack = _handingBack;
        StopHandBack();
        handingBack?.Close();
        _canvas?.Close();
        _canvas = null;
        base.OnClosed(e);
    }

    private void SetPinOpacity(double opacity)
    {
        Opacity = Math.Clamp(opacity, MinOpacity, 1);
        UpdateCanvas();
        ShowBadge($"{LocalizationService.Get("S.Pin.Opacity")} {Math.Round(Opacity * 100)}%");
    }

    /// <summary>重置貼圖: its own size and fully opaque again.</summary>
    private void ResetPin()
    {
        Opacity = 1;
        if (Math.Abs(_zoom - 1) > 1e-9) SetZoom(1);
        else ShowBadge("100%");
    }

    /// <summary>
    /// Shows a short note on the pin (and its canvas): gone 0.3 s after the last change unless a
    /// longer time is asked for, so a zoom or fade says its percentage and then gets out of the way.
    /// </summary>
    private void ShowBadge(string text, TimeSpan? duration = null)
    {
        _chrome.SetBadge(text);
        _canvas?.Chrome.SetBadge(text);
        _badgeTimer.Stop();
        _badgeTimer.Interval = duration ?? TimeSpan.FromMilliseconds(300);
        _badgeTimer.Start();
    }

    private static PinSettings Gestures => SettingsService.Instance.Current.Pin ?? new PinSettings();

    /// <summary>Carries out a one-shot action. True when one was done.</summary>
    private bool Perform(PinAction? action)
    {
        switch (action)
        {
            case PinAction.Close: Close(); return true;
            case PinAction.Reset: ResetPin(); return true;
            default: return false;
        }
    }

    /// <summary>A zoom or opacity step, up or down.</summary>
    private void Step(PinAction? action, int direction)
    {
        if (action == PinAction.Zoom)
            SetZoom(_zoom * (direction > 0 ? 1.1 : 1 / 1.1));
        else if (action == PinAction.Opacity)
            SetPinOpacity(Opacity + direction * 0.1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Perform(PinGestures.ForClick(Gestures, Keyboard.Modifiers, PinClickGesture.LeftDoubleClick));
            e.Handled = true;
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* the button was already up */ }
        SettleAfterMove();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle) return;

        var gesture = e.ClickCount >= 2 ? PinClickGesture.MiddleDoubleClick : PinClickGesture.MiddleClick;
        if (Perform(PinGestures.ForClick(Gestures, Keyboard.Modifiers, gesture))) e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int notches = Math.Sign(e.Delta);
        if (notches == 0) return;

        Step(PinGestures.ForWheel(Gestures, Keyboard.Modifiers), notches);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.C when ctrl:
                Copy();
                break;
            case Key.S when ctrl:
                SaveAs();
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                ResetPin();
                break;
            case Key.OemPlus or Key.Add:
                Step(PinGestures.ForPlusMinus(Gestures, Keyboard.Modifiers), +1);
                break;
            case Key.OemMinus or Key.Subtract:
                Step(PinGestures.ForPlusMinus(Gestures, Keyboard.Modifiers), -1);
                break;
            case Key.Left:  Nudge(-1, 0); break;
            case Key.Right: Nudge(+1, 0); break;
            case Key.Up:    Nudge(0, -1); break;
            case Key.Down:  Nudge(0, +1); break;
            default: return;
        }

        e.Handled = true;
    }

    /// <summary>Moves the picture a pixel, on the canvas if that is what is showing it.</summary>
    private void Nudge(int dx, int dy)
    {
        _picX += dx;
        _picY += dy;
        if (_canvas is not null) UpdateCanvas();
        else Place();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(Item("S.Pin.Edit", null, () => EditRequested?.Invoke(this)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("S.Pin.Copy", "Ctrl+C", Copy));
        menu.Items.Add(Item("S.Pin.SaveAs", "Ctrl+S", SaveAs));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("S.Pin.ActualSize", "Ctrl+0", ResetPin));

        var opacity = new MenuItem { Header = LocalizationService.Get("S.Pin.Opacity") };
        foreach (int percent in new[] { 100, 80, 60, 40 })
        {
            int p = percent;
            var choice = new MenuItem { Header = $"{p}%" };
            choice.Click += (_, _) => SetPinOpacity(p / 100.0);
            opacity.Items.Add(choice);
        }
        menu.Items.Add(opacity);

        var passThrough = new MenuItem
        {
            Header = LocalizationService.Get("S.Pin.PassThrough"),
            IsCheckable = true,
        };
        passThrough.Click += (_, _) => SetPassThrough(passThrough.IsChecked);
        menu.Opened += (_, _) => passThrough.IsChecked = _passThrough;
        menu.Items.Add(passThrough);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("S.Pin.Close", "Esc", Close));
        menu.Items.Add(Item("S.Pin.CloseAll", null, CloseAll));
        return menu;

        static MenuItem Item(string key, string? gesture, Action action)
        {
            var item = new MenuItem { Header = LocalizationService.Get(key), InputGestureText = gesture ?? "" };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void Copy()
    {
        try
        {
            System.Windows.Clipboard.SetImage(_image);
            ShowBadge(LocalizationService.Get("S.Main.CopiedTitle"));
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another application for a moment; nothing to undo.
            Log.Warn(ex, "Copying a pinned picture failed");
        }
    }

    private void SaveAs()
    {
        // Not topmost while the dialog is up, or the dialog opens underneath the pin it belongs to.
        Topmost = false;
        try
        {
            ScreenshotSaveService.SaveAs(this, _image, "S.Pin.Title");
        }
        finally
        {
            Topmost = true;
            // Back above whatever came forward while the dialog was up.
            AlwaysOnTop.Reassert(this);
        }
    }

    /// <summary>
    /// Text from the clipboard drawn as a card, so it can be pinned like a picture (Snipaste does
    /// the same). Rendered at <paramref name="scale"/> so it is sharp on the monitor it lands on.
    /// </summary>
    public static BitmapSource RenderText(string text, double scale)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
        };
        if (System.Windows.Application.Current?.TryFindResource("AppFont") is System.Windows.Media.FontFamily font)
            block.FontFamily = font;

        var card = new System.Windows.Controls.Border
        {
            Background = Brushes.White,
            Padding = new Thickness(12, 10, 12, 10),
            Child = block,
        };
        card.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();

        int w = Math.Max(1, (int)Math.Ceiling(card.ActualWidth * scale));
        int h = Math.Max(1, (int)Math.Ceiling(card.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        return bitmap;
    }
}
