using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLog;

namespace OverTranslate.Services;

/// <summary>How this copy was put on the machine, which decides how it is updated.</summary>
public enum AppInstallKind
{
    /// <summary>Built from the repository; updated by building it again, never from a release.</summary>
    SourceBuild,

    /// <summary>Installed by the release's Setup.exe; updated by running the newer one silently.</summary>
    Installed,

    /// <summary>Unpacked from the release's portable zip; updated by copying the newer files over it.</summary>
    Portable,
}

public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A release on the user's own repository that is newer than this copy.</summary>
public sealed record AppRelease(Version Version, string Name, string PageUrl, ReleaseAsset Package, ReleaseAsset Checksums);

/// <summary>
/// In-app update from the user's own GitHub repository (Willardzsw/TextTack): asks for its latest
/// release, downloads the package that matches how this copy was installed, checks it against the
/// release's SHA256SUMS.txt, and hands over to a small script that replaces the files once the
/// application has exited and then starts it again.
/// </summary>
/// <remarks>
/// Only this repository. The upstream OverTranslate project is never asked, which is the user's
/// standing rule. A source build never updates itself: it is replaced by building it again.
/// profile-thai, beside the executable, is never touched — the release packages do not contain it
/// and the copy step excludes it besides.
/// </remarks>
public static class AppUpdateService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public const string Repository = "Willardzsw/TextTack";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string ChecksumsName = "SHA256SUMS.txt";
    private const string ExecutableName = "TextTack.exe";

    // Lazy, and so independent of the order the static fields below are initialized in: built
    // eagerly it ran before CurrentVersion was set and threw, taking the settings page down with it.
    private static readonly Lazy<HttpClient> LazyHttp = new(CreateClient);
    private static HttpClient Http => LazyHttp.Value;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub refuses API requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TextTack/{CurrentVersion.ToString(3)}");
        return client;
    }

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static string AppDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static AppInstallKind InstallKind { get; } = DetectInstallKind(AppDirectory);

    /// <summary>
    /// A source build sits under the project (bin\Release\…\win-x64), so the project file is a few
    /// folders up; an installed copy has Inno Setup's uninstaller beside it.
    /// </summary>
    internal static AppInstallKind DetectInstallKind(string directory)
    {
        var dir = new DirectoryInfo(directory);
        var probe = dir;
        for (int up = 0; up <= 6 && probe is not null; up++, probe = probe.Parent)
            if (File.Exists(Path.Combine(probe.FullName, "OverTranslate.csproj"))) return AppInstallKind.SourceBuild;

        return dir.Exists && dir.EnumerateFiles("unins*.exe").Any() ? AppInstallKind.Installed : AppInstallKind.Portable;
    }

    public static bool CanUpdate => InstallKind != AppInstallKind.SourceBuild;

    /// <summary>The latest release, when it is newer than this copy and has a package for it; otherwise null.</summary>
    /// <exception cref="HttpRequestException">GitHub could not be reached or answered with an error.</exception>
    public static async Task<AppRelease?> CheckAsync(CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var release = ParseRelease(json, InstallKind, CurrentVersion);
        Log.Info("Update check: latest {Latest}, running {Current}", release?.Version.ToString(3) ?? "(none newer)", CurrentVersion.ToString(3));
        return release;
    }

    /// <summary>Reads GitHub's release JSON; null unless it is newer than <paramref name="current"/> and has what <paramref name="kind"/> needs.</summary>
    internal static AppRelease? ParseRelease(string json, AppInstallKind kind, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

        var version = ParseTag(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
        if (version is null || version <= Normalize(current)) return null;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var assetName = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                if (!string.IsNullOrEmpty(assetName) && IsTrustedDownload(url)) assets.Add(new ReleaseAsset(assetName, url!, size));
            }
        }

        var checksums = assets.FirstOrDefault(a => a.Name.Equals(ChecksumsName, StringComparison.OrdinalIgnoreCase));
        var suffix = kind == AppInstallKind.Installed ? "-win-x64-Setup.exe" : "-win-x64-Portable.zip";
        var package = assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (checksums is null || package is null || kind == AppInstallKind.SourceBuild) return null;

        var name = root.TryGetProperty("name", out var rn) && rn.GetString() is { Length: > 0 } title ? title : $"TextTack {version.ToString(3)}";
        var page = root.TryGetProperty("html_url", out var h) && h.GetString() is { } html && IsTrustedDownload(html)
            ? html
            : ReleasesPage;
        return new AppRelease(version, name, page, package, checksums);
    }

    /// <summary>Only files on this repository's own GitHub pages are fetched.</summary>
    private static bool IsTrustedDownload(string? url) =>
        url is not null &&
        url.StartsWith("https://github.com/" + Repository + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"v1.7.0" or "1.7.0" to 1.7.0; null for anything else.</summary>
    internal static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>The hash SHA256SUMS.txt gives for <paramref name="fileName"/> ("hash  name" per line), or null.</summary>
    internal static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space <= 0) continue;
            var name = line[(space + 1)..].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return line[..space].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Thrown with a localization key when an update cannot go ahead for a reason the user should read.</summary>
    public sealed class UpdateException(string messageKey) : Exception(messageKey)
    {
        public string MessageKey { get; } = messageKey;
    }

    /// <summary>
    /// Downloads and checks the package, unpacks a portable one, and writes the script that will put
    /// it in place. Returns the script to run once the application is ready to exit.
    /// </summary>
    public static async Task<string> PrepareAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancel = default)
    {
        if (!CanUpdate) throw new UpdateException("S.Settings.UpdateSourceBuild");
        if (InstallKind == AppInstallKind.Portable && !IsWritable(AppDirectory))
            throw new UpdateException("S.Settings.UpdateNotWritable");

        var work = Path.Combine(Path.GetTempPath(), "TextTack-update", release.Version.ToString(3));
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var sums = await Http.GetStringAsync(release.Checksums.Url, cancel).ConfigureAwait(false);
        var expected = ExpectedHash(sums, release.Package.Name) ?? throw new UpdateException("S.Settings.UpdateHashMismatch");

        var package = Path.Combine(work, release.Package.Name);
        await DownloadAsync(release.Package, package, progress, cancel).ConfigureAwait(false);

        string actual;
        await using (var stream = File.OpenRead(package))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != expected)
        {
            Log.Warn("Update package hash mismatch: expected {Expected}, got {Actual}", expected, actual);
            File.Delete(package);
            throw new UpdateException("S.Settings.UpdateHashMismatch");
        }

        string script;
        if (InstallKind == AppInstallKind.Installed)
        {
            script = WriteScript(work, InstallerScript(package));
        }
        else
        {
            var staging = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(package, staging);
            var source = Directory.EnumerateFiles(staging, ExecutableName, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .OrderBy(d => d!.Length)
                .FirstOrDefault() ?? throw new UpdateException("S.Settings.UpdateNoPackage");
            if (Directory.Exists(Path.Combine(source, "profile-thai"))) throw new UpdateException("S.Settings.UpdateNoPackage");
            script = WriteScript(work, PortableScript(source));
        }

        Log.Info("Update {Version} prepared ({Kind}) in {Work}", release.Version.ToString(3), InstallKind, work);
        return script;
    }

    private static async Task DownloadAsync(ReleaseAsset asset, string path, IProgress<double>? progress, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>PowerShell single-quoted literal.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Header() => $$"""
        $ErrorActionPreference = 'Continue'
        $log = Join-Path $PSScriptRoot 'update.log'
        "Update started $(Get-Date -Format s)" | Out-File $log -Encoding utf8
        Wait-Process -Id {{Environment.ProcessId}} -Timeout 60 -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        $app = {{Quote(AppDirectory)}}

        """;

    private static string Footer() => """

        Start-Process -FilePath (Join-Path $app 'TextTack.exe') -WorkingDirectory $app
        "Restarted $(Get-Date -Format s)" | Out-File $log -Append -Encoding utf8
        """;

    /// <summary>Runs the newer Setup.exe silently over the same install (same AppId, same folder).</summary>
    private static string InstallerScript(string setup) => Header() + $$"""
        $p = Start-Process -FilePath {{Quote(setup)}} -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
        "Setup exit code $($p.ExitCode)" | Out-File $log -Append -Encoding utf8
        """ + Footer();

    /// <summary>Copies the unpacked files over this folder, leaving profile-thai alone.</summary>
    private static string PortableScript(string source) => Header() + $$"""
        & robocopy.exe {{Quote(source)}} $app /E /XD profile-thai /R:10 /W:1 /NFL /NDL /NP | Out-File $log -Append -Encoding utf8
        "Robocopy exit code $LASTEXITCODE" | Out-File $log -Append -Encoding utf8
        """ + Footer();

    private static string WriteScript(string work, string body)
    {
        var path = Path.Combine(work, "apply-update.ps1");
        // With a BOM, so Windows PowerShell 5.1 reads a non-ASCII folder name correctly.
        File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>Starts the script, which waits for this process to exit. The caller exits right after.</summary>
    public static void Launch(string script)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
            info.ArgumentList.Add(arg);
        Process.Start(info);
        Log.Info("Update script started: {Script}", script);
    }
}
