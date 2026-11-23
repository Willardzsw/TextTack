using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OverTranslate.Models;
using OverTranslate.Translation.OpenAi;

namespace OverTranslate.Services.Providers;

/// <summary>One currency of a DeepSeek account's balance, as the balance endpoint states it.</summary>
/// <param name="Currency">CNY or USD.</param>
/// <param name="Total">What is left, gift and top-up together, as the server wrote the number.</param>
public sealed record DeepSeekBalance(string Currency, string Total);

/// <summary>What the balance endpoint answered.</summary>
/// <param name="Available">False when the account cannot be called — the balance has run out.</param>
public sealed record DeepSeekBalanceInfo(bool Available, IReadOnlyList<DeepSeekBalance> Balances);

/// <summary>
/// 「DeepSeek」: the hosted DeepSeek API, sent through the OpenAI-compatible request with everything
/// the user would otherwise have to know already chosen.
/// </summary>
/// <remarks>
/// <para>A service of its own rather than a preset of 「OpenAI 相容」, so the address cannot be
/// mistyped and the key and model are kept apart from whatever local server that one points at.
/// What goes over the wire is the same request, read the same way — see
/// <see cref="OpenAiCompatibleProvider"/>.</para>
///
/// <para>Fixed per DeepSeek's API documentation (api-docs.deepseek.com, read 2026-11-23):
/// the address has no <c>/v1</c>; thinking is on by default and <c>reasoning_effort: "none"</c>
/// turns it off, which a translation wants for speed and cost; <c>seed</c> is not a documented
/// parameter, so it is not sent; temperature only counts with thinking off and defaults to 1.0,
/// which is what is sent; top_p is ignored by the Flash model with thinking off, so it is not
/// sent either. A screen goes as one request — the account allows 2500 at once, so there is
/// nothing to save by sending a block at a time.</para>
/// </remarks>
public sealed class DeepSeekProvider : ITranslationProvider
{
    /// <summary>The chat endpoint, in full so <see cref="OpenAiCompatibleProvider.BuildEndpoint"/> adds no <c>/v1</c>.</summary>
    internal const string Endpoint = "https://api.deepseek.com/chat/completions";

    /// <summary>The account balance endpoint.</summary>
    internal const string BalanceEndpoint = "https://api.deepseek.com/user/balance";

    /// <summary>DeepSeek-V4.1-Flash: the default, cheap and quick.</summary>
    internal const string FlashModel = "deepseek-flash";

    /// <summary>DeepSeek-V4-Pro: stronger, at several times the price.</summary>
    internal const string ProModel = "deepseek-v4-pro";

    /// <summary>The models offered, in the order the picker lists them.</summary>
    internal static readonly IReadOnlyList<string> Models = [FlashModel, ProModel];

    /// <summary>DeepSeek's own default, sent explicitly so a change of theirs does not change ours.</summary>
    internal const double Temperature = 1.0;

    /// <summary>How long one request may take. A hosted model answers a screen in seconds.</summary>
    internal const int TimeoutSeconds = 60;

    // Its own client, kept connected between translations: the chat requests carry their own
    // timeout, and the balance query is given one below.
    private static readonly HttpClient DefaultHttp = OpenAiChatTranslator.CreateHttpClient();

    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;
    private readonly OpenAiCompatibleProvider _chat;

    public DeepSeekProvider(HttpClient? http = null, Func<AppSettings>? settings = null)
    {
        _http = http ?? DefaultHttp;
        _settings = settings ?? (() => SettingsService.Instance.Current);
        _chat = new OpenAiCompatibleProvider(_http, () => OptionsFor(_settings()), "DeepSeek ");
    }

    public string Name => "DeepSeek";

    // The key is DeepSeek's own, not the DeepL one the callers check; a missing one is named when
    // translating, in the words below.
    public bool RequiresApiKey => false;

    /// <summary>The model a stored choice stands for: one this build offers, or Flash.</summary>
    internal static string ModelFor(string? stored) =>
        Models.FirstOrDefault(model => string.Equals(model, stored?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? FlashModel;

    internal static OpenAiCompatibleOptions OptionsFor(AppSettings settings) => new(
        Endpoint,
        ModelFor(settings.DeepSeekModel),
        settings.DeepSeekApiKey.Trim(),
        Template: "",
        SendTemperature: true,
        Temperature: Temperature,
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

        if (_settings().DeepSeekApiKey.Trim().Length == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.DeepSeekNoKey"));

        try
        {
            return await _chat.TranslateAsync(blocks, sourceLang, targetLang, apiKey, cancellationToken, context);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            // The server's own words under DeepSeek's name, not 「OpenAI 相容」's, for a code the
            // documentation does not list.
            var fallback = ex.InnerException is OpenAiChatException chat
                ? LocalizationService.Format("S.Error.DeepSeekHttp", (int)status, chat.ServerMessage switch
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
            throw new TimeoutException(LocalizationService.Format("S.Error.DeepSeekTimeout", TimeoutSeconds), ex);
        }
    }

    /// <summary>
    /// What a refusal means for someone holding a DeepSeek account — the codes its documentation
    /// lists — or the server's own words for the rest.
    /// </summary>
    internal static string Explain(HttpStatusCode status, string fallback) => (int)status switch
    {
        401 => LocalizationService.Get("S.Error.DeepSeekUnauthorized"),
        402 => LocalizationService.Get("S.Error.DeepSeekNoBalance"),
        429 => LocalizationService.Get("S.Error.DeepSeekRateLimited"),
        500 or 503 => LocalizationService.Get("S.Error.DeepSeekBusy"),
        _ => fallback,
    };

    /// <summary>Asks DeepSeek how much is left on the account the key belongs to.</summary>
    /// <exception cref="InvalidOperationException">No key, or an answer that cannot be read — in the user's words.</exception>
    /// <exception cref="HttpRequestException">Refused, in the user's words.</exception>
    public async Task<DeepSeekBalanceInfo> QueryBalanceAsync(CancellationToken cancellationToken = default)
    {
        var key = _settings().DeepSeekApiKey.Trim();
        if (key.Length == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.DeepSeekNoKey"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get, BalanceEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(LocalizationService.Format("S.Error.DeepSeekTimeout", 15), ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    Explain(response.StatusCode, LocalizationService.Format(
                        "S.Error.DeepSeekHttp", (int)response.StatusCode,
                        body.Trim().Length > 0 ? body.Trim() : LocalizationService.Get("S.Error.NoErrorContent"))),
                    null, response.StatusCode);

            return ParseBalance(body)
                ?? throw new InvalidOperationException(LocalizationService.Get("S.Error.DeepSeekBalanceUnreadable"));
        }
    }

    /// <summary>The balance endpoint's answer, or null when it is not one.</summary>
    internal static DeepSeekBalanceInfo? ParseBalance(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var available = root.TryGetProperty("is_available", out var flag) && flag.ValueKind == JsonValueKind.True;
            var balances = new List<DeepSeekBalance>();
            if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
                foreach (var info in infos.EnumerateArray())
                {
                    if (info.ValueKind != JsonValueKind.Object) continue;
                    var currency = info.TryGetProperty("currency", out var c) ? c.ToString() : "";
                    var total = info.TryGetProperty("total_balance", out var t) ? t.ToString() : "";
                    if (currency.Length > 0 && total.Length > 0)
                        balances.Add(new DeepSeekBalance(currency, total));
                }

            return new DeepSeekBalanceInfo(available, balances);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One balance as the settings panel shows it: ¥12.34 or $1.20.</summary>
    internal static string Display(DeepSeekBalance balance)
    {
        var symbol = balance.Currency.ToUpperInvariant() switch
        {
            "CNY" => "¥",
            "USD" => "$",
            _ => balance.Currency + " ",
        };
        return decimal.TryParse(balance.Total, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? symbol + amount.ToString("0.00", CultureInfo.InvariantCulture)
            : symbol + balance.Total;
    }
}
