using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Settings;

/// <summary>
/// 軟體更新: this copy's version and kind, the 啟動時自動檢查 tick, and 檢查更新 → 下載並更新,
/// against the user's own GitHub releases. A source build shows why it has none of this.
/// </summary>
public sealed class UpdatePanel : UserControl
{
    private readonly TextBlock _current = Hint();
    private readonly CheckBox _auto = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _check;
    private readonly Button _install;
    private readonly Button _notes;
    private readonly TextBlock _status = Hint();
    private AppRelease? _found;
    private bool _busy;
    private bool _loading;

    /// <summary>Raised after the tick is saved, so the page can show 已儲存.</summary>
    public event EventHandler? Saved;

    /// <summary>A release the startup check already found, shared so the panel opens showing it.</summary>
    public static AppRelease? LastFound { get; set; }

    public UpdatePanel()
    {
        var root = new StackPanel();
        root.Children.Add(_current);

        _auto.SetResourceReference(StyleProperty, "ModernCheckBox");
        var autoLabel = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        autoLabel.SetResourceReference(TextBlock.TextProperty, "S.Settings.UpdateAutoCheck");
        autoLabel.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");
        var autoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        autoRow.Children.Add(_auto);
        autoRow.Children.Add(autoLabel);
        root.Children.Add(autoRow);
        _auto.Click += (_, _) =>
        {
            if (_loading) return;
            SettingsService.Instance.Current.AutoCheckUpdates = _auto.IsChecked == true;
            SettingsService.Instance.Save();
            Saved?.Invoke(this, EventArgs.Empty);
        };

        _check = SmallButton("S.Settings.UpdateCheck", () => _ = CheckAsync());
        _install = SmallButton("S.Settings.UpdateInstall", () => _ = InstallAsync());
        _notes = SmallButton("S.Settings.UpdateNotes", OpenNotes);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_check);
        buttons.Children.Add(_install);
        buttons.Children.Add(_notes);
        root.Children.Add(buttons);

        _status.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(_status);

        Content = root;
        Reload();
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            var kindKey = AppUpdateService.InstallKind switch
            {
                AppInstallKind.SourceBuild => "S.Settings.UpdateKindSource",
                AppInstallKind.Installed => "S.Settings.UpdateKindInstalled",
                _ => "S.Settings.UpdateKindPortable",
            };
            _current.Text = LocalizationService.Format("S.Settings.UpdateCurrent",
                AppUpdateService.CurrentVersion.ToString(3), LocalizationService.Get(kindKey));
            _auto.IsChecked = SettingsService.Instance.Current.AutoCheckUpdates;
            _found ??= LastFound;

            bool can = AppUpdateService.CanUpdate;
            // Always changeable, a source build included: the choice is kept for when it runs as a release.
            _auto.IsEnabled = true;
            _check.IsEnabled = can && !_busy;
            _install.Visibility = _found is not null ? Visibility.Visible : Visibility.Collapsed;
            _install.IsEnabled = !_busy;
            _notes.Visibility = _found is not null ? Visibility.Visible : Visibility.Collapsed;
            if (!can) _status.Text = LocalizationService.Get("S.Settings.UpdateSourceBuild");
            else if (!_busy && _found is not null)
                _status.Text = LocalizationService.Format("S.Settings.UpdateAvailable", _found.Version.ToString(3));
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        _status.Text = LocalizationService.Get("S.Settings.UpdateChecking");
        Reload();
        try
        {
            _found = LastFound = await AppUpdateService.CheckAsync();
            _status.Text = _found is null
                ? LocalizationService.Get("S.Settings.UpdateLatest")
                : LocalizationService.Format("S.Settings.UpdateAvailable", _found.Version.ToString(3));
        }
        catch (Exception ex)
        {
            _status.Text = LocalizationService.Format("S.Settings.UpdateCheckFailed", ex.Message);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private async Task InstallAsync()
    {
        if (_busy || _found is not { } release) return;
        _busy = true;
        Reload();
        var progress = new Progress<double>(p =>
            _status.Text = LocalizationService.Format("S.Settings.UpdateDownloading", (int)Math.Round(p * 100)));
        _status.Text = LocalizationService.Format("S.Settings.UpdateDownloading", 0);
        try
        {
            var script = await AppUpdateService.PrepareAsync(release, progress);
            _status.Text = LocalizationService.Get("S.Settings.UpdateRestarting");
            AppUpdateService.Launch(script);
            await Task.Delay(800);
            if (System.Windows.Application.Current.MainWindow is MainWindow main) main.ExitForUpdate();
            else System.Windows.Application.Current.Shutdown();
        }
        catch (AppUpdateService.UpdateException ex)
        {
            _status.Text = LocalizationService.Get(ex.MessageKey);
            _busy = false;
            Reload();
        }
        catch (Exception ex)
        {
            _status.Text = LocalizationService.Format("S.Settings.UpdateFailed", ex.Message);
            _busy = false;
            Reload();
        }
    }

    private void OpenNotes()
    {
        var url = _found?.PageUrl ?? AppUpdateService.ReleasesPage;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception) { /* no browser to open it in */ }
    }

    private static TextBlock Hint()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(StyleProperty, "FieldHint");
        return text;
    }

    private static Button SmallButton(string key, Action click)
    {
        var button = new Button { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), MinHeight = 26 };
        button.SetResourceReference(StyleProperty, "FlatButton");
        button.SetResourceReference(ContentProperty, key);
        button.Click += (_, _) => click();
        return button;
    }
}
