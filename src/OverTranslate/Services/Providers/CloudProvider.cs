using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OverTranslate.Models;
using OverTranslate.Translation.OpenAi;

namespace OverTranslate.Services.Providers;

/// <summary>What one TT code has used today, as the relay's quota endpoint states it.</summary>
/// <param name="Used">Spent today, in yuan.</param>
/// <param name="Daily">The day's allowance, in yuan.</param>
/// <param name="Remaining">What is left of it.</param>
/// <param name="Requests">Translations sent today.</param>
public sealed record CloudQuota(decimal Used, decimal Daily, decimal Remaining, int Requests);

/// <summary>
/// 「云翻译」: translation through the TextTack relay (tools/relay), which holds the OpenAI and
/// DeepSeek keys itself. The user types only the TT code they were given.
/// </summary>
/// <remarks>
/// <para>The relay speaks the OpenAI chat-completions request, so what goes over the wire is the one
/// <see cref="OpenAiCompatibleProvider"/> sends. Thinking off, no fast tier and the answer limit are
/// enforced by the relay as well; they are sent here so the request means what it does.</para>
///
/// <para>The relay's refusals each have their own status — see tt_relay.py — so they are put in the
/// user's own language here rather than shown in the relay's Chinese.</para>
/// </remarks>
public sealed class CloudProvider : ITranslationProvider
{
    /// <summary>The relay. Fixed: a user has nothing to set but the code.</summary>
    internal const string BaseUrl = "https://tt.allplusthailand.top";

    internal const string Endpoint = BaseUrl + "/v1/chat/completions";

    internal const string QuotaEndpoint = BaseUrl + "/v1/quota";

    /// <summary>GPT-6 Luna through the relay's OpenAI key: the default.</summary>
    internal const string LunaModel = "gpt-6-luna";

    /// <summary>DeepSeek Flash through the relay's DeepSeek key.</summary>
    internal const string DeepSeekModel = "deepseek-flash";

    /// <summary>The models offered, in the order the picker lists them.</summary>
    internal static readonly IReadOnlyList<string> Models = [LunaModel, DeepSeekModel];

    /// <summary>How long one request may take; the relay itself gives the upstream 120 seconds.</summary>
    internal const int TimeoutSeconds = 60;

    private static readonly HttpClient DefaultHttp = OpenAiChatTranslator.CreateHttpClient();

    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;
    private readonly OpenAiCompatibleProvider _chat;

    public CloudProvider(HttpClient? http = null, Func<AppSettings>? settings = null)
    {
        _http = http ?? DefaultHttp;
        _settings = settings ?? (() => SettingsService.Instance.Current);
        _chat = new OpenAiCompatibleProvider(_http, () => OptionsFor(_settings()), "Cloud ");
    }

    public string Name => "Cloud";

    // The TT code is checked when translating, in the words below.
    public bool RequiresApiKey => false;

    /// <summary>The model a stored choice stands for: one this build offers, or Luna.</summary>
    internal static string ModelFor(string? stored) =>
        Models.FirstOrDefault(model => string.Equals(model, stored?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? LunaModel;

    internal static OpenAiCompatibleOptions OptionsFor(AppSettings settings) => new(
        Endpoint,
        ModelFor(settings.CloudModel),
        settings.CloudApiKey.Trim(),
        Template: "",
        SendTemperature: false,
        SendTopP: false,
        SendSeed: false,
        ReasoningEffort: OpenAiModelProfile.ThinkingOffEffort,
        TimeoutSeconds: TimeoutSeconds,
        Batch: true);

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default, TranslationContext? context = null)
    {
        if (blocks.Count == 0) return ([], "");

        if (_settings().CloudApiKey.Trim().Length == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.CloudNoCode"));

        try
        {
            return await _chat.TranslateAsync(blocks, sourceLang, targetLang, apiKey, cancellationToken, context);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            var fallback = ex.InnerException is OpenAiChatException chat
                ? LocalizationService.Format("S.Error.CloudHttp", (int)status, chat.ServerMessage switch
                {
                    null => LocalizationService.Get("S.Error.UnknownError"),
                    "" => LocalizationService.Get("S.Error.NoErrorContent"),
                    var message => message,
                })
                : ex.Message;
            throw new HttpRequestException(Explain(status, fallback), ex, status);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(LocalizationService.Format("S.Error.CloudTimeout", TimeoutSeconds), ex);
        }
    }

    /// <summary>What each of the relay's refusals means, or the server's own words for the rest.</summary>
    internal static string Explain(HttpStatusCode status, string fallback) => (int)status switch
    {
        401 => LocalizationService.Get("S.Error.CloudBadCode"),
        403 => LocalizationService.Get("S.Error.CloudCodeDisabled"),
        402 => LocalizationService.Get("S.Error.CloudQuotaUsed"),
        429 => LocalizationService.Get("S.Error.CloudRateLimited"),
        502 => LocalizationService.Get("S.Error.CloudProviderProblem"),
        503 => LocalizationService.Get("S.Error.CloudModelUnavailable"),
        504 => LocalizationService.Format("S.Error.CloudTimeout", TimeoutSeconds),
        _ => fallback,
    };

    /// <summary>Asks the relay what this TT code has used today.</summary>
    /// <exception cref="InvalidOperationException">No code, or an answer that cannot be read — in the user's words.</exception>
    /// <exception cref="HttpRequestException">Refused, in the user's words.</exception>
    public async Task<CloudQuota> QueryQuotaAsync(CancellationToken cancellationToken = default)
    {
        var code = _settings().CloudApiKey.Trim();
        if (code.Length == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.CloudNoCode"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get, QuotaEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", code);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(LocalizationService.Format("S.Error.CloudTimeout", 15), ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    Explain(response.StatusCode, LocalizationService.Format(
                        "S.Error.CloudHttp", (int)response.StatusCode,
                        body.Trim().Length > 0 ? body.Trim() : LocalizationService.Get("S.Error.NoErrorContent"))),
                    null, response.StatusCode);

            return ParseQuota(body)
                ?? throw new InvalidOperationException(LocalizationService.Get("S.Error.CloudQuotaUnreadable"));
        }
    }

    /// <summary>The quota endpoint's answer, or null when it is not one.</summary>
    internal static CloudQuota? ParseQuota(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("used_cny", out var used) || used.ValueKind != JsonValueKind.Number ||
                !root.TryGetProperty("daily_cny", out var daily) || daily.ValueKind != JsonValueKind.Number)
                return null;

            var remaining = root.TryGetProperty("remaining_cny", out var r) && r.ValueKind == JsonValueKind.Number
                ? r.GetDecimal()
                : Math.Max(0, daily.GetDecimal() - used.GetDecimal());
            var requests = root.TryGetProperty("requests", out var n) && n.TryGetInt32(out var count) ? count : 0;
            return new CloudQuota(used.GetDecimal(), daily.GetDecimal(), remaining, requests);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>An amount as the settings panel shows it: ¥0.12.</summary>
    internal static string Yuan(decimal amount) =>
        "¥" + amount.ToString("0.00", CultureInfo.InvariantCulture);
}
