using System.Collections.Concurrent;

namespace MuvluvLLMMod;

public sealed class TranslationProgress
{
    private readonly ConcurrentDictionary<string, byte> failedTemplates = new(StringComparer.Ordinal);
    private int completed;
    private int inFlight;

    public void Start() => Interlocked.Increment(ref inFlight);

    public void Complete(string template)
    {
        Interlocked.Decrement(ref inFlight);
        Interlocked.Increment(ref completed);
        failedTemplates.TryRemove(template, out _);
    }

    public void Fail(string template)
    {
        Interlocked.Decrement(ref inFlight);
        failedTemplates.TryAdd(template, 0);
    }

    public (int Completed, int InFlight, int Failed) Snapshot() =>
        (Volatile.Read(ref completed), Volatile.Read(ref inFlight), failedTemplates.Count);
}
