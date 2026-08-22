namespace MuvluvLLMMod;

public sealed class TranslationProgress
{
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<string>> failedTemplates = new(StringComparer.Ordinal);
    private readonly LinkedList<string> failedLru = new();
    private long failedUtf8Bytes;
    private int completed;
    private int inFlight;

    public void Start() => Interlocked.Increment(ref inFlight);

    public void Complete(string template)
    {
        Interlocked.Decrement(ref inFlight);
        Interlocked.Increment(ref completed);
        lock (gate) RemoveFailedUnsafe(template);
    }

    public void Fail(string template)
    {
        Interlocked.Decrement(ref inFlight);
        if (!TextTemplate.IsTranslationCandidate(template)) return;
        lock (gate)
        {
            if (failedTemplates.TryGetValue(template, out var existing))
            {
                failedLru.Remove(existing);
                failedLru.AddLast(existing);
                return;
            }

            var bytes = Utf8Bytes(template);
            if (bytes > TranslationBudget.MaxFailedProgressUtf8Bytes) return;
            while (failedTemplates.Count >= TranslationBudget.MaxFailedProgressEntries
                || failedUtf8Bytes + bytes > TranslationBudget.MaxFailedProgressUtf8Bytes)
            {
                if (failedLru.First == null) return;
                var oldest = failedLru.First.Value;
                failedLru.RemoveFirst();
                failedTemplates.Remove(oldest);
                failedUtf8Bytes -= Utf8Bytes(oldest);
            }
            var node = failedLru.AddLast(template);
            failedTemplates[template] = node;
            failedUtf8Bytes += bytes;
        }
    }

    public (int Completed, int InFlight, int Failed) Snapshot() =>
        (Volatile.Read(ref completed), Volatile.Read(ref inFlight), FailedCount);

    public int FailedCount
    {
        get { lock (gate) return failedTemplates.Count; }
    }

    public long RetainedUtf8Bytes
    {
        get { lock (gate) return failedUtf8Bytes; }
    }

    private void RemoveFailedUnsafe(string template)
    {
        if (!failedTemplates.Remove(template, out var node)) return;
        failedLru.Remove(node);
        failedUtf8Bytes -= Utf8Bytes(template);
        if (failedUtf8Bytes < 0) failedUtf8Bytes = 0;
    }

    private static int Utf8Bytes(string template)
    {
        TranslationBudget.TryGetTextBytes(template, out var bytes);
        return bytes;
    }
}
