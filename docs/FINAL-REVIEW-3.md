# Final Release-Gate Review 3

## VERDICT: DO NOT SHIP

The scoped FR2 fixes are materially effective: full CJK/escaped aggregate pressure is rejected before dirty state, the snapshot-size estimate matches the actual serializer byte-for-byte, and a timed-out `PatchAll`/component/native boundary rolls back its late side effect and cannot reopen the generation. The fresh DLL is also standalone and has exactly the approved three Prefix hooks.

This HEAD is nevertheless not releasable. Independent validation found two source-level release failures:

1. **FR3-1 (Major):** a valid checksummed snapshot with `Epoch = long.MaxValue` is accepted. The next accepted mutation wraps the epoch to `long.MinValue`; `Flush()` reports success and clears dirty state, but restart rejects that new canonical state and recovers the old backup, losing the accepted mutation.
2. **FR3-2 (Major):** cleanup removes active hooks/components/delegates/workers, but does not reach the required zero-static-state terminal condition. The static lifecycle gate retains the generation owner, all nine `GenerationResources.*Resource` handles remain non-null after a normal unload, their rollback delegates retain captured resource objects, and all 13 static `ConfigEntry` properties remain populated. A timed-out late-component run reproduced the same retention after the boundary returned and the generation was permanently quarantined.

These are source defects, not real-game uncertainties. Runtime gates cannot make a falsely durable epoch or permanently retained failed-generation state correct, so no game canary handoff is issued for this HEAD.

## Baseline and review protocol

- **Reviewed production/source HEAD:** `ae0c022be24d2b579851aee49a9e7cae8d84560e`
- **Expected HEAD:** confirmed before review bookkeeping.
- **Initial worktree:** clean; `git status --short --branch` returned only `## main`.
- **Review-in-progress commit:** `b0a4fbe5feff08b52cc8b5c7b8101ef77db0d087` (`docs/FINAL-REVIEW-3.md` only).
- **Complete-report commit:** `HEAD`, the commit containing this completed report. Its exact SHA is supplied in the reviewer response because a Git commit cannot embed its own SHA.
- **History inspected:** every commit and changed file in `2d68318..ae0c022`, with focused review of FR2-1 commits `3a91e50`, `c112cad`, `aae14fc`, `26462fc` and FR2-2 commits `2d7c49e`, `19cd2cb`, `be863e5`, `c4d9d3f`, `84e08fa`, `ae0c022`.
- **Required prior material read:** `FINAL-REVIEW.md`, `POST-FIX-REVIEW.md`, `FINAL-REVIEW-2.md`, and `FIXER-CONTEXT.md` in full.
- **Review completed:** 2026-08-23T02:16:13+08:00.
- Apart from this report, the reviewer modified no production, test, script, README, or other documentation file. All reviewer tests, fault injection, decompilation, and mutations existed only in disposable Docker layers.

## Finding disposition matrix

`CLOSED` means the source property was independently reproduced at its relevant boundary. `PARTIAL` means the original fix is real but the larger release property remains incomplete.

| ID | Status | Independent disposition |
|---|---|---|
| **B-1** | **CLOSED** | Non-token TMP setters invalidate first; refresh writes use one-shot object/ID/generation/epoch ownership; a reviewer refresh-time nested external assignment stayed Chinese. Removing the final generation check reproduced `外部中文 -> 確認する` and was killed. |
| **B-2** | **PARTIAL** | Active late resources now commit-or-rollback and failed generations cannot reopen. FR3-2 shows the generation does not release its static reservation/config state after rollback. |
| **M-1** | **CLOSED** | Native add/remove is serialized around one retained `Il2CppSystem.Action`; exact-source identity/interleaving tests and the fresh game API agree. Real void native removal remains game-only confirmation. |
| **M-2** | **CLOSED** | No skill/data hook exists in source or artifact; skill output reaches the final TMP Prefix only, and F2 restoration passes there. |
| **M-3** | **CLOSED** | Text, generated/pending/raw, reverse, provenance, queue, retry, backlog, progress, HTTP, and file payloads have enforced count/byte limits. Aggregate JSON admission also held under full pressure. |
| **M-4** | **PARTIAL** | Paced terminal retries and coherent state/temp/backup recovery work for ordinary epochs. FR3-1 allows `Flush()` to claim an invalid wrapped epoch as durable and clear dirty state. |
| **M-5** | **PARTIAL** | Exact production wildcard linking is real; 20 configured and four meaningful reviewer mutants were killed. The suite omitted both FR3 assertions and therefore did not establish release correctness. |
| **M-6** | **CLOSED** | README is upstream-neutral and accurately qualifies bounded eligibility, output validation, provenance, and UI coverage. |
| **N-1** | **CLOSED** | Target discovery and Harmony ownership are required; failure throws and rolls back. Artifact has exactly three Prefixes and no Postfix/data target. |
| **N-2** | **PARTIAL** | Stage/callback/worker/persistence waits are bounded and late side effects roll back. Individual synchronous rollback/filesystem calls can still outlive a nominal aggregate deadline, and FR3-2 leaves terminal static ownership behind. |
| **PF-1** | **CLOSED** | A timed-out reload worker remains tracked, faults the outer generation, rejects reload 2, and is observed until actual completion. |
| **PF-2** | **PARTIAL** | Newest valid canonical/temp/backup selection and checksum rejection work, but the accepted epoch domain has no successor rule at `long.MaxValue` (FR3-1). |
| **PF-3** | **CLOSED** | Production uses `ResponseHeadersRead`; unknown-length content stops at 128 KiB plus one 8 KiB read. |
| **PF-4** | **CLOSED** | Reverse accounting includes source plus translation; the 4,000-character case reports exactly 12,003 UTF-8 bytes. |
| **PF-5** | **CLOSED** | Final placeholder expansion is budget-checked; invalid expanded output stays source and is not stranded in provenance. |
| **PF-6** | **PARTIAL** | The gate is well-formed and mutation failures are intentional, but max-epoch durability and terminal static release were unasserted. |
| **PF-7** | **CLOSED** | README wording matches bounded source/output and provenance behavior. |
| **FR2-1** | **CLOSED as scoped** | Default-encoder JSON escaping, quotes/control characters, envelope, generated/pending/raw aggregate, rejection-before-dirty, flush, and restart all matched exact byte accounting. FR3-1 is a separate epoch-domain defect. |
| **FR2-2** | **PARTIAL** | Late Harmony/component/native active effects are removed and replacement load is blocked. The required zero-static-state postcondition fails under FR3-2. |
| **FR3-1** | **OPEN — MAJOR** | Maximum accepted epoch wraps negative, `Flush()` returns true, dirty clears, and restart loses the new accepted mutation. |
| **FR3-2** | **OPEN — MAJOR** | Rolled-back reservation handles/captured resources and every static config entry remain rooted through the static failed/current generation owner. |

## New release-blocking findings

### FR3-1 — Major — maximum valid epoch produces a falsely successful, unrecoverable next commit

**Source / symbols**

- `MuvluvLLMMod/TranslationCache.cs:510-518` — `snapshotEpoch = ++nextSnapshotEpoch` is unchecked.
- `TranslationCache.cs:540-560` — a successful file replacement clears `dirty` without validating the newly generated epoch domain.
- `TranslationCache.cs:1137-1149` — admission deliberately reserves an envelope at `long.MaxValue`.
- `TranslationCache.cs:1249-1272` — every nonnegative checksummed epoch, including `long.MaxValue`, is accepted; every negative epoch is rejected on restart.

**Exact reproduction**

A throwaway console linked the exact production `TranslationCache`, `TranslationBudget`, and `TextTemplate`. It wrote an ordinary valid state, changed only its epoch/transaction/checksum to a correctly checksummed `long.MaxValue` state using the production checksum method, loaded it, accepted a new generated pair, flushed, and restarted.

The result reproduced on the production target framework/runtime (`mcr.microsoft.com/dotnet/sdk:6.0`, runtime 6.0.36):

```text
accepted=True
flush=True
written_epoch=-9223372036854775808
restart_old=True
restart_new=False
ACCEPTED_BUT_LOST=True
```

The replacement write preserves the max-epoch canonical as `.bak`; restart rejects the negative canonical and correctly chooses that older valid backup. Thus the recovery policy itself exposes the loss. This is not an infinite retry: it is worse—persistence reports success and clears dirty state for a snapshot that its own loader rejects.

**Impact**

A syntactically valid, checksummed cache state can place the cache in an accepted-but-not-durable mode. The checksum is integrity metadata, not authentication; producing such a state requires no secret. This violates the durable snapshot invariant and reopens M-4/PF-2 even though ordinary aggregate-size admission is fixed.

**Suggested correction**

Define a closed epoch domain and use checked advancement. At minimum, reject/quarantine an epoch that has no valid successor before accepting runtime mutations, and never clear dirty state unless the just-written snapshot also passes the loader's schema/domain validation. A robust journal can add a generation/era identifier and compare `(era, epoch)`, or safely rotate all candidates before rebasing. Add max and max-minus-one canonical/temp/backup tests that require either safe rejection before mutation or restart recovery of every accepted mutation.

### FR3-2 — Major — terminal cleanup leaves static config and captured generation resources rooted

**Source / symbols**

- `MuvluvLLMMod/Plugin.cs:32-55` — `GenerationResources` stores nine reservation properties.
- `Plugin.cs:408-418` — cleanup clears direct cache/resolver/machine/Harmony/component/CTS/lease fields, but not any reservation property and not `PersistenceTask`.
- `MuvluvLLMMod/PluginLifecycleGate.cs:666-669,693-697` — the static gate's current generation retains its `Owner` and resource owner graph after cleanup.
- `PluginLifecycleGate.cs:923-949` — successful rollback removes a reservation from `ResourcesUnsafe`, but the reservation still retains its readonly rollback delegate/captured objects and remains referenced by `GenerationResources`.
- `MuvluvLLMMod/Config.cs:12-24,143-163` — `Shutdown` removes the handler/lease/private `config`, but never clears any of the 13 static `ConfigEntry` properties, including `LlmApiKey`.

**Exact reproductions**

1. After a complete successful exact-source `Plugin.Load()` / `Unload()`, active resources were gone and `OutstandingResourceCount` was zero, but reflection found every reservation property still non-null:

```text
ConfigurationResource, MachineOwnerResource, CacheResource,
HarmonyResource, HotkeyResource, QuittingResource,
PersistenceResource, MachineStartupResource, ActivationResource
outstanding=0
```

2. In a separate exact-source test, fake `AddComponent` was blocked, cleanup timed out, the component boundary was released, and late rollback completed. Hooks, components, native callbacks, direct owner fields, and `OutstandingResourceCount` were all zero. The hard terminal-state assertion still failed with:

```text
retained reservation fields=[ConfigurationResource,MachineOwnerResource,
CacheResource,HarmonyResource,HotkeyResource]
config entries=[Translation,LlmEnable,LlmEndpoint,LlmModel,LlmApiKey,
LlmTimeoutSeconds,LlmRetryCount,LlmRequestsPerSecond,LlmMaxInFlight,
LlmTranslatePeriodSeconds,LlmRefreshPeriodSeconds,CacheDirectory,
DebugLogSeenText]
outstanding=0
```

Because this timed-out generation is quarantined, no later load replaces `current`; the retained graph lasts until process exit. Source inspection confirms the resource delegates capture the local cache, Harmony owner, component wrapper, machine lifecycle, persistence handle, and activation lease as applicable.

**Impact**

The FR2 active-safety objective is met—no hook, worker, callback, or component remains active—but the explicitly required zero-static-resource/config postcondition is not. A failed generation permanently roots bounded but substantial cache/machine/resource graphs and the API-key config entry. `OutstandingResourceCount == 0` is therefore not proof that ownership was released.

**Suggested correction**

Clear all `GenerationResources.*Resource` properties and reset `PersistenceTask` during teardown; keep pending reservations solely in the generation's rollback list. After a rollback succeeds, clear its delegate/captured state. Clear static `ConfigEntry` properties in `Config.Shutdown` (or move them into the generation owner), and detach/null the generation owner when late stages, callbacks, and resource rollbacks have all drained. Add exact-source reflection plus `WeakReference` tests for successful unload and blocked PatchAll/component/native late completion; assert both active counts and static/captured ownership are zero.

## FR2-1 adversarial cache audit

### Exact serializer math and full pressure

The new accounting uses the same default `System.Text.Json` encoder and indented serializer as persistence. Independent reflection compared the private estimate with a separately serialized maximum-envelope snapshot.

On .NET 6.0.36 with 4,096 attempted CJK pairs (341 CJK code units / 1,023 admitted raw UTF-8 bytes per pair):

```text
accepted=4075, rejected=21
estimated reserve bytes=8,386,588
exact serialized reserve bytes=8,386,588
math_equal=True
actual committed state bytes=8,386,570
restart generated count=4075
```

A second mixed aggregate used CJK, quotes, backslashes, U+0001/U+0002 controls, and newlines across generated, pending, and raw state:

```text
accepted observations=1067
accepted generated=3397, rejected generated=699
retained generated/pending/raw bytes=3,121,843 / 523,897 / 522,830
actual state bytes=8,387,339
estimated reserve=8,387,357
exact reserve serialization=8,387,357
```

After that flush, an additional full-length kana mutation was rejected with `dirty=False` and an unchanged retained snapshot. Restart recovered exactly `3397/1067/1067` generated/pending/raw entries. Legacy mirrors loaded the same admitted aggregate; where cleanup was required, the committed migration state remained within the same admission policy. Existing exact tests also covered epoch-zero migration and valid/corrupt canonical/temp/backup selection.

### Persistence behavior

- Accepted ordinary aggregate states serialized and restarted coherently.
- Aggregate rejection occurred before collection mutation and before dirty state.
- Normal writer failure keeps dirty signaled and retries at the paced delay; terminal flush is capped at five paced attempts and the stated budget.
- Temp/backup selection uses highest valid epoch with canonical only as a tie-breaker; corrupt checksum and negative epoch candidates lose.
- FR3-1 is the sole independently reproduced accepted-but-lost state: maximum valid epoch wraps into a loader-invalid canonical while being reported successful.

## FR2-2 lifecycle/resource audit

### Active late-publication behavior

A reviewer exact-source test (production `Plugin`, `Patch`, `PluginLifecycleGate`; only Harmony boundary stubbed) performed the required sequence:

1. block `Harmony.PatchAll` before publication;
2. call `Unload()` and wait through the five-second quiescence deadline;
3. observe `false`, one fallback unpatch, and zero hooks;
4. release PatchAll, observe the transient publication of exactly three hooks, then let the production stage return;
5. observe the reservation's late rollback unpatch, final zero hooks, null active cache/resolver/component, zero native callbacks;
6. call `Load()` and verify no new stage/resource appeared; call `Unload()` again and verify safe `false` with zero hooks.

The focused reviewer test passed in about five seconds. Source tracing confirmed each production side effect is reserved before its external boundary and only published through `Commit`; canceled/failed commits synchronously execute retained rollback. Harmony additionally retires TMP state before and after unpatch.

A separate blocked-`AddComponent` run proved the component was destroyed after late return. The prescribed suite also exercised blocked native registration with exact delegate removal, and linked/generic production-resource tests covered late persistence cancellation and machine shutdown. No lock-order cycle was found in reservation commit/rollback; rollback callbacks execute outside the lifecycle monitor.

### Residual lifecycle limits

- The generation correctly stays `Failed`; repeated unload retries only failed retained rollbacks and cannot reopen load.
- A rollback API or synchronous filesystem call that itself never returns can still outlive the nominal overall deadline; quarantine remains the safety boundary.
- FR3-2 means active teardown is not equivalent to releasing the statically rooted owner graph.

## Regression evidence for prior properties

| Property | Evidence at reviewed HEAD |
|---|---|
| TMP external preservation / F2 | Nested same/other TMP setters, unload/reload, valid restore, oversized output, and reviewer refresh-time external setter passed. Final-generation-check mutant produced prohibited Japanese and failed. |
| Config lifecycle | Handler uses a generation lease, API key logging is definition-redacted, stale callbacks cannot control a new generation, and late activation is revoked. Static entry release fails under FR3-2. |
| Native delegate | Coordinator retains one converted object and serializes add/remove; blocked late registration removes exact identity. Actual game API is `Il2CppSystem.Action` add/remove. |
| Response streaming | `ResponseHeadersRead` plus bounded stream read stopped a 512 KiB unknown-length producer within 131,072..139,264 bytes. Reviewer removal of the streaming ceiling consumed all 524,288 bytes and failed. |
| Reverse accounting | Source plus translation is charged on insert/update/ambiguity/eviction; exact retained value was 12,003 bytes. |
| Output expansion | Final filled output is budget-checked; invalid output stays source, generated entry is removed/requeued, and no unusable F2 provenance is created. |
| Snapshot journal | Highest valid epoch, checksum, temp promotion, backup preservation, mirror semantics, and aggregate admission pass for ordinary epochs. Max-epoch successor semantics fail under FR3-1. |
| Timeouts/quarantine | Stage/callback/worker/persistence waits are bounded; later teardown runs and failed generations block reload. Synchronous rollback calls remain best-effort, and static owner release fails under FR3-2. |

## Exact-source integration and mutation audit

### Source-link proof

`MuvluvLLMMod.IntegrationTests.csproj` disables default compile items and wildcard-links `../MuvluvLLMMod/*.cs`; only `CompilerServices.cs` is removed because net8 supplies those compatibility attributes. Every current production source is top-level, so `Plugin`, `Patch`, `Config`, cache, HTTP, lifecycle, delegate, queue, and budget implementations are the checked-in source rather than copies. `Stubs/` replaces only BepInEx/Harmony/Unity/TMP/native boundary types.

The limitations remain honest: fake Harmony records ownership but is not an IL2CPP detour; fake Unity uses managed objects; fake native removal can verify identity while the real remove API is void; and fonts/input/layout/UI coverage remain game-only.

### Prescribed 20-mutant gate

The script was inspected before execution. It copies the read-only source into a temporary base and gives each mutant a separate copy; it requires the exact 39-test baseline, an emitted integration DLL, a nonzero focused result, and the selected test name in output. All 20 mutations were compile-valid and failed their intended assertion:

- B1 token, B2 reopen, M1 delegate conversion, N1 verification, M3 budget, M4 terminal retry;
- PF1 worker ownership; PF2 filename/checksum/epoch/backup cases; PF3 buffering; PF4 reverse bytes; PF5 output;
- bounded stage/callback waits; exact patch surface; and both FR2-1/FR2-2 commit/rollback mutations.

Result: **39/39 baseline; 20/20 configured mutants killed.**

### Four reviewer mutations

Each was made in an independent Docker copy and compiled to `IntegrationTests.dll`:

| Area / mutation | Focused result |
|---|---|
| Lifecycle — omit generation cancellation when cleanup starts | **Killed:** cleanup-during-load test expected cancellation and failed. |
| Cache — count raw UTF-8 instead of exact JSON-string bytes | **Killed:** full CJK test reached an unwritable state and failed its flush assertion. |
| Patch — remove the final refresh assignment-generation check | **Killed:** reviewer baseline passed; mutant changed expected `外部中文` to prohibited `確認する`. |
| HTTP — disable the unknown-length streaming accumulation ceiling | **Killed:** producer emitted 524,288 bytes instead of the allowed 131,072..139,264 range. |

Two exploratory lifecycle edits survived because the remaining late-stage drain and timeout-failure paths were deliberately redundant; they were not counted as meaningful reviewer mutants.

The green gate therefore has useful sensitivity, but FR3-1 and FR3-2 demonstrate that it is not a completeness proof.

## Required Docker validation

Environment:

```text
Docker Server: 29.6.2 (overlayfs)
Primary image: mcr.microsoft.com/dotnet/sdk:8.0
Digest: sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc
Target-runtime review image: mcr.microsoft.com/dotnet/sdk:6.0
Digest: sha256:c8fdd06e430de9f4ddd066b475ea350d771f341b77dd5ff4c2fafa748e3f2ef2
```

The checkout was mounted read-only, copied to a container-writable layer, and detached at exact reviewed HEAD `ae0c022` before validation.

### Solution tests

```text
dotnet test MuvluvLLMMod.sln -c Release
MuvluvLLMMod.Tests:            251 passed, 0 failed, 0 skipped
MuvluvLLMMod.IntegrationTests:  39 passed, 0 failed, 0 skipped
TOTAL:                         290 passed, 0 failed, 0 skipped
```

### Production plugin build

The game was mounted at `/game,readonly`:

```text
dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game
Build succeeded: 0 warnings, 0 errors
Elapsed: 6.77 s
```

Fresh reviewed artifact:

```text
artifacts/bin/MuvluvLLMMod/Release/net6.0/MuvluvLLMMod.dll
size:    201,728 bytes
SHA-256: 33724e9c4cdf1dc2a0d53118186ca428badcf6f34b9b9333f3fb4dae0f1eb4df
```

### Mutation gate

```text
bash scripts/mutation-gate.sh
exact-source baseline: 39 passed, 0 failed, 0 skipped
configured mutants:    20/20 compile-valid and killed
```

## Artifact, standalone, and game API evidence

### Fresh DLL

External assembly references are limited to BepInEx, Harmony, `GameUi`, Il2CppInterop/Il2Cppmscorlib, Unity Input/TMP/Core, and standard `System.*` assemblies. There is no upstream-mod assembly reference.

Binary UTF-8 and UTF-16 scans plus full decompilation found all of the following absent:

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

Custom/decompiled attributes contain one `BepInPlugin`, exactly three `HarmonyPrefix`, exactly three `HarmonyPatch`, and no `HarmonyPostfix`/`BepInDependency`. Targets are exactly:

```text
TMPro.TMP_Text.set_text                 Prefix
ScenarioController.Refresh()           Prefix
ScenarioController.Leave()             Prefix
```

### Read-only target game API

Container-only ILSpy inspection of the installed interop/runtime assemblies confirmed:

- `ScenarioController` exposes zero-argument `void Refresh()` and `UniTask Leave()` (as well as an unrelated three-argument `Refresh` overload);
- `TMP_Text.text` is a virtual string property backed by the expected `set_text(String)` interop method;
- `UnityEngine.Application.add_quitting/remove_quitting` both take `Il2CppSystem.Action`;
- `BasePlugin.Unload()` defaults to `false`, while production overrides it; `AddComponent<T>()` routes through `IL2CPPChainloader.AddUnityComponent<T>()`; and
- Il2CppInterop's reference-array wrapper calls `Il2CppObjectPool.Get<T>`. The installed pool is pointer-keyed and returns a still-live cached wrapper, supporting ordinary provenance wrapper identity while caching is enabled.

Relevant installed hashes:

```text
GameUi.dll:                  0770584f1831ffab4e85bb62774ea3e88076daccb283b3add358ffd437ccba7f
Unity.TextMeshPro.dll:       a9c01299a3740c77af77d082c71adeb303a2074bca8b4de3d65fd659b5228347
Il2CppInterop.Runtime.dll:   65ab051a681c2c1e52df7596ff738be401aae72cff882ff18241cf54a3ea38a4
BepInEx.Unity.IL2CPP.dll:    62c8246a3076bfe459ec80c482381228d0eb1dde3434fe97ae5255fef406fe72
```

These checks validate signatures/metadata only; they do not claim a real detour, native event removal, input, font, layout, or UI-coverage run.

## Release handoff

No real-game canary handoff is authorized because source blockers remain. Fix FR3-1 and FR3-2, add their exact-source assertions and mutations, and rerun the complete Docker/source/artifact gate before any in-game canary. The unresolved game-only questions (detour ordering, native removal semantics, wrapper reuse with caching disabled, TMP coverage, F2 input, fonts/layout, and refresh cost) cannot waive either source defect.

## Exact game/read-only statement

`C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl` was **never launched and never written**. Every command that referenced it used a Docker bind mount with `readonly`. Source was also mounted read-only and copied into disposable container storage before every build, test, mutation, fault injection, metadata scan, or decompilation. All outputs and temporary tools were destroyed by `docker run --rm`. No host dependency was installed or reconfigured, and no host `dotnet` command was run.
