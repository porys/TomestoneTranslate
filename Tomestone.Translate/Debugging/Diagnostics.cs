using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Tomestone.Translate.Debugging;

/// <summary>
///     Lightweight, thread-safe diagnostics: a bounded ring buffer of log lines
///     plus fast counters. Kept free of Dalamud dependencies so any stage can use it.
/// </summary>
public sealed class Diagnostics
{
    private readonly ConcurrentQueue<string> ring = new();
    private readonly int capacity;

    public Diagnostics(int capacity = 400)
    {
        this.capacity = capacity;
    }

    // Live counters
    public int RefreshEvents, LinesCaptured, TranslationRequests, TranslationsSucceeded, TranslationsFailed, OverlayDraws, OverlayShowAttempts;

    // Latest per-frame overlay probe (written by the overlay drawer)
    public bool AddonFound;
    public bool AddonVisible;
    public bool TextNodeFound;
    public float NodeX, NodeY, NodeW, NodeH;
    public string? OverlaySurface;
    public string? OverlayLastSkipReason;

    public void Track(int by) => Interlocked.Add(ref RefreshEvents, by);
    public void Count(ref int field) => Interlocked.Increment(ref field);

    public void Log(string message)
    {
        ring.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        while (ring.Count > capacity && ring.TryDequeue(out _))
        {
        }
    }

    // Most recent translation failure. User-facing; survives until a success.
    public string? LastError { get; private set; }

    public void NoteFailure(string message)
    {
        LastError = message;
        Log(message);
    }

    public void ClearFailure()
    {
        LastError = null;
    }

    public IReadOnlyList<string> RecentLines(int max = 0)
    {
        var all = ring.Reverse().ToArray();
        return max <= 0 || all.Length <= max ? all : all[..max];
    }

    public void Clear()
    {
        while (ring.TryDequeue(out _))
        {
        }
    }
}