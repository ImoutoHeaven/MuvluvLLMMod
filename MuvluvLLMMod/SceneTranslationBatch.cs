using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuvluvLLMMod;

public sealed record SceneBatchPlan(
    SceneTranslationBatch Batch,
    IReadOnlyList<string> ReservedTemplates);

/// <summary>
/// Scene-level batch translation for one scene or one bounded scene chunk.
///
/// Targets are normalized templates, the same unit the per-string path translates and caches, so an
/// accepted line is stored as a generated template and every consumer resolves it identically.
/// Surrounding scene dialogue lines are attached as read-only context when a scene is sliced into
/// bounded chunks or partially cached.
/// </summary>
public sealed class SceneTranslationBatch
{
    public const int ProtocolVersion = 1;
    public const int DefaultMaxTargetsPerBatch = 24;
    public const int DefaultContextPaddingLines = 12;
    private const int RetryHeaderReserveChars = 320;
    private const int RetryHeaderReserveBytes = 640;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyList<TargetState> targets;
    private readonly IReadOnlyList<string> targetTemplates;

    private SceneTranslationBatch(
        long sceneId,
        string prompt,
        IReadOnlyList<TargetState> targets,
        IReadOnlyList<string> targetTemplates)
    {
        SceneId = sceneId;
        Prompt = prompt;
        this.targets = targets;
        this.targetTemplates = targetTemplates;
    }

    public long SceneId { get; }

    /// <summary>The user-message payload for this scene batch.</summary>
    public string Prompt { get; }

    public int TargetCount => targets.Count;

    public IReadOnlyList<string> TargetTemplates => targetTemplates;

    public static int GetMaximumAttempts(int configuredRetryCount) =>
        Math.Clamp(Math.Max(4, configuredRetryCount + 1), 1, 8);

    public static string BuildRetryPrompt(string basePrompt, string? previousRejectionReason)
    {
        if (string.IsNullOrWhiteSpace(previousRejectionReason))
            return basePrompt;

        var prefix =
            $"Previous response rejected ({previousRejectionReason}). "
            + "Return ONLY one valid JSON object matching version, sceneId, and every target id "
            + "in exact order, with every __MLM_FMT_n__ token preserved.\n";
        var retryPrompt = prefix + basePrompt;
        if (TranslationBudget.IsSceneWithinBudget(retryPrompt))
            return retryPrompt;

        var stripped = StripOptionalContext(basePrompt);
        if (!ReferenceEquals(stripped, basePrompt))
        {
            var strippedRetry = prefix + stripped;
            if (TranslationBudget.IsSceneWithinBudget(strippedRetry))
                return strippedRetry;
        }

        var shortPrefix = $"Rejected ({previousRejectionReason}). Return exact JSON only.\n";
        var compactRetry = shortPrefix + stripped;
        return TranslationBudget.IsSceneWithinBudget(compactRetry)
            ? compactRetry
            : basePrompt;
    }

    /// <summary>
    /// Builds a single batch from dialogue lines in story order. Every occurrence is sent so a
    /// repeated line keeps each of its speakers. Returns null when nothing is translatable or when
    /// the payload exceeds the bounded prompt budget.
    /// </summary>
    public static SceneTranslationBatch? TryCreate(long sceneId, IEnumerable<SceneDialogueLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var states = BuildTargetStates(lines, out var templates);
        if (states.Count == 0)
            return null;

        return TryBuildBatch(sceneId, states, templates, context: null, reserveRetryHeadroom: false);
    }

    /// <summary>
    /// Splits a scene's unresolved dialogue targets into bounded batches of at most
    /// <paramref name="maxTargetsPerBatch"/> distinct normalized templates in story order, with
    /// disjoint template reservations and surrounding scene dialogue context sliced across all
    /// occurrences of each batch's targets.
    /// </summary>
    public static IReadOnlyList<SceneBatchPlan> CreateBatches(
        long sceneId,
        IReadOnlyList<SceneDialogueLine> sceneLines,
        IReadOnlyList<int>? targetLineIndices = null,
        int maxTargetsPerBatch = DefaultMaxTargetsPerBatch,
        int contextPaddingLines = DefaultContextPaddingLines)
    {
        ArgumentNullException.ThrowIfNull(sceneLines);
        if (sceneLines.Count == 0)
            return Array.Empty<SceneBatchPlan>();

        var chunkSize = Math.Max(1, maxTargetsPerBatch);
        var padding = Math.Max(0, contextPaddingLines);
        var candidateIndices = targetLineIndices
            ?? Enumerable.Range(0, sceneLines.Count).ToArray();

        // Track full min/max span of each normalized template across the entire scene so even a
        // repeated template whose earlier occurrence was deduplicated slices context across all of
        // its scene occurrences.
        var spanByTemplate = new Dictionary<string, (int MinLine, int MaxLine)>(StringComparer.Ordinal);
        for (var i = 0; i < sceneLines.Count; i++)
        {
            var text = sceneLines[i].Text;
            if (!TextTemplate.IsTranslationCandidate(text))
                continue;
            var template = TextTemplate.Normalize(text).Template;
            if (spanByTemplate.TryGetValue(template, out var span))
                spanByTemplate[template] = (span.MinLine, i);
            else
                spanByTemplate[template] = (i, i);
        }

        var orderedGroups = new List<TemplateGroup>();
        var groupIndexByTemplate = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var lineIndex in candidateIndices)
        {
            if (lineIndex < 0 || lineIndex >= sceneLines.Count)
                continue;

            var line = sceneLines[lineIndex];
            if (!TextTemplate.IsTranslationCandidate(line.Text))
                continue;

            var template = TextTemplate.Normalize(line.Text).Template;
            var protectedText = TextTemplate.ProtectForLlm(template);
            if (string.IsNullOrEmpty(protectedText.Prompt))
                continue;

            if (!groupIndexByTemplate.TryGetValue(template, out var groupIndex))
            {
                groupIndex = orderedGroups.Count;
                groupIndexByTemplate[template] = groupIndex;
                orderedGroups.Add(new TemplateGroup(template));
            }

            orderedGroups[groupIndex].Occurrences.Add(
                new TargetOccurrence(
                    lineIndex,
                    string.IsNullOrEmpty(line.Speaker) ? null : line.Speaker,
                    protectedText));
        }

        if (orderedGroups.Count == 0)
            return Array.Empty<SceneBatchPlan>();

        var plans = new List<SceneBatchPlan>((orderedGroups.Count + chunkSize - 1) / chunkSize);
        var offset = 0;
        while (offset < orderedGroups.Count)
        {
            var count = Math.Min(chunkSize, orderedGroups.Count - offset);
            SceneBatchPlan? acceptedPlan = null;

            while (count > 0)
            {
                acceptedPlan = TryBuildPlanSlice(
                    sceneId,
                    sceneLines,
                    spanByTemplate,
                    orderedGroups,
                    offset,
                    count,
                    padding);
                if (acceptedPlan != null || count == 1)
                    break;
                count = Math.Max(1, count / 2);
            }

            if (acceptedPlan != null)
                plans.Add(acceptedPlan);
            offset += count;
        }

        return plans;
    }

    public bool TryParseResponse(
        string? response,
        out IReadOnlyDictionary<string, string> translations) =>
        TryParseResponse(response, out translations, out _);

    /// <summary>
    /// Validates a response against this batch and yields template-to-translated-template pairs
    /// for the entries that passed; a template sent more than once keeps its first valid
    /// translation. Returns false with a machine-readable <paramref name="rejectionReason"/> when
    /// the structure does not match.
    /// </summary>
    public bool TryParseResponse(
        string? response,
        out IReadOnlyDictionary<string, string> translations,
        out string rejectionReason)
    {
        translations = EmptyTranslations;
        if (string.IsNullOrWhiteSpace(response))
        {
            rejectionReason = "empty-response";
            return false;
        }

        if (!TranslationBudget.IsSceneWithinBudget(response))
        {
            rejectionReason = "response-over-budget";
            return false;
        }

        SceneResponsePayload? document;
        try
        {
            document = JsonSerializer.Deserialize<SceneResponsePayload>(StripCodeFence(response), JsonOptions);
        }
        catch (JsonException)
        {
            rejectionReason = "invalid-json";
            return false;
        }
        catch (ArgumentException)
        {
            rejectionReason = "invalid-json";
            return false;
        }

        if (document is null)
        {
            rejectionReason = "null-document";
            return false;
        }

        if (document.Version != ProtocolVersion)
        {
            rejectionReason = $"version-mismatch:expected={ProtocolVersion},actual={document.Version}";
            return false;
        }

        if (document.SceneId != SceneId)
        {
            rejectionReason = $"scene-id-mismatch:expected={SceneId},actual={document.SceneId}";
            return false;
        }

        if (document.Translations is null)
        {
            rejectionReason = "missing-translations";
            return false;
        }

        if (document.Translations.Count != targets.Count)
        {
            rejectionReason =
                $"target-count-mismatch:expected={targets.Count},actual={document.Translations.Count}";
            return false;
        }

        // Order and identity are checked so a missing, duplicated, or reordered entry rejects the
        // batch instead of shifting translations onto the wrong lines.
        for (var index = 0; index < targets.Count; index++)
        {
            if (document.Translations[index] is not { } entry
                || !string.Equals(entry.Id, targets[index].Id, StringComparison.Ordinal))
            {
                rejectionReason =
                    $"target-id-mismatch:index={index},expected={targets[index].Id}";
                return false;
            }
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
            {
                parsed[expected.Template] = translated;
            }
        }

        translations = parsed;
        rejectionReason = "ok";
        return true;
    }

    private static IReadOnlyDictionary<string, string> EmptyTranslations { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static List<TargetState> BuildTargetStates(
        IEnumerable<SceneDialogueLine> lines,
        out List<string> distinctTemplates)
    {
        var states = new List<TargetState>();
        distinctTemplates = new List<string>();
        var seenTemplates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (!TextTemplate.IsTranslationCandidate(line.Text))
                continue;

            var template = TextTemplate.Normalize(line.Text).Template;
            var protectedText = TextTemplate.ProtectForLlm(template);
            if (string.IsNullOrEmpty(protectedText.Prompt))
                continue;

            if (seenTemplates.Add(template))
                distinctTemplates.Add(template);

            states.Add(new TargetState(
                "t" + states.Count.ToString("D4"),
                template,
                string.IsNullOrEmpty(line.Speaker) ? null : line.Speaker,
                protectedText));
        }

        return states;
    }

    private static SceneBatchPlan? TryBuildPlanSlice(
        long sceneId,
        IReadOnlyList<SceneDialogueLine> sceneLines,
        IReadOnlyDictionary<string, (int MinLine, int MaxLine)> spanByTemplate,
        IReadOnlyList<TemplateGroup> orderedGroups,
        int offset,
        int count,
        int padding)
    {
        var templates = new List<string>(count);
        var occurrences = new List<(string Template, TargetOccurrence Item)>();
        var minLine = int.MaxValue;
        var maxLine = int.MinValue;

        for (var i = 0; i < count; i++)
        {
            var group = orderedGroups[offset + i];
            templates.Add(group.Template);
            if (spanByTemplate.TryGetValue(group.Template, out var span))
            {
                if (span.MinLine < minLine)
                    minLine = span.MinLine;
                if (span.MaxLine > maxLine)
                    maxLine = span.MaxLine;
            }

            foreach (var occ in group.Occurrences)
            {
                occurrences.Add((group.Template, occ));
                if (occ.LineIndex < minLine)
                    minLine = occ.LineIndex;
                if (occ.LineIndex > maxLine)
                    maxLine = occ.LineIndex;
            }
        }

        // Preserve exact story order among all occurrences in this chunk.
        occurrences.Sort(static (left, right) => left.Item.LineIndex.CompareTo(right.Item.LineIndex));
        var states = new List<TargetState>(occurrences.Count);
        for (var i = 0; i < occurrences.Count; i++)
        {
            var (template, occ) = occurrences[i];
            states.Add(new TargetState(
                "t" + i.ToString("D4"),
                template,
                occ.Speaker,
                occ.ProtectedText));
        }

        List<SceneContextPayload>? fullContext = null;
        if (minLine <= maxLine && sceneLines.Count > 0)
        {
            var startLine = Math.Max(0, minLine - padding);
            var endLine = Math.Min(sceneLines.Count - 1, maxLine + padding);
            fullContext = BuildContextSlice(sceneLines, startLine, endLine);
        }

        var batch = TryBuildBatch(sceneId, states, templates, fullContext, reserveRetryHeadroom: true);
        if (batch == null && fullContext != null)
        {
            var localContext = BuildLocalContextWindows(sceneLines, occurrences, padding);
            batch = TryBuildBatch(sceneId, states, templates, localContext, reserveRetryHeadroom: true)
                ?? TryBuildBatch(sceneId, states, templates, context: null, reserveRetryHeadroom: true);
        }

        if (batch == null && occurrences.Count > 1)
        {
            // When one repeated template has so many occurrences that repeating every target state
            // overflows the scene budget, deduplicate by (Template, Speaker), then by Template.
            var distinctSpeakerStates = DeduplicateStates(occurrences, includeSpeaker: true);
            batch = TryBuildBatch(sceneId, distinctSpeakerStates, templates, context: null, reserveRetryHeadroom: true);
            if (batch == null)
            {
                var singleTemplateStates = DeduplicateStates(occurrences, includeSpeaker: false);
                batch = TryBuildBatch(sceneId, singleTemplateStates, templates, context: null, reserveRetryHeadroom: true);
            }
        }

        return batch == null ? null : new SceneBatchPlan(batch, templates);
    }

    private static List<SceneContextPayload> BuildContextSlice(
        IReadOnlyList<SceneDialogueLine> sceneLines,
        int startLine,
        int endLine)
    {
        var context = new List<SceneContextPayload>(endLine - startLine + 1);
        for (var lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            var line = sceneLines[lineIndex];
            context.Add(new SceneContextPayload
            {
                Line = lineIndex,
                Name = string.IsNullOrEmpty(line.Speaker) ? null : line.Speaker,
                Text = TextTemplate.ProtectForLlm(TextTemplate.Normalize(line.Text).Template).Prompt,
            });
        }
        return context;
    }

    private static List<SceneContextPayload> BuildLocalContextWindows(
        IReadOnlyList<SceneDialogueLine> sceneLines,
        IReadOnlyList<(string Template, TargetOccurrence Item)> occurrences,
        int padding)
    {
        var indices = new SortedSet<int>();
        foreach (var (_, item) in occurrences)
        {
            var start = Math.Max(0, item.LineIndex - padding);
            var end = Math.Min(sceneLines.Count - 1, item.LineIndex + padding);
            for (var lineIndex = start; lineIndex <= end; lineIndex++)
                indices.Add(lineIndex);
        }

        var context = new List<SceneContextPayload>(indices.Count);
        foreach (var lineIndex in indices)
        {
            var line = sceneLines[lineIndex];
            context.Add(new SceneContextPayload
            {
                Line = lineIndex,
                Name = string.IsNullOrEmpty(line.Speaker) ? null : line.Speaker,
                Text = TextTemplate.ProtectForLlm(TextTemplate.Normalize(line.Text).Template).Prompt,
            });
        }
        return context;
    }

    private static List<TargetState> DeduplicateStates(
        IReadOnlyList<(string Template, TargetOccurrence Item)> occurrences,
        bool includeSpeaker)
    {
        var seen = new HashSet<(string Template, string? Speaker)>();
        var states = new List<TargetState>();
        foreach (var (template, item) in occurrences)
        {
            var key = (template, includeSpeaker ? item.Speaker : null);
            if (!seen.Add(key))
                continue;
            states.Add(new TargetState(
                "t" + states.Count.ToString("D4"),
                template,
                item.Speaker,
                item.ProtectedText));
        }
        return states;
    }

    private static SceneTranslationBatch? TryBuildBatch(
        long sceneId,
        IReadOnlyList<TargetState> states,
        IReadOnlyList<string> templates,
        List<SceneContextPayload>? context,
        bool reserveRetryHeadroom)
    {
        var prompt = JsonSerializer.Serialize(
            new SceneRequestPayload
            {
                Version = ProtocolVersion,
                SceneId = sceneId,
                Context = context,
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

        if (!TranslationBudget.IsSceneWithinBudget(prompt))
            return null;

        if (reserveRetryHeadroom
            && (prompt.Length > TranslationBudget.MaxSceneUtf16CodeUnits - RetryHeaderReserveChars
                || StrictUtf8.GetByteCount(prompt) > TranslationBudget.MaxSceneUtf8Bytes - RetryHeaderReserveBytes))
        {
            return null;
        }

        return new SceneTranslationBatch(sceneId, prompt, states, templates);
    }

    private static string StripOptionalContext(string basePrompt)
    {
        try
        {
            var document = JsonSerializer.Deserialize<SceneRequestPayload>(basePrompt, JsonOptions);
            if (document?.Context is not { Count: > 0 })
                return basePrompt;
            document.Context = null;
            return JsonSerializer.Serialize(document, JsonOptions);
        }
        catch (JsonException)
        {
            return basePrompt;
        }
    }

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

    private sealed class TemplateGroup
    {
        public TemplateGroup(string template) => Template = template;

        public string Template { get; }
        public List<TargetOccurrence> Occurrences { get; } = new();
    }

    private readonly record struct TargetOccurrence(
        int LineIndex,
        string? Speaker,
        LlmProtectedText ProtectedText);

    private sealed record TargetState(
        string Id,
        string Template,
        string? Speaker,
        LlmProtectedText ProtectedText);

    private sealed class SceneRequestPayload
    {
        public int Version { get; set; }
        public long SceneId { get; set; }
        public List<SceneContextPayload>? Context { get; set; }
        public List<SceneTargetPayload> Targets { get; set; } = new();
    }

    private sealed class SceneContextPayload
    {
        public int Line { get; set; }
        public string? Name { get; set; }
        public string Text { get; set; } = string.Empty;
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
