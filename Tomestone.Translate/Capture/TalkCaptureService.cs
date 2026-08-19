using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tomestone.Translate.Debugging;
using Tomestone.Translate.Engines;

namespace Tomestone.Translate.Capture;

/// <summary>
///     Captures dialogue text from every known dialogue addon (the <c>Talk</c>
///     box, cutscene subtitles, battle/duty chatter and world chat bubbles) and
///     drives live translation of each line. Only one dialogue surface is
///     typically visible at a time, so a single "current line" model is shared;
///     the originating surface is tracked so the overlay can anchor itself to
///     the right addon's text node.
/// </summary>
public sealed class TalkCaptureService : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly TranslationService translationService;
    private readonly Configuration configuration;
    private readonly Diagnostics diagnostics;

    private nint trackedAddonPtr;
    private nint trackedNodePtr;

    // Per-surface stability gate for the frame-based poller.
    private readonly Dictionary<string, string?> stableCandidates = new();
    private readonly Dictionary<string, int> stableFrames = new();

    // Tracks text the in-place feature has written into a surface's text node, so
    // the next read (which sees the partially-translated or fully-translated text)
    // is not mistaken for a brand-new original line and re-translated.
    private readonly Dictionary<DialogueSurfaceKind, string?> injectedTexts = new();

    // Per-bubble state for _MiniTalk, keyed by the bubble's text-node pointer.
    private readonly object miniBubbleLock = new();
    private readonly Dictionary<nint, MiniBubbleState> miniBubbles = new();
    private readonly Dictionary<nint, (string Peek, int Frames)> miniStable = new();
    private int miniGeneration;

    /// <summary>Snapshot of a single _MiniTalk bubble's live translation, for the overlay.</summary>
    public sealed class MiniBubbleView
    {
        public nint TextNodePtr = 0;
        public string OriginalText = string.Empty;
        public string? Translated;
        public string? TranslatedName;
        public string Status = string.Empty;
        public bool Pending;
    }

    private sealed class MiniBubbleState
    {
        public nint TextNode;
        public string OriginalText = string.Empty;
        public string? Translated;
        public int Generation;
    }

    private readonly Dictionary<string, DialogueSurfaceKind> surfaceByAddon = new()
    {
        { DialogueSurface.TalkAddonName, DialogueSurfaceKind.Talk },
        { DialogueSurface.TalkSubtitleAddonName, DialogueSurfaceKind.TalkSubtitle },
        { DialogueSurface.BattleTalkAddonName, DialogueSurfaceKind.BattleTalk },
        { DialogueSurface.MiniTalkAddonName, DialogueSurfaceKind.MiniTalk },
        { DialogueSurface.SelectStringAddonName, DialogueSurfaceKind.SelectString },
    };

    public DialogueOverlayState OverlayState { get; } = new();

    public TalkCaptureService(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog log,
        TranslationService translationService,
        Configuration configuration,
        Diagnostics diagnostics)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.log = log;
        this.translationService = translationService;
        this.configuration = configuration;
        this.diagnostics = diagnostics;

        foreach (var addonName in DialogueSurface.AllAddonNames)
        {
            addonLifecycle.RegisterListener(AddonEvent.PostRefresh, addonName, OnAddonRefresh);
            addonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, OnAddonReset);
            addonLifecycle.RegisterListener(AddonEvent.PreHide, addonName, OnAddonReset);
        }
    }

    public bool IsCaptureEnabled => Plugin.IsTranslationActive(configuration) && configuration.TranslateTalk;

    public static bool IsSurfaceEnabled(Configuration config, DialogueSurfaceKind surface)
    {
        if (!Plugin.IsTranslationActive(config))
        {
            return false;
        }

        return surface switch
        {
            DialogueSurfaceKind.Talk => config.TranslateTalk,
            DialogueSurfaceKind.TalkSubtitle => config.TranslateTalkSubtitle,
            DialogueSurfaceKind.BattleTalk => config.TranslateBattleTalk,
            DialogueSurfaceKind.MiniTalk => config.TranslateMiniTalk,
            DialogueSurfaceKind.SelectString => config.TranslateSelectString,
            DialogueSurfaceKind.JournalAccept => config.TranslateQuestWindow,
            DialogueSurfaceKind.JournalDetail => config.TranslateQuestDetail,
            _ => false,
        };
    }

    public static DialogueSurfaceKind KindForAddonName(string addonName)
        => addonName switch
        {
            DialogueSurface.TalkAddonName => DialogueSurfaceKind.Talk,
            DialogueSurface.TalkSubtitleAddonName => DialogueSurfaceKind.TalkSubtitle,
            DialogueSurface.BattleTalkAddonName => DialogueSurfaceKind.BattleTalk,
            DialogueSurface.MiniTalkAddonName => DialogueSurfaceKind.MiniTalk,
            DialogueSurface.SelectStringAddonName => DialogueSurfaceKind.SelectString,
            _ => DialogueSurfaceKind.Talk,
        };

    public unsafe bool TryGetTalkAddon(out AddonTalk* addon)
    {
        if (TryGetAddon(DialogueSurfaceKind.Talk, out var generic))
        {
            addon = (AddonTalk*)generic;
            return true;
        }

        addon = null;
        return false;
    }

    public unsafe bool TryGetAddon(DialogueSurfaceKind surface, out AtkUnitBase* addon)
    {
        var addonWrapper = gameGui.GetAddonByName(DialogueSurface.GetAddonName(surface));
        if (addonWrapper.IsNull)
        {
            addon = null;
            return false;
        }

        addon = (AtkUnitBase*)addonWrapper.Address;
        return addon != null && addon->IsVisible;
    }

    /// <summary>Reads the text currently rendered for a dialogue surface (for diagnostics).</summary>
    public unsafe string ReadVisibleSurfaceText(DialogueSurfaceKind surface)
    {
        if (!TryGetAddon(surface, out var addon))
        {
            return string.Empty;
        }

        var node = FindTextNodeForLine(addon, DialogueSurface.GetTextNodeId(surface), string.Empty, findAny: true);
        return node == null ? string.Empty : DialogueSurface.ReadCleanText(node);
    }

    /// <summary>Reads the text currently rendered in the Talk text box (for diagnostics).</summary>
    public unsafe string ReadVisibleTalkText()
        => ReadVisibleSurfaceText(DialogueSurfaceKind.Talk);

    /// <summary>
    ///     Finds the rendered text node currently displaying <paramref name="originalText"/>
    ///     for a given surface. The result is cached for the current addon instance and
    ///     re-searched automatically when the displayed text changes. Falls back to the
    ///     first non-empty text node.
    /// </summary>
    public unsafe AtkTextNode* GetTextNodeForLine(AddonTalk* addon, string originalText)
    {
        var node = GetTextNodeForLine(DialogueSurfaceKind.Talk, (AtkUnitBase*)addon, originalText);
        return node;
    }

    /// <summary>
    ///     Finds the rendered text node currently displaying <paramref name="originalText"/>
    ///     for a given surface. The result is cached for the current addon instance and
    ///     re-searched automatically when the displayed text changes. Falls back to the
    ///     first non-empty text node.
    /// </summary>
    public unsafe AtkTextNode* GetTextNodeForLine(DialogueSurfaceKind surface, AtkUnitBase* addon, string originalText)
    {
        if (addon == null)
        {
            trackedNodePtr = 0;
            trackedAddonPtr = 0;
            return null;
        }

        var addonPtr = (nint)addon;

        if (surface == DialogueSurfaceKind.MiniTalk)
        {
            var miniNode = FindMiniTalkTextNode((AddonMiniTalk*)addon, originalText);
            trackedNodePtr = miniNode != null ? (nint)miniNode : 0;
            trackedAddonPtr = addonPtr;
            return miniNode;
        }

        if (surface == DialogueSurfaceKind.JournalAccept)
        {
            var canvasText = GetJournalCanvasDescriptionNode(((AddonJournalAccept*)addon)->JournalCanvas);
            trackedNodePtr = canvasText != null ? (nint)canvasText : 0;
            trackedAddonPtr = addonPtr;
            return canvasText;
        }

        if (surface == DialogueSurfaceKind.JournalDetail)
        {
            var canvasText = GetJournalCanvasDescriptionNode(((AddonJournalDetail*)addon)->JournalCanvasNode);
            trackedNodePtr = canvasText != null ? (nint)canvasText : 0;
            trackedAddonPtr = addonPtr;
            return canvasText;
        }

        if (trackedNodePtr != 0 && trackedAddonPtr == addonPtr)
        {
            var cached = (AtkTextNode*)trackedNodePtr;
            if (DialogueSurface.ReadCleanText(cached) == originalText)
            {
                return cached;
            }
        }

        var node = FindTextNodeForLine(addon, DialogueSurface.GetTextNodeId(surface), originalText, findAny: true);

        trackedNodePtr = node != null ? (nint)node : 0;
        trackedAddonPtr = addonPtr;
        return node;
    }

    /// <summary>
    ///     Scans the given addon's nodes (by id first, then by walking the tree
    ///     to cover text nodes nested inside component nodes) for the text node
    ///     whose text matches <paramref name="originalText"/>. Falls back to the
    ///     first non-empty text node when <paramref name="findAny"/> is set.
    /// </summary>
    private static unsafe AtkTextNode* FindTextNodeForLine(AtkUnitBase* addon, uint preferredNodeId, string originalText, bool findAny)
    {
        if (preferredNodeId != 0)
        {
            var preferred = addon->GetTextNodeById(preferredNodeId);
            if (preferred != null && (string.IsNullOrEmpty(originalText) || DialogueSurface.ReadCleanText(preferred) == originalText))
            {
                return preferred;
            }
        }

        AtkTextNode* best = null;
        AtkTextNode* anyTextNode = null;

        for (uint id = 1; id <= 128; id++)
        {
            var node = addon->GetTextNodeById(id);
            if (node == null)
            {
                continue;
            }

            if (anyTextNode == null)
            {
                anyTextNode = node;
            }

            var text = DialogueSurface.ReadCleanText(node);
            if (!string.IsNullOrEmpty(originalText) && text == originalText)
            {
                best = node;
                break;
            }

            if (best == null && !string.IsNullOrEmpty(text))
            {
                best = node;
            }
        }

        // Text nodes inside components (e.g. _MiniTalk bubbles) are not found by
        // GetTextNodeById; walk the tree to reach them.
        if (best == null)
        {
            best = WalkForBestTextNode(addon->RootNode, originalText);
        }

        var found = best;
        if (found == null)
        {
            found = anyTextNode;
        }

        if (found == null && findAny)
        {
            found = WalkForBestTextNode(addon->RootNode, string.Empty);
        }

        return found;
    }

    private static unsafe AtkTextNode* WalkForBestTextNode(AtkResNode* node, string originalText)
    {
        AtkTextNode* bestMatch = null;
        AtkTextNode* bestAny = null;
        var bestMatchLen = 0;
        var bestAnyLen = 0;

        WalkNode(node, ref bestMatch, ref bestAny, ref bestMatchLen, ref bestAnyLen, originalText);
        return bestMatch != null ? bestMatch : bestAny;
    }

    private static unsafe void WalkNode(
        AtkResNode* node,
        ref AtkTextNode* bestMatch,
        ref AtkTextNode* bestAny,
        ref int bestMatchLen,
        ref int bestAnyLen,
        string originalText)
    {
        while (node != null)
        {
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = DialogueSurface.ReadCleanText(textNode);
                if (!string.IsNullOrEmpty(text))
                {
                    if (!string.IsNullOrEmpty(originalText) && text == originalText && text.Length > bestMatchLen)
                    {
                        bestMatch = textNode;
                        bestMatchLen = text.Length;
                    }

                    if (text.Length > bestAnyLen)
                    {
                        bestAny = textNode;
                        bestAnyLen = text.Length;
                    }
                }
            }
            else if (node->Type == NodeType.Component)
            {
                var componentNode = (AtkComponentNode*)node;
                if (componentNode->Component != null && componentNode->Component->UldManager.RootNode != null)
                {
                    WalkNode(
                        componentNode->Component->UldManager.RootNode,
                        ref bestMatch,
                        ref bestAny,
                        ref bestMatchLen,
                        ref bestAnyLen,
                        originalText);
                }
            }

            if (node->ChildCount > 0)
            {
                WalkNode(node->ChildNode, ref bestMatch, ref bestAny, ref bestMatchLen, ref bestAnyLen, originalText);
            }

            node = node->NextSiblingNode;
        }
    }

    /// <summary>
    ///     Builds a text report of any addon's node tree so the current client's
    ///     structure can be diagnosed in-game.
    /// </summary>
    public unsafe string ScanAddonNodes(string addonName)
    {
        var sb = new StringBuilder();
        var addonWrapper = gameGui.GetAddonByName(addonName);
        if (addonWrapper.IsNull)
        {
            sb.AppendLine($"{addonName} addon: NOT FOUND");
            return sb.ToString();
        }

        var addon = (AtkUnitBase*)addonWrapper.Address;
        sb.AppendLine($"{addonName} addon: found (0x{(nint)addon:X})");
        sb.AppendLine($"IsVisible: {addon->IsVisible}");
        sb.AppendLine($"RootNode: 0x{(nint)addon->RootNode:X}");

        if (addonName == DialogueSurface.MiniTalkAddonName)
        {
            var miniTalk = (AddonMiniTalk*)addon;
            var bubbles = miniTalk->TalkBubbles;
            sb.AppendLine($"TalkBubbles count: {bubbles.Length}");
            for (var i = 0; i < bubbles.Length; i++)
            {
                var node = bubbles[i].BubbleTextNode;
                var text = node == null ? string.Empty : ReadUtf8NodeText(node);
                sb.AppendLine($"  bubble[{i}] text='{Truncate(text, 40)}' (len={text.Length})");
            }
        }

        if (addonName == DialogueSurface.JournalAcceptAddonName)
        {
            var journal = (AddonJournalAccept*)addon;
            var canvas = journal->JournalCanvasText;
            sb.AppendLine($"JournalCanvasText: {(canvas == null ? "null" : $"'{Truncate(ReadUtf8NodeText(canvas), 120)}' (len={ReadUtf8NodeText(canvas).Length})")}");
            DumpJournalCanvas(sb, journal->JournalCanvas);
        }

        if (addonName == DialogueSurface.JournalDetailAddonName)
        {
            var journal = (AddonJournalDetail*)addon;
            var node33 = journal->AtkUnitBase.GetTextNodeById(33);
            var node33Text = node33 == null ? string.Empty : ReadUtf8NodeText(node33);
            sb.AppendLine($"GetTextNodeById(33): '{(node33 == null ? "null" : Truncate(node33Text, 120))}' (len={node33Text.Length})");
            DumpJournalCanvas(sb, journal->JournalCanvasNode);
        }

        var histogram = new int[16];
        var total = CountNodes(addon->RootNode, histogram);
        sb.AppendLine($"Tree walk total nodes: {total}");
        sb.AppendLine($"Type histogram: {string.Join(",", histogram)}");

        var foundAny = false;
        for (uint id = 0; id < 256; id++)
        {
            var node = addon->GetTextNodeById(id);
            if (node == null)
            {
                continue;
            }

            foundAny = true;
            var text = ReadUtf8NodeText(node);
            sb.AppendLine($"  GetTextNodeById({id}): type={(byte)node->Type} text='{Truncate(text, 40)}' (len={text.Length})");
            if (!string.IsNullOrEmpty(text))
            {
                sb.AppendLine($"      raw={EscapeForDiagnostics(text)}");
            }
        }

        if (!foundAny)
        {
            sb.AppendLine("GetTextNodeById(0..255): no text nodes returned");
        }

        sb.AppendLine("  Tree-walk text nodes:");
        var walked = WalkAndCollectTextNodes(addon->RootNode);
        foreach (var (nodeId, text) in walked)
        {
            sb.AppendLine($"    id={nodeId} text='{Truncate(text, 40)}' (len={text.Length})");
            sb.AppendLine($"      raw={EscapeForDiagnostics(text)}");
        }

        if (walked.Count == 0)
        {
            sb.AppendLine("    (none)");
        }

        return sb.ToString();
    }

    /// <summary>Scans the Talk addon (kept for backward-compat with existing diagnostics).</summary>
    public unsafe string ScanTalkNodes() => ScanAddonNodes(DialogueSurface.TalkAddonName);

    private static unsafe List<(uint, string)> WalkAndCollectTextNodes(AtkResNode* node)
    {
        var result = new List<(uint, string)>();
        CollectTextNodes(node, result);
        return result;
    }

    private static unsafe void CollectTextNodes(AtkResNode* node, List<(uint, string)> result)
    {
        while (node != null)
        {
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = ReadUtf8NodeText(textNode);
                if (!string.IsNullOrEmpty(text))
                {
                    result.Add((node->NodeId, text));
                }
            }
            else if (node->Type == NodeType.Component)
            {
                var componentNode = (AtkComponentNode*)node;
                if (componentNode->Component != null && componentNode->Component->UldManager.RootNode != null)
                {
                    CollectTextNodes(componentNode->Component->UldManager.RootNode, result);
                }
            }

            if (node->ChildCount > 0)
            {
                CollectTextNodes(node->ChildNode, result);
            }

            node = node->NextSiblingNode;
        }
    }

    private static unsafe int CountNodes(AtkResNode* node, int[] histogram)
    {
        var count = 0;
        while (node != null)
        {
            var type = (byte)node->Type;
            histogram[type < histogram.Length ? type : histogram.Length - 1]++;
            count++;

            if (node->ChildCount > 0)
            {
                count += CountNodes(node->ChildNode, histogram);
            }

            if (node->Type == NodeType.Component)
            {
                var componentNode = (AtkComponentNode*)node;
                if (componentNode->Component != null && componentNode->Component->UldManager.RootNode != null)
                {
                    count += CountNodes(componentNode->Component->UldManager.RootNode, histogram);
                }
            }

            node = node->NextSiblingNode;
        }

        return count;
    }

    private static unsafe string ReadUtf8NodeText(AtkTextNode* textNode)
    {
        var pointer = (byte*)textNode->NodeText.StringPtr;
        return pointer == null ? string.Empty : Marshal.PtrToStringUTF8((nint)pointer) ?? string.Empty;
    }

    private unsafe void OnAddonRefresh(AddonEvent type, AddonArgs args)
    {
        Interlocked.Increment(ref diagnostics.RefreshEvents);

        var surface = KindForAddonName(args.AddonName);
        if (!IsSurfaceEnabled(configuration, surface))
        {
            return;
        }

        // _MiniTalk is handled per-bubble by the frame poller, not the refresh path.
        if (surface == DialogueSurfaceKind.MiniTalk)
        {
            return;
        }

        if (!TryGetAddon(surface, out var addon))
        {
            return;
        }

        if (!TryReadSurfaceText(addon, surface, out var text, out var name))
        {
            return;
        }

        if (IsInjectedText(surface, text))
        {
            return;
        }

        CaptureLine(text, name, surface);
    }

    /// <summary>
    ///     Records the text that the in-place feature wrote into a surface's text
    ///     node this frame. Capture paths ignore text matching an injected value so
    ///     the replaced (already-translated) text is never re-translated.
    /// </summary>
    public void NoteInjectedText(DialogueSurfaceKind surface, string? injectedText)
    {
        injectedTexts[surface] = injectedText;
    }

    private bool IsInjectedText(DialogueSurfaceKind surface, string text)
        => injectedTexts.TryGetValue(surface, out var injected)
           && !string.IsNullOrEmpty(injected)
           && injected == text;

    /// <summary>
    ///     Frame-based capture fallback: reads the current text node for the
    ///     active surface and captures a new line whenever it changes. Called
    ///     from the overlay draw loop, so capture works even if lifecycle refresh
    ///     events are missed.
    /// </summary>
    public unsafe void PollActiveSurface(AtkUnitBase* activeAddon, DialogueSurfaceKind surface)
    {
        if (activeAddon == null || !IsSurfaceEnabled(configuration, surface))
        {
            return;
        }

        if (!TryReadSurfaceText(activeAddon, surface, out var text, out var name) || string.IsNullOrWhiteSpace(text))
        {
            stableCandidates[surface.ToString()] = null;
            stableFrames[surface.ToString()] = 0;
            return;
        }

        // If the in-place feature just wrote this text into the node, it is our own
        // replacement (or the original while we wait) - never a new line to translate.
        if (IsInjectedText(surface, text))
        {
            stableCandidates[surface.ToString()] = null;
            stableFrames[surface.ToString()] = 0;
            return;
        }

        var key = surface.ToString();

        // Require the rendered text to be stable across two frames before
        // capturing, so mid-transition partial reads are never translated.
        if (!stableCandidates.TryGetValue(key, out var stableCandidate) || text != stableCandidate)
        {
            stableCandidates[key] = text;
            stableFrames[key] = 1;
            return;
        }

        stableFrames[key] = stableFrames.TryGetValue(key, out var frames) ? frames + 1 : 1;
        if (stableFrames[key] < 2)
        {
            return;
        }

        CaptureLine(text, name, surface);
    }

    /// <summary>
    ///     Polls each visible _MiniTalk bubble independently: every bubble gets its
    ///     own stability gate, translation request, and result, so multiple NPCs
    ///     talking at once each get their own overlay. Called from the overlay draw
    ///     loop when the _MiniTalk surface is active.
    /// </summary>
    public unsafe void PollMiniTalkBubbles(AddonMiniTalk* addon)
    {
        if (addon == null || !IsSurfaceEnabled(configuration, DialogueSurfaceKind.MiniTalk))
        {
            return;
        }

        var seen = new HashSet<nint>();
        var bubbles = addon->TalkBubbles;
        for (var i = 0; i < bubbles.Length; i++)
        {
            var node = bubbles[i].BubbleTextNode;
            if (node == null)
            {
                continue;
            }

            var text = DialogueSurface.ReadCleanText(node);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var key = (nint)node;
            seen.Add(key);

            if (!miniStable.TryGetValue(key, out var stable) || stable.Peek != text)
            {
                miniStable[key] = (text, 1);
                continue;
            }

            var frames = stable.Frames + 1;
            miniStable[key] = (text, frames);
            if (frames < 2)
            {
                continue;
            }

            lock (miniBubbleLock)
            {
                if (miniBubbles.TryGetValue(key, out var existing) && existing.OriginalText == text)
                {
                    continue;
                }
            }

            StartMiniBubbleLine(key, text);
        }

        lock (miniBubbleLock)
        {
            var stale = miniBubbles.Keys.Where(k => !seen.Contains(k)).ToList();
            foreach (var key in stale)
            {
                miniBubbles.Remove(key);
                miniStable.Remove(key);
                diagnostics.Log($"[MiniTalk] bubble gone - cleared state");
            }
        }
    }

    /// <summary>
    ///     Returns every visible <c>_MiniTalk</c> addon instance. Each world chat
    ///     bubble is rendered by its own mini-talk instance, so all of them must be
    ///     polled together rather than only the first (default index 1).
    /// </summary>
    public unsafe List<nint> GetVisibleMiniTalks()
    {
        var pointers = new List<nint>();
        var seen = new HashSet<nint>();
        const int maxInstances = 16;
        for (var i = 1; i <= maxInstances; i++)
        {
            var wrapper = gameGui.GetAddonByName(DialogueSurface.MiniTalkAddonName, i);
            if (wrapper.IsNull)
            {
                break;
            }

            var addon = (AddonMiniTalk*)wrapper.Address;
            if (addon == null || !addon->IsVisible || !seen.Add((nint)addon))
            {
                continue;
            }

            pointers.Add((nint)addon);
        }

        return pointers;
    }

    /// <summary>Snapshot of the live translation state of every visible bubble.</summary>
    public List<MiniBubbleView> GetMiniBubbleViews()
    {
        lock (miniBubbleLock)
        {
            var views = new List<MiniBubbleView>(miniBubbles.Count);
            foreach (var state in miniBubbles.Values)
            {
                views.Add(new MiniBubbleView
                {
                    TextNodePtr = state.TextNode,
                    OriginalText = state.OriginalText,
                    Translated = state.Translated,
                    TranslatedName = null,
                    Status = state.Translated == null ? (string.IsNullOrEmpty(state.OriginalText) ? string.Empty : "Translation pending") : string.Empty,
                    Pending = state.Translated == null && !string.IsNullOrEmpty(state.OriginalText),
                });
            }

            return views;
        }
    }

    private void StartMiniBubbleLine(nint key, string text)
    {
        if (!text.Any(char.IsLetter))
        {
            diagnostics.Log($"[MiniTalk] Ignored non-dialogue bubble: '{Inline(text)}'");
            return;
        }

        int generation;
        lock (miniBubbleLock)
        {
            miniGeneration++;
            generation = miniGeneration;
            miniBubbles[key] = new MiniBubbleState { TextNode = key, OriginalText = text, Generation = generation };
        }

        Interlocked.Increment(ref diagnostics.LinesCaptured);
        diagnostics.Log($"[MiniTalk] Captured bubble ({text.Length} chars): {Inline(text)}");

        if (!translationService.IsConfigured)
        {
            lock (miniBubbleLock)
            {
                if (miniBubbles.TryGetValue(key, out var state) && state.Generation == generation)
                {
                    state.Translated = null;
                }
            }

            diagnostics.Log("Translate skipped: engine not configured (check Engine tab / base URL / model)");
            return;
        }

        Interlocked.Increment(ref diagnostics.TranslationRequests);
        _ = Task.Run(() => TranslateMiniBubbleAsync(key, generation, text));
    }

    private async Task TranslateMiniBubbleAsync(nint key, int generation, string text)
    {
        try
        {
            var translated = await translationService.TranslateAsync(text, default).ConfigureAwait(false);

            lock (miniBubbleLock)
            {
                if (!miniBubbles.TryGetValue(key, out var state) || state.Generation != generation)
                {
                    return;
                }

                state.Translated = translated;
            }

            if (translated == null)
            {
                Interlocked.Increment(ref diagnostics.TranslationsFailed);
                diagnostics.Log($"[MiniTalk] Translation failed for '{Inline(text)}'");
            }
            else
            {
                Interlocked.Increment(ref diagnostics.TranslationsSucceeded);
                diagnostics.Log($"[MiniTalk] Translated -> '{Inline(translated)}'");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref diagnostics.TranslationsFailed);
            log.Error(ex, "Error translating MiniTalk bubble");
            diagnostics.Log($"[MiniTalk] Translation exception: {ex.Message}");
        }
    }

    private static unsafe void DumpJournalCanvas(StringBuilder sb, AtkComponentJournalCanvas* journalCanvas)
    {
        if (journalCanvas == null)
        {
            sb.AppendLine("JournalCanvas: null");
            return;
        }

        var uld = journalCanvas->UldManager;
        sb.AppendLine($"JournalCanvas NodeListCount: {uld.NodeListCount}");
        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var n = uld.NodeList[i];
            if (n == null)
            {
                continue;
            }

            if (n->Type == NodeType.Text)
            {
                var text = ReadUtf8NodeText((AtkTextNode*)n);
                sb.AppendLine($"  canvasNode[{i}] id={(uint)n->NodeId} text='{Truncate(text, 120)}' (len={text.Length})");
                if (!string.IsNullOrEmpty(text))
                {
                    sb.AppendLine($"      raw={EscapeForDiagnostics(text)}");
                }
            }
            else
            {
                sb.AppendLine($"  canvasNode[{i}] id={(uint)n->NodeId} type={(byte)n->Type}");
            }
        }
    }

    /// <summary>
    ///     Finds the quest-description text node inside a journal canvas. The
    ///     description is rendered inside the <see cref="AtkComponentJournalCanvas"/>
    ///     component (used by JournalAccept and JournalDetail), whose nodes live in
    ///     its own UldManager (not the addon's), so it is found by scanning the
    ///     canvas node list for the known description node id (75), falling back to
    ///     the longest non-empty text node in the canvas.
    /// </summary>
    private static unsafe AtkTextNode* GetJournalCanvasDescriptionNode(AtkComponentJournalCanvas* canvas)
    {
        if (canvas == null)
        {
            return null;
        }

        AtkTextNode* best = null;
        var bestLen = 0;
        var uld = canvas->UldManager;
        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null || node->Type != NodeType.Text)
            {
                continue;
            }

            var textNode = (AtkTextNode*)node;
            if (textNode->NodeId == 75)
            {
                return textNode;
            }

            var len = DialogueSurface.ReadCleanText(textNode).Length;
            if (len > bestLen)
            {
                best = textNode;
                bestLen = len;
            }
        }

        return best;
    }

    /// <summary>
    ///     Reads the text (and speaker name, when the surface has one) for the
    ///     current frame of a dialogue surface.
    /// </summary>
    private unsafe bool TryReadSurfaceText(AtkUnitBase* addon, DialogueSurfaceKind surface, out string text, out string name)
    {
        text = string.Empty;
        name = string.Empty;

        if (surface == DialogueSurfaceKind.JournalAccept)
        {
            var canvasText = GetJournalCanvasDescriptionNode(((AddonJournalAccept*)addon)->JournalCanvas);
            text = canvasText == null ? string.Empty : DialogueSurface.ReadCleanText(canvasText);
            return !string.IsNullOrWhiteSpace(text);
        }

        if (surface == DialogueSurfaceKind.JournalDetail)
        {
            var canvasText = GetJournalCanvasDescriptionNode(((AddonJournalDetail*)addon)->JournalCanvasNode);
            text = canvasText == null ? string.Empty : DialogueSurface.ReadCleanText(canvasText);
            return !string.IsNullOrWhiteSpace(text);
        }

        if (surface == DialogueSurfaceKind.MiniTalk)
        {
            return TryReadMiniTalkText((AddonMiniTalk*)addon, out text);
        }

        var textNode = FindTextNodeForLine(addon, DialogueSurface.GetTextNodeId(surface), string.Empty, findAny: true);
        text = textNode == null ? string.Empty : DialogueSurface.ReadCleanText(textNode);

        var nameNodeId = DialogueSurface.GetNameNodeId(surface);
        if (nameNodeId != 0)
        {
            var nameNode = addon->GetTextNodeById(nameNodeId);
            name = nameNode == null ? string.Empty : DialogueSurface.ReadCleanText(nameNode);
        }

        return !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    ///     Reads the combined text of every visible _MiniTalk bubble. Unlike the
    ///     other dialogue addons, _MiniTalk does not store its text in the addon's
    ///     node tree; each bubble's text lives in an <see cref="AddonMiniTalk.TalkBubbles"/>
    ///     entry, so it must be read directly from the bubble entries.
    /// </summary>
    private static unsafe bool TryReadMiniTalkText(AddonMiniTalk* addon, out string text)
    {
        text = string.Empty;
        if (addon == null)
        {
            return false;
        }

        var parts = new List<string>();
        var bubbles = addon->TalkBubbles;
        for (var i = 0; i < bubbles.Length; i++)
        {
            var node = bubbles[i].BubbleTextNode;
            if (node == null)
            {
                continue;
            }

            var entry = DialogueSurface.ReadCleanText(node);
            if (!string.IsNullOrWhiteSpace(entry))
            {
                parts.Add(entry);
            }
        }

        text = string.Join("\n", parts);
        return !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    ///     Finds the article text node of the _MiniTalk bubble matching
    ///     <paramref name="originalText"/>, used as the overlay anchor.
    /// </summary>
    private static unsafe AtkTextNode* FindMiniTalkTextNode(AddonMiniTalk* addon, string originalText)
    {
        if (addon == null)
        {
            return null;
        }

        AtkTextNode* any = null;
        var bubbles = addon->TalkBubbles;
        for (var i = 0; i < bubbles.Length; i++)
        {
            var node = bubbles[i].BubbleTextNode;
            if (node == null)
            {
                continue;
            }

            var clean = DialogueSurface.ReadCleanText(node);
            if (string.IsNullOrEmpty(clean))
            {
                continue;
            }

            if (any == null)
            {
                any = node;
            }

            // The captured line may join several bubbles with newlines, so an
            // exact node match only makes sense for the single-bubble case.
            if (clean == originalText)
            {
                return node;
            }
        }

        return any;
    }

    private void CaptureLine(string text, string name, DialogueSurfaceKind surface)
    {
        // Ignore transition markers and non-dialogue fragments that have no
        // word characters (e.g. "....", "----", page indicators).
        if (!text.Any(char.IsLetter))
        {
            diagnostics.Log($"[{surface}] Ignored non-dialogue text: '{Inline(text)}'");
            return;
        }

        if (OverlayState.IsSameLine(text, surface, configuration.TargetLanguage))
        {
            return;
        }

        Interlocked.Increment(ref diagnostics.LinesCaptured);
        diagnostics.Log($"[{surface}] Captured line ({text.Length} chars) name='{Truncate(name, 24)}': {Inline(text)}");

        var generation = OverlayState.BeginLine(text, name, surface, configuration.TargetLanguage);

        if (!translationService.IsConfigured)
        {
            OverlayState.Complete(generation, null, "Engine not configured");
            diagnostics.NoteFailure("Engine not configured - check the Translation tab (engine, base URL, model, API key)");
            return;
        }

        Interlocked.Increment(ref diagnostics.TranslationRequests);
        _ = Task.Run(() => TranslateLineAsync(text, name, generation, surface));
    }

    private async Task TranslateLineAsync(string text, string name, int generation, DialogueSurfaceKind surface)
    {
        try
        {
            var translateName = !string.IsNullOrWhiteSpace(name) && configuration.TranslateSpeakerNames;

            var textTask = translationService.TranslateAsync(text, default);
            var nameTask = translateName
                ? translationService.TranslateAsync(name, default)
                : Task.FromResult<string?>(null);

            var translatedName = await nameTask.ConfigureAwait(false);
            var translated = await textTask.ConfigureAwait(false);

            if (translateName && string.IsNullOrWhiteSpace(translatedName))
            {
                translatedName = null;
            }

            OverlayState.Complete(generation, translated, translatedName, translated == null ? "Translation failed" : string.Empty);

            if (translated == null)
            {
                Interlocked.Increment(ref diagnostics.TranslationsFailed);
                diagnostics.Log($"[{surface}] Translation failed for '{Inline(text)}'");
            }
            else
            {
                diagnostics.ClearFailure();
                Interlocked.Increment(ref diagnostics.TranslationsSucceeded);
                diagnostics.Log($"[{surface}] Translated -> '{Inline(translated)}'");
            }

            if (translateName && !string.IsNullOrWhiteSpace(translatedName))
            {
                diagnostics.Log($"[{surface}] Name '{Inline(name)}' -> '{Inline(translatedName)}'");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref diagnostics.TranslationsFailed);
            log.Error(ex, "Error translating dialogue line");
            diagnostics.NoteFailure($"[{surface}] Translation exception: {ex.Message}");
            OverlayState.Complete(generation, null, null, "Translation error");
        }
    }

    private unsafe void OnAddonReset(AddonEvent type, AddonArgs args)
    {
        var surface = KindForAddonName(args.AddonName);
        if (OverlayState.IsSurface(args.AddonName, surface))
        {
            OverlayState.Clear();
            diagnostics.Log($"[{surface}] addon hidden/closed - state cleared");
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static string EscapeForDiagnostics(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 32)
            {
                sb.Append($"\\x{(int)c:X2}");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string Inline(string text)
        => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    public void Dispose()
    {
        foreach (var addonName in DialogueSurface.AllAddonNames)
        {
            addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, addonName, OnAddonRefresh);
            addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, OnAddonReset);
            addonLifecycle.UnregisterListener(AddonEvent.PreHide, addonName, OnAddonReset);
        }
    }
}
