using Dalamud.Plugin.Services;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tomestone.Translate.Debugging;

namespace Tomestone.Translate.Engines;

/// <summary>
///     Translates via Google's public web translation endpoint
///     (<c>translate.googleapis.com/translate_a/single</c>). This is a free
///     auto-detect engine that needs no API key; the source language is
///     detected automatically. It is an unofficial endpoint, so it may be
///     subject to rate limits or change without notice.
/// </summary>
public sealed class GoogleTranslateTranslator : TranslatorHttpBase
{
    private readonly string baseUrl;
    private readonly string targetLanguage;

    public GoogleTranslateTranslator(
        string baseUrl,
        string targetLanguage,
        bool allowSelfSignedHttps,
        IPluginLog log,
        Diagnostics diagnostics)
        : base(null, allowSelfSignedHttps, log, diagnostics)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.targetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "en" : targetLanguage;
    }

    public override async Task<string?> TranslateAsync(string sourceText, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        // Google uses short ISO codes; resolve them from the caller-provided target
        // each call so changing languages takes effect without rebuilding the engine.
        var lang = string.IsNullOrWhiteSpace(targetLanguage) ? this.targetLanguage : TargetLanguageCodes.ForGoogle(targetLanguage);

        var url = $"{baseUrl}/translate_a/single" +
                  $"?client=gtx&sl=auto&tl={Uri.EscapeDataString(lang)}" +
                  $"&dt=t&q={Uri.EscapeDataString(sourceText)}";

        try
        {
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log.Warning($"[Tomestone.Translate] Google Translate returned {(int)response.StatusCode}: {errorBody}");
                Diagnostics.Log($"Google Translate HTTP {(int)response.StatusCode}: {TruncateError(errorBody, 300)}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                Diagnostics.Log("Google Translate returned an unexpected response shape");
                return null;
            }

            var sb = new StringBuilder();
            if (root[0].ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in root[0].EnumerateArray())
                {
                    if (segment.ValueKind == JsonValueKind.Array
                        && segment.GetArrayLength() > 0
                        && segment[0].ValueKind == JsonValueKind.String)
                    {
                        sb.Append(segment[0].GetString());
                    }
                }
            }

            return Normalize(sb.ToString());
        }
        catch (TaskCanceledException)
        {
            Diagnostics.Log("Google Translate request timed out (30s)");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Tomestone.Translate] Google Translate request failed");
            Diagnostics.Log($"Google Translate request failed: {ex.Message}");
            return null;
        }
    }
}