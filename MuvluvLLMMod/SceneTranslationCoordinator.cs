using Assets.Api.Client;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MuvluvLLMMod;

/// <summary>
/// Bridges the bounded chunked scene batch protocol to the game's frame arrays.
///
/// Single reader: the game reads
/// <c>SceneFrameMaster.ConfigurationJson</c> exactly once, inside
/// <c>ScenarioController+&lt;&gt;c__DisplayClass113_0.&lt;GenerateFrames&gt;b__0</c>, which runs
/// <c>JsonConvert.DeserializeObject&lt;ScenarioConfiguration&gt;</c> and copies
/// <c>ScenarioConfiguration.Phrase</c> onto the frame view model. <c>GenerateFrames</c> is invoked
/// from <c>ScenarioController+&lt;Refresh&gt;d__76</c> before <c>ScenarioController.Refresh</c>,
/// so a prefix on <c>GenerateFrames</c> is the single seam that reaches every consumer with no
/// second pass.
///
/// Only authoritative cached scene translations rewrite frame documents when display is enabled;
/// unresolved or sentence-fallback lines keep their Japanese frame document intact even while LLM
/// production is disabled so later scene passes can still collect and upgrade them, while the
/// render layer displays any cached fallback in the meantime.
/// </summary>
internal static class SceneTranslationCoordinator
{
    internal static ScenePendingWork? Prepare(Il2CppReferenceArray<SceneFrameMaster>? masters, long sceneId) =>
        Prepare(
            masters,
            sceneId,
            Config.SceneTranslation.Value,
            Config.Translation.Value,
            Config.LlmEnable.Value,
            Plugin.CurrentCache,
            Plugin.CurrentResolver,
            Plugin.CurrentSceneRetries);

    internal static ScenePendingWork? Prepare(
        Il2CppReferenceArray<SceneFrameMaster>? masters,
        long sceneId,
        bool sceneTranslationEnabled,
        bool displayEnabled,
        bool llmEnabled,
        TranslationCache? cache,
        TranslationResolver? resolver,
        SceneRetryRegistry? sceneRetries)
    {
        if (!sceneTranslationEnabled || masters is null || sceneId <= 0)
            return null;

        if (!displayEnabled && !llmEnabled)
            return null;

        if (cache is null || resolver is null)
            return null;

        var retryPriority = sceneRetries?.Contains(sceneId) == true;
        var sceneLines = new List<SceneDialogueLine>();
        var targetIndices = new List<int>();
        var applied = 0;

        foreach (var master in masters)
        {
            if (master is null)
                continue;

            var json = master.ConfigurationJson;
            var lines = SceneFrameDocument.CollectLines(json);
            if (lines.Count == 0)
                continue;

            var known = displayEnabled
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : null;
            foreach (var line in lines)
            {
                var sourceText = line.Text;
                var alreadyTranslatedFrame = false;
                if (cache.IsKnownTranslatedValue(line.Text))
                {
                    if (!cache.TryGetSourceForTranslatedValue(line.Text, out var recoveredSource))
                        continue;
                    sourceText = recoveredSource;
                    alreadyTranslatedFrame = true;
                }

                var template = TextTemplate.Normalize(sourceText).Template;
                var translated = resolver.LookupKnownSource(sourceText);
                var hasCached = !string.IsNullOrEmpty(translated)
                    && !string.Equals(translated, sourceText, StringComparison.Ordinal);
                var isAuthoritative = hasCached
                    && !cache.IsSentenceGenerated(template)
                    && (!retryPriority || cache.IsSceneAuthoritative(template));

                var lineIndex = sceneLines.Count;
                sceneLines.Add(new SceneDialogueLine(sourceText, line.Speaker));
                if (isAuthoritative)
                {
                    if (known is not null
                        && (!alreadyTranslatedFrame
                            || !string.Equals(translated, line.Text, StringComparison.Ordinal)))
                    {
                        known[line.Text] = translated!;
                    }
                }
                else if (llmEnabled)
                {
                    targetIndices.Add(lineIndex);
                }
            }

            if (known is null || known.Count == 0)
                continue;

            var rewritten = SceneFrameDocument.TryRewrite(json, known);
            if (rewritten is null)
                continue;

            master.ConfigurationJson = rewritten;
            applied++;
        }

        if (applied > 0 && Plugin.Log is not null)
            Logger.Info($"[LLM][Scene] applied cached translations scene={sceneId} frames={applied}");

        return llmEnabled && targetIndices.Count > 0
            ? new ScenePendingWork(sceneId, sceneLines, targetIndices)
            : null;
    }
}

/// <summary>Scene dialogue lines and unresolved target indices awaiting chunked scene requests.</summary>
internal readonly record struct ScenePendingWork(
    long SceneId,
    IReadOnlyList<SceneDialogueLine> SceneLines,
    IReadOnlyList<int> TargetIndices);
