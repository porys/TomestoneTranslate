using Dalamud.Plugin.Services;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tomestone.Translate.Debugging;

namespace Tomestone.Translate.Engines;

/// <summary>
///     Translates via any OpenAI-compatible chat completions endpoint
///     (OpenAI, DeepSeek, OpenRouter, Ollama /v1, LM Studio, ...).
/// </summary>
public sealed class OpenAICompatibleTranslator : TranslatorHttpBase
{
    private readonly string baseUrl;
    private readonly string model;
    private readonly float temperature;

    public OpenAICompatibleTranslator(
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
        this.model = model;
        this.temperature = temperature;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    public override async Task<string?> TranslateAsync(string sourceText, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        var payload = new
        {
            model,
            temperature,
            messages = new[]
            {
                new { role = "system", content = BuildSystemMessage(SystemPrompt, targetLanguage) },
                new { role = "user", content = BuildUserMessage(sourceText, targetLanguage) },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
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
                Log.Warning($"[Tomestone.Translate] Engine returned {(int)response.StatusCode}: {errorBody}");
                Diagnostics.NoteFailure($"Engine HTTP {(int)response.StatusCode}: {TruncateError(errorBody, 300)}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                Diagnostics.NoteFailure("Engine returned no 'choices' array");
                return null;
            }

            var text = choices[0].TryGetProperty("message", out var message)
                       && message.TryGetProperty("content", out var content)
                ? content.GetString()
                : null;

            if (text == null)
            {
                Diagnostics.NoteFailure("Engine returned a message with no 'content'");
            }

            return Normalize(text);
        }
        catch (TaskCanceledException)
        {
            Diagnostics.NoteFailure("Engine request timed out (30s)");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Tomestone.Translate] Engine request failed");
            Diagnostics.NoteFailure($"Engine request failed: {ex.Message}");
            return null;
        }
    }
}