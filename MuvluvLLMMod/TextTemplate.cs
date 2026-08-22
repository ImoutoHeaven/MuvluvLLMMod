using System.Text.RegularExpressions;

namespace MuvluvLLMMod;

public sealed class LlmProtectedText
{
    internal LlmProtectedText(string prompt, string[] tokens, string[] originals)
    {
        Prompt = prompt;
        Tokens = tokens;
        Originals = originals;
    }

    public string Prompt { get; }
    internal string[] Tokens { get; }
    internal string[] Originals { get; }
}

public static class TextTemplate
{
    private const int MaxPlaceholderIndex = 9999;
    private static readonly Regex Number = new(@"<.*?>|\{[0-9]+\}|[0-9]+(?:\.[0-9]+)?", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"\{([0-9]+)\}", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<.*?>", RegexOptions.Compiled);
    private static readonly Regex LineBreak = new(@"\\r\\n|\\n|\r\n|\n", RegexOptions.Compiled);
    private static readonly Regex FormatToken = new(@"<.*?>|\{[0-9]+\}|\\r\\n|\\n|\r\n|\n", RegexOptions.Compiled);
    private static readonly Regex ProtectedToken = new(@"__MLM_FMT_[A-Za-z0-9]+__", RegexOptions.Compiled);

    public static (string Template, string[] Values, int NumericPlaceholderStart) Normalize(string text)
    {
        if (!TranslationBudget.IsTextWithinBudget(text))
            return (text, Array.Empty<string>(), -1);

        var placeholders = Placeholder.Matches(text);
        if (!TryGetPlaceholderIndices(placeholders, out var indices)) return (text, Array.Empty<string>(), -1);
        var values = new List<string>();
        var nextPlaceholder = indices.DefaultIfEmpty(-1).Max() + 1;
        var numericCount = Number.Matches(text).Count(match =>
            !match.Value.StartsWith("<", StringComparison.Ordinal)
            && !match.Value.StartsWith("{", StringComparison.Ordinal));
        if (numericCount > 0 && (nextPlaceholder > MaxPlaceholderIndex || numericCount > MaxPlaceholderIndex - nextPlaceholder + 1))
        {
            return (text, Array.Empty<string>(), -1);
        }
        var template = Number.Replace(text, match =>
        {
            if (match.Value.StartsWith("<", StringComparison.Ordinal)
                || match.Value.StartsWith("{", StringComparison.Ordinal)) return match.Value;
            var index = nextPlaceholder + values.Count;
            values.Add(match.Value);
            return "{" + index + "}";
        });
        return (template, values.ToArray(), nextPlaceholder);
    }

    public static string? Fill(string translatedTemplate, string[] values)
    {
        if (!TranslationBudget.IsTextWithinBudget(translatedTemplate)
            || values.Any(value => !TranslationBudget.IsTextWithinBudget(value)))
            return null;
        if (values.Length == 0) return translatedTemplate;
        return Fill(translatedTemplate, values, 0);
    }

    public static string? Fill(string translatedTemplate, string[] values, int numericPlaceholderStart)
    {
        if (!TranslationBudget.IsTextWithinBudget(translatedTemplate)
            || values.Any(value => !TranslationBudget.IsTextWithinBudget(value))
            || numericPlaceholderStart < 0) return null;
        var matches = Placeholder.Matches(translatedTemplate);
        if (!TryGetPlaceholderIndices(matches, out var indices)) return null;
        if (values.Length > 0
            && (numericPlaceholderStart > MaxPlaceholderIndex
                || values.Length > MaxPlaceholderIndex - numericPlaceholderStart + 1)) return null;
        var numeric = indices.Where(index => index >= numericPlaceholderStart).ToArray();
        if (!numeric.SequenceEqual(Enumerable.Range(numericPlaceholderStart, values.Length))) return null;
        return Placeholder.Replace(translatedTemplate, match =>
        {
            _ = int.TryParse(match.Groups[1].Value, out var index);
            return index < numericPlaceholderStart ? match.Value : values[index - numericPlaceholderStart];
        });
    }

    public static bool HasSamePlaceholders(string sourceTemplate, string translatedTemplate)
    {
        if (!TranslationBudget.IsTextWithinBudget(sourceTemplate)
            || !TranslationBudget.IsTextWithinBudget(translatedTemplate))
            return false;

        var source = Placeholder.Matches(sourceTemplate);
        var translated = Placeholder.Matches(translatedTemplate);
        return TryGetPlaceholderIndices(source, out _)
            && TryGetPlaceholderIndices(translated, out _)
            && source.Select(match => match.Value)
                .SequenceEqual(translated.Select(match => match.Value), StringComparer.Ordinal);
    }

    public static bool HasSameMarkup(string source, string translated)
    {
        if (!TranslationBudget.IsTextWithinBudget(source)
            || !TranslationBudget.IsTextWithinBudget(translated))
            return false;

        if (!Tag.Matches(source).Select(match => match.Value)
            .SequenceEqual(Tag.Matches(translated).Select(match => match.Value), StringComparer.Ordinal)) return false;
        if (!LineBreak.Matches(source).Select(match => match.Value)
            .SequenceEqual(LineBreak.Matches(translated).Select(match => match.Value), StringComparer.Ordinal)) return false;

        var sourceLines = LineBreak.Split(source);
        var translatedLines = LineBreak.Split(translated);
        if (sourceLines.Length != translatedLines.Length) return false;
        for (var index = 0; index < sourceLines.Length; index++)
        {
            if (sourceLines[index].Length == 0 != (translatedLines[index].Length == 0)) return false;
        }
        return true;
    }

    public static bool IsTranslationCandidate(string text)
    {
        if (!TranslationBudget.IsTextWithinBudget(text) || string.IsNullOrWhiteSpace(text)) return false;
        foreach (var character in text)
        {
            if ((character >= '\u3041' && character <= '\u3096')
                || (character >= '\u309d' && character <= '\u309f')
                || (character >= '\u30a1' && character <= '\u30fa')
                || (character >= '\u30fd' && character <= '\u30ff')
                || (character >= '\uff66' && character <= '\uff9d')) return true;
        }
        return false;
    }

    public static LlmProtectedText ProtectForLlm(string text)
    {
        if (!TranslationBudget.IsTextWithinBudget(text))
            return new LlmProtectedText(string.Empty, Array.Empty<string>(), Array.Empty<string>());

        var tokens = new List<string>();
        var originals = new List<string>();
        var prompt = FormatToken.Replace(text, match =>
        {
            var token = "__MLM_FMT_" + tokens.Count + "__";
            tokens.Add(token);
            originals.Add(match.Value);
            return token;
        });
        return new LlmProtectedText(prompt, tokens.ToArray(), originals.ToArray());
    }

    public static bool TryRestoreLlm(string response, LlmProtectedText protectedText, out string restored)
    {
        restored = string.Empty;
        if (!TranslationBudget.IsTextWithinBudget(response)
            || !TranslationBudget.IsTextWithinBudget(protectedText.Prompt)
            || FormatToken.IsMatch(response)) return false;
        if (!protectedText.Tokens.SequenceEqual(
                ProtectedToken.Matches(response).Select(match => match.Value),
                StringComparer.Ordinal)) return false;

        restored = response;
        for (var index = 0; index < protectedText.Tokens.Length; index++)
        {
            restored = restored.Replace(protectedText.Tokens[index], protectedText.Originals[index], StringComparison.Ordinal);
        }
        return TranslationBudget.IsTextWithinBudget(restored);
    }

    private static bool TryGetPlaceholderIndices(MatchCollection matches, out int[] indices)
    {
        indices = new int[matches.Count];
        for (var index = 0; index < matches.Count; index++)
        {
            if (!int.TryParse(matches[index].Groups[1].Value, out var value) || value > MaxPlaceholderIndex)
            {
                indices = Array.Empty<int>();
                return false;
            }
            indices[index] = value;
        }
        return true;
    }
}
