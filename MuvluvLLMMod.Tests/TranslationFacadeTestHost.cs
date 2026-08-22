namespace MuvluvLLMMod;

// Loader-free test doubles for the small static adapter in Translation.cs.
public sealed class TranslationTestConfigEntry<T>
{
    public TranslationTestConfigEntry(T value) => Value = value;

    public T Value { get; set; }
}

public static class Config
{
    public static TranslationTestConfigEntry<bool> Translation { get; } = new(true);
}

public static class Patch
{
    public static bool isPlayingScenario;
}

public static class Core
{
    [ThreadStatic]
    private static bool observing;

    [ThreadStatic]
    private static bool accepted;

    public static TranslationCache Cache { get; set; } = null!;
    public static TranslationResolver Resolver { get; set; } = null!;
    public static bool AcceptEnqueues { get; set; } = true;

    public static void BeginEnqueueObservation()
    {
        observing = true;
        accepted = false;
    }

    public static bool EndEnqueueObservation()
    {
        var result = accepted;
        observing = false;
        accepted = false;
        return result;
    }

    public static void MarkEnqueueAccepted()
    {
        if (observing && AcceptEnqueues)
            accepted = true;
    }
}
