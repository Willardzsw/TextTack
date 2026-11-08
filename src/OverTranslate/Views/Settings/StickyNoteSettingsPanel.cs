using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OverTranslate.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.StickyNote;
// UseWindowsForms puts System.Windows.Forms in the implicit usings
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Settings;

/// <summary>
/// 便利貼設定: the picture-reading language, a new note's width and font size, the paper and tape
/// colours, and the archive log. Saved as soon as anything changes and shown on every open note.
/// </summary>
public sealed class StickyNoteSettingsPanel : UserControl
{
    private bool _loading;
    private readonly List<Action> _reloads = [];
    private readonly ColorPickerPopup _picker = new();

    public StickyNoteSettingsPanel()
    {
        _loading = true;
        var grid = new Grid();
        // Shared with the 新建便利貼 shortcut row the settings page puts above this panel.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "NoteSettingsLabel" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 識別語言
        var language = Box(180);
        foreach (var item in LanguageData.OcrSourceLanguages)
        {
            var option = new ComboBoxItem { Tag = item.Code };
            option.SetResourceReference(ContentProperty, item.NameKey);
            language.Items.Add(option);
        }
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem { Tag: string code }) Save(() => S.OcrLanguage = code);
        };
        _reloads.Add(() => Select(language, S.OcrLanguage));
        AddRow(grid, "S.Settings.NoteOcrLanguage", language);

        // 預設寬度
        var width = Numbers([160, 200, 260, 340], "px", v => S.Width = v);
        _reloads.Add(() => SelectNumber(width, S.Width, "px"));
        AddRow(grid, "S.Settings.NoteWidth", width);

        // 字號
        var font = Numbers([12, 13, 14, 16, 18, 20], "", v => S.FontSize = v);
        _reloads.Add(() => SelectNumber(font, S.FontSize, ""));
        AddRow(grid, "S.Settings.NoteFontSize", font);

        // 預設透明度 (new notes; each note's own is on its right-click menu)
        var opacity = Box(100);
        foreach (int p in StickyNoteWindow.OpacityChoices)
            opacity.Items.Add(new ComboBoxItem { Content = $"{p}%", Tag = p / 100.0 });
        opacity.SelectionChanged += (_, _) =>
        {
            if (opacity.SelectedItem is ComboBoxItem { Tag: double v }) Save(() => S.Opacity = v);
        };
        _reloads.Add(() =>
        {
            double current = Math.Round(Math.Clamp(S.Opacity, 0.4, 1) * 10) / 10;
            Select(opacity, current);
        });
        AddRow(grid, "S.Settings.NoteOpacity", opacity);

        // 紙張顏色 / 膠帶顏色
        AddRow(grid, "S.Settings.NotePaperColor",
            Swatches(StickyNoteColors.Papers, () => S.PaperColor, v => S.PaperColor = v, StickyNoteColors.DefaultPaper));
        AddRow(grid, "S.Settings.NoteTapeColor",
            Swatches(StickyNoteColors.Tapes, () => S.TapeColor, v => S.TapeColor = v, StickyNoteColors.DefaultTape));

        // 自動存檔
        var archive = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        archive.SetResourceReference(StyleProperty, "ModernCheckBox");
        var archiveLabel = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        archiveLabel.SetResourceReference(TextBlock.TextProperty, "S.Settings.NoteArchive");
        archiveLabel.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");
        var archiveRow = new StackPanel { Orientation = Orientation.Horizontal };
        archiveRow.Children.Add(archive);
        archiveRow.Children.Add(archiveLabel);

        var folder = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        folder.SetResourceReference(StyleProperty, "FieldHint");
        var choose = SmallButton("S.Settings.NoteArchiveChoose", ChooseFolder);
        var open = SmallButton("S.Settings.NoteArchiveOpen", OpenFolder);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(choose);
        buttons.Children.Add(open);

        var archiveBlock = new StackPanel();
        archiveBlock.Children.Add(archiveRow);
        archiveBlock.Children.Add(folder);
        archiveBlock.Children.Add(buttons);
        var archiveHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        archiveHint.SetResourceReference(StyleProperty, "FieldHint");
        archiveHint.SetResourceReference(TextBlock.TextProperty, "S.Settings.NoteArchiveHint");
        archiveBlock.Children.Add(archiveHint);
        archive.Click += (_, _) => Save(() => S.ArchiveEnabled = archive.IsChecked == true);
        _reloads.Add(() =>
        {
            archive.IsChecked = S.ArchiveEnabled;
            folder.Text = System.IO.Path.Combine(StickyNoteArchive.FolderFor(S), StickyNoteArchive.FileName);
            buttons.IsEnabled = folder.IsEnabled = S.ArchiveEnabled;
        });
        AddRow(grid, "S.Settings.NoteArchiveTitle", archiveBlock);

        Content = grid;
        _loading = false;
        Reload();
    }

    private static StickyNoteSettings S => StickyNoteManager.Settings;

    /// <summary>Raised after a change is written, so the page can show 已儲存.</summary>
    public event EventHandler? Saved;

    public void Reload()
    {
        _loading = true;
        try
        {
            foreach (var reload in _reloads) reload();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Action apply)
    {
        if (_loading) return;
        apply();
        SettingsService.Instance.Save();
        StickyNoteManager.ApplyAppearance();
        Reload();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private static void AddRow(Grid grid, string labelKey, UIElement content)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 14, 6) };
        label.SetResourceReference(StyleProperty, "OptionTitle");
        label.SetResourceReference(TextBlock.TextProperty, labelKey);
        Grid.SetRow(label, row);
        grid.Children.Add(label);
        if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
    }

    private static ComboBox Box(double width)
    {
        var box = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        box.SetResourceReference(StyleProperty, "ModernComboBox");
        return box;
    }

    private ComboBox Numbers(int[] values, string unit, Action<double> set)
    {
        var box = Box(100);
        foreach (var v in values) box.Items.Add(new ComboBoxItem { Content = $"{v}{unit}", Tag = (double)v });
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: double v }) Save(() => set(v));
        };
        return box;
    }

    private static void SelectNumber(ComboBox box, double value, string unit)
    {
        if (!box.Items.Cast<ComboBoxItem>().Any(i => Equals(i.Tag, value)))
            box.Items.Add(new ComboBoxItem { Content = $"{value:0}{unit}", Tag = value });
        Select(box, value);
    }

    private static void Select(ComboBox box, object value)
    {
        foreach (ComboBoxItem item in box.Items)
            if (Equals(item.Tag, value)) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    /// <summary>A row of colour chips, the chosen one ringed, and 自訂… for any other colour.</summary>
    private FrameworkElement Swatches(string[] colors, Func<string> get, Action<string> set, string fallback)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var chips = new List<(Border Chip, string Hex)>();
        foreach (var hex in colors)
        {
            var chip = Chip(StickyNoteColors.Parse(hex, fallback));
            chip.MouseLeftButtonUp += (_, _) => Save(() => set(hex));
            chips.Add((chip, hex));
            row.Children.Add(chip);
        }

        var custom = SmallButton("S.Settings.NoteCustomColor", () => { });
        custom.Margin = new Thickness(6, 0, 0, 0);
        custom.Click += (_, _) => _picker.Toggle(custom, StickyNoteColors.Parse(get(), fallback),
            color => Save(() => set(StickyNoteColors.ToHex(color))));
        row.Children.Add(custom);

        _reloads.Add(() =>
        {
            var current = StickyNoteColors.ToHex(StickyNoteColors.Parse(get(), fallback));
            foreach (var (chip, hex) in chips)
                chip.BorderThickness = new Thickness(string.Equals(hex, current, StringComparison.OrdinalIgnoreCase) ? 2.5 : 1);
            custom.Background = colors.Any(h => string.Equals(h, current, StringComparison.OrdinalIgnoreCase))
                ? null
                : new SolidColorBrush(StickyNoteColors.Parse(current, fallback));
        });
        return row;
    }

    private static Border Chip(Color color)
    {
        var chip = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(color),
            Cursor = Cursors.Hand,
        };
        chip.SetResourceReference(Border.BorderBrushProperty, "AppTextSecondary");
        return chip;
    }

    private static Button SmallButton(string key, Action click)
    {
        var button = new Button { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), MinHeight = 26 };
        button.SetResourceReference(StyleProperty, "FlatButton");
        button.SetResourceReference(ContentProperty, key);
        button.Click += (_, _) => click();
        return button;
    }

    private void ChooseFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = StickyNoteArchive.FolderFor(S),
            UseDescriptionForTitle = true,
            Description = LocalizationService.Get("S.Settings.NoteArchiveChoose"),
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            Save(() => S.ArchiveFolder = dialog.SelectedPath);
    }

    private static void OpenFolder()
    {
        try
        {
            var dir = StickyNoteArchive.FolderFor(S);
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception) { /* nothing to show it in */ }
    }
}
