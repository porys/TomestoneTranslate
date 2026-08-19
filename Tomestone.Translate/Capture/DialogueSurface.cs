using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Tomestone.Translate.Capture;

/// <summary>
///     Identifies which in-game dialogue surface a captured line came from.
///     Each surface is a separate <c>AtkUnitBase</c> addon with its own text
///     node layout, so the capture service and overlay drawer both route on
///     this value.
/// </summary>
public enum DialogueSurfaceKind
{
    Talk,
    TalkSubtitle,
    BattleTalk,
    MiniTalk,
SelectString,
    JournalAccept,
    JournalDetail,
}

/// <summary>
///     Static description of every dialogue surface the plugin knows about:
///     the addon name plus the text/name node ids used by that addon. Kept in
///     one place so capture and overlay share the same node map.
/// </summary>
public static class DialogueSurface
{
    public const string TalkAddonName = "Talk";
    public const string TalkSubtitleAddonName = "TalkSubtitle";
    public const string BattleTalkAddonName = "_BattleTalk";
    public const string MiniTalkAddonName = "_MiniTalk";
    public const string SelectStringAddonName = "SelectString";
    public const string JournalAcceptAddonName = "JournalAccept";
    public const string JournalDetailAddonName = "JournalDetail";

    public static readonly string[] AllAddonNames =
    {
        TalkAddonName,
        TalkSubtitleAddonName,
        BattleTalkAddonName,
        MiniTalkAddonName,
        SelectStringAddonName,
        JournalAcceptAddonName,
        JournalDetailAddonName,
    };

    // Scan-only candidates in the Developer tab; not capture surfaces.
    public static readonly string[] ScanAddonNames = new[]
    {
        TalkAddonName,
        TalkSubtitleAddonName,
        BattleTalkAddonName,
        MiniTalkAddonName,
        SelectStringAddonName,
        JournalAcceptAddonName,
        "Journal",
        "JournalDetail",
    };

    /// <summary>
    ///     Whether a surface supports in-place replacement of its original text.
    ///     Line-based addons (Talk, subtitles, choices, battle chatter) keep a
    ///     written translation until they refresh; world bubbles re-render from
    ///     game state every frame, so replacing their text in the node is
    ///     overwritten immediately and is not reliable.
    /// </summary>
    public static bool SupportsTextReplacement(DialogueSurfaceKind surface)
        => surface is not (DialogueSurfaceKind.MiniTalk or DialogueSurfaceKind.JournalAccept or DialogueSurfaceKind.JournalDetail);

    // Node ids are per-addon and were confirmed against the current client for
    // Talk; the remaining surfaces use ids matched via in-game scans.
    private const uint TalkTextNodeId = 3;
    private const uint TalkNameNodeId = 2;
    private const uint BattleTalkTextNodeId = 6;
    private const uint BattleTalkNameNodeId = 4;
    private const uint MiniTalkTextNodeId = 1;
    private const uint TalkSubtitleTextNodeId = 2;
    private const uint SelectStringTextNodeId = 2;

    // JournalAccept's quest-description node id is not hardcoded; it is
    // discovered at runtime via ScanAddonNodes. Node id 0 makes the capture
    // fall back to the longest text node, which is the description paragraph.
    private const uint JournalAcceptTextNodeId = 0;

    public static string GetAddonName(DialogueSurfaceKind surface)
        => surface switch
        {
            DialogueSurfaceKind.Talk => TalkAddonName,
            DialogueSurfaceKind.TalkSubtitle => TalkSubtitleAddonName,
            DialogueSurfaceKind.BattleTalk => BattleTalkAddonName,
            DialogueSurfaceKind.MiniTalk => MiniTalkAddonName,
            DialogueSurfaceKind.SelectString => SelectStringAddonName,
            DialogueSurfaceKind.JournalAccept => JournalAcceptAddonName,
            DialogueSurfaceKind.JournalDetail => JournalDetailAddonName,
            _ => TalkAddonName,
        };

    public static uint GetTextNodeId(DialogueSurfaceKind surface)
        => surface switch
        {
            DialogueSurfaceKind.Talk => TalkTextNodeId,
            DialogueSurfaceKind.TalkSubtitle => TalkSubtitleTextNodeId,
            DialogueSurfaceKind.BattleTalk => BattleTalkTextNodeId,
            DialogueSurfaceKind.MiniTalk => MiniTalkTextNodeId,
            DialogueSurfaceKind.SelectString => SelectStringTextNodeId,
            DialogueSurfaceKind.JournalAccept => JournalAcceptTextNodeId,
            DialogueSurfaceKind.JournalDetail => JournalAcceptTextNodeId,
            _ => TalkTextNodeId,
        };

    public static uint GetNameNodeId(DialogueSurfaceKind surface)
        => surface switch
        {
            DialogueSurfaceKind.Talk => TalkNameNodeId,
            DialogueSurfaceKind.BattleTalk => BattleTalkNameNodeId,
            _ => 0,
        };

    /// <summary>Renders a human-readable label for end users and diagnostics.</summary>
    public static string GetDisplayName(DialogueSurfaceKind surface)
        => surface switch
        {
            DialogueSurfaceKind.Talk => "NPC dialogue box",
            DialogueSurfaceKind.TalkSubtitle => "Cutscene subtitle bar",
            DialogueSurfaceKind.BattleTalk => "Duty & event dialogue",
            DialogueSurfaceKind.MiniTalk => "World NPC chat bubbles",
            DialogueSurfaceKind.SelectString => "Dialogue choices",
            DialogueSurfaceKind.JournalAccept => "Quest Accept Window",
            DialogueSurfaceKind.JournalDetail => "Quest Detail Window",
            _ => surface.ToString(),
        };

    /// <summary>
    ///     Reads a node's UTF-8 text with FFXIV color/formatting markup stripped.
    /// </summary>
    public static unsafe string ReadCleanText(AtkTextNode* textNode)
    {
        if (textNode == null)
        {
            return string.Empty;
        }

        var pointer = (byte*)textNode->NodeText.StringPtr;
        var raw = pointer == null
            ? string.Empty
            : Marshal.PtrToStringUTF8((nint)pointer) ?? string.Empty;
        return SanitizeSourceText(raw);
    }

    /// <summary>
    ///     Removes FFXIV inline color/formatting markup and stray control characters
    ///     from captured text before it is translated. Handles three markup forms:
    ///     angle-bracket tags (<c>&lt;Color(r,g,b,a)&gt;</c>), square-bracket codes
    ///     (<c>[98;5u</c>), and control-byte macros (<c>\x02</c>..<c>\x03</c>) whose
    ///     binary params decode as U+FFFD and whose command id survives as a literal
    ///     letter (e.g. 0x48/0x49 = Color).
    /// </summary>
    public static string SanitizeSourceText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var noTags = AngleMarkupRegex.Replace(text, string.Empty);
        noTags = ColorMarkupRegex.Replace(noTags, string.Empty);

        // Control-byte markup: a run of control bytes / U+FFFD that may enclose a
        // few literal letters (the macro command ids). A letter is only markup when
        // both of its neighbours are markup, so a real word sitting next to a run
        // (e.g. "Collection" after \x03) survives.
        var isMarkup = new bool[noTags.Length];
        for (var i = 0; i < noTags.Length; i++)
        {
            var c = noTags[i];
            isMarkup[i] = (c < 32 && c is not ('\t' or '\n' or '\r')) || c == '\uFFFD';
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < noTags.Length; i++)
            {
                if (isMarkup[i] || !char.IsLetter(noTags[i]))
                {
                    continue;
                }

                var left = i > 0 && isMarkup[i - 1];
                var right = i < noTags.Length - 1 && isMarkup[i + 1];
                if (left && right)
                {
                    isMarkup[i] = true;
                    changed = true;
                }
            }
        }

        var sb = new StringBuilder(noTags.Length);
        for (var i = 0; i < noTags.Length; i++)
        {
            if (!isMarkup[i])
            {
                sb.Append(noTags[i]);
            }
        }

        return sb.ToString().Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex ColorMarkupRegex =
        new(@"\[\d+(?:;\d+)+\w?|\[[A-Za-z]+;\d+;5u", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex AngleMarkupRegex =
        new(@"</?[A-Za-z][^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);

#if DEBUG
    /// <summary>Returns an error message when the sanitizer fails to strip a known markup form.</summary>
    public static string? SanitizeSelfCheck()
    {
        var cases = new (string Raw, string Expected)[]
        {
            ("the H I [98;5uCollection[98;5u IH menu", "the H I Collection IH menu"),
            ("the <Color(255,85,0,255)>Collection</Color> menu", "the Collection menu"),
            ("<If(1)>Hello</If> world", "Hello world"),
            ("plain text", "plain text"),
            ("tab\tand\nnewline", "tab\tand\nnewline"),
            ("Quest Sync\rThe herd is missing.", "Quest Sync\nThe herd is missing."),
            (
                "from the \x02H\x04\uFFFD\x01\uFFFD\x03\x02I\x04\uFFFD\x01\uFFFD\x03Collection\x02I\x02\x01\x03\x02H\x02\x01\x03 menu",
                "from the Collection menu"),
        };

        foreach (var (raw, expected) in cases)
        {
            var actual = SanitizeSourceText(raw);
            if (actual != expected)
            {
                return $"expected '{expected}' but got '{actual}' for '{raw}'";
            }
        }

        return null;
    }
#endif
}

/// <summary>
///     Holds a reusable null-terminated UTF-8 buffer for writing text into game
///     nodes via <c>AtkTextNode.SetText</c>. The game keeps the pointer passed to
///     SetText alive, so the buffer must outlive the call - a managed/transient
///     buffer would dangle and crash. This allocates unmanaged memory once and
///     reuses it, growing on demand, until disposed.
/// </summary>
public sealed unsafe class TextNodeBuffer : IDisposable
{
    private byte* buffer;
    private int capacity;

    /// <summary>
    ///     Writes <paramref name="text"/> into <paramref name="node"/> in place. When
    ///     <paramref name="maxWidthPx"/> is greater than zero the text is hard-wrapped
    ///     to that pixel width first (the raw SetText does not re-wrap existing nodes,
    ///     so a long line would otherwise overflow the box). Returns the text actually
    ///     written to the node.
    /// </summary>
    public string SetText(AtkTextNode* node, string text, float maxWidthPx = 0f)
    {
        if (node == null)
        {
            return string.Empty;
        }

        var layoutText = maxWidthPx > 0f
            ? WrapToWidth(text, maxWidthPx, node->FontSize)
            : text;

        var byteCount = Encoding.UTF8.GetByteCount(layoutText);
        EnsureCapacity(byteCount + 1);

        var span = new Span<byte>(buffer, byteCount + 1);
        Encoding.UTF8.GetBytes(layoutText, span);
        span[byteCount] = 0;

        node->SetText(buffer);
        return layoutText;
    }

    /// <summary>Restores the original text that was previously displayed in the node.</summary>
    public void RestoreText(AtkTextNode* node, string originalText)
        => SetText(node, originalText);

    /// <summary>
    ///     Break <paramref name="text"/> into lines so each fits within
    ///     <paramref name="maxWidthPx"/> pixels. Glyph width is estimated from the
    ///     node's font size: full-width (CJK) glyphs are roughly the font size wide
    ///     while most latin glyphs are about half that.
    /// </summary>
    private static string WrapToWidth(string text, float maxWidthPx, float fontSize)
    {
        if (string.IsNullOrEmpty(text) || maxWidthPx <= 0f)
        {
            return text;
        }

        var ss = fontSize <= 0f ? 9f : fontSize * 0.53f;      // half-width (latin) advance
        var fullWidth = ss * 2f;                              // CJK advance
        var availablePx = Math.Max(1f, maxWidthPx);

        var lines = text.Split('\n');
        var result = new StringBuilder(text.Length + lines.Length);

        for (var l = 0; l < lines.Length; l++)
        {
            if (l > 0)
            {
                result.Append('\n');
            }

            var words = lines[l].Split(' ');
            var lineWidth = 0f;

            for (var w = 0; w < words.Length; w++)
            {
                var word = words[w];
                var wordWidth = MeasureWidth(word, ss, fullWidth);

                if (w > 0 && lineWidth + wordWidth > availablePx)
                {
                    result.Append('\n');
                    lineWidth = 0f;
                }

                if (w > 0)
                {
                    result.Append(' ');
                    lineWidth += ss;
                }

                result.Append(word);
                lineWidth += wordWidth;
            }
        }

        return result.ToString();
    }

    private static float MeasureWidth(string s, float advance, float fullWidth)
    {
        var width = 0f;
        foreach (var c in s)
        {
            width += char.IsLetterOrDigit(c) && c <= 0x2FFF ? advance : fullWidth;
        }

        return width;
    }

    private void EnsureCapacity(int needed)
    {
        if (buffer != null && needed <= capacity)
        {
            return;
        }

        if (buffer != null)
        {
            NativeMemory.Free(buffer);
        }

        capacity = needed;
        buffer = (byte*)NativeMemory.Alloc((nuint)capacity);
    }

    public void Dispose()
    {
        if (buffer != null)
        {
            NativeMemory.Free(buffer);
            buffer = null;
            capacity = 0;
        }
    }
}
