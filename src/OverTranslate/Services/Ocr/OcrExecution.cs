using Microsoft.ML.OnnxRuntime;
using System.Reflection;
using OverTranslate.Models;
using RapidOcrNet;

namespace OverTranslate.Services.Ocr;

internal static class OcrExecution
{
    /// <param name="Integrated">An integrated GPU — usually slower than the CPU for OCR.</param>
    internal sealed record DeviceOption(string Id, string Display, bool Integrated = false);
    internal static event EventHandler? Changed;
    internal static string? LastIdentity { get; private set; }
    internal static string? LastDevice { get; private set; }
    // Offline harness only; the application never enables profiling or records screen contents here.
    internal static string? ProfilePrefix { get; set; }

    internal static IReadOnlyList<DeviceOption> Devices()
    {
        var result = new List<DeviceOption> { new("cpu", "CPU") };
        try
        {
            // By name, strongest first. Two identical cards share a name; the first is used.
            result.AddRange(DirectMlDevice.Enumerate().Where(a => !DirectMlDevice.IsSoftware(a))
                .OrderByDescending(a => a.DedicatedMemory)
                .DistinctBy(a => a.Name)
                .Select(a => DirectMlDevice.IsIntegrated(a)
                    ? new DeviceOption($"directml:{a.Name}",
                        $"DirectML · {a.Name} {LocalizationService.Get("S.OcrModel.IntegratedTag")}", true)
                    : new DeviceOption($"directml:{a.Name}", $"DirectML · {a.Name}")));
        }
        catch { /* CPU remains available on machines without DXGI. */ }
        return result;
    }

    /// <summary>
    /// What an old position-numbered choice becomes: the GPU with the most dedicated memory — the
    /// fast one, which is what choosing a GPU was for — or the CPU when there is none.
    /// </summary>
    internal static string PreferredGpu() =>
        Devices().FirstOrDefault(d => d.Id != "cpu")?.Id ?? "cpu";

    /// <summary>The adapter a device id means now, or null when it is not in this machine.</summary>
    private static DirectMlDevice.Adapter? Resolve(string id)
    {
        var rest = id[9..];
        var adapters = DirectMlDevice.Enumerate().Where(a => !DirectMlDevice.IsSoftware(a)).ToList();
        if (int.TryParse(rest, out var index))
            return adapters.Where(a => a.Index == index).Select(a => (DirectMlDevice.Adapter?)a).FirstOrDefault();
        return adapters.Where(a => string.Equals(a.Name, rest, StringComparison.Ordinal))
            .OrderByDescending(a => a.DedicatedMemory)
            .Select(a => (DirectMlDevice.Adapter?)a).FirstOrDefault();
    }

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    internal static SessionOptions Options(OcrModelSettings settings, int threads, out string device)
    {
        var options = RapidOcr.GetDefaultSessionOptions(threads);
        device = "CPU";
        if (OcrModelCatalog.Device(settings) == "cpu") return options;
        try
        {
            var id = OcrModelCatalog.Device(settings);
            if (Resolve(id) is not { } adapter)
            {
                // The chosen GPU is not in this machine now (switched off, removed, renamed by a
                // driver). Recognition carries on, on the CPU, and the OCR card's 上次執行裝置 says so.
                Log.Warn("OCR device {Id} is not available; using the CPU", id);
                return options;
            }
            var index = adapter.Index;
            if (DirectMlDevice.Direct3D12Problem(adapter) is { } incompatible)
                throw new NotSupportedException(incompatible);
            if (DirectMlDevice.Load(OcrModelAssets.RuntimePath, OcrModelAssets.RuntimeFile) is { } missing)
                throw new InvalidOperationException(LocalizationService.Get("S.OcrModel.PrepareNeeded") + " " + missing);
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
            options.AppendExecutionProvider_DML(index);
            device = $"DirectML · {adapter.Name}";
            return options;
        }
        catch
        {
            options.Dispose();
            // An explicit GPU choice must not silently become a CPU session.
            throw;
        }
    }

    internal static void Initialize(RapidOcr engine, RapidOcrModelSet models, SessionOptions options)
    {
        if (ProfilePrefix is not { } prefix)
        {
            engine.InitModels(models, options);
            return;
        }
        // Every session needs its own file: ORT timestamps have one-second resolution. Reusing
        // the same options' prefix across det/cls/rec otherwise overwrites profiles or locks files.
        void Profile(string role)
        {
            options.EnableProfiling = false;
            options.ProfileOutputPathPrefix = prefix + "-" + role + "-" + Guid.NewGuid().ToString("N");
            options.EnableProfiling = true;
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Profile("det");
        ((TextDetector)typeof(RapidOcr).GetField("_textDetector", flags)!.GetValue(engine)!)
            .InitModel(models.DetModelPath, models.DetMean, models.DetStd, options);
        Profile("cls");
        ((TextClassifier)typeof(RapidOcr).GetField("_textClassifier", flags)!.GetValue(engine)!)
            .InitModel(models.ClsModelPath, options);
        Profile("rec");
        ((TextRecognizer)typeof(RapidOcr).GetField("_textRecognizer", flags)!.GetValue(engine)!)
            .InitModel(models.RecModelPath, models.KeysPath, options);
    }

    internal static void Report(string identity, string device)
    {
        LastIdentity = identity;
        LastDevice = device;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
