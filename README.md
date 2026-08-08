<p align="center">
  <img alt="Tomestone Translate" src="https://raw.githubusercontent.com/porys/xivplugins/main/icon.png" width="120">
</p>

<h1 align="center">Tomestone Translate</h1>

<p align="center">
  <b>Read FINAL FANTASY XIV in your language.</b><br>
  A Dalamud plugin that live-translates cutscene, NPC and chat dialogue
  and overlies it on the original text.
</p>

<p align="center">
  <img alt="Dotnet" src="https://img.shields.io/badge/.NET-512BD4?logo=dotnet&logoColor=white">
  <img alt="License" src="https://img.shields.io/github/license/porys/TomestoneTranslate">
  <img alt="API" src="https://img.shields.io/badge/Dalamud%20API-15-blueviolet">
</p>

<p align="center">
  <b>⚡ Live translation overlay</b> · <b>🌐 9+ providers</b> · <b>🗣️ per-speaker bubbles</b>
</p>

---

## Install

The easiest way is to add Tomestone Translate as a custom Dalamud plugin
repository — then it shows up in `/xlplugins` like any other plugin and updates
automatically.

1. In the game, open the Dalamud settings: **`/xlsettings`**
2. Go to the **Experimental** tab
3. Paste the repo URL into **Custom plugin repositories**, then press **`+`**:

```
https://raw.githubusercontent.com/porys/xivplugins/main/pluginmaster.json
```

4. Open the plugin installer (**`/xlplugins`**), switch to **All plugins**
   and search for **Tomestone Translate** — or find it under *Available plugins*.
5. Press **Install**, then open **Settings** to pick your language and provider.

---

## Quick start

1. Open the main window: `/tt`
2. Flip the big **Translation** switch to **ON**
3. Open the settings (**`/ttconfig`**), pick your **target language**
4. Choose a **provider** (see below) and enter any API key it needs
5. Done — next dialogue line gets translated on top of the game text

> 💡 Repeat lines are cached forever, so a line you've seen before costs
> nothing to translate again.

## Features

- **Live translation of everything spoken**: the NPC dialogue box, cutscene
  subtitle bar, duty & event dialogue, world chat bubbles _and_ dialogue choices
  are all captured on the fly.
- **Per-bubble world chat** — every visible chat bubble is polled independently,
  so several NPCs talking at once each get their own overlay instead of one
  blended line.
- **Smart auto-off** — when your target language matches the game client
  language, capture and overlay switch off by themselves and leave your text
  untouched.
- **9 translation engines** — any **OpenAI-compatible** endpoint (OpenAI,
  DeepSeek, OpenRouter, Ollama `/v1`, LM Studio), **Anthropic Claude**,
  **Google Gemini**, **DeepL**, or free no-key **Google Translate** /
  **MyMemory**.
- **In-place or overlay** — either draw the translation right on the original
  text, or replace the in-game text with it (experimental).
- **Speaker name translation** rendered above the dialogue in its own colour.
- **Immutable cache** per source line + language.
- **Full display control** — font scale, opacity, wrap width, background box,
  position (above / on top / below), text & name colours, adjustable offset.
- **CJK-ready** — managed font atlas ships Hangul / CJK / Kana glyphs.

## Commands

| Command      | Description                          |
| ------------ | ------------------------------------ |
| `/tt`        | Main window + master on/off switch   |
| `/ttconfig`  | Settings window                      |

## Configuration

The settings window is organised into four tabs, one concern each:

1. **Translation** — target language, provider, endpoint & key, and the
   **Disable inside instance** toggle (auto-pause in dungeons/raids).
2. **Content** — translate speaker names, and choose which surfaces get
   translated.
3. **Display** — everything about how translations look.
4. **Developer** — hidden by default; live status, node scanner, counters,
   cache clear and event log for troubleshooting.

---

## Building from source

Prerequisites: a .NET SDK (the Dalamud SDK resolves the local runtime
automatically) and [XIVLauncher](https://goatcorp.github.io/) with Dalamud
(ran once).

```
dotnet build Tomestone.Translate.slnx -c Debug
powershell -ExecutionPolicy Bypass -File scripts\copy-to-dev.ps1
```

Then `/xlplugins` → *Dev Tools* → *Installed Dev Plugins* → enable.

## License

[GNU AGPL-3.0-or-later](https://github.com/porys/TomestoneTranslate)