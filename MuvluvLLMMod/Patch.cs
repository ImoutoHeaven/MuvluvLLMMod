using System.Diagnostics;
using Assets.Api.Client;
using Assets.GameUi.Scenario;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MuvluvLLMMod;

public static class Patch
{
    public static bool isPlayingScenario;

    private static readonly DebugTextLogPolicy debugTextLogPolicy = new();
    private static readonly TmpTranslationProvenance tmpProvenance = new();
    private static readonly TmpPluginWriteOwnership tmpWriteOwnership =
        new(tmpProvenance);
    private static int runtimeActive;
    private static int refreshScanCount;

    public static void Initialize(Harmony harmony)
    {
        // Clear the previous lifecycle before Harmony can publish any new setter hooks.
        ResetRuntimeState();
        harmony.PatchAll(typeof(Patch));
        // Hooks may be installed while the generation is still Loading. Keep their runtime
        // body inert until Plugin publishes Running and activates the complete owner.
        var verification = VerifyPatches(harmony.Id);
        if (!verification.Succeeded)
            throw new HarmonyPatchVerificationException(verification);
    }

    public static void Activate()
    {
        Volatile.Write(ref runtimeActive, 1);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.Refresh), new Type[] { })]
    public static void SetIsPlayingScenario() => isPlayingScenario = true;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.Leave))]
    public static void SetIsNotPlayingScenario() => isPlayingScenario = false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TMP_Text), "set_text")]
    public static void TranslateTmpSetter(TMP_Text __instance, ref string value)
    {
        var instanceId = __instance.GetInstanceID();
        if (tmpWriteOwnership.TryConsume(__instance, instanceId))
            return;

        // Every non-token setter is an external assignment. Invalidate before looking at the
        // incoming value: equal content is not evidence that the pooled assignment is continuous.
        var assignment = tmpProvenance.BeginExternalSetter(__instance, instanceId);
        if (!IsRuntimeActive(tmpProvenance.LifecycleEpoch))
            return;

        ResolveTmpValue(ref value, assignment, TmpTextAssignmentOrigin.ExternalSetter);
    }

    private static void ResolveTmpValue(
        ref string value,
        TmpTextAssignment assignment,
        TmpTextAssignmentOrigin origin)
    {
        var original = value ?? string.Empty;
        var containsKana = TextTemplate.IsTranslationCandidate(original);
        var enqueueObservation = default(EnqueueObservation);
        if (!string.IsNullOrEmpty(original))
        {
            if (Config.Translation.Value)
            {
                value = Translation.ResolveAny(original, enqueue: true);
                enqueueObservation = Translation.LastEnqueueObservation;
                if (!string.Equals(value, original, StringComparison.Ordinal))
                    tmpProvenance.Record(assignment, value, original);
            }
            else
            {
                Translation.ObserveForTranslation(original, isPlayingScenario);
                enqueueObservation = Translation.LastEnqueueObservation;
                // HARD RULE: external setter calls invalidate provenance before this resolution,
                // including byte-identical values. Only the guarded refresh path may restore, and
                // only after identity/generation and reverse-source validation succeed. Any
                // uncertainty leaves the incoming text unchanged.
                value = TmpRestoreDecision.Resolve(
                    tmpProvenance,
                    assignment,
                    original,
                    origin,
                    translatedValue => Core.Cache.TryGetSourceForTranslatedValue(translatedValue, out var source)
                        ? source
                        : null);
            }
        }

        LogSeenText(original, containsKana, enqueueObservation);
    }

    public static void RefreshAllTmpText()
    {
        var started = Stopwatch.GetTimestamp();
        var scanEpoch = tmpProvenance.LifecycleEpoch;
        if (!IsRuntimeActive(scanEpoch))
            return;

        var texts = UnityEngine.Object.FindObjectsByType<TMP_Text>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        foreach (var text in texts)
        {
            if (text == null || !IsRuntimeActive(scanEpoch))
                continue;

            var instanceId = text.GetInstanceID();
            var value = text.text ?? string.Empty;
            var assignment = tmpProvenance.BeginPluginRefresh(text, instanceId);
            ResolveTmpValue(ref value, assignment, TmpTextAssignmentOrigin.PluginRefresh);

            if (!string.Equals(text.text, value, StringComparison.Ordinal)
                && IsRuntimeActive(scanEpoch)
                && tmpProvenance.IsCurrent(assignment))
            {
                // This scope covers only the exact property setter. Resolver, queue, reverse
                // lookup, and logging callbacks run with no ownership guard, so nested setters
                // are classified as external and invalidate their own provenance first.
                using (tmpWriteOwnership.BeginPluginWrite(assignment))
                    text.text = value;
            }
        }

        var elapsedMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        if (Interlocked.Increment(ref refreshScanCount) % 20 == 0)
        {
            Logger.Info(
                $"[LLM] TMP refresh scan: objects={texts.Length}, "
                + $"elapsedMs={elapsedMilliseconds:F2}");
        }
    }

    private static void LogSeenText(
        string text,
        bool containsKana,
        EnqueueObservation enqueueObservation)
    {
        if (!Config.DebugLogSeenText.Value)
            return;

        var decision = debugTextLogPolicy.Observe(
            text,
            containsKana,
            enqueueObservation.DurablyPending,
            enqueueObservation.AcceptedByScheduler,
            DateTimeOffset.UtcNow);
        if (decision.SuppressedLines > 0)
        {
            Logger.Info($"[LLM] seen text: {decision.SuppressedLines} lines suppressed");
        }

        if (!decision.ShouldLog || decision.Text == null)
            return;

        var displayed = decision.Text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        displayed = DebugTextLogPolicy.Truncate(displayed);

        Logger.Info(
            $"[LLM] seen text=\"{displayed}\" kana={containsKana} "
            + $"durablyPending={enqueueObservation.DurablyPending} "
            + $"acceptedByScheduler={enqueueObservation.AcceptedByScheduler}");
    }

    internal static void Retire()
    {
        ResetRuntimeState();
    }

    private static void ResetRuntimeState()
    {
        Volatile.Write(ref runtimeActive, 0);
        tmpWriteOwnership.ResetForLifecycle();
        tmpProvenance.ResetForLifecycle();
        isPlayingScenario = false;
        Volatile.Write(ref refreshScanCount, 0);
    }

    private static bool IsRuntimeActive(long epoch) =>
        Volatile.Read(ref runtimeActive) != 0
        && !Plugin.IsCleaningUp
        && tmpProvenance.LifecycleEpoch == epoch;

    private static HarmonyPatchVerificationResult VerifyPatches(string harmonyId)
    {
        var targets = new[]
        {
            (
                Label: "TMPro.TMP_Text.set_text",
                Method: AccessTools.Method(typeof(TMP_Text), "set_text")),
            (
                Label: "Assets.GameUi.Scenario.ScenarioController.Refresh",
                Method: AccessTools.Method(
                    typeof(ScenarioController),
                    nameof(ScenarioController.Refresh),
                    new Type[] { })),
            (
                Label: "Assets.GameUi.Scenario.ScenarioController.Leave",
                Method: AccessTools.Method(typeof(ScenarioController), nameof(ScenarioController.Leave)))
        };
        var result = HarmonyPatchVerificationPolicy.Verify(
            targets.Select(target => new HarmonyPatchTargetStatus(
                target.Label,
                target.Method != null,
                target.Method != null
                    && Harmony.GetPatchInfo(target.Method)?.Owners.Contains(harmonyId) == true)));
        var targetList = string.Join(", ", result.Targets.Select(target => target.Label));
        if (result.Succeeded)
        {
            Logger.Info($"Harmony patches verified: owner={harmonyId}, targets=[{targetList}]");
        }
        else
        {
            var missing = string.Join(
                ", ",
                result.MissingRequiredTargets.Select(target => target.Label));
            Logger.Error(
                $"Harmony patch verification failed: owner={harmonyId}, missing=[{missing}], "
                + $"targets=[{targetList}]");
        }

        return result;
    }
}
