using System.Collections.Concurrent;

namespace Tomestone.Translate.Engines;

/// <summary>
///     Simple bounded in-memory cache keyed by (target language + source text)
///     so repeated dialogue lines are not re-requested.
/// </summary>
public sealed class TranslationCache
{
    private readonly ConcurrentDictionary<string, string?> cache = new();
    private readonly int maxEntries;

    public TranslationCache(int maxEntries = 2000)
    {
        this.maxEntries = maxEntries;
    }

    public int Count => cache.Count;
    public int Capacity => maxEntries;

    public string? Get(string sourceText, string targetLanguage)
        => cache.TryGetValue(Key(sourceText, targetLanguage), out var value) ? value : null;

    public void Set(string sourceText, string targetLanguage, string? translation)
    {
        cache[Key(sourceText, targetLanguage)] = translation;

        // Cheap eviction: occasionally reset when the cache grows too large.
        if (cache.Count > maxEntries)
        {
            cache.Clear();
        }
    }

    public void Clear() => cache.Clear();

    private static string Key(string sourceText, string targetLanguage)
        => $"{targetLanguage}|{sourceText}";
}