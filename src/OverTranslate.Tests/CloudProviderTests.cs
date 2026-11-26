using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 云翻译 against a fake relay: what it sends, and how each of the relay's refusals reaches the
/// user. No request here leaves the machine, and every TT code is synthetic.
/// </summary>
public class CloudProviderTests
{
    private static Func<string, string?> BatchEcho => text => string.Join("\n",
        Regex.Matches(text, @"(?m)^<(\d+)>(.*?)</\1>")
            .Select(row => $"<{row.Groups[1].Value}>譯:{row.Groups[2].Value}</{row.Groups[1].Value}>"));

    private static AppSettings Settings(string code = "tt-synthetic-code", string model = "") =>
        new() { CloudApiKey = code, CloudModel = model };

    [Fact]
    public async Task TranslateAsync_SendsTheScreenToTheRelayWithTheCode()
    {
        var server = new OpenAiFakeServer { Answer = BatchEcho };
        using var http = new HttpClient(server);
        var provider = new CloudProvider(http, () => Settings());

        var (blocks, _) = await provider.TranslateAsync(
            [new OcrTextBlock("hello there", new Rect()), new OcrTextBlock("good night", new Rect())],
            "EN", "ZH-HANS", "ignored-deepl-key");

        var request = Assert.Single(server.Requests);
        Assert.Equal("https://tt.allplusthailand.top/v1/chat/completions", request.Url);
        Assert.Equal("Bearer tt-synthetic-code", request.Authorization);

        var payload = request.Payload;
        Assert.Equal("gpt-6-luna", payload.GetProperty("model").GetString());
        Assert.Equal("none", payload.GetProperty("reasoning_effort").GetString());
        Assert.False(payload.TryGetProperty("service_tier", out _));
        Assert.False(payload.TryGetProperty("seed", out _));

        Assert.Equal(["譯:hello there", "譯:good night"], blocks.Select(block => block.TranslatedText));
    }

    [Theory]
    [InlineData("", "gpt-6-luna")]
    [InlineData("deepseek-flash", "deepseek-flash")]
    [InlineData(" DeepSeek-Flash ", "deepseek-flash")]
    [InlineData("deepseek-v4-pro", "gpt-6-luna")]
    public void ModelFor_OffersLunaAndDeepSeek_AndReadsAnythingElseAsLuna(string stored, string expected)
    {
        Assert.Equal(expected, CloudProvider.ModelFor(stored));
        Assert.Equal(expected, CloudProvider.OptionsFor(Settings(model: stored)).Model);
    }

    [Fact]
    public async Task TranslateAsync_WithNoCode_SaysSoAndSendsNothing()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var provider = new CloudProvider(http, () => Settings(code: "  "));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANS", ""));

        Assert.Equal(LocalizationService.Get("S.Error.CloudNoCode"), error.Message);
        Assert.Equal(0, server.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "S.Error.CloudBadCode")]
    [InlineData(HttpStatusCode.Forbidden, "S.Error.CloudCodeDisabled")]
    [InlineData(HttpStatusCode.PaymentRequired, "S.Error.CloudQuotaUsed")]
    [InlineData(HttpStatusCode.TooManyRequests, "S.Error.CloudRateLimited")]
    [InlineData(HttpStatusCode.BadGateway, "S.Error.CloudProviderProblem")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "S.Error.CloudModelUnavailable")]
    public async Task TranslateAsync_NamesEachOfTheRelaysRefusals(HttpStatusCode status, string key)
    {
        using var http = new HttpClient(new Answering(status, """{"error":{"message":"synthetic"}}"""));
        var provider = new CloudProvider(http, () => Settings());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANS", ""));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(LocalizationService.Get(key), error.Message);
    }

    [Fact]
    public async Task TranslateAsync_OtherRefusalsKeepTheServersWordsUnderTheCloudName()
    {
        using var http = new HttpClient(new Answering(HttpStatusCode.UnprocessableEntity,
            """{"error":{"message":"synthetic parameter error"}}"""));
        var provider = new CloudProvider(http, () => Settings());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANS", ""));

        Assert.Contains("422", error.Message);
        Assert.Contains("synthetic parameter error", error.Message);
        Assert.DoesNotContain("OpenAI", error.Message);
    }

    [Fact]
    public async Task QueryQuotaAsync_ReadsTodaysUseWithTheCode()
    {
        var handler = new Answering(HttpStatusCode.OK, """
            {"date":"2026-11-26","used_cny":0.1234,"daily_cny":1.0,"remaining_cny":0.8766,"requests":12,
             "models":["gpt-6-luna","deepseek-flash"]}
            """);
        using var http = new HttpClient(handler);
        var provider = new CloudProvider(http, () => Settings());

        var quota = await provider.QueryQuotaAsync();

        Assert.Equal("https://tt.allplusthailand.top/v1/quota", handler.LastUrl);
        Assert.Equal("Bearer tt-synthetic-code", handler.LastAuthorization);
        Assert.Equal(12, quota.Requests);
        Assert.Equal("¥0.12", CloudProvider.Yuan(quota.Used));
        Assert.Equal("¥1.00", CloudProvider.Yuan(quota.Daily));
        Assert.Equal("¥0.88", CloudProvider.Yuan(quota.Remaining));
    }

    [Fact]
    public async Task QueryQuotaAsync_WithAWrongCode_SaysTheCodeIsWrong()
    {
        using var http = new HttpClient(new Answering(HttpStatusCode.Unauthorized, "{}"));
        var provider = new CloudProvider(http, () => Settings());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.QueryQuotaAsync());

        Assert.Equal(LocalizationService.Get("S.Error.CloudBadCode"), error.Message);
    }

    [Fact]
    public async Task QueryQuotaAsync_WithNoCode_SendsNothing()
    {
        var handler = new Answering(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var provider = new CloudProvider(http, () => Settings(code: ""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.QueryQuotaAsync());

        Assert.Equal(LocalizationService.Get("S.Error.CloudNoCode"), error.Message);
        Assert.Null(handler.LastUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"used_cny":"0.1","daily_cny":1}""")]
    [InlineData("""{"daily_cny":1}""")]
    public void ParseQuota_RejectsWhatIsNotAnAllowance(string json)
    {
        Assert.Null(CloudProvider.ParseQuota(json));
    }

    [Fact]
    public void ParseQuota_WorksOutWhatIsLeftWhenTheServerDoesNotSay()
    {
        var quota = CloudProvider.ParseQuota("""{"used_cny":1.5,"daily_cny":1.0}""")!;

        Assert.Equal(0m, quota.Remaining);
        Assert.Equal(0, quota.Requests);
    }

    [Fact]
    public void Cloud_IsOfferedAsALanguageModelAfterDeepSeek()
    {
        var providers = LanguageData.Providers.Select(item => item.Provider).ToList();
        var item = LanguageData.Providers.Single(item => item.Provider == TranslationProvider.Cloud);

        Assert.Equal("S.Provider.Cloud", item.DisplayKey);
        Assert.False(item.RequiresApiKey);
        Assert.True(LanguageData.IsLanguageModel(TranslationProvider.Cloud));
        Assert.Equal(providers.IndexOf(TranslationProvider.DeepSeek) + 1, providers.IndexOf(TranslationProvider.Cloud));
    }

    [Fact]
    public void TheCode_IsStoredEncryptedLikeAKey()
    {
        var json = SettingsService.Serialize(Settings(code: "tt-synthetic-code-0123456789"));

        Assert.DoesNotContain("synthetic", json);
        Assert.Equal("tt-synthetic-code-0123456789", SettingsService.Parse(json).CloudApiKey);
    }

    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.AbsoluteUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
