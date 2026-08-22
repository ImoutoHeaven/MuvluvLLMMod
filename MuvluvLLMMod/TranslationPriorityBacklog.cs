namespace MuvluvLLMMod;

public sealed class TranslationPriorityBacklog
{
    public readonly record struct Work(string Template, long PendingGeneration);
    private readonly record struct Entry(string Template, long Generation, long PendingGeneration);

    private readonly object gate = new();
    private readonly Queue<Entry> queue = new();
    private readonly Dictionary<string, Entry> scheduled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> canceledPendingThrough = new(StringComparer.Ordinal);
    private long nextGeneration;

    public bool Enqueue(string template)
    {
        return Enqueue(template, 0);
    }

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
            canceledPendingThrough.Remove(template);
            var generation = ++nextGeneration;
            var entry = new Entry(template, generation, pendingGeneration);
            scheduled[template] = entry;
            queue.Enqueue(entry);
            return true;
        }
    }

    public string[] Drain()
    {
        return DrainWork().Select(entry => entry.Template).ToArray();
    }

    public Work[] DrainWork()
    {
        lock (gate)
        {
            var entries = queue.ToArray();
            queue.Clear();
            scheduled.Clear();
            return entries.Select(entry => new Work(entry.Template, entry.PendingGeneration)).ToArray();
        }
    }

    public bool Cancel(string template)
    {
        return Cancel(template, long.MaxValue);
    }

    public bool Cancel(string template, long pendingGeneration)
    {
        lock (gate)
        {
            RecordPendingCancellationUnsafe(template, pendingGeneration);
            return RemoveThroughUnsafe(template, nextGeneration, pendingGeneration);
        }
    }

    public (bool Removed, long Cutoff) CancelAndGetCutoff(string template)
    {
        return CancelAndGetCutoff(template, long.MaxValue);
    }

    public (bool Removed, long Cutoff) CancelAndGetCutoff(string template, long pendingGeneration)
    {
        lock (gate)
        {
            RecordPendingCancellationUnsafe(template, pendingGeneration);
            var cutoff = nextGeneration;
            return (RemoveThroughUnsafe(template, cutoff, pendingGeneration), cutoff);
        }
    }

    public void CancelThrough(string template, long cutoff)
    {
        CancelThrough(template, cutoff, long.MaxValue);
    }

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
                    scheduled.Remove(template);
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
    }

    private bool IsPendingCanceledUnsafe(string template, long pendingGeneration) =>
        pendingGeneration > 0
        && canceledPendingThrough.TryGetValue(template, out var cutoff)
        && pendingGeneration <= cutoff;

    private void RecordPendingCancellationUnsafe(string template, long pendingGeneration)
    {
        if (pendingGeneration <= 0 || pendingGeneration == long.MaxValue) return;
        if (!canceledPendingThrough.TryGetValue(template, out var cutoff) || pendingGeneration > cutoff)
            canceledPendingThrough[template] = pendingGeneration;
    }
}
