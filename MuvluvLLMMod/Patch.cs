using System.Diagnostics;
using Assets.Api.Client;
using Assets.GameUi.Scenario;
using Assets.GameUi.Utilities;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MuvluvLLMMod;

public static class Patch
{
    public static bool isPlayingScenario;

    [ThreadStatic]
    private static bool translatingTmp;

    private static readonly object debugGate = new();
    private static readonly HashSet<string> debugSeenText = new(StringComparer.Ordinal);
    private static int refreshScanCount;

    public static void Initialize(Harmony harmony)
    {
        harmony.PatchAll(typeof(Patch));
        VerifyPatches(harmony.Id);
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
        if (translatingTmp)
            return;

        var original = value ?? string.Empty;
        var containsKana = TextTemplate.IsTranslationCandidate(original);
        var enqueued = false;
        translatingTmp = true;
        try
        {
            if (!string.IsNullOrEmpty(original))
            {
                if (Config.Translation.Value)
                {
                    value = Translation.ResolveAny(original, enqueue: true);
                    enqueued = Translation.LastResolveEnqueued;
                }
                else
                {
                    enqueued = Translation.ObserveForTranslation(original, isPlayingScenario);
                    // HARD RULE: only restore text that our reverse map resolves; upstream text is never reverted.
                    if (Core.Cache.TryGetSourceForTranslatedValue(original, out var source))
                        value = source;
                }
            }
        }
        finally
        {
            translatingTmp = false;
        }

        LogSeenText(original, containsKana, enqueued);
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(SkillDescriptionBuilder),
        nameof(SkillDescriptionBuilder.GetDescription),
        new[] { typeof(SkillMaster), typeof(int), typeof(bool) })]
    public static void TranslateSkillDescription(ref string __result)
    {
        if (!string.IsNullOrEmpty(__result))
            __result = Translation.ResolveAny(__result);
    }

    public static void RefreshAllTmpText()
    {
        var started = Stopwatch.GetTimestamp();
        var texts = UnityEngine.Object.FindObjectsByType<TMP_Text>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        foreach (var text in texts)
        {
            if (text == null)
                continue;

            var value = text.text ?? string.Empty;
            TranslateTmpSetter(text, ref value);
            if (!string.Equals(text.text, value, StringComparison.Ordinal))
                text.text = value;
        }

        var elapsedMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        if (Interlocked.Increment(ref refreshScanCount) % 20 == 0)
        {
            Logger.Info(
                $"[LLM] TMP refresh scan: objects={texts.Length}, "
                + $"elapsedMs={elapsedMilliseconds:F2}");
        }
    }

    private static void LogSeenText(string text, bool containsKana, bool enqueued)
    {
        if (!Config.DebugLogSeenText.Value)
            return;

        lock (debugGate)
        {
            if (!debugSeenText.Add(text))
                return;
        }

        var displayed = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        if (displayed.Length > 80)
            displayed = displayed[..80] + "...";

        Logger.Info($"[LLM] seen text=\"{displayed}\" kana={containsKana} enqueued={enqueued}");
    }

    private static void VerifyPatches(string harmonyId)
    {
        var targets = new[]
        {
            AccessTools.Method(typeof(TMP_Text), "set_text"),
            AccessTools.Method(
                typeof(SkillDescriptionBuilder),
                nameof(SkillDescriptionBuilder.GetDescription),
                new[] { typeof(SkillMaster), typeof(int), typeof(bool) })
        };
        var missing = targets
            .Where(target => target == null || Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) != true)
            .Select(target => target == null ? "unknown" : target.DeclaringType?.FullName + "." + target.Name)
            .ToArray();
        if (missing.Length == 0)
            Logger.Info($"Harmony patches verified: owner={harmonyId}, targets={targets.Length}");
        else
            Logger.Error($"Harmony patch verification failed: owner={harmonyId}, missing={string.Join(", ", missing)}");
    }
}
