# Fixer context — standing brief

Read this file first. Your dispatch message names which ITEMs to do. Do only those.

## Process rules (non-negotiable)

- **Commit after EACH item.** Never batch. Agents on this task have been terminated at
  21–60 turns; only committed work survives. This is the single most important rule.
- Work items in the order given.
- Do not re-read files you don't need. Do not re-verify facts listed here. Budget is the
  scarcest resource — spend it on edits, not on rediscovery.
- Run the Docker build after each item; run the Docker tests after any item touching
  testable code.
- Do not re-review, re-litigate, or add scope.

## Hard constraints

1. `C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl` is the GAME INSTALL — **STRICTLY READ-ONLY**.
   Never write there. Always mount `readonly`.
2. `C:/Users/Eden/Muv-Luv/MuvluvMod` (third-party upstream) and
   `C:/Users/Eden/Muv-Luv/MuvluvModMod` (donor repo) are **READ-ONLY REFERENCES**.
3. All build/test via `docker run --rm`. Never run `dotnet` on the host. Never install on the host.
4. Prefer FastCtx tools (`fastctx_run`, `fastctx_inspect_local_file`, `fastctx_grep`, `fastctx_replace`).

## What this plugin is

`MuvluvLLMMod` — a **completely standalone** BepInEx 6 IL2CPP plugin that machine-translates
Japanese game text via an LLM. It patches **only** `TMP_Text.set_text` (render layer).

A separate, unrelated third-party plugin (`anosu/MuvluvMod`) translates via curated data at the
**data/parameter layer**. We are the fallback tier for whatever it did not cover.

### The central invariant — never break this

**ZERO coupling to the third-party plugin:** no `[BepInDependency]`, no reading its state, no
compile/runtime reference, and the literal string `MuvluvMod` must not appear in compiled output.

**Never patch these four third-party data-layer targets:**
`ScenarioController.GenerateFrames`, `ScenarioHistoryCell.ApplyText`,
`ScenarioChoiceElementComponent.Apply`, `MemoryDB.LoadMasterData`.

The existing `ScenarioController.Refresh` / `Leave` prefixes are a **permitted, approved
exception**, used only to track "is a scenario playing". The other plugin also patches these two,
only to set its own flag — two independent prefixes on one method is harmless.

### The correctness gate

A string is queued for translation **iff it contains Japanese kana**. That single rule is the
entire defence against re-translating text the other plugin already handled. It is already
verified character-for-character correct. Pure-kanji strings are deliberately never translated —
**do not add kanji detection**. We read no third-party data structure at all; that is what makes
zero-coupling possible.

### The display toggle

`Config.Translation` (default `true`) is the **display** toggle, flipped by **F2**.
Contract: **display only, never production.** The LLM must keep translating while display is off,
so toggling back on is instant.

## Established facts — trust these, do not re-derive

- `BepInEx.Unity.IL2CPP.BasePlugin.Unload()` is `public virtual bool Unload() => false;`
- Neither `IL2CPPChainloader` nor `BaseChainloader<T>` calls `Unload()` or hooks application quit.
  **BepInEx never invokes `Unload()` on normal game exit.**
- `AddComponent<T>()` delegates to `IL2CPPChainloader.AddUnityComponent<T>()`, attaching to a
  BepInEx-owned manager GameObject. Setting our own `Instance = null` does **not** destroy it.
- `TranslationCache.RunPersistenceLoopAsync` already persists periodically during play, so
  exit-time loss is bounded to the last dirty window — real, but not a whole session.
- 11 of the 13 copied core files are byte-identical to the donor repo. `TextTemplate.cs` and
  `TranslationResolver.cs` carry approved deltas. **Do not modify donor-copied files** unless an
  item explicitly grants an exception.
- Interop namespaces are **unprefixed**: `TMPro`, `UnityEngine`, `Assets.*`. Target install is
  BepInEx `6.0.0-be.785`, .NET 6.0.7, Unity 6000.0.59f2.

## REJECTED finding — do not implement

A review claimed `TMP_Text.set_text` should be a **Postfix** instead of `[HarmonyPrefix]`.
**REJECTED — the original spec was wrong, the code is right.**
`TranslateTmpSetter(TMP_Text __instance, ref string value)` is inherently a Prefix signature and is
the correct way to intercept a property setter; a Postfix would have to read `__instance.text` and
re-assign, causing recursion. **KEEP THE PREFIX.** (The separate re-entrancy defect in ITEM 4 is
still real and must be fixed.)

## Docker commands (verified working — `MSYS_NO_PATHCONV=1` is mandatory)

```bash
# build
MSYS_NO_PATHCONV=1 docker run --rm \
  --mount type=bind,src=/c/Users/Eden/Muv-Luv/MuvluvLLMMod,dst=/src \
  --mount type=bind,src=/c/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl,dst=/game,readonly \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c 'export DOTNET_CLI_TELEMETRY_OPTOUT=1; dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game'

# test
MSYS_NO_PATHCONV=1 docker run --rm \
  --mount type=bind,src=/c/Users/Eden/Muv-Luv/MuvluvLLMMod,dst=/src \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c 'export DOTNET_CLI_TELEMETRY_OPTOUT=1; dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release'
```

Baseline: build 0 warnings / 0 errors; tests **117 passed / 0 failed**.

---

# Worklist

Status legend: `DONE` / `WIP` / `TODO`

## ITEM 1 — `DONE` (commit `130c71a`) — BLOCKER: F2 restore provenance

Was: `TryGetSourceForTranslatedValue` keyed on the translated string alone, so F2-off could
replace another plugin's curated Chinese with our unrelated Japanese source (short strings like
确定/取消 collide readily). Fixed with per-TMP-instance provenance.

## ITEM 2 — `DONE` (commit `cbe5bde`) — BLOCKER: debug logging unbounded, unthrottled, default ON

`Patch.cs` ~18-20 / ~116-135, `Config.cs` ~89-93. `debugSeenText` was a process-lifetime
`HashSet<string>` of every distinct observed string, never capped. Dedupe is not rate limiting:
one scan of hundreds of unseen strings emits hundreds of synchronous main-thread log calls.

**A previous agent already created `MuvluvLLMMod/DebugTextLogPolicy.cs` and
`MuvluvLLMMod/EnqueueObservation.cs` and edited `Config.cs`, then was terminated. That work is
uncommitted and NOT yet wired into `Patch.cs`. Inspect it, finish it, wire it up, commit.**

Required:
- `DebugLogSeenText` default **`false`** (it is a diagnostic; shipping an unbounded default-on
  diagnostic is unacceptable).
- Fixed-capacity, thread-safe deduper (LRU/ring, hard cap ~2048).
- Token-bucket rate limiter (~≤10 lines/sec) with periodic "N lines suppressed" summary.
- Truncate logged text to ~80 chars.
- Fix the misleading `enqueued` field: while the LLM is disabled, work IS added to durable cache
  pending state but `MachineLifecycle.EnqueueNormal` returns false, so the log printed
  `enqueued=false`. Distinguish "durably pending" from "accepted by a live worker".

## ITEM 3 — `DONE` (commit `6d49ebb`) — BLOCKER: make shutdown a real, idempotent lifecycle

`Plugin.cs` ~78 / ~83-115, `Hotkey.cs` ~9-31. Three defects:
(a) `Instance = null` leaves the injected `Hotkey` alive on BepInEx's manager object, so it keeps
calling `Patch.RefreshAllTmpText()` every 0.5s after cleanup/unpatch;
(b) `return base.Unload()` returns **false** after destructive cleanup;
(c) nothing runs freeze/cancel/flush on normal game exit.

Required:
- ONE idempotent cleanup routine guarded by `Interlocked` (runs at most once).
- Called from `Unload()` **and** an application-quit path — `UnityEngine.Application.quitting`,
  an `OnApplicationQuit` on the injected component, or `AppDomain.CurrentDomain.ProcessExit`.
  Pick what actually works under IL2CPP; state which and why.
- Disable **and** `UnityEngine.Object.Destroy` the injected component; null-guard its `Update` so
  any survivor is inert.
- Preserve ordering: `Cache.FreezeMutations()` → `MachineLifecycle.Shutdown()` → cancel
  persistence CTS → `Cache.Flush()` → `UnpatchSelf()`.
- `Unload()` must `return true` on successful cleanup.
- Guard against duplicate components if `Load()` runs twice (two `Hotkey`s would double-toggle F2
  and double-scan).

## ITEM 4 — `DONE` (commit `ba18552`) — MAJOR: `RefreshAllTmpText` double-processes every changed object

`Patch.cs` ~90-115. It calls `TranslateTmpSetter(text, ref value)` as a plain method; that method
sets and then **clears** `translatingTmp` in its own `finally`; the following `text.text = value`
therefore re-enters the Harmony prefix. Double work per changed object, and possible
double-observe/enqueue.

Fix: extract the shared resolution logic into a private method that does **not** touch the guard;
have both the Harmony prefix and the scan call it, with the scan holding `translatingTmp` across
its own assignment. Never invoke a Harmony patch method directly as a normal call.

## ITEM 5 — `DONE` (commit `d3fb4cb`) — MAJOR: F2 must not restart the translator

`Hotkey.cs` ~16-22, `Config.cs` ~95 / ~107-116, `Plugin.cs` ~118-128. Flipping
`Config.Translation` raises `SettingChanged`, whose handler unconditionally reloads the machine
translator, cancelling in-flight HTTP. Violates the display-only contract. Same needless reload
fires for `RefreshPeriodSeconds` and `DebugLogSeenText`.

Fix: reload **only** for machine-affecting entries — `LLM.Enable`, `Endpoint`, `Model`, `ApiKey`,
`TimeoutSeconds`, `RetryCount`, `RequestsPerSecond`, `MaxInFlight`, `TranslatePeriodSeconds`.

**Connected defect (must fix together):** the LLM's effective enabled state was
`Config.Translation.Value && Config.LlmEnable.Value`. Since production must continue while display
is off, that coupling is itself wrong — **decouple it so enablement depends on `LlmEnable` only.**
Without this, F2-off silently halts production and ITEM 5 is only cosmetically fixed. Check
nothing else depended on the old coupling.

## ITEM 6 — `DONE` (commit `7cc0472`) — MAJOR: blocked templates survive reload

`Plugin.cs` ~23-27 / ~160-168, `TranslationRetryPolicy.cs` ~54-70 / ~82-99. A single
`static readonly TranslationRetryPolicy` is shared across all reloads; after 3 failed cycles a
template is blocked for the process lifetime. Correcting a wrong endpoint/model/API key reloads the
worker but reuses the blocked state, so those strings stay untranslated until the game restarts.

Fix: on a **material LLM configuration change**, atomically reset or replace the retry policy
alongside the worker. Do **not** reset it for F2/debug/refresh changes. Add a unit test.

## ITEM 7 — `DONE` (commit `916a3dd`) — MAJOR: bound the cache's runtime indexes

`TranslationCache.cs` ~17-25 / ~282-307 / ~324-337. `generated` is bounded by normalized templates,
but `raw`, `sourceByTranslatedValue`, and `knownTranslatedValues` retain every exact runtime
source/translation pair. A kana-bearing countdown or changing status string adds permanent entries
every refresh tick, forever, across a multi-hour session.

Fix: impose explicit retention limits — LRU-bound the runtime reverse indexes; normalize/sample `raw`.

> **Explicit exception granted:** `TranslationCache.cs` is a byte-for-byte donor copy. The
> byte-copy rule is waived **for this item only**. Keep the diff minimal and surgical; do not
> reformat or restructure. Note the exception in the commit message.

## ITEM 8 — `DONE` (commit `835aa23`) — MAJOR: terminal flush can silently lose data

`Plugin.cs` ~90-106, `TranslationCache.cs` ~251-275 / ~403-427. After the persistence CTS is
cancelled, shutdown calls `Flush()` once. On a failed atomic write `Flush()` only re-signals
`dirtySignal` — whose sole consumer is already cancelled — and returns no success indicator.
Cleanup then reports success with dirty state stranded in memory.

Fix: terminal flush must report success/failure, retry synchronously a bounded number of times
after mutations are frozen, and log clearly if data is unrecoverable. Add a test injecting one
failed final write followed by a success.

## ITEM 9 — `DONE` (commit `a0b0c5e`) — MAJOR: `Load()` has no rollback

`Plugin.cs` ~52-80. Persistence and machine workers start *before* Harmony patching and component
injection. If `Patch.Initialize` or `AddComponent` throws, BepInEx drops the plugin without calling
our cleanup, leaving workers and partial patches live.

Fix: make initialization transactional — prefer starting workers only *after* patching/component
setup succeeds; wrap staged init in `try/catch`, invoke ITEM 3's idempotent cleanup on failure,
then rethrow.

## ITEM 10 — `DONE` (commit `b2a74d4`) — MINOR: pin the kana boundaries in tests

`MuvluvLLMMod.Tests/TextTemplateTests.cs` ~156-173. Implementation is correct but tests omit most
endpoints/neighbours. Add a numeric-codepoint `[Theory]` asserting BOTH inclusive endpoints AND
both immediately-excluded neighbours for every range:

| range | included endpoints | excluded neighbours |
|---|---|---|
| hiragana | U+3041, U+3096 | U+3040, U+3097 |
| hiragana iteration | U+309D, U+309F | U+309C, U+30A0 |
| katakana | U+30A1, U+30FA | U+30A0, U+30FB |
| katakana iteration | U+30FD, U+30FF | U+30FC, U+3100 |
| half-width katakana | U+FF66, U+FF9D | U+FF65, U+FF9E |

Plus all four combining marks U+3099–U+309C must be excluded. This predicate is the single gate of
the whole system; an off-by-one silently breaks everything.

## ITEM 11 — `DONE` (commits `2ec56d4`, `43bdfad`, `39deec9`, `a13bbd9`) — MINOR: make the new integration testable, and test it

Completed the deliverable (steps 1–4) without changing loader runtime semantics:

- `2ec56d4`: linked `TmpTranslationProvenance` and added collision, external-overwrite,
  unresolvable-source, bounded-eviction, and concurrent-access tests. Test count: 150 passed.
- `43bdfad`: linked `DebugTextLogPolicy` and added fixed-memory, token-bucket,
  suppression-summary, 80-character truncation, flag-propagation, and concurrent-access tests.
  Test count: 155 passed.
- `39deec9`: extracted `ConfigReloadPolicy`, routed `Config` through it, and tested every
  machine-affecting setting plus F2/debug/refresh/cache/unknown exclusions. Test count: 172 passed.
- `a13bbd9`: linked `Translation` and `EnqueueObservation` with loader-free test doubles and
  tested display-off observation/enqueue, durable-pending versus live-worker acceptance,
  generated lookup, and scenario priority routing. Test count: 176 passed.

Final Docker test suite: **176 passed / 0 failed** (145-test baseline; +31). Bonus step 5
(`Plugin` lifecycle state machine and remaining `Patch` restore decision) was not attempted;
the requested steps 1–4 deliverable is complete and those pieces remain loader-coupled.

## ITEM 12 — `DONE` (commit `ae3ee97`) — MINOR: verify all four patch targets

`Patch.cs` ~138-155. `VerifyPatches` checks only the TMP and skill-description targets while four
are applied, so a missing `Refresh`/`Leave` hook would silently break priority routing yet still
log success. Include all four and report exact targets.

## ITEM 13 — `DONE` (commit `8115d17`) — MINOR: pin dependency versions

`MuvluvLLMMod.csproj` ~30-31 uses floating `6.0.0-be.*` and `2.*`. Target install is BepInEx
`6.0.0-be.785`. Pin `BepInEx.Unity.IL2CPP` to `6.0.0-be.785`, pin the props package. Confirm the
Docker build still passes.

## ITEM 14 — `DONE` (commit `918736c`) — docs + licence

Fix `README.md`:
- It claims the two plugins share no Harmony target — **false**, both patch
  `ScenarioController.Refresh` and `Leave`. Describe this as a deliberate, harmless exception (two
  independent prefixes, each setting only its own state flag) rather than claiming zero overlap.
- `DebugLogSeenText` is documented under `[Translation]` but bound under `[Translation.Debug]` —
  correct it, and update its default to `false` with a note that it is a diagnostic.
- Document the exposed `CacheDirectory` option (and whether changing it needs a restart).
- Update anything items 1–13 changed (F2 semantics; LLM enablement no longer gated on the display
  toggle).

Add a `LICENSE`. Provenance findings: neither the third-party upstream nor the donor repo ships a
licence file; however the 13 copied files are the LLM subsystem, which **never existed in upstream
in any commit of its entire history** — they are our own original work. Replace the blunt
"not derived from" wording with a precise statement: this project is independent, interoperates with
but does not depend on the other plugin, and its translation core was authored by us. Choose MIT
unless there is a reason not to; state what you chose.

---

# ROUND 2 — final gate review returned DO-NOT-SHIP

All 14 items above are `DONE` (26 commits, clean rebuild: build 0/0, tests 176 passed).
A second adversarial review then found the original BLOCKER-1 only **partially** closed, plus
three new MAJORs and two MINORs. Five fixes are required before the next ship gate.

Outcome per original finding: **CLOSED** — MAJOR-1(re-entry half), MAJOR-2, MAJOR-3, MAJOR-4,
MAJOR-5, MINOR-1, MINOR-3, MINOR-4. **PARTIALLY CLOSED** — BLOCKER-1, BLOCKER-2, BLOCKER-3,
MAJOR-6, MINOR-2, MINOR-5. **Zero-coupling invariant survived intact.**

## FIX-1 — `TODO` — BLOCKER: pooled same-instance/same-value still restores the wrong source

`Patch.cs:56-86`, `TmpTranslationProvenance.cs:66-109`.

Deterministic corruption sequence:
1. Instance `N` translated by us: `確認する → 确定`; provenance records `(N, 确定, 確認する)`.
2. The same TMP component is **pooled/reused** for unrelated curated text whose value is *also* `确定`.
3. `InvalidateIfTextChanged` sees `__instance.text` still equals `确定`, so it **keeps** the stale entry.
4. The incoming value is also `确定`, and the reverse cache still resolves it to `確認する`.
5. Display-off → `TryRestore` succeeds → unrelated curated `确定` becomes `確認する`.

This needs **no** instance-ID collision. Unity pooling deliberately keeps the same ID because it is
the same component. The missing dimension is **assignment/content generation**, not object identity.
`ScenarioHistoryCell` is exactly such a pooled list cell.

Capacity eviction is safe (an evicted entry restores nothing), but a **failed** source validation
currently leaves the stale entry resident, so it can reactivate if the reverse mapping reappears.

Required:
- Distinguish **external** setter calls from **plugin-owned refresh** restoration. Any unguarded
  external setter must invalidate prior provenance *before* resolving — **even when the incoming
  value equals the recorded translation.** Our own writes are identifiable because they go through
  the guarded refresh path.
- Restore **only** from the plugin-owned refresh path.
- Bind provenance to a validated identity/generation, not a bare integer ID.
- **Discard entries permanently on failed validation.**
- Every failure mode must be a **no-op**, never "restore the wrong thing".
- Extract the restore decision so it is testable without Unity, and test same-instance/same-value
  reuse plus external-setter-vs-refresh origin.

## FIX-2 — `TODO` — MAJOR: debug deduper has a count cap, not a memory cap

`DebugTextLogPolicy.cs:22-23, 75, 93, 147-156`. The dictionary and linked list retain the **complete**
input string; truncation happens only when building the log decision. 2,048 arbitrarily long strings
stay rooted after their TMP owners release them. Lock and token accounting are otherwise sound
(suppression summaries correctly consume tokens).

Fix: deduplicate on fixed-size hashes or bounded keys, retaining only already-truncated display text.
A hash collision may conservatively suppress a diagnostic line; it must never require storing the
full original string.

Also in scope (was MINOR): `acceptedByLiveWorker` is untruthful for scenario-priority work —
`Plugin.cs:264-268`, `MachineTranslatorLifecycle.cs:73-80`. With the LLM disabled, `current` is null
yet priority work returns `true` after being retained in `priorityBacklog`. Return a richer result
(`LiveWorker` / `Backlog` / `Rejected`) or rename to `AcceptedByScheduler`. Durable-pending state is
already reported correctly and separately.

## FIX-3 — `TODO` — MAJOR: the IL2CPP quit handler cannot be removed

`Plugin.cs:33, 87, 118`. The game's `Application.quitting` is an `Il2CppSystem.Action`;
`ApplicationQuittingHandler` is a `System.Action`, and both registration and removal implicitly
convert it. **Il2CppInterop allocates a fresh native delegate target and method-info object on every
conversion**, so the delegate built at line 118 never compares equal to the one added at line 87 and
subtraction cannot remove it.

Normal quit still fires, but explicit unload and failed-load rollback leave a native callback rooted;
repeated unload/reload cycles accumulate callbacks and make rollback incomplete.

Fix: convert **once**, retain that exact `Il2CppSystem.Action`, pass the same native delegate to both
`Application.add_quitting` and `Application.remove_quitting`, and clear the field only after a
successful removal.

(Verified read-only against the game's interop assemblies: `Application.quitting` and
`Internal_ApplicationQuit` genuinely exist — the defect is removal identity, not hook selection.)

## FIX-4 — `TODO` — MAJOR: lifecycle shutdown is not terminal or quiescent

`Plugin.cs:137, 174-177, 207-218`, `MachineTranslatorLifecycle.cs:47-60, 117-171`. Two races:
- Configuration stays **subscribed** until after machine shutdown. A machine-setting change between
  shutdown and `Config.Shutdown` obtains a newer lifecycle generation and can start a worker after
  cleanup, against a frozen cache.
- Cleanup resets `loadStarted` **without awaiting in-flight transition tasks**. A stale transition
  takes `current` and stops it before checking its generation, so after a rapid unload/reload it can
  capture and stop the **newly initialised** worker.

`ReloadMachineTranslator` never checks `IsCleaningUp`, and `MachineTranslatorLifecycle.Shutdown` is
not a terminal state.

Fix: reject reload/enqueue after lifecycle shutdown; unsubscribe or suppress configuration reloads
**before** machine shutdown; await/settle current and queued transitions; and either create a fresh
lifecycle object per successful load or provide a tested complete reset.

## FIX-5 — `TODO` — MINOR but load-bearing: the new tests include a vacuous one, and miss the
production paths

- `MuvluvLLMMod.Tests/DebugTextLogPolicyTests.cs:81-100` configures 10,000 lines/sec for 5,000 inputs
  then asserts the logged count is between 0 and 5,000 — **a condition that cannot fail** and so
  cannot detect missing or racy throttling. It also asserts only retained entry *count*, never
  retained key *size*.
- `TmpTranslationProvenanceTests.cs:7-22` would catch a structure keyed only by translated value, but
  it **does not invoke `Patch.cs`** — reverting or bypassing the production wiring leaves all tests
  green. Its overwrite test uses a *different* replacement value, so it misses the same-instance/
  same-value case that is FIX-1.
- `Patch` restore behaviour and `Plugin` lifecycle sequencing remain untested (ITEM 11 step 5 was
  never attempted); those omissions are what concealed FIX-1, FIX-3 and FIX-4.

Fix: add concurrent unique inputs at a single timestamp and assert normal + summary lines cannot
exceed available tokens; add bounded-key-size assertions; and add behavioural tests for the `Patch`
restore decision (external-setter vs refresh origin) and for lifecycle rollback, ordering, and races.

## Also outstanding

`README.md` currently claims other-mod text is always left alone. That is **false at HEAD** while
FIX-1 is open. Correct it as part of FIX-1, not before.

---

# Remaining unverifiable-without-the-game risks

Carry these forward; do not attempt to fix them blind. The round-2 review expanded this into an
actionable hand-off — for each, what to look for and what indicates failure. Highest-value first:
pooled-cell F2 behaviour (item 7) and glyph fallback (item 1).

1. Whether the other plugin's global `TMP_Settings.fallbackFontAssets` renders all our generated
   Chinese (we deleted a 415-LOC font/style subsystem on the assumption it does).
2. Whether the `TMP_Text.set_text` hook observes every untranslated Japanese string.
3. Real cost of the inactive-inclusive full TMP scan every 0.5s.
4. Whether the IL2CPP-injected component reliably receives `Update` and Input System F2 events.
5. Actual Harmony/native setter behaviour and ordering, incl. skill-description and scenario hooks.
6. Whether `Refresh`/`Leave` accurately bracket every scenario lifecycle.
7. Live F2 visual behaviour and real-world frequency of reverse-map collisions with curated Chinese.
8. Cache-directory permissions and graceful application-quit behaviour in this install.
9. Real endpoint cancellation, model format compliance, latency, and translation quality.
10. Chinese layout, clipping, wrapping, and style suitability after the font/style removal.
