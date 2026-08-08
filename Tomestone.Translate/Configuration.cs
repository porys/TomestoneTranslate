using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tomestone.Translate;

public enum EngineKind
{
    OpenAICompatible = 0,
    AnthropicClaude = 1,
    GoogleGemini = 2,
    DeepL = 3,
    GoogleTranslate = 4,
    MyMemory = 5,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // ---- Master switch ------------------------------------------------------
    /// <summary>Master on/off switch for the whole translation feature.</summary>
    public bool PluginEnabled { get; set; } = true;

    /// <summary>When enabled, translation is paused while inside an instanced duty
    /// (dungeon, trial, raid) and resumes automatically outside.</summary>
    public bool DisableInsideInstance { get; set; } = true;

    // ---- Translation target -------------------------------------------------
    /// <summary>Target language for translations, e.g. "English" or a language code like "en".</summary>
    public string TargetLanguage { get; set; } = "English";

    // ---- Engine (provider selector) -----------------------------------------
    /// <summary>Which translation provider to use; see <see cref="EngineKind"/>.</summary>
    public EngineKind EngineKind { get; set; } = EngineKind.GoogleTranslate;
    public bool EngineAllowSelfSignedHttps { get; set; } = false;

    // ---- Shared chat settings (OpenAI-compatible / Claude / Gemini) ---------
    public float EngineTemperature { get; set; } = 0.1f;
    public string EnginePrompt { get; set; } = string.Empty;

    // ---- Engine: OpenAI-compatible, v1 --------------------------------------
    public string EngineBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string EngineApiKey { get; set; } = string.Empty;
    public string EngineModel { get; set; } = "gpt-4o-mini";

    // ---- Engine: Anthropic Claude -------------------------------------------
    public string ClaudeBaseUrl { get; set; } = "https://api.anthropic.com/v1";
    public string ClaudeApiKey { get; set; } = string.Empty;
    public string ClaudeModel { get; set; } = "claude-3-5-sonnet-20241022";

    // ---- Engine: Google Gemini ----------------------------------------------
    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public string GeminiApiKey { get; set; } = string.Empty;
    public string GeminiModel { get; set; } = "gemini-2.0-flash";

    // ---- Engine: DeepL --------------------------------------------------------
    public string DeepLBaseUrl { get; set; } = "https://api-free.deepl.com/v2";
    public string DeepLApiKey { get; set; } = string.Empty;
    /// <summary>Whether to use DeepL's informal register where supported.</summary>
    public bool DeepLFormalityInformal { get; set; } = true;

    // ---- Engine: Google Translate (free, no API key) ---------------------------
    public string GoogleTranslateBaseUrl { get; set; } = "https://translate.googleapis.com";

    // ---- Engine: MyMemory (free, no API key) ------------------------------------
    public string MyMemoryBaseUrl { get; set; } = "https://api.mymemory.translated.net";

    // ---- Overlay display ----------------------------------------------------
    public bool OverlayAboveText { get; set; } = true;
    public bool OverlayOnTopOfText { get; set; } = false;
    public float OverlayFontScale { get; set; } = 1.0f;
    public float OverlayVerticalOffset { get; set; } = -35f;
    public float OverlayOpacity { get; set; } = 1.0f;
    public float OverlayBackgroundOpacity { get; set; } = 1.0f;
    public float OverlayMaxWidth { get; set; } = 800f;
    public bool OverlayShowBackground { get; set; } = true;
    public bool OverlayShowPlaceholder { get; set; } = true;
    public string OverlayPlaceholderText { get; set; } = "…";
    public bool OverlayReplaceOriginalText { get; set; } = false;
    public uint OverlayTextColor { get; set; } = 0xFFFFFFFF;
    public uint OverlayNameColor { get; set; } = 0xFF9ECBFF;

    // ---- Per-surface toggles ------------------------------------------------
    public bool TranslateTalk { get; set; } = true;
    public bool TranslateTalkSubtitle { get; set; } = true;
    public bool TranslateBattleTalk { get; set; } = true;
    public bool TranslateMiniTalk { get; set; } = true;
    public bool TranslateSelectString { get; set; } = true;
    public bool TranslateSpeakerNames { get; set; } = true;

    // ---- Window bookkeeping -------------------------------------------------
    public bool IsConfigWindowMovable { get; set; } = true;

    /// <summary>Whether the Developer (troubleshooting) tab is shown in the settings window.</summary>
    public bool ShowDeveloperTab { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

/// <summary>
///     Converts the single user-facing target language (a friendly name or ISO
///     code from the Language tab) into the ISO codes that DeepL and Google
///     Translate require, so there is only ever one place to set the language.
/// </summary>
public static class TargetLanguageCodes
{
    /// <summary>Gets a DeepL ISO target code (e.g. "EN-US") for the given language name.</summary>
    public static string ForDeepL(string languageName) => Resolve(languageName).DeepL;

    /// <summary>Gets a Google ISO target code (e.g. "en") for the given language name.</summary>
    public static string ForGoogle(string languageName) => Resolve(languageName).Google;

    /// <summary>Maps the in-game client language to a short language code.</summary>
    public static string ClientLanguageCode(Dalamud.Game.ClientLanguage client) => client switch
    {
        Dalamud.Game.ClientLanguage.Japanese => "ja",
        Dalamud.Game.ClientLanguage.English => "en",
        Dalamud.Game.ClientLanguage.German => "de",
        Dalamud.Game.ClientLanguage.French => "fr",
        _ => string.Empty,
    };

    /// <summary>
    ///     True when the requested target language is the same as the game client
    ///     language, in which case no translation is needed.
    /// </summary>
    public static bool MatchesClientLanguage(Dalamud.Game.ClientLanguage client, string targetLanguage)
    {
        var clientCode = ClientLanguageCode(client);
        return !string.IsNullOrEmpty(clientCode)
               && string.Equals(clientCode, ForGoogle(targetLanguage), StringComparison.OrdinalIgnoreCase);
    }

    private static (string DeepL, string Google) Resolve(string? languageName)
    {
        var key = languageName?.Trim();
        if (!string.IsNullOrEmpty(key) && Map.TryGetValue(key, out var mapped))
        {
            return mapped;
        }

        // A raw ISO code typed in the Language tab is passed through as-is.
        if (!string.IsNullOrEmpty(key) && key.Length <= 8 && key.All(c => char.IsAsciiLetter(c) || c is '-' or '_'))
        {
            return (key.ToUpperInvariant(), key.ToLowerInvariant());
        }

        return Map["English"];
    }

    private static readonly Dictionary<string, (string DeepL, string Google)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["English"] = ("EN-US", "en"),
            ["Japanese"] = ("JA", "ja"),
            ["日本語"] = ("JA", "ja"),
            ["Korean"] = ("KO", "ko"),
            ["한국어"] = ("KO", "ko"),
            ["Simplified Chinese"] = ("ZH", "zh-CN"),
            ["Chinese (Simplified)"] = ("ZH", "zh-CN"),
            ["简体中文"] = ("ZH", "zh-CN"),
            ["Traditional Chinese"] = ("ZH-HANT", "zh-TW"),
            ["Chinese (Traditional)"] = ("ZH-HANT", "zh-TW"),
            ["繁體中文"] = ("ZH-HANT", "zh-TW"),
            ["Chinese"] = ("ZH", "zh-CN"),
            ["French"] = ("FR", "fr"),
            ["German"] = ("DE", "de"),
            ["Spanish"] = ("ES", "es"),
            ["Portuguese"] = ("PT-BR", "pt"),
            ["Italian"] = ("IT", "it"),
            ["Russian"] = ("RU", "ru"),
            ["Ukrainian"] = ("UK", "uk"),
            ["Polish"] = ("PL", "pl"),
            ["Thai"] = ("TH", "th"),
            ["Vietnamese"] = ("VI", "vi"),
            ["Indonesian"] = ("ID", "id"),
            ["Arabic"] = ("AR", "ar"),
        };
}
