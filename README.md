# MuvluvLLMMod

LLM machine-translation fallback for **Muv-Luv Girls Garden**, delivered as a standalone BepInEx 6 IL2CPP plugin.

The plugin observes text at the render layer. Kana-bearing text can be translated through an
OpenAI-compatible chat endpoint asynchronously, with local caching, rate limiting, and retries.
It does not require, load, inspect, or coordinate with another plugin.

## Standalone render-layer boundary

This DLL has no compile-time or runtime dependency on another renderer or data mod, and has no
load-order requirement. Its fallback behavior is based only on the text arriving at the final
`TMP_Text` assignment:

- kana-free incoming text is not queued;
- kana-bearing text may enter the bounded pipeline only when its input and normalized-template
  budgets are accepted; and
- if another renderer or data mod has already supplied Chinese (or another kana-free value), this
  plugin does not actively queue that incoming value. A generated or filled result is used only
  after its final output budget is accepted.

Those are render-layer rules, not a guarantee that unrelated mods cannot affect the same UI or that
every game text path is covered. The plugin never reads another mod's data or state.

## What gets translated

A string is eligible for the bounded translation pipeline only when it contains Japanese kana
(hiragana, katakana, or half-width katakana) **and** its accepted input and normalized-template
budgets permit admission. The generated or filled output must pass its own bounded output check
before it is displayed or retained. Kana is necessary but not sufficient. Pure-kanji strings (for
example `提供割合`) are deliberately not translated; this keeps the candidate test conservative.

Game-produced text reaches the pipeline through two routes. Scenario dialogue is rewritten at the
frame document before the game consumes it, which is what the section below describes. Every other
string is translated when the final UI assignment is observed by this plugin's `TMP_Text` render
Prefix, so builder output and other data sources are covered once they reach that assignment.

## Scene translation

Scenario dialogue has a second route. A scene's frame documents are rewritten before the game
consumes them, so one request covers the whole scene and the model sees every line in order. That is
what keeps a speaker's tone, honorifics, and terminology consistent within a scene, and it turns
roughly seventy requests per scene into one.

Evidence for the seam is recorded in [`docs/scene-frame-evidence.md`](docs/scene-frame-evidence.md):
the game reads `SceneFrameMaster.ConfigurationJson` exactly once, and the frame factory runs before
`Refresh` publishes the frame view models.

Only dialogue `Text` is batched. Speaker and team names are few and highly repetitive, so the
per-string path already translates each of them once.

Validation of a scene response is all-or-nothing. The response must echo the protocol version and
scene id, carry exactly one entry per target in the same order, and restore to the exact protected
token sequence with matching markup. Any mismatch rejects the whole scene, and the dialogue is then
translated by the per-string path instead. A scene is never published half-translated.

The scene route shares the bounded text, request, and cache budgets with the rest of the plugin; a
scene payload has its own ceiling because one scene is larger than one line. `__MLM_FMT_n__` tokens,
including ruby tags such as `<r=ダイブ>潜航</r>`, are preserved exactly as in the per-string path.

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
| `Enable` | `true` | Whether this plugin's known translations are displayed. Toggled at runtime with **F2**; it does not stop translation production. |
| `SceneTranslation` | `true` | Sends a whole scene's dialogue as one request so terminology and tone stay consistent across it. Turning this off falls back to per-string translation. |

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

## Hotkey and display fallback

**F2** toggles whether this plugin's successfully provenance-recorded render-layer translations
are displayed. The toggle is a display operation only: when `[LLM] Enable` is true, production
continues while display is off so translations can be available when display is enabled again.

For an observed TMP assignment, display-off refresh can restore the source only when the object
identity, assignment generation, lifecycle epoch, and cache reverse mapping all validate. An
external setter starts a new assignment even when its value is identical. A plugin refresh uses a
one-shot token only around the exact setter write it owns. Out-of-budget or otherwise invalid
values remain the original text and are not provenance-recorded; ambiguity or failed validation is
a no-op that leaves the current text unchanged. The toggle does not claim text rendered outside an
observed TMP setter path or promise every game UI route.

## Runtime verification gates

The release behavior still needs game verification for:

- font fallback and glyph coverage for generated Chinese;
- whether every relevant UI path reaches the TMP setter; and
- real pooled-object F2 toggles, layout, and refresh cost.

The documented F2 behavior is limited to the observed TMP display path; it is not a claim of complete
in-game UI coverage.

## Status output

Console/log only — there is no in-game UI. A periodic line reports queue health:

```
[LLM] pending=… completed=… in-flight=… failed=… blocked=…
```

## Building

Requires only Docker; nothing is installed on the host. The game directory must be mounted **read-only**
and is never written to.

```bash
# plugin (net6.0, matching the BepInEx IL2CPP runtime)
docker run --rm \
  --mount type=bind,src=/path/to/MuvluvLLMMod,dst=/src \
  --mount type=bind,src=/path/to/game,dst=/game,readonly \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game

# legacy tests (net8.0; no game mount needed)
docker run --rm \
  --mount type=bind,src=/path/to/MuvluvLLMMod,dst=/src,readonly \
  -w / mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'cp -a /src /work && cd /work && dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release'

# exact-source production integration tests (deterministic; no game mount needed)
docker run --rm \
  --mount type=bind,src=/path/to/MuvluvLLMMod,dst=/src,readonly \
  -w / mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'cp -a /src /work && cd /work && dotnet test MuvluvLLMMod.IntegrationTests/MuvluvLLMMod.IntegrationTests.csproj -c Release'

# optional mutation gate; the script copies every mutant to a throwaway container path
# and deliberately expects each focused integration test to fail.
docker run --rm \
  --mount type=bind,src=/path/to/MuvluvLLMMod,dst=/src,readonly \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  bash scripts/mutation-gate.sh
```

`MuvluvLLMMod.IntegrationTests` links the checked-in production `.cs` files directly (the test
also asserts this project link and executes current linked budget behavior; it is not a copied
implementation). It includes `Patch.cs`, `Plugin.cs`, `Config.cs`, `Hotkey.cs`, `Logger.cs`,
`RetainedDelegate.cs`, `NativeDelegateCoordinator`, and all of their real translation/lifecycle
dependencies. It does not copy a parallel implementation. `Stubs/RuntimeStubs.cs` replaces only
external boundaries:
BepInEx configuration/logging/BasePlugin, Harmony discovery/ownership, Unity object/component
lifetime and time/input, the TMP type, the application-quitting add/remove calls, and the two
scenario target types. The fake `TMP_Text.text` setter calls the real `Patch.TranslateTmpSetter`
Prefix; fake application and config events call the real `Plugin` registration/removal and
`Config` generation-lease handler. The native delegate interleaving uses the real coordinator
with identity-preserving fake native storage. No game process or actual IL2CPP runtime is claimed
by this net8.0 harness; the production DLL build above remains the artifact/runtime check.

Build output goes to `artifacts/`. Game DLL references resolve from `$(GameDir)/BepInEx/interop/`.

## Provenance and licence

This project is independently authored and interoperates at the render boundary without depending
on another plugin's code, data, or state. The LLM translation core in this repository was authored
by us.

Licensed under the MIT License; see [`LICENSE`](LICENSE).
