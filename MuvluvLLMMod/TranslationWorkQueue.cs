namespace MuvluvLLMMod;

public sealed class TranslationWorkQueue
{
    public readonly record struct Work(string Template, bool Priority, long Generation, long PendingGeneration);

    private readonly object gate = new();
    private readonly Queue<Work> priorityQueue = new();
    private readonly Queue<Work> normalQueue = new();
    private readonly HashSet<string> queuedPriority = new(StringComparer.Ordinal);
    private readonly HashSet<string> queuedNormal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Work> scheduled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> canceledThrough = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> canceledPendingThrough = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Work> deferredPriority = new(StringComparer.Ordinal);
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim signal = new(0);
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
            if (completed.Contains(template) || scheduled.ContainsKey(template)) return false;
            canceledPendingThrough.Remove(template);
            var generation = ++nextGeneration;
            var work = new Work(template, false, generation, pendingGeneration);
            scheduled[template] = work;
            normalQueue.Enqueue(work);
            queuedNormal.Add(template);
        }
        signal.Release();
        return true;
    }

    public bool EnqueuePriority(string template)
    {
        return EnqueuePriority(template, 0);
    }

    public bool EnqueuePriority(string template, long pendingGeneration)
    {
        if (!TextTemplate.IsTranslationCandidate(template)) return false;
        var added = false;
        lock (gate)
        {
            if (IsPendingCanceledUnsafe(template, pendingGeneration)) return false;
            if (completed.Contains(template)) return false;
            if (scheduled.TryGetValue(template, out var scheduledWork))
            {
                if (pendingGeneration < scheduledWork.PendingGeneration) return false;
                if (!queuedNormal.Remove(template))
                {
                    if (pendingGeneration > scheduledWork.PendingGeneration
                        && (!deferredPriority.TryGetValue(template, out var deferredWork)
                            || pendingGeneration > deferredWork.PendingGeneration))
                        deferredPriority[template] = new Work(template, true, ++nextGeneration, pendingGeneration);
                    return false;
                }
                RemoveQueuedNormal(template);
                scheduledWork = new Work(template, true, ++nextGeneration, pendingGeneration);
                scheduled[template] = scheduledWork;
            }
            else
            {
                added = true;
                canceledPendingThrough.Remove(template);
                scheduledWork = new Work(template, true, ++nextGeneration, pendingGeneration);
                scheduled[template] = scheduledWork;
            }
            priorityQueue.Enqueue(scheduledWork);
            queuedPriority.Add(template);
        }
        if (added) signal.Release();
        return true;
    }

    public bool TryDequeue(out string template)
    {
        return TryDequeue(out template, out _);
    }

    private bool TryDequeue(out string template, out bool priority)
    {
        if (TryDequeue(out Work work))
        {
            template = work.Template;
            priority = work.Priority;
            return true;
        }
        template = string.Empty;
        priority = false;
        return false;
    }

    private bool TryDequeue(out Work work)
    {
        lock (gate)
        {
            if (priorityQueue.Count > 0)
            {
                work = priorityQueue.Dequeue();
                queuedPriority.Remove(work.Template);
                return true;
            }
            if (normalQueue.Count > 0)
            {
                work = normalQueue.Dequeue();
                queuedNormal.Remove(work.Template);
                return true;
            }
            work = default;
            return false;
        }
    }

    public async Task<Work> DequeueAsync(CancellationToken token)
    {
        while (true)
        {
            await signal.WaitAsync(token).ConfigureAwait(false);
            if (TryDequeue(out Work work)) return work;
        }
    }

    public string[] DrainPriority()
    {
        return DrainPriorityWork().Select(item => item.Template).ToArray();
    }

    public Work[] DrainPriorityWork()
    {
        lock (gate)
        {
            var work = priorityQueue
                .Concat(deferredPriority.Values)
                .OrderBy(item => item.Generation)
                .ToArray();
            priorityQueue.Clear();
            deferredPriority.Clear();
            foreach (var item in work)
            {
                queuedPriority.Remove(item.Template);
                if (scheduled.TryGetValue(item.Template, out var scheduledWork)
                    && scheduledWork.Generation == item.Generation)
                    scheduled.Remove(item.Template);
            }
            return work;
        }
    }

    public bool Cancel(string template)
    {
        return Cancel(template, long.MaxValue);
    }

    public bool Cancel(string template, long pendingGeneration)
    {
        var enqueueDeferred = false;
        lock (gate)
        {
            RecordPendingCancellationUnsafe(template, pendingGeneration);
            if (!scheduled.TryGetValue(template, out var scheduledWork))
            {
                if (deferredPriority.TryGetValue(template, out var deferredWork)
                    && deferredWork.PendingGeneration <= pendingGeneration)
                    deferredPriority.Remove(template);
                return false;
            }
            if (scheduledWork.PendingGeneration > pendingGeneration) return false;
            scheduled.Remove(template);
            canceledThrough[template] = scheduledWork.Generation;
            if (deferredPriority.TryGetValue(template, out var queuedDeferredWork)
                && queuedDeferredWork.PendingGeneration <= pendingGeneration)
                deferredPriority.Remove(template);
            else if (queuedDeferredWork.PendingGeneration > pendingGeneration)
            {
                deferredPriority.Remove(template);
                enqueueDeferred = true;
            }
            queuedPriority.Remove(template);
            queuedNormal.Remove(template);
            RemoveQueued(priorityQueue, template);
            RemoveQueued(normalQueue, template);
            if (enqueueDeferred) EnqueueDeferredPriorityUnsafe(queuedDeferredWork);
        }
        if (enqueueDeferred) signal.Release();
        return true;
    }

    public bool IsCanceled(Work work)
    {
        lock (gate) return canceledThrough.TryGetValue(work.Template, out var generation) && generation >= work.Generation;
    }

    public bool TryIfNotCanceled(Work work, Func<bool> action)
    {
        lock (gate) return !IsCanceledUnsafe(work) && action();
    }

    public void Finish(string template, bool succeeded)
    {
        lock (gate)
        {
            if (succeeded) completed.Add(template);
            scheduled.Remove(template);
        }
    }

    public void Finish(Work work, bool succeeded)
    {
        var enqueueDeferred = false;
        lock (gate)
        {
            if (succeeded && !IsCanceledUnsafe(work)) completed.Add(work.Template);
            if (succeeded
                && canceledPendingThrough.TryGetValue(work.Template, out var cutoff)
                && work.PendingGeneration > cutoff)
                canceledPendingThrough.Remove(work.Template);
            if (scheduled.TryGetValue(work.Template, out var scheduledWork)
                && scheduledWork.Generation == work.Generation)
                scheduled.Remove(work.Template);
            if (!succeeded && deferredPriority.Remove(work.Template, out var deferredWork))
            {
                EnqueueDeferredPriorityUnsafe(deferredWork);
                enqueueDeferred = true;
            }
            if (!succeeded && work.PendingGeneration > 0)
                RecordPendingCancellationUnsafe(work.Template, work.PendingGeneration - 1);
        }
        if (enqueueDeferred) signal.Release();
    }

    public Work? FinishAndTakeDeferred(Work work)
    {
        lock (gate)
        {
            if (scheduled.TryGetValue(work.Template, out var scheduledWork)
                && scheduledWork.Generation == work.Generation)
                scheduled.Remove(work.Template);
            var deferred = deferredPriority.Remove(work.Template, out var deferredWork)
                ? deferredWork
                : (Work?)null;
            if (work.PendingGeneration > 0)
                RecordPendingCancellationUnsafe(work.Template, work.PendingGeneration - 1);
            return deferred;
        }
    }

    private void RemoveQueuedNormal(string template)
    {
        RemoveQueued(normalQueue, template);
    }

    private void EnqueueDeferredPriorityUnsafe(Work work)
    {
        canceledPendingThrough.Remove(work.Template);
        scheduled[work.Template] = work;
        queuedPriority.Add(work.Template);
        priorityQueue.Enqueue(work);
        var ordered = priorityQueue.OrderBy(item => item.Generation).ToArray();
        priorityQueue.Clear();
        foreach (var item in ordered) priorityQueue.Enqueue(item);
    }

    private static void RemoveQueued(Queue<Work> queue, string template)
    {
        var count = queue.Count;
        for (var index = 0; index < count; index++)
        {
            var current = queue.Dequeue();
            if (!string.Equals(current.Template, template, StringComparison.Ordinal)) queue.Enqueue(current);
        }
    }

    private bool IsCanceledUnsafe(Work work) =>
        canceledThrough.TryGetValue(work.Template, out var generation) && generation >= work.Generation;

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
