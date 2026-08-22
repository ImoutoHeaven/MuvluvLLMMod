#!/usr/bin/env bash
set -euo pipefail

# This script is intentionally a Docker-only gate. The caller mounts the repository read-only;
# every mutant is made in a separate throwaway copy under /tmp and is deleted on exit.
SOURCE_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
PROJECT="MuvluvLLMMod.IntegrationTests/MuvluvLLMMod.IntegrationTests.csproj"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/muvluv-m5.XXXXXX")
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

echo "M-5 baseline: exact-source integration suite"
baseline_output=$(run_tests "$ROOT" 2>&1) || {
    printf '%s\n' "$baseline_output"
    echo "baseline integration suite failed" >&2
    exit 1
}
printf '%s\n' "$baseline_output"
if ! grep -Eq 'Passed:[[:space:]]+20' <<<"$baseline_output" || ! grep -Eq 'Failed:[[:space:]]+0' <<<"$baseline_output"; then
    echo "baseline did not report the expected 20 integration tests" >&2
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
    if grep -q 'Build FAILED' <<<"$output"; then
        echo "$id was not compile-valid" >&2
        return 1
    fi
    if [[ $status -eq 0 ]]; then
        echo "$id SURVIVED (defect: integration coverage is insufficient)" >&2
        return 1
    fi
    echo "$id KILLED (expected non-zero test result)"
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

echo "M-5/PF-1/PF-2..PF-5 mutation gate: all 11 compile-valid configured mutants killed"
