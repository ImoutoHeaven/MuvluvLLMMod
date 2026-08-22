# Final Release-Gate Review 2

## VERDICT: DO NOT SHIP

The five concrete post-review fixes are real: the timed-out reload worker is retained and quarantined, cache recovery chooses the newest valid checksummed epoch, HTTP uses `ResponseHeadersRead`, reverse accounting includes both strings, and final placeholder expansion fails closed. The exact-source test target and its 17 configured mutants also run as claimed.

This HEAD is nevertheless not deliverable. Independent adversarial validation found two release-blocking source defects that the 275 green tests and mutation gate do not cover:

1. **FR2-1 (Major):** the cache admits a legal 4 MiB generated payload that its 8 MiB JSON snapshot representation cannot serialize. Normal persistence then retries forever, terminal flush fails, and the latest accepted epoch is not durable.
2. **FR2-2 (Major):** after a load-stage quiescence timeout, cleanup runs once and marks itself complete, but the already-admitted stage can resume and publish a resource after its teardown step. An exact-source fault-injection test made `PatchAll` publish all three hooks after the sole `UnpatchSelf`; a second cleanup did not retry unpatching.

These are deterministic source defects, not missing in-game confirmation. Quarantine is truthful in both timeout paths, but it does not make the late resource disappear, and it cannot make an unrepresentable cache epoch durable. Runtime gates therefore cannot justify shipment.

## Review baseline and protocol

- **Reviewed production/source HEAD:** `40c4149cfd9670c56b037507174bfcdb39c05503`
- Expected HEAD was confirmed before review bookkeeping.
- **Initial worktree:** clean; `git status --porcelain=v1 --untracked-files=all` produced no output.
- **Review-in-progress commit:** `4e802989e01f6051bcd388703f88ccc939108768` (`docs/FINAL-REVIEW-2.md` only).
- **History inspected:** all 41 commits in `2d68318..40c4149`. After the post-fix verdict, the only production-changing commits are exactly `37c718d`, `8a62ccf`, `0761479`, `aae00b2`, and `139ec4f`; later commits change tests, the mutation gate, README, or review context.
- **Review completed:** `2026-08-22T23:49:29+08:00`.
- The complete-report commit is the commit containing this final file; its exact SHA is supplied in the reviewer response because a Git commit cannot contain its own hash.
- No production, test, script, README, or other file was modified. All added tests, fault injection, and mutations existed only in disposable Docker copies.

## Finding disposition matrix

`CLOSED` means the original defect was independently resolved at the source boundary. `PARTIAL` means material parts are fixed but the release property is still incomplete. No item was marked closed merely because its existing test was green.

| ID | Status | Independent evidence |
|---|---|---|
| **B-1** | **CLOSED** | Every non-token TMP Prefix invalidates first; plugin writes use a one-shot identity/generation/epoch token; lifecycle reset retires provenance. Existing nested/unload tests passed, and an added refresh-time nested external-setter test preserved `外部中文`. Removing the final assignment-generation check changed it to prohibited Japanese and was killed. |
| **B-2** | **PARTIAL** | Generation ownership, shared cleanup, duplicate-initialize rejection, stale callback leases, and worker fault quarantine work. FR2-2 proves an admitted timed-out load stage can still publish after its teardown and remain installed. |
| **M-1** | **CLOSED** | `NativeDelegateCoordinator<T>` serializes conversion/add/remove and retains exact identity. Fresh metadata has the expected single plugin attribute; forced fake-native interleaving passes. Real void native removal remains a game-only confirmation, not a source defect. |
| **M-2** | **CLOSED** | Production and fresh artifact contain no skill-description hook or literal. Skill output reaches only final TMP `set_text`, and its exact-source F2 flow passes. |
| **M-3** | **PARTIAL** | Runtime collections now have real count/payload admission and reverse/provenance account both strings. FR2-1 shows the aggregate durable representation is not coherent with those admission budgets and causes repeated large serialization work. |
| **M-4** | **PARTIAL** | Paced retries, authoritative epochs, checksum, temp/backup recovery, and truthful failure are present. FR2-1 still makes a fully admitted state permanently unwritable and loses its latest epoch on process exit. |
| **M-5** | **PARTIAL** | Exact production files are wildcard-linked and 17 configured plus 4 reviewer-added mutants were killed for intended assertions. The suite still omitted both deterministic FR2 defects; its bounded-stage test asserted only that *new* stages cannot enter, not that the admitted stage cannot publish late. |
| **M-6** | **CLOSED** | README is upstream-neutral, qualifies kana eligibility by admission/output budgets, and limits F2 claims to validated provenance. |
| **N-1** | **CLOSED** | All three required targets require discovery and ownership; failure throws and rolls back. Fresh artifact has exactly three Harmony Prefixes. |
| **N-2** | **PARTIAL** | Worker stop and stage/callback waits are bounded and later teardown steps execute. FR2-2 shows the timeout continuation is not terminal with respect to late side effects; synchronous work after acquiring a teardown resource also is not made cancellable by the nominal deadline. |
| **PF-1** | **PARTIAL** | The timed-out-worker defect itself is closed: worker A remains referenced/observed, reload 2 is rejected, cleanup fails, the generation stays quarantined, and late completion removes only A. The same commit's stage/callback timeout design remains incomplete because of FR2-2. |
| **PF-2** | **CLOSED** | Valid candidates are selected by greatest nonnegative epoch, checksum-invalid candidates lose, recovery is promoted, and canonical/temp/backup tie order is deterministic. Mirror failure leaves the authoritative epoch clean. |
| **PF-3** | **CLOSED** | Actual call is `SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)`; unknown-length production stops within 128 KiB plus one 8 KiB read. |
| **PF-4** | **CLOSED** | Insert, same-pair update, ambiguity, touch, admission, removal, and LRU eviction use recorded source-plus-translation cost. Exact 4,000-character case retains 12,003 bytes. |
| **PF-5** | **CLOSED** | Final `Fill` checks the completed output; resolver removes invalid generated data and leaves/requeues source. Direct and exact fake-TMP oversized routes keep the original through F2. |
| **PF-6** | **PARTIAL** | Source linking and configured mutation execution are valid, and the reviewer-added lifecycle/cache/HTTP/Patch mutants were also killed. Assurance is still incomplete because neither FR2 behavior had a committed assertion or mutant. |
| **PF-7** | **CLOSED** | README matches bounded eligibility/provenance behavior and contains no named upstream implementation or hotkey assumptions. |

## New release-blocking findings

### FR2-1 — Major — admitted cache payload can never fit the authoritative snapshot

**Source/symbols**

- `MuvluvLLMMod/TranslationBudget.cs:19-24,43-44` — generated state admits 4 MiB UTF-8, while the complete state file is capped at 8 MiB.
- `MuvluvLLMMod/TranslationCache.cs:769-794` — `TryStoreGeneratedUnsafe` admits by source-plus-translation UTF-8 payload only.
- `TranslationCache.cs:501-523` — every flush builds and serializes the authoritative state before committing it.
- `TranslationCache.cs:1287-1297` — `JsonSerializer` uses the default encoder plus indented JSON, then rejects serialized UTF-8 over 8 MiB.

The admission budget does not reserve JSON quotes, separators, indentation, envelope/checksum metadata, or escaping. Default `System.Text.Json` escaping emits ordinary CJK code units as six ASCII bytes (`\uXXXX`) even though each occupies three bytes in the cache's UTF-8 admission metric.

**Docker reproduction against exact current source**

A throwaway exact-source integration test inserted 4,096 distinct valid pairs. Each pair contained 341 CJK code units total and cost 1,023 admitted UTF-8 bytes:

```text
StoreGenerated successes:       4096 / 4096
Retained GeneratedCount:        4096
Retained GeneratedUtf8Bytes:    4,190,208
Configured generated maximum:   4,194,304
Flush():                        false
cache.state.v1.json exists:     false
diagnostic:                     bounded snapshot size; persistence was rejected
```

The test passed only because it asserted this defective current behavior. This state is reachable with ordinary CJK source/translation pairs; it does not require malformed disk data or an injected writer failure. Once reached, the persistence loop continues paced serialization attempts, while terminal flush exhausts its attempts and the latest accepted data remains only in memory.

**Correction**

Make durable admission and serialization one coherent budget. Suitable approaches include streaming UTF-8 serialization with an explicitly chosen encoder and exact byte accounting, or reserving worst-case escape/envelope overhead and evicting/rejecting before accepting a dirty mutation. Every state admitted by generated/pending/raw aggregate caps must be provably serializable under `MaxCacheSnapshotBytes`. Add a full-cap CJK test that requires a successful state commit and restart recovery.

### FR2-2 — Major — a timed-out admitted stage can publish after terminal teardown

**Source/symbols**

- `MuvluvLLMMod/PluginLifecycleGate.cs:257-285` — after the bounded wait expires, cleanup records failure and immediately runs teardown once.
- `MuvluvLLMMod/Plugin.cs:93-160` — admitted stages assign owners and call external boundaries without a post-call generation commit/rollback.
- `MuvluvLLMMod/Patch.cs:21-30` — `PatchAll` can publish hooks inside an already-admitted stage.
- `Plugin.cs:258-269` — cleanup calls `UnpatchSelf` once and clears the owner field.
- `PluginLifecycleGate.cs:227-235` — once cleanup is complete, later cleanup calls return the stored result without rerunning teardown.

The gate correctly rejects another stage or generation, but that does not revoke code already executing inside the timed-out stage. The existing test comment that an admitted stage “may finish late” checks only later gate admission; it does not prevent the stage's own side effect.

**Exact-source Docker fault injection**

Only the external Harmony stub was extended with a controllable `PatchAll` boundary. Production `Plugin`, `PluginLifecycleGate`, and `Patch` were the exact linked current source, including the default five-second quiescence deadline; the focused test completed in about five seconds.

1. Start real `Plugin.Load` and block fake `Harmony.PatchAll` before it publishes hooks.
2. Call real `Plugin.Unload` after the quiescence deadline.
3. Cleanup returns `false` within the deadline, calls `UnpatchSelf` once, and leaves zero hooks at that moment.
4. Release `PatchAll`; the admitted stage publishes all three hooks, then the next stage admission rejects load.
5. Call `Unload` again.

Observed/asserted result:

```text
first cleanup result:            false
UnpatchSelf calls after cleanup: 1
hooks immediately after cleanup: 0
hooks after late stage resumes:  3
second cleanup result:           false
final UnpatchSelf calls:         1
final installed hooks:           3
```

The generation remains failed and a replacement load is blocked, so the result is not falsely reported as success. Nevertheless cleanup is not terminal: resources can appear after their teardown step. Similar publication windows exist around cache load, component creation, and native registration; a native operation held under its coordinator lock can also make the subsequent remove block despite the outer quiescence deadline.

**Correction**

Give each stage a cancellation-aware commit/rollback protocol. Create resources locally, and after every external side effect atomically commit only if the generation is still current; otherwise immediately destroy/unregister/unpatch that local resource. Register a late-completion disposer before entering a potentially non-cooperative boundary, and retain it until completion. Cleanup completion must not suppress a required late rollback, and repeated cleanup should be able to retry a late-published teardown safely. Add the exact blocked-`PatchAll` test plus equivalent component/native-stage tests.

## Required audit proofs

### PF-1 / B-2 / N-2 worker and generation sequence

The worker-specific fix is sound:

- `MachineTranslatorLifecycle.cs:331-350` moves old worker A into a reference-identity `stoppingWorkers` set before awaiting stop.
- A stop timeout is retained and observed through `MachineTranslator.StopCompletion` (`MachineTranslator.cs:112-153`) and `ObserveStoppedWorker` (`MachineTranslatorLifecycle.cs:413-465`).
- `Fault` records a terminal exception and notifies the owning plugin generation (`:475-505`). Reload/enqueue reject that fault (`:138-185`).
- Shutdown snapshots active plus every stopping worker and propagates the transition/stop failure (`:252-314`).

The exact-source test performed A ignores cancellation -> reload 1 times out -> reload 2 rejected -> terminal cleanup. It asserted one factory call, one retained worker, failed shared cleanup, later flush/unpatch execution, failed generation, rejected new load, and removal of exactly A only after its late completion. The reviewer lifecycle-notification mutant made the outer gate remain `Loading` instead of `Failed` and was killed.

No worker reference was lost in this path and no inner lifecycle deadlock was found. The distinct outer-stage defect is FR2-2.

### PF-2 / M-4 cache candidates and dirty semantics

Source inspection and real files covered:

- old valid canonical plus newer valid `.tmp`: newer epoch selected, promoted, old canonical preserved as `.bak`, restart coherent;
- newer checksum-corrupt `.tmp`: rejected in favor of valid canonical and stale temp removed;
- matching-checksum negative epoch: rejected;
- canonical missing with valid backup: backup copied through temp and promoted;
- legacy epoch-zero state: accepted and marked dirty for journal migration;
- valid three-file legacy input with no state artifact: loaded safely; load alone remains a clean legacy state, and the first subsequent mutation commits both old and new content to authoritative state;
- legacy mirror write failure after authoritative commit: `Flush()` returned true, the custom writer was called four times, a second clean `Flush()` made no additional calls, and restart loaded authoritative data.

Selection at `TranslationCache.cs:928-952`, integrity at `:955-1012`, and promotion at `:1015-1045` are coherent. Epoch is monotonic for practical inputs, checksum covers sorted generated content plus ordered pending/raw and transaction metadata, and a mirror failure does not incorrectly leave a committed epoch dirty. PF-2 is closed; FR2-1 is a separate admission/representation failure before commit.

### PF-3 HTTP streaming

`OpenAiChatClient.cs:166-168` calls the actual overload with `HttpCompletionOption.ResponseHeadersRead`. `ReadContentBoundedAsync` rejects declared oversize content and stops an unknown-length stream when the accumulated bytes would exceed 131,072 (`:114-141`). The exact-source chunked 512 KiB producer stayed in the required 131,072..139,264-byte range. A reviewer mutant doubling only the streaming ceiling produced 270,336 bytes and failed the intended assertion; the configured default-buffering mutant produced all 524,288 bytes and also failed.

### PF-4 / M-3 reverse and collection accounting

Reverse accounting was traced through every transition:

- new unique mapping: translated plus source bytes (`TranslationCache.cs:669-677`);
- same-pair update/touch: recomputes full pair (`:648-655,709-725`);
- collision: removes source identity and retains only the translated ambiguity marker (`:658-665`);
- capacity admission: evicts complete recorded entries before insert/update (`:680-706`);
- removal/LRU eviction: subtracts the same stored cost and clears all reverse collections (`:747-766`).

The 4,000-`あ` source plus `译` retained exactly **12,003 UTF-8 bytes**, remained resolvable, became a 3-byte ambiguity marker on collision, and was evicted under pressure. Oversize admission was rejected.

All plugin-owned retained collections were also audited rather than accepting metric inequalities: generated, pending, raw, promoted pending, reverse/known/ambiguous, TMP provenance, retry state, scheduled/deferred/completed/cancellation queue maps, priority backlog/cancellations, failed progress, lifecycle transition cancellations, stopping workers, native delegate ownership, and debug dedupe all have count and/or payload boundaries appropriate to their stored text. FR2-1 is the aggregate durable-format mismatch that remains.

### PF-5 / B-1 final output and provenance

`TextTemplate.Fill` validates the completed replacement result at `TextTemplate.cs:82-85`. A null fill makes `TranslationResolver` remove the invalid generated template, leave the original source, and reobserve it. The direct 4,080-Chinese-plus-100-digit case returned null. The exact production fake-TMP route left the Japanese source on initial assignment and after F2-off; no over-budget plugin output entered provenance.

The one-shot token remains scoped only to `text.text = value`. External setters invalidate before resolution, unload/reload resets both ownership and provenance epochs, and the refresh path checks `tmpProvenance.IsCurrent(assignment)` immediately before writing. An added nested setter during an F2 refresh left the external Chinese assignment intact. Removing that final check reproduced the prohibited `外部中文 -> 確認する` overwrite and was killed by the added assertion.

## PF-6 exact-source and mutation audit

### Source boundary and limitations

`MuvluvLLMMod.IntegrationTests.csproj` disables default compile items and wildcard-links `../MuvluvLLMMod/*.cs`, removing only `CompilerServices.cs` (net8 already supplies those compatibility attributes). Every current production file is top-level, so the current implementation is linked rather than copied. The test itself rejects a local `Production` copy path and executes current linked behavior.

The stubs are still only boundary simulations:

- fake TMP always calls the Prefix directly, independent of a real native detour;
- fake Harmony discovers/records attributes but does not patch IL2CPP code;
- fake Unity uses managed wrappers and monotonic IDs;
- fake native event storage can report identity/removal failure, whereas the real removal API is void;
- no stub proves real input delivery, fonts/layout, native wrapper reuse, or complete UI coverage.

Those limitations are documented accurately. FR2-2 demonstrates why exact production source plus a stub is useful only when the relevant external interleaving is actually injected.

### Supplied 17-mutant gate

The script copies the read-only source to `/tmp`, gives every mutant its own copy, requires the 25-test baseline, requires an emitted `IntegrationTests.dll`, and requires the named focused test to appear in the failing output. Inspection and execution found each target meaningful and compile-valid:

| Mutant | Target / intended assertion | Observed focused result |
|---|---|---|
| `M5-B1-broad-tmp-token` | Broadly bypass external TMP invalidation; nested external text must not restore | Killed, 1 failed / 0 passed |
| `M5-B2-reopen-generation-gate` | Admit load over failed generation | Killed, 1/0; only expected CS0162 warning |
| `M5-M1-reconvert-quit-delegate` | Add a newly converted native delegate | Killed, callback count remained 1 after removal |
| `M5-N1-open-required-patch-verification` | Skip required verification failure | Killed, expected exception absent |
| `M5-M3-disable-budget-gate` | Accept every text budget | Killed, oversize input entered pending state |
| `M5-M4-remove-terminal-retry-guard` | Reduce final retries to one | Killed, transient failure not recovered |
| `M5-PF1-single-stopping-owner` | Reopen fault checks and overwrite tracked stopping ownership | Killed, lifecycle failed retention/quarantine assertion |
| `PF2-fixed-filename-priority` | Choose canonical by path before epoch | Killed, newer generated entry lost |
| `PF3-response-content-read` | Restore default whole-body buffering | Killed, producer emitted 524,288 bytes |
| `PF4-translated-only-reverse-bytes` | Omit source bytes | Killed, 3 actual vs 12,003 expected |
| `PF5-unbounded-filled-output` | Return unchecked final expansion | Killed, expanded Chinese displayed |
| `PF2-preserve-recovery-false` | Stop preserving prior canonical as backup | Killed, backup absent |
| `PF2-ignore-checksum` | Accept checksum mismatch | Killed, corrupt newer state won |
| `PF2-accept-invalid-epoch` | Accept negative epoch | Killed, invalid journal loaded |
| `PF1-unbounded-stage-wait` | Replace deadline with one hour | Killed by explicit bounded-deadline assertion |
| `PF1-unbounded-callback-wait` | Replace deadline with one hour | Killed by explicit bounded-deadline assertion |
| `M2-extra-nonrender-hook` | Add a fourth non-render route | Killed, applied patch count 4 vs 3 |

Result: **baseline 25/25; all 17/17 configured compile-valid mutants killed for the intended focused assertion.** The gate is correctly implemented, but its matrix did not cover FR2-1 or late publication by the already-admitted stage.

### Reviewer-added mutations

Four additional compile-valid mutants were made in independent Docker copies, at least one in each required area:

| Area / mutation | Focused assertion | Actual failure |
|---|---|---|
| Lifecycle: drop `terminalFailure` notification | reload timeout must fault the owning plugin gate | expected `Failed`, actual `Loading` |
| Cache: clear dirty state when `mutationVersion` matches even though authoritative write failed | transient state write must retry and commit | expected 5 writer calls, actual 1 |
| HTTP: double only the streaming body ceiling | producer must stop at 128 KiB plus one read | actual 270,336, outside 131,072..139,264 |
| Patch: remove final refresh assignment-generation check | nested external assignment during F2 refresh must win | expected `外部中文`, actual `確認する` |

All four built an integration DLL and were killed by their intended behavioral test. The Patch assertion was reviewer-added and passed against unmodified current source before mutation.

## README / standalone / artifact evidence

README is neutral and accurately says kana is necessary but not sufficient, output must pass its budget, F2 restores only validated provenance, and runtime UI coverage is not guaranteed. It contains no named upstream implementation, target, or hotkey assumption.

A fresh exact-source net6 build against the read-only game produced:

```text
artifacts/bin/MuvluvLLMMod/Release/net6.0/MuvluvLLMMod.dll
size:    183,296 bytes
SHA-256: 736912b333c1c49f311b42c039b5c5d73acdd1d37ac7ab47333975ab248d30b1
```

Direct metadata enumeration reported these assembly references only:

```text
0Harmony, BepInEx.Core, BepInEx.Unity.IL2CPP, GameUi,
Il2CppInterop.Runtime, Il2Cppmscorlib, Unity.InputSystem,
Unity.TextMeshPro, UnityEngine.CoreModule, and System.* assemblies
```

Relevant custom attributes were one `BepInPlugin` and exactly three `HarmonyPrefix` plus three `HarmonyPatch`; there was no `BepInDependency` and no Harmony Postfix. Decompiled target attributes were exactly:

```text
ScenarioController.Refresh()          Prefix
ScenarioController.Leave()            Prefix
TMP_Text.set_text                     Prefix
```

Raw metadata and full IL scans found all prohibited/standalone terms absent from the DLL:

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

Read-only interop inspection independently confirmed `ScenarioController.Refresh()` among its overloads, `UniTask Leave()`, and virtual `TMP_Text.text` with the expected string setter. The two scenario hooks only update local priority state; no data target is patched.

## Required Docker validation record

Environment:

```text
Docker Server: 29.6.2 (overlayfs, Docker Desktop)
Image: mcr.microsoft.com/dotnet/sdk:8.0
Digest: sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc
```

The repository was mounted read-only, copied to the container writable layer, and checked out at exact source HEAD `40c4149`.

### Solution tests

```text
dotnet test MuvluvLLMMod.sln -c Release
MuvluvLLMMod.IntegrationTests: 25 passed, 0 failed, 0 skipped
MuvluvLLMMod.Tests:            250 passed, 0 failed, 0 skipped
TOTAL:                         275 passed, 0 failed, 0 skipped
```

### Production build

The game was mounted as `/game,readonly`:

```text
dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game
Build succeeded: 0 warnings, 0 errors
Elapsed: 8.00 s
```

### Mutation gate

```text
bash scripts/mutation-gate.sh
Exact-source baseline: 25 passed, 0 failed, 0 skipped
Configured mutants:    17/17 compile-valid and killed
```

### Additional disposable validation

- 4/4 reviewer adversarial cache/Patch tests passed, including the deterministic unrepresentable-cache repro.
- 1/1 blocked-`PatchAll` late-publication repro passed against the production five-second deadline.
- 4/4 reviewer-added compile-valid mutants were killed by intended assertions.
- No host `dotnet`, package installation, or dependency reconfiguration was performed; ILSpy and all temporary test sources lived only in `docker run --rm` layers.

## Game-directory read-only statement

`C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl` was never launched and never written. Every command that referenced it used a Docker bind mount with `readonly`; source was also read-only and copied before compilation or mutation. Build products, restores, metadata tools, decompilation, fault injection, and mutants existed only in ephemeral container storage removed by `docker run --rm`.

Because source blockers remain, this review intentionally does **not** issue a game-only runtime handoff checklist. Fix FR2-1 and FR2-2 and rerun the full source gate before in-game canary work.
