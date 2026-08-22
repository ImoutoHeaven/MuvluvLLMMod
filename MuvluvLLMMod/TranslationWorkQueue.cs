namespace MuvluvLLMMod;

public sealed class TranslationWorkQueue
{
    public readonly record struct Work(string Template, bool Priority, long Generation, long PendingGeneration);

    public readonly record struct BudgetSnapshot(
        int ScheduledCount,
        long ScheduledUtf8Bytes,
        int DeferredCount,
        long DeferredUtf8Bytes,
        int CompletedCount,
        long CompletedUtf8Bytes,
        int CanceledCount,
        long CanceledUtf8Bytes,
        int CanceledPendingCount,
        long CanceledPendingUtf8Bytes);

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
    private readonly LinkedList<string> completedLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> completedNodes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim signal = new(0);
    private long scheduledUtf8Bytes;
    private long deferredUtf8Bytes;
    private long canceledUtf8Bytes;
    private long canceledPendingUtf8Bytes;
    private long completedUtf8Bytes;
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
                    deferredPriority.Count,
                    deferredUtf8Bytes,
                    completed.Count,
                    completedUtf8Bytes,
                    canceledThrough.Count,
                    canceledUtf8Bytes,
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
            if (IsPendingCanceledUnsafe(template, pendingGeneration)
                || completed.Contains(template)
                || scheduled.ContainsKey(template)
                || !CanScheduleUnsafe(template))
                return false;
            RemoveCanceledPendingUnsafe(template);
            var generation = ++nextGeneration;
            var work = new Work(template, false, generation, pendingGeneration);
            scheduled[template] = work;
            scheduledUtf8Bytes += Utf8Bytes(template);
            normalQueue.Enqueue(work);
            queuedNormal.Add(template);
        }
        signal.Release();
        return true;
    }

    public bool EnqueuePriority(string template) => EnqueuePriority(template, 0);

    public bool EnqueuePriority(string template, long pendingGeneration)
    {
        if (!TextTemplate.IsTranslationCandidate(template)) return false;
        var added = false;
        lock (gate)
        {
            if (IsPendingCanceledUnsafe(template, pendingGeneration)
                || completed.Contains(template))
                return false;
            if (scheduled.TryGetValue(template, out var scheduledWork))
            {
                if (pendingGeneration < scheduledWork.PendingGeneration) return false;
                if (!queuedNormal.Remove(template))
                {
                    if (pendingGeneration > scheduledWork.PendingGeneration
                        && (!deferredPriority.TryGetValue(template, out var deferredWork)
                            || pendingGeneration > deferredWork.PendingGeneration))
                    {
                        if (!TrySetDeferredUnsafe(
                                new Work(template, true, ++nextGeneration, pendingGeneration)))
                            return false;
                    }
                    return false;
                }
                RemoveQueuedNormal(template);
                scheduledWork = new Work(template, true, ++nextGeneration, pendingGeneration);
                scheduled[template] = scheduledWork;
            }
            else
            {
                if (!CanScheduleUnsafe(template)) return false;
                added = true;
                RemoveCanceledPendingUnsafe(template);
                scheduledWork = new Work(template, true, ++nextGeneration, pendingGeneration);
                scheduled[template] = scheduledWork;
                scheduledUtf8Bytes += Utf8Bytes(template);
            }
            priorityQueue.Enqueue(scheduledWork);
            queuedPriority.Add(template);
        }
        if (added) signal.Release();
        return true;
    }

    public bool TryDequeue(out string template) => TryDequeue(out template, out _);

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

    public string[] DrainPriority() => DrainPriorityWork().Select(item => item.Template).ToArray();

    public Work[] DrainPriorityWork()
    {
        lock (gate)
        {
            var work = priorityQueue
                .Concat(deferredPriority.Values)
                .OrderBy(item => item.Generation)
                .ToArray();
            priorityQueue.Clear();
            foreach (var item in work)
            {
                queuedPriority.Remove(item.Template);
                if (scheduled.TryGetValue(item.Template, out var scheduledWork)
                    && scheduledWork.Generation == item.Generation)
                    RemoveScheduledUnsafe(item.Template);
            }
            foreach (var template in deferredPriority.Keys.ToArray())
                RemoveDeferredUnsafe(template);
            return work;
        }
    }

    public bool Cancel(string template) => Cancel(template, long.MaxValue);

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
                    RemoveDeferredUnsafe(template);
                return false;
            }
            if (scheduledWork.PendingGeneration > pendingGeneration) return false;

            var wasQueued = queuedPriority.Contains(template) || queuedNormal.Contains(template);
            RemoveScheduledUnsafe(template);
            if (!wasQueued)
                SetCanceledThroughUnsafe(template, scheduledWork.Generation);
            if (deferredPriority.TryGetValue(template, out var queuedDeferredWork)
                && queuedDeferredWork.PendingGeneration <= pendingGeneration)
                RemoveDeferredUnsafe(template);
            else if (queuedDeferredWork.PendingGeneration > pendingGeneration)
            {
                RemoveDeferredUnsafe(template);
                _ = TrySetDeferredUnsafe(queuedDeferredWork);
            }
            queuedPriority.Remove(template);
            queuedNormal.Remove(template);
            RemoveQueued(priorityQueue, template);
            RemoveQueued(normalQueue, template);
            if (enqueueDeferred) signal.Release();
        }
        return true;
    }

    public bool IsCanceled(Work work)
    {
        lock (gate) return canceledThrough.TryGetValue(work.Template, out var generation)
            && generation >= work.Generation;
    }

    public bool TryIfNotCanceled(Work work, Func<bool> action)
    {
        lock (gate) return !IsCanceledUnsafe(work) && action();
    }

    public void Finish(string template, bool succeeded)
    {
        lock (gate)
        {
            if (succeeded) AddCompletedUnsafe(template);
            RemoveScheduledUnsafe(template);
            RemoveCanceledUnsafe(template);
        }
    }

    public void Finish(Work work, bool succeeded)
    {
        var enqueueDeferred = false;
        lock (gate)
        {
            if (succeeded && !IsCanceledUnsafe(work)) AddCompletedUnsafe(work.Template);
            if (succeeded
                && canceledPendingThrough.TryGetValue(work.Template, out var cutoff)
                && work.PendingGeneration > cutoff)
                RemoveCanceledPendingUnsafe(work.Template);
            if (scheduled.TryGetValue(work.Template, out var scheduledWork)
                && scheduledWork.Generation == work.Generation)
                RemoveScheduledUnsafe(work.Template);
            if (!succeeded && deferredPriority.ContainsKey(work.Template))
            {
                var deferred = RemoveDeferredUnsafe(work.Template);
                if (deferred.HasValue)
                {
                    EnqueueDeferredPriorityUnsafe(deferred.Value);
                    enqueueDeferred = true;
                }
            }
            if (!succeeded && work.PendingGeneration > 0)
                RecordPendingCancellationUnsafe(work.Template, work.PendingGeneration - 1);
            RemoveCanceledUnsafe(work.Template);
        }
        if (enqueueDeferred) signal.Release();
    }

    public Work? FinishAndTakeDeferred(Work work)
    {
        lock (gate)
        {
            if (scheduled.TryGetValue(work.Template, out var scheduledWork)
                && scheduledWork.Generation == work.Generation)
                RemoveScheduledUnsafe(work.Template);
            var deferred = RemoveDeferredUnsafe(work.Template);
            if (work.PendingGeneration > 0)
                RecordPendingCancellationUnsafe(work.Template, work.PendingGeneration - 1);
            RemoveCanceledUnsafe(work.Template);
            return deferred;
        }
    }

    private bool CanScheduleUnsafe(string template)
    {
        var cancellationCapacity = canceledThrough.ContainsKey(template)
            || (canceledThrough.Count < TranslationBudget.MaxCancellationEntries
                && canceledUtf8Bytes + Utf8Bytes(template) <= TranslationBudget.MaxCancellationUtf8Bytes);
        return scheduled.Count < TranslationBudget.MaxWorkItems
            && scheduledUtf8Bytes + Utf8Bytes(template) <= TranslationBudget.MaxWorkItemUtf8Bytes
            && cancellationCapacity;
    }

    private void RemoveQueuedNormal(string template) => RemoveQueued(normalQueue, template);

    private bool TrySetDeferredUnsafe(Work work, bool enqueueToQueue = false)
    {
        if (!deferredPriority.ContainsKey(work.Template))
        {
            if (deferredPriority.Count >= TranslationBudget.MaxWorkItems
                || deferredUtf8Bytes + Utf8Bytes(work.Template) > TranslationBudget.MaxWorkItemUtf8Bytes)
                return false;
            deferredUtf8Bytes += Utf8Bytes(work.Template);
        }
        deferredPriority[work.Template] = work;
        if (enqueueToQueue)
        {
            scheduled[work.Template] = work;
            scheduledUtf8Bytes += Utf8Bytes(work.Template);
            queuedPriority.Add(work.Template);
            priorityQueue.Enqueue(work);
        }
        return true;
    }

    private Work? RemoveDeferredUnsafe(string template)
    {
        if (!deferredPriority.Remove(template, out var work)) return null;
        deferredUtf8Bytes -= Utf8Bytes(template);
        if (deferredUtf8Bytes < 0) deferredUtf8Bytes = 0;
        return work;
    }

    private void EnqueueDeferredPriorityUnsafe(Work work)
    {
        RemoveCanceledPendingUnsafe(work.Template);
        if (!CanScheduleUnsafe(work.Template)) return;
        scheduled[work.Template] = work;
        scheduledUtf8Bytes += Utf8Bytes(work.Template);
        queuedPriority.Add(work.Template);
        priorityQueue.Enqueue(work);
        var ordered = priorityQueue.OrderBy(item => item.Generation).ToArray();
        priorityQueue.Clear();
        foreach (var item in ordered) priorityQueue.Enqueue(item);
    }

    private void RemoveScheduledUnsafe(string template)
    {
        if (!scheduled.Remove(template)) return;
        scheduledUtf8Bytes -= Utf8Bytes(template);
        if (scheduledUtf8Bytes < 0) scheduledUtf8Bytes = 0;
    }

    private void SetCanceledThroughUnsafe(string template, long generation)
    {
        if (canceledThrough.TryGetValue(template, out var existing))
        {
            if (generation > existing) canceledThrough[template] = generation;
            return;
        }
        if (canceledThrough.Count >= TranslationBudget.MaxCancellationEntries
            || canceledUtf8Bytes + Utf8Bytes(template) > TranslationBudget.MaxCancellationUtf8Bytes)
            return;
        canceledThrough[template] = generation;
        canceledUtf8Bytes += Utf8Bytes(template);
    }

    private void RemoveCanceledUnsafe(string template)
    {
        if (!canceledThrough.Remove(template)) return;
        canceledUtf8Bytes -= Utf8Bytes(template);
        if (canceledUtf8Bytes < 0) canceledUtf8Bytes = 0;
    }

    private void AddCompletedUnsafe(string template)
    {
        if (completed.Contains(template))
        {
            if (completedNodes.TryGetValue(template, out var existing))
            {
                completedLru.Remove(existing);
                completedLru.AddLast(existing);
            }
            return;
        }
        var bytes = Utf8Bytes(template);
        if (bytes > TranslationBudget.MaxWorkItemUtf8Bytes) return;
        while (completed.Count >= TranslationBudget.MaxWorkItems
            || completedUtf8Bytes + bytes > TranslationBudget.MaxWorkItemUtf8Bytes)
        {
            if (completedLru.First == null) return;
            var oldest = completedLru.First.Value;
            completedLru.RemoveFirst();
            completedNodes.Remove(oldest);
            completed.Remove(oldest);
            completedUtf8Bytes -= Utf8Bytes(oldest);
        }
        completed.Add(template);
        var node = completedLru.AddLast(template);
        completedNodes[template] = node;
        completedUtf8Bytes += bytes;
    }

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

    private bool IsCanceledUnsafe(Work work) =>
        canceledThrough.TryGetValue(work.Template, out var generation) && generation >= work.Generation;

    private bool IsPendingCanceledUnsafe(string template, long pendingGeneration) =>
        pendingGeneration > 0
        && canceledPendingThrough.TryGetValue(template, out var cutoff)
        && pendingGeneration <= cutoff;

    private static void RemoveQueued(Queue<Work> queue, string template)
    {
        var count = queue.Count;
        for (var index = 0; index < count; index++)
        {
            var current = queue.Dequeue();
            if (!string.Equals(current.Template, template, StringComparison.Ordinal)) queue.Enqueue(current);
        }
    }

    private static int Utf8Bytes(string template)
    {
        TranslationBudget.TryGetTextBytes(template, out var bytes);
        return bytes;
    }
}
