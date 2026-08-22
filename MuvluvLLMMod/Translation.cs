namespace MuvluvLLMMod;

/// <summary>
/// Resolves rendered text through the local machine-translation cache and queue.
/// </summary>
public static class Translation
{
    [ThreadStatic]
    private static EnqueueObservation lastEnqueueObservation;

    public static EnqueueObservation LastEnqueueObservation => lastEnqueueObservation;
    public static bool LastResolveAcceptedByScheduler => lastEnqueueObservation.AcceptedByScheduler;

    public static string ResolveAny(string original, bool enqueue = true)
    {
        lastEnqueueObservation = default;
        if (!Config.Translation.Value || string.IsNullOrEmpty(original))
            return original;

        Core.BeginEnqueueObservation();
        try
        {
            return enqueue
                ? Core.Resolver.Resolve(original, enabled: true, priority: Patch.isPlayingScenario)
                : Core.Resolver.Lookup(original, enabled: true);
        }
        finally
        {
            lastEnqueueObservation = CaptureObservation(
                original,
                Core.EndEnqueueObservation());
        }
    }

    public static bool ObserveForTranslation(string original, bool priority)
    {
        lastEnqueueObservation = default;
        if (string.IsNullOrEmpty(original))
            return false;

        Core.BeginEnqueueObservation();
        try
        {
            // This intentionally ignores the returned translation: F2 controls display, not production.
            _ = Core.Resolver.Resolve(original, enabled: true, priority: priority);
        }
        finally
        {
            lastEnqueueObservation = CaptureObservation(
                original,
                Core.EndEnqueueObservation());
        }
        return lastEnqueueObservation.AcceptedByScheduler;
    }

    private static EnqueueObservation CaptureObservation(string original, bool acceptedByScheduler)
    {
        var source = Core.Cache.TryGetSourceForTranslatedValue(original, out var resolvedSource)
            ? resolvedSource
            : original;
        var durablyPending = TextTemplate.IsTranslationCandidate(source)
            && Core.Cache.TryGetPendingGeneration(TextTemplate.Normalize(source).Template, out _);
        return new EnqueueObservation(durablyPending, acceptedByScheduler);
    }
}
