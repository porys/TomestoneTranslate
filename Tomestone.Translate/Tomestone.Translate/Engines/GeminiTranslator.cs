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
///     Translates via the Google Gemini Generative Language API
///     (<c>POST {baseUrl}/models/{model}:generateContent</c>). A new chat
///     context is sent for every request (system instruction + user message),
///     so requests are fully stateless.
/// </summary>
public sealed class GeminiTranslator : TranslatorHttpBase
{
    private readonly string baseUrl;
    private readonly string apiKey;
    private readonly string model;
    private readonly float temperature;

    public GeminiTranslator(
        string baseUrl,
        string apiKey,
        string model,
        float temperature,
        string? systemPrompt,
        bool allowSelfSignedHttps,
        IPluginLog log,
        Diagnostics diagnostics)
        : base(systemPrompt, allowSelfSignedHttps, log, diagnostics)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.apiKey = apiKey;
        this.model = model;
        this.temperature = temperature;
    }

    public override async Task<string?> TranslateAsync(string sourceText, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        var payload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = BuildSystemMessage(SystemPrompt, targetLanguage) } },
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = BuildUserMessage(sourceText, targetLanguage) } },
                },
            },
            generationConfig = new { temperature = temperature },
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log.Warning($"[Tomestone.Translate] Gemini returned {(int)response.StatusCode}: {errorBody}");
                Diagnostics.Log($"Gemini HTTP {(int)response.StatusCode}: {TruncateError(errorBody, 300)}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                Diagnostics.Log("Gemini returned no 'candidates' array");
                return null;
            }

            var sb = new StringBuilder();
            if (candidates[0].TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text))
                    {
                        sb.Append(text.GetString());
                    }
                }
            }

            return Normalize(sb.ToString());
        }
        catch (TaskCanceledException)
        {
            Diagnostics.Log("Gemini request timed out (30s)");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Tomestone.Translate] Gemini request failed");
            Diagnostics.Log($"Gemini request failed: {ex.Message}");
            return null;
        }
    }
}