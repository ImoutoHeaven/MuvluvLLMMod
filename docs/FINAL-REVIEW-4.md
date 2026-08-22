## VERDICT: DO NOT SHIP

The required build, the 293-test baseline, and all 24 configured mutants are green, and the FR2 late-stage fix plus the basic FR3 detachment/max-epoch fixes are real. They are not sufficient for release. Independent exact-source Docker probes found two deterministic source failures and one release-assurance failure:

1. **FR4-1 — Major durability defect:** at `Epoch = long.MaxValue - 1`, the cache accepts a mutation, consumes `long.MaxValue` before attempting the write, and permanently marks the epoch exhausted. If that one write fails transiently before a recoverable temp file exists, none of the remaining normal/terminal retries reaches the writer. Restart loses the accepted mutation.
2. **FR4-2 — Major lifecycle retention defect:** after a machine shutdown timeout, the worker can eventually stop and leave `TrackedStoppingWorkerCount == 0`, but the cached faulted `shutdownTask` makes every resource-rollback retry throw forever. The statically retained failed generation therefore keeps one `PluginGenerationResource` and its machine-lifecycle delegate permanently.
3. **FR4-3 — Major assurance gap:** deleting the final refresh-time assignment-generation check in `Patch.RefreshAllTmpText` is compile-valid and leaves the entire committed solution at **293/293 passed**. A reviewer-added exact-source assertion shows why that guard is load-bearing: the mutant overwrites a nested external `外部中文` assignment with `確認する`.

FR4-1 and FR4-2 are production-source properties, not IL2CPP-stub uncertainty. Runtime canaries cannot repair them. No game canary handoff is authorized for this HEAD.

## Review baseline and protocol

- **Reviewed source/production HEAD:** `2f2b04644fb03be53df4e617d6801e1031a454f1`.
- **Initial state:** clean; `git status --short --branch` returned only `## main`.
- **Review-in-progress commit:** `9401c4013aa730372edd311f2be6702e50e94d97` (this report file only).
- **Completed:** `2026-08-22T19:24:02Z`.
- **Complete-report commit:** the commit containing this completed file; its SHA is supplied in the release-gate response because a commit cannot contain its own SHA.
- Read in full: `FINAL-REVIEW.md`, `POST-FIX-REVIEW.md`, `FINAL-REVIEW-2.md`, `FINAL-REVIEW-3.md`, and `FIXER-CONTEXT.md`.
- Inspected all 58 commits and changed-file inventory in `2d68318..2f2b046`, with direct source/diff review of the FR2-1, FR2-2, and FR3 commits named in the request.
- Apart from this report, no production, test, script, README, or other documentation file was changed. Added probes and mutants existed only in disposable container layers.

## New findings

### FR4-1 — Major — a transient failure consumes the only terminal successor before commit

**Source trace**

- `TranslationCache.cs:528-544` advances the epoch while holding the cache lock, before serialization or any file operation.
- `TranslationCache.cs:706-718` assigns `nextSnapshotEpoch = long.MaxValue` and immediately sets `snapshotEpochExhausted = true`.
- `TranslationCache.cs:565-586` can then fail the authoritative write and preserve `dirty`, but does not restore or retain the attempted epoch for retry.
- Every later call returns at `TranslationCache.cs:708-712`, before invoking the writer. The diagnostic says the dirty state is “retryable”, but it is not.

**Independent production-writer reproduction**

A Docker-only exact-source xUnit probe wrote a schema/checksum-valid `long.MaxValue - 1` canonical state, loaded it, and successfully called `StoreGenerated("新する", "新译")`. It then created a directory at the production temp-file path so the first real `File.WriteAllText(cache.state.v1.json.tmp, ...)` failed. After removing that directory, it invoked the normal terminal retry path and restarted from disk:

```text
StoreGenerated:                  true
first Flush():                   false
transient obstruction removed:  yes
FlushTerminal(1s):               false
IsDurableMutationBlocked:        true
restart recovered new entry:     false
```

The focused assertion failed with:

```text
real writer did not retry the accepted mutation:
retry=False, blocked=True, recovered=False
```

A second probe using the injectable production writer reported the same state with **one authoritative attempt only** despite five terminal-loop iterations. The adjacent no-failure case passed: max-minus-one committed to max, restarted with the new entry, and became read-only. Thus the defect is specifically commit-before-success epoch consumption, not rejection of `long.MaxValue` itself.

**Impact and correction requirement**

This reopens M-4/PF-2/FR3-1 at the terminal boundary. Cleanup truthfully fails and quarantines, but its promised transient retries are bypassed and process exit loses an already accepted cache mutation. Reserve an in-flight successor without permanently consuming it; retries must reuse that exact epoch/snapshot until authoritative commit succeeds. At the terminal successor, block concurrent durable mutation while the write is in flight, but on failure keep the same successor retryable. Add real-writer and injected-writer max-minus-one transient tests, plus normal and terminal retry/restart assertions.

### FR4-2 — Major — eventual machine quiescence cannot settle its retained generation resource

**Source trace**

- Production registers `machineLifecycle.Shutdown` as the machine resource rollback (`Plugin.cs:284-308`).
- `MachineTranslatorLifecycle.ShutdownAsync` permanently returns the first `shutdownTask` (`MachineTranslatorLifecycle.cs:252-267`).
- A timeout observes the worker’s eventual `StopCompletion`, but `CompleteShutdownAsync` throws and leaves that cached task faulted (`:293-314`, `:413-455`).
- `PluginGenerationResource.ExecuteRollback` removes itself only when its callback returns without exception (`PluginLifecycleGate.cs:932-964`). Repeated cleanup does retry outstanding resources (`:235-254`, `:288-301`), but receives the same faulted shutdown task forever.
- FR3 detaches `PluginGeneration.Owner`, yet the static gate intentionally retains the failed generation and its `ResourcesUnsafe`; the remaining resource delegate roots the machine lifecycle.

**Independent exact-source reproduction**

A disposable integration test used the linked production `PluginLifecycleGate`, `MachineTranslatorLifecycle`, `MachineTranslator`, cache, queue, and resource classes. A translation ignored cancellation until explicitly released. Cleanup timed out and failed as expected. The probe then released the worker, awaited `StopCompletion`, waited for the lifecycle observer to report zero tracked workers, and invoked cleanup again:

```text
first cleanup:                   false / generation Failed
owner after terminal cleanup:    null
outstanding resources initially: 1
worker StopCompletion:           completed
TrackedStoppingWorkerCount:      0
repeated cleanup:                false
outstanding resources finally:   1   (expected 0)
```

The worker is no longer active and replacement load remains blocked, so active-safety quarantine works. The required zero-resource terminal postcondition does not: the failed generation statically retains the settled machine reservation for process lifetime.

**Impact and correction requirement**

This makes B-2/N-2/FR3-2 incomplete for the failed-machine path and defeats the requested repeated-cleanup/resource-release gate. Preserve the terminal failure/quarantine result, but separate it from eventual resource settlement. Once every retained worker’s `StopCompletion` has completed, the machine reservation must be removable without reopening the generation or pretending the original cleanup succeeded. Add an exact resource test that requires owner/config detachment immediately and `OutstandingResourceCount == 0` after eventual stop plus repeated cleanup.

### FR4-3 — Major assurance gap — the final TMP assignment guard is not mutation-pinned

`Patch.cs:123-131` correctly requires both the lifecycle epoch and `tmpProvenance.IsCurrent(assignment)` immediately before a plugin refresh writes. The current integration test exercises nested setters during an initial Prefix resolution, not an external setter that occurs during `RefreshAllTmpText` resolution.

Independent mutation:

```text
tmpProvenance.IsCurrent(assignment)  ->  true
```

The mutant compiled and the complete committed solution still reported:

```text
MuvluvLLMMod.Tests:             251 passed
MuvluvLLMMod.IntegrationTests:   42 passed
TOTAL:                          293 passed
```

A reviewer-added exact-source test then set `確認する -> 确定`, turned display off, and made the synchronous log callback assign `外部中文` to the same TMP object during refresh. Baseline preserved `外部中文`; the mutant failed with:

```text
Expected: 外部中文
Actual:   確認する
```

This is the hard B-1 prohibited outcome. The current source is correct, but M-5/PF-6 remains open because a one-token critical guard can regress while every committed test and the configured gate stay green. Commit the refresh-time nested-assignment assertion and a focused mutant for this exact check.

## Historical finding disposition

`CLOSED AS SCOPED` means the original behavior was independently reproduced as fixed; it does not hide a new adjacent finding.

| ID | Current status | Independent disposition at `2f2b046` |
|---|---|---|
| **B-1** | **CLOSED in source; assurance unpinned** | One-shot TMP ownership, external invalidation, lifecycle epochs, and final assignment validation are correct. FR4-3 proves the final guard is absent from the committed behavioral gate. |
| **B-2** | **PARTIAL** | Generation admission, shared cleanup, late commit/rollback, and reload quarantine work. FR4-2 leaves a settled machine reservation rooted forever after timeout. |
| **M-1** | **CLOSED AS SCOPED** | One converted `Il2CppSystem.Action` is serialized through add/remove; identity and forced interleaving pass. Real void native removal remains game-only confirmation. |
| **M-2** | **CLOSED AS SCOPED** | No skill/data hook exists; skill output reaches only final TMP assignment. |
| **M-3** | **CLOSED AS SCOPED** | Text, aggregate JSON, durable collections, reverse/provenance, queue/retry/progress, HTTP, and file payloads have enforced bounds. |
| **M-4** | **REOPENED / PARTIAL** | Ordinary paced retries and journal recovery work; FR4-1 makes the sole terminal successor non-retryable after a transient failure. |
| **M-5** | **OPEN — MAJOR ASSURANCE** | Exact source linking is real and 24 configured mutants are valid, but FR4-3 survives all 293 committed tests. |
| **M-6** | **CLOSED AS SCOPED** | README is standalone, upstream-neutral, and qualifies bounded eligibility/provenance/UI coverage. |
| **N-1** | **CLOSED AS SCOPED** | Discovery and ownership of all three required hooks are fail-closed before later resources. |
| **N-2** | **PARTIAL** | Stage/callback/worker/persistence waits are bounded and later teardown runs. FR4-2 prevents eventual machine-resource settlement; synchronous external rollback calls remain best-effort. |
| **PF-1** | **CLOSED for active worker ownership** | A timed-out reload worker remains tracked, faults the outer generation, blocks reload 2, and is observed to actual completion. FR4-2 is the remaining post-completion reservation defect. |
| **PF-2** | **REOPENED / PARTIAL** | Highest valid canonical/temp/backup selection, checksum, promotion, and backup recovery pass. Terminal successor retry semantics fail under FR4-1. |
| **PF-3** | **CLOSED AS SCOPED** | `ResponseHeadersRead` plus bounded streaming stops at 128 KiB plus one 8 KiB read. |
| **PF-4** | **CLOSED AS SCOPED** | Reverse accounting includes source plus translation; exact retained result is 12,003 UTF-8 bytes. |
| **PF-5** | **CLOSED AS SCOPED** | Final filled output is budget-checked; invalid output remains source and is not stranded in provenance. |
| **PF-6** | **OPEN — MAJOR ASSURANCE** | The supplied gate validates its 24 mutations, but omits FR4-1, FR4-2, and the surviving refresh-time guard mutant. |
| **PF-7** | **CLOSED AS SCOPED** | README language matches bounded input/output and provenance behavior. |
| **FR2-1** | **CLOSED AS SCOPED** | Exact default-encoder escaping, structural envelope, generated/pending/raw aggregate admission, rejection-before-mutation, flush, and restart all hold. |
| **FR2-2** | **CLOSED AS SCOPED** | Pending Harmony/component/native/persistence/machine stage effects commit-or-rollback; blocked PatchAll leaves zero final active effects and cannot reload. |
| **FR3-1** | **REOPENED / PARTIAL** | A loaded max epoch is read-only and never wraps. FR4-1 shows max-minus-one can consume max on a failed, not committed, attempt. |
| **FR3-2** | **REOPENED / PARTIAL** | Normal, persistence-failure, and late-PatchAll owner/config roots detach. FR4-2 leaves a failed machine resource under the retained generation token. |
| **FR4-1** | **OPEN — MAJOR** | Terminal successor is consumed before authoritative success. |
| **FR4-2** | **OPEN — MAJOR** | Eventually stopped machine rollback remains permanently outstanding/rooted. |
| **FR4-3** | **OPEN — MAJOR ASSURANCE** | Critical final-generation Patch mutant survives the complete committed suite. |

## Cache and persistence audit evidence

Positive behavior independently retained:

- The exact serializer options and JSON-string cost are used for CJK, quotes, backslashes, controls, newlines, markup, and placeholders. Full-cap CJK pressure rejects before creating an unwriteable aggregate; normal flush, terminal no-op flush, bounded file size, and restart pass.
- Generated replacement/eviction and pending removal are planned before mutation. Combined pending/raw admission is atomic. Aggregate rejection returns before dictionary changes and before `MarkDirtyUnsafe`.
- A reviewer mutant that charged raw UTF-8 instead of encoded JSON cost compiled and failed the full-CJK flush assertion.
- Canonical/temp/backup candidates are bounded, schema/checksum validated, selected by highest nonnegative epoch, promoted, and deterministic on ties. Corrupt checksum and negative epoch lose; prior canonical backup recovery passes. Legacy mirrors are not the commit decision.
- Ordinary transient terminal failure retries and succeeds; persistent failure remains dirty and quarantines. A loaded exact max epoch rejects durable mutation without changing retained state and refuses dirty flush rather than wrapping.

Release failure: FR4-1 means the max-minus-one first failure leaves `dirty` signaled but structurally impossible to retry. Therefore the requested “no accepted-but-lost mutation” property fails.

## Lifecycle/resource audit evidence

A reviewer-added exact-source test independently performed the requested blocked-`PatchAll` sequence using the production five-second deadline:

1. block before publication;
2. cleanup returns `false`, detaches owner/config/resource fields, invokes fallback unpatch, and reports zero hooks;
3. attempted `Load()` is rejected;
4. release publication (three transient approved hooks), then release the stage;
5. late rollback unpatches to zero;
6. verify no cache/resolver, config roots, component, native callback/delegate, persistence handle/task, machine owner, or outstanding resource;
7. attempt `Load()` again and verify no new stage/resource.

The test passed in about five seconds. Existing exact tests also pass normal successful/repeated cleanup, persistence-failure quarantine/repeated cleanup, blocked component destruction, blocked native add/exact removal, late persistence cancellation, late machine stop, callback/stage deadlines, and failed rollback retry.

Reservation audit:

- configuration, cache, Harmony, component, native delegate, persistence, machine startup, and activation all reserve before their external boundary;
- commit validates current generation/stage before publication;
- failed/late commit executes rollback outside the lifecycle monitor;
- cleanup marks rollback intent without waiting for pending boundaries;
- successful rollback removes the reservation; failed rollback stays quarantined and is retryable;
- no lifecycle-monitor/rollback lock cycle was found.

FR4-2 is the exception: the machine callback is retried, but its cached terminal fault can never transition to a settled rollback after the worker actually ends.

## Regression evidence for prior properties

| Property | Evidence |
|---|---|
| F2/TMP ownership | Baseline nested same/other TMP, stale unload/reload, valid restore, over-budget output, and reviewer refresh-time nested external assignment pass. The reviewer mutant demonstrates the missing committed pin. |
| Config leases/redaction | Subscription starts only after Running; callbacks enter the owning lease; stale events cannot reload a replacement generation; API-key formatting never logs `BoxedValue`; all 13 static entries clear on tested terminal paths. |
| Native delegate identity | Coordinator serializes conversion/add/remove and retains exact identity. Installed game APIs take `Il2CppSystem.Action` for both add and remove. |
| HTTP streaming | Unknown-length 512 KiB producer stops within 131,072..139,264 bytes. Disabling the accumulation ceiling consumes all 524,288 bytes and fails. |
| Reverse bytes | Source plus translation is charged through insert/touch/ambiguity/eviction; 4,000 Japanese characters plus `译` retain exactly 12,003 bytes. |
| Final fill | 4,080-character generated template plus a 100-character placeholder expansion remains original, removes invalid generated output, and requeues safely. |
| Snapshot journal | Ordinary epochs, temp promotion, checksum rejection, backup preservation, aggregate serialization, and mirror semantics pass. FR4-1 is the terminal-successor exception. |
| Timeouts | Reload timeout is terminal, later teardown runs, and no new load is admitted. Worker active ownership drains; FR4-2 leaves only the static failed reservation. |

## Exact-source harness and mutation audit

The integration project disables default compile items and wildcard-links `../MuvluvLLMMod/*.cs`, removing only `CompilerServices.cs` because net8 supplies those compatibility attributes. Every current production source is top-level, so Plugin/Patch/Config/cache/HTTP/lifecycle/delegate/queue/budget code is the checked-in implementation. Stubs replace only BepInEx, Harmony, Unity/TMP, scenario, and native-event boundaries; they do not claim real detours, native conversion, input, layout, fonts, or complete UI coverage.

### Required configured gate

`bash scripts/mutation-gate.sh` was run directly from the read-only source mount. It copied the source and each mutant to disposable `/tmp` directories. Baseline was **42/42**. Every configured mutant emitted `IntegrationTests.dll`, selected the named focused test, and failed semantically rather than through setup/build failure:

- M5: B1 token, B2 reopen, M1 delegate conversion, N1 verification, M3 budget, M4 terminal attempts;
- PF: PF1 worker ownership; PF2 filename/checksum/negative-epoch/backup; PF3 buffering; PF4 reverse bytes; PF5 final output; bounded stage/callback waits; exact three-patch surface;
- FR2: aggregate admission, stage commit, and late Harmony rollback; and
- FR3: max-epoch guard plus owner/config/resource-field detachment.

Result: **24/24 compile-valid configured mutants killed**. Expected warnings were limited to deliberate unreachable branches. The FR3 epoch mutant faults in the selected max-epoch behavior rather than producing a setup failure.

### Reviewer-added mutations

| Area / mutation | Current committed result | Independent disposition |
|---|---|---|
| Cache — charge raw text instead of serialized JSON strings | Focused full-CJK test failed | Compile-valid, killed for unwriteable aggregate. |
| Lifecycle — retain a successfully rolled-back resource in `ResourcesUnsafe` | Successful terminal cleanup test failed | Compile-valid, killed for nonzero cleanup result/resource count. |
| HTTP — disable the streaming accumulation ceiling | Focused producer test failed at 524,288 bytes | Compile-valid, killed for intended assertion. |
| Patch — remove `tmpProvenance.IsCurrent(assignment)` before refresh write | **Full solution survived 293/293** | Reviewer exact-source assertion passed baseline and killed mutant with `外部中文 -> 確認する`. This is FR4-3. |

## Required Docker validation

Environment:

```text
Docker Server: 29.6.2 (linux/x86_64)
Image: mcr.microsoft.com/dotnet/sdk:8.0
Digest: sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc
```

Source was mounted read-only and tested only after copying to container-writable storage. The solution test and artifact build were reset/cleaned to exact `2f2b046`; the mutation script ran directly from the read-only review checkout, whose only post-`2f2b046` change was this report bookkeeping file.

```text
dotnet test MuvluvLLMMod.sln -c Release
  MuvluvLLMMod.Tests:             251 passed, 0 failed, 0 skipped
  MuvluvLLMMod.IntegrationTests:   42 passed, 0 failed, 0 skipped
  TOTAL:                          293 passed, 0 failed, 0 skipped

dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game
  Build succeeded, 0 warnings, 0 errors

bash scripts/mutation-gate.sh
  exact-source baseline: 42 passed, 0 failed, 0 skipped
  configured mutants:    24/24 compile-valid and killed
```

## Fresh artifact, standalone, and game API evidence

Fresh exact-source artifact:

```text
artifacts/bin/MuvluvLLMMod/Release/net6.0/MuvluvLLMMod.dll
size:    203,776 bytes
SHA-256: 2ceb3b1721b99ad66e14978f2657b4747f094241858e1639cc27c9986412be73
```

External assembly references are limited to `0Harmony`, BepInEx Core/IL2CPP, `GameUi`, Il2CppInterop/Il2Cppmscorlib, Unity Input/TMP/Core, and framework assemblies. There is no project/upstream-mod reference and no `BepInDependency` attribute.

Fresh decompilation and raw artifact scans found these all absent:

```text
MuvluvMod
BepInDependency
GenerateFrames
ApplyText
ScenarioChoiceElementComponent
LoadMasterData
SkillDescriptionBuilder
GetDescription
```

The DLL has one `BepInPlugin`, exactly three `HarmonyPrefix` and three `HarmonyPatch` attributes, and no Postfix/data-layer hook. Targets are exactly:

```text
TMPro.TMP_Text.set_text       Prefix
ScenarioController.Refresh()  Prefix
ScenarioController.Leave()    Prefix
```

Read-only installed-game inspection reconfirmed:

- `ScenarioController` has zero-argument `void Refresh()` and `UniTask Leave()`;
- `TMP_Text.text` is a virtual string property using the expected `set_text(String)` interop method;
- `Application.add_quitting/remove_quitting` both take `Il2CppSystem.Action`;
- `BasePlugin.Unload()` and `AddComponent<T>()` have the expected plugin API surface; and
- `Il2CppObjectPool` is native-pointer keyed (`ConcurrentDictionary<IntPtr, WeakReference<Il2CppObjectBase>>`) and exposes `Get<T>(IntPtr)`.

Installed hashes matched the prior reviewed install:

```text
GameUi.dll:                  0770584f1831ffab4e85bb62774ea3e88076daccb283b3add358ffd437ccba7f
Unity.TextMeshPro.dll:       a9c01299a3740c77af77d082c71adeb303a2074bca8b4de3d65fd659b5228347
UnityEngine.CoreModule.dll:  54fbff45a7b1e203fb43d33a55230fc2b374d193e0579b9a01c05d6a4f81850b
Il2CppInterop.Runtime.dll:   65ab051a681c2c1e52df7596ff738be401aae72cff882ff18241cf54a3ea38a4
BepInEx.Unity.IL2CPP.dll:    62c8246a3076bfe459ec80c482381228d0eb1dde3434fe97ae5255fef406fe72
```

These metadata checks do not claim a live native detour, input event, font/layout result, or complete UI route.

## Release handoff

No game-only canary checklist is issued because source blockers remain. Fix FR4-1 and FR4-2, commit the FR4-3 regression/mutant, and rerun the exact required Docker/source/artifact gate before any live-game canary.

## Read-only game and host statement

`C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl` was **never launched and never written**. Every reference to it was a Docker bind mount with `readonly`. Source was likewise mounted read-only; all restores, artifacts, temporary test sources, mutations, decompilation, and fault injection lived only in `docker run --rm` layers. No host dependency was installed, upgraded, or reconfigured, and no host `dotnet` command was run.
