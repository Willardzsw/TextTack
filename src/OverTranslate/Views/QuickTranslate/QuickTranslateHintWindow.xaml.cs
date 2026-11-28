using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OverTranslate.Views.QuickTranslate;

/// <summary>
/// The one thing 替換翻譯 puts on the screen: a small label by the pointer saying what is happening
/// to the text the user just selected.
/// </summary>
/// <remarks>
/// The same small label as 取詞翻譯's 簡單顯示 — text only, no icons, no buttons, no progress ring —
/// so every hint the application puts over another program looks alike. It says 翻譯中… while the
/// translation is in flight, goes away without a word once the replacement is pasted (the document
/// underneath already shows it worked), and stays only to say why something failed or which
/// language a double press switched to.
///
/// A failure waits while the pointer rests on it; a right-click or a double-click closes it.
///
/// One at a time: a second shortcut press replaces the label outright — see <see cref="Summon"/>.
/// It never takes activation, because the paste goes to whatever window has the foreground.
/// </remarks>
public partial class QuickTranslateHintWindow : Window
{
    private static QuickTranslateHintWindow? _current;

    /// <summary>How long a failure stays up: it has to be read, not glanced at.</summary>
    private static readonly TimeSpan FailureHold = TimeSpan.FromMilliseconds(2000);

    /// <summary>How long the language switch stays up: a list to glance at, not to study.</summary>
    private static readonly TimeSpan SwitchHold = TimeSpan.FromMilliseconds(1000);

    private const int FadeMs = 200;

    /// <summary>Space, in DIP, between the label and the pointer.</summary>
    private const double Gap = 6;

    /// <summary>Where the pointer was when the label was put up; it stays there.</summary>
    private readonly System.Drawing.Point _pointer;

    /// <summary>The window the paste goes to; see <see cref="Summon"/>.</summary>
    private readonly IntPtr _returnTo;

    private DispatcherTimer? _hold;
    private bool _holdsForPointer;
    private bool _closed;
    private bool _fadingOut;

    /// <summary>Puts a label up saying 翻譯中…, replacing whichever one is on screen.</summary>
    /// <param name="returnTo">
    /// The window holding the selection, which the translation will be pasted into. Should Windows
    /// activate the label anyway, the foreground goes straight back there.
    /// </param>
    public static QuickTranslateHintWindow Summon(IntPtr returnTo)
    {
        Dismiss();

        var hint = new QuickTranslateHintWindow(returnTo);
        _current = hint;
        ((Window)hint).Show();
        return hint;
    }

    /// <summary>Takes the label off the screen, if one is up.</summary>
    public static void Dismiss()
    {
        var existing = _current;
        _current = null;
        existing?.Close();
    }

    // Private on purpose: Window.Show() on one made directly would bypass the one-at-a-time rule.
    private QuickTranslateHintWindow(IntPtr returnTo)
    {
        _returnTo = returnTo;
        InitializeComponent();
        _pointer = System.Windows.Forms.Cursor.Position;

        MessageText.Text = LocalizationService.Get("S.Translation.Translating");

        Closed += (_, _) =>
        {
            _closed = true;
            _hold?.Stop();
            if (ReferenceEquals(_current, this)) _current = null;
        };

        MouseEnter += (_, _) => { if (_holdsForPointer) PauseHold(); };
        MouseLeave += (_, _) => { if (_holdsForPointer) _hold?.Start(); };
        MouseRightButtonUp += (_, e) => { e.Handled = true; CloseNow(); };
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) CloseNow(); };

        // Parked off-screen until Loaded can measure it, and invisible until the position is settled.
        Left = -9999;
        Top = -9999;
        Opacity = 0;

        Loaded += (_, _) =>
        {
            Position();
            Dispatcher.BeginInvoke(
                new Action(() => { Position(); Opacity = 1; }), DispatcherPriority.Loaded);
        };

        SizeChanged += (_, _) => { if (IsLoaded && !_fadingOut) Position(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowStyles.ApplyHintWindow(this, () => _returnTo);
    }

    /// <summary>
    /// Last resort before pasting: if the label itself still holds the foreground — Windows activated
    /// it and the hand-back in <see cref="WindowStyles.ApplyHintWindow"/> did not take — gives it back
    /// to <paramref name="target"/> and waits briefly for that to happen.
    /// </summary>
    /// <returns>
    /// Null when the label did not hold the foreground (nothing to do); otherwise whether the target
    /// has it again. Any other window in front is left alone: that is the user's own move.
    /// </returns>
    public async Task<bool?> ReclaimForegroundAsync(IntPtr target)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (_closed || hwnd == IntPtr.Zero || GetForegroundWindow() != hwnd) return null;
        if (target == IntPtr.Zero || !IsWindow(target)) return false;

        SetForegroundWindow(target);
        for (var i = 0; i < 10 && GetForegroundWindow() != target; i++)
            await Task.Delay(15);
        return GetForegroundWindow() == target;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    // ══════════════════════════ What it says ══════════════════════════

    /// <summary>The replacement is in the document: nothing left to say, so the label goes.</summary>
    public void ReportSuccess(bool appended = false)
    {
        if (_closed) return;
        CloseNow();
    }

    /// <summary>Says why the selection was left as it was.</summary>
    public void ReportFailure(string message)
    {
        if (_closed) return;

        MessageText.Text = message;
        _holdsForPointer = true;
        StartHold(FailureHold);
    }

    /// <summary>
    /// Says that a double press moved the target language on: every picked language in order, the
    /// one now in use stressed, so the reader also sees which comes next.
    /// </summary>
    public void ReportLanguageSwitch(IReadOnlyList<string> languages, string current)
    {
        if (_closed) return;

        MessageText.Inlines.Clear();
        MessageText.Inlines.Add(new Run(LocalizationService.Get("S.QuickTranslate.TargetIs")));
        for (var i = 0; i < languages.Count; i++)
        {
            if (i > 0)
            {
                var separator = new Run("  ·  ");
                separator.SetResourceReference(TextElement.ForegroundProperty, "AppTextSecondary");
                MessageText.Inlines.Add(separator);
            }

            var name = new Run(LanguageData.GetTargetDisplayName(languages[i]));
            if (languages[i].Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                name.FontWeight = FontWeights.SemiBold;
                name.SetResourceReference(TextElement.ForegroundProperty, "AppAccent");
            }
            else
            {
                name.SetResourceReference(TextElement.ForegroundProperty, "AppTextSecondary");
            }
            MessageText.Inlines.Add(name);
        }

        _holdsForPointer = false;
        StartHold(SwitchHold);
    }

    // ══════════════════════════ Placement ══════════════════════════

    /// <remarks>All physical pixels, scaled by the monitor the pointer is on.</remarks>
    private void Position()
    {
        var area = System.Windows.Forms.Screen.FromPoint(_pointer).WorkingArea;
        var scale = ScreenGeometry.ScaleAt(_pointer.X, _pointer.Y);

        var (left, top) = HintPlacement.Place(
            new Point(_pointer.X, _pointer.Y),
            new Size(ActualWidth * scale, ActualHeight * scale),
            new Rect(area.Left, area.Top, area.Width, area.Height),
            Gap * scale);

        ScreenGeometry.MoveToPhysical(this, left, top);
    }

    // ══════════════════════════ Going away ══════════════════════════

    private void StartHold(TimeSpan duration)
    {
        _hold?.Stop();
        _hold = new DispatcherTimer { Interval = duration };
        _hold.Tick += (_, _) =>
        {
            _hold!.Stop();
            FadeOutAndClose();
        };
        _hold.Start();

        if (_holdsForPointer && IsMouseOver) PauseHold();
    }

    private void PauseHold()
    {
        if (_closed) return;

        _hold?.Stop();
        _fadingOut = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    private void CloseNow()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        _hold?.Stop();
        Close();
    }

    private void FadeOutAndClose()
    {
        if (_closed) return;

        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        _fadingOut = true;
        var fade = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(FadeMs));
        fade.Completed += (_, _) => { if (_fadingOut) Close(); };
        BeginAnimation(OpacityProperty, fade);
    }
}
