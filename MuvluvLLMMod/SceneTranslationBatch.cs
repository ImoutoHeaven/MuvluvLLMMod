using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuvluvLLMMod;

/// <summary>
/// Scene-level batch translation for one scene.
///
/// A scene is sent as one request so the model sees every line in order, with each line's speaker
/// name, which is what keeps speaker tone, forms of address, and terminology consistent across the
/// scene. Only dialogue <c>Text</c> is translated; speaker names are context and stay on the
/// per-string path.
///
/// Targets are normalized templates, the same unit the per-string path translates and caches, so an
/// accepted line is stored as a generated template and every consumer resolves it identically.
///
/// Validation has two levels. The response structure must echo the protocol version and scene id
/// and carry exactly one entry per target, in order, with matching ids; otherwise the whole batch
/// is rejected, because entries can no longer be matched to lines. Within a valid structure each
/// entry is checked on its own: a non-empty translation that restores to its exact protected token
/// sequence with matching placeholders and markup is accepted, and any other entry is dropped so
/// that line stays on the per-string path.
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
    /// Builds a batch from the scene's dialogue lines in story order. Every occurrence is sent, so
    /// a repeated line keeps each of its speakers. Returns null when the scene holds nothing
    /// translatable, or when the payload would exceed the bounded prompt budget.
    /// </summary>
    public static SceneTranslationBatch? TryCreate(long sceneId, IEnumerable<SceneDialogueLine> lines)
    {
        var states = new List<TargetState>();

        foreach (var line in lines)
        {
            if (!TextTemplate.IsTranslationCandidate(line.Text))
                continue;

            var template = TextTemplate.Normalize(line.Text).Template;
            var protectedText = TextTemplate.ProtectForLlm(template);
            if (string.IsNullOrEmpty(protectedText.Prompt))
                continue;

            states.Add(new TargetState(
                "t" + states.Count.ToString("D4"),
                template,
                string.IsNullOrEmpty(line.Speaker) ? null : line.Speaker,
                protectedText));
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
                        Name = state.Speaker,
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
    /// Validates a response against this batch and yields template-to-translated-template pairs
    /// for the entries that passed; a template sent more than once keeps its first valid
    /// translation. Returns false only when the structure does not match.
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

        // Order and identity are checked so a missing, duplicated, or reordered entry rejects the
        // batch instead of shifting translations onto the wrong lines.
        for (var index = 0; index < targets.Count; index++)
        {
            if (document.Translations[index] is not { } entry
                || !string.Equals(entry.Id, targets[index].Id, StringComparison.Ordinal))
                return false;
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < targets.Count; index++)
        {
            var expected = targets[index];
            var value = document.Translations[index]!.Text;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            // Do not trim: a leading or trailing line break is meaningful scene text, and trimming
            // it would desynchronize the protection checks. Whitespace the model adds around the
            // value is caught by the markup comparison below.
            if (!parsed.ContainsKey(expected.Template)
                && !string.IsNullOrWhiteSpace(text)
                && TextTemplate.TryRestoreLlm(text, expected.ProtectedText, out var translated)
                && !string.IsNullOrEmpty(translated)
                && !string.Equals(translated, expected.Template, StringComparison.Ordinal)
                && TextTemplate.HasSamePlaceholders(expected.Template, translated)
                && TextTemplate.HasSameMarkup(expected.Template, translated))
                parsed[expected.Template] = translated;
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

    private sealed record TargetState(string Id, string Template, string? Speaker, LlmProtectedText ProtectedText);

    private sealed class SceneRequestPayload
    {
        public int Version { get; set; }
        public long SceneId { get; set; }
        public List<SceneTargetPayload> Targets { get; set; } = new();
    }

    private sealed class SceneTargetPayload
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string Source { get; set; } = string.Empty;
    }

    private sealed class SceneResponsePayload
    {
        public int Version { get; set; }
        public long SceneId { get; set; }
        public List<SceneTranslationEntry?>? Translations { get; set; }
    }

    private sealed class SceneTranslationEntry
    {
        public string Id { get; set; } = string.Empty;
        // Read per entry, so a wrongly typed text drops only its own line.
        public JsonElement Text { get; set; }
    }
}
