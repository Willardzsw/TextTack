using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;
using Clipboard = System.Windows.Clipboard;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace OverTranslate.Views.QuickLookup;

/// <summary>
/// 取詞翻譯's 簡單顯示: the translation in a small label beside the selection, instead of the popup.
/// </summary>
/// <remarks>
/// <para>It never takes the keyboard (no-activate), so the user goes on typing or reading where they
/// were. Ctrl+C with the pointer on it copies the translation, a right-click or a double-click closes
/// it, Shift+right-click closes all of them, dragging moves it, and Esc anywhere closes all of them — see <see cref="SelectionLabelKeyHook"/>. Not a
/// single click: the first click of every double-click would copy too, replacing the clipboard on
/// the way to closing the label. Several can be up at once, one per press of the shortcut.</para>
///
/// <para>It stays where it was put. The text it belongs to may scroll or its window move; nothing
/// tells another process's text where it went, so the label cannot follow.</para>
/// </remarks>
public partial class SelectionLabelWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>The labels up now; the key hook lives while there are any.</summary>
    private static readonly List<SelectionLabelWindow> Open = [];

    /// <summary>The labels' window handles, for the key hook's thread to tell what the pointer is on.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, SelectionLabelWindow> Handles = new();

    private static SelectionLabelKeyHook? _keyHook;

    private IntPtr _hwnd;

    /// <summary>Space, in DIP, between the label and the selection's corner.</summary>
    private const double GapDip = 4;

    // The root Border's margin in the XAML, room for the card's shadow.
    private const double ShadowLeft = 8, ShadowTop = 6, ShadowRight = 8, ShadowBottom = 10;

    /// <summary>How far, in DIP, a press may move before it drags the label.</summary>
    private const double DragSlopDip = 4;

    private static readonly TimeSpan CopiedShown = TimeSpan.FromMilliseconds(1100);

    private readonly DrawingRectangle? _selection;
    private readonly DrawingPoint _pointer;
    private readonly CancellationTokenSource _cts = new();

    private string _translation = "";
    private bool _closing;

    /// <summary>Set once the user has dragged it: from then on it stays where they put it.</summary>
    private bool _moved;

    private bool _pressed;
    private Point _pressAt;

    private DispatcherTimer? _autoHide;
    private DispatcherTimer? _copiedTimer;

    // ══════════════════════════ Showing one ══════════════════════════

    /// <summary>
    /// Translates <paramref name="text"/> into a new label beside <paramref name="selection"/> (or
    /// the pointer). Uses 取詞翻譯's languages, 雙語互譯 and 自動複製, and the global service.
    /// </summary>
    /// <param name="selection">Where the selected text is, physical pixels; null when unknown.</param>
    public static async Task ShowAsync(string text, DrawingRectangle? selection)
    {
        var desktop = ScreenGeometry.VirtualDesktopBounds();
        if (selection is { } box && !SelectionLabelPlacement.IsPlausible(box, desktop)) selection = null;

        var label = new SelectionLabelWindow(selection, System.Windows.Forms.Cursor.Position);
        Open.Add(label);
        EnsureKeyHook();
        label.Show();

        await label.TranslateAsync(text);
    }

    /// <summary>Takes every label off the screen — Esc, or a realtime session taking the screen.</summary>
    public static void DismissAll()
    {
        foreach (var label in Open.ToList()) label.CloseLabel();
    }

    private static void EnsureKeyHook()
    {
        if (_keyHook is not null) return;
        _keyHook = SelectionLabelKeyHook.Install(
            () => (IReadOnlyCollection<IntPtr>)Handles.Keys,
            hwnd => { if (Handles.TryGetValue(hwnd, out var label)) label.Copy(); },
            DismissAll);
    }

    private static void ReleaseKeyHookIfIdle()
    {
        if (Open.Count > 0 || _keyHook is null) return;
        _keyHook.Dispose();
        _keyHook = null;
    }

    private SelectionLabelWindow(DrawingRectangle? selection, DrawingPoint pointer)
    {
        InitializeComponent();
        _selection = selection;
        _pointer = pointer;

        CopiedText.Text = LocalizationService.Get("S.QuickLookup.Copied");
        ShowWorking();

        // Parked off-screen and invisible until it can be measured and put in place.
        Left = -9999;
        Top = -9999;
        Opacity = 0;

        Loaded += (_, _) =>
        {
            Position();
            Dispatcher.BeginInvoke(new Action(() => { Position(); Opacity = 1; }), DispatcherPriority.Loaded);
        };

        // The text changes size from 翻譯中 to the answer; it stays against the same corner — unless
        // the user has moved it.
        SizeChanged += (_, _) => { if (IsLoaded && !_moved && !_closing) Position(); };

        Closed += (_, _) =>
        {
            _closing = true;
            _cts.Cancel();
            _autoHide?.Stop();
            _copiedTimer?.Stop();
            Open.Remove(this);
            Handles.TryRemove(_hwnd, out _);
            ReleaseKeyHookIfIdle();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowStyles.ApplyNoActivate(this);
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Handles[_hwnd] = this;
    }

    // ══════════════════════════ Translating ══════════════════════════

    private async Task TranslateAsync(string text)
    {
        var settings = SettingsService.Instance.Current;
        if (AppServices.Translation.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowError(LocalizationService.Get("S.Translation.MissingApiKey"));
            return;
        }

        try
        {
            var translation = await TranslateTextAsync(text, settings, _cts.Token);
            if (_closing) return;

            if (translation.Length == 0)
            {
                ShowError(LocalizationService.Get("S.QuickTranslate.EmptyResult"));
                return;
            }

            ShowTranslation(translation);
            if (settings.QuickLookup.AutoCopyTranslation) Copy(showBadge: false);
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (Exception ex)
        {
            if (_closing) return;
            Log.Warn(ex, "取詞翻譯 (簡單顯示) could not translate");
            ShowError(LocalizationService.Format(
                "S.Translation.ProviderUnavailable",
                LanguageData.GetProviderDisplay(settings.Provider), ex.Message));
        }
    }

    /// <summary>
    /// The popup's translation, rule for rule: its fixed pair, or 雙語互譯 when that is on — see
    /// QuickLookupWindow.TranslateNowAsync.
    /// </summary>
    private static async Task<string> TranslateTextAsync(string text, AppSettings settings, CancellationToken token)
    {
        var block = new OcrTextBlock(text, new Rect());
        var quickLookup = settings.QuickLookup;

        if (!quickLookup.BilingualEnabled)
        {
            var (results, _) = await AppServices.Translation.TranslateAsync(
                [block],
                LanguageData.GetValidSourceCode(settings.SourceLanguage),
                LanguageData.GetValidTargetCode(settings.TargetLanguage),
                settings.ApiKey,
                cancellationToken: token);
            return results.FirstOrDefault()?.TranslatedText ?? "";
        }

        var pair = BilingualPair.Load(quickLookup.BilingualFirstLanguage, quickLookup.BilingualSecondLanguage);
        var answer = await BilingualTranslation.TranslateAsync(text, pair,
            (from, target) => AppServices.Translation.TranslateAsync(
                [block], BilingualTranslation.SourceCode(from), target, settings.ApiKey,
                cancellationToken: token));
        return answer.Blocks.FirstOrDefault()?.TranslatedText ?? "";
    }

    // ══════════════════════════ What it says ══════════════════════════

    private void ShowWorking()
    {
        LabelText.Text = LocalizationService.Get("S.Translation.Translating");
        LabelText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AppTextSecondary");
    }

    private void ShowTranslation(string translation)
    {
        _translation = translation;
        LabelText.Text = translation;
        LabelText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AppTextPrimary");
        StartAutoHide();
    }

    private void ShowError(string message)
    {
        _translation = "";
        LabelText.Text = message;
        LabelText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AppError");
        StartAutoHide();
    }

    // ══════════════════════════ Placement ══════════════════════════

    private void Position()
    {
        var anchor = _selection is { } s ? new DrawingPoint(s.Right, s.Top) : _pointer;
        var area = System.Windows.Forms.Screen.FromPoint(anchor).WorkingArea;
        var scale = ScreenGeometry.ScaleAt(anchor.X, anchor.Y);

        // The window's own margin holds the shadow; the card inside it is what is placed against the
        // corner, so the card's size goes in and the margin comes off where the window goes.
        var card = new DrawingSize(
            (int)Math.Ceiling((ActualWidth - ShadowLeft - ShadowRight) * scale),
            (int)Math.Ceiling((ActualHeight - ShadowTop - ShadowBottom) * scale));
        var place = SelectionLabelPlacement.Place(_selection, _pointer, card, area, (int)Math.Round(GapDip * scale));
        ScreenGeometry.MoveToPhysical(this,
            place.X - (int)Math.Round(ShadowLeft * scale),
            place.Y - (int)Math.Round(ShadowTop * scale));
    }

    // ══════════════════════════ Double-click, drag ══════════════════════════

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled) return;
        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            _pressed = false;
            ReleaseMouseCapture();
            CloseLabel();
            return;
        }

        _pressed = true;
        _pressAt = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _pressAt.X) < DragSlopDip && Math.Abs(pos.Y - _pressAt.Y) < DragSlopDip) return;

        _pressed = false;
        ReleaseMouseCapture();
        _moved = true;
        try { DragMove(); }
        catch (InvalidOperationException) { /* the button was let go in the meantime */ }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();
    }

    /// <summary>
    /// Right-click: this label goes, as the capture's right-click does away with its box; with Shift,
    /// every label goes. On the release, so the press does not land on whatever is uncovered.
    /// </summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        // The asynchronous state: this window never has the keyboard, so its thread's own key state
        // does not see Shift go down.
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) DismissAll();
        else CloseLabel();
    }

    private const int VK_SHIFT = 0x10;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <remarks>
    /// The clipboard can be held open by anything else running; a failed copy is logged rather than
    /// let reach the dispatcher, which would take the application down over a label.
    /// </remarks>
    private void Copy(bool showBadge = true)
    {
        if (_translation.Length == 0) return;
        try
        {
            Clipboard.SetText(_translation);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "取詞翻譯 (簡單顯示) could not copy the translation");
            return;
        }

        if (!showBadge) return;
        CopiedBadge.Visibility = Visibility.Visible;
        _copiedTimer?.Stop();
        _copiedTimer = new DispatcherTimer { Interval = CopiedShown };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer!.Stop();
            CopiedBadge.Visibility = Visibility.Collapsed;
        };
        _copiedTimer.Start();
    }

    // ══════════════════════════ Going away ══════════════════════════

    /// <summary>自動消失, when it is on: counted from when the answer is shown; not while the pointer is on it.</summary>
    private void StartAutoHide()
    {
        var quickLookup = SettingsService.Instance.Current.QuickLookup;
        if (!quickLookup.SimpleAutoHide) return;

        int seconds = Math.Clamp(quickLookup.SimpleAutoHideSeconds, 1, 3600);
        _autoHide?.Stop();
        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _autoHide.Tick += (_, _) =>
        {
            if (IsMouseOver) return; // checked again next tick
            _autoHide!.Stop();
            CloseLabel();
        };
        _autoHide.Start();
    }

    private void CloseLabel()
    {
        if (_closing) return;
        _closing = true;
        _cts.Cancel();

        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
