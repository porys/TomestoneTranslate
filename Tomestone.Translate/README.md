# Tomestone Translate

A Dalamud plugin for FINAL FANTASY XIV that translates in-game cutscene and NPC
dialogue in real time and draws the translation as an overlay on top of (or
above) the original text.

## Features

- Captures dialogue live from every in-game dialogue surface: the NPC dialogue
  box, cutscene subtitle bar, duty & event dialogue, world NPC chat bubbles, and
  dialogue choices.
- **World NPC chat bubbles are translated per-bubble**: every visible bubble is
  polled independently, so multiple NPCs talking at once each get their own
  overlay (instead of a single combined line).
- Goes fully inactive when the target language matches the game client language:
  capture and overlay both switch off, so text already in the client's language is
  left untouched.
- Translates each line through the chosen engine: any **OpenAI-compatible**
  endpoint (OpenAI, DeepSeek, OpenRouter, Ollama `/v1`, LM Studio), **Anthropic
  Claude**, **Google Gemini**, **DeepL**, or one of the free no-API-key
  providers: **Google Translate** or **MyMemory**.
- An immutable translation cache (per source line + target language), so a
  repeated line costs nothing after the first translation.
- Translated-line overlay anchored to the original text's on-screen position using
  the game's own node screen coordinates; includes a managed font atlas with
  Hangul / CJK / Kana glyphs.
- Optional **speaker name translation**, drawn above the dialogue in its own
  distinct color.
- Experimental **in-place replacement**: overwrite the in-game text with the
  translation instead of drawing a separate overlay (world bubbles are always
  shown as an overlay, since they re-render every frame).
- Per-surface toggles, display position (above / on top / below) with an
  adjustable vertical offset, text opacity and separate background opacity,
  wrap width, background toggle, text/name colors, and a configurable system
  prompt.

## Commands

- `/tt` – open the main window (also hosts the master on/off switch).
- `/ttconfig` – open the settings window.

## Building

Prerequisites:

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled (game must
  have been run once with Dalamud).
- A .NET SDK (the Dalamud SDK resolves the local `addon\Hooks\dev` runtime
  automatically; `DALAMUD_HOME` can override it).

```
dotnet build Tomestone.Translate.slnx -c Debug
```

## Testing in-game

The built plugin is deployed to Dalamud's dev plugin folder with:

```
powershell -ExecutionPolicy Bypass -File scripts\copy-to-dev.ps1
```

Restart the game, open the plugin installer (`/xlplugins`), and enable
**Tomestone Translate** from *Dev Tools → Installed Dev Plugins*. Alternatively
add the build output folder to *Dev Plugin Locations* in
`/xlsettings` → *Experimental*.

## Configuration

The settings window is organised into four tabs, one concern each:

1. **Translation** – pick the target language and the translation provider, plus
   the **Disable inside instance** toggle.
   - The game's client language is shown as the source. This single target
     language setting drives every provider — DeepL, Google Translate and
     MyMemory derive their ISO codes (e.g. `EN-US`/`en`) from it automatically,
     and the plugin goes fully inactive when the target equals the game client
     language.
   - Each provider shows its API key (blank for local endpoints) and model as
     needed. Base URLs use the provider's default and are shown read-only; click
     **Edit** next to one to set a custom endpoint for self-hosted or alternate
     servers.
   - Temperature and system prompt live under the collapsed **Advanced
     (optional)** header and are only relevant to the LLM providers.
2. **Content** – whether to translate speaker names, and which kinds of in-game
   text are translated (NPC dialogue box, cutscene subtitles, duty & event
   dialogue, world chat bubbles, dialogue choices).
3. **Display** – how translations look: position (above / on top / below; the
   vertical offset is set automatically to −35/0/15 and remains adjustable),
   font scale, text opacity, separate background opacity, wrap width,
   background toggle, placeholder, in-place replacement, and text/name colors.
4. **Developer** – troubleshooting only, not needed for normal use: a live
   status summary, overlay probe, node-tree scanner, counters (with a **Clear
   cache** button), and an event log. Hidden by default behind the **Show
   Developer tab** checkbox at the bottom of the window.

The main window (`/tt`) has the master **Translation on/off** switch. With it
off, no dialogue is captured and no overlay is drawn. **Disable inside
instance** on the Translation tab pauses translation while you are in a
dungeon, trial, or raid and resumes automatically when you leave.