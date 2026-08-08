using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.IoC;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Text.Unicode;
using Tomestone.Translate.Capture;
using Tomestone.Translate.Debugging;
using Tomestone.Translate.Engines;
using Tomestone.Translate.Overlay;
using Tomestone.Translate.Windows;

namespace Tomestone.Translate;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;

    private const string MainCommand = "/tt";
    private const string ConfigCommand = "/ttconfig";

    public Configuration Configuration { get; init; }

    private readonly WindowSystem windowSystem = new("Tomestone.Translate");
    private readonly ConfigWindow configWindow;
    private readonly MainWindow mainWindow;

    private readonly TranslationService translationService;
    private readonly TalkCaptureService talkCapture;
    private readonly OverlayDrawer overlayDrawer;
    private readonly Diagnostics diagnostics = new();
    private readonly IFontHandle overlayFont;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        translationService = new TranslationService(Configuration, Log, diagnostics, DataManager.Language);
        talkCapture = new TalkCaptureService(AddonLifecycle, GameGui, Log, translationService, Configuration, diagnostics);

        overlayFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var cfg = new SafeFontConfig { SizePx = 20f };
            cfg.GlyphRanges = ImGuiHelpers.CreateImGuiRangesFrom(new[]
            {
                UnicodeRanges.BasicLatin,
                UnicodeRanges.GeneralPunctuation,
                UnicodeRanges.HangulSyllables,
                UnicodeRanges.HangulJamo,
                UnicodeRanges.HangulCompatibilityJamo,
                UnicodeRanges.CjkUnifiedIdeographs,
                UnicodeRanges.CjkUnifiedIdeographsExtensionA,
                UnicodeRanges.Hiragana,
                UnicodeRanges.Katakana,
                new UnicodeRange(0x3000, 0x40),
                new UnicodeRange(0xFF00, 0xF0),
            });
            cfg.MergeFont = tk.AddFontFromFile(@"C:\Windows\Fonts\malgun.ttf", cfg);
            tk.AttachExtraGlyphsForDalamudLanguage(cfg);
            tk.Font = cfg.MergeFont;
        }));

        overlayDrawer = new OverlayDrawer(Configuration, talkCapture, diagnostics, overlayFont);

        configWindow = new ConfigWindow(this, translationService, talkCapture, diagnostics, DataManager.Language);
        mainWindow = new MainWindow(this, translationService, talkCapture);

        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(MainCommand, new CommandInfo(OnMain)
        {
            HelpMessage = "Open the Tomestone Translate main window."
        });

        CommandManager.AddHandler(ConfigCommand, new CommandInfo(OnConfigCommand)
        {
            HelpMessage = "Open the Tomestone Translate settings window."
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.Draw += overlayDrawer.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

#if DEBUG
        var sanitizeError = DialogueSurface.SanitizeSelfCheck();
        if (sanitizeError != null)
        {
            diagnostics.Log($"[SanitizeSelfCheck] FAIL: {sanitizeError}");
        }
#endif

        Log.Information($"{PluginInterface.Manifest.Name} (v{PluginInterface.Manifest.AssemblyVersion}) loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= overlayDrawer.Draw;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        windowSystem.RemoveAllWindows();

        configWindow.Dispose();
        mainWindow.Dispose();

        CommandManager.RemoveHandler(MainCommand);
        CommandManager.RemoveHandler(ConfigCommand);

        talkCapture.Dispose();
        translationService.Dispose();
        overlayFont.Dispose();
        overlayDrawer.Dispose();

        Log.Information($"{PluginInterface.Manifest.Name} unloaded.");
    }

    private void OnMain(string command, string args) => mainWindow.Toggle();

    private void OnConfigCommand(string command, string args) => configWindow.Toggle();

    public void ToggleConfigUi() => configWindow.Toggle();

    public void ToggleMainUi() => mainWindow.Toggle();

    /// <summary>True when translation should actually run: the master switch is on
    /// and (unless disabled) we are not standing inside an instanced duty.</summary>
    public bool IsTranslationActive()
        => IsTranslationActive(Configuration);

    /// <summary>Static gate used by capture/overlay code that only has a config.</summary>
    public static bool IsTranslationActive(Configuration config)
    {
        if (!config.PluginEnabled)
        {
            return false;
        }

        if (TargetLanguageCodes.MatchesClientLanguage(DataManager.Language, config.TargetLanguage))
        {
            return false;
        }

        return !(config.DisableInsideInstance && DutyState.IsDutyStarted);
    }
}