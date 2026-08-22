using System.Text.Json;

namespace MuvluvLLMMod;

public sealed class StaticTranslationCatalog
{
    private readonly Dictionary<(string MasterType, string FieldPath, string Original), string> curated;

    private StaticTranslationCatalog(
        Dictionary<(string MasterType, string FieldPath, string Original), string> curated,
        string[] unchangedTemplates)
    {
        this.curated = curated;
        UnchangedTemplates = unchangedTemplates;
    }

    public IReadOnlyList<string> UnchangedTemplates { get; }

    public bool TryGet(string masterType, string fieldPath, string original, out string translated) =>
        curated.TryGetValue((masterType, fieldPath, original), out translated!);

    public static StaticTranslationCatalog Parse(string json)
    {
        var curated = new Dictionary<(string, string, string), string>();
        var unchanged = new List<string>();
        var unchangedSet = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Empty();
            foreach (var master in document.RootElement.EnumerateObject())
            {
                if (master.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in master.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var entry in field.Value.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.String) continue;
                        var translated = entry.Value.GetString();
                        if (string.IsNullOrEmpty(translated)) continue;
                        if (!string.Equals(entry.Name, translated, StringComparison.Ordinal))
                        {
                            curated[(master.Name, field.Name, entry.Name)] = translated;
                            continue;
                        }
                        if (!TextTemplate.IsTranslationCandidate(entry.Name)) continue;
                        var template = TextTemplate.Normalize(entry.Name).Template;
                        if (unchangedSet.Add(template)) unchanged.Add(template);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return Empty();
        }
        return new StaticTranslationCatalog(curated, unchanged.ToArray());
    }

    public static StaticTranslationCatalog Empty() =>
        new(new Dictionary<(string, string, string), string>(), Array.Empty<string>());
}
