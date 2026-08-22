# Final adversarial release review

## VERDICT: DO NOT SHIP

The standalone binary builds and its compiled patch/dependency surface is clean, but the release gate fails on two independently reproducible Blockers:

1. the TMP re-entrancy/lifecycle design can preserve stale provenance and later replace an unrelated Chinese assignment with Japanese; and
2. load/config/cleanup ownership is not a terminal, generation-safe state machine and can orphan a worker or create resources after cleanup.

No runtime gate is strong enough to make this HEAD shippable without code changes. In particular, the first defect violates the hard rule that no failure mode may turn another plugin's/upstream Chinese back into Japanese.

## Review baseline and scope

- Initial branch: `main`
- Initial reviewed code HEAD: `2d683187514fa598e41a3635256fce6ebeb00135`
- Initial working tree: clean
- Review bookkeeping commit: `daa7a3613af779cffad42c87590aed43000f879c` (`docs/FINAL-REVIEW.md` only)
- `git diff 2d6831..daa7a3` contained only the in-progress review file. Thus all production/test line references below describe the exact initial code HEAD.
- Scope covered every production source, project file, test source, README, the freshly built DLL, the relevant generated game interop surface, and the high-risk hypotheses H1–H6.
- No production code or test code was changed. Mutation and wiring experiments used throwaway filesystem copies inside `docker run --rm` containers.

## Finding summary

| ID | Severity | Result |
|---|---|---|
| B-1 | **Blocker** | A thread-wide TMP guard and provenance surviving unpatch/reload can hide external assignments, then restore unrelated Chinese to Japanese. |
| B-2 | **Blocker** | Plugin load/config/cleanup ownership can overlap; `Initialize` can orphan an old worker and cleanup can finish before later load stages create resources. |
| M-1 | **Major** | The retained IL2CPP quit delegate has correct sequential identity, but native add/remove is not atomic with retention and can leave an untracked callback. |
| M-2 | **Major** | F2-off cannot remove already-rendered translations produced by the skill-description hook. |
| M-3 | **Major** | Cache, reverse/provenance state, and worker bookkeeping have count-only or no limits, not hard memory budgets. |
| M-4 | **Major** | Terminal flush makes three immediate attempts only; a time-based transient/persistent failure leaves the final dirty state solely in memory. |
| M-5 | **Major** | Critical production-wiring guards can be defeated by simple compile-valid mutations while all 201 tests remain green. |
| M-6 | **Major** | README makes false safety/display guarantees and couples those guarantees to named upstream implementation details. |
| N-1 | **Minor** | Harmony verification is fail-open: missing required hooks are logged but plugin startup continues. |
| N-2 | **Minor** | Synchronous shutdown has no production timeout; one non-cooperative operation can prevent final flush and unpatch indefinitely. |

---

## Detailed findings

### B-1 — Blocker — `ThreadStatic` suppresses unrelated nested setters; stale provenance can restore Chinese as Japanese

**Locations**

- `MuvluvLLMMod/Patch.cs:15-19` — process-lifetime static guard and provenance store.
- `MuvluvLLMMod/Patch.cs:38-54` — `TranslateTmpSetter`; line 40 returns before `BeginExternalSetter` at line 45 for *every* nested setter on the thread.
- `MuvluvLLMMod/Patch.cs:82-89` — reverse-source restore.
- `MuvluvLLMMod/Patch.cs:93`, `148-180` — synchronous diagnostic logging occurs while `translatingTmp` is still true.
- `MuvluvLLMMod/Patch.cs:112-136` — guarded refresh performs the later restore.
- `MuvluvLLMMod/TmpTranslationProvenance.cs:104-115`, `143-159`, `166-219` — invalidation, recording, and restore are correct only if every external setter reaches `BeginExternalSetter`.
- No cleanup/load path clears `Patch.tmpProvenance`; it is `static readonly` and `Patch.Initialize` only patches/verifies.

**Why this is wrong**

`translatingTmp` is described as a plugin-write recursion guard, but it is set for the whole resolver/logging prefix. A synchronous nested setter on any other TMP object returns at line 40. It therefore performs neither translation nor the mandatory assignment-generation invalidation. If that object receives the same Chinese value as a stale plugin translation, the next F2-off refresh still has a matching object/generation/value/reverse mapping and restores the stale Japanese source.

This is not limited to an artificial recursive call to the same setter. BepInEx logging dispatch is synchronous to registered sinks; a sink/UI can update a TMP object while `LogSeenText` runs. More generally, any synchronous callback reached during resolution has the same defect.

There is a second independent blind window: explicit unload unpatches `set_text`, but the static provenance table survives. A game/other-plugin assignment made while unpatched cannot invalidate it. Reloading the same assembly and cache can then validate and restore that stale entry.

**Production-wiring reproduction**

A Docker harness compiled the exact production `Patch.cs`, `Translation.cs`, `TranslationResolver.cs`, `TranslationCache.cs`, `TmpTranslationProvenance.cs`, `TmpRestoreDecision.cs`, `DebugTextLogPolicy.cs`, and `TextTemplate.cs`. Only Unity/Harmony boundaries and the log sink were stubbed; the fake TMP setter invoked the real production prefix.

1. Store `確認する -> 确定` and assign `確認する` to TMP victim `N`; the prefix displays `确定` and records provenance.
2. Enable debug logging.
3. While an unrelated outer setter is inside production `LogSeenText`, the synchronous log sink externally assigns the byte-identical `确定` to `N`.
4. The nested setter hits `Patch.cs:40` and skips invalidation.
5. Set display off and call the real `Patch.RefreshAllTmpText()`.

Observed output:

```text
after_nested_external_assignment=确定
after_F2_off_refresh=確認する
PROHIBITED_RESTORE_REPRODUCED=True
```

That final transition is explicitly forbidden: the later assignment was unrelated/upstream Chinese, yet F2 restored Japanese.

**Wrapper/ID review**

The actual restored dependency is Il2CppInterop Runtime 1.5.3. Docker decompilation showed:

- game `Object.FindObjectsByType<T>` returns `Il2CppArrayBase<T>`;
- `Il2CppReferenceArray<T>.WrapElement` calls `Il2CppObjectPool.Get<T>(nativePointer)`; and
- `Il2CppObjectPool` caches a live wrapper by native pointer and returns the same assignable wrapper.

Because provenance strongly roots its wrapper, ordinary setter/scan wrapper identity is likely stable on this exact runtime. That mitigates the ordinary alias-wrapper case, but it does not mitigate the confirmed nested-setter blind spot, assignments while unpatched, cache-disabled wrappers, or native pointer/Unity ID reuse across lifecycle gaps.

**Why tests did not cover it**

- `MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj:15-35` does not compile `Patch.cs`.
- `TmpTranslationProvenanceTests` manually calls `BeginExternalSetter`; it therefore assumes away the defect.
- `PatchSurfaceTests.cs:57-66` checks only that method-name strings occur.
- A throwaway mutation changing `if (translatingTmp)` to `if (false && translatingTmp)` left all **201/201** tests green and still built the plugin (see M-5).
- There is no re-entrant setter, unpatch/reload, native-wrapper, or actual skill/TMP wiring test.

**Required fix**

- Do not hold a thread-wide “plugin write” flag around resolution, queueing, reverse lookup, or logging.
- Use a narrowly scoped, per-assignment plugin-write token for the exact object/native identity and generation, set only around the plugin-owned `text.text = value` call.
- Every other Prefix invocation must invalidate external provenance before any callback, including nested and byte-identical assignments.
- Add lifecycle epoching and clear/retire all provenance before unpatch and before a new load can publish hooks.
- Add a production-wiring test that invokes the real Prefix through a setter, re-enters on a second object, unloads/reloads, and proves every uncertainty is a no-op.

### B-2 — Blocker — outer plugin lifecycle is not terminal or generation-safe

**Locations**

- `MuvluvLLMMod/Plugin.cs:54-103` — staged `Load` has no commit point or cancellation check after `TryBeginLoad`.
- `MuvluvLLMMod/Plugin.cs:70-95` — config is subscribed before cache load and before final machine initialization.
- `MuvluvLLMMod/Plugin.cs:108-185` — cleanup operates directly on mutable static fields.
- `MuvluvLLMMod/Plugin.cs:179` — `currentMachine` is cleared *after* `PluginLifecycleGate.Cleanup` reopens load.
- `MuvluvLLMMod/Plugin.cs:208-223` — reload checks state and then separately reads the replaceable static lifecycle; there is no event/lifecycle generation lease.
- `MuvluvLLMMod/Config.cs:95-118` — subscription removal does not quiesce an already-running handler.
- `MuvluvLLMMod/PluginLifecycleGate.cs:20-27`, `30-59` — it has only `loadStarted` and `cleanupStarted`; cleanup may begin during loading and resets `loadStarted` before its caller finishes post-cleanup work.
- `MuvluvLLMMod/MachineTranslatorLifecycle.cs:37-58` — `Initialize` overwrites `current` without stopping/rejecting an existing machine or transition-installed machine.

**Concrete races**

1. **Cleanup can miss resources created later by the same `Load`.** Pause `Load` immediately before `Plugin.cs:85`. Cleanup sees no persistence CTS, freezes/stops/flushes/unpatches, and resets the gate. The paused load then executes lines 85–87 and creates an uncancelled persistence task against the already-frozen cache. Earlier pauses can similarly create a Hotkey, patch, or quit callback after the corresponding cleanup step has passed.
2. **A config event during initial load can orphan a worker.** `Config.Initialize` subscribes at line 70. Once static `Cache` is assigned at line 72, a machine-setting event can pass `ReloadMachineTranslator`, complete a reload transition, and install worker A while `Cache.Load`/later stages continue. `Load` then calls `Initialize` at line 91; `MachineTranslatorLifecycle.Initialize` overwrites `current` with worker B without stopping A. Shutdown captures only B.
3. **A stale old-generation event can control a new generation.** An old handler can pass `IsCleaningUp` at `Plugin.cs:210`, be preempted, then resume after cleanup/new load and read the new static `MachineLifecycle` at line 213. Unsubscribing the old event does not revoke the already-running call. For an old API-key event, the new static `LlmApiKey` identity can also make `Config.cs:110` fail and log the old key through the generic `BoxedValue` branch.
4. **Cleanup result is not shared.** A concurrent second `Cleanup` returns the current default `cleanupSucceeded == 0` immediately instead of waiting for the owning cleanup. Cleanup errors still reset `loadStarted`, allowing reload despite an unremoved callback, failed unpatch, or unrecoverable flush.

**Deterministic production-class reproduction**

A Docker harness linked the exact production `MachineTranslatorLifecycle`, `MachineTranslator`, queue, retry, cache, and rate-limiter classes. It started worker A, called `Initialize` a second time, then called terminal shutdown.

```text
factory_calls=2
orphan_token_canceled_after_shutdown=False
ORPHAN_WORKER_REPRODUCED=True
```

The first worker remained alive because the second `Initialize` replaced the only owned `current` reference.

**What was independently verified as correct**

Within a single, correctly owned lifecycle instance, shutdown sets a terminal bit, increments the version, awaits the transition chain, and all four enqueue overloads reject after shutdown (`MachineTranslatorLifecycle.cs:61-175`). The stale-transition check occurs before taking `current` at lines 220–228. Those inner guards are useful but do not protect the outer static lifecycle swap, in-flight config callback, duplicate initialization, or load/cleanup overlap.

**Why tests did not cover it**

- `PluginLifecycleGateTests.cs:39-66` checks only that multiple cleanup callers run steps once; it does not assert that all callers await and receive the same result.
- Its “late setting” test (`:68-99`) calls `Reload` on the same already-shutdown lifecycle, not an old callback crossing into a new static generation.
- No test overlaps `Load` with cleanup or a config callback, and `Plugin.cs`/`Config.cs` are not compiled by the test project.
- The lifecycle test at `MachineTranslatorLifecycleTests.cs:135-212` intentionally calls `Initialize` while transitions exist, but never asserts that an already-current worker is stopped or remains owned.

**Required fix**

Implement one generation-bearing state machine (`NotLoaded -> Loading -> Running -> Stopping -> Stopped/Failed`) that owns all resources:

- stage resources in a per-load object and publish them atomically only if that generation is still current;
- make cleanup cancel/wait for `Loading`, then tear down the exact generation it owns;
- never reopen load until cleanup and all caller-visible post-actions have completed successfully;
- make all cleanup callers await the same completion/result;
- issue generation leases to config handlers and wait for in-flight handlers before disposing/replacing config/lifecycle state;
- reject duplicate `Initialize`, or asynchronously stop and await the prior current machine before replacement;
- quarantine a generation after failed removal/unpatch/flush instead of allowing a new load over leaked state; and
- reset/null static cache/resolver references only as part of the atomic generation handoff.

### M-1 — Major — retained quit-delegate identity is sequentially correct but registration/removal is racy

**Locations**

- `MuvluvLLMMod/Plugin.cs:30-31`, `188-204` — exact converted delegate is retained and reused.
- `MuvluvLLMMod/RetainedDelegate.cs:21-47` — helper lock does not cover the native add/remove operation as one state transition.
- `MuvluvLLMMod.Tests/RetainedDelegateTests.cs` — sequential identity/failure only.
- `MuvluvLLMMod.Tests/PluginSurfaceTests.cs:8-24` — source-substring assertions only.

**Verified good path**

Fresh DLL decompilation showed a single retained conversion feeding `Application.quitting += ...`, and removal uses the retained `Il2CppSystem.Action`. This fixes the original “convert twice” identity bug.

**Race**

Registration does `GetOrCreate` and then native `add_quitting` outside the retention lock. Cleanup can remove/clear between those operations; registration then resumes and adds a callback that is no longer retained and cannot be removed by a later cleanup.

A Docker harness using the exact production `RetainedDelegate<T>` forced that interleaving and simulated the native void remove semantics:

```text
removal_reported_success=True
retained_current_is_null=True
native_callback_count=1
UNTRACKED_CALLBACK_REPRODUCED=True
```

A failed native removal is also followed by gate reopening (B-2); the next load re-adds the retained delegate and can accumulate duplicate subscriptions.

**Why tests missed it**

They test conversion/removal in serial and look for source tokens. The compile-valid mutation that re-converted `handler` immediately before `Application.add_quitting(handler)` retained every asserted token, and all 201 tests passed.

**Required fix**

Serialize native add/remove with lifecycle ownership and an explicit `Unregistered/Registering/Registered/Removing` state. Publish “registered” only after add succeeds; never clear identity until confirmed removal; prevent a new generation from registering while removal is pending/failed. Add a forced-interleaving test around the real registration coordinator, not just the generic holder.

### M-2 — Major — F2-off does not hide already-rendered skill-description translations

**Locations**

- `MuvluvLLMMod/Patch.cs:96-110` — skill Postfix translates the returned string before it reaches TMP.
- `MuvluvLLMMod/Patch.cs:67-73` — TMP provenance is recorded only when the TMP Prefix itself changes the incoming value.
- `MuvluvLLMMod/TranslationResolver.cs:57-61` — a value already known as translated is returned unchanged.
- `MuvluvLLMMod/Patch.cs:112-136` — refresh has no provenance to restore.
- README `:61`, `:92-104` claims F2 controls displayed translation.

**Reproduction**

Using the exact production Patch/Translation/cache/provenance wiring in Docker:

1. cache `説明する -> 技能说明`;
2. call real `TranslateSkillDescription`, producing `技能说明`;
3. assign that result to TMP; the Prefix recognizes it as already translated, makes no change, and records no TMP provenance;
4. turn display off and call real `RefreshAllTmpText`.

Observed:

```text
skill_postfix_result=技能说明
after_F2_off_refresh=技能说明
DISPLAY_OFF_FAILED=True
```

New skill-description calls while display is off do remain Japanese and still enqueue production, but an already-visible label does not toggle off.

**Why tests missed it**

The test project does not compile `Patch.cs`; `PatchSurfaceTests.cs:13-15` only checks target-name literals. Translation facade tests use fake `Patch`, `Config`, and `Core` types and never pass a skill result through TMP.

**Required fix**

Carry an explicit plugin-owned assignment token/source from the skill Postfix to the immediately consuming TMP setter (with strict object/generation expiry), or force a safe skill UI rebuild on display changes. Do not infer ownership from a translated string/reverse index alone. Add an end-to-end skill-result -> TMP setter -> F2-off test.

### M-3 — Major — memory bounds are count-only or absent

**Locations**

- `MuvluvLLMMod/TranslationCache.cs:10-30` — capacities apply only to raw/reverse *entry counts*; `generated`, `pending`, generations, and promoted-pending are unbounded.
- `TranslationCache.cs:211-255`, `403-465` — complete files are read/deserialized before cleanup/capping.
- `TranslationCache.cs:323-350` — every unique kana template remains pending until resolved/cancelled; no entry/byte budget.
- `TranslationCache.cs:366-400` — reverse LRU has 4,096 entries but retains arbitrary-length translated keys and source strings.
- `MuvluvLLMMod/TmpTranslationProvenance.cs:75-89`, `143-159` — 2,048 slots/entries, each retaining arbitrary-length source/translation strings and a wrapper.
- `TranslationRetryPolicy.states`, `TranslationWorkQueue.completed/cancel maps`, `TranslationPriorityBacklog.canceledPendingThrough`, and `TranslationProgress.failedTemplates` are also unbounded for a worker/process generation.

**Assessment**

The debug deduper itself is genuinely bounded in retained text: `DebugTextLogPolicy.cs:22-25, 94-96, 140-145, 169-179` keeps a fixed `ulong` key and at most 80 UTF-16 code units per LRU display entry. The concurrency/token tests for that helper are meaningful.

That does not create a hard bound for the rest of the plugin. One very long string can consume an arbitrary amount in reverse/provenance state, and an arbitrary number of unique kana templates grows pending/generated/worker dictionaries. JSON snapshotting further duplicates the full cache into dictionaries, arrays, and serialized strings.

**Reproducible examples**

- The existing `TranslationCacheTests.cs:135-149` feeds 5,000 unique values and checks only `raw <= 4096`; it simultaneously leaves roughly 5,000 pending templates, demonstrating the uncapped path.
- `RememberResolution(new string('源', N), new string('译', N))` retains O(N) characters despite a 4,096-entry limit.
- A large on-disk JSON file is fully materialized by `File.ReadAllText` and `JsonSerializer.Deserialize` before any 4,096-entry cleanup.

**Why tests missed it**

Cache tests assert cardinality eviction, not retained bytes, max input/file size, total process budget, or unbounded pending/generated/worker maps. Provenance tests likewise assert entry count only. Debug tests correctly distinguish count from text size, but that standard was not applied elsewhere.

**Required fix**

Define and enforce limits for input UTF-16/UTF-8 bytes, per-entry size, total reverse/provenance bytes, pending/generated in-memory working sets, response body size, and on-disk file read size. Stream/validate bounded files, avoid whole-cache serialization copies, evict or page durable generated data, and bound/expire worker cancellation/retry/progress maps. Expose counters and fail closed (leave incoming text unchanged) when a budget is exhausted.

### M-4 — Major — final dirty cache mutation is still lost after three immediate write failures

**Locations**

- `MuvluvLLMMod/TranslationCache.cs:258-275` — normal retry is paced.
- `TranslationCache.cs:277-284` — terminal attempts have no delay/backoff.
- `TranslationCache.cs:286-317` — failure re-signals dirty state, but the persistence consumer has already been cancelled during cleanup.
- `MuvluvLLMMod/Plugin.cs:135-161` — persistence is cancelled, terminal flush fails cleanup after three tries, and application quit can then end the process.
- `TranslationCache.cs:303-305` — three files are committed separately, with no epoch manifest/journal for a crash between files.

**Reproduction**

A Docker harness injected a writer that remained unavailable and inspected the real cache state:

```text
terminal_result=False
attempts=3
elapsed_ms=35.452
dirty_only_in_memory=True
generated_file_exists=False
```

The elapsed time was serialization/call overhead; there is no retry delay. A file lock lasting longer than those back-to-back calls defeats all three “retries”. The code does log an error and returns cleanup failure, so this is not silent on explicit unload; nevertheless, on normal application quit the final mutation remains only in memory and is lost.

**Why tests missed it**

`TranslationCacheTests.cs:190-205` makes a synthetic writer succeed on its second immediate invocation. It does not model a time-based lock, persistent failure, retained recovery journal, mixed three-file epoch, process interruption, or whether failed cleanup prevents reload.

**Required fix**

Use bounded, paced terminal retries within an explicit shutdown budget; await persistence-loop termination; preserve a recoverable journal/temp generation plus manifest; recover incomplete epochs on next load; and do not reopen plugin load after terminal persistence failure. If true fsync durability is claimed, flush file/directory metadata appropriately and test crash recovery rather than only callback counts.

### M-5 — Major — the suite does not protect critical production wiring

**Locations**

- `MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj:15-35` links helpers but excludes `Plugin.cs`, `Patch.cs`, `Config.cs`, `Hotkey.cs`, and `Logger.cs`.
- `PluginSurfaceTests.cs:8-74` and `PatchSurfaceTests.cs:8-66` mostly assert source substrings/order.
- `TranslationFacadeTestHost.cs` substitutes fake `Config`, `Patch`, and `Core`, so facade tests cannot exercise real loader/Patch behavior.

**Mutation experiment (throwaway Docker copy only)**

The following three compile-valid production-wiring mutations were applied together:

```text
Patch.cs:    if (translatingTmp)
             -> if (false && translatingTmp)

Plugin.cs:   if (IsCleaningUp || Cache == null) return;
             -> preserve the asserted text in an empty if, then use if (false) return;

Plugin.cs:   handler = (Il2CppSystem.Action)ApplicationQuittingHandler;
             inserted immediately before Application.add_quitting(handler), discarding retained identity.
```

Results:

```text
Passed: 201, Failed: 0, Skipped: 0
mutated plugin build: succeeded, 1 unreachable-code warning, 0 errors
```

Thus the suite stayed green while disabling the production re-entry guard, disabling the plugin cleanup/reload gate, and reintroducing the IL2CPP conversion-identity defect. These mutations do not cancel one another; the test project simply does not execute those paths, and substring assertions remain satisfied.

The inner `MachineTranslatorLifecycle` terminal enqueue guards do have mutation-sensitive behavioral tests; the gap is specifically the loader/Patch/native wiring that carries the release invariants.

**Required fix**

Create a test assembly that compiles the real production `Patch`, `Plugin` coordination, `Config` handler, and delegate coordinator against narrow BepInEx/Unity/IL2CPP stubs. Drive actual Prefix/Postfix methods through fake setters/events, including re-entry, load/cleanup overlap, stale config generations, duplicate initialize, delegate add/remove interleavings, skill handoff, and flush failure. Add artifact metadata/IL assertions for exact patch/dependency targets. Keep a small mandatory mutation set in CI.

### M-6 — Major — README promises behavior the code does not provide and depends on upstream implementation assumptions

**Locations**

- `README.md:15-28` names a separate plugin, enumerates its data-layer methods, and claims the plugins “never fight”.
- `README.md:61`, `92-104` claims F2 controls displayed translation and that every external setter invalidates provenance.
- `README.md:106`, `140-142` names upstream hotkeys/project behavior and makes an interoperability guarantee.

**Problems**

- B-1 disproves “Text assigned by another mod is therefore left alone” and “Every unguarded setter starts a new assignment.” Nested setters and setters while unpatched are not observed.
- M-2 disproves complete F2 display control for skill descriptions.
- The compatibility claim is coupled to a named third party's current patch implementation and hotkeys. That is a load-order/upstream-data assumption in release documentation even though the binary itself does not inspect that plugin.
- The hard standalone boundary should be stated as a generic render-layer contract, not justified by tracking another project's internal targets.

**Why tests missed it**

No test validates README claims. The source forbidden-term test scans only `MuvluvLLMMod/**/*.cs`, not release documentation.

**Required fix**

After fixing behavior, replace absolute “never” claims with tested conditions and residual limitations. Remove named upstream internals/hotkey assumptions if the no-recognition rule applies to the whole release, and document only the generic fact that kana-free incoming text is not queued. Do not claim F2 restoration for a hook until end-to-end behavior is tested.

### N-1 — Minor — required Harmony patch verification fails open

**Location**: `MuvluvLLMMod/Patch.cs:183-215`; startup call at `Plugin.cs:80-82`.

If any TMP/Scenario/skill target is missing or not owned by this Harmony ID, `VerifyPatches` logs an error and returns. `Load` then creates Hotkey/persistence/workers and logs success. A game update/signature drift can therefore run a partially functional plugin while presenting a successful load.

**Test gap**: the suite checks target source strings, not the runtime verification result or startup decision.

**Fix**: return a structured verification result and fail/rollback startup when a required hook is absent. If scenario tracking is optional, explicitly degrade only priority; TMP setter and any advertised skill hook should be hard gates.

### N-2 — Minor — cleanup can wait forever on a non-cooperative operation

**Locations**

- `MachineTranslatorLifecycle.cs:155-158`, `179-195` synchronously waits transitions/workers.
- `MachineTranslator.cs:109-170` supports a timeout, but lifecycle shutdown calls `StopAsync()` without one.
- `Plugin.cs:134` invokes this synchronous shutdown on the cleanup/application-quit path before final flush and unpatch.

The production HTTP path usually propagates cancellation correctly, and no internal lock-order deadlock was found. However, a stuck transport, synchronous logger sink, or unexpected non-cooperative task can block Unity's quit thread indefinitely and prevent flush/unpatch. Tests release their deliberately blocked delegates; they do not assert a bounded production shutdown.

**Fix**: define a shutdown deadline. Cache is already frozen before worker stop, so after the deadline late publication can safely be rejected; retain/observe the task for eventual disposal, report the timeout, and still execute terminal flush/unpatch. Test a delegate that never completes after cancellation.

---

## High-risk hypothesis disposition

| Hypothesis | Disposition |
|---|---|
| **H1 TMP Prefix/guard/provenance** | **Failed.** Same-instance/same-value helper logic is sound only when the Prefix observes the setter. Real production wiring has a confirmed nested-setter blind spot and a lifecycle/unpatch blind spot. Ordinary wrapper identity is mitigated by Il2CppObjectPool on runtime 1.5.3, not proven across lifecycle reuse. |
| **H2 shutdown/reload/config races** | **Failed.** Inner lifecycle terminal/version guards are good, but outer load/config ownership is not generation-safe; duplicate `Initialize` orphaned a worker in a production-class harness. Config unsubscribe is not handler quiescence. |
| **H3 IL2CPP quit identity** | **Partial.** Fresh artifact uses the same converted delegate sequentially. Atomic registration/removal is missing and an untracked callback was reproduced. |
| **H4 memory/final flush** | **Failed overall.** Debug LRU has a real bounded retained key/text shape. Cache/provenance/queues lack byte budgets, whole files are materialized, and terminal write failure strands dirty state in memory. |
| **H5 mutation resistance** | **Failed.** Three release-critical wiring mutations passed 201/201 tests and produced a DLL. |
| **H6 standalone DLL/static surface** | **Passed for the compiled DLL; failed for README guarantees.** Artifact has no upstream literal/reference/dependency attribute or forbidden target. Documentation names and assumes upstream internals. |

## Positive evidence and residual runtime gates

The following claims were independently supported and should be preserved during fixes:

- F2 does **not** reload/stop the machine translator; display-off TMP/skill paths call `ObserveForTranslation`, and LLM enablement is captured only from `Config.LlmEnable`.
- All machine lifecycle enqueue overloads reject after terminal shutdown; queued transitions are chained and the stale generation check precedes ownership of `current`.
- Cleanup ordering is freeze -> machine stop -> persistence cancellation -> terminal flush -> unpatch, after earlier event/component shutdown steps.
- Debug logging defaults off, uses a fixed-size hash key, retains at most 80 characters per LRU entry, and is token-bucket limited.
- The freshly built DLL's Harmony target set is exactly TMP `set_text`, Scenario `Refresh`/`Leave`, and skill `GetDescription`.
- The compiled DLL has no `[BepInDependency]`, no `MuvluvMod` literal/reference, and no `GenerateFrames`, `ApplyText`, `ScenarioChoiceElementComponent`, or `LoadMasterData` literal/target.

Because Blockers exist, these are **post-fix release gates**, not permission to ship this HEAD:

1. Default restoration fail-closed until the real setter/re-entry/unload integration suite passes; uncertainty must leave current Chinese untouched.
2. Disable hot reload/config-applied worker replacement until lifecycle generations and handler quiescence are proven; require process restart as a temporary operational gate.
3. Treat missing required Harmony hooks, cleanup timeout, unpatch failure, delegate-removal failure, or terminal flush failure as a quarantined plugin generation that cannot reload.
4. Enforce/telemetry-test memory and response/cache budgets under a multi-hour synthetic unique-text workload.
5. Run an in-game canary matrix after code fixes: pooled TMP reuse, byte-identical external assignment, synchronous nested setter, explicit unload/reload assignment window, skill label F2 toggle, quit during HTTP/config reload, and missing-hook simulation.

## Validation record (report footer)

### HEAD and date

- **Reviewed production HEAD:** `2d683187514fa598e41a3635256fce6ebeb00135`
- **Report parent HEAD before final report commit:** `daa7a3613af779cffad42c87590aed43000f879c`
- **Review date:** 2026-08-22 (UTC+08:00; validation completed 2026-08-22 UTC)
- The final report commit is the commit containing this file; self-referencing its own hash is intentionally impossible. Its parent and reviewed production HEAD are recorded above.

### Required Docker commands and results

Image used: `mcr.microsoft.com/dotnet/sdk:8.0`, local digest `sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc`; Docker Server 29.6.2.

The repository was mounted read-only and copied to the container writable layer so build/test artifacts could not alter the real working tree.

```bash
MSYS_NO_PATHCONV=1 docker run --rm \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/MuvluvLLMMod,dst=/src,readonly \
  -w / mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'cp -a /src /work && cd /work && dotnet test MuvluvLLMMod.Tests/MuvluvLLMMod.Tests.csproj -c Release'
```

Result: **PASS — 201 passed, 0 failed, 0 skipped**, duration 851 ms.

```bash
MSYS_NO_PATHCONV=1 docker run --rm \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/MuvluvLLMMod,dst=/src,readonly \
  --mount type=bind,src=C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl,dst=/game,readonly \
  -w / mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'cp -a /src /work && cd /work && dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game'
```

Result: **PASS — build succeeded, 0 warnings, 0 errors**, elapsed 6.66 s.

One preliminary Docker CLI invocation omitted `MSYS_NO_PATHCONV=1` and was rejected before container start with `working directory 'C:/Program Files/Git/' is invalid`; it did not execute tests/build or touch either mount. The commands above are the successful required validations.

### Fresh artifact inspection

- Artifact: `/work/artifacts/bin/MuvluvLLMMod/Release/net6.0/MuvluvLLMMod.dll`
- SHA-256: `9dbaf23069ac694efac3d4a91faa505526d165f8a37bd39fa25f1c64818c39d0`
- Assembly references: standard System assemblies plus BepInEx, Harmony, Unity/Input/TMP, GameUi, Api, and Il2CppInterop; no upstream mod assembly.
- Raw UTF-8/UTF-16 and ILSpy scan: absent `MuvluvMod`, `BepInDependency`, `GenerateFrames`, `ApplyText`, `ScenarioChoiceElementComponent`, and `LoadMasterData`.
- Decompiled plugin attribute: only `[BepInPlugin("muvluv.llmmod", "MuvluvLLMMod", "1.0.0")]`; no dependency attribute.
- Decompiled Harmony attributes: `ScenarioController.Refresh`, `ScenarioController.Leave`, `TMP_Text.set_text`, and `SkillDescriptionBuilder.GetDescription(SkillMaster,int,bool)` only.

### Additional Docker adversarial results

- Real Patch wiring nested-setter harness: `PROHIBITED_RESTORE_REPRODUCED=True`.
- Real Patch wiring skill/F2 harness: `DISPLAY_OFF_FAILED=True`.
- Real lifecycle duplicate-initialize harness: `ORPHAN_WORKER_REPRODUCED=True`.
- Real retained-delegate interleaving harness: `UNTRACKED_CALLBACK_REPRODUCED=True`.
- Real terminal-flush failure harness: 3 attempts, result false, `dirty_only_in_memory=True`, no generated file.
- Throwaway combined guard mutation: tests **201/201 passed**; mutated plugin still built (1 warning, 0 errors).
- Actual game interop/Il2CppInterop assemblies were only read/decompiled inside Docker; no native game process was launched.

### Game-directory write statement

`C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl` was mounted only with `readonly` on every successful Docker command that used it. All build products, restored packages, decompilation output, mutation copies, and harness files lived in ephemeral container layers and were destroyed by `docker run --rm`. **The game directory was not written to.**
