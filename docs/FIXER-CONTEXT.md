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

## FIX-1 — `DONE` — BLOCKER: pooled same-instance/same-value still restores the wrong source

Implemented in commits `c84f1e2` (pure generation-aware restore decision), `21ee914`
(production wiring and README correction), `eb5f7be` (behavioral and concurrency coverage), and
`00e8fc4` (explicit external-origin coverage). The final Docker suite is **181 passed / 0 failed**;
the plugin build is **0 warnings / 0 errors**.

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

## FIX-2 — `DONE` (commit `7feac9c`) — MAJOR: debug deduper has a count cap, not a memory cap

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

## FIX-3 — `DONE` (commit `d7dfe4d`) — MAJOR: the IL2CPP quit handler cannot be removed

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

## FIX-4 — `DONE` (commit `06584c2`) — MAJOR: lifecycle shutdown is not terminal or quiescent

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

## FIX-5 — `DONE` — commits `f00a21a`, `74de3cc`, `e672502`, `97c1616`, `cc5518b`, `329c433`, `c2b2037`, `6af8ef8` — MINOR but load-bearing: test the production paths

Replaced the vacuous debug test with 16 genuinely concurrent workers feeding 16,000 unique,
long inputs at one timestamp. The test asserts the exact 37-line normal-plus-summary token
ceiling, a 64-entry retention ceiling, fixed `ulong` keys, and 80-character retained display
text. Throwaway-container mutation runs made the test red when throttling or bounded display
retention was removed.

Extracted and linked the loader-free `TmpRestoreDecision`, `PluginLifecycleGate`, and
`RetainedDelegate<T>` policies. Added behavioral coverage for:

- external-setter versus plugin-refresh restore origin, including same-instance/same-value pool
  reuse and permanent failed-validation no-ops; a production-wiring surface test goes red when
  `Patch.cs` bypasses the extracted decision;
- cleanup ordering (`Config.Shutdown` → freeze → machine shutdown → persistence cancellation →
  terminal flush → unpatch), at-most-once concurrent teardown, failed-step continuation, and
  `Load()` rollback after partial initialization;
- terminal lifecycle rejection of late machine-setting reloads and a cancellation-sensitive stale
  generation transition that must not stop a newly initialized worker;
- quit-delegate conversion/retention once, add/remove reference identity, and conditional clearing
  after removal success or failure.

The actual BepInEx/Unity loader, Harmony/native TMP setter, and IL2CPP native quit registration
cannot execute in the net8.0 test host; those game-bound operations remain explicitly untested.
Their extracted decision, ordering, generation, and delegate-retention behavior is tested without
pretending to validate native registration.

Final Docker suite after these commits: **199 passed / 0 failed**; plugin build: **0 warnings / 0 errors**.


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

## Follow-up — terminal enqueue guard mutation gap

The orchestrator's independent mutation testing found that removing the
`EnqueuePriority(string, long)` terminal-shutdown guard left the original **199-test** suite
passing. Commit `c398c6a` adds terminal rejection coverage for all four enqueue overloads,
including absence of retained backlog/retry state. The tests also pin `Initialize`'s terminal
rejection. In throwaway Docker copies (the repository was never mutated), the results were:

- `EnqueuePriority(string)`: baseline **201 passed**, mutant **198 passed / 3 failed**, restored **201 passed**.
- `EnqueuePriority(string, long)`: baseline **201 passed**, mutant **199 passed / 2 failed**, restored **201 passed**.
- `EnqueueNormal(string)`: baseline **201 passed**, mutant **200 passed / 1 failed**, restored **201 passed**.
- `EnqueueNormal(string, long)`: baseline **201 passed**, mutant **200 passed / 1 failed**, restored **201 passed**.
- `Initialize` terminal guard: mutant **200 passed / 1 failed**, restored **201 passed**.

`ReloadMachineTranslator` was checked as well; its terminal path delegates to the already-tested
terminal `MachineTranslatorLifecycle.Reload` rejection, so no additional unpinned guard was found.

---

## B-1 follow-up — TMP setter ownership and lifecycle provenance

**Status: DONE (feature commit `25e6845`; companion tests/docs commit is included with this status update).** This batch addresses only
B-1 from `docs/FINAL-REVIEW.md`; B-2, M-1, M-2, and the other outstanding review IDs remain
unchanged for later batches.

The broad thread-wide `translatingTmp` flag was removed. `TmpPluginWriteOwnership` now registers a
per-thread, one-shot token immediately before the exact `text.text = value` issued by
`RefreshAllTmpText`. The TMP Prefix consumes that token only once when object reference, native
instance ID, provenance assignment generation, provenance lifecycle epoch, and token lifecycle
match. A consumed token cannot cover a nested setter; a mismatch is processed as an external
setter. All non-token Prefix calls therefore enter `BeginExternalSetter` before resolution,
including byte-identical assignments and synchronous callbacks from resolution or logging.
The scope is disposed with `using`, including exception/no-prefix paths.

`TmpTranslationProvenance.ResetForLifecycle` retires all entries and advances an epoch. Patch load
resets provenance and ownership before publishing Harmony hooks; cleanup retires them before
`UnpatchSelf`. Patch runtime state relevant to this path (active epoch, scenario flag, scan count,
and outstanding token generation) is reset as part of those transitions. An assignment made while
hooks are absent consequently cannot find an entry from the previous lifecycle after reload.

### Regression coverage

The loader-free ownership/provenance tests cover:

- exact plugin-refresh setter bypass and one-shot consumption;
- nested other-TMP and same-TMP external setters, including byte-identical `确定`;
- target/ID/generation/lifecycle token mismatch as external, plus scope cleanup on exception and
  when no Prefix consumes the token;
- unload/clear/reload stale-entry non-restoration; and
- the normal valid F2-off restore path.

`PatchSurfaceTests` additionally require the real production Prefix wiring, require the
`[HarmonyPrefix]` TMP target, reject the old broad guard, and verify that `Patch.Retire` is ordered
before production unpatch. The test project links the actual loader-free ownership component;
there is no Unity/game process dependency. The native IL2CPP/Harmony boundary remains covered by
source/wiring assertions rather than a fake claim of execution in net8.0.

### Docker validation

Both validations copied the read-only repository mount into a throwaway container directory.
The game directory was mounted read-only for the plugin build and was never launched or written.

```text
MSYS_NO_PATHCONV=1 docker run --rm \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/MuvluvLLMMod,dst=/src,readonly \
  -w / mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'cp -a /src /work; cd /work; dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release'

PASS — 209 passed, 0 failed, 0 skipped.

MSYS_NO_PATHCONV=1 docker run --rm \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/MuvluvLLMMod,dst=/src,readonly \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl,dst=/game,readonly \
  -w / mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'cp -a /src /work; cd /work; dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game'

PASS — build succeeded, 0 warnings, 0 errors.
```

## B-2 / M-1 follow-up — DONE (commits `4474896`, `a45f1f6`)

This batch closes only the outer generation/lifecycle and IL2CPP quit-delegate findings from the
final review. `FINAL-REVIEW.md` is unchanged.

### B-2 design and production wiring

`PluginLifecycleGate` is now a loader-free, generation-bearing state machine with explicit
`NotLoaded`, `Loading`, `Running`, `Stopping`, `Stopped`, and terminal `Failed` states. Every
load gets a `PluginGeneration` cancellation token, owner, stage count, shared cleanup completion,
and generation leases. `Plugin.Load` stages all cache/resolver, Harmony, Hotkey, quit callback,
persistence, and machine resources through that generation, publishes the owner only at
`Running`, then activates the TMP hook and config lease. A cleanup moves the generation to
`Stopping` first, cancels it, waits for load/activation stages and in-flight lease callbacks,
then runs the owned teardown. Later stages cannot enter or publish after that boundary.
Concurrent cleanup callers wait for the same completion/result; any failed teardown leaves the
generation `Failed` and blocks a replacement load.

Configuration is bound before startup but subscribed only after `Running`. Its event callback
captures and enters the owning generation lease, so unsubscribe revokes stale callbacks instead
of merely removing the event delegate. Reload resolves the lifecycle and cache from that lease's
owner rather than a replaceable static side channel. `ConfigChangePolicy` formats ApiKey events
from their definition and never includes `BoxedValue`, including for stale entries. Machine
translator lifecycle initialization is explicitly single-owner and rejects duplicate initialize;
progress is exposed from the active lifecycle and is zero after terminal shutdown.

Teardown preserves the required order: configuration stop, quit callback/component/TMP
retirement, cache freeze, machine shutdown, persistence cancellation/await, terminal flush, and
Harmony unpatch. Persistence is awaited before terminal flush. `Patch` remains a Prefix and is
runtime-inert until the generation is Running and also rejects calls as soon as cleanup enters
`Stopping`.

### M-1 design and tests

`NativeDelegateCoordinator<T>` serializes conversion, native add/remove, retention, generation
ownership, and state publication under one lock. It exposes `Unregistered`, `Registering`,
`Registered`, `Removing`, and `Failed`; registration is published only after native add succeeds,
and failed removal retains the exact delegate and prevents a new generation from registering.
Production `Plugin` uses this coordinator for the retained `Il2CppSystem.Action` and passes the
same object to native add and remove.

Behavioral tests now cover cleanup during Loading, cancellation/rejection of later stages, stale
leases, same-result concurrent cleanup, failed-cleanup quarantine, duplicate machine initialize
without an orphan worker, terminal machine snapshots, ApiKey redaction, and forced native
remove/register interleaving with exact delegate identity. Production surface tests require the
actual Plugin coordinator/lease wiring; they are supplemental to the loader-free behavior tests,
not a replacement for them.

### Docker validation for `4474896` + `a45f1f6`

Both commands copied a read-only repository mount into a throwaway container. The game directory
was mounted read-only for the build, and no game process was launched.

```text
MSYS_NO_PATHCONV=1 docker run --rm \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/MuvluvLLMMod,dst=/src,readonly \
  -w / mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'cp -a /src /work && cd /work && dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release'

PASS — 220 passed, 0 failed, 0 skipped.

MSYS_NO_PATHCONV=1 docker run --rm \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/MuvluvLLMMod,dst=/src,readonly \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl,dst=/game,readonly \
  -w / mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'cp -a /src /work && cd /work && dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game'

PASS — build succeeded, 0 warnings, 0 errors.
```

## M-2 follow-up — DONE (commit `cbbe784`)

Removed the `SkillDescriptionBuilder.GetDescription` Postfix and its Harmony verification target.
Skill descriptions now remain untouched until their final `TMP_Text.set_text` assignment, where the
existing render Prefix resolves and records provenance. This keeps F2 display-off restoration on the
single object-bound, generation-validated render path; it does not infer ownership from translated
strings or add a pre-render handoff. `SkillRenderFlowTests` exercises generated skill output through
that final assignment and restores the source on the plugin refresh path when display is off.

Docker validation used read-only source/game mounts and did not launch the game:

```text
PASS — `dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release`: 221 passed, 0 failed, 0 skipped.
PASS — `dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game`: 0 warnings, 0 errors.
```

## M-6 follow-up — DONE (commit `3660de6`)

Rewrote the release README around the standalone render-layer contract. It no longer names or
assumes a particular upstream project, internal patch methods, or other mod hotkeys, and it avoids
unverified "never fights"/complete-coverage guarantees. It documents the tested kana-free incoming
text rule, the object/generation/epoch fail-closed F2 behavior, continued production while display
is off, and the remaining runtime gates for font fallback and TMP coverage. `ReadmeSurfaceTests`
protects the generic wording and rejects upstream-coupled terms.

Docker validation used read-only source/game mounts and did not launch the game:

```text
PASS — `dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release`: 222 passed, 0 failed, 0 skipped.
PASS — `dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game`: 0 warnings, 0 errors.
```

## N-1 follow-up — DONE (commit `541c26f`)

Harmony verification now produces a structured result for every required target: target discovery
and ownership are both required. `Patch.Initialize` logs the exact target set and throws
`HarmonyPatchVerificationException` on any missing target or owner; `Plugin.Load` therefore enters
its existing generation cleanup path before Hotkey, persistence, or machine resources are started.
The three remaining targets (TMP setter plus the two scenario-priority hooks) are all required.
`PatchVerificationPolicyTests` simulates missing target and missing owner evidence, verifies the
structured failure, and exercises rollback intent; production surface coverage pins verification
before runtime resource creation.

Docker validation used read-only source/game mounts and did not launch the game:

```text
PASS — `dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release`: 226 passed, 0 failed, 0 skipped.
PASS — `dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game`: 0 warnings, 0 errors.
```

## M-3 / M-4 / N-2 follow-up — DONE (commits `cc54749`, `b78ca4a`, `b459e9f`)

This batch changes only the bounded-memory, terminal-persistence, and shutdown-timeout paths.
The input-boundary follow-up is commit `bc7fa29`; the response-diagnostic follow-up is
`63e5c48`.
The earlier TMP ownership/epoch, generation/quarantine, F2 display-only, and required-patch
verification behavior remains in place.

### Runtime budgets and fail-closed rules

`TranslationBudget` is production code used by the render, cache, worker, and HTTP paths; these
are not source-test-only limits:

- Every source, normalized template, translation, reverse-index source/value, and provenance
  source/value is limited to **4,096 UTF-16 code units and 16 KiB UTF-8**. Oversize input is
  returned/left unchanged and is not queued or retained. Markup and placeholder checks still
  run for every accepted generated/LLM value.
- LLM response bodies are streamed with a **128 KiB** byte ceiling before JSON parsing; LLM retry
  attempts are clamped to **1–8**. Oversize bodies and invalid formatting fail closed.
- `generated` is an LRU with **4,096 entries / 4 MiB UTF-8 payload**. `pending` and
  `runtimePromotedPending` are each bounded to **2,048 entries / 512 KiB**. `raw` is bounded to
  **2,048 normalized samples / 512 KiB**. A full pending/priority budget leaves the incoming
  display text unchanged; the next observation may reconsider it.
- The reverse index (including known/ambiguous sets and provenance source mappings) is bounded to
  **4,096 entries / 1 MiB**. `TmpTranslationProvenance` retains at most **2,048 wrappers/slots /
  512 KiB of source+translation text** and reports its retained-byte metric.
- Retry state is bounded to **4,096 templates / 1 MiB**; the work queue's scheduled, deferred,
  completed, and cancellation maps are each bounded by the production limits (**4,096 work
  items / 1 MiB**, **4,096 cancellation entries / 1 MiB**). Priority backlog is
  **2,048 items / 512 KiB**. Failed-progress history is **2,048 templates / 512 KiB**.
  Worker concurrency is clamped to **16**, and at most **16** lifecycle transitions are retained.
  Historical retry/completed/cancellation/progress entries are evicted or rejected; active
  queue cancellation state is admission-bounded rather than evicted, while bounded transition
  cutoff history may expire old diagnostics safely.
- Cache files and serialized snapshots are each capped at **8 MiB**. Reads use a bounded
  `FileStream` path (not `File.ReadAllText`); oversize/malformed input is diagnosed and ignored
  before it can be materialized into the runtime collections. Snapshot counts and payload bytes
  are bounded before serialization. Budget diagnostics retain at most 32 fixed codes and are
  rate-limited to one emission per code per second.

`TranslationCache.RetainedSnapshot`, queue/backlog/retry/progress metrics, and provenance byte
metrics are used by pressure tests. `TranslationBudgetTests` covers long text, an oversized
simulated file, more than every relevant cap, and concurrent unique observations; normal retry,
cancellation, priority, F2, and translation tests remain green.

### Durable persistence and recovery protocol

`cache.state.v1.json` is the single authoritative atomic state snapshot containing generated,
pending, and raw state. It is written through `cache.state.v1.json.tmp` and replaced atomically;
the previous valid state is retained as `.bak`. A failed state write keeps the temporary snapshot
as a recoverable journal. The original `generated.zh_Hans.json`, `pending.zh_Hans.json`, and
`dump/ui_raw.json` paths remain as bounded backward-compatible legacy mirrors and are read only
when no state artifact exists. A valid state, temporary state, or backup is always preferred over
legacy files, so a crash cannot load a generated/new-pending/raw mixed epoch. Missing-manifest,
partial-write, backup recovery, coherent snapshot, transient writer failure, and persistent
failure/quarantine tests are in `CachePersistenceProtocolTests`.

Terminal flush obtains the same writer semaphore as normal persistence, waits for it only within
its budget, and performs up to **five** paced attempts with 40 ms exponential backoff (capped at
500 ms) inside the **five-second** terminal budget. Cleanup cancels the persistence loop, waits at
most the same five-second bounded shutdown interval, and defers CTS disposal until a timed-out
writer task eventually completes. A persistent failure logs a bounded-budget error, leaves the
generation quarantined through the existing lifecycle gate, and does not claim durability on an
unwritable medium.

### Bounded shutdown semantics

Production constructs `MachineTranslatorLifecycle` with a five-second shutdown timeout, injectable
for tests and capped at 60 seconds. `MachineTranslator.StopAsync` cancels first and returns
`false` on a non-cooperative worker/transition timeout; it retains the stopping queue and observes
the eventual task before disposing its CTS. Cache mutation freeze already precedes machine stop,
and the worker's cancellation/generation checks reject late publication. The lifecycle records
`ShutdownTimedOut`, continues to the caller's later flush/unpatch steps, and its shared shutdown
task/result is returned to concurrent cleanup callers. A timeout/failure faults cleanup, leaves
the generation quarantined, and cannot be covered by a later load. Tests use never-completing
translation delegates and assert fast timeout, late-publication rejection, later cleanup steps,
and bounded reload-transition shutdown.

### Docker validation for this batch

All commands used `docker run --rm`; the repository was copied into the container writable layer,
and the game directory was mounted read-only for the plugin build. No game process was launched and
no host dependency was installed.

```text
M-3 checkpoint: dotnet test — 232 passed, 0 failed, 0 skipped.
M-4 checkpoint: dotnet test — 238 passed, 0 failed, 0 skipped.
N-2/final checkpoint: dotnet test — 241 passed, 0 failed, 0 skipped.
N-2/final checkpoint: dotnet build — 0 warnings, 0 errors.
```

The remaining requested review ID is **M-5** only; it is intentionally deferred to the next
integration/mutation-test batch.

## M-5 follow-up — exact production wiring harness and mutation gate (implementation submitted)

This section records the M-5 implementation at `9616715` plus commits `53a2432`, `5ac464a`,
and `4774c78`.
It intentionally does **not** declare M-5 closed; that remains for the final reviewer.
No production runtime source was changed. The two commits add only the exact-source test target,
its runtime-boundary stubs, documentation, and the Docker-only mutation script.

### Harness architecture and source boundary

`MuvluvLLMMod.IntegrationTests/MuvluvLLMMod.IntegrationTests.csproj` is a separate net8.0
xUnit target with `<Compile Include="..\MuvluvLLMMod\*.cs" Link="Production\..." />`. Thus the
assembly compiles the checked-in production implementation itself, not a project reference or a
copied analogue. The only excluded production file is `CompilerServices.cs`, whose net6
compatibility attributes are supplied by the net8 target framework. The linked set includes
`Patch.cs`, `Plugin.cs`, `Config.cs`, `Hotkey.cs`, `Logger.cs`, `RetainedDelegate.cs`,
`NativeDelegateCoordinator`, `PluginLifecycleGate`, all translation/cache/worker/budget sources,
and every other current production `.cs` dependency.

`Stubs/RuntimeStubs.cs` replaces only external runtime edges: BepInEx plugin/config/logging
objects, Harmony attributes/discovery/ownership, Unity object/component lifetime/time/input,
TMP, the two scenario target types, and the IL2CPP `Action` plus application add/remove calls.
The boundaries are executable rather than source checks:

- fake `TMP_Text.text` invokes the exact production `Patch.TranslateTmpSetter` Prefix;
- fake application add/remove stores native delegate objects by reference, so exact production
  `Plugin` registration, cleanup, and `NativeDelegateCoordinator` identity are exercised;
- fake `ConfigFile` raises the actual production `Config` handler, which enters the actual
  `PluginGenerationLease`; and
- fake Harmony discovery reports target ownership and can omit a required target, so the actual
  `Patch.Initialize`/`Plugin.Load` rollback path runs.

No game process or real IL2CPP native runtime is claimed by this harness. The native boundary is
narrowly simulated; the production coordinator and all surrounding ownership code are exact
source. Existing source-surface tests remain supplemental only.

### Behavioral coverage

The 13 integration tests are grouped as follows:

- `ProductionRenderIntegrationTests` (3): nested same/other TMP external setters with identical
  Chinese, F2-off no-op, valid plugin-refresh restoration, unload/retire/reload stale provenance,
  the single final TMP path for a skill description, and missing required Harmony verification
  rolling back before Hotkey/application/persistence/worker publication.
- `ProductionCoordinationIntegrationTests` (6): real Plugin quit registration/removal, forced
  native remove/register interleaving, cleanup during Loading with rejected late stages, stale
  Config event snapshots across generations, duplicate machine initialization without replacing
  the owner, and failed cleanup continuation/quarantine.
- `ProductionBudgetAndShutdownIntegrationTests` (4): oversized render rejection and no pending
  retention, transient terminal flush recovery, persistent flush failure quarantining a cleanup,
  and a non-cooperative machine timing out while later flush/unpatch steps still execute.

### Reproducible mutation gate

`scripts/mutation-gate.sh` copies the repository into a throwaway container directory before
running anything. It first requires the exact integration baseline to report 13 passed and zero
failed tests, then makes each mutant in a separate temporary copy. The final Docker run against
this implementation reported these exact compile-valid outcomes:

| ID | Deliberate production mutation | Focused result |
|---|---|---|
| `M5-B1-broad-tmp-token` | Make the real Prefix return for every setter while a token exists (`|| true`). | Build succeeded; 1 failed, 0 passed — killed. |
| `M5-B2-reopen-generation-gate` | Replace the generation state admission guard with `if (false)`. | Build succeeded with one unreachable-code warning; 1 failed, 0 passed — killed. |
| `M5-M1-reconvert-quit-delegate` | Re-convert the quit delegate inside the native add callback. | Build succeeded; 1 failed, 0 passed — killed. |
| `M5-N1-open-required-patch-verification` | Change the required verification failure branch to `if (false && ...)`. | Build succeeded; 1 failed, 0 passed — killed. |
| `M5-M3-disable-budget-gate` | Make `TranslationBudget.IsTextWithinBudget` always return true. | Build succeeded; 1 failed, 0 passed — killed. |
| `M5-M4-remove-terminal-retry-guard` | Reduce terminal flush attempts from the production constant to one. | Build succeeded; 1 failed, 0 passed — killed. |

A surviving mutant is a gate failure. The script ends only after all six are killed and prints
`M-5 mutation gate: all 6 compile-valid mutants killed`; it never mutates the checkout.

### Docker validation

All validation used `docker run --rm` with `mcr.microsoft.com/dotnet/sdk:8.0`; source was copied
into the container writable layer and the game was never launched. The game path was mounted
`readonly` for the production build:

- legacy `MuvluvLLMMod.Tests`: **241 passed, 0 failed, 0 skipped**;
- exact-source `MuvluvLLMMod.IntegrationTests`: **13 passed, 0 failed, 0 skipped**;
- `dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game`: **0 warnings,
  0 errors**; and
- Docker throwaway mutation gate: baseline **13/13**, all six compile-valid mutants killed.

The game directory `C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl` was read-only on the build
mount and was not written or started. No host `dotnet` or dependency installation was used.

---

## PF-1 follow-up — terminal reload-worker quarantine and bounded generation quiescence

Implementation and exact-source regression coverage are in commit `37c718d` (`fix: quarantine
timed-out reload workers and bound teardown waits`). This batch changes only PF-1 and the
related lifecycle/shutdown audit note; PF-2 through PF-7 remain deferred.

### Fault and ownership model

`MachineTranslatorLifecycle` no longer has an overwriteable stopping slot. Every worker handed
off for stopping is retained in a reference-identity `HashSet<MachineTranslator>`. A worker's
bounded `StopAsync` result is separate from its `StopCompletion` task: a timeout/failure records
a terminal lifecycle exception, retains the worker, and observes `StopCompletion` until the worker
really ends. The eventual observer removes only that worker after completion and observes any late
exception. Shutdown captures the active worker plus the complete stopping set, and all callers
receive the same shared shutdown task/result.

A reload stop timeout/failure faults the lifecycle before replacement creation. The lifecycle then
rejects reload, initialization, enqueue, and cancellation paths; queued transitions also check the
fault before taking ownership or starting a replacement. Request-rate carryover and ordinary
successful reload handoff remain unchanged. The worker's cancellation/generation checks continue
to reject late cache publication.

The lifecycle sends a terminal-fault notification to the owning `PluginLifecycleGate`. The
production recipient only records the generation fault/quarantine; it does not recursively invoke
synchronous cleanup from the transition. Cleanup remains the owner of freeze, machine shutdown,
flush, and unpatch. A faulted generation is rejected by `TryBeginLoad`, and its later cleanup
observes the same machine failure rather than reporting success over worker A. A timed-out worker
remains retained/observed even after terminal cleanup; only its eventual completion releases the
reference.

### Bounded stage/callback teardown

`PluginLifecycleGate` now accepts an injectable quiescence deadline, defaulting to five seconds and
capped at 60 seconds for production configuration. One deadline covers both admitted load stages
and generation config callbacks. Cleanup revokes the generation first, waits each boundary only
within the remaining budget, and on timeout records a `TimeoutException`/generation failure while
continuing independent teardown steps. The shared cleanup completion means concurrent callers see
the same Failed result. Lease disposal is also bounded and becomes non-blocking after quiescence
timeout, so `Config.Shutdown` cannot reintroduce an unbounded callback wait.

Stage and callback completion sources, leases, and stale generation tokens remain retained for
late completion. Their late `Dispose`/exit is safe, but state admission, running publication,
lease entry, and a replacement load remain rejected after quarantine. The generation token caches
its cancellation token so late code can observe cancellation after the CTS is disposed.

### Regression and Docker validation

The exact-source integration target now has 16 tests (including the A-ignores-cancellation reload
1 timeout → reload 2 rejection → terminal cleanup case, retained-worker observation, blocked-stage
and blocked-callback deadline cases). `scripts/mutation-gate.sh` adds the compile-valid
`M5-PF1-single-stopping-owner` mutation; the current gate baseline is 16/16 and all 7 focused
mutants are killed, including the deliberate single-owner overwrite mutation.

Validation for commit `37c718d` used `docker run --rm`, read-only source copies, and the game as a
strict read-only mount; no game launch or host dependency change was performed:

- `dotnet test MuvluvLLMMod.sln -c Release`: **241 + 16 = 257 passed, 0 failed, 0 skipped**;
- `dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game`: **0 warnings,
  0 errors**; and
- `bash scripts/mutation-gate.sh`: **16/16 baseline, all 7 compile-valid mutants killed**.

## PF-2..PF-5 follow-up — implemented; PF-6/PF-7 remain deferred

This batch changes only PF-2 through PF-5. PF-1 lifecycle/quarantine semantics remain as-is. The
post-fix review document was not edited, and this note does not declare the PF-6 mutation/test
audit or PF-7 final documentation wording closed.

### PF-2 authoritative cache journal

Commits `8a62ccf` (`fix: recover newest authoritative cache epoch`), `22deacd` (missing
canonical recovery coverage), and `d32b9e3` (the mutation gate update) add an epoch-bearing,
checksummed state journal. New `cache.state.v1.json` snapshots
carry a monotonic `Epoch`, `TransactionId`, and SHA-256 `Checksum` over the version, epoch,
transaction metadata, and sorted/generated plus ordered pending/raw payload. Pre-journal V1 files
without metadata are accepted deterministically as epoch zero and marked for safe migration.

Startup reads canonical, temporary, and backup state artifacts with the existing bounded file
reader, validates each schema/checksum, and chooses the greatest valid epoch; path order is only
the equal-epoch tie-breaker. A recovered temp or backup is promoted with the prior valid canonical
preserved as backup, while stale/corrupt temporary recovery files are removed deterministically.
No legacy mirror is mixed with any observed state artifact. The canonical state write is the sole
commit decision: generated/pending/raw legacy files are migration mirrors, and a mirror failure
is diagnosed without making a successful authoritative epoch dirty or lost.

### PF-3 response streaming

Commit `0761479` (`fix: stream bounded LLM response bodies`) sends through the existing rate
limiter with `HttpCompletionOption.ResponseHeadersRead`, preserving status, retry, and
cancellation handling. The existing bounded reader therefore sees the response stream before
`HttpClient` buffers it and rejects bodies over `TranslationBudget.MaxResponseBodyBytes`.
Unknown-length 512 KiB producer tests allow only the 8 KiB reader-buffer slack and stop before the
producer completes.

### PF-4 reverse-index accounting

Commit `aae00b2` (`fix: account reverse index source bytes`) admits and updates reverse entries
with the UTF-8 cost of both retained source and translated strings. Unique mappings retain the
full pair; same-pair touches repair/update the same accounted node; ambiguity drops the source
identity and retains only the translated ambiguity marker; removal and LRU eviction subtract the
same recorded cost. Admission is atomic and remains below the configured byte/entry caps. Tests
assert the exact 12,003-byte cost of a 4,000-character Japanese source plus a short translation,
then cover ambiguity and eviction.

### PF-5 final fill budget and provenance

Commit `139ec4f` (`fix: bound final placeholder expansion`) validates the final `TextTemplate.Fill`
result against both UTF-16 and UTF-8 budgets after replacement. A failed fill returns null, so
`TranslationResolver` removes the invalid generated template and leaves the original text while
re-queueing normal production work. The exact render path covers a 4,080-Chinese-template plus
100-character numeric expansion: it never displays the over-budget plugin output, including with
F2 display disabled, and does not loosen provenance recording.

### Regression and mutation results

The checkpoints used `docker run --rm`; the game was mounted strictly read-only for production
builds and was never launched or written:

- PF-2 checkpoint: legacy tests **245 passed**, exact-source integration **17 passed**, plugin
  build **0 warnings / 0 errors**.
- PF-3 checkpoint: legacy tests **246 passed**, integration **18 passed**, plugin build **0/0**.
- PF-4 checkpoint: legacy tests **248 passed**, integration **19 passed**, plugin build **0/0**.
- PF-5 checkpoint: legacy tests **250 passed**, integration **20 passed**, plugin build **0/0**.

The Docker-only `scripts/mutation-gate.sh` now requires the exact-source baseline **20 passed**
and adds one concrete mutant for each PF-2 through PF-5 behavior. The actual throwaway run killed all
**11/11** configured compile-valid mutants (the prior M-5/PF-1 seven plus PF-2, PF-3, PF-4, and
PF-5). This is recorded as coverage evidence only; PF-6 remains the next independent audit.
Remaining requested PF IDs: **PF-6 and PF-7**.
