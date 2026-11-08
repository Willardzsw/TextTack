using System.Windows.Controls;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Settings;

public partial class OcrModelCard : UserControl
{
    private readonly OcrModelSettings _settings;
    private readonly Action _save;
    private bool _loading;
    private CancellationTokenSource? _preparation;

    public OcrModelCard() : this(SettingsService.Instance.Current.OcrModels, () => SettingsService.Instance.Save()) { }

    internal OcrModelCard(OcrModelSettings settings, Action? save = null)
    {
        _settings = settings;
        _save = save ?? (() => { });
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LocalizationService.LanguageChanged += OnLanguageChanged;
            OcrExecution.Changed += OnExecutionChanged;
            LoadChoice();
        };
        Unloaded += (_, _) =>
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            OcrExecution.Changed -= OnExecutionChanged;
            _preparation?.Cancel();
        };
        LoadChoice();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => LoadChoice();

    private void LoadChoice()
    {
        _loading = true;
        try
        {
            ModelBox.ItemsSource = OcrModelCatalog.Options;
            ModelBox.Items.Refresh();
            ModelBox.SelectedValue = OcrModelCatalog.Normalize(_settings.Selection);
            ModelBox.ToolTip = (ModelBox.SelectedItem as OcrModelOption)?.Display;
            DetectorBox.ItemsSource = new[]
            {
                new OcrModelOption("tiny", "S.OcrModel.Tiny"), new("small", "S.OcrModel.Small"), new("medium", "S.OcrModel.Medium"),
            };
            RecognizerBox.ItemsSource = new[]
            {
                new OcrModelOption("small", "S.OcrModel.Small"), new("medium", "S.OcrModel.Medium"),
            };
            DetectorBox.SelectedValue = OcrModelCatalog.DetectorSize(_settings);
            RecognizerBox.SelectedValue = OcrModelCatalog.RecognizerSize(_settings);
            var devices = OcrExecution.Devices().ToList();
            var selectedDevice = OcrModelCatalog.Device(_settings);
            if (devices.All(d => d.Id != selectedDevice))
                devices.Add(new(selectedDevice, LocalizationService.Get("S.OcrModel.DeviceUnavailable")));
            DeviceBox.ItemsSource = devices;
            DeviceBox.SelectedValue = selectedDevice;
            RefreshStatus();
        }
        finally { _loading = false; }
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelBox.SelectedValue is not string choice) return;
        _settings.Selection = OcrModelCatalog.Normalize(choice);
        ModelBox.ToolTip = (ModelBox.SelectedItem as OcrModelOption)?.Display;
        _save();
        RefreshStatus();
    }

    private void Performance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.DetectorSize = DetectorBox.SelectedValue as string ?? "tiny";
        _settings.RecognizerSize = RecognizerBox.SelectedValue as string ?? "small";
        _settings.Device = DeviceBox.SelectedValue as string ?? "cpu";
        _save();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var ready = OcrModelAssets.IsPrepared(_settings);
        RecognizerBox.IsEnabled = _preparation is null &&
            OcrModelCatalog.Normalize(_settings.Selection) is OcrModelCatalog.ByLanguage or OcrModelCatalog.General;
        AssetStatus.Text = LocalizationService.Get(ready ? "S.OcrModel.Ready" : "S.OcrModel.PrepareNeeded");
        IntegratedGpuWarning.Visibility = DeviceBox.SelectedItem is OcrExecution.DeviceOption { Integrated: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrepareButton.IsEnabled = !ready && _preparation is null;
        ExecutionStatus.Text = OcrExecution.LastIdentity == OcrModelCatalog.Identity(_settings) && OcrExecution.LastDevice is { } device
            ? string.Format(LocalizationService.Get("S.OcrModel.ActualDevice"), device)
            : LocalizationService.Get("S.OcrModel.NextRun");
    }

    private void OnExecutionChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => RefreshStatus());

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_preparation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _preparation = cancellation;
        SetChoicesEnabled(false);
        PrepareButton.IsEnabled = false;
        try
        {
            await OcrModelAssets.PrepareAsync(_settings.Copy(), new Progress<string>(name =>
                AssetStatus.Text = string.Format(LocalizationService.Get("S.OcrModel.Preparing"), name)), cancellation.Token);
            RefreshStatus();
        }
        catch (OperationCanceledException) { AssetStatus.Text = LocalizationService.Get("S.OcrModel.PrepareNeeded"); }
        catch (Exception ex) { AssetStatus.Text = LocalizationService.Get("S.OcrModel.PrepareFailed") + " " + ex.Message; }
        finally
        {
            _preparation = null;
            SetChoicesEnabled(true);
            PrepareButton.IsEnabled = !OcrModelAssets.IsPrepared(_settings);
        }
    }

    private void SetChoicesEnabled(bool enabled)
    {
        ModelBox.IsEnabled = DetectorBox.IsEnabled = DeviceBox.IsEnabled = enabled;
        RecognizerBox.IsEnabled = enabled &&
            OcrModelCatalog.Normalize(_settings.Selection) is OcrModelCatalog.ByLanguage or OcrModelCatalog.General;
    }
}
