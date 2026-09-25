using System.Diagnostics;
using Assets.Api.Client;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MuvluvLLMMod;

/// <summary>
/// Bridges the pure-logic scene batch to the game's frame arrays.
///
/// Evidence (docs/scene-frame-evidence.md): the game reads
/// <c>SceneFrameMaster.ConfigurationJson</c> exactly once, inside
/// <c>ScenarioController+&lt;&gt;c__DisplayClass113_0.&lt;GenerateFrames&gt;b__0</c>, which runs
/// <c>JsonConvert.DeserializeObject&lt;ScenarioConfiguration&gt;</c> and copies
/// <c>ScenarioConfiguration.Phrase</c> onto the frame view model. <c>GenerateFrames</c> is invoked
/// from <c>ScenarioController+&lt;Refresh&gt;d__76</c> before <c>ScenarioController.Refresh</c>,
/// so a prefix on <c>GenerateFrames</c> is the single seam that reaches every consumer with no
/// second pass.
///
/// Translations that are already known are applied immediately; the remainder are sent as one
/// scene request. Only a fully validated response is written back, so a scene is never published
/// half-translated.
/// </summary>
internal sealed class SceneTranslationCoordinator
{
    private readonly SceneTranslationMarker marker = new(TranslationBudget.MaxAppliedSceneEntries);
    private long generation;

    /// <summary>
    /// Applies the currently known translations to every frame document in the array, and returns
    /// the dialogue that still needs translating along with the scene generation it belongs to.
    /// Returns null when scene translation is disabled or the array carries nothing to translate.
    /// </summary>
    internal ScenePendingWork? Prepare(Il2CppReferenceArray<SceneFrameMaster>? masters, long sceneId)
    {
        if (!Config.Translation.Value || !Config.SceneTranslation.Value || masters is null)
            return null;

        var currentGeneration = ++generation;
        if (marker.IsApplied(sceneId, currentGeneration))
            return null;

        var pending = new List<string>();
        var applied = 0;
        foreach (var master in masters)
        {
            if (master is null)
                continue;

            var json = master.ConfigurationJson;
            if (string.IsNullOrEmpty(json))
                continue;

            var texts = SceneFrameDocument.CollectTexts(json);
            if (texts.Count == 0)
                continue;

            var known = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var text in texts)
            {
                var translated = Plugin.CurrentResolver?.Lookup(text);
                if (!string.IsNullOrEmpty(translated)
                    && !string.Equals(translated, text, StringComparison.Ordinal))
                    known[text] = translated;
                else if (!pending.Contains(text, StringComparer.Ordinal))
                    pending.Add(text);
            }

            if (known.Count == 0)
                continue;

            var rewritten = SceneFrameDocument.TryRewrite(json, known);
            if (rewritten is null)
                continue;

            master.ConfigurationJson = rewritten;
            applied++;
        }

        if (applied > 0)
            Logger.Info($"[LLM][Scene] applied cached translations scene={sceneId} frames={applied}");

        return pending.Count == 0
            ? null
            : new ScenePendingWork(sceneId, currentGeneration, pending);
    }

    /// <summary>
    /// Records that a scene generation was fully applied, so a re-entered scene is not resent.
    /// </summary>
    internal void MarkApplied(long sceneId, long sceneGeneration) =>
        marker.MarkApplied(sceneId, sceneGeneration);

    internal int RetainedSceneCount => marker.Count;

    internal void Reset() => marker.Clear();
}

/// <summary>Dialogue awaiting a scene request, together with the scene generation it belongs to.</summary>
internal readonly record struct ScenePendingWork(long SceneId, long Generation, IReadOnlyList<string> Sources);
