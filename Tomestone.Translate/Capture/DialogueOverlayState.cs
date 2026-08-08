using System;

namespace Tomestone.Translate.Capture;

/// <summary>
///     Thread-safe snapshot of the currently visible dialogue line and its
///     live translation. Content is produced by the capture service; the
///     overlay drawer only reads it. Tracks which surface produced the line so
///     the overlay can anchor itself to the right addon's text node.
/// </summary>
public sealed class DialogueOverlayState
{
    private readonly object gate = new();

    private int generation;
    private string originalName = string.Empty;
    private string originalText = string.Empty;
    private string targetLanguage = string.Empty;
    private string? translatedText;
    private string? translatedName;
    private string status = string.Empty;
    private DialogueSurfaceKind surface = DialogueSurfaceKind.Talk;

    /// <summary>Starts tracking a new source line. Older in-flight responses are invalidated.</summary>
    public int BeginLine(string originalText, string originalName, DialogueSurfaceKind surface, string targetLanguage)
    {
        lock (gate)
        {
            generation++;
            this.originalText = originalText;
            this.originalName = originalName;
            this.surface = surface;
            this.targetLanguage = targetLanguage;
            translatedText = null;
            translatedName = null;
            status = string.Empty;
            return generation;
        }
    }

    /// <summary>Applies a translation only if the line is still current.</summary>
    public void Complete(int lineGeneration, string? translation, string? nameTranslation, string statusMessage = "")
    {
        lock (gate)
        {
            if (lineGeneration != generation)
            {
                return;
            }

            translatedText = string.IsNullOrWhiteSpace(translation) ? null : translation;
            translatedName = string.IsNullOrWhiteSpace(nameTranslation) ? null : nameTranslation;
            status = statusMessage;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            generation++;
            originalText = string.Empty;
            originalName = string.Empty;
            targetLanguage = string.Empty;
            translatedText = null;
            translatedName = null;
            status = string.Empty;
        }
    }

    /// <summary>
    ///     True when the currently displayed line is exactly the given source text on the
    ///     given surface <em>and</em> was translated into the given target language. When
    ///     the target language changes, this returns false so the line is re-translated.
    /// </summary>
    public bool IsSameLine(string text, DialogueSurfaceKind surface, string targetLanguage)
    {
        lock (gate)
        {
            return this.originalText == text
                   && this.surface == surface
                   && this.targetLanguage == targetLanguage;
        }
    }

    /// <summary>True when the current line originated from the given surface.</summary>
    public bool IsSurface(string addonName, DialogueSurfaceKind surface)
        => DialogueSurface.GetAddonName(this.GetSurface()) == addonName || this.GetSurface() == surface;

    private DialogueSurfaceKind GetSurface()
    {
        lock (gate)
        {
            return surface;
        }
    }

    public bool TryGetLine(
        out string originalName,
        out string originalText,
        out string? translatedText,
        out string? translatedName,
        out string status,
        out DialogueSurfaceKind surface,
        out bool isPending)
    {
        lock (gate)
        {
            originalName = this.originalName;
            originalText = this.originalText;
            translatedText = this.translatedText;
            translatedName = this.translatedName;
            status = this.status;
            surface = this.surface;
            isPending = this.translatedText == null && !string.IsNullOrEmpty(this.originalText);
            return !string.IsNullOrEmpty(this.originalText);
        }
    }

    public bool TryGetLine(
        out string originalName,
        out string originalText,
        out string? translatedText,
        out string? translatedName,
        out string status,
        out bool isPending)
    {
        var hasLine = TryGetLine(out originalName, out originalText, out translatedText, out translatedName, out status, out _, out isPending);
        return hasLine;
    }
}
