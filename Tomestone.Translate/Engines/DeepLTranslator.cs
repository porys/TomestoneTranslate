using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tomestone.Translate.Debugging;

namespace Tomestone.Translate.Engines;

/// <summary>
///     Translates via the DeepL text translation API v2
///     (form-encoded <c>POST {baseUrl}/translate</c>). DeepL is a raw text
///     translator rather than a chat model, so the system prompt is not used;
///     the target language comes from a fixed ISO language code.
/// </summary>
public sealed class DeepLTranslator : TranslatorHttpBase
{
    private readonly string baseUrl;
    private readonly string targetLanguage;
    private readonly bool informal;

    public DeepLTranslator(
        string baseUrl,
        string apiKey,
        string targetLanguage,
        bool informal,
        bool allowSelfSignedHttps,
        IPluginLog log,
        Diagnostics diagnostics)
        : base(null, allowSelfSignedHttps, log, diagnostics)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.targetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "EN-US" : targetLanguage;
        this.informal = informal;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {apiKey.Trim()}");
        }
    }

    public override async Task<string?> TranslateAsync(string sourceText, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        // DeepL needs an ISO code; resolve it from the caller-provided target each
        // call so changing languages takes effect without rebuilding the engine.
        var lang = string.IsNullOrWhiteSpace(targetLanguage) ? this.targetLanguage : TargetLanguageCodes.ForDeepL(targetLanguage);

        var form = new List<KeyValuePair<string, string>>
        {
            new("text", sourceText),
            new("target_lang", lang),
        };

        if (informal)
        {
            form.Add(new KeyValuePair<string, string>("formality", "informal"));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/translate")
        {
            Content = new FormUrlEncodedContent(form),
        };

        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log.Warning($"[Tomestone.Translate] DeepL returned {(int)response.StatusCode}: {errorBody}");
                Diagnostics.Log($"DeepL HTTP {(int)response.StatusCode}: {TruncateError(errorBody, 300)}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            if (!root.TryGetProperty("translations", out var translations) || translations.GetArrayLength() == 0)
            {
                Diagnostics.Log("DeepL returned no 'translations' array");
                return null;
            }

            var text = translations[0].TryGetProperty("text", out var translated) ? translated.GetString() : null;
            if (text == null)
            {
                Diagnostics.Log("DeepL response had no 'text'");
            }

            return Normalize(text);
        }
        catch (TaskCanceledException)
        {
            Diagnostics.Log("DeepL request timed out (30s)");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Tomestone.Translate] DeepL request failed");
            Diagnostics.Log($"DeepL request failed: {ex.Message}");
            return null;
        }
    }
}