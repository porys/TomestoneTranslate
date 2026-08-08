using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Tomestone.Translate.Debugging;

namespace Tomestone.Translate.Engines;

/// <summary>
///     Shared plumbing for HTTP-based translation engines: HTTP client setup,
///     the default translation prompt, and post-processing that strips common
///     LLM artifacts (labels, arrows, markdown fences, surrounding quotes) from
///     the returned text.
/// </summary>
public abstract class TranslatorHttpBase : ITranslator
{
    protected const string DefaultSystemPrompt =
        "You are a professional translation engine for a video game. " +
        "Translate the user-provided text into the requested target language. " +
        "Keep it natural and in-character. Preserve line breaks and formatting. " +
        "Output ONLY the translation, with no explanation, no quotes, and no labels.";

    protected readonly HttpClient Http;
    protected readonly IPluginLog Log;
    protected readonly Diagnostics Diagnostics;
    protected readonly string SystemPrompt;

    protected TranslatorHttpBase(string? systemPrompt, bool allowSelfSignedHttps, IPluginLog log, Diagnostics diagnostics)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        if (allowSelfSignedHttps)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        Http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt;
        Log = log;
        Diagnostics = diagnostics;
    }

    public abstract Task<string?> TranslateAsync(string sourceText, string targetLanguage, CancellationToken ct);

    protected static string BuildUserMessage(string sourceText, string targetLanguage)
        => $"Translate the following text into {targetLanguage}.\n\n{sourceText}";

    /// <summary>
    ///     Builds a system prompt that also pins the requested output language,
    ///     so the model receives the target both in the system and user messages.
    /// </summary>
    protected static string BuildSystemMessage(string systemPrompt, string targetLanguage)
        => $"{systemPrompt}\n\nThe requested output language is {targetLanguage}. Reply ONLY in {targetLanguage}.";

    protected static string TruncateError(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    protected static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || IsMarkdownArtifact(line))
            {
                continue;
            }

            if (lines.Count == 0)
            {
                line = StripLabelPrefix(line);
            }

            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var result = string.Join('\n', lines).Trim();

        if (result.Length >= 2)
        {
            var c0 = result[0];
            var c1 = result[^1];
            if ((c0 == '"' && c1 == '"') || (c0 == '“' && c1 == '”') || (c0 == '\u2018' && c1 == '\u2019'))
            {
                result = result[1..^1].Trim();
            }
        }

        if (!result.Any(char.IsLetter))
        {
            return null;
        }

        return result;
    }

    private static bool IsMarkdownArtifact(string line)
    {
        if (line.StartsWith("```", StringComparison.Ordinal) || line == "```")
        {
            return true;
        }

        if (line.Length >= 3)
        {
            var c = line[0];
            if (c is '=' or '-' or '*' or '_' or '~' or '`' or '#' or '>')
            {
                for (var i = 1; i < line.Length; i++)
                {
                    if (line[i] != c)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return false;
    }

    private static string StripLabelPrefix(string line)
    {
        foreach (var prefix in LabelPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = line[prefix.Length..].Trim();
                if (!string.IsNullOrEmpty(rest))
                {
                    return rest;
                }
            }
        }

        return TrailArrow(line);
    }

    private static string TrailArrow(string line)
    {
        foreach (var arrow in ArrowPrefixes)
        {
            if (line.StartsWith(arrow, StringComparison.OrdinalIgnoreCase))
            {
                return line[arrow.Length..].Trim();
            }
        }

        return line;
    }

    private static readonly string[] LabelPrefixes =
    {
        "translation:", "translated:", "translation →", "translated →", "translation ->", "translated ->",
    };

    private static readonly string[] ArrowPrefixes =
    {
        "→", "=>", "->", ":", "»",
    };

    public void Dispose()
    {
        Http.Dispose();
    }
}