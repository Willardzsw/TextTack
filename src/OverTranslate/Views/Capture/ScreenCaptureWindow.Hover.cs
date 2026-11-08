using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OverTranslate.Services;
using WPoint = System.Windows.Point;

namespace OverTranslate.Views.Capture;

/// <summary>
/// 自動選取視窗 / 自動選取介面元素 (Snipaste-style): before any box is drawn, the window or element
/// under the pointer is framed; the wheel steps out to what encloses it and back in, a click keeps
/// the box, and a press that moves still draws one by hand.
/// </summary>
public partial class ScreenCaptureWindow
{
    /// <summary>How far, in DIP, a press may move and still be a click on the framed element.</summary>
    private const double ClickSlopDip = 4;

    private CaptureElementPicker? _picker;

    /// <summary>Whether the frame on screen is the offered box rather than one drawn or kept.</summary>
    private bool _hovering;

    /// <summary>The offered boxes at the pointer, outermost first, in window DIP.</summary>
    private List<Rect> _hoverChain = [];

    /// <summary>How many steps the wheel has gone out from the innermost box.</summary>
    private int _hoverUp;

    /// <summary>The innermost box last offered; when it changes, the wheel's steps start over.</summary>
    private System.Drawing.Rectangle _hoverInnermost;

    /// <summary>Set when the press began on an offered box and has not yet moved far enough to be a drag.</summary>
    private bool _pressOnHover;

    private Rect _pressedHoverRect;

    /// <summary>Whether this capture offers boxes at all — a fresh one does, a pin being edited does not.</summary>
    public bool OffersHoverBoxes => _picker is not null;

    /// <summary>
    /// Offers the windows and elements <paramref name="picker"/> knows about. Takes ownership; a
    /// later picker (F5) replaces it.
    /// </summary>
    internal void UseHoverPicker(CaptureElementPicker? picker)
    {
        if (_picker is not null)
        {
            _picker.Updated -= Picker_Updated;
            _picker.Dispose();
        }
        _picker = picker;
        _hoverInnermost = default;
        if (_picker is null)
        {
            HideHover();
            return;
        }
        _picker.Updated += Picker_Updated;
        if (IsLoaded) RefreshHover();
    }

    private void Picker_Updated(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _picker)) RefreshHover();
    }

    private static bool PickWindows => SettingsService.Instance.Current.Capture.AutoSelectWindow;
    private static bool PickElements => SettingsService.Instance.Current.Capture.AutoSelectElement;

    /// <summary>Whether an offered box belongs on screen now: nothing drawn, being drawn, or locked.</summary>
    private bool HoverAllowed =>
        _picker is not null && !_hasSelection && !_isDragging && !_processingStarted &&
        (PickWindows || PickElements);

    /// <summary>Offers the box under the pointer as it is now, or takes the offer away.</summary>
    private void RefreshHover()
    {
        if (!HoverAllowed || !IsLoaded)
        {
            HideHover();
            return;
        }
        UpdateHover(Mouse.GetPosition(this));
    }

    private void UpdateHover(WPoint pos)
    {
        if (!HoverAllowed || _picker is null)
        {
            HideHover();
            return;
        }

        var phys = new System.Drawing.Point(
            (int)Math.Round(_physBounds.Left + pos.X * _dpiX),
            (int)Math.Round(_physBounds.Top + pos.Y * _dpiY));
        var chain = _picker.Resolve(phys, PickWindows, PickElements);
        if (chain.Count == 0)
        {
            HideHover();
            return;
        }

        if (chain[^1] != _hoverInnermost)
        {
            _hoverInnermost = chain[^1];
            _hoverUp = 0;
        }

        _hoverChain = chain.Select(r => new Rect(
            (r.Left - _physBounds.Left) / _dpiX, (r.Top - _physBounds.Top) / _dpiY,
            r.Width / _dpiX, r.Height / _dpiY)).ToList();
        _hoverUp = Math.Clamp(_hoverUp, 0, _hoverChain.Count - 1);
        ShowHover(_hoverChain[_hoverChain.Count - 1 - _hoverUp]);
    }

    /// <summary>How long the frame takes to grow or shrink onto the next box.</summary>
    private static readonly TimeSpan HoverGlide = TimeSpan.FromMilliseconds(140);

    /// <summary>The box being offered — where the frame is going, whatever it shows mid-glide.</summary>
    private Rect _hoverTarget;

    private Rect _glideFrom;
    private readonly System.Diagnostics.Stopwatch _glideClock = new();
    private bool _gliding;

    /// <summary>
    /// Offers <paramref name="r"/>. The first box appears where it is; after that the frame glides
    /// from the box it was on to the new one, growing or shrinking, the way Snipaste moves it —
    /// a frame that jumped between boxes read as flicker rather than as following the pointer.
    /// </summary>
    private void ShowHover(Rect r)
    {
        if (_hovering && _hoverTarget == r) return;
        bool wasHovering = _hovering;
        _hovering = true;
        _hoverTarget = r;

        if (!wasHovering)
        {
            StopGlide();
            PlaceHoverFrame(r);
            SetFrameVisibility(true);
            return;
        }

        // From wherever the frame is now, mid-glide included, so a quick sweep across several
        // boxes stays one continuous movement.
        _glideFrom = _selectionWpfRect;
        _glideClock.Restart();
        if (!_gliding)
        {
            _gliding = true;
            CompositionTarget.Rendering += GlideFrame;
        }
    }

    private void GlideFrame(object? sender, EventArgs e)
    {
        if (!_hovering)
        {
            StopGlide();
            return;
        }

        double t = Math.Min(1, _glideClock.Elapsed.TotalMilliseconds / HoverGlide.TotalMilliseconds);
        double k = 1 - Math.Pow(1 - t, 3); // ease-out: quick to leave, settling onto the box
        var r = new Rect(
            _glideFrom.X + (_hoverTarget.X - _glideFrom.X) * k,
            _glideFrom.Y + (_hoverTarget.Y - _glideFrom.Y) * k,
            _glideFrom.Width + (_hoverTarget.Width - _glideFrom.Width) * k,
            _glideFrom.Height + (_hoverTarget.Height - _glideFrom.Height) * k);
        PlaceHoverFrame(r);
        if (t >= 1) StopGlide();
    }

    private void StopGlide()
    {
        if (!_gliding) return;
        _gliding = false;
        _glideClock.Stop();
        CompositionTarget.Rendering -= GlideFrame;
    }

    private void PlaceHoverFrame(Rect r)
    {
        _selectionWpfRect = r;
        PlaceFrame(r);
        UpdateDimLayer();
        UpdateSizeBar();
    }

    private void HideHover()
    {
        StopGlide();
        if (!_hovering) return;
        _hovering = false;
        _hoverChain = [];
        if (_hasSelection || _isDragging) return;
        SetFrameVisibility(false);
        UpdateDimLayer();
    }

    /// <summary>Wheel up: what encloses the framed box; wheel down: back in.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_hovering || _hoverChain.Count == 0 || !HoverAllowed) return;
        e.Handled = true;
        int up = Math.Clamp(_hoverUp + (e.Delta > 0 ? 1 : -1), 0, _hoverChain.Count - 1);
        if (up == _hoverUp) return;
        _hoverUp = up;
        ShowHover(_hoverChain[_hoverChain.Count - 1 - _hoverUp]);
    }

    /// <summary>A press on an offered box: kept as a click until it moves, see <see cref="ClickSlopDip"/>.</summary>
    private bool BeginPressOnHover()
    {
        if (!_hovering || !HoverAllowed) return false;
        _pressOnHover = true;
        // Where the frame is going, not where the glide has got to.
        _pressedHoverRect = _hoverTarget;
        return true;
    }

    /// <summary>
    /// The pointer moved while pressed on an offered box. True while it is still a click; once it
    /// has gone further, the offer goes and the drag draws a box from where the press began.
    /// </summary>
    private bool ContinuePressOnHover(WPoint pos)
    {
        if (!_pressOnHover) return false;
        if (Math.Abs(pos.X - _startPoint.X) < ClickSlopDip && Math.Abs(pos.Y - _startPoint.Y) < ClickSlopDip)
            return true;
        _pressOnHover = false;
        _hovering = false;
        _hoverChain = [];
        StopGlide();
        HintHost.Visibility = Visibility.Collapsed;
        SetFrameVisibility(true);
        return false;
    }

    /// <summary>The press on an offered box ended without becoming a drag: that box is the selection.</summary>
    private bool EndPressOnHover()
    {
        if (!_pressOnHover) return false;
        _pressOnHover = false;
        _hovering = false;
        _hoverChain = [];
        StopGlide();
        _isDragging = false;
        ReleaseMouseCapture();
        SetSelectionFromCode(_pressedHoverRect);
        return true;
    }

    /// <summary>Drops a press on an offered box without keeping it (the right button pressed meanwhile).</summary>
    private void CancelPressOnHover()
    {
        _pressOnHover = false;
        _hovering = false;
        StopGlide();
    }
}
