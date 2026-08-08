using Dalamud.Plugin.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using Tomestone.Translate.Debugging;

namespace Tomestone.Translate.Engines;

/// <summary>
///     Thin orchestration layer that owns the active <see cref="ITranslator"/>,
///     rebuilds it when engine settings change, and consults the cache first.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly Diagnostics diagnostics;
    private readonly Dalamud.Game.ClientLanguage clientLanguage;
    private readonly TranslationCache cache = new();

    private readonly object gate = new();
    private ITranslator? translator;
    private string? translatorKey;

    public TranslationService(Configuration configuration, IPluginLog log, Diagnostics diagnostics, Dalamud.Game.ClientLanguage clientLanguage)
    {
        this.configuration = configuration;
        this.log = log;
        this.diagnostics = diagnostics;
        this.clientLanguage = clientLanguage;
    }

    public int CacheSize => cache.Count;

    public void ClearCache() => cache.Clear();

    /// <summary>True when the current provider has the required settings filled in.
    /// Independent of the master /tt switch and the in-instance toggle.</summary>
    public bool IsConfigured
    {
        get
        {
            return configuration.EngineKind switch
            {
                EngineKind.AnthropicClaude => !string.IsNullOrWhiteSpace(configuration.ClaudeApiKey)
                                              && !string.IsNullOrWhiteSpace(configuration.ClaudeModel),
                EngineKind.GoogleGemini => !string.IsNullOrWhiteSpace(configuration.GeminiApiKey)
                                           && !string.IsNullOrWhiteSpace(configuration.GeminiModel),
                EngineKind.DeepL => !string.IsNullOrWhiteSpace(configuration.DeepLApiKey),
                EngineKind.GoogleTranslate => true,
                EngineKind.MyMemory => true,
                _ => !string.IsNullOrWhiteSpace(configuration.EngineModel)
                     && !string.IsNullOrWhiteSpace(configuration.EngineBaseUrl),
            };
        }
    }

    public ITranslator GetTranslator()
    {
        var key = BuildTranslatorKey();

        lock (gate)
        {
            if (translator == null || translatorKey != key)
            {
                translator?.Dispose();
                translator = CreateTranslator();
                translatorKey = key;
                diagnostics.Log($"Engine (re)initialized: {configuration.EngineKind}");
            }

            return translator;
        }
    }

    private string BuildTranslatorKey()
        => string.Join("|",
            configuration.EngineKind,
            configuration.TargetLanguage,
            configuration.EngineBaseUrl, configuration.EngineApiKey, configuration.EngineModel,
            configuration.ClaudeBaseUrl, configuration.ClaudeApiKey, configuration.ClaudeModel,
            configuration.GeminiBaseUrl, configuration.GeminiApiKey, configuration.GeminiModel,
            configuration.DeepLBaseUrl, configuration.DeepLApiKey,
            configuration.DeepLFormalityInformal,
            configuration.GoogleTranslateBaseUrl,
            configuration.MyMemoryBaseUrl,
            configuration.EngineTemperature, configuration.EnginePrompt, configuration.EngineAllowSelfSignedHttps);

    private ITranslator CreateTranslator()
    {
        var allowSelfSigned = configuration.EngineAllowSelfSignedHttps;

        return configuration.EngineKind switch
        {
            EngineKind.AnthropicClaude => new ClaudeTranslator(
                configuration.ClaudeBaseUrl,
                configuration.ClaudeApiKey,
                configuration.ClaudeModel,
                configuration.EngineTemperature,
                configuration.EnginePrompt,
                allowSelfSigned,
                log,
                diagnostics),
            EngineKind.GoogleGemini => new GeminiTranslator(
                configuration.GeminiBaseUrl,
                configuration.GeminiApiKey,
                configuration.GeminiModel,
                configuration.EngineTemperature,
                configuration.EnginePrompt,
                allowSelfSigned,
                log,
                diagnostics),
            EngineKind.DeepL => new DeepLTranslator(
                configuration.DeepLBaseUrl,
                configuration.DeepLApiKey,
                TargetLanguageCodes.ForDeepL(configuration.TargetLanguage),
                configuration.DeepLFormalityInformal,
                allowSelfSigned,
                log,
                diagnostics),
            EngineKind.GoogleTranslate => new GoogleTranslateTranslator(
                configuration.GoogleTranslateBaseUrl,
                TargetLanguageCodes.ForGoogle(configuration.TargetLanguage),
                allowSelfSigned,
                log,
                diagnostics),
            EngineKind.MyMemory => new MyMemoryTranslator(
                configuration.MyMemoryBaseUrl,
                TargetLanguageCodes.ClientLanguageCode(clientLanguage),
                allowSelfSigned,
                log,
                diagnostics),
            _ => new OpenAICompatibleTranslator(
                configuration.EngineBaseUrl,
                configuration.EngineApiKey,
                configuration.EngineModel,
                configuration.EngineTemperature,
                configuration.EnginePrompt,
                allowSelfSigned,
                log,
                diagnostics),
        };
    }

    /// <summary>Translates the given line into the configured target language.</summary>
    public async Task<string?> TranslateAsync(string sourceText, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return null;
        }

        // When the target language matches the game's client language, nothing
        // needs translating - return the text as-is.
        if (TargetLanguageCodes.MatchesClientLanguage(clientLanguage, configuration.TargetLanguage))
        {
            return sourceText;
        }

        if (cache.Get(sourceText, configuration.TargetLanguage) is { } cached)
        {
            return cached;
        }

        var result = await GetTranslator().TranslateAsync(sourceText, configuration.TargetLanguage, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result))
        {
            cache.Set(sourceText, configuration.TargetLanguage, result);
        }

        return result;
    }

    public void Dispose()
    {
        lock (gate)
        {
            translator?.Dispose();
            translator = null;
        }
    }
}