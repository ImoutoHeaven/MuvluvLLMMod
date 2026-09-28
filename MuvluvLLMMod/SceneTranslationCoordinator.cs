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
/// Translations already in the cache are applied immediately; the remaining lines are returned for
/// one scene request. Accepted scene results are stored in the same generated-template cache the
/// per-string path uses, so the next entry applies them here and the rendered text resolves them
/// in the meantime. A line whose cached translation exists is never requested again.
/// </summary>
internal static class SceneTranslationCoordinator
{
    /// <summary>
    /// Applies the currently known translations to every frame document in the array, and returns
    /// every dialogue line that still needs translating, in story order with its speaker. Returns null
    /// when scene translation is disabled or nothing remains to translate.
    /// </summary>
    internal static ScenePendingWork? Prepare(Il2CppReferenceArray<SceneFrameMaster>? masters, long sceneId)
    {
        if (!Config.Translation.Value || !Config.SceneTranslation.Value || masters is null)
            return null;

        var pending = new List<SceneDialogueLine>();
        var applied = 0;
        foreach (var master in masters)
        {
            if (master is null)
                continue;

            var json = master.ConfigurationJson;
            var lines = SceneFrameDocument.CollectLines(json);
            if (lines.Count == 0)
                continue;

            var known = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                // Frame objects are cached by the game, so a re-entered scene can carry a document
                // this seam already rewrote; a translation that still contains kana is not new work.
                // ponytail: relies on the bounded reverse index, like the per-string path; once its
                // entry is evicted such a line is requested again. Keep rewrite provenance per
                // frame if that shows up in logs.
                if (Plugin.CurrentCache?.IsKnownTranslatedValue(line.Text) == true)
                    continue;

                var translated = Plugin.CurrentResolver?.Lookup(line.Text);
                if (!string.IsNullOrEmpty(translated)
                    && !string.Equals(translated, line.Text, StringComparison.Ordinal))
                    known[line.Text] = translated;
                else
                    pending.Add(line);
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
            : new ScenePendingWork(sceneId, pending);
    }
}

/// <summary>Dialogue awaiting a scene request, in story order.</summary>
internal readonly record struct ScenePendingWork(long SceneId, IReadOnlyList<SceneDialogueLine> Lines);
