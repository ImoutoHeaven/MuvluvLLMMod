namespace MuvluvLLMMod;

public sealed class TranslationResolver
{
    private readonly TranslationCache cache;
    private readonly Action<string, long> enqueuePriority;
    private readonly Action<string, long> enqueueNormal;
    private readonly Action<string, long>? cancel;

    public TranslationResolver(
        TranslationCache cache,
        Action<string> enqueuePriority,
        Action<string> enqueueNormal,
        Action<string>? cancel = null)
    {
        this.cache = cache;
        this.enqueuePriority = (template, _) => enqueuePriority(template);
        this.enqueueNormal = (template, _) => enqueueNormal(template);
        this.cancel = cancel == null ? null : (template, _) => cancel(template);
    }

    public TranslationResolver(
        TranslationCache cache,
        Action<string> enqueuePriority,
        Action<string> enqueueNormal,
        Action<string, long> cancel)
    {
        this.cache = cache;
        this.enqueuePriority = (template, _) => enqueuePriority(template);
        this.enqueueNormal = (template, _) => enqueueNormal(template);
        this.cancel = cancel;
    }

    public TranslationResolver(
        TranslationCache cache,
        Action<string, long> enqueuePriority,
        Action<string, long> enqueueNormal,
        Action<string, long>? cancel = null)
    {
        this.cache = cache;
        this.enqueuePriority = enqueuePriority;
        this.enqueueNormal = enqueueNormal;
        this.cancel = cancel;
    }

    public string Resolve(string original, string? curated = null, bool enabled = true, bool priority = true)
    {
        return ResolveCore(original, curated, enabled, priority, false, true);
    }

    public string ResolveKnownSource(string original, string? curated = null, bool enabled = true, bool priority = true)
    {
        return ResolveCore(original, curated, enabled, priority, true, true);
    }

    public string Lookup(string original, string? curated = null, bool enabled = true) =>
        ResolveCore(original, curated, enabled, true, false, false);

    public string LookupKnownSource(string original, string? curated = null, bool enabled = true) =>
        ResolveCore(original, curated, enabled, true, true, false);

    private string ResolveCore(string original, string? curated, bool enabled, bool priority, bool knownSource, bool enqueue)
    {
        if (!enabled || string.IsNullOrEmpty(original)) return original;
        if (knownSource) cache.ConfirmSourceIdentity(original);
        var source = !knownSource && cache.TryGetSourceForTranslatedValue(original, out var resolvedSource) ? resolvedSource : original;
        var alreadyResolved = !string.Equals(source, original, StringComparison.Ordinal);
        if (!knownSource && !alreadyResolved && cache.IsKnownTranslatedValue(original)) return original;
        if (!string.IsNullOrEmpty(curated) && !string.Equals(curated, source, StringComparison.Ordinal))
        {
            var template = TextTemplate.Normalize(source).Template;
            if (string.Equals(template, source, StringComparison.Ordinal)
                && cache.CancelPending(template, out var pendingGeneration))
                cancel?.Invoke(template, pendingGeneration);
            cache.RememberResolution(source, curated);
            return curated;
        }
        if (alreadyResolved) return original;

        var normalized = TextTemplate.Normalize(source);
        if (cache.TryGetGenerated(normalized.Template, out var translatedTemplate))
        {
            var valid = TextTemplate.HasSamePlaceholders(normalized.Template, translatedTemplate)
                && TextTemplate.HasSameMarkup(normalized.Template, translatedTemplate);
            var filled = valid ? TextTemplate.Fill(translatedTemplate, normalized.Values, normalized.NumericPlaceholderStart) : null;
            if (filled != null)
            {
                cache.RememberResolution(source, filled);
                return filled;
            }
            cache.RemoveGenerated(normalized.Template);
        }

        if (!enqueue || !TextTemplate.IsTranslationCandidate(source)) return original;
        if (priority)
        {
            var observation = cache.ObservePriorityGeneration(source, normalized.Template);
            if (observation.ShouldEnqueue) enqueuePriority(normalized.Template, observation.Generation);
        }
        else
        {
            var observation = cache.ObserveNormalGeneration(source, normalized.Template);
            if (observation.ShouldEnqueue) enqueueNormal(normalized.Template, observation.Generation);
        }
        return original;
    }

    public string ResolveFromDictionary(string original, IReadOnlyDictionary<string, string>? curated, bool enabled = true)
    {
        if (curated == null) return original;
        if (curated != null && curated.TryGetValue(original, out var exact)) return ResolveKnownSource(original, exact, enabled);
        var source = cache.TryGetSourceForTranslatedValue(original, out var resolvedSource) ? resolvedSource : original;
        return Resolve(original, curated != null && curated.TryGetValue(source, out var value) ? value : null, enabled);
    }

    public void PublishCurated(IReadOnlyDictionary<string, string> curated)
    {
        foreach (var entry in curated)
        {
            if (!string.IsNullOrEmpty(entry.Value) && !string.Equals(entry.Key, entry.Value, StringComparison.Ordinal))
            {
                ResolveKnownSource(entry.Key, entry.Value, true);
            }
        }
    }

    public string ResolveStatic(
        string original,
        StaticTranslationCatalog catalog,
        string masterType,
        string fieldPath,
        bool enabled = true)
    {
        if (catalog.TryGet(masterType, fieldPath, original, out var exact)) return ResolveKnownSource(original, exact, enabled);
        var source = cache.TryGetSourceForTranslatedValue(original, out var resolvedSource) ? resolvedSource : original;
        return Resolve(original, catalog.TryGet(masterType, fieldPath, source, out var value) ? value : null, enabled);
    }

    public void ObserveNormal(string template)
    {
        if (!TextTemplate.IsTranslationCandidate(template)) return;
        var observation = cache.ObserveNormalGeneration(template, template);
        if (observation.ShouldEnqueue) enqueueNormal(template, observation.Generation);
    }
}
