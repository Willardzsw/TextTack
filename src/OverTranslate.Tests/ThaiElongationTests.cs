using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Translation;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

// Stretched Thai (ไดดดดด้) and the answer limit that keeps a model repeating itself from hanging a
// translation until the timeout.
public class ThaiElongationTests
{
    [Theory]
    [InlineData("ไดดดดดดดดดดดดดดดดดดดดดดดดดดดดดด้", "ได้")]
    [InlineData("ไดดดดด้", "ได้")]
    [InlineData("มากกกกกก", "มาก")]
    [InlineData("ดีมากกกก ขอบคุณนะคะะะ", "ดีมาก ขอบคุณนะคะะะ")]
    public void Collapse_BringsStretchedConsonantsBackToOne(string text, string expected) =>
        Assert.Equal(expected, ThaiElongation.Collapse(text));

    [Theory]
    [InlineData("ได้")]
    [InlineData("คคน")]                // two in a row is left as it is
    [InlineData("555555")]             // Thai laughter, digits
    [InlineData("sooooo")]             // other scripts
    [InlineData("วันนี้อากาศดีมาก")]
    [InlineData("")]
    public void Collapse_LeavesEverythingElseTheSameInstance(string text) =>
        Assert.Same(text, ThaiElongation.Collapse(text));

    [Fact]
    public void Stretching_IsSentCollapsedAndShownAsWritten()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("ไดดดดดด้", new Rect(1, 2, 3, 4)),
            new("hello", new Rect()),
        };

        var (sent, stretched) = TranslationService.CollapseStretching(blocks);
        Assert.Equal(["ได้", "hello"], sent.Select(block => block.Text));
        Assert.Equal(new Rect(1, 2, 3, 4), sent[0].Bounds);
        Assert.Same(blocks[1], sent[1]);
        Assert.NotNull(stretched);

        var restored = TranslationService.RestoreStretching(
        [
            new TranslatedBlock("ได้", "可以", new Rect(1, 2, 3, 4)),
            new TranslatedBlock("hello", "你好", new Rect()),
        ], stretched!);
        Assert.Equal(["ไดดดดดด้", "hello"], restored.Select(block => block.OriginalText));
        Assert.Equal(["可以", "你好"], restored.Select(block => block.TranslatedText));

        // Untranslated: the original stands in as written, not the collapsed form.
        var untranslated = TranslationService.RestoreStretching(
            [new TranslatedBlock("ได้", "ได้", new Rect()) { Untranslated = true }], stretched!);
        Assert.Equal("ไดดดดดด้", untranslated[0].TranslatedText);
    }

    [Fact]
    public void NothingStretched_NothingToRestore()
    {
        var (_, stretched) = TranslationService.CollapseStretching([new OcrTextBlock("ได้", new Rect())]);
        Assert.Null(stretched);
    }

    [Fact]
    public void AnswerLimit_OnlyWithThinkingOff()
    {
        Assert.Equal(256 + 4 * 3 + 16, OpenAiChatTranslator.AnswerLimit(Request("m", "none"), ["ได้"]));
        Assert.Null(OpenAiChatTranslator.AnswerLimit(Request("m", null), ["ได้"]));
        Assert.Null(OpenAiChatTranslator.AnswerLimit(Request("m", "low"), ["ได้"]));
    }

    [Fact]
    public async Task ThinkingOff_SendsTheLimitAsMaxCompletionTokens()
    {
        var server = new Scripted((_, _) => Answer("可以"));
        var outcomes = await new OpenAiChatTranslator(new HttpClient(server))
            .TranslateAsync(["ได้"], Request("limit-sent", "none"));

        Assert.Equal("可以", outcomes[0].Translation);
        var payload = Assert.Single(server.Payloads);
        Assert.Equal(256 + 4 * 3 + 16, payload.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(payload.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task ThinkingOn_SendsNoLimit()
    {
        var server = new Scripted((_, _) => Answer("可以"));
        await new OpenAiChatTranslator(new HttpClient(server)).TranslateAsync(["ได้"], Request("limit-none", null));

        Assert.False(Assert.Single(server.Payloads).TryGetProperty("max_completion_tokens", out _));
    }

    /// <summary>An answer cut at the limit is a model repeating itself, not a translation.</summary>
    [Fact]
    public async Task AnswerCutAtTheLimit_IsNotTakenForATranslation()
    {
        var server = new Scripted((_, _) => Answer("哒哒哒哒哒哒哒哒", "length"));

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(new HttpClient(server)).TranslateAsync(["ไดดดด้"], Request("runaway", "none")));

        Assert.Equal(OpenAiChatFailure.Runaway, error.Failure);
    }

    /// <summary>A server that refuses the field is asked again without it, and never sent it again.</summary>
    [Fact]
    public async Task ServerRefusingTheLimit_IsAskedAgainWithoutIt()
    {
        var server = new Scripted((payload, _) => payload.TryGetProperty("max_completion_tokens", out JsonElement _)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }
            : Answer("可以"));
        var translator = new OpenAiChatTranslator(new HttpClient(server));
        var request = Request("refuses-limit", "none");

        Assert.Equal("可以", (await translator.TranslateAsync(["ได้"], request))[0].Translation);
        Assert.Equal("可以", (await translator.TranslateAsync(["ได้"], request))[0].Translation);

        Assert.Equal([true, false, false], server.Payloads.Select(p => p.TryGetProperty("max_completion_tokens", out _)));
    }

    private static OpenAiChatRequest Request(string model, string? effort) =>
        new(new Uri("http://localhost:1234/v1/chat/completions"), model, "",
            OverTranslate.Services.Providers.OpenAiCompatibleProvider.BuildPrompt("TH", "ZH", "Translate:\n\n{text}"),
            ReasoningEffort: effort);

    private static HttpResponseMessage Answer(string content, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } },
        })),
    };

    private sealed class Scripted(Func<JsonElement, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<JsonElement> Payloads { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement.Clone();
            Payloads.Enqueue(payload);
            return answer(payload, Payloads.Count);
        }
    }
}
