using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;
using Tomestone.Translate.Capture;
using Tomestone.Translate.Engines;

namespace Tomestone.Translate.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly TranslationService translationService;
    private readonly TalkCaptureService talkCapture;

    public MainWindow(Plugin plugin, TranslationService translationService, TalkCaptureService talkCapture)
        : base("Tomestone Translate###TomestoneTranslateMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.translationService = translationService;
        this.talkCapture = talkCapture;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 220),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    private static string EngineKindName(EngineKind kind)
        => kind switch
        {
            EngineKind.AnthropicClaude => "Anthropic Claude",
            EngineKind.GoogleGemini => "Google Gemini",
            EngineKind.DeepL => "DeepL",
            EngineKind.GoogleTranslate => "Google Translate",
            EngineKind.MyMemory => "MyMemory",
            _ => "OpenAI-compatible",
        };

    private void DrawStatusBanner(Configuration configuration)
    {
        if (!configuration.PluginEnabled)
        {
            DrawStatus("Inactive", "Translation is turned off.",
                new Vector4(1f, 0.5f, 0.3f, 1f), "Master switch is off. Enable it above to resume.");
            return;
        }

        if (configuration.DisableInsideInstance && Plugin.DutyState.IsDutyStarted)
        {
            DrawStatus("Inactive in instance", "You are inside a duty.",
                new Vector4(1f, 0.7f, 0.3f, 1f), "Translation will resume automatically when you leave the duty.");
            return;
        }

        if (TargetLanguageCodes.MatchesClientLanguage(Plugin.DataManager.Language, configuration.TargetLanguage))
        {
            DrawStatus("Inactive", "Target language matches the game client language.",
                new Vector4(1f, 0.7f, 0.3f, 1f), "No translation is needed - the text is already in the target language.");
            return;
        }

        DrawStatus("Active", "Translating dialogue.",
            new Vector4(0.4f, 0.9f, 0.5f, 1f), "Capture and overlay are enabled.");
    }

    private static void DrawStatus(string state, string title, Vector4 stateColor, string detail)
    {
        ImGui.Text("Status:");
        ImGui.SameLine();
        ImGui.TextColored(stateColor, state);
        ImGui.Text(title);
        ImGui.TextDisabled(detail);
    }

    public override void Draw()
    {
        var configuration = plugin.Configuration;

        ImGui.Text("Live AI translation for cutscene and NPC dialogue.");
        ImGui.Spacing();

        var enabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("Translation on", ref enabled))
        {
            configuration.PluginEnabled = enabled;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawStatusBanner(configuration);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Engine readiness
        ImGui.Text("Engine:");
        ImGui.SameLine();
        if (translationService.IsConfigured)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.5f, 1f), EngineKindName(configuration.EngineKind));
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "not configured");
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Set a provider and API key in Settings.");
        }

        ImGui.Text("Translating into:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1f), configuration.TargetLanguage);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCurrentLine();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Open Settings"))
        {
            plugin.ToggleConfigUi();
        }
    }

    private void DrawCurrentLine()
    {
        if (!talkCapture.OverlayState.TryGetLine(out var name, out var text, out var translated, out _, out _, out _, out var isPending))
        {
            ImGui.TextDisabled("No dialogue captured yet.");
            ImGui.TextDisabled("Open a conversation with an NPC or start a cutscene.");
            return;
        }

        if (!string.IsNullOrEmpty(name))
        {
            ImGui.Text($"Speaker: {name}");
        }

        ImGui.TextWrapped($"Original: {text}");
        if (translated != null)
        {
            ImGui.TextWrapped($"Translation: {translated}");
        }
        else if (isPending)
        {
            ImGui.TextDisabled("Translation: … translating");
        }
    }
}
