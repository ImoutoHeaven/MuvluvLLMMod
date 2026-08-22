#!/usr/bin/env bash
set -euo pipefail

# This script is intentionally a Docker-only gate. The caller mounts the repository read-only;
# every mutant is made in a separate throwaway copy under /tmp and is deleted on exit.
SOURCE_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
PROJECT="MuvluvLLMMod.IntegrationTests/MuvluvLLMMod.IntegrationTests.csproj"
EXPECTED_BASELINE=42
WORK=$(mktemp -d "${TMPDIR:-/tmp}/muvluv-fr3.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
ROOT="$WORK/base"
mkdir -p "$ROOT"
cp -a "$SOURCE_ROOT/." "$ROOT/"

run_tests() {
    local directory=$1
    local filter=${2:-}
    local command=(dotnet test "$PROJECT" -c Release)
    if [[ -n "$filter" ]]; then
        command+=(--filter "FullyQualifiedName~$filter")
    fi
    (cd "$directory" && DOTNET_CLI_TELEMETRY_OPTOUT=1 "${command[@]}")
}

replace_once() {
    local file=$1
    local old=$2
    local new=$3
    OLD="$old" NEW="$new" perl -0pi -e '
        my $old = $ENV{OLD};
        my $new = $ENV{NEW};
        my $count = () = /\Q$old\E/sg;
        die "mutation did not find exactly one target (found $count)\n" unless $count == 1;
        s/\Q$old\E/$new/sg;
    ' "$file"
}

echo "FR3 baseline: exact-source integration suite"
baseline_output=$(run_tests "$ROOT" 2>&1) || {
    printf '%s\n' "$baseline_output"
    echo "baseline integration suite failed" >&2
    exit 1
}
printf '%s\n' "$baseline_output"
if ! grep -Eq "Passed:[[:space:]]+$EXPECTED_BASELINE" <<<"$baseline_output" || ! grep -Eq 'Failed:[[:space:]]+0' <<<"$baseline_output"; then
    echo "baseline did not report the expected $EXPECTED_BASELINE integration tests" >&2
    exit 1
fi

gate_mutant() {
    local id=$1
    local filter=$2
    local copy="$WORK/$id"
    mkdir -p "$copy"
    cp -a "$ROOT/." "$copy/"

    case "$id" in
        M5-B1-broad-tmp-token)
            replace_once "$copy/MuvluvLLMMod/Patch.cs" \
                'if (tmpWriteOwnership.TryConsume(__instance, instanceId))' \
                'if (tmpWriteOwnership.TryConsume(__instance, instanceId) || true)'
            ;;
        M5-B2-reopen-generation-gate)
            replace_once "$copy/MuvluvLLMMod/PluginLifecycleGate.cs" \
                '            if (state is not (PluginLifecycleState.NotLoaded or PluginLifecycleState.Stopped))' \
                '            if (false)'
            ;;
        M5-M1-reconvert-quit-delegate)
            replace_once "$copy/MuvluvLLMMod/Plugin.cs" \
                'handler => Application.add_quitting(handler)' \
                'handler => Application.add_quitting((Il2CppSystem.Action)ApplicationQuittingHandler)'
            ;;
        M5-N1-open-required-patch-verification)
            replace_once "$copy/MuvluvLLMMod/Patch.cs" \
                'if (!verification.Succeeded)' \
                'if (false && !verification.Succeeded)'
            ;;
        M5-M3-disable-budget-gate)
            replace_once "$copy/MuvluvLLMMod/TranslationBudget.cs" \
                'public static bool IsTextWithinBudget(string? text) => TryGetTextBytes(text, out _);' \
                'public static bool IsTextWithinBudget(string? text) => true;'
            ;;
        FR2-1-disable-durable-admission)
            replace_once "$copy/MuvluvLLMMod/TranslationCache.cs" \
                $'            return EstimateDurableSnapshotUtf8Bytes(\n                generatedCount,\n                generatedJsonBytes,\n                pendingCount,\n                pendingJsonBytes,\n                rawCount,\n                rawJsonBytes) <= TranslationBudget.MaxCacheSnapshotBytes;' \
                '            return true;'
            ;;
        FR2-2-remove-harmony-stage-commit)
            replace_once "$copy/MuvluvLLMMod/Plugin.cs" \
                $'                if (!harmonyResource.Commit(\n                        () =>\n                        {\n                            resources.Harmony = harmony;\n                            resources.PendingHarmony = null;\n                        }))\n                    throw CanceledGeneration(generation, "Harmony patches");' \
                '                // mutant: the stage commit was removed; Dispose rolls the hook back.'
            ;;
        FR2-2-remove-late-harmony-rollback)
            replace_once "$copy/MuvluvLLMMod/Plugin.cs" \
                '                            harmony.UnpatchSelf();' \
                '                            // mutant: late completion no longer unpatches.'
            ;;
        M5-M4-remove-terminal-retry-guard)
            replace_once "$copy/MuvluvLLMMod/TranslationCache.cs" \
                'for (var attempt = 0; attempt < TerminalFlushMaxAttempts; attempt++)' \
                'for (var attempt = 0; attempt < 1; attempt++)'
            ;;
        M5-PF1-single-stopping-owner)
            replace_once "$copy/MuvluvLLMMod/MachineTranslatorLifecycle.cs" \
                $'            if (shutdown\n                || terminalException != null\n                || !initialized\n                || transitionsInFlight >= TranslationBudget.MaxTransitions)' \
                $'            if (shutdown\n                || !initialized\n                || transitionsInFlight >= TranslationBudget.MaxTransitions)'
            replace_once "$copy/MuvluvLLMMod/MachineTranslatorLifecycle.cs" \
                $'                if (shutdown || terminalException != null || generation != version)\n                    return;\n                old = current;\n                current = null;\n                if (old != null)\n                    stoppingWorkers.Add(old);' \
                $'                if (shutdown || generation != version)\n                    return;\n                old = current;\n                current = null;\n                stoppingWorkers.Clear();\n                if (old != null)\n                    stoppingWorkers.Add(old);'
            replace_once "$copy/MuvluvLLMMod/MachineTranslatorLifecycle.cs" \
                '                if (shutdown || terminalException != null || generation != version || !enabled || factory == null)' \
                '                if (shutdown || generation != version || !enabled || factory == null)'
            replace_once "$copy/MuvluvLLMMod/MachineTranslatorLifecycle.cs" \
                '                if (!shutdown && terminalException == null && generation == version)' \
                '                if (!shutdown && generation == version)'
            ;;
        PF2-fixed-filename-priority)
            replace_once "$copy/MuvluvLLMMod/TranslationCache.cs" \
                $'        var selected = candidates\n            .OrderByDescending(candidate => candidate.Snapshot.Epoch)\n            .ThenBy(candidate => candidate.Priority)\n            .First();' \
                $'        var selected = candidates\n            .OrderBy(candidate => candidate.Priority)\n            .First();'
            ;;
        PF3-response-content-read)
            replace_once "$copy/MuvluvLLMMod/OpenAiChatClient.cs" \
                '            () => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),' \
                '            () => client.SendAsync(request, token),'
            ;;
        PF4-translated-only-reverse-bytes)
            replace_once "$copy/MuvluvLLMMod/TranslationCache.cs" \
                '        var entryBytes = translatedBytes + Utf8Bytes(source);' \
                '        var entryBytes = translatedBytes;'
            ;;
        PF5-unbounded-filled-output)
            replace_once "$copy/MuvluvLLMMod/TextTemplate.cs" \
                '        return TranslationBudget.IsTextWithinBudget(filled) ? filled : null;' \
                '        return filled;'
            ;;
        PF2-preserve-recovery-false)
            replace_once "$copy/MuvluvLLMMod/TranslationCache.cs" \
                '&& TryWriteAtomic(StatePath, stateJson, preserveRecovery: true);' \
                '&& TryWriteAtomic(StatePath, stateJson, preserveRecovery: false);'
            ;;
        PF2-ignore-checksum)
            replace_once "$copy/MuvluvLLMMod/TranslationCache.cs" \
                '            return string.Equals(expected, snapshot.Checksum, StringComparison.OrdinalIgnoreCase);' \
                '            return true;'
            ;;
        PF2-accept-invalid-epoch)
            replace_once "$copy/MuvluvLLMMod/TranslationCache.cs" \
                '            || snapshot.Epoch < 0)' \
                '            || false)'
            ;;
        PF1-unbounded-stage-wait)
            replace_once "$copy/MuvluvLLMMod/PluginLifecycleGate.cs" \
                '            if (!generation.WaitForStages(Remaining(deadline)))' \
                '            if (!generation.WaitForStages(TimeSpan.FromHours(1)))'
            ;;
        PF1-unbounded-callback-wait)
            replace_once "$copy/MuvluvLLMMod/PluginLifecycleGate.cs" \
                '            if (!generation.WaitForCallbacks(Remaining(deadline)))' \
                '            if (!generation.WaitForCallbacks(TimeSpan.FromHours(1)))'
            ;;
        M2-extra-nonrender-hook)
            replace_once "$copy/MuvluvLLMMod/Patch.cs" \
                $'[HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.Leave))]\n    public static void SetIsNotPlayingScenario()' \
                $'[HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.Leave))]\n    [HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.Refresh), new Type[] { })]\n    public static void SetIsNotPlayingScenario()'
            ;;
        FR3-1-remove-epoch-overflow-guard)
            replace_once "$copy/MuvluvLLMMod/TranslationCache.cs" \
                '        if (nextSnapshotEpoch == long.MaxValue)' \
                '        if (false)'
            ;;
        FR3-2-retain-generation-owner)
            replace_once "$copy/MuvluvLLMMod/PluginLifecycleGate.cs" \
                '            Owner = null;' \
                '            // mutant: retain the terminated generation owner.'
            ;;
        FR3-2-retain-config-entry-roots)
            replace_once "$copy/MuvluvLLMMod/Config.cs" \
                $'            if (oldConfig != null && oldHandler != null)\n                oldConfig.SettingChanged -= oldHandler;\n            config = null;\n            ClearStaticEntriesUnsafe();' \
                $'            if (oldConfig != null && oldHandler != null)\n                oldConfig.SettingChanged -= oldHandler;\n            config = null;\n            // mutant: retain terminated ConfigEntry roots.'
            ;;
        FR3-2-retain-resource-fields)
            replace_once "$copy/MuvluvLLMMod/Plugin.cs" \
                '        resources?.Detach();' \
                '        // mutant: retain terminated generation resource fields.'
            ;;
        *)
            echo "unknown mutant $id" >&2
            return 1
            ;;
    esac

    local output status
    set +e
    output=$(run_tests "$copy" "$filter" 2>&1)
    status=$?
    set -e
    printf '%s\n' "$output"
    if grep -q 'Build FAILED' <<<"$output" || ! grep -q -- '-> .*IntegrationTests.dll' <<<"$output"; then
        echo "$id was not compile-valid" >&2
        return 1
    fi
    if [[ $status -eq 0 ]]; then
        echo "$id SURVIVED (defect: integration coverage is insufficient)" >&2
        return 1
    fi
    if ! grep -Fq "$filter" <<<"$output"; then
        echo "$id failed outside its focused exact-source test; gate result is invalid" >&2
        return 1
    fi
    echo "$id KILLED (focused exact-source test failed as intended)"
}

gate_mutant M5-B1-broad-tmp-token \
    ProductionRenderIntegrationTests.Real_prefix_and_fake_setter_fail_closed_for_nested_external_and_stale_lifecycle_writes
gate_mutant M5-B2-reopen-generation-gate \
    ProductionCoordinationIntegrationTests.Failed_cleanup_quarantines_the_generation_and_blocks_reopen
gate_mutant M5-M1-reconvert-quit-delegate \
    ProductionCoordinationIntegrationTests.Real_plugin_registers_and_removes_the_same_native_quit_delegate
gate_mutant M5-N1-open-required-patch-verification \
    ProductionRenderIntegrationTests.Missing_required_patch_verification_rolls_back_before_hotkey_persistence_or_worker_start
gate_mutant M5-M3-disable-budget-gate \
    ProductionBudgetAndShutdownIntegrationTests.Oversized_render_input_is_rejected_fail_closed_and_not_retained
gate_mutant FR2-1-disable-durable-admission \
    ProductionBudgetAndShutdownIntegrationTests.Full_cap_cjk_pairs_are_rejected_before_an_unwritable_authoritative_state
gate_mutant FR2-2-remove-harmony-stage-commit \
    ProductionSourceLinkIntegrationTests.Exact_source_harness_applies_only_final_tmp_and_scenario_priority_patches
gate_mutant FR2-2-remove-late-harmony-rollback \
    ZzzProductionLateStageIntegrationTests.Real_blocked_patch_all_is_unpatched_after_timeout_and_late_completion
gate_mutant M5-M4-remove-terminal-retry-guard \
    ProductionBudgetAndShutdownIntegrationTests.Terminal_flush_recovers_from_one_transient_write_failure
gate_mutant M5-PF1-single-stopping-owner \
    ProductionBudgetAndShutdownIntegrationTests.Timed_out_reload_is_terminal_and_retains_the_old_worker_for_cleanup
gate_mutant PF2-fixed-filename-priority \
    ProductionBudgetAndShutdownIntegrationTests.Durable_cache_recovery_chooses_the_newest_valid_journal_epoch
gate_mutant PF3-response-content-read \
    ProductionBudgetAndShutdownIntegrationTests.Response_body_budget_stops_unknown_length_producer_before_full_buffer
gate_mutant PF4-translated-only-reverse-bytes \
    ProductionBudgetAndShutdownIntegrationTests.Reverse_index_retains_source_and_translation_bytes_for_admission
gate_mutant PF5-unbounded-filled-output \
    ProductionRenderIntegrationTests.Over_budget_generated_expansion_stays_source_even_when_f2_is_off
gate_mutant PF2-preserve-recovery-false \
    ProductionBudgetAndShutdownIntegrationTests.Authoritative_write_preserves_the_previous_epoch_for_backup_recovery
gate_mutant PF2-ignore-checksum \
    ProductionBudgetAndShutdownIntegrationTests.Newer_invalid_checksum_is_rejected_in_favor_of_a_valid_canonical_epoch
gate_mutant PF2-accept-invalid-epoch \
    ProductionBudgetAndShutdownIntegrationTests.Negative_epoch_journal_is_rejected_even_when_its_checksum_matches_its_payload
gate_mutant PF1-unbounded-stage-wait \
    ProductionCoordinationIntegrationTests.Cleanup_stage_wait_deadline_quarantines_and_continues_later_teardown
gate_mutant PF1-unbounded-callback-wait \
    ProductionCoordinationIntegrationTests.Cleanup_callback_wait_deadline_has_shared_failure_and_late_callback_is_inert
gate_mutant M2-extra-nonrender-hook \
    ProductionSourceLinkIntegrationTests.Exact_source_harness_applies_only_final_tmp_and_scenario_priority_patches
gate_mutant FR3-1-remove-epoch-overflow-guard \
    ProductionBudgetAndShutdownIntegrationTests.Maximum_epoch_is_terminal_and_flush_never_claims_an_invalid_success
gate_mutant FR3-2-retain-generation-owner \
    ZzzProductionLateStageIntegrationTests.Real_successful_cleanup_detaches_static_generation_owner_and_config_roots
gate_mutant FR3-2-retain-config-entry-roots \
    ZzzProductionLateStageIntegrationTests.Real_successful_cleanup_detaches_static_generation_owner_and_config_roots
gate_mutant FR3-2-retain-resource-fields \
    ZzzProductionLateStageIntegrationTests.Real_successful_cleanup_detaches_static_generation_owner_and_config_roots

echo "FR3 mutation gate: all 24 compile-valid configured mutants killed"
