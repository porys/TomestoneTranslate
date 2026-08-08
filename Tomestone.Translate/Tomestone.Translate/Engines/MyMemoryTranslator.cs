using Dalamud.Plugin.Services;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tomestone.Translate.Debugging;

namespace Tomestone.Translate.Engines;

/// <summary>
///     Translates via the public MyMemory REST endpoint
///     (<c>api.mymemory.translated.net/get</c>). Free and no API key is required
///     (5,000 chars/day anonymous by IP). Unlike Google Translate it does not
///     auto-detect the source, so the game's client language code is passed
///     explicitly. It is an unofficial free tier, so it may be subject to rate
///     limits (500 bytes max per request) or change without notice.
/// </summary>
public sealed class MyMemoryTranslator : TranslatorHttpBase
{
    private readonly string baseUrl;
    private readonly string sourceLanguage;

    public MyMemoryTranslator(
        string baseUrl,
        string sourceLanguage,
        bool allowSelfSignedHttps,
        IPluginLog log,
        Diagnostics diagnostics)
        : base(null, allowSelfSignedHttps, log, diagnostics)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.sourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage) ? "en" : sourceLanguage;
    }

    public override async Task<string?> TranslateAsync(string sourceText, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        // MyMemory uses short ISO codes; resolve them from the caller-provided target
        // each call so changing languages takes effect without rebuilding the engine.
        var target = string.IsNullOrWhiteSpace(targetLanguage) ? "en" : TargetLanguageCodes.ForGoogle(targetLanguage);
        var langPair = $"{sourceLanguage}|{target}";

        var url = $"{baseUrl}/get?q={Uri.EscapeDataString(sourceText)}&langpair={Uri.EscapeDataString(langPair)}";

        try
        {
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log.Warning($"[Tomestone.Translate] MyMemory returned {(int)response.StatusCode}: {errorBody}");
                Diagnostics.Log($"MyMemory HTTP {(int)response.StatusCode}: {TruncateError(errorBody, 300)}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            if (!root.TryGetProperty("responseData", out var responseData)
                || !responseData.TryGetProperty("translatedText", out var translated)
                || translated.ValueKind != JsonValueKind.String)
            {
                Diagnostics.Log("MyMemory returned an unexpected response shape");
                return null;
            }

            return Normalize(translated.GetString());
        }
        catch (TaskCanceledException)
        {
            Diagnostics.Log("MyMemory request timed out (30s)");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Tomestone.Translate] MyMemory request failed");
            Diagnostics.Log($"MyMemory request failed: {ex.Message}");
            return null;
        }
    }
}