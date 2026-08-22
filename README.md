# MuvluvLLMMod

LLM machine-translation fallback for **Muv-Luv Girls Garden**, as a standalone BepInEx 6 IL2CPP plugin.

It watches text as the game renders it, and for anything still containing Japanese kana, translates it
through an OpenAI-compatible chat endpoint — asynchronously, cached, rate-limited, and retried.

## Fully standalone

This plugin has **no dependency on any other mod**: no compile-time reference, no runtime reference,
no load-order requirement. It works in either configuration:

| Setup | Behaviour |
|---|---|
| **With** [`anosu/MuvluvMod`](https://github.com/anosu/MuvluvMod) installed | Acts as a *fallback tier* — machine-translates only what the curated translation repository does not cover |
| **Without** it | Acts as a *full* machine translator for all kana-bearing text |

Same DLL, no configuration change. Nothing to coordinate.

### Why it never fights other translation mods

`MuvluvMod` patches the **data / parameter layer** (`ScenarioController.GenerateFrames`,
`ScenarioHistoryCell.ApplyText`, `ScenarioChoiceElementComponent.Apply`, `MemoryDB.LoadMasterData`) and
never touches `TMP_Text`. It also installs prefixes on `ScenarioController.Refresh` and `Leave` to maintain
its own scenario-state flag. This plugin has separate prefixes on those same two methods for its own flag;
that deliberate overlap is harmless because each prefix only sets its own state and neither reads or changes
the other's state. The translation flow remains one way — curated data → game logic → `set_text` → us —
and the two plugins do not share any data or runtime dependency.

### What gets translated

A string is queued **if and only if it contains Japanese kana** (hiragana, katakana, or half-width
katakana). This single rule is the whole correctness story:

- already translated by another mod → no kana → skipped
- not covered by any mod → kana present → queued
- curated entry exists but is identical to the original → still Japanese → queued (correctly: no real translation was provided)
- curated download failed → still Japanese → queued

Pure-kanji strings (e.g. `提供割合`) are deliberately never translated — they are generally legible, and
skipping them keeps the rule free of false positives.

## Requirements

- BepInEx 6 (IL2CPP), tested against `6.0.0-be.785`
- An OpenAI-compatible `/v1/chat/completions` endpoint (Ollama, LM Studio, a proxy, or a hosted API)

## Install

Drop `MuvluvLLMMod.dll` into `<GameDir>/BepInEx/plugins/`. Launch once to generate
`BepInEx/config/muvluv.llmmod.cfg`, then set `[LLM] Enable = true` and point `Endpoint` at your model.

## Configuration

`BepInEx/config/muvluv.llmmod.cfg`

### `[Translation]`

| Key | Default | Meaning |
|---|---|---|
| `Enable` | `true` | Whether translated text is *displayed*. Toggled at runtime with **F2**; it does not stop translation production. |

### `[Translation.Debug]`

| Key | Default | Meaning |
|---|---|---|
| `DebugLogSeenText` | `false` | Diagnostic logging of observed text. Output is truncated, deduplicated within a fixed memory cap, and rate-limited; it is off by default. |

### `[LLM]`

| Key | Default | Meaning |
|---|---|---|
| `Enable` | `false` | Master switch for machine-translation production, independent of the display toggle |
| `Endpoint` | `http://127.0.0.1:11434/v1/chat/completions` | OpenAI-compatible chat completions URL |
| `Model` | `qwen2.5:7b` | Model name |
| `ApiKey` | *(empty)* | Sent as `Authorization: Bearer` when set |
| `TimeoutSeconds` | `30` | Per-request timeout |
| `RetryCount` | `3` | Total attempts per request |
| `RequestsPerSecond` | `2` | Outbound rate limit |
| `MaxInFlight` | `5` | Concurrent request ceiling |
| `TranslatePeriodSeconds` | `5` | Retry-scan period for failed items |
| `RefreshPeriodSeconds` | `0.5` | How often on-screen text is re-scanned and refreshed |
| `CacheDirectory` | `MuvluvLLMMod/cache` | Durable cache directory, relative to the plugin directory unless an absolute path is supplied; changing it requires a restart |

Changing a machine-affecting LLM setting (`Enable`, `Endpoint`, `Model`, `ApiKey`, `TimeoutSeconds`,
`RetryCount`, `RequestsPerSecond`, `MaxInFlight`, or `TranslatePeriodSeconds`) reloads the translator
in place. Display, debug, refresh-period, and cache-directory changes do not reload it; no restart is
needed for the first three, while a cache-directory change takes effect after restart.

## Hotkey

**F2** — toggle whether translations are *displayed*.

Turning it off restores original text, but **only on the TMP object whose current value this plugin
translated**. Restoration uses per-instance provenance rather than the translated value alone, so a
matching value on another object is left untouched. Text translated by another mod is also left alone:
we have no record of its original and must not corrupt it.

LLM production continues in the background while display is off because it is controlled by `[LLM] Enable`,
not by F2. Toggling back on is therefore instant, without restarting the worker. Pressing F2 also logs a
progress snapshot (`completed / in-flight / failed`).

F3–F5 are deliberately left alone for `anosu/MuvluvMod` to use.

## Status output

Console/log only — there is no in-game UI. A periodic line reports queue health:

```
[LLM] pending=… completed=… in-flight=… failed=… blocked=…
```

## Building

Requires only Docker; nothing is installed on the host. The game directory is mounted **read-only** and
is never written to.

```bash
# plugin (net6.0, matching the BepInEx IL2CPP runtime)
docker run --rm \
  --mount type=bind,src=/path/to/MuvluvLLMMod,dst=/src \
  --mount type=bind,src=/path/to/game,dst=/game,readonly \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game

# tests (net8.0; sources are loader-agnostic, no game mount needed)
docker run --rm \
  --mount type=bind,src=/path/to/MuvluvLLMMod,dst=/src \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release
```

Build output goes to `artifacts/`. Game DLL references resolve from `$(GameDir)/BepInEx/interop/`.

## Provenance and licence

This is an independent project that interoperates with, but does not depend on,
[`anosu/MuvluvMod`](https://github.com/anosu/MuvluvMod). The LLM translation core in this repository
was authored by us; that subsystem never existed in the other project's history.

Licensed under the MIT License; see [`LICENSE`](LICENSE).
