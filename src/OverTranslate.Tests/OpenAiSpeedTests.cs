using System.Net.Http;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What makes a hosted model answer sooner: Fast mode when asked for, and a slow request sent again.
/// Every server here is a fake one on this machine.
/// </summary>
public class OpenAiSpeedTests
{
    private const string Hosted = "https://api.example.test/v1";

    [Fact]
    public async Task FastMode_SendsServiceTierFast()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var provider = new OpenAiCompatibleProvider(http, () => new OpenAiCompatibleOptions(
            Hosted, "test-model", ServiceTier: OpenAiModelProfile.FastServiceTier));

        await provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "");

        Assert.Equal("fast", Assert.Single(server.Requests).Payload.GetProperty("service_tier").GetString());
    }

    [Fact]
    public async Task WithoutFastMode_SendsNoServiceTier()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var provider = new OpenAiCompatibleProvider(http, () => new OpenAiCompatibleOptions(Hosted, "test-model"));

        await provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "");

        Assert.False(Assert.Single(server.Requests).Payload.TryGetProperty("service_tier", out _));
    }

    [Fact]
    public void FastMode_IsReadFromTheSelectedProfile()
    {
        var settings = new AppSettings();
        var profile = new OpenAiModelProfile { Id = "p", Name = "luna", Model = "m", FastMode = true };
        settings.OpenAi.Profiles.Add(profile);
        settings.OpenAi.SelectedProfileId = "p";

        Assert.Equal("fast", OpenAiCompatibleProvider.FromSettings(settings).ServiceTier);

        profile.FastMode = false;
        Assert.Null(OpenAiCompatibleProvider.FromSettings(settings).ServiceTier);
    }

    /// <summary>
    /// A hosted server that sits on the first request is sent the same request again, and the copy's
    /// answer is used.
    /// </summary>
    [Fact]
    public async Task SlowRequestToAHostedServer_IsSentAgainAndTheCopyAnswers()
    {
        var server = new OpenAiFakeServer();
        var handler = new FirstNeverAnswers(server);
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(http, () => new OpenAiCompatibleOptions(
            Hosted, "test-model", ReasoningEffort: OpenAiModelProfile.ThinkingOffEffort, TimeoutSeconds: 30));

        var started = DateTime.UtcNow;
        var (blocks, _) = await provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "");

        Assert.Equal(2, handler.Calls);
        Assert.Equal("translated:hello", Assert.Single(blocks).TranslatedText);
        Assert.False(blocks[0].Untranslated);
        // Sent again after the wait, long before the 30-second timeout.
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15));
        Assert.True(handler.FirstCancelled.Task.Wait(TimeSpan.FromSeconds(5)), "the slow copy is cancelled");
    }

    [Fact]
    public async Task QuickAnswer_IsNotSentTwice()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var provider = new OpenAiCompatibleProvider(http, () => new OpenAiCompatibleOptions(
            Hosted, "test-model", ReasoningEffort: OpenAiModelProfile.ThinkingOffEffort));

        await provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "");

        Assert.Equal(1, server.Sent);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions", true)]
    [InlineData("https://api.deepseek.com/chat/completions", true)]
    [InlineData("http://8.8.8.8:8000/v1/chat/completions", true)]
    [InlineData("http://localhost:11434/v1/chat/completions", false)]
    [InlineData("http://127.0.0.1:1234/v1/chat/completions", false)]
    [InlineData("http://[::1]:1234/v1/chat/completions", false)]
    [InlineData("http://192.168.1.20:1234/v1/chat/completions", false)]
    [InlineData("http://10.0.0.5:1234/v1/chat/completions", false)]
    [InlineData("http://172.20.0.5:1234/v1/chat/completions", false)]
    [InlineData("http://gpu-box:1234/v1/chat/completions", false)]
    [InlineData("http://gpu-box.local:1234/v1/chat/completions", false)]
    public void OnlyServersAcrossTheNetworkAreSentACopy(string endpoint, bool remote)
    {
        Assert.Equal(remote, OpenAiChatTranslator.IsRemote(new Uri(endpoint)));
    }

    [Fact]
    public void TheWaitGrowsWithTheAnswer()
    {
        var oneLine = OpenAiChatTranslator.HedgeDelay(400);
        var screen = OpenAiChatTranslator.HedgeDelay(4800);

        Assert.InRange(oneLine, TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(5));
        Assert.True(screen > oneLine + TimeSpan.FromSeconds(5));
    }

    /// <summary>Never answers its first request; passes every later one to the fake server.</summary>
    private sealed class FirstNeverAnswers(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCancelled.TrySetResult();
                    throw;
                }
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
