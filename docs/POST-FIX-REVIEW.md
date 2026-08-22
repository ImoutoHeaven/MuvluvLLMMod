# Post-Fix Release-Gate Review

## VERDICT: DO NOT SHIP

The wrong-source TMP corruption path, render-only patch boundary, native delegate identity, and required-Harmony rollback are materially improved. However, the reviewed production HEAD still has independently reproduced source-level release defects:

1. a timed-out reload worker can be forgotten by the next reload, after which terminal cleanup reports success and permits a new plugin generation over the leaked worker;
2. a valid newer cache journal is ignored whenever an older valid canonical snapshot remains, losing the final dirty epoch on restart;
3. the HTTP response ceiling is applied only after `HttpClient` has buffered the complete response;
4. reverse-index byte accounting omits retained source strings; and
5. placeholder filling can create an over-budget translation that the plugin displays but cannot track or restore when F2 is turned off.

The exact-source test target and six-mutant gate are useful, but two additional release-critical compile-valid mutants survived all 254 tests. These defects cannot be made safe by an in-game runtime gate, so this HEAD is not releasable.

## Baseline and review scope

- **Reviewed production HEAD:** `2e76a01af378b587fc5206fdd177c0e52bdc6fea` (`docs: record quit callback integration coverage`)
- **Initial review parent:** `2d683187514fa598e41a3635256fce6ebeb00135`
- **Fix range inspected:** every commit and diff in `2d68318..2e76a01`
- **Review started:** `2026-08-22T20:57:40+08:00`
- **Review completed:** `2026-08-22T21:34:24+08:00`
- **Initial worktree:** clean (`git status --short --branch` returned only `## main`)
- **Review bookkeeping commit:** `bdd75f1493b09b10b7488b2c31b29ec2b062153d` (`docs/POST-FIX-REVIEW.md` only)
- **Complete-report commit parent:** `bdd75f1493b09b10b7488b2c31b29ec2b062153d`
- **Final report commit:** `HEAD`, the commit containing this complete report. Its exact SHA is emitted in the final reviewer response; a Git commit cannot embed its own SHA in its content.
- Apart from this report, the reviewer modified no production, test, script, README, or other documentation file. All adversarial tests and mutations were made only in ephemeral Docker copies.

The review read `docs/FINAL-REVIEW.md` and `docs/FIXER-CONTEXT.md`, inspected all current production sources, project files, README, integration stubs/tests, mutation script, relevant fix diffs, the exact rebuilt DLL, and the read-only game interop API surface. Claims in commits and prior reports were treated only as hypotheses.

## Required Docker validation

Docker Server was `29.6.2`. Image `mcr.microsoft.com/dotnet/sdk:8.0` had local digest:

```text
sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc
```

The repository was mounted read-only and copied to a writable container layer. The exact production checkout was selected inside that layer where artifact identity mattered.

### Solution test

```text
dotnet test MuvluvLLMMod.sln -c Release
```

**PASS**:

- `MuvluvLLMMod.IntegrationTests`: 13 passed, 0 failed, 0 skipped
- `MuvluvLLMMod.Tests`: 241 passed, 0 failed, 0 skipped
- Total: **254 passed, 0 failed, 0 skipped**

### Exact production plugin build

The container checked out `2e76a01af378b587fc5206fdd177c0e52bdc6fea`, mounted the game as `/game,readonly`, and ran:

```text
dotnet build MuvluvLLMMod/MuvluvLLMMod.csproj -c Release -p:GameDir=/game
```

**PASS:** build succeeded, 0 warnings, 0 errors (7.34 s).

Exact reviewed artifact:

```text
artifacts/bin/MuvluvLLMMod/Release/net6.0/MuvluvLLMMod.dll
size:    170496 bytes
SHA-256: 3efc03340c82186174a9a790bfc6f751b458c83bb3ac6fa7f895f60ac123a551
```

### Required mutation gate

```text
bash scripts/mutation-gate.sh
```

**PASS as implemented:** baseline 13/13; all six compile-valid focused mutants were killed.

| Mutant | Observed focused result |
|---|---|
| `M5-B1-broad-tmp-token` | 1 failed / 0 passed; nested external Chinese restored incorrectly |
| `M5-B2-reopen-generation-gate` | 1 failed / 0 passed; quarantine reopened (plus expected unreachable-code warning) |
| `M5-M1-reconvert-quit-delegate` | 1 failed / 0 passed; native callback remained registered |
| `M5-N1-open-required-patch-verification` | 1 failed / 0 passed; expected verification exception was absent |
| `M5-M3-disable-budget-gate` | 1 failed / 0 passed; oversized render entered pending state |
| `M5-M4-remove-terminal-retry-guard` | 1 failed / 0 passed; transient terminal write did not recover |

The script itself was audited before execution. `SOURCE_ROOT` was the read-only mount, it copied to an `mktemp` directory, and every mutant was made in another temporary copy. None touched the checkout.

A green baseline and the six killed mutants do **not** resolve the findings below.

## Artifact and static invariant evidence

### Standalone contract

The exact DLL references only:

```text
0Harmony, BepInEx.Core, BepInEx.Unity.IL2CPP, GameUi,
Il2CppInterop.Runtime, Il2Cppmscorlib, Unity.InputSystem,
Unity.TextMeshPro, UnityEngine.CoreModule, and System.* assemblies
```

There is no upstream-mod assembly reference. Decompiled custom attributes contain only:

```text
[BepInPlugin("muvluv.llmmod", "MuvluvLLMMod", "1.0.0")]
```

There is no `[BepInDependency]`.

Decompiled artifact scans found all of these absent:

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

No production path reads another plugin's data or state.

### Patch surface

The exact artifact has three Harmony patches, all Prefixes:

```text
ScenarioController.Refresh()
ScenarioController.Leave()
TMP_Text.set_text
```

`TMP_Text.set_text` remains the required **Prefix**. There is no skill-description or data-layer patch. The two scenario hooks only update local scenario-priority state and are within the approved exception.

Read-only game interop inspection confirmed this install exposes:

- `ScenarioController.Refresh()` (alongside an unrelated three-argument overload);
- private `UniTask ScenarioController.Leave()`; and
- virtual `TMP_Text.text` with the expected string setter.

### Exact-source integration boundary

`MuvluvLLMMod.IntegrationTests.csproj:17-22` links `../MuvluvLLMMod/*.cs` directly and removes only `CompilerServices.cs`, whose compatibility attributes are supplied by net8. All current production files are top-level and therefore included. It is not a copied implementation.

The stubs do **not** prove native behavior. In particular:

- fake TMP calls the production Prefix directly even when fake Harmony is unpatched;
- fake Unity uses monotonic IDs and one managed wrapper per object;
- fake native event removal can confirm reference identity, whereas real `Application.remove_quitting` is void;
- fake Harmony records ownership but does not execute an IL2CPP detour; and
- most lifecycle integration tests drive exact policy classes directly rather than forcing an actual `Plugin.Load`/`Cleanup` interleaving.

Those are legitimate test boundaries, but they must not be described as game/runtime proof.

## Original finding disposition

| Initial ID | Status | One-line evidence |
|---|---|---|
| **B-1** | **PARTIAL** | Exact one-shot TMP ownership and lifecycle retirement close the wrong-source nested/unpatch paths, but a valid generated template can expand over budget, be displayed without provenance, and remain Chinese after F2-off. |
| **B-2** | **PARTIAL** | Generation staging, leases, shared cleanup, and duplicate-initialize rejection work, but a timed-out reload worker is discarded by a second reload and is absent from later cleanup/quarantine. |
| **M-1** | **CLOSED** | `NativeDelegateCoordinator<T>` serializes conversion/add/remove/retention and preserves exact delegate identity; artifact and forced interleaving tests agree. Real native removal remains a game-only confirmation. |
| **M-2** | **CLOSED** | Production and artifact contain no skill `GetDescription` route; skill text reaches only final `TMP_Text.set_text`. |
| **M-3** | **PARTIAL** | Most collections now have count/payload caps, but reverse bytes omit sources, HTTP buffers before its cap, and filled output can exceed the text budget. |
| **M-4** | **PARTIAL** | Paced terminal retries and one authoritative state file exist, but recovery chooses an older canonical state before a valid newer `.tmp` journal, losing that epoch. |
| **M-5** | **PARTIAL** | Current source is linked exactly and six mutants are killed, but two additional critical mutants survived 254/254 and several key assertions are absent/vacuous. |
| **M-6** | **PARTIAL** | Named-upstream assumptions were removed, but README's absolute kana-only eligibility and F2 display wording omit enforced budgets and the reproduced untracked expanded-output case. |
| **N-1** | **CLOSED** | All three required targets need discovery plus ownership; failure throws `HarmonyPatchVerificationException`, then load rollback unpatches before runtime resources start. |
| **N-2** | **PARTIAL** | Direct terminal worker shutdown is bounded and later flush/unpatch steps run, but timeout ownership during reload can be forgotten and pre-teardown stage/callback waits remain unbounded. |

## B-1 source trace and adversarial disposition

### What is now correct

`Patch.cs:48-60` asks `TmpPluginWriteOwnership` to consume a token first. Every non-token setter calls `BeginExternalSetter` **before** runtime checks, lookup, resolver, logging, or restoration. Therefore byte-identical Chinese and synchronous nested setters invalidate old provenance even while loading/stopping.

`Patch.cs:120-132` creates a plugin-refresh assignment and places the token only around the exact `text.text = value` call. The token validates managed identity, native instance ID, assignment generation, provenance epoch, ownership epoch, and one-shot consumption (`TmpPluginWriteOwnership.cs:76-98`). Mismatch is treated as external rather than searching another token.

`Patch.Retire` resets ownership and provenance before `Harmony.UnpatchSelf`; initialization resets both before `PatchAll`. Failed current-text or reverse-source validation permanently clears the record (`TmpTranslationProvenance.cs:236-289`). The valid ordinary F2 path restored `確認する -> 确定 -> 確認する` in the exact-source harness.

The exact-source integration test exercised synchronous nested same-TMP and other-TMP setters with byte-identical `确定`; neither was restored to Japanese. It also exercised assignment across unload/reload. Unit tests cover target/ID/generation/epoch mismatch and same-instance/same-value pooling.

Read-only Il2CppInterop 1.5.3 inspection showed `Il2CppReferenceArray<T>.WrapElement` calls `Il2CppObjectPool.Get<T>`. The pool is keyed by native pointer and returns its live cached wrapper. Provenance strongly retains the wrapper, which supports ordinary wrapper stability. Cache-disabled wrapping or native-pointer reuse without an observed setter still requires the game handoff below; uncertainty on a token mismatch is fail-closed.

Display-off still calls `Translation.ObserveForTranslation`, and machine enablement is based on `Config.LlmEnable`, not the F2 display flag. Ordinary background production therefore continues while display is off.

### Why B-1 is still only partial

`TextTemplate.Fill` validates each input but does not validate the final expanded result (`TextTemplate.cs:65-81`). A throwaway exact-source integration test stored:

```text
source:              "する" + 100 numeric characters
normalized template: "する{0}"
translation template: 4080 Chinese characters + "{0}"
```

Both stored values satisfy the individual 4096-code-unit/16-KiB limits. Filling produces an output over 4096 code units. The resolver returns it, but reverse/provenance recording rejects it. Observed result:

```text
plugin translated the TMP value: yes
output over text budget:          yes
F2-off expected:                  original Japanese
F2-off actual:                    expanded Chinese translation
```

This does not recreate the prohibited wrong-source Japanese corruption, but it disproves complete restoration of actual plugin-owned content and the display-toggle promise.

## New source-level findings

### PF-1 — BLOCKER — a timed-out reload worker is forgotten and cleanup falsely succeeds

**Locations:**

- `MuvluvLLMMod/MachineTranslatorLifecycle.cs:287-305` — transition moves `current` to the single `stopping` slot.
- `MachineTranslatorLifecycle.cs:298-335` — stop timeout is caught and diagnosed; the transition task completes successfully while `stopping` retains the worker.
- `MachineTranslatorLifecycle.cs:293-295` — the next reload assigns `stopping = old`; when `current` is null, this overwrites the timed-out worker with null.
- `MachineTranslatorLifecycle.cs:219-270` — shutdown can capture only `current` and the now-cleared single `stopping` reference.
- `Plugin.cs:357-380` — configuration reload ignores the lifecycle result/failure state and permits another reload.

**Concrete Docker reproduction:** an added throwaway test compiled the exact production lifecycle/cache/worker classes. Worker A ignored cancellation. Reload 1 timed out stopping A. Reload 2 started B and overwrote the `stopping` owner. Terminal `Shutdown()` then returned successfully rather than faulting/quarantining A:

```text
Expected InvalidOperationException from terminal cleanup
Actual: no exception was thrown
```

The focused adversarial test failed exactly on that assertion in 75 ms.

**Risk:** a canceled but noncooperative HTTP/translation worker remains alive and unowned. Its token prevents a normal late cache publication, which limits data corruption, but cleanup can claim success, reopen plugin load, and accumulate leaked tasks/transports across reload cycles. This is the ownership/failure-quarantine class that made the initial B-2 a blocker.

**Suggested fix:** make a transition stop timeout terminal for that lifecycle/generation; reject subsequent reloads, retain and observe every stopping worker (not one overwriteable slot), propagate failure to outer generation cleanup, and quarantine before a new plugin generation can load. Add the exact timeout -> second reload -> terminal cleanup test.

### PF-2 — MAJOR — valid newer cache journal loses to older canonical snapshot

**Locations:**

- `MuvluvLLMMod/TranslationCache.cs:471-480` — authoritative state is written first.
- `TranslationCache.cs:1054-1097` — failed canonical move intentionally leaves `cache.state.v1.json.tmp` as recovery journal.
- `TranslationCache.cs:759-790` — loader probes `StatePath`, then `.tmp`, then `.bak`, returning the first valid snapshot.

**Concrete Docker reproduction:** a valid old canonical state and a valid newer temporary state were written in the exact production format. `Load()` returned the old generated entry and did not load the new one:

```text
old canonical: valid
new .tmp:      valid
TryGetGenerated(new): false
```

The adversarial assertion that the newer journal win failed.

**Risk:** the common failure mode that motivates the journal—temporary write succeeds, replacement of a locked canonical file fails—leaves exactly this pair. On restart the last dirty epoch is silently ignored despite being recoverable. Because the canonical snapshot is valid, the cache is not marked dirty and the journal can remain ignored indefinitely.

**Suggested fix:** include a monotonic snapshot epoch/transaction ID and integrity check, select the newest valid canonical/temp/backup epoch, promote recovered temp atomically, and test old-valid-canonical + new-valid-temp + backup combinations. A valid temp produced by this writer should not be lower priority merely because the previous canonical is parseable.

### PF-3 — MAJOR — response limit is after full `HttpClient` buffering

**Location:** `MuvluvLLMMod/OpenAiChatClient.cs:166` calls `client.SendAsync(request, token)`, whose default completion mode is `ResponseContentRead`. The bounded stream reader at lines 114-142 runs only afterward.

**Concrete Docker reproduction:** a chunked `HttpContent` with unknown length counted bytes produced. The configured production ceiling is 131072 bytes; allowing one read buffer gives 139264. Before the bounded reader rejected the body, the handler had already produced and `HttpClient` had buffered all 524288 bytes:

```text
Expected produced bytes: 0..139264
Actual produced bytes:   524288
```

**Risk:** a faulty or hostile configured endpoint can force allocation up to `HttpClient`'s much larger buffering limit before the plugin's 128-KiB check. The advertised response memory bound is therefore not real and can cause large transient allocation/OOM.

**Suggested fix:** send with `HttpCompletionOption.ResponseHeadersRead`, retain the current bounded streaming loop, and add an unknown-`Content-Length` content test that proves the producer is stopped near the ceiling rather than merely returning null after buffering.

### PF-4 — MAJOR — reverse index counts only translated keys, not retained sources

**Locations:**

- `MuvluvLLMMod/TranslationCache.cs:600-606` computes full pair bytes, stores the source, calls `SetReverseEntryBytesUnsafe`, then calls `TouchReverseIndexUnsafe`.
- `TranslationCache.cs:644-652` makes `SetReverseEntryBytesUnsafe` a no-op when the node does not yet exist.
- `TranslationCache.cs:626-642` then creates the node using only `Utf8Bytes(translatedValue)`.

**Concrete Docker reproduction:** one 4000-character Japanese source (12000 UTF-8 bytes) mapped to `译` (3 bytes):

```text
ReverseCount:             1
Expected retained bytes: 12003
Reported/limited bytes:  3
```

**Risk:** with short unique translations, up to 4096 individually valid sources can be retained while the nominal 1-MiB reverse budget counts only tiny keys. Payload can reach roughly 64 MiB plus object overhead. Pressure tests assert only `0 <= metric <= max`, so the undercount passes.

**Suggested fix:** create/touch the node with the full pair cost (or set its bytes after node creation), account updates and ambiguity transitions atomically, and assert exact/lower-bound retained byte totals in tests.

### PF-5 — MAJOR — final placeholder expansion bypasses the text budget and F2 provenance

**Locations:**

- `MuvluvLLMMod/TextTemplate.cs:65-81` — no final-result budget validation.
- `MuvluvLLMMod/TranslationResolver.cs:83-95` — returns a non-null filled result.
- `MuvluvLLMMod/Patch.cs:75-79` and `TmpTranslationProvenance.cs:195-213` — value is displayed even when provenance rejects the over-budget pair.

**Reproduction:** the direct test expected `TextTemplate.Fill` to return null for an expanded 4180-code-unit result; it returned the large string. The exact production render test then reproduced the F2 failure described under B-1.

**Risk:** bounded generated/cache inputs can still produce an output outside the stated budget. That output bypasses reverse/provenance retention and becomes a plugin-owned translation that F2 cannot turn off.

**Suggested fix:** validate the final filled string before returning it. On failure, leave source text unchanged and remove/quarantine the invalid generated template as the resolver already does for a null fill. Add direct and exact render/F2 tests.

### PF-6 — MAJOR assurance gap — critical mutants still survive the complete suite

Three additional compile-valid mutations were run in independent Docker copies:

| Additional mutant | Tests | Production net6 build |
|---|---|---|
| Disable both response-body ceiling branches | **SURVIVED: 254/254 passed** | 0 warnings, 0 errors |
| Change authoritative state write to `preserveRecovery: false` | **SURVIVED: 254/254 passed** | 0 warnings, 0 errors |
| Change initial reverse-byte increment to zero | Killed: unit suite 239 passed / 2 failed because later eviction drove the metric negative; integration 13/13 still passed | compile-valid in exact-source targets |

The third result does not validate correct source-byte accounting; it catches only a negative counter after eviction. Current production already undercounts the source while staying nonnegative.

Other observed gaps:

- `OpenAiBudgetTests.Oversized_response_body_is_rejected_before_unbounded_string_read` pre-allocates `StringContent` and never proves `ResponseHeadersRead`/early producer termination.
- Cache recovery tests cover missing canonical and corrupt canonical, not valid old canonical plus valid new temp.
- No test covers reload timeout followed by another accepted reload.
- Several budget assertions accept zero as success, allowing no-op accounting.
- `MachineTranslatorLifecycleTests.Throwing_lifecycle_diagnostic_does_not_fault_reload_transition` calls `Reload` before `Initialize`; reload is rejected, so neither factory nor diagnostic is exercised.
- Integration stubs call the Prefix directly independent of Harmony ownership, so those tests cannot prove native patch activation/unpatch behavior.

**Suggested fix:** add the adversarial cases above to the exact-source suite and mandatory mutation gate; assert the mutated branch is reached and the failure reason is the intended invariant, not any nonzero test exit.

### PF-7 — MINOR — README still overstates eligibility and F2 coverage

**Locations:** `README.md:25-27` says production eligibility is “if and only if” kana; `README.md:82-91` says F2 toggles this plugin's displayed translations.

The code also requires input/template budgets, and PF-5 produces an observed-TMP plugin translation that remains displayed after F2-off. The upstream-neutral wording and runtime caveats are otherwise substantially corrected.

**Suggested fix:** qualify eligibility with accepted text/template budgets and explicitly state that only successfully provenance-recorded output is restorable—after PF-5 is fixed, document the tested final behavior rather than the current defect.

## Lifecycle and shutdown audit notes

The following behavior was verified and should be preserved:

- load stages are admitted under a generation state; cleanup enters `Stopping`, cancels, and waits for admitted stages;
- later load stages cannot enter after cleanup starts;
- concurrent cleanup callers receive the same completion/result;
- failed teardown leaves state `Failed` and blocks `TryBeginLoad`;
- configuration subscribes only after `Running`, captures a generation lease, and stale callbacks cannot read a replacement static owner;
- duplicate `MachineTranslatorLifecycle.Initialize` is rejected without replacing the current worker;
- configuration shutdown precedes cache freeze and machine shutdown;
- direct noncooperative terminal workers time out, reject late cache publication, and do not prevent later flush/unpatch cleanup steps; and
- persistence cancellation is awaited up to five seconds before terminal flush.

Residual source risk remains in `PluginLifecycleGate.Cleanup`: `WaitForStages()` and `WaitForCallbacks()` are unbounded and occur before teardown. A permanently blocked load boundary or synchronous logger/config callback can still prevent flush/unpatch. Filesystem operations after `writerGate` acquisition are also synchronous and cannot be interrupted by the nominal flush deadline. These are separate from the reproduced PF-1 and should be covered by a quarantine/continuation strategy rather than pretending every external call is cancellable.

## Cache compatibility and correctness audit notes

Positive evidence:

- legacy generated/pending/raw files are read when no state artifact exists;
- an authoritative state snapshot prevents mixed generated/pending/raw epochs;
- malformed/oversized files are bounded before JSON materialization;
- generated, pending, raw, provenance, queues, retries, backlog, cancellation, and progress sets have finite count and payload admission policies;
- terminal flush serializes with the normal writer, makes up to five paced attempts, and faults cleanup on persistent failure; and
- frozen cache generation checks reject late worker publication.

Limitations/defects:

- PF-2 loses a valid newer temp behind old canonical;
- the state has only schema `Version = 1`, not a snapshot epoch, checksum, or commit marker;
- a corrupt state artifact suppresses legacy fallback entirely, conservatively avoiding mixed epochs but potentially discarding otherwise usable migration data;
- four complete JSON strings (state plus three legacy mirrors) are materialized for a flush, though each is individually capped; and
- the writer uses atomic replacement semantics but no fsync guarantee. No stronger power-loss durability claim should be made without platform-specific testing.

## Mutation-gate audit conclusion

The supplied gate is reproducible, runs from a read-only checkout, and each of its six mutants failed for the intended behavioral assertion. It closes the specific original three-mutation criticism.

It does not establish general wiring resistance. Two additional release-critical mutants survived both test projects and built as the production net6 DLL. Therefore M-5 remains partial and the green 254-test baseline cannot support shipment.

## Game-only handoff checklist

These items require the real game/native runtime **after the source defects above are fixed**. They are not waivers for PF-1 through PF-6.

1. Confirm startup logs exactly the three required owned Harmony targets and that omission/signature drift throws and fully rolls back.
2. Verify real Harmony Prefix ordering with another render Prefix and confirm every non-token synchronous nested setter invalidates provenance.
3. Exercise ordinary, pooled same-instance/same-value, inactive-object, and byte-identical external-Chinese F2 cases; no non-owned Chinese may become Japanese.
4. Exercise unload/unpatch assignment/reload and native pointer/Unity instance-ID reuse. Confirm a stale wrapper/entry never restores after destruction/reuse.
5. Confirm actual Il2CppObjectPool wrapper identity and behavior if runtime caching is disabled or a native pointer is recycled.
6. Verify skill descriptions and every relevant UI route reach final `TMP_Text.set_text`; no pre-TMP skill translation should appear.
7. Verify `ScenarioController.Refresh()`/`Leave()` accurately bracket scenario priority without touching scenario data.
8. Confirm IL2CPP `Application.quitting` receives exactly one retained callback, removes the same native delegate on explicit unload/rollback, and does not duplicate across reloads.
9. Stress rapid config changes, cleanup during load, quit during HTTP, a timed-out reload, and a second reload; leaked workers/callbacks must quarantine the generation.
10. Force real cache file locks/crashes at temp write, canonical replacement, and mirror writes; verify newest coherent state recovery and legacy migration.
11. Feed a chunked oversized endpoint response and monitor process memory; reading must stop near 128 KiB after `ResponseHeadersRead` is implemented.
12. Verify F2 input delivery, inactive-inclusive scan cost, Chinese font fallback/glyphs, wrapping, clipping, and layout.
13. Verify cache-directory permissions and endpoint cancellation/model response quality on the target install.

Unavoidable game-only uncertainties at this review point are native detour behavior/order, real delegate event semantics, wrapper/pointer reuse, font/layout, UI coverage, Input System delivery, and scenario bracketing. The artifact is statically clean, but those uncertainties cannot cure the reproduced source defects.

## Exact read-only game statement

`C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl` was never launched and was never written. Every Docker command that referenced it used:

```text
--mount type=bind,src=C:/Users/Eden/Muv-Luv/muv_luv_girlsgarden_cl,dst=/game,readonly
```

All restores, builds, decompilation, added adversarial tests, and mutations occurred in ephemeral container writable layers destroyed by `docker run --rm`. No host dependency was installed, and no host `dotnet` command was run.
