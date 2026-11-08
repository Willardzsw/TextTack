using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using OverTranslate.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings
using Brush = System.Windows.Media.Brush;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Separator = System.Windows.Controls.Separator;
using TextBox = System.Windows.Controls.TextBox;

namespace OverTranslate.Views.StickyNote;

/// <summary>
/// One 便利貼: a sheet of coloured paper with a strip of tape across the top and a small curled
/// corner, showing its text. Double-click (or 編輯) to edit, ✓ to save, Esc to leave it as it was.
/// </summary>
public sealed class StickyNoteWindow : Window
{
    /// <summary>Room round the paper for its shadow and for the tape that sits over its top edge.</summary>
    private static readonly Thickness Outer = new(10, 14, 12, 12);

    /// <summary>The tallest a note grows on its own before its text scrolls.</summary>
    private const double AutoMaxTextHeight = 460;

    public StickyNoteData Data { get; }

    private readonly NotePaper _paper = new();
    private readonly Rectangle _tape = new() { Width = 64, Height = 20, RadiusX = 1.5, RadiusY = 1.5 };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private readonly TextBlock _view = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _editor = new()
    {
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true,
        AcceptsTab = true,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        Visibility = Visibility.Collapsed,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _editTools = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
    private readonly Button _pinButton;
    private readonly Border _badge = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _badgeText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.White };
    private readonly DispatcherTimer _badgeTimer = new();
    private readonly Grid _resizeFrame = new() { Visibility = Visibility.Collapsed };
    private bool _editing;
    private bool _resizing;
    private bool _placing;
    private readonly bool _startEditing;

    public StickyNoteWindow(StickyNoteData data, bool startEditing)
    {
        Data = data;
        _startEditing = startEditing;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = startEditing;
        Topmost = data.Topmost;
        Opacity = Math.Clamp(data.Opacity, 0.4, 1);
        UseLayoutRounding = true;
        Title = LocalizationService.Get("S.Note.Title");
        SetResourceReference(FontFamilyProperty, "AppFont");

        _view.Foreground = _editor.Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        _view.Text = data.Text;
        _scroll.Content = _view;

        // Paper, then the text over it, then the tools, then the tape over the top edge.
        var sheet = new Grid { Margin = Outer };
        sheet.Children.Add(_paper);

        var body = new Grid { Margin = new Thickness(14, 18, 14, 16) };
        body.Children.Add(_scroll);
        body.Children.Add(_editor);
        sheet.Children.Add(body);

        var toolbar = new Grid { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 2, 0) };
        _pinButton = Tool("", "S.Note.Topmost", () => SetTopmost(!Data.Topmost));
        _tools.Children.Add(Tool("", "S.Note.Edit", BeginEdit));
        _tools.Children.Add(_pinButton);
        _tools.Children.Add(Tool("", "S.Note.Delete", Delete));
        _tools.Opacity = 0;
        _editTools.Children.Add(Tool("", "S.Note.Save", CommitEdit));
        _editTools.Children.Add(Tool("", "S.Note.Cancel", CancelEdit));
        toolbar.Children.Add(_tools);
        toolbar.Children.Add(_editTools);
        sheet.Children.Add(toolbar);

        _badge.Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x30, 0x30, 0x30));
        _badge.Child = _badgeText;
        _badge.VerticalAlignment = VerticalAlignment.Bottom;
        _badge.Margin = new Thickness(8, 0, 8, 10);
        sheet.Children.Add(_badge);
        _badgeTimer.Tick += (_, _) =>
        {
            _badgeTimer.Stop();
            _badge.Visibility = Visibility.Collapsed;
        };

        _tape.VerticalAlignment = VerticalAlignment.Top;
        _tape.HorizontalAlignment = HorizontalAlignment.Center;
        _tape.Margin = new Thickness(0, -11, 0, 0);
        _tape.RenderTransformOrigin = new Point(0.5, 0.5);
        _tape.RenderTransform = new RotateTransform(-2.5);
        _tape.IsHitTestVisible = false;
        sheet.Children.Add(_tape);

        BuildResizeFrame();
        sheet.Children.Add(_resizeFrame);

        Content = sheet;
        ContextMenu = BuildMenu();

        MouseEnter += (_, _) => FadeTools(1);
        MouseLeave += (_, _) => FadeTools(0);
        MouseLeftButtonDown += OnMouseDown;
        _editor.PreviewKeyDown += OnEditorKey;
        PreviewKeyDown += (_, e) =>
        {
            if (!_editing && e.Key == Key.Escape && _resizing) SetResizing(false);
        };

        ApplyAppearance();
        ApplySizeMode();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _placing = true;
        ScreenGeometry.MoveToPhysical(this, Data.X, Data.Y);
        _placing = false;
        if (Data.PassThrough) WindowStyles.SetClickThrough(this, true);
        LocationChanged += (_, _) =>
        {
            if (_placing) return;
            var r = ScreenGeometry.PhysicalBounds(this);
            if (r.IsEmpty) return;
            Data.X = r.X;
            Data.Y = r.Y;
            StickyNoteManager.ScheduleSave();
        };
        if (_startEditing) Dispatcher.BeginInvoke(BeginEdit, DispatcherPriority.Loaded);
    }

    // ── Look ─────────────────────────────────────────────────────────────────

    /// <summary>Paper and tape colours and the font size, from 便利貼設定.</summary>
    public void ApplyAppearance()
    {
        var s = StickyNoteManager.Settings;
        // The note's own colours when it has them, the 便利貼設定 ones otherwise.
        var paper = StickyNoteColors.Parse(Data.PaperColor ?? s.PaperColor, StickyNoteColors.DefaultPaper);
        var tape = StickyNoteColors.Parse(Data.TapeColor ?? s.TapeColor, StickyNoteColors.DefaultTape);
        _paper.Paper = paper;
        _tape.Fill = new SolidColorBrush(Color.FromArgb(0xC0, tape.R, tape.G, tape.B));
        _tape.Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        _view.FontSize = _editor.FontSize = Math.Clamp(s.FontSize, 9, 40);
        ApplySizeMode();
    }

    /// <summary>
    /// Automatic: the 便利貼設定 width, and a height that follows the text up to a limit. Once the
    /// user has resized the note, the size they chose, with the text scrolling inside it.
    /// </summary>
    private void ApplySizeMode()
    {
        if (Data.Width is { } w && Data.Height is { } h)
        {
            SizeToContent = SizeToContent.Manual;
            Width = Math.Max(w, 120);
            Height = Math.Max(h, 90);
            _scroll.MaxHeight = double.PositiveInfinity;
            _editor.MaxHeight = double.PositiveInfinity;
        }
        else
        {
            Width = Math.Clamp(StickyNoteManager.Settings.Width, 120, 800) + Outer.Left + Outer.Right;
            SizeToContent = SizeToContent.Height;
            _scroll.MaxHeight = AutoMaxTextHeight;
            _editor.MaxHeight = AutoMaxTextHeight;
            ClearValue(HeightProperty);
        }
        _view.MinHeight = _editor.MinHeight = 40;
    }

    private void FadeTools(double to)
    {
        if (_editing) return;
        _tools.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)));
    }

    private static Button Tool(string glyph, string tipKey, Action click)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
            Width = 24,
            Height = 24,
            Margin = new Thickness(1, 0, 0, 0),
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0x30, 0x30, 0x30)),
            Template = ToolTemplate(),
        };
        button.SetResourceReference(ToolTipProperty, tipKey);
        button.Click += (_, _) => click();
        return button;
    }

    private static ControlTemplate? _toolTemplate;

    /// <summary>A round glyph button that darkens the paper under it on hover.</summary>
    private static ControlTemplate ToolTemplate()
    {
        if (_toolTemplate is not null) return _toolTemplate;
        var border = new FrameworkElementFactory(typeof(Border), "Root");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)), "Root"));
        template.Triggers.Add(hover);
        _toolTemplate = template;
        return template;
    }

    /// <summary>A short message over the bottom of the note, or nothing.</summary>
    private void ShowBadge(string? text, TimeSpan? duration = null)
    {
        _badgeTimer.Stop();
        if (string.IsNullOrEmpty(text))
        {
            _badge.Visibility = Visibility.Collapsed;
            return;
        }
        _badgeText.Text = text;
        _badge.Visibility = Visibility.Visible;
        if (duration is { } d)
        {
            _badgeTimer.Interval = d;
            _badgeTimer.Start();
        }
    }

    /// <summary>"識別中…" while a copied picture is being read; null to clear it.</summary>
    public void ShowBusy(string? text) => ShowBadge(text);

    // ── Moving, editing ──────────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !_editing)
        {
            BeginEdit();
            e.Handled = true;
            return;
        }

        if (_editing && IsInside(e.OriginalSource as DependencyObject, _editor)) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); } catch (InvalidOperationException) { /* button released already */ }
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, container)) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or Thumb) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    public void SetText(string text)
    {
        Data.Text = text;
        Data.Saved = DateTime.Now;
        _view.Text = text;
        if (_editing) _editor.Text = text;
        StickyNoteManager.RaiseChanged();
    }

    public void BeginEdit()
    {
        if (_editing) return;
        if (Data.PassThrough) SetPassThrough(false);
        _editing = true;
        _editor.Text = Data.Text;
        _editor.Visibility = Visibility.Visible;
        _scroll.Visibility = Visibility.Collapsed;
        _tools.Visibility = Visibility.Collapsed;
        _editTools.Visibility = Visibility.Visible;
        Activate();
        _editor.Focus();
        _editor.CaretIndex = _editor.Text.Length;
    }

    private void EndEdit()
    {
        _editing = false;
        _editor.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;
        _editTools.Visibility = Visibility.Collapsed;
        _tools.Visibility = Visibility.Visible;
        _tools.Opacity = IsMouseOver ? 1 : 0;
    }

    /// <summary>✓: keeps the edited text and adds a line to the archive when it changed. Emptied, the note goes.</summary>
    public void CommitEdit()
    {
        if (!_editing) return;
        var text = _editor.Text.TrimEnd();
        EndEdit();
        if (string.IsNullOrWhiteSpace(text))
        {
            Delete();
            return;
        }
        if (text == Data.Text) return;
        SetText(text);
        StickyNoteManager.Archive(text);
    }

    /// <summary>Esc / ✕: leaves the text as it was. A note that never had any goes.</summary>
    public void CancelEdit()
    {
        if (!_editing) return;
        EndEdit();
        if (string.IsNullOrWhiteSpace(Data.Text)) Delete();
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CommitEdit();
            e.Handled = true;
        }
    }

    // ── Menu actions ─────────────────────────────────────────────────────────

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("S.Note.Edit", "", BeginEdit));
        menu.Items.Add(Item("S.Note.CopyText", "", CopyText));
        menu.Items.Add(new Separator());
        var topmost = new MenuItem { IsCheckable = true };
        topmost.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Note.Topmost");
        topmost.Click += (_, _) => SetTopmost(topmost.IsChecked);
        menu.Items.Add(topmost);
        var passThrough = new MenuItem { IsCheckable = true };
        passThrough.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Note.PassThrough");
        passThrough.Click += (_, _) => SetPassThrough(passThrough.IsChecked);
        menu.Items.Add(passThrough);
        var opacity = new MenuItem();
        opacity.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Note.Opacity");
        var opacityChoices = new List<(MenuItem Item, int Percent)>();
        foreach (int percent in OpacityChoices)
        {
            int p = percent;
            // Stays open, so several can be tried in a row; the tick follows the choice.
            var choice = new MenuItem { Header = $"{p}%", IsCheckable = true, StaysOpenOnClick = true };
            choice.Click += (_, _) =>
            {
                SetOpacity(p / 100.0);
                foreach (var (item, q) in opacityChoices) item.IsChecked = q == p;
            };
            opacity.Items.Add(choice);
            opacityChoices.Add((choice, p));
        }
        menu.Items.Add(opacity);
        var colours = BuildColourMenu(out var syncColours);
        menu.Items.Add(colours);
        menu.Items.Add(new Separator());
        var resize = new MenuItem { IsCheckable = true };
        resize.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Note.Resize");
        resize.Click += (_, _) => SetResizing(resize.IsChecked);
        menu.Items.Add(resize);
        var autoSize = Item("S.Note.AutoSize", "", ResetSize);
        menu.Items.Add(autoSize);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("S.Note.Delete", "", Delete));
        menu.Opened += (_, _) =>
        {
            topmost.IsChecked = Data.Topmost;
            passThrough.IsChecked = Data.PassThrough;
            resize.IsChecked = _resizing;
            foreach (var (item, p) in opacityChoices) item.IsChecked = (int)Math.Round(Data.Opacity * 100) == p;
            autoSize.IsEnabled = Data.Width is not null;
            syncColours();
        };
        return menu;

        static MenuItem Item(string key, string gesture, Action action)
        {
            var item = new MenuItem { InputGestureText = gesture };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
            item.Click += (_, _) => action();
            return item;
        }
    }

    private static readonly ColorPickerPopup ColourPicker = new();

    /// <summary>
    /// 顏色: this note's paper and tape, each from the 便利貼設定 presets or 自訂…, and 跟隨預設 to
    /// go back to the 便利貼設定 colours. Only this note changes.
    /// </summary>
    private MenuItem BuildColourMenu(out Action sync)
    {
        var root = new MenuItem();
        root.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Note.Colour");
        // The choices stay open when clicked, so colours can be tried one after another; the ticks
        // are brought up to date after each.
        Action syncAll = () => { };
        var paper = ColourChoices("S.Note.PaperColour", StickyNoteColors.Papers,
            () => Data.PaperColor ?? StickyNoteManager.Settings.PaperColor, StickyNoteColors.DefaultPaper,
            v => { SetColours(v, Data.TapeColor); syncAll(); }, out var syncPaper);
        var tape = ColourChoices("S.Note.TapeColour", StickyNoteColors.Tapes,
            () => Data.TapeColor ?? StickyNoteManager.Settings.TapeColor, StickyNoteColors.DefaultTape,
            v => { SetColours(Data.PaperColor, v); syncAll(); }, out var syncTape);
        var follow = new MenuItem { IsCheckable = true, StaysOpenOnClick = true };
        follow.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Note.ColourDefault");
        follow.Click += (_, _) => { SetColours(null, null); syncAll(); };
        root.Items.Add(paper);
        root.Items.Add(tape);
        root.Items.Add(new Separator());
        root.Items.Add(follow);
        syncAll = () =>
        {
            syncPaper();
            syncTape();
            follow.IsChecked = Data.PaperColor is null && Data.TapeColor is null;
        };
        sync = syncAll;
        return root;
    }

    private MenuItem ColourChoices(string key, string[] presets, Func<string> current, string fallback,
        Action<string> set, out Action sync)
    {
        var parent = new MenuItem();
        parent.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
        var items = new List<(MenuItem Item, string Hex)>();
        foreach (var hex in presets)
        {
            var item = new MenuItem
            {
                IsCheckable = true,
                StaysOpenOnClick = true,
                Header = new Border
                {
                    Width = 56,
                    Height = 16,
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                    Background = new SolidColorBrush(StickyNoteColors.Parse(hex, fallback)),
                },
            };
            item.Click += (_, _) => set(hex);
            parent.Items.Add(item);
            items.Add((item, hex));
        }
        parent.Items.Add(new Separator());
        var custom = new MenuItem();
        custom.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Settings.NoteCustomColor");
        custom.Click += (_, _) =>
        {
            if (Content is not FrameworkElement anchor) return;
            // After the menu has closed, so the picker is not shut by the menu going away.
            Dispatcher.BeginInvoke(() => ColourPicker.Toggle(anchor, StickyNoteColors.Parse(current(), fallback),
                colour => set(StickyNoteColors.ToHex(colour))), DispatcherPriority.Input);
        };
        parent.Items.Add(custom);
        sync = () =>
        {
            var now = StickyNoteColors.ToHex(StickyNoteColors.Parse(current(), fallback));
            bool preset = false;
            foreach (var (item, hex) in items)
            {
                item.IsChecked = string.Equals(hex, now, StringComparison.OrdinalIgnoreCase);
                preset |= item.IsChecked;
            }
            custom.IsCheckable = true;
            custom.IsChecked = !preset;
        };
        return parent;
    }

    /// <summary>This note's own colours; null for either follows 便利貼設定.</summary>
    public void SetColours(string? paper, string? tape)
    {
        Data.PaperColor = paper;
        Data.TapeColor = tape;
        ApplyAppearance();
        StickyNoteManager.RaiseChanged();
    }

    private void CopyText()
    {
        try { Clipboard.SetText(Data.Text); }
        catch (Exception) { /* clipboard busy */ }
    }

    public void SetTopmost(bool on)
    {
        Data.Topmost = on;
        Topmost = on;
        _pinButton.Content = new TextBlock
        {
            Text = on ? "" : "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
        };
        _pinButton.SetResourceReference(ToolTipProperty, on ? "S.Note.Unpin" : "S.Note.Topmost");
        if (on) AlwaysOnTop.Reassert(this);
        StickyNoteManager.RaiseChanged();
    }

    /// <summary>
    /// Lets every click through to what is under the note. It cannot be clicked to undo that, so
    /// the 便利貼 shortcut over it, the tray menu, and the 便利貼 page do.
    /// </summary>
    public void SetPassThrough(bool on)
    {
        if (Data.PassThrough == on) return;
        if (on && _editing) CancelEdit();
        Data.PassThrough = on;
        WindowStyles.SetClickThrough(this, on);
        // No message on the note — how to undo it is explained by the ⓘ in 設定 — only a flash, so a
        // shortcut that worked can be told from one that missed: once on, twice off.
        Flash(on ? 1 : 2);
        StickyNoteManager.RaiseChanged();
    }

    /// <summary>Dims the note and back <paramref name="times"/> times, ending at its own opacity.</summary>
    private void Flash(int times)
    {
        var flash = new DoubleAnimation(Data.Opacity, 0.2, TimeSpan.FromMilliseconds(150))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(times),
        };
        flash.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = Data.Opacity;
        };
        BeginAnimation(OpacityProperty, flash);
    }

    /// <summary>The choices the 透明度 menu offers, in percent.</summary>
    public static readonly int[] OpacityChoices = [100, 90, 80, 70, 60, 50, 40];

    /// <summary>This note's opacity only; the 便利貼設定 value is for new notes.</summary>
    public void SetOpacity(double value)
    {
        Data.Opacity = Math.Clamp(value, 0.4, 1);
        BeginAnimation(OpacityProperty, null);
        Opacity = Data.Opacity;
        StickyNoteManager.RaiseChanged();
    }

    public void Delete()
    {
        Close();
    }

    /// <summary>Brings the note to the middle of the screen the main window is on and flashes it.</summary>
    public void Locate()
    {
        if (Data.PassThrough) SetPassThrough(false);
        var anchor = Shell.ShellWindow.Current is { } shell ? ScreenGeometry.PhysicalBounds(shell) : System.Drawing.Rectangle.Empty;
        var screen = anchor.IsEmpty
            ? System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea
            : System.Windows.Forms.Screen.FromRectangle(anchor).WorkingArea;
        var me = ScreenGeometry.PhysicalBounds(this);
        int x = screen.Left + (screen.Width - me.Width) / 2;
        int y = screen.Top + (screen.Height - me.Height) / 2;
        ScreenGeometry.MoveToPhysical(this, x, y);
        AlwaysOnTop.Reassert(this);
        if (!Data.Topmost) Activate();

        var flash = new DoubleAnimation(Data.Opacity, 0.2, TimeSpan.FromMilliseconds(180)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
        flash.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = Data.Opacity;
        };
        BeginAnimation(OpacityProperty, flash);
    }

    // ── Size ─────────────────────────────────────────────────────────────────

    /// <summary>A dashed outline and grips on the right edge, the bottom edge and the corner.</summary>
    private void BuildResizeFrame()
    {
        _resizeFrame.Children.Add(new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x40, 0x40, 0x40)),
            StrokeDashArray = [4, 3],
            StrokeThickness = 1,
            IsHitTestVisible = false,
        });
        _resizeFrame.Children.Add(Grip(Cursors.SizeWE, HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, (dx, _) => (dx, 0)));
        _resizeFrame.Children.Add(Grip(Cursors.SizeNS, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, (_, dy) => (0, dy)));
        _resizeFrame.Children.Add(Grip(Cursors.SizeNWSE, HorizontalAlignment.Right, VerticalAlignment.Bottom, 14, 14, (dx, dy) => (dx, dy)));
    }

    private Thumb Grip(System.Windows.Input.Cursor cursor, HorizontalAlignment h, VerticalAlignment v, double width, double height,
        Func<double, double, (double, double)> pick)
    {
        var thumb = new Thumb
        {
            Cursor = cursor,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Width = width,
            Height = height,
            Opacity = 0.01,
        };
        thumb.DragDelta += (_, e) =>
        {
            var (dx, dy) = pick(e.HorizontalChange, e.VerticalChange);
            if (SizeToContent != SizeToContent.Manual)
            {
                var w = ActualWidth;
                var hgt = ActualHeight;
                SizeToContent = SizeToContent.Manual;
                Width = w;
                Height = hgt;
                _scroll.MaxHeight = double.PositiveInfinity;
                _editor.MaxHeight = double.PositiveInfinity;
            }
            Width = Math.Max(120 + Outer.Left + Outer.Right, Width + dx);
            Height = Math.Max(90 + Outer.Top + Outer.Bottom, Height + dy);
        };
        thumb.DragCompleted += (_, _) =>
        {
            Data.Width = Width;
            Data.Height = Height;
            StickyNoteManager.RaiseChanged();
        };
        return thumb;
    }

    /// <summary>The 調整大小 switch: grips on, or off again.</summary>
    public void SetResizing(bool on)
    {
        _resizing = on;
        _resizeFrame.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>恢復自動大小: the default width again and a height that follows the text.</summary>
    public void ResetSize()
    {
        Data.Width = null;
        Data.Height = null;
        ApplySizeMode();
        StickyNoteManager.RaiseChanged();
    }

    /// <summary>The first line, for the 便利貼 page.</summary>
    public string Preview
    {
        get
        {
            var text = Data.Text.Replace("\r\n", "\n").Trim();
            if (text.Length == 0) return LocalizationService.Get("S.Note.Empty");
            return text.Length > 120 ? text[..120] + "…" : text;
        }
    }

    /// <summary>
    /// The paper: a rectangle with its bottom-right corner folded up into a small curl, and a soft
    /// shadow under it.
    /// </summary>
    private sealed class NotePaper : FrameworkElement
    {
        private const double Curl = 13;
        private Color _paper = StickyNoteColors.Parse(null, StickyNoteColors.DefaultPaper);

        public NotePaper()
        {
            Effect = new DropShadowEffect { BlurRadius = 9, ShadowDepth = 2, Direction = 280, Opacity = 0.24, Color = Colors.Black };
        }

        public Color Paper
        {
            set
            {
                _paper = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, c = Curl;
            if (w <= c * 2 || h <= c * 2) return;

            var sheet = new StreamGeometry();
            using (var g = sheet.Open())
            {
                g.BeginFigure(new Point(0, 0), true, true);
                g.LineTo(new Point(w, 0), false, false);
                g.LineTo(new Point(w, h - c), false, false);
                g.LineTo(new Point(w - c, h), false, false);
                g.LineTo(new Point(0, h), false, false);
            }
            sheet.Freeze();
            var paper = new LinearGradientBrush(Lighten(_paper, 0.06), _paper, new Point(0, 0), new Point(0, 1));
            dc.DrawGeometry(paper, new Pen(new SolidColorBrush(Darken(_paper, 0.10)), 0.6), sheet);

            // The folded flap: the cut-away corner turned up over the paper, lit along its fold.
            var flap = new StreamGeometry();
            using (var g = flap.Open())
            {
                g.BeginFigure(new Point(w, h - c), true, true);
                g.QuadraticBezierTo(new Point(w - c * 0.62, h - c * 0.62), new Point(w - c * 1.05, h - c * 1.05), false, false);
                g.QuadraticBezierTo(new Point(w - c * 0.62, h - c * 0.62), new Point(w - c, h), false, false);
            }
            flap.Freeze();
            var flapBrush = new LinearGradientBrush(Darken(_paper, 0.16), Lighten(_paper, 0.20),
                new Point(1, 1), new Point(0, 0));
            dc.DrawGeometry(flapBrush, new Pen(new SolidColorBrush(Darken(_paper, 0.14)), 0.5), flap);
        }

        private static Color Lighten(Color c, double t) => Color.FromRgb(
            (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

        private static Color Darken(Color c, double t) => Color.FromRgb(
            (byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
    }
}
