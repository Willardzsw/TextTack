using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NLog;
using OverTranslate.Services;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace OverTranslate.Views.Capture;

/// <summary>
/// 二維碼識別's answer: what each code in the selection says, with 複製, 開啟連結 (only for a web
/// address) and 翻譯 under it.
/// </summary>
/// <remarks>
/// Shown where the selection was, after the capture has ended — the code is read and the capture
/// has nothing left to do. Nothing is opened by itself: a link is only followed when its button is
/// pressed, since the text of a code is whatever whoever printed it wanted.
/// </remarks>
public sealed class CodeResultWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static CodeResultWindow? _current;

    private readonly int _physX, _physY;

    private CodeResultWindow(IReadOnlyList<ScannedCode> codes, int physX, int physY, Action<string> translate)
    {
        _physX = physX;
        _physY = physY;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = LocalizationService.Get("S.Code.QrCode");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var list = new StackPanel();
        for (int i = 0; i < codes.Count; i++)
            list.Children.Add(Section(codes[i], translate, first: i == 0));

        var close = new Button
        {
            Content = "",
            Style = (Style)FindResource("IconButton"),
            Width = 24,
            Height = 24,
            FontSize = 8,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -6, 0),
        };
        close.SetResourceReference(ToolTipProperty, "S.Common.Close");
        close.Click += (_, _) => Close();

        var grid = new Grid { Margin = new Thickness(14, 10, 12, 12) };
        grid.Children.Add(list);
        grid.Children.Add(close);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = grid,
            MinWidth = 280,
            MaxWidth = 460,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.2, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "HintCardBg");
        card.SetResourceReference(Border.BorderBrushProperty, "HintCardBorder");
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not TextBox) DragMove();
        };

        Content = new Border { Margin = new Thickness(14, 12, 14, 16), Child = card };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        SourceInitialized += (_, _) => PlaceAt(_physX, _physY);
    }

    /// <summary>Replaces any answer still up with this one, its top-left near the physical point.</summary>
    public static void Show(IReadOnlyList<ScannedCode> codes, int physX, int physY, Action<string> translate)
    {
        _current?.Close();
        var window = new CodeResultWindow(codes, physX, physY, translate);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window)) _current = null;
        };
        _current = window;
        window.Show();
        window.Activate();
    }

    private UIElement Section(ScannedCode code, Action<string> translate, bool first)
    {
        var panel = new StackPanel { Margin = new Thickness(0, first ? 0 : 14, 0, 0) };

        var kind = new TextBlock
        {
            Text = code.IsTwoDimensional && code.Format == ZXing.BarcodeFormat.QR_CODE
                ? LocalizationService.Get("S.Code.QrCode")
                : $"{LocalizationService.Get(code.IsTwoDimensional ? "S.Code.QrCode" : "S.Code.Barcode")} · {code.Format}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 28, 6),
        };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        kind.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        panel.Children.Add(kind);

        // A read-only box rather than a label, so part of it can be selected and copied too.
        var text = new TextBox
        {
            Text = code.Text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            MaxHeight = 180,
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0),
        };
        text.SetResourceReference(ForegroundProperty, "AppTextPrimary");
        text.SetResourceReference(FontFamilyProperty, "AppFont");
        panel.Children.Add(text);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var copy = ActionButton("S.Code.Copy", accent: code.Link is null);
        copy.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(code.Text);
                copy.Content = LocalizationService.Get("S.Main.CopiedTitle");
            }
            catch (Exception ex)
            {
                // Another program can hold the clipboard for a moment; the text is still selectable.
                Log.Warn(ex, "Copying a scanned code failed");
            }
        };
        buttons.Children.Add(copy);

        if (code.Link is { } link)
        {
            var open = ActionButton("S.Code.OpenLink", accent: true);
            open.ToolTip = link.AbsoluteUri;
            open.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
                    Close();
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Opening a scanned link failed");
                }
            };
            buttons.Children.Add(open);
        }

        var translateButton = ActionButton("S.Code.Translate", accent: false);
        translateButton.Click += (_, _) =>
        {
            Close();
            translate(code.Text);
        };
        buttons.Children.Add(translateButton);

        panel.Children.Add(buttons);
        return panel;
    }

    private Button ActionButton(string key, bool accent)
    {
        var button = new Button
        {
            Content = LocalizationService.Get(key),
            Style = (Style)FindResource(accent ? "AccentButton" : "FlatButton"),
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 64,
        };
        return button;
    }

    // ---- placing: physical pixels, kept on the screen the point is on ----

    private void PlaceAt(int physX, int physY)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Measured first, so the size the clamp works with is the size the window will be.
        ((FrameworkElement)Content).Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ((FrameworkElement)Content).DesiredSize;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(physX, physY)).WorkingArea;
        double scale = ScreenGeometry.ScaleAt(physX, physY);
        int width = (int)Math.Ceiling(size.Width * scale), height = (int)Math.Ceiling(size.Height * scale);
        int x = Math.Clamp(physX, screen.Left, Math.Max(screen.Left, screen.Right - width));
        int y = Math.Clamp(physY, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
