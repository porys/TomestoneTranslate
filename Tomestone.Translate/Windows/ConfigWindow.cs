using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Tomestone.Translate.Capture;using Tomestone.Translate.Debugging;
using Tomestone.Translate.Engines;

namespace Tomestone.Translate.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
private readonly TranslationService translationService;
    private readonly TalkCaptureService talkCapture;
    private readonly Diagnostics diagnostics;
    private readonly Dalamud.Game.ClientLanguage clientLanguage;
    private string? nodeScan;
    private string? scanAddonName;
    private readonly HashSet<string> endpointEditToggles = new(StringComparer.Ordinal);
    private readonly HashSet<string> customEditing = new(StringComparer.Ordinal);

    public ConfigWindow(Plugin plugin, TranslationService translationService, TalkCaptureService talkCapture, Diagnostics diagnostics, Dalamud.Game.ClientLanguage clientLanguage)
        : base("Tomestone Translate Settings###TomestoneTranslateConfig")
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;
        this.translationService = translationService;
        this.talkCapture = talkCapture;
        this.diagnostics = diagnostics;
        this.clientLanguage = clientLanguage;

        Size = new Vector2(480, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // ---- Preset data (pick lists shown as dropdowns) --------------
    private static readonly (string Name, string Value)[] CommonLanguages =
    {
        ("English", "English"),
        ("Japanese", "Japanese"),
        ("Korean", "Korean"),
        ("Chinese (Simplified)", "Simplified Chinese"),
        ("Chinese (Traditional)", "Traditional Chinese"),
        ("French", "French"),
        ("German", "German"),
        ("Spanish", "Spanish"),
        ("Portuguese", "Portuguese"),
        ("Italian", "Italian"),
        ("Russian", "Russian"),
        ("Ukrainian", "Ukrainian"),
        ("Polish", "Polish"),
        ("Thai", "Thai"),
        ("Vietnamese", "Vietnamese"),
        ("Indonesian", "Indonesian"),
        ("Arabic", "Arabic"),
    };

    private static readonly (string Name, string Value)[] OpenAiModels =
    {
        ("OpenAI GPT-4o mini", "gpt-4o-mini"),
        ("OpenAI GPT-4o", "gpt-4o"),
        ("OpenAI GPT-4.1", "gpt-4.1"),
        ("DeepSeek chat", "deepseek-chat"),
        ("DeepSeek reasoner", "deepseek-reasoner"),
        ("Qwen 2.5 72B", "Qwen/Qwen2.5-72B-Instruct"),
        ("Llama 3.3 70B", "meta-llama/Llama-3.3-70B-Instruct"),
    };

    private static readonly (string Name, string Value)[] ClaudeModels =
    {
        ("Claude Opus 4.1", "claude-opus-4-1"),
        ("Claude Sonnet 4", "claude-sonnet-4"),
        ("Claude Haiku 4.5", "claude-haiku-4-5"),
        ("Claude 3.5 Sonnet", "claude-3-5-sonnet-20241022"),
        ("Claude 3.5 Haiku", "claude-3-5-haiku-latest"),
    };

    private static readonly (string Name, string Value)[] GeminiModels =
    {
        ("Gemini 2.5 Pro (preview)", "gemini-2.5-pro-preview-03-25"),
        ("Gemini 2.5 Flash (preview)", "gemini-2.5-flash-preview-04-17"),
        ("Gemini 2.0 Flash", "gemini-2.0-flash"),
        ("Gemini 2.0 Flash-Lite", "gemini-2.0-flash-lite"),
        ("Gemini 1.5 Pro", "gemini-1.5-pro"),
        ("Gemini 1.5 Flash", "gemini-1.5-flash"),
    };

    private enum PickResult { None, Preset, Custom }

    private static bool IsPresetValue(string current, IReadOnlyList<(string Name, string Value)> presets)
    {
        for (var i = 0; i < presets.Count; i++)
        {
            if (presets[i].Value == current)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Draws a labelled dropdown. Returns a picked preset value, a request to type a
    /// custom value, or nothing if the menu was simply dismissed.</summary>
    private static (PickResult Kind, string? Value) DrawPickCombo(
        string label, string id, string current, IReadOnlyList<(string Name, string Value)> presets)
    {
        var index = -1;
        for (var i = 0; i < presets.Count; i++)
        {
            if (presets[i].Value == current)
            {
                index = i;
                break;
            }
        }

        var isPreset = index >= 0;
        var preview = isPreset
            ? presets[index].Name
            : string.IsNullOrEmpty(current) ? "Choose…" : $"Custom: {current}";

        ImGui.Text(label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(320f);
        if (!ImGui.BeginCombo($"##{id}", preview))
        {
            return (PickResult.None, null);
        }

        var result = (PickResult.None, (string?)null);
        for (var i = 0; i < presets.Count; i++)
        {
            if (ImGui.Selectable(presets[i].Name, i == index))
            {
                result = (PickResult.Preset, presets[i].Value);
            }
        }

        ImGui.Separator();
        if (ImGui.Selectable("Custom value…", !isPreset))
        {
            result = (PickResult.Custom, current);
        }

        ImGui.EndCombo();
        return result;
    }

    /// <summary>Draws a dropdown with a persistent custom-value input. Once "Custom value…" is
    /// chosen the editable field stays visible until a preset is picked, letting the user type
    /// an arbitrary URL/model instead of being limited to the presets. When
    /// <paramref name="readOnlyLabel"/> is set, the resolved preset value is shown beneath the
    /// dropdown in the read-only client-language style.</summary>
    private void DrawPickField(
        string label, string id, string current, IReadOnlyList<(string Name, string Value)> presets,
        Action<string> setter, string? customHint = null, string? readOnlyLabel = null)
    {
        var (result, value) = DrawPickCombo(label, id, current, presets);
        if (result == PickResult.Preset && value != null)
        {
            customEditing.Remove(id);
            setter(value);
            configuration.Save();
            return;
        }

        if (result == PickResult.Custom)
        {
            customEditing.Add(id);
        }

        if (customEditing.Contains(id) || !IsPresetValue(current, presets))
        {
            var buffer = current;
            ImGui.SetNextItemWidth(320f);
            if (ImGui.InputText(label, ref buffer, 256))
            {
                setter(buffer.Trim());
                configuration.Save();
            }

            if (customHint != null)
            {
                ImGui.TextDisabled(customHint);
            }
        }
        else if (readOnlyLabel != null && !string.IsNullOrEmpty(current))
        {
            DrawReadOnlyValue(readOnlyLabel, current);
        }
    }

    /// <summary>Draws a value the same way the client language is shown: a label and the
    /// value in muted coloured text. Used for read-only base URLs.</summary>
    private static void DrawReadOnlyValue(string label, string value)
    {
        ImGui.Text(label + ":");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1f), value);
    }

    public override void PreDraw()
    {
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("TomestoneConfigTabs"))
        {
            if (ImGui.BeginTabItem("Translation"))
            {
                DrawTranslationTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Content"))
            {
                DrawContentTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Display"))
            {
                DrawDisplayTab();
                ImGui.EndTabItem();
            }

            if (configuration.ShowDeveloperTab && ImGui.BeginTabItem("Developer"))
            {
                DrawDeveloperTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.Spacing();
        ImGui.Separator();

        var showDeveloper = configuration.ShowDeveloperTab;
        if (ImGui.Checkbox("Show Developer tab (troubleshooting)", ref showDeveloper))
        {
            configuration.ShowDeveloperTab = showDeveloper;
            configuration.Save();
        }

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "Commands: /tt (main window, on/off), /ttconfig (this window)");
    }

    private void DrawTranslationTab()
    {
        DrawLanguageSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawEngineSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawInstanceSection();
    }

    private void DrawInstanceSection()
    {
        var disableInsideInstance = configuration.DisableInsideInstance;
        if (ImGui.Checkbox("Disable inside instance (duty)", ref disableInsideInstance))
        {
            configuration.DisableInsideInstance = disableInsideInstance;
            configuration.Save();
        }
        ImGui.TextDisabled("Translation pauses while inside a dungeon, trial, or raid, and resumes when you leave. Use the /tt switch to turn it off entirely.");
    }

    private void DrawLanguageSection()
    {
        var sourceName = ClientLanguageName(clientLanguage);
        var isSame = TargetLanguageCodes.MatchesClientLanguage(clientLanguage, configuration.TargetLanguage);

        ImGui.TextWrapped($"Game language (source): {sourceName}");
        ImGui.Spacing();

        ImGui.Text("Translate dialogue into:");
        ImGui.Spacing();

        DrawPickField(
            "Target language", "targetLanguage", configuration.TargetLanguage, CommonLanguages,
            v => configuration.TargetLanguage = v);

        if (isSame)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), $"Target matches the game's {sourceName} - translation is skipped.");
        }
    }

    private static string ClientLanguageName(Dalamud.Game.ClientLanguage c)
        => c switch
        {
            Dalamud.Game.ClientLanguage.Japanese => "Japanese",
            Dalamud.Game.ClientLanguage.English => "English",
            Dalamud.Game.ClientLanguage.German => "German",
            Dalamud.Game.ClientLanguage.French => "French",
            _ => c.ToString(),
        };

    private static string EngineKindName(EngineKind kind)
        => kind switch
        {
            EngineKind.AnthropicClaude => "Anthropic Claude",
            EngineKind.GoogleGemini => "Google Gemini",
            EngineKind.DeepL => "DeepL",
            EngineKind.GoogleTranslate => "Google Translate (free, no API key)",
            EngineKind.MyMemory => "MyMemory (free, no API key)",
            _ => "OpenAI-compatible (OpenAI / DeepSeek / Ollama / LM Studio ...)",
        };

    private static readonly EngineKind[] EngineKindOrder =
    {
        EngineKind.GoogleTranslate,
        EngineKind.MyMemory,
        EngineKind.OpenAICompatible,
        EngineKind.AnthropicClaude,
        EngineKind.GoogleGemini,
        EngineKind.DeepL,
    };

    private void DrawEngineSection()
    {
        ImGui.Text("Provider:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(320f);
        if (ImGui.BeginCombo("##engineKindCombo", EngineKindName(configuration.EngineKind)))
        {
            foreach (var kind in EngineKindOrder)
            {
                if (ImGui.Selectable(EngineKindName(kind), kind == configuration.EngineKind))
                {
                    if (kind != configuration.EngineKind)
                    {
                        configuration.EngineKind = kind;
                        translationService.ClearCache();
                        configuration.Save();
                    }
                }
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        switch (configuration.EngineKind)
        {
            case EngineKind.AnthropicClaude:
                DrawClaudeFields();
                break;
            case EngineKind.GoogleGemini:
                DrawGeminiFields();
                break;
            case EngineKind.DeepL:
                DrawDeepLFields();
                break;
            case EngineKind.GoogleTranslate:
                DrawGoogleTranslateFields();
                break;
            case EngineKind.MyMemory:
                DrawMyMemoryFields();
                break;
            default:
                DrawOpenAICompatibleFields();
                break;
        }

        if (configuration.EngineKind != EngineKind.DeepL && configuration.EngineKind != EngineKind.GoogleTranslate && configuration.EngineKind != EngineKind.MyMemory)
        {
            DrawChatSharedFields();
        }
    }

    private void DrawOpenAICompatibleFields()
    {
        ImGui.TextDisabled("Works with OpenAI, DeepSeek, OpenRouter, Ollama (local), LM Studio, and more.");
        ImGui.Spacing();

        DrawEndpointField("Endpoint URL", "engineBaseUrl", configuration.EngineBaseUrl, "https://api.openai.com/v1",
            v => configuration.EngineBaseUrl = v.Trim().TrimEnd('/'));

        var apiKey = configuration.EngineApiKey;
        if (ImGui.InputText("API key", ref apiKey, 256, ImGuiInputTextFlags.Password))
        {
            configuration.EngineApiKey = apiKey.Trim();
            configuration.Save();
        }
        ImGui.TextDisabled("Leave blank for local tools (Ollama / LM Studio).");
        ImGui.Spacing();

        DrawPickField(
            "Model", "engineModel", configuration.EngineModel, OpenAiModels,
            v => configuration.EngineModel = v);

        ImGui.Spacing();
        DrawAllowSelfSignedField();
    }

    private void DrawClaudeFields()
    {
        ImGui.TextDisabled("Uses Anthropic's Messages API.");
        ImGui.Spacing();

        DrawEndpointField("Base URL", "claudeBaseUrl", configuration.ClaudeBaseUrl, "https://api.anthropic.com/v1",
            v => configuration.ClaudeBaseUrl = v);

        var apiKey = configuration.ClaudeApiKey;
        if (ImGui.InputText("API key", ref apiKey, 256, ImGuiInputTextFlags.Password))
        {
            configuration.ClaudeApiKey = apiKey.Trim();
            configuration.Save();
        }
        ImGui.Spacing();

        DrawPickField(
            "Model", "claudeModel", configuration.ClaudeModel, ClaudeModels,
            v => configuration.ClaudeModel = v);
    }

    private void DrawGeminiFields()
    {
        ImGui.TextDisabled("Uses Google's Gemini Generative Language API.");
        ImGui.Spacing();

        DrawEndpointField("Base URL", "geminiBaseUrl", configuration.GeminiBaseUrl, "https://generativelanguage.googleapis.com/v1beta",
            v => configuration.GeminiBaseUrl = v);

        var apiKey = configuration.GeminiApiKey;
        if (ImGui.InputText("API key", ref apiKey, 256, ImGuiInputTextFlags.Password))
        {
            configuration.GeminiApiKey = apiKey.Trim();
            configuration.Save();
        }
        ImGui.Spacing();

        DrawPickField(
            "Model", "geminiModel", configuration.GeminiModel, GeminiModels,
            v => configuration.GeminiModel = v);
    }

    private void DrawGoogleTranslateFields()
    {
        ImGui.TextDisabled("Free and no API key required. Source language is auto-detected.");
        ImGui.TextDisabled("May be rate-limited - best for occasional manual translation.");
        ImGui.Spacing();

        DrawEndpointField("Base URL", "googleTranslateBaseUrl", configuration.GoogleTranslateBaseUrl, "https://translate.googleapis.com",
            v => configuration.GoogleTranslateBaseUrl = v);
        ImGui.Spacing();

        ImGui.TextWrapped($"Translating into: {configuration.TargetLanguage}  (set on the Language tab)");
    }

    private void DrawMyMemoryFields()
    {
        ImGui.TextDisabled("Free and no API key required. Source language is taken from the game client.");
        ImGui.TextDisabled("Limited to 5,000 chars/day per IP and 500 bytes per request.");
        ImGui.Spacing();

        DrawEndpointField("Base URL", "myMemoryBaseUrl", configuration.MyMemoryBaseUrl, "https://api.mymemory.translated.net",
            v => configuration.MyMemoryBaseUrl = v);
        ImGui.Spacing();

        ImGui.TextWrapped($"Translating into: {configuration.TargetLanguage}  (set on the Language tab)");
    }

    private void DrawDeepLFields()
    {
        ImGui.TextDisabled("DeepL is a raw text translator; it ignores the system prompt below.");
        ImGui.Spacing();

        DrawEndpointField("Base URL", "deeplBaseUrl", configuration.DeepLBaseUrl, "https://api-free.deepl.com/v2",
            v => configuration.DeepLBaseUrl = v);

        var apiKey = configuration.DeepLApiKey;
        if (ImGui.InputText("API key", ref apiKey, 256, ImGuiInputTextFlags.Password))
        {
            configuration.DeepLApiKey = apiKey.Trim();
            configuration.Save();
        }
        ImGui.Spacing();

        ImGui.TextWrapped($"Target language: {configuration.TargetLanguage}  (set on the Language tab).");
        ImGui.Spacing();
        var informal = configuration.DeepLFormalityInformal;
        ImGui.Text("Register style:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Informal", informal))
        {
            configuration.DeepLFormalityInformal = true;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Default", !informal))
        {
            configuration.DeepLFormalityInformal = false;
            configuration.Save();
        }
    }

/// <summary>
///     Draws an endpoint URL the same way the client language is shown: the label
///     and the current value in muted coloured text. An "Edit" button switches to
///     an editable input for the (rare) custom-endpoint case; "Done" reverts to
///     the read-only display.
/// </summary>
private void DrawEndpointField(string label, string id, string current, string defaultValue, Action<string> setter)
{
    var editing = endpointEditToggles.Contains(id);
    var display = string.IsNullOrEmpty(current) ? defaultValue : current;

    if (!editing)
    {
        ImGui.Text(label + ":");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1f), display);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Edit##{id}Edit"))
        {
            endpointEditToggles.Add(id);
        }

        return;
    }

    ImGui.Text(label);
    ImGui.SameLine();
    ImGui.SetNextItemWidth(320f);
    var buffer = display;
    if (ImGui.InputText($"##{id}", ref buffer, 256))
    {
        setter(buffer.Trim().TrimEnd('/'));
        configuration.Save();
    }

    ImGui.SameLine();
    if (ImGui.SmallButton($"Done##{id}Done"))
    {
        endpointEditToggles.Remove(id);
        configuration.Save();
    }
}

private void DrawChatSharedFields()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!ImGui.CollapsingHeader("Advanced (optional)"))
        {
            return;
        }

        ImGui.TextDisabled("Not needed for most setups - these tweak how the model is asked to translate.");

        var temperature = configuration.EngineTemperature;
        if (ImGui.SliderFloat("Temperature", ref temperature, 0f, 2f))
        {
            configuration.EngineTemperature = temperature;
            configuration.Save();
        }

        var prompt = configuration.EnginePrompt;
        if (ImGui.InputTextMultiline("System prompt (optional)", ref prompt, 2048, new Vector2(0, 80)))
        {
            configuration.EnginePrompt = prompt;
            configuration.Save();
        }
        ImGui.TextDisabled("Leave empty for the default translation prompt.");
    }

    private void DrawAllowSelfSignedField()
    {
        ImGui.Spacing();

        var allowSelfSigned = configuration.EngineAllowSelfSignedHttps;
        if (ImGui.Checkbox("Allow self-signed / untrusted HTTPS (local endpoints)", ref allowSelfSigned))
        {
            configuration.EngineAllowSelfSignedHttps = allowSelfSigned;
            configuration.Save();
        }
    }

    private void DrawDisplayTab()
    {
        DrawOverlaySection();
    }

    private void DrawContentTab()
    {
        var translateNames = configuration.TranslateSpeakerNames;
        if (ImGui.Checkbox("Translate speaker names", ref translateNames))
        {
            configuration.TranslateSpeakerNames = translateNames;
            configuration.Save();
        }
        ImGui.TextDisabled("Speaker names shown above the translation (e.g. NPC names).");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped("Which kinds of in-game text to translate:");
        ImGui.Spacing();

        DrawSurfaceToggle("NPC dialogue box", configuration.TranslateTalk, v => configuration.TranslateTalk = v);
        DrawSurfaceToggle("Cutscene subtitle bar", configuration.TranslateTalkSubtitle, v => configuration.TranslateTalkSubtitle = v);
        DrawSurfaceToggle("Duty & event dialogue", configuration.TranslateBattleTalk, v => configuration.TranslateBattleTalk = v);
        DrawSurfaceToggle("World NPC chat bubbles", configuration.TranslateMiniTalk, v => configuration.TranslateMiniTalk = v);
        DrawSurfaceToggle("Dialogue choices", configuration.TranslateSelectString, v => configuration.TranslateSelectString = v);
    }

    private void DrawOverlaySection()
    {
        ImGui.TextWrapped("Position of the translation relative to the original text:");
        ImGui.Spacing();

        var above = configuration.OverlayAboveText;
        var onTop = configuration.OverlayOnTopOfText;

        if (ImGui.RadioButton("Above the original text", above))
        {
            configuration.OverlayAboveText = true;
            configuration.OverlayOnTopOfText = false;
            configuration.OverlayVerticalOffset = -35f;
            configuration.Save();
        }

        if (ImGui.RadioButton("On top of the original text", onTop))
        {
            configuration.OverlayAboveText = false;
            configuration.OverlayOnTopOfText = true;
            configuration.OverlayVerticalOffset = 0f;
            configuration.Save();
        }

        if (ImGui.RadioButton("Below the original text", !above && !onTop))
        {
            configuration.OverlayAboveText = false;
            configuration.OverlayOnTopOfText = false;
            configuration.OverlayVerticalOffset = 15f;
            configuration.Save();
        }

        ImGui.Spacing();

        var verticalOffset = configuration.OverlayVerticalOffset;
        if (ImGui.SliderFloat("Vertical offset", ref verticalOffset, -100f, 100f))
        {
            configuration.OverlayVerticalOffset = verticalOffset;
            configuration.Save();
        }
        ImGui.TextDisabled("Extra spacing between the translation and the original text (pixels).");

        ImGui.Spacing();

        var fontScale = configuration.OverlayFontScale;
        if (ImGui.SliderFloat("Font scale", ref fontScale, 0.5f, 2.0f))
        {
            configuration.OverlayFontScale = fontScale;
            configuration.Save();
        }

        var opacity = configuration.OverlayOpacity;
        if (ImGui.SliderFloat("Opacity", ref opacity, 0.1f, 1.0f))
        {
            configuration.OverlayOpacity = opacity;
            configuration.Save();
        }

        var maxWidth = configuration.OverlayMaxWidth;
        if (ImGui.SliderFloat("Max wrap width", ref maxWidth, 200f, 1600f))
        {
            configuration.OverlayMaxWidth = maxWidth;
            configuration.Save();
        }

        var background = configuration.OverlayShowBackground;
        if (ImGui.Checkbox("Draw background behind translated text", ref background))
        {
            configuration.OverlayShowBackground = background;
            configuration.Save();
        }

        if (configuration.OverlayShowBackground)
        {
            var bgOpacity = configuration.OverlayBackgroundOpacity;
            if (ImGui.SliderFloat("Background opacity", ref bgOpacity, 0.0f, 1.0f))
            {
                configuration.OverlayBackgroundOpacity = bgOpacity;
                configuration.Save();
            }
        }

        var placeholder = configuration.OverlayShowPlaceholder;
        if (ImGui.Checkbox("Show placeholder while translating", ref placeholder))
        {
            configuration.OverlayShowPlaceholder = placeholder;
            configuration.Save();
        }

        if (configuration.OverlayShowPlaceholder)
        {
            var placeholderText = configuration.OverlayPlaceholderText;
            if (ImGui.InputText("Placeholder text", ref placeholderText, 32))
            {
                configuration.OverlayPlaceholderText = placeholderText;
                configuration.Save();
            }
        }

        var replaceOriginal = configuration.OverlayReplaceOriginalText;
        if (ImGui.Checkbox("Replace original text in place (experimental)", ref replaceOriginal))
        {
            configuration.OverlayReplaceOriginalText = replaceOriginal;
            configuration.Save();
        }
        ImGui.TextDisabled("Overwrites the in-game text with the translation. World chat bubbles are not replaced.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var textColor = ArgbToVec4(configuration.OverlayTextColor);
        if (ImGui.ColorEdit4("Translation text color", ref textColor, ImGuiColorEditFlags.NoAlpha))
        {
            configuration.OverlayTextColor = Vec4ToArgb(textColor);
            configuration.Save();
        }

        if (configuration.TranslateSpeakerNames)
        {
            var nameColor = ArgbToVec4(configuration.OverlayNameColor);
            if (ImGui.ColorEdit4("Speaker name color", ref nameColor, ImGuiColorEditFlags.NoAlpha))
            {
                configuration.OverlayNameColor = Vec4ToArgb(nameColor);
                configuration.Save();
            }
        }
    }

    private void DrawSurfaceToggle(string label, bool value, Action<bool> setter)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            setter(value);
            configuration.Save();
        }
    }

    private void DrawDeveloperTab()
    {
        ImGui.TextUnformatted("For troubleshooting only - not needed for normal use.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawStatusRow("Engine ready", translationService.IsConfigured);
        DrawStatusRow("Translation active", plugin.IsTranslationActive());
        DrawStatusRow("Talk capture enabled", configuration.TranslateTalk);
        DrawStatusRow("Target language", configuration.TargetLanguage);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Live overlay probe"))
        {
            ImGui.TextDisabled("Dialogue addon / text node state as seen by the overlay drawer:");
            DrawStatusRow("Active surface", diagnostics.OverlaySurface ?? "none");
            DrawStatusRow("Addon found", diagnostics.AddonFound);
            DrawStatusRow("Addon visible", diagnostics.AddonVisible);
            DrawStatusRow("Text node found", diagnostics.TextNodeFound);

            if (diagnostics.NodeW > 0)
            {
                ImGui.Text($"Node position  X={diagnostics.NodeX:F0}  Y={diagnostics.NodeY:F0}  W={diagnostics.NodeW:F0}  H={diagnostics.NodeH:F0}");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "Node not located yet - is a dialogue box open?");
            }

            ImGui.Text($"Visible text in node: '{Truncate(talkCapture.ReadVisibleTalkText(), 60)}'");

            ImGui.Spacing();
            var scanAddon = scanAddonName ?? DialogueSurface.TalkAddonName;
            ImGui.SetNextItemWidth(180f);
            if (ImGui.BeginCombo("Scan addon", scanAddon))
            {
                foreach (var addonName in DialogueSurface.AllAddonNames)
                {
                    if (ImGui.Selectable(addonName, addonName == scanAddon))
                    {
                        scanAddonName = addonName;
                    }
                }

                ImGui.EndCombo();
            }

            if (ImGui.Button("Scan node tree"))
            {
                nodeScan = talkCapture.ScanAddonNodes(scanAddon);
                diagnostics.Log(nodeScan);
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Run with a dialogue box open");

            if (!string.IsNullOrEmpty(nodeScan))
            {
                ImGui.Spacing();
                ImGui.TextUnformatted(nodeScan);
            }

            if (!string.IsNullOrEmpty(diagnostics.OverlayLastSkipReason))
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), $"Why overlay is not drawing: {diagnostics.OverlayLastSkipReason}");
            }
        }

        if (ImGui.CollapsingHeader("Current dialogue line"))
        {
            if (talkCapture.OverlayState.TryGetLine(out var name, out var text, out var translated, out var translatedName, out var status, out var surface, out var isPending))
            {
                ImGui.Text($"Surface: {DialogueSurface.GetDisplayName(surface)}");
                ImGui.Text($"Speaker: {(string.IsNullOrEmpty(name) ? "(unknown)" : name)}");
                ImGui.TextWrapped($"Original: {text}");
                ImGui.TextWrapped($"Translation: {translated ?? (isPending ? "… translating" : "none")}");
                if (!string.IsNullOrEmpty(translatedName))
                {
                    ImGui.TextWrapped($"Translated speaker: {translatedName}");
                }
                if (!string.IsNullOrEmpty(status))
                {
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), $"Status: {status}");
                }
            }
            else
            {
                ImGui.TextDisabled("No dialogue line captured yet.");
                ImGui.TextDisabled("Open a conversation with an NPC or start a cutscene - a Talk refresh must fire.");
            }
        }

        if (ImGui.CollapsingHeader("Counters"))
        {
            ImGui.Text($"Talk refresh events:      {diagnostics.RefreshEvents}");
            ImGui.Text($"Lines captured:           {diagnostics.LinesCaptured}");
            ImGui.Text($"Translation requests:     {diagnostics.TranslationRequests}");
            ImGui.Text($"Translations succeeded:   {diagnostics.TranslationsSucceeded}");
            ImGui.Text($"Translations failed:      {diagnostics.TranslationsFailed}");
            ImGui.Text($"Overlay draw calls:       {diagnostics.OverlayDraws}");
            ImGui.Text($"Cache entries:            {translationService.CacheSize}");
            ImGui.SameLine();
            if (ImGui.Button("Clear cache"))
            {
                translationService.ClearCache();
            }
        }

        if (ImGui.CollapsingHeader("Event log"))
        {
            if (ImGui.Button("Copy log"))
            {
                var sb = new StringBuilder();
                foreach (var line in diagnostics.RecentLines())
                {
                    sb.AppendLine(line);
                }

                ImGui.SetClipboardText(sb.ToString());
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear log"))
            {
                diagnostics.Clear();
            }

            ImGui.Spacing();
            ImGui.BeginChild("DiagnosticsLog", new Vector2(0, 320), true);
            foreach (var line in diagnostics.RecentLines(0))
            {
                ImGui.TextUnformatted(line);
            }

            ImGui.EndChild();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Tip: if the counters never move, the plugin may not be loaded.");
        ImGui.TextDisabled("Check /xlplugins -> Installed plugins, or the game's console log for errors.");
    }

    private static void DrawStatusRow(string label, bool ok)
        => DrawStatusRow(label, ok ? "yes" : "no", ok);

    private static void DrawStatusRow(string label, string value, bool ok = true)
    {
        ImGui.Text(label + ":");
        ImGui.SameLine();
        if (ok)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.5f, 1f), value);
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), value);
        }
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "(empty)" : text.Length <= max ? text : text[..max] + "…";

    private static System.Numerics.Vector4 ArgbToVec4(uint argb)
        => new(
            ((argb >> 16) & 0xFF) / 255f,
            ((argb >> 8) & 0xFF) / 255f,
            (argb & 0xFF) / 255f,
            ((argb >> 24) & 0xFF) / 255f);

    private static uint Vec4ToArgb(System.Numerics.Vector4 v)
    {
        var a = (uint)Math.Clamp((int)(v.W * 255f), 0, 255);
        var r = (uint)Math.Clamp((int)(v.X * 255f), 0, 255);
        var g = (uint)Math.Clamp((int)(v.Y * 255f), 0, 255);
        var b = (uint)Math.Clamp((int)(v.Z * 255f), 0, 255);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}
