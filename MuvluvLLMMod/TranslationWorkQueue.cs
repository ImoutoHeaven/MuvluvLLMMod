namespace MuvluvLLMMod;

public sealed class TranslationWorkQueue
{
    public const int ForegroundBurstLimit = 4;

    public readonly record struct Work(
        string Template,
        bool Priority,
        long Generation,
        long PendingGeneration,
        TranslationPriority Level = TranslationPriority.Foreground)
    {
        public Work(string template, bool priority, long generation, long pendingGeneration)
            : this(
                template,
                priority,
                generation,
                pendingGeneration,
                priority ? TranslationPriority.Urgent : TranslationPriority.Foreground)
        {
        }
    }

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
        long CanceledPendingUtf8Bytes,
        int ReservedCount = 0,
        long ReservedUtf8Bytes = 0);

    private readonly object gate = new();
    private readonly AsyncPulse workPulse = new();
    private readonly Queue<Work> urgentQueue = new();
    private readonly Queue<Work> sceneFallbackQueue = new();
    private readonly Queue<Work> foregroundQueue = new();
    private readonly Queue<Work> backgroundQueue = new();
    private readonly Dictionary<string, TranslationPriority> queuedLevels = new(StringComparer.Ordinal);
    private readonly HashSet<string> reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Work> scheduled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> canceledThrough = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> canceledPendingThrough = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Work> deferredPriority = new(StringComparer.Ordinal);
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private readonly LinkedList<string> completedLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> completedNodes = new(StringComparer.Ordinal);
    private int foregroundDequeuesSinceBackground;
    private int sceneFallbackDequeuesSinceForeground;
    private long reservedUtf8Bytes;
    private long scheduledUtf8Bytes;
    private long deferredUtf8Bytes;
    private long canceledUtf8Bytes;
    private long canceledPendingUtf8Bytes;
    private long completedUtf8Bytes;
    private long nextGeneration;

    public event Action? StateChanged;

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
                    canceledPendingUtf8Bytes,
                    reservations.Count,
                    reservedUtf8Bytes);
            }
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (gate)
                return queuedLevels.Count;
        }
    }

    public bool HasQueuedWork
    {
        get
        {
            lock (gate)
                return HasQueuedWorkUnsafe;
        }
    }

    public bool HasUrgentWork
    {
        get
        {
            lock (gate)
                return urgentQueue.Count > 0;
        }
    }

    private bool HasQueuedWorkUnsafe =>
        urgentQueue.Count > 0
        || sceneFallbackQueue.Count > 0
        || foregroundQueue.Count > 0
        || backgroundQueue.Count > 0;

    public bool IsReserved(string template)
    {
        if (string.IsNullOrEmpty(template))
            return false;
        lock (gate)
            return reservations.Contains(template);
    }

    public bool TryReserve(string template, Func<bool> isAlreadyCached) =>
        TryReserve(template, isAlreadyCached, out _);

    public bool TryReserve(
        string template,
        Func<bool> isAlreadyCached,
        out TranslationReservationStatus status)
    {
        ArgumentNullException.ThrowIfNull(isAlreadyCached);
        if (!TextTemplate.IsTranslationCandidate(template))
        {
            status = TranslationReservationStatus.AlreadyCached;
            return false;
        }

        var changed = false;
        lock (gate)
        {
            if (isAlreadyCached())
            {
                status = TranslationReservationStatus.AlreadyCached;
                return false;
            }

            if (reservations.Contains(template))
            {
                status = TranslationReservationStatus.AlreadyReserved;
                return false;
            }

            if (!CanReserveUnsafe(template))
            {
                status = TranslationReservationStatus.BudgetExceeded;
                return false;
            }

            if (scheduled.ContainsKey(template))
            {
                if (!queuedLevels.Remove(template, out var queuedLevel))
                {
                    status = TranslationReservationStatus.AlreadyInFlight;
                    return false;
                }

                RemoveQueued(GetQueueUnsafe(queuedLevel), template);
                RemoveScheduledUnsafe(template);
                RemoveDeferredUnsafe(template);
                AddReservationUnsafe(template);
                status = TranslationReservationStatus.ReservedFromPending;
                changed = true;
            }
            else
            {
                RemoveDeferredUnsafe(template);
                AddReservationUnsafe(template);
                status = TranslationReservationStatus.Reserved;
                changed = true;
            }
        }

        if (changed)
            NotifyStateChanged();
        return true;
    }

    public void ReleaseReservation(string template)
    {
        if (string.IsNullOrEmpty(template))
            return;

        bool removed;
        lock (gate)
        {
            removed = reservations.Remove(template);
            if (removed)
            {
                reservedUtf8Bytes -= Utf8Bytes(template);
                if (reservedUtf8Bytes < 0)
                    reservedUtf8Bytes = 0;
            }
        }
        if (removed)
            NotifyStateChanged();
    }

    public void CompleteCached(string template)
    {
        if (!TextTemplate.IsTranslationCandidate(template))
            return;

        var changed = false;
        lock (gate)
        {
            AddCompletedUnsafe(template);
            if (RemoveDeferredUnsafe(template).HasValue)
                changed = true;
            if (queuedLevels.Remove(template, out var queuedLevel))
            {
                RemoveQueued(GetQueueUnsafe(queuedLevel), template);
                RemoveScheduledUnsafe(template);
                changed = true;
            }
        }

        if (changed)
            NotifyStateChanged();
    }

    public bool Enqueue(string template) => Enqueue(template, 0, TranslationPriority.Foreground);

    public bool Enqueue(string template, long pendingGeneration) =>
        Enqueue(template, pendingGeneration, TranslationPriority.Foreground);

    public bool EnqueueBackground(string template, long pendingGeneration = 0) =>
        Enqueue(template, pendingGeneration, TranslationPriority.Background);

    public bool EnqueueSceneFallback(string template, long pendingGeneration = 0) =>
        Enqueue(template, pendingGeneration, TranslationPriority.SceneFallback);

    public bool EnqueuePriority(string template) =>
        Enqueue(template, 0, TranslationPriority.Urgent);

    public bool EnqueuePriority(string template, long pendingGeneration) =>
        Enqueue(template, pendingGeneration, TranslationPriority.Urgent);

    public bool Enqueue(
        string template,
        long pendingGeneration,
        TranslationPriority priority)
    {
        if (!TextTemplate.IsTranslationCandidate(template))
            return false;

        lock (gate)
        {
            if (reservations.Contains(template)
                || IsPendingCanceledUnsafe(template, pendingGeneration)
                || completed.Contains(template))
                return false;

            var isUrgentOrScene = priority >= TranslationPriority.SceneFallback;
            if (scheduled.TryGetValue(template, out var scheduledWork))
            {
                if (pendingGeneration < scheduledWork.PendingGeneration)
                    return false;

                if (!queuedLevels.TryGetValue(template, out var currentLevel))
                {
                    if (priority == TranslationPriority.Urgent
                        && pendingGeneration > scheduledWork.PendingGeneration
                        && (!deferredPriority.TryGetValue(template, out var deferredWork)
                            || pendingGeneration > deferredWork.PendingGeneration))
                    {
                        _ = TrySetDeferredUnsafe(
                            new Work(
                                template,
                                true,
                                ++nextGeneration,
                                pendingGeneration,
                                TranslationPriority.Urgent));
                    }
                    return false;
                }

                if (priority <= currentLevel && pendingGeneration == scheduledWork.PendingGeneration)
                    return false;

                var targetLevel = priority > currentLevel ? priority : currentLevel;
                RemoveQueued(GetQueueUnsafe(currentLevel), template);
                scheduledWork = new Work(
                    template,
                    targetLevel >= TranslationPriority.SceneFallback,
                    ++nextGeneration,
                    pendingGeneration,
                    targetLevel);
                scheduled[template] = scheduledWork;
                queuedLevels[template] = targetLevel;
                GetQueueUnsafe(targetLevel).Enqueue(scheduledWork);
            }
            else
            {
                if (!CanScheduleUnsafe(template))
                    return false;

                RemoveCanceledPendingUnsafe(template);
                scheduledWork = new Work(
                    template,
                    isUrgentOrScene,
                    ++nextGeneration,
                    pendingGeneration,
                    priority);
                scheduled[template] = scheduledWork;
                scheduledUtf8Bytes += Utf8Bytes(template);
                queuedLevels[template] = priority;
                GetQueueUnsafe(priority).Enqueue(scheduledWork);
            }
        }

        NotifyStateChanged();
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

    public bool TryDequeueWork(out Work work)
    {
        bool dequeued;
        lock (gate)
            dequeued = TryDequeueUnsafe(out work);
        if (dequeued)
            StateChanged?.Invoke();
        return dequeued;
    }

    private bool TryDequeue(out Work work) => TryDequeueWork(out work);

    public async Task WaitForWorkAsync(CancellationToken token)
    {
        while (true)
        {
            Task waitTask;
            lock (gate)
            {
                if (HasQueuedWorkUnsafe)
                    return;
                waitTask = workPulse.WaitAsync();
            }

            await waitTask.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async Task<Work> DequeueAsync(CancellationToken token)
    {
        while (true)
        {
            Task? waitTask = null;
            Work work;
            var dequeued = false;
            lock (gate)
            {
                if (TryDequeueUnsafe(out work))
                    dequeued = true;
                else
                    waitTask = workPulse.WaitAsync();
            }

            if (dequeued)
            {
                StateChanged?.Invoke();
                return work;
            }

            await waitTask!.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public string[] DrainPriority() => DrainPriorityWork().Select(item => item.Template).ToArray();

    public Work[] DrainPriorityWork()
    {
        Work[] work;
        lock (gate)
        {
            work = urgentQueue
                .Concat(deferredPriority.Values)
                .OrderBy(item => item.Generation)
                .ToArray();
            urgentQueue.Clear();
            foreach (var item in work)
            {
                queuedLevels.Remove(item.Template);
                if (scheduled.TryGetValue(item.Template, out var scheduledWork)
                    && scheduledWork.Generation == item.Generation)
                    RemoveScheduledUnsafe(item.Template);
            }
            foreach (var template in deferredPriority.Keys.ToArray())
                RemoveDeferredUnsafe(template);
        }

        if (work.Length > 0)
            NotifyStateChanged();
        return work;
    }

    public bool Cancel(string template) => Cancel(template, long.MaxValue);

    public bool Cancel(string template, long pendingGeneration)
    {
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
            if (scheduledWork.PendingGeneration > pendingGeneration)
                return false;

            var wasQueued = queuedLevels.Remove(template, out var queuedLevel);
            RemoveScheduledUnsafe(template);
            if (!wasQueued)
                SetCanceledThroughUnsafe(template, scheduledWork.Generation);
            else
                RemoveQueued(GetQueueUnsafe(queuedLevel), template);

            if (deferredPriority.TryGetValue(template, out var queuedDeferredWork)
                && queuedDeferredWork.PendingGeneration <= pendingGeneration)
            {
                RemoveDeferredUnsafe(template);
            }
            else if (queuedDeferredWork.PendingGeneration > pendingGeneration)
            {
                RemoveDeferredUnsafe(template);
                _ = TrySetDeferredUnsafe(queuedDeferredWork);
            }
        }

        NotifyStateChanged();
        return true;
    }

    public bool IsCanceled(Work work)
    {
        lock (gate)
            return canceledThrough.TryGetValue(work.Template, out var generation)
                && generation >= work.Generation;
    }

    public bool TryIfNotCanceled(Work work, Func<bool> action)
    {
        lock (gate)
            return !IsCanceledUnsafe(work) && action();
    }

    public void Finish(string template, bool succeeded)
    {
        lock (gate)
        {
            if (succeeded)
                AddCompletedUnsafe(template);
            RemoveScheduledUnsafe(template);
            RemoveCanceledUnsafe(template);
        }
        NotifyStateChanged();
    }

    public void Finish(Work work, bool succeeded)
    {
        var enqueueDeferred = false;
        lock (gate)
        {
            if (succeeded && !IsCanceledUnsafe(work))
                AddCompletedUnsafe(work.Template);
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

        if (enqueueDeferred)
            NotifyStateChanged();
        else
            StateChanged?.Invoke();
    }

    public Work? FinishAndTakeDeferred(Work work)
    {
        Work? deferred;
        lock (gate)
        {
            if (scheduled.TryGetValue(work.Template, out var scheduledWork)
                && scheduledWork.Generation == work.Generation)
                RemoveScheduledUnsafe(work.Template);
            deferred = RemoveDeferredUnsafe(work.Template);
            if (work.PendingGeneration > 0)
                RecordPendingCancellationUnsafe(work.Template, work.PendingGeneration - 1);
            RemoveCanceledUnsafe(work.Template);
        }
        StateChanged?.Invoke();
        return deferred;
    }

    private bool TryDequeueUnsafe(out Work work)
    {
        if (urgentQueue.Count > 0)
        {
            work = urgentQueue.Dequeue();
            queuedLevels.Remove(work.Template);
            return true;
        }

        var hasBackground = backgroundQueue.Count > 0;
        if (!hasBackground || foregroundDequeuesSinceBackground < ForegroundBurstLimit)
        {
            var hasSceneFallback = sceneFallbackQueue.Count > 0;
            var hasForeground = foregroundQueue.Count > 0;
            if (hasSceneFallback
                && (!hasForeground || sceneFallbackDequeuesSinceForeground < ForegroundBurstLimit))
            {
                work = sceneFallbackQueue.Dequeue();
                queuedLevels.Remove(work.Template);
                sceneFallbackDequeuesSinceForeground++;
                foregroundDequeuesSinceBackground++;
                return true;
            }

            if (hasForeground)
            {
                work = foregroundQueue.Dequeue();
                queuedLevels.Remove(work.Template);
                sceneFallbackDequeuesSinceForeground = 0;
                foregroundDequeuesSinceBackground++;
                return true;
            }

            if (hasSceneFallback)
            {
                work = sceneFallbackQueue.Dequeue();
                queuedLevels.Remove(work.Template);
                foregroundDequeuesSinceBackground++;
                return true;
            }
        }

        if (hasBackground)
        {
            work = backgroundQueue.Dequeue();
            queuedLevels.Remove(work.Template);
            foregroundDequeuesSinceBackground = 0;
            return true;
        }

        work = default;
        return false;
    }

    private Queue<Work> GetQueueUnsafe(TranslationPriority priority) =>
        priority switch
        {
            TranslationPriority.Urgent => urgentQueue,
            TranslationPriority.SceneFallback => sceneFallbackQueue,
            TranslationPriority.Foreground => foregroundQueue,
            _ => backgroundQueue,
        };

    private void NotifyStateChanged()
    {
        workPulse.PulseAll();
        StateChanged?.Invoke();
    }

    private bool CanReserveUnsafe(string template) =>
        reservations.Count < TranslationBudget.MaxWorkItems
        && reservedUtf8Bytes + Utf8Bytes(template) <= TranslationBudget.MaxWorkItemUtf8Bytes;

    private void AddReservationUnsafe(string template)
    {
        if (reservations.Add(template))
            reservedUtf8Bytes += Utf8Bytes(template);
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

    private bool TrySetDeferredUnsafe(Work work)
    {
        if (!deferredPriority.ContainsKey(work.Template))
        {
            if (deferredPriority.Count >= TranslationBudget.MaxWorkItems
                || deferredUtf8Bytes + Utf8Bytes(work.Template) > TranslationBudget.MaxWorkItemUtf8Bytes)
                return false;
            deferredUtf8Bytes += Utf8Bytes(work.Template);
        }
        deferredPriority[work.Template] = work;
        return true;
    }

    private Work? RemoveDeferredUnsafe(string template)
    {
        if (!deferredPriority.Remove(template, out var work))
            return null;
        deferredUtf8Bytes -= Utf8Bytes(template);
        if (deferredUtf8Bytes < 0)
            deferredUtf8Bytes = 0;
        return work;
    }

    private void EnqueueDeferredPriorityUnsafe(Work work)
    {
        if (reservations.Contains(work.Template))
            return;
        RemoveCanceledPendingUnsafe(work.Template);
        if (!CanScheduleUnsafe(work.Template))
            return;
        scheduled[work.Template] = work;
        scheduledUtf8Bytes += Utf8Bytes(work.Template);
        queuedLevels[work.Template] = TranslationPriority.Urgent;
        urgentQueue.Enqueue(work);
        var ordered = urgentQueue.OrderBy(item => item.Generation).ToArray();
        urgentQueue.Clear();
        foreach (var item in ordered)
            urgentQueue.Enqueue(item);
    }

    private void RemoveScheduledUnsafe(string template)
    {
        if (!scheduled.Remove(template))
            return;
        scheduledUtf8Bytes -= Utf8Bytes(template);
        if (scheduledUtf8Bytes < 0)
            scheduledUtf8Bytes = 0;
    }

    private void SetCanceledThroughUnsafe(string template, long generation)
    {
        if (canceledThrough.TryGetValue(template, out var existing))
        {
            if (generation > existing)
                canceledThrough[template] = generation;
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
        if (!canceledThrough.Remove(template))
            return;
        canceledUtf8Bytes -= Utf8Bytes(template);
        if (canceledUtf8Bytes < 0)
            canceledUtf8Bytes = 0;
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
        if (bytes > TranslationBudget.MaxWorkItemUtf8Bytes)
            return;
        while (completed.Count >= TranslationBudget.MaxWorkItems
            || completedUtf8Bytes + bytes > TranslationBudget.MaxWorkItemUtf8Bytes)
        {
            if (completedLru.First == null)
                return;
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
        if (pendingGeneration <= 0
            || pendingGeneration == long.MaxValue
            || !TextTemplate.IsTranslationCandidate(template))
            return;
        if (canceledPendingThrough.TryGetValue(template, out var cutoff))
        {
            if (pendingGeneration > cutoff)
                canceledPendingThrough[template] = pendingGeneration;
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
        if (!canceledPendingThrough.Remove(template))
            return;
        canceledPendingUtf8Bytes -= Utf8Bytes(template);
        if (canceledPendingUtf8Bytes < 0)
            canceledPendingUtf8Bytes = 0;
    }

    private bool IsCanceledUnsafe(Work work) =>
        canceledThrough.TryGetValue(work.Template, out var generation)
        && generation >= work.Generation;

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
            if (!string.Equals(current.Template, template, StringComparison.Ordinal))
                queue.Enqueue(current);
        }
    }

    private static int Utf8Bytes(string template)
    {
        TranslationBudget.TryGetTextBytes(template, out var bytes);
        return bytes;
    }
}
