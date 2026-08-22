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

`MuvluvMod` patches only the **data / parameter layer** (`ScenarioController.GenerateFrames`,
`ScenarioHistoryCell.ApplyText`, `ScenarioChoiceElementComponent.Apply`, `MemoryDB.LoadMasterData`) and
never touches `TMP_Text`. This plugin patches only the **render layer** (`TMP_Text.set_text`). Data flows
one way — curated prefix → game logic → `set_text` → us — so the two never contend, and Harmony patch
ordering is irrelevant because they share no patch target.

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
| `Enable` | `true` | Whether translated text is *displayed*. Toggled at runtime with **F2**. |
| `DebugLogSeenText` | `true` | Log every distinct string observed, whether it contains kana, and whether it was queued. Useful for measuring coverage gaps; turn off once satisfied. |

### `[LLM]`

| Key | Default | Meaning |
|---|---|---|
| `Enable` | `false` | Master switch for machine translation |
| `Endpoint` | `http://127.0.0.1:11434/v1/chat/completions` | OpenAI-compatible chat completions URL |
| `Model` | `qwen2.5:7b` | Model name |
| `ApiKey` | *(empty)* | Sent as `Authorization: Bearer` when set |
| `TimeoutSeconds` | `30` | Per-request timeout |
| `RetryCount` | `3` | Total attempts per request |
| `RequestsPerSecond` | `2` | Outbound rate limit |
| `MaxInFlight` | `5` | Concurrent request ceiling |
| `TranslatePeriodSeconds` | `5` | Retry-scan period for failed items |
| `RefreshPeriodSeconds` | `0.5` | How often on-screen text is re-scanned and refreshed |

Changing any LLM setting reloads the translator in place — no restart needed.

## Hotkey

**F2** — toggle whether translations are *displayed*.

Turning it off restores original text, but **only for strings this plugin itself translated**. Text
translated by another mod is left untouched: we have no record of its original and must not corrupt it.

Translation continues in the background while display is off, so toggling back on is instant. Pressing
F2 also logs a progress snapshot (`completed / in-flight / failed`).

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

## Credits

Independent project. Interoperates with, but is not derived from and does not depend on,
[`anosu/MuvluvMod`](https://github.com/anosu/MuvluvMod).
