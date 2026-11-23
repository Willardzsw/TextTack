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
/// The DeepSeek service against a fake server: what it sends, and how a refusal reaches the user.
/// No request here leaves the machine.
/// </summary>
public class DeepSeekProviderTests
{
    private static Func<string, string?> BatchEcho => text => string.Join("\n",
        Regex.Matches(text, @"(?m)^<(\d+)>(.*?)</\1>")
            .Select(row => $"<{row.Groups[1].Value}>譯:{row.Groups[2].Value}</{row.Groups[1].Value}>"));

    private static AppSettings Settings(string key = "synthetic-key", string model = "") =>
        new() { DeepSeekApiKey = key, DeepSeekModel = model };

    [Fact]
    public async Task TranslateAsync_SendsTheScreenToDeepSeekWithThinkingOff()
    {
        var server = new OpenAiFakeServer { Answer = BatchEcho };
        using var http = new HttpClient(server);
        var provider = new DeepSeekProvider(http, () => Settings());

        var (blocks, _) = await provider.TranslateAsync(
            [new OcrTextBlock("hello there", new Rect()), new OcrTextBlock("good night", new Rect())],
            "EN", "ZH-HANS", "ignored-deepl-key");

        // One request for the screen, to the documented address with no /v1.
        var request = Assert.Single(server.Requests);
        Assert.Equal("https://api.deepseek.com/chat/completions", request.Url);
        Assert.Equal("Bearer synthetic-key", request.Authorization);

        var payload = request.Payload;
        Assert.Equal("deepseek-flash", payload.GetProperty("model").GetString());
        Assert.Equal("none", payload.GetProperty("reasoning_effort").GetString());
        Assert.Equal(1.0, payload.GetProperty("temperature").GetDouble());
        Assert.False(payload.TryGetProperty("seed", out _));
        Assert.False(payload.TryGetProperty("top_p", out _));

        Assert.Equal(["譯:hello there", "譯:good night"], blocks.Select(block => block.TranslatedText));
    }

    [Theory]
    [InlineData("", "deepseek-flash")]
    [InlineData("deepseek-v4-pro", "deepseek-v4-pro")]
    [InlineData(" DeepSeek-V4-Pro ", "deepseek-v4-pro")]
    [InlineData("deepseek-chat", "deepseek-flash")]
    public void ModelFor_OffersFlashAndPro_AndReadsAnythingElseAsFlash(string stored, string expected)
    {
        Assert.Equal(expected, DeepSeekProvider.ModelFor(stored));
        Assert.Equal(expected, DeepSeekProvider.OptionsFor(Settings(model: stored)).Model);
    }

    [Fact]
    public async Task TranslateAsync_WithNoKey_SaysSoAndSendsNothing()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var provider = new DeepSeekProvider(http, () => Settings(key: "  "));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANS", ""));

        Assert.Equal(LocalizationService.Get("S.Error.DeepSeekNoKey"), error.Message);
        Assert.Equal(0, server.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "S.Error.DeepSeekUnauthorized")]
    [InlineData(HttpStatusCode.PaymentRequired, "S.Error.DeepSeekNoBalance")]
    [InlineData(HttpStatusCode.TooManyRequests, "S.Error.DeepSeekRateLimited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "S.Error.DeepSeekBusy")]
    public async Task TranslateAsync_NamesTheDocumentedRefusals(HttpStatusCode status, string key)
    {
        using var http = new HttpClient(new Refusing(status, """{"error":{"message":"synthetic"}}"""));
        var provider = new DeepSeekProvider(http, () => Settings());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANS", ""));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(LocalizationService.Get(key), error.Message);
    }

    [Fact]
    public async Task TranslateAsync_OtherRefusalsKeepTheServersWordsUnderDeepSeeksName()
    {
        using var http = new HttpClient(new Refusing(HttpStatusCode.UnprocessableEntity,
            """{"error":{"message":"synthetic parameter error"}}"""));
        var provider = new DeepSeekProvider(http, () => Settings());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANS", ""));

        Assert.Contains("DeepSeek", error.Message);
        Assert.Contains("422", error.Message);
        Assert.Contains("synthetic parameter error", error.Message);
        Assert.DoesNotContain("OpenAI", error.Message);
    }

    [Fact]
    public async Task QueryBalanceAsync_ReadsTheBalanceWithTheKey()
    {
        var handler = new Refusing(HttpStatusCode.OK, """
            {"is_available":true,"balance_infos":[
              {"currency":"CNY","total_balance":"10.00","granted_balance":"0.00","topped_up_balance":"10.00"}]}
            """);
        using var http = new HttpClient(handler);
        var provider = new DeepSeekProvider(http, () => Settings());

        var info = await provider.QueryBalanceAsync();

        Assert.Equal("https://api.deepseek.com/user/balance", handler.LastUrl);
        Assert.Equal("Bearer synthetic-key", handler.LastAuthorization);
        Assert.True(info.Available);
        Assert.Equal("¥10.00", DeepSeekProvider.Display(Assert.Single(info.Balances)));
    }

    [Fact]
    public async Task QueryBalanceAsync_WithAWrongKey_SaysTheKeyIsWrong()
    {
        using var http = new HttpClient(new Refusing(HttpStatusCode.Unauthorized, "{}"));
        var provider = new DeepSeekProvider(http, () => Settings());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.QueryBalanceAsync());

        Assert.Equal(LocalizationService.Get("S.Error.DeepSeekUnauthorized"), error.Message);
    }

    [Theory]
    [InlineData("""{"is_available":false,"balance_infos":[{"currency":"USD","total_balance":"0.5"}]}""", false, "$0.50")]
    [InlineData("""{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00"}]}""", true, "¥110.00")]
    public void ParseBalance_ReadsAvailabilityAndAmount(string json, bool available, string shown)
    {
        var info = DeepSeekProvider.ParseBalance(json)!;

        Assert.Equal(available, info.Available);
        Assert.Equal(shown, DeepSeekProvider.Display(Assert.Single(info.Balances)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void ParseBalance_RejectsWhatIsNotABalance(string json)
    {
        Assert.Null(DeepSeekProvider.ParseBalance(json));
    }

    [Fact]
    public void DeepSeek_IsOfferedAsALanguageModelWithoutTheDeepLKey()
    {
        var item = LanguageData.Providers.Single(item => item.Provider == TranslationProvider.DeepSeek);

        Assert.Equal("S.Provider.DeepSeek", item.DisplayKey);
        Assert.False(item.RequiresApiKey);
        Assert.True(LanguageData.IsLanguageModel(TranslationProvider.DeepSeek));
        Assert.True(LanguageData.IsLanguageModel(TranslationProvider.OpenAI));
        Assert.False(LanguageData.IsLanguageModel(TranslationProvider.DeepL));
    }

    private sealed class Refusing(HttpStatusCode status, string body) : HttpMessageHandler
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
