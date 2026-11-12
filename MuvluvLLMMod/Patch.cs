using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Assets.Api.Client;
using Assets.GameUi.Scenario;
using Assets.GameUi.Scenario.Configuration;
using Assets.GameUi.Scenario.Text;
using Assets.GameUi.Service;
using Assets.GameUi.Services;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;

namespace MuvluvLLMMod;

public static class Patch
{
    public static bool isPlayingScenario;

    private static readonly Regex RubyTag = new("<r=.*?>(.*?)</r>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly object scenarioGate = new();
    private static readonly DebugTextLogPolicy debugTextLogPolicy = new();
    private static readonly TmpTranslationProvenance tmpProvenance = new();
    private static readonly TmpPluginWriteOwnership tmpWriteOwnership =
        new(tmpProvenance);
    private static int runtimeActive;
    private static int refreshScanCount;
    private static long prepareSequence;
    private static IntPtr preparingControllerPointer;
    private static long scenarioSequence;
    private static IntPtr bindingControllerPointer;
    private static PendingMasterBatch? pendingMasterSnapshots;
    private static ActiveScenario? activeScenario;

    private sealed record PendingMasterBatch(
        IntPtr ControllerPointer,
        (SceneFrameMaster Master, string OriginalJson)[] Snapshots);

    private readonly record struct ActiveScenarioFrame(int Index, string SourceText);

    private sealed class ActiveScenario
    {
        public ActiveScenario(ScenarioController controller, ActiveScenarioFrame[] frames)
        {
            Controller = controller;
            Frames = frames;
        }

        public ScenarioController Controller { get; }
        public ActiveScenarioFrame[] Frames { get; }
        public ScenarioComponent? Component { get; set; }
        public int? SentenceTmpInstanceId { get; set; }
        public IntPtr HandedOffTextModelPointer { get; set; }
        public string? AppliedDisplayText { get; set; }
    }

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
    public static void SetIsPlayingScenario(ScenarioController __instance)
    {
        var epoch = tmpProvenance.LifecycleEpoch;
        if (!IsRuntimeActive(epoch))
            return;

        var controllerPointer = GetControllerPointer(__instance);
        long bindSequence;
        (SceneFrameMaster Master, string OriginalJson)[]? snapshots = null;
        lock (scenarioGate)
        {
            if (!IsRuntimeActive(epoch))
                return;

            bindSequence = ++scenarioSequence;
            bindingControllerPointer = controllerPointer;
            if (pendingMasterSnapshots is { } batch
                && (controllerPointer == IntPtr.Zero || batch.ControllerPointer == controllerPointer))
            {
                snapshots = batch.Snapshots;
                pendingMasterSnapshots = null;
            }
        }

        RestoreMasterSnapshots(snapshots);
        BindActiveScenario(__instance, epoch, bindSequence, controllerPointer);
    }

    /// <summary>
    /// Scene-level seam. Single reader: the game reads
    /// <c>SceneFrameMaster.ConfigurationJson</c> exactly once, in
    /// <c>ScenarioController+&lt;&gt;c__DisplayClass113_0.&lt;GenerateFrames&gt;b__0</c>, and
    /// <c>GenerateFrames</c> runs before <c>ScenarioController.Refresh</c>. Rewriting the frame
    /// documents here reaches initial deserialization, while live refresh updates active view models
    /// as async scene chunks complete or display is toggled.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.GenerateFrames))]
    public static void TranslateSceneFrames(ScenarioController __instance, Il2CppReferenceArray<SceneFrameMaster> masters)
    {
        var epoch = tmpProvenance.LifecycleEpoch;
        if (!IsRuntimeActive(epoch))
            return;

        var controllerPointer = GetControllerPointer(__instance);
        var sceneId = __instance?.sceneMasterId ?? 0;
        if (controllerPointer == IntPtr.Zero || masters is null || sceneId <= 0)
            return;

        long prepSeq;
        (SceneFrameMaster Master, string OriginalJson)[]? displaced = null;
        lock (scenarioGate)
        {
            if (!IsRuntimeActive(epoch))
                return;

            prepSeq = ++prepareSequence;
            preparingControllerPointer = controllerPointer;
            displaced = pendingMasterSnapshots?.Snapshots;
            pendingMasterSnapshots = null;
        }

        RestoreMasterSnapshots(displaced);

        (SceneFrameMaster Master, string OriginalJson)[]? snapshots = null;
        ScenePendingWork? pending;
        try
        {
            snapshots = CaptureMasterSnapshots(masters);
            pending = SceneTranslationCoordinator.Prepare(masters, sceneId);
        }
        catch (Exception exception)
        {
            RestoreMasterSnapshots(snapshots);
            // The frame array belongs to the game; never let translation break scenario loading.
            Logger.Warn("[LLM][Scene] prepare failed: " + exception.GetType().Name);
            return;
        }

        displaced = null;
        var published = false;
        lock (scenarioGate)
        {
            if (IsRuntimeActive(epoch)
                && prepSeq == prepareSequence
                && preparingControllerPointer == controllerPointer)
            {
                displaced = pendingMasterSnapshots?.Snapshots;
                pendingMasterSnapshots = new PendingMasterBatch(controllerPointer, snapshots);
                published = true;
            }
        }

        RestoreMasterSnapshots(displaced);
        if (!published)
        {
            RestoreMasterSnapshots(snapshots);
            return;
        }

        if (pending is { } work && IsPrepareCurrent(epoch, prepSeq, controllerPointer))
            Plugin.SubmitScene(work);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.Leave))]
    public static void SetIsNotPlayingScenario(ScenarioController __instance)
    {
        var epoch = tmpProvenance.LifecycleEpoch;
        if (!IsRuntimeActive(epoch))
            return;

        var leavePointer = GetControllerPointer(__instance);
        if (leavePointer == IntPtr.Zero)
            return;

        (SceneFrameMaster Master, string OriginalJson)[]? snapshots = null;
        lock (scenarioGate)
        {
            if (!IsRuntimeActive(epoch))
                return;

            if (preparingControllerPointer == leavePointer)
            {
                ++prepareSequence;
                preparingControllerPointer = IntPtr.Zero;
            }

            if (pendingMasterSnapshots is { } batch && batch.ControllerPointer == leavePointer)
            {
                snapshots = batch.Snapshots;
                pendingMasterSnapshots = null;
            }

            var activeMatches = activeScenario is { } current
                && GetControllerPointer(current.Controller) == leavePointer;
            var bindingMatches = bindingControllerPointer == leavePointer;
            if (bindingMatches)
            {
                ++scenarioSequence;
                bindingControllerPointer = IntPtr.Zero;
            }

            if (activeMatches)
            {
                Volatile.Write(ref activeScenario, null);
                if (bindingControllerPointer == IntPtr.Zero)
                    isPlayingScenario = false;
            }
            else if (bindingMatches && activeScenario == null)
            {
                isPlayingScenario = false;
            }
        }

        RestoreMasterSnapshots(snapshots);
    }

    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(ScenarioTextTimer),
        nameof(ScenarioTextTimer.Apply),
        new[] { typeof(ScenarioController.TextViewModel) })]
    public static void ObserveScenarioTimerApply(
        ScenarioTextTimer __instance,
        ScenarioController.TextViewModel? model)
    {
        var epoch = tmpProvenance.LifecycleEpoch;
        if (!IsRuntimeActive(epoch))
            return;

        var scenario = Volatile.Read(ref activeScenario);
        if (scenario == null || !IsCurrentScenario(scenario, epoch))
            return;

        var component = ResolveScenarioComponent(scenario);
        if (component?.textTimer is not { } ownedTimer
            || ownedTimer.Pointer == IntPtr.Zero
            || ownedTimer.Pointer != __instance.Pointer)
        {
            return;
        }

        if (model is { } textModel && textModel.Pointer != IntPtr.Zero)
        {
            scenario.HandedOffTextModelPointer = textModel.Pointer;
            scenario.AppliedDisplayText = textModel.Sentence?.Text;
        }
        else
        {
            scenario.HandedOffTextModelPointer = IntPtr.Zero;
            scenario.AppliedDisplayText = null;
        }
    }

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

        // TextMeshProRuby expands ruby tags and prepends <line-height=...em> on every typewriter
        // character tick for ScenarioTextComponent.sentenceText; active scenario dialogue is owned
        // at the ScenarioFrameViewModel/ScenarioComponent level instead.
        if (IsActiveScenarioSentenceTmp(instanceId))
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
                if (origin == TmpTextAssignmentOrigin.PluginRefresh
                    && tmpProvenance.TryGetRecordedSource(assignment, original, out var recordedSource))
                {
                    var upgraded = Core.Resolver.LookupKnownSource(recordedSource, enabled: true);
                    if (!string.Equals(upgraded, recordedSource, StringComparison.Ordinal)
                        && !string.Equals(upgraded, original, StringComparison.Ordinal))
                    {
                        value = upgraded;
                        tmpProvenance.Record(assignment, value, recordedSource);
                    }
                }
                else
                {
                    value = Translation.ResolveAny(original, enqueue: true);
                    enqueueObservation = Translation.LastEnqueueObservation;
                    if (!string.Equals(value, original, StringComparison.Ordinal))
                        tmpProvenance.Record(assignment, value, original);
                }
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

        RefreshActiveScenario(scanEpoch);

        var texts = UnityEngine.Object.FindObjectsByType<TMP_Text>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);

        foreach (var text in texts)
        {
            if (text == null || !IsRuntimeActive(scanEpoch))
                continue;

            var instanceId = text.GetInstanceID();
            if (IsActiveScenarioSentenceTmp(instanceId))
                continue;

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

    private static (SceneFrameMaster Master, string OriginalJson)[] CaptureMasterSnapshots(
        Il2CppReferenceArray<SceneFrameMaster> masters)
    {
        var snapshots = new List<(SceneFrameMaster, string)>(masters.Length);
        foreach (var master in masters)
        {
            if (master is null)
                continue;

            var json = master.ConfigurationJson;
            if (!string.IsNullOrEmpty(json))
                snapshots.Add((master, json));
        }
        return snapshots.ToArray();
    }

    private static void RestoreMasterSnapshots((SceneFrameMaster Master, string OriginalJson)[]? snapshots)
    {
        if (snapshots is null)
            return;

        foreach (var (master, originalJson) in snapshots)
        {
            try
            {
                if (master is not null)
                    master.ConfigurationJson = originalJson;
            }
            catch
            {
            }
        }
    }

    private static void BindActiveScenario(
        ScenarioController? controller,
        long epoch,
        long bindSequence,
        IntPtr controllerPointer)
    {
        lock (scenarioGate)
        {
            if (!IsRuntimeActive(epoch)
                || bindSequence != scenarioSequence
                || bindingControllerPointer != controllerPointer)
            {
                return;
            }

            Volatile.Write(ref activeScenario, null);
            if (controller == null)
            {
                isPlayingScenario = true;
                return;
            }
        }

        ActiveScenario? publishedScenario = null;
        try
        {
            var frames = controller.frames;
            ActiveScenarioFrame[] trackedFrames;
            if (frames == null || frames.Length == 0)
            {
                trackedFrames = Array.Empty<ActiveScenarioFrame>();
            }
            else
            {
                var tracked = new List<ActiveScenarioFrame>(frames.Length);
                for (var i = 0; i < frames.Length; i++)
                {
                    if (!IsBindCurrent(epoch, bindSequence, controllerPointer))
                        return;

                    var frame = frames[i];
                    if (frame == null)
                        continue;

                    var sourceText = ExtractFrameSourceText(frame);
                    if (!string.IsNullOrEmpty(sourceText))
                        tracked.Add(new ActiveScenarioFrame(i, sourceText));
                }

                trackedFrames = tracked.ToArray();
            }

            var scenario = new ActiveScenario(controller, trackedFrames);
            lock (scenarioGate)
            {
                if (!IsRuntimeActive(epoch)
                    || bindSequence != scenarioSequence
                    || bindingControllerPointer != controllerPointer)
                {
                    return;
                }

                isPlayingScenario = true;
                Volatile.Write(ref activeScenario, scenario);
                publishedScenario = scenario;
            }

            SyncActiveScenario(scenario, epoch, updateVisibleComponent: false);
        }
        catch (Exception exception)
        {
            if (publishedScenario != null)
            {
                lock (scenarioGate)
                {
                    if (ReferenceEquals(activeScenario, publishedScenario))
                        Volatile.Write(ref activeScenario, null);
                }
            }
            Logger.Warn("[LLM][Scene] bind failed: " + exception.GetType().Name);
        }
    }

    private static string? ExtractFrameSourceText(ScenarioController.ScenarioFrameViewModel frame)
    {
        if (SceneFrameDocument.TryReadText(frame.FrameMaster?.ConfigurationJson, out var masterText))
            return masterText is not null && TextTemplate.IsTranslationCandidate(masterText) ? masterText : null;

        var phraseText = frame.PhraseConfiguration?.Text;
        return phraseText is not null && TextTemplate.IsTranslationCandidate(phraseText) ? phraseText : null;
    }

    private static void RefreshActiveScenario(long scanEpoch)
    {
        var scenario = Volatile.Read(ref activeScenario);
        if (scenario == null || !IsCurrentScenario(scenario, scanEpoch))
            return;

        try
        {
            SyncActiveScenario(scenario, scanEpoch, updateVisibleComponent: true);
        }
        catch (Exception exception)
        {
            lock (scenarioGate)
            {
                if (ReferenceEquals(activeScenario, scenario))
                    Volatile.Write(ref activeScenario, null);
            }
            Logger.Warn("[LLM][Scene] live sync failed: " + exception.GetType().Name);
        }
    }

    private static void SyncActiveScenario(ActiveScenario scenario, long scanEpoch, bool updateVisibleComponent)
    {
        if (!IsCurrentScenario(scenario, scanEpoch))
            return;

        var controller = scenario.Controller;
        var frames = controller.frames;
        var resolver = Plugin.CurrentResolver;
        if (frames == null || frames.Length == 0 || resolver == null)
            return;

        var currentFrameId = updateVisibleComponent
            ? controller.FrameIdentifier?.Value?.Current
            : null;
        var displayEnabled = Config.Translation.Value;

        foreach (var entry in scenario.Frames)
        {
            if (!IsCurrentScenario(scenario, scanEpoch))
                return;

            if (entry.Index < 0 || entry.Index >= frames.Length)
               continue;

            var frame = frames[entry.Index];
            var phrase = frame?.PhraseConfiguration;
            if (frame == null || phrase == null)
                continue;

            var isCurrentFrame = currentFrameId.HasValue && frame.Id == currentFrameId.Value;
            string desiredRawText;
            if (displayEnabled)
            {
                desiredRawText = isCurrentFrame
                    ? resolver.ResolveKnownSource(entry.SourceText, enabled: true, priority: true)
                    : resolver.LookupKnownSource(entry.SourceText, enabled: true);
            }
            else
            {
                if (isCurrentFrame)
                    _ = resolver.ResolveKnownSource(entry.SourceText, enabled: true, priority: true);
                desiredRawText = entry.SourceText;
            }

            if (!IsCurrentScenario(scenario, scanEpoch))
                return;

            var phraseChanged = !string.Equals(phrase.Text, desiredRawText, StringComparison.Ordinal);
            if (phraseChanged)
            {
                phrase.Text = desiredRawText;
                UpdatePhraseDisplayText(phrase, controller.scenarioService, desiredRawText);
                if (!IsCurrentScenario(scenario, scanEpoch))
                    return;

                UpdateFrameHistory(controller.histories, frame.Id, phrase.DisplayText ?? desiredRawText);
            }

            if (!IsCurrentScenario(scenario, scanEpoch))
                return;

            var displayText = phrase.DisplayText ?? desiredRawText;
            UpdateFrameSentenceModel(frame, displayText);

            if (isCurrentFrame
                && updateVisibleComponent
                && IsFrameHandedOff(scenario, frame)
                && !string.Equals(scenario.AppliedDisplayText, displayText, StringComparison.Ordinal)
                && IsCurrentScenario(scenario, scanEpoch))
            {
                ApplyLiveFrameText(scenario, scanEpoch, frame, displayText);
            }
        }
    }

    private static void UpdatePhraseDisplayText(
        ScenarioPhraseConfiguration phrase,
        IScenarioService? scenarioService,
        string rawText)
    {
        if (scenarioService != null)
        {
            phrase.ConvertStringToDisplay(scenarioService);
            return;
        }

        phrase.DisplayText = rawText;
    }

    private static void UpdateFrameHistory(
        Il2CppSystem.Collections.Generic.List<ScenarioController.HistoryViewModel>? histories,
        long frameId,
        string displayText)
    {
        if (histories == null)
            return;

        string? historyText = null;
        for (var i = 0; i < histories.Count; i++)
        {
            var item = histories[i];
            if (item == null || item.FrameId != frameId)
                continue;

            historyText ??= RubyTag.Replace(displayText, "$1");
            item.Text = historyText;
        }
    }

    private static void UpdateFrameSentenceModel(
        ScenarioController.ScenarioFrameViewModel frame,
        string displayText)
    {
        var sentence = frame.Text?.Sentence;
        if (sentence == null || string.Equals(sentence.Text, displayText, StringComparison.Ordinal))
            return;

        sentence.Text = displayText;
        sentence.ContainsRuby = RubyTag.IsMatch(displayText);
    }

    private static bool IsFrameHandedOff(
        ActiveScenario scenario,
        ScenarioController.ScenarioFrameViewModel frame) =>
        frame.Text is { } textModel
        && textModel.Pointer != IntPtr.Zero
        && scenario.HandedOffTextModelPointer == textModel.Pointer;

    private static void ApplyLiveFrameText(
        ActiveScenario scenario,
        long scanEpoch,
        ScenarioController.ScenarioFrameViewModel frame,
        string displayText)
    {
        var textModel = frame.Text;
        var sentence = textModel?.Sentence;
        if (textModel == null || sentence == null)
            return;

        sentence.Text = displayText;
        sentence.ContainsRuby = RubyTag.IsMatch(displayText);

        var component = ResolveScenarioComponent(scenario);
        if (component == null || !IsCurrentScenario(scenario, scanEpoch))
            return;

        var timer = component.textTimer;
        if (timer != null && !timer.Disposed)
        {
            timer.Apply(textModel);
            return;
        }

        if (component.textView != null)
        {
            sentence.AtEnd = true;
            component.textView.ApplySentence(sentence);
            scenario.AppliedDisplayText = displayText;
        }
    }

    private static bool IsActiveScenarioSentenceTmp(int instanceId)
    {
        var scenario = Volatile.Read(ref activeScenario);
        if (scenario == null)
            return false;

        try
        {
            var component = ResolveScenarioComponent(scenario);
            if (component == null)
                return false;

            if (scenario.SentenceTmpInstanceId is { } knownId)
                return knownId == instanceId;

            var tmp = component.textView?.sentenceText?.tmpText;
            if (tmp == null)
                return false;

            var resolvedId = tmp.GetInstanceID();
            scenario.SentenceTmpInstanceId = resolvedId;
            return resolvedId == instanceId;
        }
        catch
        {
            return false;
        }
    }

    private static ScenarioComponent? ResolveScenarioComponent(ActiveScenario scenario)
    {
        if (IsOwnedActiveComponent(scenario.Component, scenario.Controller))
            return scenario.Component;

        scenario.Component = null;
        scenario.SentenceTmpInstanceId = null;
        var found = UnityEngine.Object.FindObjectsByType<ScenarioComponent>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        foreach (var candidate in found)
        {
            if (!IsOwnedActiveComponent(candidate, scenario.Controller))
                continue;

            scenario.Component = candidate;
            return candidate;
        }

        return null;
    }

    private static bool IsOwnedActiveComponent(
        ScenarioComponent? component,
        ScenarioController controller) =>
        component is { isActiveAndEnabled: true }
        && component.Controller is { } componentController
        && componentController.Pointer != IntPtr.Zero
        && componentController.Pointer == controller.Pointer;

    private static IntPtr GetControllerPointer(ScenarioController? controller) =>
        controller is not null ? controller.Pointer : IntPtr.Zero;

    private static void ResetRuntimeState()
    {
        (SceneFrameMaster Master, string OriginalJson)[]? snapshots;
        lock (scenarioGate)
        {
            Volatile.Write(ref runtimeActive, 0);
            tmpWriteOwnership.ResetForLifecycle();
            tmpProvenance.ResetForLifecycle();
            ++prepareSequence;
            preparingControllerPointer = IntPtr.Zero;
            ++scenarioSequence;
            bindingControllerPointer = IntPtr.Zero;
            isPlayingScenario = false;
            Volatile.Write(ref activeScenario, null);
            snapshots = pendingMasterSnapshots?.Snapshots;
            pendingMasterSnapshots = null;
        }

        RestoreMasterSnapshots(snapshots);
        Volatile.Write(ref refreshScanCount, 0);
    }

    private static bool IsPrepareCurrent(long epoch, long expectedSequence, IntPtr controllerPointer)
    {
        if (!IsRuntimeActive(epoch))
            return false;

        lock (scenarioGate)
        {
            return IsRuntimeActive(epoch)
                && prepareSequence == expectedSequence
                && preparingControllerPointer == controllerPointer;
        }
    }

    private static bool IsBindCurrent(long epoch, long expectedSequence, IntPtr controllerPointer)
    {
        if (!IsRuntimeActive(epoch))
            return false;

        lock (scenarioGate)
        {
            return IsRuntimeActive(epoch)
                && scenarioSequence == expectedSequence
                && bindingControllerPointer == controllerPointer;
        }
    }

    private static bool IsCurrentScenario(ActiveScenario scenario, long epoch) =>
        IsRuntimeActive(epoch)
        && ReferenceEquals(Volatile.Read(ref activeScenario), scenario);

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
            Spec(
                "Assets.GameUi.Scenario.Text.ScenarioTextTimer.Apply",
                typeof(ScenarioTextTimer),
                nameof(ScenarioTextTimer.Apply),
                typeof(ScenarioController.TextViewModel)),
        };

    // Every seam names its exact signature; no argument types means the parameterless overload,
    // matching `new Type[] { }` on the Refresh patch (the game declares three Refresh overloads).
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
            argumentTypes);

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
