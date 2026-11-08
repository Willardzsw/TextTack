using System.IO;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class AppUpdateServiceTests
{
    private const string Base = "https://github.com/Willardzsw/TextTack/releases/download/v1.7.1/";

    private static string Release(string tag, bool prerelease = false, string host = Base) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "TextTack {{tag.TrimStart('v')}}",
          "html_url": "https://github.com/Willardzsw/TextTack/releases/tag/{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "SHA256SUMS.txt", "browser_download_url": "{{host}}SHA256SUMS.txt", "size": 300 },
            { "name": "TextTack-1.7.1-win-x64-Portable.zip", "browser_download_url": "{{host}}TextTack-1.7.1-win-x64-Portable.zip", "size": 1000 },
            { "name": "TextTack-1.7.1-win-x64-Setup.exe", "browser_download_url": "{{host}}TextTack-1.7.1-win-x64-Setup.exe", "size": 900 }
          ]
        }
        """;

    [Fact]
    public void NewerRelease_PicksThePackageForHowThisCopyWasInstalled()
    {
        var portable = AppUpdateService.ParseRelease(Release("v1.7.1"), AppInstallKind.Portable, new Version(1, 7, 0));
        var installed = AppUpdateService.ParseRelease(Release("v1.7.1"), AppInstallKind.Installed, new Version(1, 7, 0));

        Assert.NotNull(portable);
        Assert.Equal(new Version(1, 7, 1), portable!.Version);
        Assert.EndsWith("Portable.zip", portable.Package.Name);
        Assert.Equal("SHA256SUMS.txt", portable.Checksums.Name);
        Assert.EndsWith("Setup.exe", installed!.Package.Name);
    }

    [Fact]
    public void SameOrOlder_Prerelease_SourceBuild_OrForeignHost_GiveNothing()
    {
        Assert.Null(AppUpdateService.ParseRelease(Release("v1.7.0"), AppInstallKind.Portable, new Version(1, 7, 0)));
        Assert.Null(AppUpdateService.ParseRelease(Release("v1.6.9"), AppInstallKind.Portable, new Version(1, 7, 0)));
        Assert.Null(AppUpdateService.ParseRelease(Release("v1.7.1", prerelease: true), AppInstallKind.Portable, new Version(1, 7, 0)));
        Assert.Null(AppUpdateService.ParseRelease(Release("v1.7.1"), AppInstallKind.SourceBuild, new Version(1, 7, 0)));
        Assert.Null(AppUpdateService.ParseRelease(Release("v1.7.1", host: "https://example.com/"), AppInstallKind.Portable, new Version(1, 7, 0)));
    }

    [Theory]
    [InlineData("v1.7.1", "1.7.1")]
    [InlineData("1.10.0", "1.10.0")]
    [InlineData("v2.0", "2.0.0")]
    public void Tags_AreReadAsVersions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), AppUpdateService.ParseTag(tag));

    [Fact]
    public void ChecksumFile_GivesTheHashForTheNamedFile()
    {
        const string sums = "ABCDEF01  TextTack-1.7.1-win-x64-Portable.zip\r\n123456  TextTack-1.7.1-win-x64-Setup.exe\r\n";
        Assert.Equal("abcdef01", AppUpdateService.ExpectedHash(sums, "TextTack-1.7.1-win-x64-Portable.zip"));
        Assert.Equal("123456", AppUpdateService.ExpectedHash(sums, "TextTack-1.7.1-win-x64-Setup.exe"));
        Assert.Null(AppUpdateService.ExpectedHash(sums, "other.zip"));
    }

    [Fact]
    public void InstallKind_SourceBuild_Installed_Portable()
    {
        var root = Path.Combine(Path.GetTempPath(), "tt-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(root, "src", "OverTranslate");
            var bin = Path.Combine(project, "bin", "Release", "net8.0-windows10.0.26100.0", "win-x64");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(project, "OverTranslate.csproj"), "");
            Assert.Equal(AppInstallKind.SourceBuild, AppUpdateService.DetectInstallKind(bin));

            var installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "unins000.exe"), "");
            Assert.Equal(AppInstallKind.Installed, AppUpdateService.DetectInstallKind(installed));

            var portable = Path.Combine(root, "portable");
            Directory.CreateDirectory(portable);
            Assert.Equal(AppInstallKind.Portable, AppUpdateService.DetectInstallKind(portable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StaticState_InitializesWithoutThrowing()
    {
        // The settings page reads these on open; a type initializer that throws takes the page down.
        Assert.True(AppUpdateService.CurrentVersion >= new Version(1, 0, 0));
        _ = AppUpdateService.InstallKind;
        _ = AppUpdateService.CanUpdate;
    }

    [Fact]
    public void AutoCheck_IsOnByDefault_AndRoundTrips()
    {
        Assert.True(new AppSettings().AutoCheckUpdates);
        var back = SettingsService.Parse(SettingsService.Serialize(new AppSettings { AutoCheckUpdates = false }));
        Assert.False(back.AutoCheckUpdates);
    }
}
