using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MuvluvLLMMod;

/// <summary>
/// Reads and rewrites the dialogue text inside a <c>SceneFrameMaster.ConfigurationJson</c> frame
/// document.
///
/// Evidence (see docs/scene-frame-evidence.md): the game reads this string exactly once, in
/// <c>ScenarioController+&lt;&gt;c__DisplayClass113_0.&lt;GenerateFrames&gt;b__0</c>, which runs
/// <c>JsonConvert.DeserializeObject&lt;ScenarioConfiguration&gt;</c> and copies
/// <c>ScenarioConfiguration.Phrase</c> onto each frame view model. Rewriting the document before
/// that call is therefore the single write that reaches every consumer, and no later stage needs
/// a second pass.
///
/// The document is edited as a JSON tree so unrelated fields survive byte-for-byte in meaning;
/// only <c>configuration.Phrase.Text</c> is replaced.
///
/// Pure logic: no game or Harmony types, so the loader-free test project covers it.
/// </summary>
public static class SceneFrameDocument
{
    // ConfigurationJson deserializes directly into ScenarioConfiguration, so Phrase sits at the
    // document root. Evidence: ScenarioConfiguration exposes get_Phrase/set_Phrase, and
    // <GenerateFrames>b__0 passes the whole string to
    // JsonConvert.DeserializeObject<ScenarioConfiguration>.
    private const string PhraseProperty = "Phrase";
    private const string TextProperty = "Text";

    // The game deserializes this document with Newtonsoft.Json, which leaves non-ASCII and markup
    // literal. Matching that keeps the rewritten document compact and its ruby tags readable.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Extracts every dialogue text in the document that is a translation candidate. Returns an
    /// empty list when the document is absent, unparsable, or carries no candidates.
    /// </summary>
    public static IReadOnlyList<string> CollectTexts(string? configurationJson)
    {
        var texts = new List<string>();
        foreach (var phrase in EnumeratePhrases(configurationJson))
        {
            if (phrase[TextProperty] is not JsonValue value)
                continue;

            string? text;
            try
            {
                text = value.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                // A non-string Text is left untouched rather than coerced.
                continue;
            }

            if (TextTemplate.IsTranslationCandidate(text))
                texts.Add(text);
        }

        return texts;
    }

    /// <summary>
    /// Returns the document with each candidate dialogue text replaced by its translation. Texts
    /// without a translation keep their original value, so a partial result can never blank a
    /// line. Returns null when nothing changed, which lets the caller skip the write entirely.
    /// </summary>
    public static string? TryRewrite(string? configurationJson, IReadOnlyDictionary<string, string>? translations)
    {
        if (string.IsNullOrEmpty(configurationJson)
            || translations is null
            || translations.Count == 0)
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(configurationJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject document)
            return null;

        var changed = false;
        if (document[PhraseProperty] is JsonObject phrase
            && phrase[TextProperty] is JsonValue value)
        {
            string? text;
            try
            {
                text = value.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            if (translations.TryGetValue(text, out var translated)
                && !string.IsNullOrEmpty(translated)
                && !string.Equals(text, translated, StringComparison.Ordinal))
            {
                phrase[TextProperty] = translated;
                changed = true;
            }
        }

        return changed ? document.ToJsonString(WriteOptions) : null;
    }

    private static IEnumerable<JsonObject> EnumeratePhrases(string? configurationJson)
    {
        if (string.IsNullOrEmpty(configurationJson))
            yield break;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(configurationJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (root is JsonObject document
            && document[PhraseProperty] is JsonObject phrase)
            yield return phrase;
    }
}
