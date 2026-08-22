namespace MuvluvLLMMod;

public sealed class TranslationPriorityBacklog
{
    public readonly record struct Work(string Template, long PendingGeneration);
    public readonly record struct BudgetSnapshot(
        int ScheduledCount,
        long ScheduledUtf8Bytes,
        int CanceledPendingCount,
        long CanceledPendingUtf8Bytes);
    private readonly record struct Entry(string Template, long Generation, long PendingGeneration);

    private readonly object gate = new();
    private readonly Queue<Entry> queue = new();
    private readonly Dictionary<string, Entry> scheduled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> canceledPendingThrough = new(StringComparer.Ordinal);
    private long scheduledUtf8Bytes;
    private long canceledPendingUtf8Bytes;
    private long nextGeneration;

    public BudgetSnapshot RetainedSnapshot
    {
        get
        {
            lock (gate)
            {
                return new BudgetSnapshot(
                    scheduled.Count,
                    scheduledUtf8Bytes,
                    canceledPendingThrough.Count,
                    canceledPendingUtf8Bytes);
            }
        }
    }

    public bool Enqueue(string template) => Enqueue(template, 0);

    public bool Enqueue(string template, long pendingGeneration)
    {
        if (!TextTemplate.IsTranslationCandidate(template)) return false;
        lock (gate)
        {
            if (IsPendingCanceledUnsafe(template, pendingGeneration)) return false;
            if (scheduled.TryGetValue(template, out var existing))
            {
                if (pendingGeneration <= existing.PendingGeneration) return false;
                RemoveGenerationUnsafe(template, existing.Generation);
            }
            else if (scheduled.Count >= TranslationBudget.MaxPriorityBacklogItems
                || scheduledUtf8Bytes + Utf8Bytes(template) > TranslationBudget.MaxPriorityBacklogUtf8Bytes)
                return false;

            RemoveCanceledPendingUnsafe(template);
            var generation = ++nextGeneration;
            var entry = new Entry(template, generation, pendingGeneration);
            scheduled[template] = entry;
            scheduledUtf8Bytes += Utf8Bytes(template);
            queue.Enqueue(entry);
            return true;
        }
    }

    public string[] Drain() => DrainWork().Select(entry => entry.Template).ToArray();

    public Work[] DrainWork()
    {
        lock (gate)
        {
            var entries = queue.ToArray();
            queue.Clear();
            scheduled.Clear();
            scheduledUtf8Bytes = 0;
            return entries.Select(entry => new Work(entry.Template, entry.PendingGeneration)).ToArray();
        }
    }

    public bool Cancel(string template) => Cancel(template, long.MaxValue);

    public bool Cancel(string template, long pendingGeneration)
    {
        lock (gate)
        {
            RecordPendingCancellationUnsafe(template, pendingGeneration);
            return RemoveThroughUnsafe(template, nextGeneration, pendingGeneration);
        }
    }

    public (bool Removed, long Cutoff) CancelAndGetCutoff(string template) =>
        CancelAndGetCutoff(template, long.MaxValue);

    public (bool Removed, long Cutoff) CancelAndGetCutoff(string template, long pendingGeneration)
    {
        lock (gate)
        {
            RecordPendingCancellationUnsafe(template, pendingGeneration);
            var cutoff = nextGeneration;
            return (RemoveThroughUnsafe(template, cutoff, pendingGeneration), cutoff);
        }
    }

    public void CancelThrough(string template, long cutoff) =>
        CancelThrough(template, cutoff, long.MaxValue);

    public void CancelThrough(string template, long cutoff, long pendingGeneration)
    {
        lock (gate)
        {
            RecordPendingCancellationUnsafe(template, pendingGeneration);
            RemoveThroughUnsafe(template, cutoff, pendingGeneration);
        }
    }

    private bool RemoveThroughUnsafe(string template, long cutoff, long pendingGeneration)
    {
        var removed = false;
        var count = queue.Count;
        for (var index = 0; index < count; index++)
        {
            var current = queue.Dequeue();
            var remove = current.PendingGeneration > 0
                ? current.PendingGeneration <= pendingGeneration
                : current.Generation <= cutoff;
            if (string.Equals(current.Template, template, StringComparison.Ordinal) && remove)
            {
                removed = true;
                if (scheduled.TryGetValue(template, out var scheduledEntry)
                    && scheduledEntry.Generation == current.Generation)
                    RemoveScheduledUnsafe(template);
            }
            else queue.Enqueue(current);
        }
        return removed;
    }

    private void RemoveGenerationUnsafe(string template, long generation)
    {
        var count = queue.Count;
        for (var index = 0; index < count; index++)
        {
            var current = queue.Dequeue();
            if (!string.Equals(current.Template, template, StringComparison.Ordinal)
                || current.Generation != generation)
                queue.Enqueue(current);
        }
        RemoveScheduledUnsafe(template);
    }

    private void RemoveScheduledUnsafe(string template)
    {
        if (!scheduled.Remove(template)) return;
        scheduledUtf8Bytes -= Utf8Bytes(template);
        if (scheduledUtf8Bytes < 0) scheduledUtf8Bytes = 0;
    }

    private bool IsPendingCanceledUnsafe(string template, long pendingGeneration) =>
        pendingGeneration > 0
        && canceledPendingThrough.TryGetValue(template, out var cutoff)
        && pendingGeneration <= cutoff;

    private void RecordPendingCancellationUnsafe(string template, long pendingGeneration)
    {
        if (pendingGeneration <= 0 || pendingGeneration == long.MaxValue
            || !TextTemplate.IsTranslationCandidate(template)) return;
        if (canceledPendingThrough.TryGetValue(template, out var cutoff))
        {
            if (pendingGeneration > cutoff) canceledPendingThrough[template] = pendingGeneration;
            return;
        }
        var bytes = Utf8Bytes(template);
        if (canceledPendingThrough.Count >= TranslationBudget.MaxCancellationEntries
            || canceledPendingUtf8Bytes + bytes > TranslationBudget.MaxCancellationUtf8Bytes)
            return;
        canceledPendingThrough[template] = pendingGeneration;
        canceledPendingUtf8Bytes += bytes;
    }

    private void RemoveCanceledPendingUnsafe(string template)
    {
        if (!canceledPendingThrough.Remove(template)) return;
        canceledPendingUtf8Bytes -= Utf8Bytes(template);
        if (canceledPendingUtf8Bytes < 0) canceledPendingUtf8Bytes = 0;
    }

    private static int Utf8Bytes(string template)
    {
        TranslationBudget.TryGetTextBytes(template, out var bytes);
        return bytes;
    }
}
