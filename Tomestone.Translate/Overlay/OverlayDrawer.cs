using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;
using System.Text;
using System.Threading;
using Tomestone.Translate.Capture;
using Tomestone.Translate.Debugging;

namespace Tomestone.Translate.Overlay;

/// <summary>
///     Draws the translated dialogue text over/above the original text using
///     the game's own node screen coordinates (AtkResNode.ScreenX/ScreenY).
///     Works across every dialogue surface (Talk, TalkSubtitle, _BattleTalk,
///     _MiniTalk) by drawing on whichever addon is currently visible.
/// </summary>
public sealed class OverlayDrawer
{
    private readonly Configuration configuration;
    private readonly TalkCaptureService talkCapture;
    private readonly Diagnostics diagnostics;
    private readonly IFontHandle overlayFont;
    private readonly TextNodeBuffer textNodeBuffer = new();

    public OverlayDrawer(Configuration configuration, TalkCaptureService talkCapture, Diagnostics diagnostics, IFontHandle overlayFont)
    {
        this.configuration = configuration;
        this.talkCapture = talkCapture;
        this.diagnostics = diagnostics;
        this.overlayFont = overlayFont;
    }

    /// <summary>Breaks down why the master gate is off, for the Developer tab.</summary>
    private string GateOffReason()
    {
        var reason = string.Empty;
        if (!configuration.PluginEnabled)
        {
            reason += "/tt disabled; ";
        }

        if (TargetLanguageCodes.MatchesClientLanguage(Plugin.DataManager.Language, configuration.TargetLanguage))
        {
            reason += $"target==client (Target={configuration.TargetLanguage}, Client={Plugin.DataManager.Language}); ";
        }

        if (configuration.DisableInsideInstance && Plugin.DutyState.IsDutyStarted)
        {
            reason += "inside instance; ";
        }

        return string.IsNullOrEmpty(reason)
            ? "MASTER GATE OFF (unknown reason)"
            : $"MASTER GATE OFF: {reason.TrimEnd(' ', ';')}";
    }

    public unsafe void Draw()
    {
        Interlocked.Increment(ref diagnostics.OverlayDraws);

        if (!Plugin.IsTranslationActive(configuration))
        {
            diagnostics.OverlaySurfaceCheck = GateOffReason();
            diagnostics.AddonFound = false;
            diagnostics.AddonVisible = false;
            diagnostics.TextNodeFound = false;
            diagnostics.OverlayLastSkipReason = "Translation disabled (/tt off or inside instance)";
            return;
        }

        // Walk surfaces in priority order, poll the first visible + enabled one
        // (so its changing text is captured), then draw the resulting line on it.
        var surfaces = new[]
        {
            DialogueSurfaceKind.Talk,
            DialogueSurfaceKind.BattleTalk,
            DialogueSurfaceKind.TalkSubtitle,
            DialogueSurfaceKind.MiniTalk,
            DialogueSurfaceKind.JournalAccept,
            DialogueSurfaceKind.JournalDetail,
        };

        var check = new StringBuilder();
        DialogueSurfaceKind? activeSurface = null;
        AtkUnitBase* activeAddon = null;
        foreach (var surface in surfaces)
        {
            if (!TalkCaptureService.IsSurfaceEnabled(configuration, surface))
            {
                check.Append(surface).Append(":off, ");
                continue;
            }

            if (surface == DialogueSurfaceKind.MiniTalk)
            {
                if (talkCapture.GetVisibleMiniTalks().Count == 0)
                {
                    check.Append("MiniTalk:none, ");
                    continue;
                }

                activeSurface = surface;
                activeAddon = (AtkUnitBase*)talkCapture.GetVisibleMiniTalks()[0];
                check.Append("MiniTalk:found+visible, ");
            }
            else if (!talkCapture.TryGetAddon(surface, out var addon))
            {
                check.Append(surface).Append(":notfound, ");
                continue;
            }
            else
            {
                activeSurface = surface;
                activeAddon = addon;
                check.Append(surface).Append(":found").Append(addon->IsVisible ? "+visible" : "+hidden").Append(", ");
            }

            break;
        }

        diagnostics.OverlaySurfaceCheck = check.Length > 0 ? check.ToString().TrimEnd(' ', ',') : "no surfaces";

        if (activeSurface == null || activeAddon == null)
        {
            diagnostics.AddonFound = false;
            diagnostics.AddonVisible = false;
            diagnostics.OverlayLastSkipReason = "No dialogue addon present/visible";
            return;
        }

        diagnostics.AddonFound = true;
        diagnostics.AddonVisible = activeAddon->IsVisible;

        if (activeSurface == DialogueSurfaceKind.MiniTalk)
        {
            DrawMiniTalkOverlays(activeAddon);
            return;
        }

        talkCapture.PollActiveSurface(activeAddon, activeSurface.Value);

        if (!talkCapture.OverlayState.TryGetLine(out _, out var originalText, out var translated, out var translatedName, out var status, out var isPending))
        {
            diagnostics.OverlayLastSkipReason = "No line captured yet";
            return;
        }

        var textNode = talkCapture.GetTextNodeForLine(activeSurface.Value, activeAddon, originalText);
            diagnostics.TextNodeFound = textNode != null;
            diagnostics.OverlaySurface = DialogueSurface.GetDisplayName(activeSurface.Value);

        if (textNode == null)
        {
            diagnostics.OverlayLastSkipReason = "No rendered text node matches the captured line";
            return;
        }

        diagnostics.NodeX = textNode->ScreenX;
        diagnostics.NodeY = textNode->ScreenY;
        diagnostics.NodeW = textNode->Width;
        diagnostics.NodeH = textNode->Height;

        // In-place replacement: show the original text until the translation is
        // ready, then overwrite the node. Falls back to the box overlay when the
        // surface can't be replaced or the translation is still pending/failed.
        if (configuration.OverlayReplaceOriginalText && DialogueSurface.SupportsTextReplacement(activeSurface.Value))
        {
            if (translated == null)
            {
                // Leave the original text in the node; no placeholder replacement.
                diagnostics.OverlayLastSkipReason = "Replacement pending translation";
                return;
            }

            if (string.IsNullOrEmpty(translated))
            {
                return;
            }

            // Ensure a stale translation is never left behind: if the node no
            // longer matches the original line, do not touch it.
            if (DialogueSurface.ReadCleanText(textNode) != originalText && !string.IsNullOrEmpty(originalText))
            {
                diagnostics.OverlayLastSkipReason = "Node text changed during translation";
                return;
            }

            var maxPx = textNode->Width * textNode->ScaleX;
            var written = textNodeBuffer.SetText(textNode, NormalizeLineBreaks(translated), maxPx);
            talkCapture.NoteInjectedText(activeSurface.Value, written);
            diagnostics.OverlayLastSkipReason = string.Empty;
            return;
        }

        if (translated == null)
        {
            if (!isPending && !string.IsNullOrEmpty(diagnostics.LastError))
            {
                translated = $"! {diagnostics.LastError}";
            }
            else if (!isPending || !configuration.OverlayShowPlaceholder)
            {
                diagnostics.OverlayLastSkipReason = "No translation yet (pending or failed); placeholder disabled";
                return;
            }
            else
            {
                translated = configuration.OverlayPlaceholderText;
            }
        }

        if (string.IsNullOrEmpty(translated))
        {
            return;
        }

        diagnostics.OverlayLastSkipReason = string.Empty;

        if (activeSurface.Value == DialogueSurfaceKind.JournalAccept)
        {
            var anchor = GetQuestAcceptButtonNode(activeAddon);
            DrawOverlay(anchor == null ? (AtkResNode*)textNode : anchor, translated, translatedName, 25f, forceBelow: true, sizeAnchor: (AtkResNode*)textNode, xOffset: -40f);
        }
        else if (activeSurface.Value == DialogueSurfaceKind.JournalDetail)
        {
            var anchor = activeAddon->GetTextNodeById(33);
            DrawOverlay(anchor == null ? (AtkResNode*)textNode : (AtkResNode*)anchor, translated, translatedName, 0f, forceRight: true, sizeAnchor: (AtkResNode*)textNode, xOffset: 50f);
        }
        else
        {
            DrawOverlay((AtkResNode*)textNode, translated, translatedName, configuration.OverlayVerticalOffset);
        }
    }

    private unsafe void DrawMiniTalkOverlays(AtkUnitBase* addon)
    {
        foreach (var instance in talkCapture.GetVisibleMiniTalks())
        {
            var bubbler = (FFXIVClientStructs.FFXIV.Client.UI.AddonMiniTalk*)instance;
            talkCapture.PollMiniTalkBubbles(bubbler);
        }

        foreach (var bubble in talkCapture.GetMiniBubbleViews())
        {
            var translated = bubble.Translated;
            if (translated == null)
            {
                if (!bubble.Pending || !configuration.OverlayShowPlaceholder)
                {
                    continue;
                }

                translated = configuration.OverlayPlaceholderText;
            }

            if (string.IsNullOrEmpty(translated))
            {
                continue;
            }

            var textNode = (AtkTextNode*)bubble.TextNodePtr;
            if (textNode == null)
            {
                continue;
            }

            diagnostics.NodeX = textNode->ScreenX;
            diagnostics.NodeY = textNode->ScreenY;
            diagnostics.NodeW = textNode->Width;
            diagnostics.NodeH = textNode->Height;
            DrawOverlay((AtkResNode*)textNode, translated, bubble.TranslatedName, 0f);
        }

        diagnostics.OverlaySurface = DialogueSurface.GetDisplayName(DialogueSurfaceKind.MiniTalk);
    }

    private unsafe void DrawOverlay(AtkResNode* anchor, string translated, string? translatedName, float verticalOffset, bool forceBelow = false, AtkResNode* sizeAnchor = null, float xOffset = 0f, bool forceRight = false)
    {
        translated = NormalizeLineBreaks(translated);
        if (translatedName != null)
        {
            translatedName = NormalizeLineBreaks(translatedName);
        }

        var hasName = !string.IsNullOrWhiteSpace(translatedName);
        var display = hasName ? translatedName + "\n" + translated : translated;

        var scale = Math.Max(configuration.OverlayFontScale, 0.1f);
        var pos = new Vector2(anchor->ScreenX + xOffset, anchor->ScreenY);
        var sizeNode = sizeAnchor == null ? anchor : sizeAnchor;
        var nodeWidth = Math.Max(sizeNode->Width, 100f);
        var nodeHeight = Math.Max(anchor->Height, 1f);

        var wrapWidthUi = Math.Clamp(nodeWidth, 120f, configuration.OverlayMaxWidth);
        var padding = 6f * scale;

        using (overlayFont.Push())
        {
            // Measure with the overlay font (which includes CJK glyphs); the wrap
            // width is expressed in font pixels, then scaled back up for the box.
            var wrapWidthFont = wrapWidthUi / scale;
            var textSizeFont = ImGui.CalcTextSize(display, false, wrapWidthFont);
            // The window's own WindowPadding (set below) is (padding, padding), so a
            // top margin of `padding` is inherent. Adding 3.5*padding to the height
            // leaves ~1.5x that margin below the text for a balanced look.
            var boxSize = new Vector2(wrapWidthUi + padding * 2f, textSizeFont.Y * scale + padding * 3.5f);

            var boxPos = pos;
            if (forceRight)
            {
                boxPos.X += nodeWidth + padding;
            }
            else if (forceBelow)
            {
                boxPos.Y += nodeHeight + padding;
            }
            else if (configuration.OverlayAboveText)
            {
                boxPos.Y -= boxSize.Y;
            }
            else if (!configuration.OverlayOnTopOfText)
            {
                boxPos.Y += nodeHeight + padding;
            }

            boxPos.Y += verticalOffset;

            if (configuration.OverlayAboveText && hasName)
            {
                boxPos.Y -= 30f;
            }

            var flags = ImGuiWindowFlags.NoDecoration
                        | ImGuiWindowFlags.NoInputs
                        | ImGuiWindowFlags.NoMove
                        | ImGuiWindowFlags.NoFocusOnAppearing
                        | ImGuiWindowFlags.NoNav
                        | ImGuiWindowFlags.NoSavedSettings
                        | ImGuiWindowFlags.NoResize
                        | ImGuiWindowFlags.NoScrollbar;

            if (!configuration.OverlayShowBackground)
            {
                flags |= ImGuiWindowFlags.NoBackground;
            }

            // The box background uses ImGui's default WindowBg color, with its own
            // opacity so the background can be faded independently of the text.
            var bgOpacity = configuration.OverlayBackgroundOpacity;
            if (configuration.OverlayShowBackground && bgOpacity < 1f)
            {
                var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
                bg.W = bgOpacity;
                ImGui.PushStyleColor(ImGuiCol.WindowBg, bg);
            }

            // Match the window's own content padding to the box padding so the
            // background leaves an even margin on every side (including bottom).
            // Without this, ImGui's default padding makes the text wrap at a
            // different width than was measured, overflowing the box bottom.
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));

            ImGui.SetNextWindowPos(boxPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(boxSize, ImGuiCond.Always);

            if (ImGui.Begin("###TomestoneTalkOverlay", flags))
            {
                ImGui.SetWindowFontScale(scale);

                if (hasName)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorWithAlpha(configuration.OverlayNameColor, configuration.OverlayOpacity));
                    ImGui.TextWrapped(translatedName);
                    ImGui.PopStyleColor();
                }

                ImGui.PushStyleColor(ImGuiCol.Text, ColorWithAlpha(configuration.OverlayTextColor, configuration.OverlayOpacity));
                ImGui.TextWrapped(translated);
                ImGui.PopStyleColor();
            }

            ImGui.End();
            if (configuration.OverlayShowBackground && configuration.OverlayBackgroundOpacity < 1f)
            {
                ImGui.PopStyleColor();
            }

            ImGui.PopStyleVar();
        }
    }

    /// <summary>Returns the Accept button's node of a JournalAccept addon, or null.</summary>
    private static unsafe AtkResNode* GetQuestAcceptButtonNode(AtkUnitBase* addon)
    {
        var journal = (AddonJournalAccept*)addon;
        if (journal->AcceptButton == null)
        {
            return null;
        }

        var owner = journal->AcceptButton->AtkComponentBase.OwnerNode;
        return owner == null ? null : (AtkResNode*)&owner->AtkResNode;
    }

    private static string NormalizeLineBreaks(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static uint ColorWithAlpha(uint color, float alpha)
    {
        var a = (byte)Math.Clamp((int)(alpha * 255f), 0, 255);
        return ((uint)a << 24) | (color & 0x00FFFFFF);
    }

    public void Dispose()
    {
        textNodeBuffer.Dispose();
    }
}
