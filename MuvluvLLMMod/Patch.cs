using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Assets.Api.Client;
using Assets.GameUi.Scenario;
using Assets.GameUi.Service;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;

namespace MuvluvLLMMod;

public static class Patch
{
    public static bool isPlayingScenario;
    private static readonly SceneTranslationCoordinator sceneCoordinator = new();

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

        // Pre-install gate: resolve every declared target before Harmony publishes anything, so a
        // game update that moves a seam aborts the generation instead of installing a partial patch
        // set whose hooks silently no-op.
        var preflight = Preflight();
        if (!preflight.Ok)
        {
            throw new HarmonyPatchPreflightException(preflight.Failures);
        }

        harmony.PatchAll(typeof(Patch));
        // Hooks may be installed while the generation is still Loading. Keep their runtime
        // body inert until Plugin publishes Running and activates the complete owner.
        var verification = VerifyPatches(harmony.Id, preflight.Targets);
        if (!verification.Succeeded)
            throw new HarmonyPatchVerificationException(verification);
    }

    /// <summary>
    /// Resolves every declared Harmony target against the loaded game assemblies. The loader calls
    /// this before any configuration side effect, so a moved seam aborts the generation with all
    /// resources still unallocated.
    /// </summary>
    public static PatchPreflightPolicy.Report Preflight() =>
        PatchPreflightPolicy.Check(TargetSpecs(), AppDomain.CurrentDomain.GetAssemblies());

    public static void Activate()
    {
        Volatile.Write(ref runtimeActive, 1);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.Refresh), new Type[] { })]
    public static void SetIsPlayingScenario() => isPlayingScenario = true;

    /// <summary>
    /// Scene-level seam. Evidence (docs/scene-frame-evidence.md): the game reads
    /// <c>SceneFrameMaster.ConfigurationJson</c> exactly once, in
    /// <c>ScenarioController+&lt;&gt;c__DisplayClass113_0.&lt;GenerateFrames&gt;b__0</c>, and
    /// <c>GenerateFrames</c> runs before <c>ScenarioController.Refresh</c>. Rewriting the frame
    /// documents here is therefore the single write that reaches every consumer.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.GenerateFrames))]
    public static void TranslateSceneFrames(ScenarioController __instance, Il2CppReferenceArray<SceneFrameMaster> masters)
    {
        var sceneId = __instance.sceneMasterId;
        if (masters is null || sceneId <= 0)
            return;

        ScenePendingWork? pending;
        try
        {
            pending = sceneCoordinator.Prepare(masters, sceneId);
        }
        catch (Exception exception)
        {
            // The frame array belongs to the game; never let translation break scenario loading.
            Logger.Warn("[LLM][Scene] prepare failed: " + exception.GetType().Name);
            return;
        }

        if (pending is { } work)
            EnqueueSceneRequest(work);
    }

    /// <summary>
    /// Sends one whole-scene request and writes the result back. A failure leaves the frame
    /// documents untouched, so the existing per-string path still translates the rendered text.
    /// </summary>
    private static void EnqueueSceneRequest(ScenePendingWork work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var batch = SceneTranslationBatch.TryCreate(work.SceneId, work.Sources);
                if (batch is null)
                    return;

                var client = Plugin.CurrentSceneClient;
                if (client is null)
                    return;

                var response = await client.SendSceneAsync(batch).ConfigureAwait(false);
                if (!batch.TryParseResponse(response, out var translations))
                {
                    Logger.Warn(
                        $"[LLM][Scene] response rejected scene={work.SceneId} targets={batch.TargetCount}");
                    return;
                }

                // Register before marking applied: the reverse index is what stops the per-string
                // path from re-enqueueing these lines once they render.
                foreach (var pair in translations)
                    Plugin.CurrentCache?.RememberResolution(pair.Key, pair.Value);

                sceneCoordinator.MarkApplied(work.SceneId, work.Generation);
                Logger.Info(
                    $"[LLM][Scene] translated scene={work.SceneId} targets={translations.Count}");
            }
            catch (Exception exception)
            {
                Logger.Warn("[LLM][Scene] request failed: " + exception.GetType().Name);
            }
        });
    }

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
        sceneCoordinator.Reset();
        Volatile.Write(ref refreshScanCount, 0);
    }

    private static bool IsRuntimeActive(long epoch) =>
        Volatile.Read(ref runtimeActive) != 0
        && !Plugin.IsCleaningUp
        && tmpProvenance.LifecycleEpoch == epoch;

    /// <summary>
    /// Declared Harmony targets, resolved together with their labels. A game update that removes
    /// or re-signatures a seam fails preflight before any patch is installed.
    /// </summary>
    private static PatchPreflightPolicy.PatchTargetSpec[] TargetSpecs() =>
        new[]
        {
            Spec("TMPro.TMP_Text.set_text", typeof(TMP_Text), "set_text", typeof(string)),
            Spec(
                "Assets.GameUi.Scenario.ScenarioController.Refresh",
                typeof(ScenarioController),
                nameof(ScenarioController.Refresh)),
            Spec(
                "Assets.GameUi.Scenario.ScenarioController.Leave",
                typeof(ScenarioController),
                nameof(ScenarioController.Leave)),
            // Scene-level seam: the game consumes ConfigurationJson in GenerateFrames, which runs
            // before Refresh publishes the frame view models.
            Spec(
                "Assets.GameUi.Scenario.ScenarioController.GenerateFrames",
                typeof(ScenarioController),
                nameof(ScenarioController.GenerateFrames),
                typeof(Il2CppReferenceArray<SceneFrameMaster>),
                typeof(ScenarioController.FunctionFlags),
                typeof(bool)),
        };

    private static PatchPreflightPolicy.PatchTargetSpec Spec(
        string label,
        Type declaringType,
        string methodName,
        params Type[] argumentTypes) =>
        new(
            nameof(Patch),
            label,
            declaringType.FullName ?? declaringType.Name,
            methodName,
            argumentTypes.Length == 0 ? null : argumentTypes);

    private static HarmonyPatchVerificationResult VerifyPatches(
        string harmonyId,
        IReadOnlyList<MethodBase> targets)
    {
        var result = HarmonyPatchVerificationPolicy.Verify(
            targets.Select(target => new HarmonyPatchTargetStatus(
                target.DeclaringType?.FullName + "." + target.Name,
                TargetFound: true,
                OwnedByHarmony: Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) == true)));
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
