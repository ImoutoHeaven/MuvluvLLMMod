# Final Release-Gate Review 5

## VERDICT: SHIP WITH RUNTIME GATES

No source-level release blocker was reproduced at reviewed production HEAD
`42fb52348480401b20f31734370168faa9daa2a9`. The three FR4 changes are real rather than commit-message
claims:

- the cache now reserves, retries, and commits the terminal successor epoch in the correct order;
- a timed-out machine keeps the generation failed while its reservation can settle after actual worker
  completion; and
- the exact refresh-time external-assignment race is behaviorally pinned, including the load-bearing
  `tmpProvenance.IsCurrent(assignment)` check.

The required solution test, production build, and mutation gate passed: **251 unit + 47 exact-source
integration = 298 tests**, build **0 warnings / 0 errors**, and **27/27 configured compile-valid mutants
killed**. Four independent reviewer mutants were also killed. A fresh DLL is standalone and exposes only
the approved three Prefix patches.

This is not an unconditional real-game sign-off. The game was deliberately never launched, so real Harmony
IL2CPP detours/order, native event removal, wrapper reuse, input, UI coverage, fonts/layout, and synchronous
native-boundary liveness remain mandatory canary gates. Subject to those gates, the source and artifact are
deliverable.

## Baseline, scope, and protocol

- **Reviewed production/source HEAD:** `42fb52348480401b20f31734370168faa9daa2a9`.
- Expected short HEAD `42fb523` was confirmed before review bookkeeping.
- **Initial worktree:** clean; both short status and porcelain-with-all-untracked output were empty.
- **Review started:** `2026-08-22T19:52:52Z`.
- **Review completed:** `2026-08-22T20:25:05Z`.
- **Review-in-progress commit:** `5c1bbd976a1a90e0166e54945d3d84caf28c7d6e` (only this report).
- **Complete-report commit parent:** `5c1bbd976a1a90e0166e54945d3d84caf28c7d6e`.
- **Complete-report commit:** the commit containing this completed file; its exact SHA is supplied in the
  reviewer response because a Git commit cannot embed its own SHA in its own content.
- Read in full: `FINAL-REVIEW.md`, `POST-FIX-REVIEW.md`, `FINAL-REVIEW-2.md`,
  `FINAL-REVIEW-3.md`, `FINAL-REVIEW-4.md`, and `FIXER-CONTEXT.md`.
- Inspected the complete 61-commit inventory and changed-file range `2d68318..42fb523`, every current
  production file/project, the exact-source harness, focused tests, mutation script, README, fresh artifact,
  and the installed game API surface relevant to the plugin.
- Apart from this report, no source, test, script, README, or other documentation file was modified.
  Reviewer probes and mutations existed only in disposable container layers.

## Historical finding disposition

`CLOSED` means the source property was independently checked at its applicable boundary. `RUNTIME GATE`
means no source failure was found, but the external game/native behavior cannot be proven by the stubbed
harness or static metadata.

| ID | Final status | Decisive evidence at `42fb523` |
|---|---|---|
| **B-1** | **CLOSED; RUNTIME GATE** | Every non-token setter invalidates first; plugin writes have one-shot object/ID/generation/epoch ownership. Nested initial, refresh-time, and unload/reload cases pass. Real detour order/pointer reuse remains a canary. |
| **B-2** | **CLOSED as source ownership** | Generation admission, shared cleanup, late resource commit/rollback, owner/config detachment, failure quarantine, and eventual machine-resource settlement pass. |
| **M-1** | **CLOSED; RUNTIME GATE** | One retained `Il2CppSystem.Action` is serialized through add/remove. Real native `remove_quitting` is void and needs cycle confirmation. |
| **M-2** | **CLOSED** | No skill/data hook exists; skill text reaches only final `TMP_Text.set_text`. |
| **M-3** | **CLOSED** | Text, durable aggregate, reverse/provenance, queue/retry/progress, response, and file budgets are enforced and pressure-tested. |
| **M-4** | **CLOSED for claimed protocol** | Paced retries, checksummed authoritative state, newest valid temp/backup recovery, truthful dirty state, and terminal successor retry all pass. No fsync/power-loss guarantee is claimed. |
| **M-5** | **CLOSED at required assurance gate** | Exact wildcard-linked production source: 47 baseline tests, 27 configured mutants, plus four independent reviewer mutants. |
| **M-6** | **CLOSED** | README is upstream-neutral and qualifies admission, output, provenance, and UI coverage. |
| **N-1** | **CLOSED** | All three required targets require discovery and Harmony ownership; failure throws and rolls back before runtime resources. |
| **N-2** | **RUNTIME GATE** | Stage/callback/worker/persistence waits are bounded and quarantine is terminal. Arbitrary synchronous Unity/native/filesystem calls cannot be forcibly preempted and must be canaried. |
| **PF-1** | **CLOSED** | A timed-out worker remains tracked, faults the generation, rejects another reload/load, and is observed to actual completion. |
| **PF-2** | **CLOSED** | Epoch/checksum candidate selection, promotion, backup, invalid-epoch rejection, and Max-1/Max successor semantics pass. |
| **PF-3** | **CLOSED** | `ResponseHeadersRead` plus bounded streaming stops unknown-length content at 128 KiB plus one 8 KiB read. |
| **PF-4** | **CLOSED** | Reverse accounting includes source and translation; the exact retained test reports 12,003 bytes. |
| **PF-5** | **CLOSED** | Final placeholder expansion is budget-checked and invalid output never enters display/provenance. |
| **PF-6** | **CLOSED at current matrix** | Gate validates compilation, selected test execution, and semantic failure for all 27 configured mutants. |
| **PF-7** | **CLOSED** | Documentation matches bounded eligibility and provenance behavior. |
| **FR2-1** | **CLOSED** | Full-cap CJK and hostile JSON aggregate states reject before dirty/unserializable state; accepted state flushes and restarts. |
| **FR2-2** | **CLOSED; RUNTIME GATE** | Late Harmony/component/native/persistence/machine effects roll back and cannot reopen. Real native lock behavior remains external. |
| **FR3-1** | **CLOSED** | Loaded Max is read-only and never wraps; FR4 additionally makes its Max-1 successor retryable until commit. |
| **FR3-2** | **CLOSED** | Successful, failed-persistence, late-stage, and timed-out-machine paths detach owner/config fields; settled reservations drain. |
| **FR4-1** | **CLOSED** | Same reserved Max successor is retried after real and injected transient failure and committed only after authoritative success. |
| **FR4-2** | **CLOSED** | Original failure remains quarantined while eventual `StopCompletion` permits only the machine reservation to be removed. |
| **FR4-3** | **CLOSED** | Exact refresh-time `外部中文` assignment survives; replacing `IsCurrent` with `true` is killed. |

## FR4 adversarial validation

### FR4-1 — reserved terminal successor is now coherent

**Source trace:** `TranslationCache.cs:512-600` serializes under the writer gate and treats only the
state file as the commit decision. `:568-595` calls `CommitSnapshotEpochUnsafe` only after the
authoritative writer reports success. `:707-744` reuses an existing reservation, blocks additional durable
mutation while Max is reserved, and changes the committed epoch only on success.

Independent checks covered both required writers:

1. The committed exact-source real-writer test loaded a valid checksummed `long.MaxValue - 1` canonical,
   accepted `新する -> 新译`, obstructed the real `.tmp` path so the first write failed before a journal was
   created, removed the obstruction, retried the same successor, restarted, and recovered `新译`.
2. The injected writer failed its first authoritative call and succeeded on the terminal retry. It made
   exactly two state attempts and five total calls (successful state plus three mirrors on attempt two).
3. A reviewer reflection probe verified the state transition exactly:

```text
first Flush(): false
committed snapshotEpoch: long.MaxValue - 1
dirty: true
reservedSnapshotEpoch: long.MaxValue
snapshotEpochExhausted: false
authoritative attempts: 1

FlushTerminal(): true
committed snapshotEpoch: long.MaxValue
dirty: false
reservedSnapshotEpoch: null
snapshotEpochExhausted: true
authoritative attempts: 2
clean follow-up Flush(): true, zero additional writes
restart: new entry present, checksum valid, durable mutation blocked
```

A no-failure Max-1 commit becomes read-only at Max, rejects later generated/observation mutations without
changing retained state, and restarts read-only. Existing candidate tests also retained newest-epoch
selection, checksum rejection, backup promotion, negative-epoch rejection, full-cap serializer accounting,
and bounded file/snapshot behavior.

No success/dirty lie or accepted-and-lost retry was reproduced. The protocol is atomic/checksummed at the
application level; it does not claim filesystem `fsync` durability across power loss.

### FR4-2 — machine failure and eventual settlement are separated

**Source trace:** production registers `MachineTranslatorLifecycle.ShutdownForResourceRollback` at
`Plugin.cs:284-308`. `MachineTranslatorLifecycle.cs:252-295` preserves the original cached shutdown result
but permits a later resource rollback only when that task is completed/faulted and the tracked-worker set is
empty. Ordinary lifecycle callers still observe the original failure. `PluginLifecycleGate` keeps the failed
generation quarantined and retries only retained resources after the one teardown graph has completed.

The committed exact-resource test passed timeout -> failed generation -> one outstanding reservation ->
eventual worker `StopCompletion` -> zero tracked workers -> repeated cleanup -> zero resources, while state
remained `Failed` and `TryBeginLoad` remained false.

A stronger reviewer-only exact-production probe used a real loaded `Plugin` and its registered machine
resource, forced a worker to ignore cancellation, and checked the static terminal state:

```text
first Plugin.Unload(): false
state: Failed
owner/cache/resolver: detached
all Config static roots: detached
hooks/components/native callbacks: zero
tracked workers / outstanding resources: 1 / 1
Plugin.Load() while failed: no new component/resource
worker eventually completes: tracked workers 0
repeated Plugin.Unload(): false
outstanding resources: 0
state remains Failed; another Plugin.Load() still publishes nothing
```

A reviewer test also retried cleanup *before* releasing the worker and proved the reservation stayed at one.
The complementary early-settlement mutant failed that assertion.

A focused **15-test** late-boundary run passed the real blocked `PatchAll`, `AddComponent`, native add,
persistence, machine, stage, and callback timeout cases. Late effects were removed, owner/config fields stayed
detached, and no replacement generation opened. Source lock tracing found no lifecycle-monitor/rollback lock
cycle: resource callbacks execute outside the lifecycle monitor. As with any synchronous native API, a call
that never returns cannot be forcibly interrupted; quarantine and the game canary are the safety boundary.

### FR4-3 — exact refresh-time nested setter is pinned

**Source trace:** `Patch.cs:46-60` invalidates every non-token setter before resolution.
`Patch.cs:102-132` resolves refresh text without a broad guard, then requires runtime epoch and
`tmpProvenance.IsCurrent(assignment)` immediately before the one-shot owned setter.

The exact linked test translated a TMP label, turned display off, and synchronously assigned `外部中文` to the
same label from the logging callback during `RefreshAllTmpText`. Baseline left `外部中文` unchanged. The
configured mutation

```text
tmpProvenance.IsCurrent(assignment) -> true
```

compiled and failed with actual Japanese `更新確認する`. Unload/reload retirement, same/other-TMP nested
setters, normal valid restoration, over-budget output, and the single final skill/TMP route also passed.

## New findings and residual gates

**No new source blocker, Major, or correctness Minor requiring a code change was found.** The remaining
items are external/runtime qualifications, not waivers for a known failing source assertion:

| Gate | Source / reproduction evidence | Required action |
|---|---|---|
| **G-1 real patch/TMP behavior** | Static metadata and fake Harmony prove targets/ownership policy, not an IL2CPP detour, prefix ordering, cross-plugin re-entry, or every UI route. | Canary exact three-hook startup, nested/pooled/unload F2 cases, and missing-target rollback. |
| **G-2 native identity/wrappers** | Source retains exact quit delegate; installed APIs accept `Il2CppSystem.Action`; installed object pool is pointer-keyed. Native remove is void and pointer recycling is external. | Repeated load/unload/quit callback-count test and pooled/destroyed-wrapper F2 test. |
| **G-3 liveness/power loss** | Managed waits are bounded, but a synchronous native/filesystem rollback cannot be preempted. Atomic rename/checksum is not an fsync claim. | Force file locks/crash points and quit during HTTP/config reload; failure must quarantine and restart must recover the newest valid epoch. |
| **G-4 presentation/input** | No real game was launched. | Verify F2 delivery, Chinese glyphs, wrapping/clipping, inactive UI coverage, and 0.5-second scan cost. |

## Exact-source and mutation evidence

### Required gate

The integration project disables default compile items and wildcard-links
`../MuvluvLLMMod/*.cs`, removing only `CompilerServices.cs` because net8 already supplies those compatibility
attributes. Every current production `.cs` file is top-level and therefore linked. Stubs replace only
BepInEx/Harmony/Unity/TMP/scenario/native boundaries.

Required mutation result:

```text
exact-source baseline: 47 passed, 0 failed, 0 skipped
configured mutants:    27/27 compile-valid and killed
```

The matrix includes B-1/B-2, delegate identity, required patch verification, memory/durable admission,
terminal retry, worker ownership, journal selection/checksum/backup/epoch, HTTP streaming, reverse bytes,
final fill, bounded waits, exact patch surface, late stage/rollback, FR3 Max/static detachment, and all three
FR4 checks. Each focused mutant emitted `IntegrationTests.dll`, exited nonzero in its named behavioral test,
and was inspected for the intended assertion rather than credited for a compile/setup failure.

### Four independent reviewer mutants

| Area | Independent compile-valid mutation | Result |
|---|---|---|
| Cache | Commit the reserved epoch even when `authoritativeSucceeded == false`. | **Killed:** Max successor could not be retried. |
| Lifecycle | Settle a faulted machine resource without checking that tracked workers reached zero. | **Killed:** outstanding reservation disappeared while the worker was still live. |
| Patch | Use `BeginExternalSetter` instead of `BeginPluginRefresh` during refresh. | **Killed:** valid skill/TMP F2 restoration failed. |
| HTTP | Remove the unknown-length accumulated-byte ceiling while retaining streaming mode. | **Killed:** producer exceeded the 128 KiB + 8 KiB allowance. |

The added lifecycle baseline passed before mutation. All files and mutations were created only under the
container writable layer.

## Required Docker validation

Environment:

```text
Docker Server: 29.6.2 (overlayfs, linux/x86_64)
Image: mcr.microsoft.com/dotnet/sdk:8.0
Digest: sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc
```

Source was mounted read-only and copied to `/work`; test/build copies were detached at exact reviewed HEAD.

### Solution test

```text
dotnet test MuvluvLLMMod.sln -c Release
MuvluvLLMMod.Tests:             251 passed, 0 failed, 0 skipped
MuvluvLLMMod.IntegrationTests:   47 passed, 0 failed, 0 skipped
TOTAL:                          298 passed, 0 failed, 0 skipped
```

### Production build

The game was mounted at `/game,readonly`:

```text
dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game
Build succeeded: 0 warnings, 0 errors
Required build elapsed: 7.63 s
```

### Mutation gate

The command was run from the read-only source mount exactly as requested:

```text
bash scripts/mutation-gate.sh
baseline: 47/47
configured mutants: 27/27 compile-valid and killed
```

Several preliminary disposable inspection/mutation helper invocations were corrected (container stdin,
an unavailable in-image Python helper, metadata restore, and an ILSpy generic type name). None was a
required gate, modified the checkout, or wrote the game. The corrected probes/scans above completed
successfully.

## Fresh artifact, standalone surface, and installed API

Fresh reviewed DLL:

```text
artifacts/bin/MuvluvLLMMod/Release/net6.0/MuvluvLLMMod.dll
size:    204,288 bytes
SHA-256: 0746b4cb410ba7dd6c6fc48cd916d3c3d926dc415055b19f3421165f0c6b56ce
```

Metadata references are limited to framework assemblies plus:

```text
0Harmony, BepInEx.Core, BepInEx.Unity.IL2CPP, GameUi,
Il2CppInterop.Runtime, Il2Cppmscorlib, Unity.InputSystem,
Unity.TextMeshPro, UnityEngine.CoreModule
```

Decompilation found one `[BepInPlugin("muvluv.llmmod", "MuvluvLLMMod", "1.0.0")]`, exactly three
`HarmonyPrefix`/`HarmonyPatch` pairs, no Postfix, and these targets only:

```text
TMPro.TMP_Text.set_text       Prefix
ScenarioController.Refresh()  Prefix
ScenarioController.Leave()    Prefix
```

Raw/decompiled scans found all prohibited or non-standalone terms absent:

```text
MuvluvMod, BepInDependency, GenerateFrames, ApplyText,
ScenarioChoiceElementComponent, LoadMasterData,
SkillDescriptionBuilder, GetDescription
```

Read-only ILSpy inspection of the installed game API confirmed:

- `ScenarioController` has zero-argument `void Refresh()` and `UniTask Leave()`;
- `TMP_Text.text` is a virtual string property invoking the expected `set_text(String)` method;
- `Application.add_quitting/remove_quitting` both accept `Il2CppSystem.Action`;
- `BasePlugin` exposes virtual `bool Unload()` and `AddComponent<T>()`; and
- Il2CppInterop 1.5.3 uses a `ConcurrentDictionary<IntPtr, WeakReference<Il2CppObjectBase>>`, with
  `Il2CppReferenceArray<T>.WrapElement` calling `Il2CppObjectPool.Get<T>(IntPtr)`.

Installed hashes matched the prior reviewed target:

```text
GameUi.dll:                  0770584f1831ffab4e85bb62774ea3e88076daccb283b3add358ffd437ccba7f
Unity.TextMeshPro.dll:       a9c01299a3740c77af77d082c71adeb303a2074bca8b4de3d65fd659b5228347
UnityEngine.CoreModule.dll:  54fbff45a7b1e203fb43d33a55230fc2b374d193e0579b9a01c05d6a4f81850b
Il2CppInterop.Runtime.dll:   65ab051a681c2c1e52df7596ff738be401aae72cff882ff18241cf54a3ea38a4
BepInEx.Unity.IL2CPP.dll:    62c8246a3076bfe459ec80c482381228d0eb1dde3434fe97ae5255fef406fe72
```

## Mandatory game-only canary before broad release

1. Require startup verification of exactly the three approved owned Prefixes; a missing target must fully
   roll back and publish no component/worker/callback.
2. Exercise ordinary, pooled same-instance/same-value, synchronous nested same/other-TMP, refresh-time
   `外部中文`, unload-window/reload, destroyed object, and native pointer reuse. No non-owned Chinese may
   become Japanese.
3. Toggle F2 across skill text, inactive UI, history/list pooling, and display-off background production;
   verify input delivery, glyphs, wrapping, clipping, and scan latency.
4. Perform repeated explicit load/unload plus application quit and verify exactly one native callback is
   added and the same callback is removed with no duplicate.
5. Race config reload, quit, blocked HTTP, and a timed-out worker. The generation must quarantine, late
   publication must be rejected, and eventual completion must not reopen load.
6. Force cache locks/crash points around temp/canonical/backup writes and an oversized/chunked endpoint;
   recover the newest valid checksum epoch and keep memory/read volume within the documented bounds.

## Read-only game and host statement

`C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl` was **never launched and never written**. Every Docker
command that referenced it used a read-only bind mount. Source was also mounted read-only and copied into a
disposable writable container layer before build, test, mutation, fault injection, metadata scan, or
decompilation. All temporary outputs/tools were destroyed by `docker run --rm`. No host dependency was
installed, upgraded, downgraded, or reconfigured, and no host `dotnet` command was run.
