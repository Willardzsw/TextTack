using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace OverTranslate.Views.Capture;

/// <summary>
/// 調整截圖區域: the box's top-left corner (on its monitor) and size typed in, with 預設 sizes
/// that can be added to and edited. Each half applies only while its box is ticked.
/// </summary>
internal sealed partial class CaptureRegionDialog : Window
{
    private readonly CheckBox _usePosition = new();
    private readonly CheckBox _useSize = new() { IsChecked = true };
    private readonly NumberField _x, _y, _w, _h;

    public bool UsePosition => _usePosition.IsChecked == true;
    public bool UseSize => _useSize.IsChecked == true;
    public int RegionX => _x.Value;
    public int RegionY => _y.Value;
    public int RegionWidth => _w.Value;
    public int RegionHeight => _h.Value;

    private (int X, int Y, System.Drawing.Rectangle WorkArea)? _placeAt;

    public CaptureRegionDialog(int x, int y, int width, int height, string unit)
    {
        SetResourceReference(TitleProperty, "S.Capture.RegionTitle");
        SetResourceReference(BackgroundProperty, "AppWindowBg");
        SetResourceReference(ForegroundProperty, "AppTextPrimary");
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _x = new NumberField(x, 0, 100000);
        _y = new NumberField(y, 0, 100000);
        _w = new NumberField(width, 1, 100000);
        _h = new NumberField(height, 1, 100000);

        var grid = new Grid { Margin = new Thickness(16, 14, 16, 14) };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddRow(grid, 0, _usePosition, "S.Capture.RegionPosition", _x, _y, unit);
        AddRow(grid, 1, _useSize, "S.Capture.RegionSize", _w, _h, unit);

        var presets = new Button { Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4), MinHeight = 30 };
        presets.SetResourceReference(StyleProperty, "FlatButton");
        var presetsLabel = new StackPanel { Orientation = Orientation.Horizontal };
        var presetsText = new TextBlock();
        presetsText.SetResourceReference(TextBlock.TextProperty, "S.Capture.RegionPresets");
        presetsLabel.Children.Add(presetsText);
        presetsLabel.Children.Add(new TextBlock
        {
            Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        presets.Content = presetsLabel;
        presets.Click += (_, _) => OpenPresets(presets);
        Grid.SetRow(presets, 1);
        Grid.SetColumn(presets, 5);
        grid.Children.Add(presets);

        var ok = new Button { IsDefault = true, MinWidth = 88, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButton");
        ok.SetResourceReference(ContentProperty, "S.Capture.RegionOk");
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { IsCancel = true, MinWidth = 88, MinHeight = 30 };
        cancel.SetResourceReference(StyleProperty, "FlatButton");
        cancel.SetResourceReference(ContentProperty, "S.Common.Cancel");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 6);
        grid.Children.Add(buttons);

        Content = grid;

        _usePosition.Checked += (_, _) => SyncEnabled();
        _usePosition.Unchecked += (_, _) => SyncEnabled();
        _useSize.Checked += (_, _) => SyncEnabled();
        _useSize.Unchecked += (_, _) => SyncEnabled();
        SyncEnabled();

        // Typing in here: Esc, Ctrl+C and the capture's other keys belong to these boxes.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            TypingWindows.Add(hwnd);
            Closed += (_, _) => TypingWindows.Remove(hwnd);
            if (_placeAt is { } at) ScreenGeometry.MoveToPhysical(this, at.X, at.Y);
        };
        Loaded += (_, _) =>
        {
            KeepOnWorkArea();
            _w.Focus();
        };
    }

    /// <summary>Opens with its top-left corner at a physical point, kept on <paramref name="workArea"/>.</summary>
    public void PlaceAtPhysical(int x, int y, System.Drawing.Rectangle workArea) => _placeAt = (x, y, workArea);

    private void KeepOnWorkArea()
    {
        if (_placeAt is not { } at) return;
        var current = ScreenGeometry.PhysicalBounds(this);
        int x = Math.Clamp(current.Left, at.WorkArea.Left, Math.Max(at.WorkArea.Left, at.WorkArea.Right - current.Width));
        int y = Math.Clamp(current.Top, at.WorkArea.Top, Math.Max(at.WorkArea.Top, at.WorkArea.Bottom - current.Height));
        if (x != current.Left || y != current.Top) ScreenGeometry.MoveToPhysical(this, x, y);
    }

    private void SyncEnabled()
    {
        _x.IsEnabled = _y.IsEnabled = UsePosition;
        _w.IsEnabled = _h.IsEnabled = UseSize;
    }

    private static void AddRow(Grid grid, int row, CheckBox check, string labelKey, NumberField a, NumberField b, string unit)
    {
        check.SetResourceReference(StyleProperty, "ModernCheckBox");
        check.VerticalAlignment = VerticalAlignment.Center;
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
        label.SetResourceReference(TextBlock.TextProperty, labelKey);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        head.Children.Add(check);
        head.Children.Add(label);
        // Clicking the words ticks the box, as a label would.
        label.MouseLeftButtonUp += (_, _) => check.IsChecked = check.IsChecked != true;

        Place(grid, head, row, 0);
        Place(grid, a, row, 1);
        Place(grid, new TextBlock { Text = "×", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, row, 2);
        Place(grid, b, row, 3);
        var unitText = new TextBlock { Text = unit, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        unitText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        Place(grid, unitText, row, 4);

        static void Place(Grid g, UIElement e, int r, int c)
        {
            Grid.SetRow(e, r);
            Grid.SetColumn(e, c);
            g.Children.Add(e);
        }
    }

    private void OpenPresets(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var preset in CaptureOptions.SizePresets)
        {
            if (ParsePreset(preset) is not { } size) continue;
            var item = new MenuItem { Header = $"{size.Width} × {size.Height}" };
            item.Click += (_, _) =>
            {
                _useSize.IsChecked = true;
                _w.Value = size.Width;
                _h.Value = size.Height;
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var add = new MenuItem();
        add.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Capture.RegionAddPreset");
        add.Click += (_, _) =>
        {
            var entry = $"{_w.Value}x{_h.Value}";
            if (!CaptureOptions.SizePresets.Any(p => ParsePreset(p) == (_w.Value, _h.Value)))
            {
                CaptureOptions.SizePresets.Add(entry);
                SettingsService.Instance.Save();
            }
        };
        menu.Items.Add(add);

        var edit = new MenuItem();
        edit.SetResourceReference(HeaderedItemsControl.HeaderProperty, "S.Capture.RegionEditPresets");
        edit.Click += (_, _) => EditPresets();
        menu.Items.Add(edit);

        menu.IsOpen = true;
    }

    private static Models.CaptureSettings CaptureOptions => SettingsService.Instance.Current.Capture;

    /// <summary>編輯預設值: the list as text, one 寬x高 a line.</summary>
    private void EditPresets()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = 240,
            Height = 180,
            Padding = new Thickness(6),
            Text = string.Join(Environment.NewLine, CaptureOptions.SizePresets),
        };
        box.SetResourceReference(BackgroundProperty, "AppInputBg");
        box.SetResourceReference(ForegroundProperty, "AppTextPrimary");
        box.SetResourceReference(BorderBrushProperty, "AppInputBorder");

        var hint = new TextBlock { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = 240 };
        hint.SetResourceReference(TextBlock.TextProperty, "S.Capture.PresetsHint");
        hint.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        var editor = new Window
        {
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        editor.SetResourceReference(TitleProperty, "S.Capture.PresetsTitle");
        editor.SetResourceReference(BackgroundProperty, "AppWindowBg");
        editor.SetResourceReference(ForegroundProperty, "AppTextPrimary");

        var ok = new Button { IsDefault = false, MinWidth = 80, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButton");
        ok.SetResourceReference(ContentProperty, "S.Capture.RegionOk");
        ok.Click += (_, _) => editor.DialogResult = true;
        var cancel = new Button { IsCancel = true, MinWidth = 80, MinHeight = 30 };
        cancel.SetResourceReference(StyleProperty, "FlatButton");
        cancel.SetResourceReference(ContentProperty, "S.Common.Cancel");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(hint);
        root.Children.Add(box);
        root.Children.Add(buttons);
        editor.Content = root;
        editor.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(editor).Handle;
            TypingWindows.Add(hwnd);
            editor.Closed += (_, _) => TypingWindows.Remove(hwnd);
        };
        editor.Loaded += (_, _) => box.Focus();

        if (editor.ShowDialog() != true) return;
        CaptureOptions.SizePresets = ParsePresetList(box.Text);
        SettingsService.Instance.Save();
    }

    [GeneratedRegex(@"^\s*(\d{1,6})\s*[xX×*,\s]\s*(\d{1,6})\s*$")]
    private static partial Regex PresetPattern();

    /// <summary>"640x480", "640 × 480", "640*480" or "640,480"; null for anything else or a zero.</summary>
    internal static (int Width, int Height)? ParsePreset(string? text)
    {
        if (text is null) return null;
        var m = PresetPattern().Match(text);
        if (!m.Success) return null;
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
        return w > 0 && h > 0 ? (w, h) : null;
    }

    /// <summary>The lines that read as sizes, written back as "寬x高", repeats dropped.</summary>
    internal static List<string> ParsePresetList(string text) => text
        .Split('\n')
        .Select(ParsePreset)
        .OfType<(int Width, int Height)>()
        .Distinct()
        .Select(s => $"{s.Width}x{s.Height}")
        .ToList();

    /// <summary>
    /// A whole-number box with up and down arrows; the arrow keys and the wheel step it too.
    /// </summary>
    private sealed class NumberField : Grid
    {
        private readonly TextBox _text = new() { Width = 72, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly int _min, _max;

        public NumberField(int value, int min, int max)
        {
            _min = min;
            _max = max;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _text.SetResourceReference(StyleProperty, "ModernTextBox");
            _text.Height = 30;
            Children.Add(_text);

            var arrows = new StackPanel { Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            arrows.Children.Add(Arrow("", +1));
            arrows.Children.Add(Arrow("", -1));
            SetColumn(arrows, 1);
            Children.Add(arrows);

            Value = value;
            _text.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            System.Windows.DataObject.AddPastingHandler(_text, (_, e) =>
            {
                if (e.DataObject.GetData(typeof(string)) is not string s || !s.Trim().All(char.IsDigit)) e.CancelCommand();
            });
            _text.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Up) { Step(+1); e.Handled = true; }
                else if (e.Key == Key.Down) { Step(-1); e.Handled = true; }
            };
            _text.PreviewMouseWheel += (_, e) =>
            {
                Step(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            };
            _text.GotKeyboardFocus += (_, _) => _text.SelectAll();
            _text.LostKeyboardFocus += (_, _) => Value = Value;
        }

        public int Value
        {
            get => int.TryParse(_text.Text.Trim(), out var v) ? Math.Clamp(v, _min, _max) : _min;
            set => _text.Text = Math.Clamp(value, _min, _max).ToString();
        }

        public new bool Focus() => _text.Focus();

        private void Step(int by)
        {
            Value = Value + by;
            _text.CaretIndex = _text.Text.Length;
        }

        private RepeatButton Arrow(string glyph, int by)
        {
            var b = new RepeatButton
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 8,
                },
                Width = 18,
                Height = 15,
                Padding = new Thickness(0),
                Focusable = false,
                Delay = 350,
                Interval = 50,
            };
            b.Background = System.Windows.Media.Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.SetResourceReference(ForegroundProperty, "AppTextSecondary");
            b.Click += (_, _) => Step(by);
            return b;
        }
    }
}
