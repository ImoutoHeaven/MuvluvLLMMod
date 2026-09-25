using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuvluvLLMMod;

/// <summary>
/// Scene-level batch translation for one scene.
///
/// A scene is sent as one request so the model sees every line in order, which is what keeps
/// speaker tone and terminology consistent across the scene. Only dialogue <c>Text</c> is
/// batched: speaker and team names are few and highly repetitive, so the per-string cache
/// already translates each of them once.
///
/// Validation is deliberately all-or-nothing. The batch is accepted only when the response
/// echoes the protocol version and scene id, carries exactly one entry per target in the same
/// order, and every entry is non-empty and restores to its exact protected token sequence with
/// matching markup. A single bad entry rejects the whole scene, and the caller falls back to the
/// per-string path rather than publishing a partial scene.
///
/// Pure logic: no Harmony, no Unity, and no game types, so the loader-free test project covers it.
/// </summary>
public sealed class SceneTranslationBatch
{
    public const int ProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyList<TargetState> targets;

    private SceneTranslationBatch(long sceneId, string prompt, IReadOnlyList<TargetState> targets)
    {
        SceneId = sceneId;
        Prompt = prompt;
        this.targets = targets;
    }

    public long SceneId { get; }

    /// <summary>The user-message payload for this scene.</summary>
    public string Prompt { get; }

    public int TargetCount => targets.Count;

    /// <summary>
    /// Builds a batch from the scene's dialogue sources. Returns null when the scene holds nothing
    /// translatable, or when the payload would exceed the bounded prompt budget.
    /// </summary>
    public static SceneTranslationBatch? TryCreate(long sceneId, IEnumerable<string> sources)
    {
        var states = new List<TargetState>();
        var idByTemplate = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            if (!TextTemplate.IsTranslationCandidate(source))
                continue;

            var protectedText = TextTemplate.ProtectForLlm(source);
            if (string.IsNullOrEmpty(protectedText.Prompt)
                || idByTemplate.ContainsKey(protectedText.Prompt))
                continue;

            idByTemplate.Add(protectedText.Prompt, "t" + states.Count.ToString("D4"));
            states.Add(new TargetState(idByTemplate[protectedText.Prompt], source, protectedText));
        }

        if (states.Count == 0)
            return null;

        var prompt = JsonSerializer.Serialize(
            new SceneRequestPayload
            {
                Version = ProtocolVersion,
                SceneId = sceneId,
                Targets = states
                    .Select(state => new SceneTargetPayload
                    {
                        Id = state.Id,
                        Source = state.ProtectedText.Prompt,
                    })
                    .ToList(),
            },
            JsonOptions);

        return TranslationBudget.IsSceneWithinBudget(prompt)
            ? new SceneTranslationBatch(sceneId, prompt, states)
            : null;
    }

    /// <summary>
    /// Validates a response against this batch and yields source-to-translation pairs. Every
    /// target must appear exactly once, in order, with the same id, carrying non-empty text whose
    /// token sequence restores exactly and whose markup matches the source. Any mismatch fails
    /// the whole scene.
    /// </summary>
    public bool TryParseResponse(string? response, out IReadOnlyDictionary<string, string> translations)
    {
        translations = EmptyTranslations;
        if (string.IsNullOrWhiteSpace(response)
            || !TranslationBudget.IsSceneWithinBudget(response))
            return false;

        SceneResponsePayload? document;
        try
        {
            document = JsonSerializer.Deserialize<SceneResponsePayload>(StripCodeFence(response), JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (document is null
            || document.Version != ProtocolVersion
            || document.SceneId != SceneId
            || document.Translations is null
            || document.Translations.Count != targets.Count)
            return false;

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < targets.Count; index++)
        {
            var expected = targets[index];
            var actual = document.Translations[index];

            // Order and identity are checked so a missing, duplicated, or reordered entry fails the
            // whole scene instead of shifting translations onto the wrong lines.
            // Do not trim: a leading or trailing line break is meaningful scene text, and
            // trimming it would desynchronize the rest of the protection checks. Whitespace the
            // model adds around the value is caught by the markup comparison below.
            if (actual is null
                || !string.Equals(actual.Id, expected.Id, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(actual.Text))
                return false;

            if (!TextTemplate.TryRestoreLlm(actual.Text, expected.ProtectedText, out var translated))
                return false;

            if (string.IsNullOrEmpty(translated)
                || string.Equals(translated, expected.Source, StringComparison.Ordinal)
                || !TextTemplate.HasSamePlaceholders(expected.Source, translated)
                || !TextTemplate.HasSameMarkup(expected.Source, translated))
                return false;

            parsed[expected.Source] = translated;
        }

        translations = parsed;
        return true;
    }

    private static IReadOnlyDictionary<string, string> EmptyTranslations { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static string StripCodeFence(string response)
    {
        var trimmed = response.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var start = trimmed.IndexOf('\n');
        var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start
            ? trimmed.Substring(start + 1, end - start - 1).Trim()
            : trimmed;
    }

    private sealed record TargetState(string Id, string Source, LlmProtectedText ProtectedText);

    private sealed class SceneRequestPayload
    {
        public int Version { get; set; }
        public long SceneId { get; set; }
        public List<SceneTargetPayload> Targets { get; set; } = new();
    }

    private sealed class SceneTargetPayload
    {
        public string Id { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    private sealed class SceneResponsePayload
    {
        public int Version { get; set; }
        public long SceneId { get; set; }
        public List<SceneTranslationEntry>? Translations { get; set; }
    }

    private sealed class SceneTranslationEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}
