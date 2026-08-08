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
///     Translates via the Anthropic Claude Messages API
///     (<c>POST {baseUrl}/messages</c>).
/// </summary>
public sealed class ClaudeTranslator : TranslatorHttpBase
{
    private const string ApiVersionHeader = "anthropic-version";
    private const string ApiVersionValue = "2023-06-01";

    private readonly string baseUrl;
    private readonly string model;
    private readonly float temperature;

    public ClaudeTranslator(
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

        Http.DefaultRequestHeaders.Add(ApiVersionHeader, ApiVersionValue);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            Http.DefaultRequestHeaders.Add("x-api-key", apiKey.Trim());
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
            max_tokens = 2048,
            temperature,
            system = BuildSystemMessage(SystemPrompt, targetLanguage),
            messages = new[]
            {
                new { role = "user", content = BuildUserMessage(sourceText, targetLanguage) },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/messages")
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
                Log.Warning($"[Tomestone.Translate] Claude returned {(int)response.StatusCode}: {errorBody}");
                Diagnostics.Log($"Claude HTTP {(int)response.StatusCode}: {TruncateError(errorBody, 300)}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            if (!root.TryGetProperty("content", out var content) || content.GetArrayLength() == 0)
            {
                Diagnostics.Log("Claude returned no 'content' array");
                return null;
            }

            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && block.TryGetProperty("text", out var text))
                {
                    sb.Append(text.GetString());
                }
            }

            return Normalize(sb.ToString());
        }
        catch (TaskCanceledException)
        {
            Diagnostics.Log("Claude request timed out (30s)");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Tomestone.Translate] Claude request failed");
            Diagnostics.Log($"Claude request failed: {ex.Message}");
            return null;
        }
    }
}