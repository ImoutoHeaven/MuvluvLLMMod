namespace MuvluvLLMMod;

/// <summary>
/// Resolves rendered text through the local machine-translation cache and queue.
/// </summary>
public static class Translation
{
    [ThreadStatic]
    private static bool lastResolveEnqueued;

    public static bool LastResolveEnqueued => lastResolveEnqueued;

    public static string ResolveAny(string original, bool enqueue = true)
    {
        lastResolveEnqueued = false;
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
            lastResolveEnqueued = Core.EndEnqueueObservation();
        }
    }

    public static bool ObserveForTranslation(string original, bool priority)
    {
        lastResolveEnqueued = false;
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
            lastResolveEnqueued = Core.EndEnqueueObservation();
        }
        return lastResolveEnqueued;
    }
}
