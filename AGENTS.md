# AGENTS.md

Dalamud plugin for FFXIV that live-translates cutscene/NPC dialogue and draws it
as an overlay. C# / .NET, single project, built against the local Dalamud SDK
(`Dalamud.NET.Sdk/15.0.0`). `FFXIVClientStructs` is not referenced directly; it comes
transitively from the SDK and is resolved to the current game build (7.51.0.8681 on
the last-verified client).

## Layout

The repo root is `projects\Tomestone Translate\Tomestone.Translate` (a git repo,
`origin` = `porys/TomestoneTranslate`). From the repo root:

- `Tomestone.Translate.slnx` — solution
- `Tomestone.Translate/Tomestone.Translate.csproj` — actual project
- `scripts/copy-to-dev.ps1` — deploy script
- `deploy/pluginmaster.json` — Dalamud pluginmaster entry for the distro repo
- `.github/workflows/release.yml` — tag `v*` → build Release → GitHub release
- `AGENTS.md`, `README.md`, `.gitignore`

GitHub-Actions side note: release workflow builds from `main`; **patch releases
are triggered by pushing a `v*` tag** (version is read from the tag, e.g. `v0.0.0.2`).

## Build & deploy (exact, Debug matters)

```powershell
dotnet build "Tomestone.Translate\Tomestone.Translate.csproj" -c Debug
powershell -ExecutionPolicy Bypass -File "scripts\copy-to-dev.ps1"
```

- Must build **Debug** — the deploy script reads from `bin\Debug`.
- Deploy copies dll+json to `%APPDATA%\XIVLauncher\devPlugins\Tomestone.Translate`.
- No test project / test framework exists. Verification is build + manual in-game check.

## In-game reload

Dalamud hot-reloads dev plugins: after deploying, the updated dll/json is picked up
automatically (reloading with `/xlload Tomestone.Translate`, or restarting the game,
still forces it). The config UI lives at `/ttconfig`, main window `/tt`.

## Source architecture

- `Plugin.cs` — composition root; wires services, registers commands, sets up the
  overlay font atlas (Hangul/CJK/Kana). static `Plugin.Xxx` service properties.
  `IsTranslationActive(config)` is the master gate (enabled + not in a duty +
  target language ≠ client language).
- `Capture/TalkCaptureService.cs` — captures lines from addon text nodes, per-surface
  and per-`_MiniTalk`-bubble, holds overlay view state.
- `Capture/DialogueSurface.cs` — addon↔surface map, node id/kind lookup,
  `GetDisplayName`, `ReadCleanText` (strips FFXIV markup), the
  `SanitizeSourceText` markup stripper, and `TextNodeBuffer`
  (unmanaged UTF-8 buffer used for in-place `SetText`).
- `Capture/DialogueOverlayState.cs` — tracked current line + translation + pending.
- `Engines/TranslationService.cs` — owns `ITranslator`, rebuilds on setting change,
  consults cache (`ClearCache()` used by the Developer tab and on provider change).
  `Engines/` has Claude/Gemini/DeepL/GoogleTranslate/MyMemory/OpenAICompatible
  translators plus `TranslatorHttpBase` and `TranslationCache`.
- `Overlay/OverlayDrawer.cs` — draws the overlay; also the in-place replacement path.
  Background box opacity (`OverlayBackgroundOpacity`, applied to ImGui `WindowBg`).
- `Windows/MainWindow.cs` (`/tt`), `Windows/ConfigWindow.cs` (`/ttconfig`). Provider
  names appear in a duplicated `EngineKindName` switch in BOTH windows — keep in sync
  when adding a provider.
- `Debugging/Diagnostics.cs` — counters + ring-buffer log surfaced in the Developer tab.
- `Configuration.cs` — `IPluginConfiguration`; also `TargetLanguageCodes` (maps a single
  friendly target language to ISO codes per provider).

## Framework / client quirks (verify or respect)

- `AtkTextNode.SetText` keeps the pointer passed to it, so you cannot pass a managed/
  transient buffer. Use the persistent unmanaged `TextNodeBuffer` (existing helper) for
  any in-place node writes. Returning text via transient allocations after a frame is a
  crash risk.
- `_MiniTalk` world bubbles are each a separate addon instance and re-render every frame
  from game state; they are translated per-bubble and cannot meaningfully use in-place
  replacement.
- `DialogOverlayState.TryGetLine` has two overloads (one drops the surface) — check
  signatures before use.
- FFXIV text nodes store inline color/formatting as **three distinct markup forms**, all
  of which `SanitizeSourceText` must strip or they corrupt the LLM translation:
  1. Angle tags: `<Color(r,g,b,a)>` / `</Color>` and any other `<tag>`.
  2. Square codes: `[98;5u` / `[ABC;255;5u`.
  3. Control-byte macros: `\x02…\x03` blocks (with `\x01` separators, `\x04` arg
     wrapper). Binary color params decode as U+FFFD and the macro command id survives
     as a literal ASCII letter (0x48/0x49 = Color). A literal letter is stripped only
     when **both** neighbours are markup chars (control / U+FFFD), so real words like
     `Collection` sitting next to a `\x03` survive.
- Diagnosing markup: `ScanAddonNodes` (Developer tab) prints a `raw=` line with escaped
  control bytes (`\xNN`) per text node. Note the Talk text lives in `GetTextNodeById(3)`
  and is **not** found by the tree-walk (only 2 nodes in the root tree), so raw dumps
  appear on the id loop, not the tree-walk section.
- `SanitizeSelfCheck()` (`#if DEBUG`) asserts known markup forms are stripped, run once
  at plugin startup; failures land in the diagnostics log.
- Target == client language means the plugin is fully inactive (capture + overlay off,
  status banner shows it) — see `Plugin.IsTranslationActive` and `MatchesClientLanguage`.
- MyMemory is the only free provider that can't auto-detect the source: it needs the game
  client's language code (`TargetLanguageCodes.ClientLanguageCode`), unlike Google
  Translate's `sl=auto`. It also caps at 500 bytes/request and 5k chars/day anonymous.
- `OverlayVerticalOffset` is auto-set when the position radio changes (−35 above / 0 on
  top / 15 below); default for new installs is −35 to match the default "above" position.

## UI conventions

Config window has 4 tabs: **Translation** (language + engine + endpoint fields +
instance toggle), **Content** (speaker-name toggle + per-surface toggles), **Display**
(overlay appearance + position), **Developer** (troubleshooting; hidden by default
behind a "Show Developer tab" checkbox at the bottom of the window). Endpoint
fields use a single shared read-only-value + Edit pattern (`DrawEndpointField`); keep
that consistency if adding providers. All `Save()` calls go through
`configuration.Save()`.