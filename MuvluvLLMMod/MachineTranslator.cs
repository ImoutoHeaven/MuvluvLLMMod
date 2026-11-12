namespace MuvluvLLMMod;

public sealed class MachineTranslator : IDisposable
{
    public static readonly TimeSpan DefaultUrgentPreemptGracePeriod = TimeSpan.FromSeconds(3);

    private readonly object lifecycleGate = new();
    private readonly TranslationCache cache;
    private readonly Func<string, CancellationToken, Task<string?>> translate;
    private readonly Func<string, Action?, CancellationToken, Task<ScenePromptResponse>>? sendScenePrompt;
    private readonly int maxInFlight;
    private readonly int sceneRetryCount;
    private readonly TimeSpan translatePeriod;
    private readonly TimeSpan urgentPreemptGracePeriod;
    private readonly Action<string>? diagnostic;
    private readonly TimeSpan statusPeriod;
    private readonly TranslationPriorityBacklog priorityBacklog;
    private readonly TranslationRetryPolicy retryPolicy;
    private readonly SceneRetryRegistry sceneRetries;
    private readonly TranslationProgress progress = new();
    private readonly RequestInFlightGate inFlightGate = new();
    private readonly HashSet<Task> activeWorkTasks = new();
    private CancellationTokenSource? cancellation;
    private TranslationWorkQueue? queue;
    private SceneAttemptCoordinator? sceneCoordinator;
    private TranslationWorkQueue? stoppingQueue;
    private Task[] tasks = Array.Empty<Task>();
    private Task<bool>? stopTask;
    private Task? eventualStopTask;
    private int stopFinalized;

    public MachineTranslator(
        TranslationCache cache,
        Func<string, CancellationToken, Task<string?>> translate,
        int maxInFlight,
        TimeSpan translatePeriod,
        TranslationPriorityBacklog? priorityBacklog = null,
        Action<string>? diagnostic = null,
        TimeSpan? statusPeriod = null,
        TranslationRetryPolicy? retryPolicy = null,
        Func<string, Action?, CancellationToken, Task<ScenePromptResponse>>? sendScenePrompt = null,
        SceneRetryRegistry? sceneRetries = null,
        int sceneRetryCount = 3,
        TimeSpan? urgentPreemptGracePeriod = null)
    {
        this.cache = cache;
        this.translate = translate;
        this.sendScenePrompt = sendScenePrompt;
        this.maxInFlight = TranslationBudget.ClampMaxInFlight(maxInFlight);
        this.sceneRetryCount = Math.Max(0, sceneRetryCount);
        this.translatePeriod = translatePeriod > TimeSpan.Zero ? translatePeriod : TimeSpan.FromMilliseconds(100);
        this.urgentPreemptGracePeriod = urgentPreemptGracePeriod is { } grace && grace >= TimeSpan.Zero
            ? grace
            : DefaultUrgentPreemptGracePeriod;
        this.priorityBacklog = priorityBacklog ?? new TranslationPriorityBacklog();
        this.diagnostic = diagnostic;
        this.statusPeriod = statusPeriod > TimeSpan.Zero ? statusPeriod.Value : TimeSpan.FromSeconds(30);
        this.retryPolicy = retryPolicy ?? new TranslationRetryPolicy();
        this.sceneRetries = sceneRetries ?? new SceneRetryRegistry();
    }

    public (int Completed, int InFlight, int Failed) ProgressSnapshot => progress.Snapshot();

    public bool IsReserved(string template)
    {
        if (string.IsNullOrEmpty(template))
            return false;
        lock (lifecycleGate)
            return queue?.IsReserved(template) == true;
    }

    public bool EnqueuePriority(string template)
    {
        return EnqueuePriority(template, PendingGeneration(template));
    }

    public bool EnqueuePriority(string template, long pendingGeneration)
    {
        lock (lifecycleGate)
        {
            return retryPolicy.TrySchedule(
                template,
                () => queue?.EnqueuePriority(template, pendingGeneration)
                    ?? priorityBacklog.Enqueue(template, pendingGeneration));
        }
    }

    public bool EnqueueNormal(string template)
    {
        return EnqueueNormal(template, PendingGeneration(template));
    }

    public bool EnqueueNormal(string template, long pendingGeneration)
    {
        lock (lifecycleGate)
        {
            return retryPolicy.TrySchedule(
                template,
                () => queue?.Enqueue(template, pendingGeneration) ?? false);
        }
    }

    public int EnqueueSceneFallback(IEnumerable<string> templates)
    {
        if (templates == null)
            return 0;

        var queued = 0;
        foreach (var template in templates)
        {
            if (!TextTemplate.IsTranslationCandidate(template)
                || HasValidCachedTranslation(template))
                continue;

            _ = cache.ObserveNormalGeneration(template, template);
            if (!cache.TryGetPendingGeneration(template, out var pendingGeneration)
                || pendingGeneration <= 0)
                continue;

            lock (lifecycleGate)
            {
                if (retryPolicy.TrySchedule(
                        template,
                        () => queue?.EnqueueSceneFallback(template, pendingGeneration) ?? false))
                {
                    queued++;
                }
            }
        }
        return queued;
    }

    public int SubmitScene(
        long sceneId,
        IReadOnlyList<SceneDialogueLine> sceneLines,
        IReadOnlyList<int> targetIndices)
    {
        if (sendScenePrompt == null
            || sceneId <= 0
            || sceneLines == null
            || sceneLines.Count == 0
            || targetIndices == null
            || targetIndices.Count == 0)
            return 0;

        TranslationWorkQueue? workQueue;
        SceneAttemptCoordinator? coordinator;
        CancellationToken token;
        lock (lifecycleGate)
        {
            if (cancellation == null || stopTask != null || queue == null || sceneCoordinator == null)
                return 0;
            workQueue = queue;
            coordinator = sceneCoordinator;
            token = cancellation.Token;
        }

        var retryPriority = sceneRetries.Contains(sceneId);
        var reservedTemplates = new HashSet<string>(StringComparer.Ordinal);
        var acceptedIndices = new List<int>(targetIndices.Count);
        var deferredBudgetTemplates = new List<string>();
        var inFlightElsewhere = 0;
        var reservedElsewhere = 0;
        var budgetDeferred = 0;

        foreach (var lineIndex in targetIndices)
        {
            if (lineIndex < 0 || lineIndex >= sceneLines.Count)
                continue;

            var line = sceneLines[lineIndex];
            if (!TextTemplate.IsTranslationCandidate(line.Text))
                continue;

            var template = TextTemplate.Normalize(line.Text).Template;
            if (reservedTemplates.Contains(template))
            {
                acceptedIndices.Add(lineIndex);
                continue;
            }

            if (workQueue.TryReserve(
                    template,
                    () => !retryPriority
                        && !cache.IsSentenceGenerated(template)
                        && HasValidCachedTranslation(template),
                    out var status))
            {
                reservedTemplates.Add(template);
                acceptedIndices.Add(lineIndex);
                if (status == TranslationReservationStatus.ReservedFromPending)
                    cache.UnmarkPromotedPending(template);
                continue;
            }

            if (status == TranslationReservationStatus.AlreadyInFlight)
                inFlightElsewhere++;
            else if (status == TranslationReservationStatus.AlreadyReserved)
                reservedElsewhere++;
            else if (status == TranslationReservationStatus.BudgetExceeded)
            {
                budgetDeferred++;
                deferredBudgetTemplates.Add(template);
            }
        }

        if (inFlightElsewhere > 0 || reservedElsewhere > 0 || budgetDeferred > 0)
        {
            retryPriority = true;
            _ = sceneRetries.Mark(sceneId);
        }

        var deferredQueued = budgetDeferred > 0
            ? EnqueueSceneFallback(deferredBudgetTemplates)
            : 0;

        var expectedFailureEpoch = sceneRetries.GetFailureEpoch(sceneId);
        var reservationScope = new TranslationReservationScope(workQueue, reservedTemplates);
        IReadOnlyList<SceneBatchPlan> plans;
        try
        {
            plans = SceneTranslationBatch.CreateBatches(sceneId, sceneLines, acceptedIndices);
        }
        catch (Exception exception)
        {
            reservationScope.Dispose();
            _ = sceneRetries.Mark(sceneId);
            var fallback = EnqueueSceneFallback(reservedTemplates) + deferredQueued;
            Diagnose($"[Scene] prepare failed scene={sceneId}: {exception.GetType().Name} fallbackQueued={fallback}");
            return fallback;
        }

        var plannedTemplates = new HashSet<string>(
            plans.SelectMany(plan => plan.ReservedTemplates),
            StringComparer.Ordinal);
        var unplanned = reservedTemplates
            .Where(template => !plannedTemplates.Contains(template))
            .ToArray();
        var unplannedQueued = 0;
        if (unplanned.Length > 0)
        {
            reservationScope.Release(unplanned);
            _ = sceneRetries.Mark(sceneId);
            unplannedQueued = EnqueueSceneFallback(unplanned);
        }

        var totalTargets = plans.Sum(plan => plan.Batch.TargetCount);
        if (totalTargets == 0)
        {
            reservationScope.Dispose();
            return unplannedQueued + deferredQueued;
        }

        var clearRetryMarkerOnSuccess = retryPriority
            && inFlightElsewhere == 0
            && reservedElsewhere == 0
            && budgetDeferred == 0
            && unplanned.Length == 0;
        if (!TryTrackActiveTask(
                workQueue,
                () => ExecuteSceneAsync(
                    sceneId,
                    plans,
                    reservationScope,
                    workQueue,
                    coordinator,
                    clearRetryMarkerOnSuccess,
                    expectedFailureEpoch,
                    token)))
        {
            reservationScope.Dispose();
            return 0;
        }

        Diagnose(
            $"[Scene] scheduled scene={sceneId} batches={plans.Count} targets={totalTargets} "
            + $"retryPriority={retryPriority}");
        return totalTargets;
    }

    public bool Cancel(string template)
    {
        return Cancel(template, long.MaxValue);
    }

    public bool Cancel(string template, long pendingGeneration)
    {
        lock (lifecycleGate)
        {
            return (queue?.Cancel(template, pendingGeneration) ?? false)
                | (stoppingQueue?.Cancel(template, pendingGeneration) ?? false)
                | priorityBacklog.Cancel(template, pendingGeneration);
        }
    }

    public void Start()
    {
        lock (lifecycleGate)
        {
            if (cancellation != null || stopTask != null) return;
            cancellation = new CancellationTokenSource();
            queue = new TranslationWorkQueue();
            sceneCoordinator = new SceneAttemptCoordinator(inFlightGate, queue);
            SchedulePriorityBacklog(queue);
            foreach (var template in cache.PendingSnapshot())
            {
                var pendingGeneration = PendingGeneration(template);
                retryPolicy.TrySchedule(
                    template,
                    () => queue.EnqueueBackground(template, pendingGeneration));
            }
            var running = new List<Task>(3)
            {
                DispatcherLoopAsync(queue, sceneCoordinator, cancellation.Token),
                PeriodicScanAsync(queue, cancellation.Token),
            };
            if (diagnostic != null) running.Add(StatusLoopAsync(cancellation.Token));
            tasks = running.ToArray();
        }
    }

    /// <summary>
    /// Completes only when every worker and scene task has actually ended.
    /// </summary>
    public Task StopCompletion
    {
        get
        {
            lock (lifecycleGate)
                return eventualStopTask ?? Task.CompletedTask;
        }
    }

    public Task<bool> StopAsync(TimeSpan? timeout = null)
    {
        CancellationTokenSource? source;
        TranslationWorkQueue? workQueue;
        SceneAttemptCoordinator? coordinator;
        Task[] running;
        lock (lifecycleGate)
        {
            if (stopTask != null)
                return stopTask;
            source = cancellation;
            if (source == null)
                return Task.FromResult(true);
            workQueue = queue;
            coordinator = sceneCoordinator;
            if (workQueue != null)
            {
                foreach (var work in workQueue.DrainPriorityWork())
                    priorityBacklog.Enqueue(work.Template, work.PendingGeneration);
            }
            cancellation = null;
            queue = null;
            sceneCoordinator = null;
            stoppingQueue = workQueue;
            running = tasks.Concat(activeWorkTasks).ToArray();
            tasks = Array.Empty<Task>();
            source.Cancel();
            Volatile.Write(ref stopFinalized, 0);
            eventualStopTask = Task.WhenAll(running);
            stopTask = StopCoreAsync(source, workQueue, coordinator, eventualStopTask, timeout);
            return stopTask;
        }
    }

    private async Task<bool> StopCoreAsync(
        CancellationTokenSource source,
        TranslationWorkQueue? workQueue,
        SceneAttemptCoordinator? coordinator,
        Task all,
        TimeSpan? timeout)
    {
        try
        {
            if (timeout.HasValue)
            {
                var boundedTimeout = timeout.Value < TimeSpan.Zero ? TimeSpan.Zero : timeout.Value;
                var completed = await Task.WhenAny(all, Task.Delay(boundedTimeout)).ConfigureAwait(false);
                if (!ReferenceEquals(completed, all))
                {
                    if (all.IsCompleted)
                    {
                        await all.ConfigureAwait(false);
                        return true;
                    }
                    ObserveEventualStop(all, source, workQueue, coordinator);
                    return false;
                }
            }

            await all.ConfigureAwait(false);
            return true;
        }
        catch
        {
            if (!all.IsCompleted)
                ObserveEventualStop(all, source, workQueue, coordinator);
            throw;
        }
        finally
        {
            if (all.IsCompleted)
                FinalizeStop(source, workQueue, coordinator);
        }
    }

    private void ObserveEventualStop(
        Task all,
        CancellationTokenSource source,
        TranslationWorkQueue? workQueue,
        SceneAttemptCoordinator? coordinator)
    {
        _ = all.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                FinalizeStop(source, workQueue, coordinator);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void FinalizeStop(
        CancellationTokenSource source,
        TranslationWorkQueue? workQueue,
        SceneAttemptCoordinator? coordinator)
    {
        if (Interlocked.Exchange(ref stopFinalized, 1) != 0)
            return;
        lock (lifecycleGate)
        {
            if (ReferenceEquals(stoppingQueue, workQueue))
                stoppingQueue = null;
        }
        coordinator?.Dispose();
        source.Dispose();
    }

    public void Stop()
    {
        var stop = StopAsync();
        _ = stop.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryTrackActiveTask(TranslationWorkQueue expectedQueue, Func<Task> startTask)
    {
        lock (lifecycleGate)
        {
            if (!ReferenceEquals(queue, expectedQueue) || cancellation == null || stopTask != null)
                return false;

            var task = startTask();
            if (!task.IsCompleted)
            {
                activeWorkTasks.Add(task);
                _ = task.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        lock (lifecycleGate)
                            activeWorkTasks.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            return true;
        }
    }

    private async Task DispatcherLoopAsync(
        TranslationWorkQueue workQueue,
        SceneAttemptCoordinator coordinator,
        CancellationToken token)
    {
        try
        {
            while (true)
            {
                await workQueue.WaitForWorkAsync(token).ConfigureAwait(false);
                if (coordinator.ShouldDeferNonUrgentDispatch)
                {
                    await coordinator.WaitWhileDeferredAsync(token).ConfigureAwait(false);
                    continue;
                }

                await inFlightGate.WaitAcquireAsync(maxInFlight, token).ConfigureAwait(false);
                if (coordinator.ShouldDeferNonUrgentDispatch)
                {
                    inFlightGate.Release();
                    continue;
                }

                if (!workQueue.TryDequeueWork(out var work))
                {
                    inFlightGate.Release();
                    continue;
                }

                if (!TryTrackActiveTask(workQueue, () => ExecuteWorkItemAsync(workQueue, work, token)))
                {
                    if (work.Level == TranslationPriority.Urgent)
                    {
                        workQueue.TryIfNotCanceled(
                            work,
                            () => priorityBacklog.Enqueue(work.Template, work.PendingGeneration));
                    }
                    workQueue.Finish(work, false);
                    inFlightGate.Release();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteWorkItemAsync(
        TranslationWorkQueue workQueue,
        TranslationWorkQueue.Work work,
        CancellationToken token)
    {
        try
        {
            var template = work.Template;
            if (workQueue.IsCanceled(work))
            {
                workQueue.Finish(work, false);
                return;
            }

            if (HasValidCachedTranslation(template))
            {
                retryPolicy.RecordSuccess(template);
                workQueue.Finish(work, true);
                return;
            }

            var succeeded = false;
            var translationFailed = false;
            var finished = false;
            progress.Start();
            try
            {
                var translated = await translate(template, token).ConfigureAwait(false);
                if (workQueue.IsCanceled(work))
                {
                    progress.Fail(template);
                }
                else if (!string.IsNullOrWhiteSpace(translated)
                    && !string.Equals(template, translated, StringComparison.Ordinal)
                    && TextTemplate.HasSamePlaceholders(template, translated)
                    && TextTemplate.HasSameMarkup(template, translated))
                {
                    token.ThrowIfCancellationRequested();
                    var supersededByScene = false;
                    succeeded = workQueue.TryIfNotCanceled(
                        work,
                        () => cache.StoreGeneratedIfPending(
                            template,
                            work.PendingGeneration,
                            translated,
                            out supersededByScene));
                    if (succeeded)
                    {
                        retryPolicy.RecordSuccess(template);
                        progress.Complete(template);
                    }
                    else if (supersededByScene || cache.IsSceneAuthoritative(template))
                    {
                        succeeded = true;
                        retryPolicy.RecordSuccess(template);
                        progress.FinishInFlight();
                    }
                    else
                    {
                        progress.Fail(template);
                    }
                }
                else
                {
                    translationFailed = !token.IsCancellationRequested;
                    progress.Fail(template);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                progress.Fail(template);
            }
            catch
            {
                if (!token.IsCancellationRequested && !workQueue.IsCanceled(work))
                    translationFailed = true;
                progress.Fail(template);
            }
            finally
            {
                TranslationRetryDecision? decision = null;
                if (translationFailed)
                {
                    lock (lifecycleGate)
                    {
                        if (ReferenceEquals(queue, workQueue) && !token.IsCancellationRequested)
                        {
                            var recorded = false;
                            if (work.PendingGeneration > 0)
                            {
                                recorded = cache.TryIfPendingGeneration(
                                    template,
                                    work.PendingGeneration,
                                    () => decision = retryPolicy.RecordFailure(template));
                            }
                            else
                            {
                                decision = retryPolicy.RecordFailure(template);
                                recorded = true;
                            }
                            if (recorded)
                            {
                                var deferred = workQueue.FinishAndTakeDeferred(work);
                                finished = true;
                                if (deferred.HasValue && !decision!.Value.Blocked)
                                {
                                    priorityBacklog.Enqueue(
                                        deferred.Value.Template,
                                        deferred.Value.PendingGeneration);
                                }
                            }
                        }
                    }
                    if (decision?.Blocked == true)
                    {
                        Diagnose(
                            $"template blocked after {decision.Value.Failures} failed work cycles for this process.");
                    }
                }
                if (!finished && !succeeded && token.IsCancellationRequested && work.Level == TranslationPriority.Urgent)
                {
                    workQueue.TryIfNotCanceled(
                        work,
                        () => priorityBacklog.Enqueue(template, work.PendingGeneration));
                }
                if (!finished)
                    workQueue.Finish(work, succeeded);
            }
        }
        finally
        {
            inFlightGate.Release();
        }
    }

    private async Task ExecuteSceneAsync(
        long sceneId,
        IReadOnlyList<SceneBatchPlan> plans,
        TranslationReservationScope reservationScope,
        TranslationWorkQueue workQueue,
        SceneAttemptCoordinator coordinator,
        bool clearRetryMarkerOnSuccess,
        long expectedFailureEpoch,
        CancellationToken token)
    {
        using (reservationScope)
        {
            var maxAttempts = SceneTranslationBatch.GetMaximumAttempts(sceneRetryCount);
            var allSucceeded = await coordinator.ExecuteScenePlansAsync(
                plans,
                reservationScope,
                maxAttempts,
                () => maxInFlight,
                urgentPreemptGracePeriod,
                () => !token.IsCancellationRequested,
                sendPromptAsync: async (_, _, _, requestPrompt, onFirstDispatchReady, ct) =>
                {
                    progress.Start();
                    try
                    {
                        return await sendScenePrompt!(requestPrompt, onFirstDispatchReady, ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        progress.FinishInFlight();
                    }
                },
                tryCommitTranslations: (chunkIndex, chunkCount, attempt, translations) =>
                {
                    var stored = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var pair in translations)
                    {
                        if (cache.StoreGenerated(pair.Key, pair.Value))
                        {
                            workQueue.CompleteCached(pair.Key);
                            retryPolicy.RecordSuccess(pair.Key);
                            progress.RecordTranslated(pair.Key);
                            stored.Add(pair.Key);
                        }
                    }

                    Diagnose(
                        $"[Scene] completed scene={sceneId} chunk={chunkIndex}/{chunkCount} "
                        + $"attempt={attempt}/{maxAttempts} accepted={translations.Count} stored={stored.Count}");
                    return stored;
                },
                onRetryScheduled: (chunkIndex, chunkCount, attempt, preempted, reason) =>
                {
                    var retryDelayMs = preempted ? 50 : attempt * 250;
                    Diagnose(
                        $"[Scene] retry scene={sceneId} chunk={chunkIndex}/{chunkCount} "
                        + $"nextAttempt={attempt + 1}/{maxAttempts} reason={reason} delayMs={retryDelayMs}");
                    return retryDelayMs;
                },
                onChunkFallback: (chunkIndex, chunkCount, fallbackTemplates, reason, attempts) =>
                {
                    sceneRetries.Mark(sceneId);
                    var queued = EnqueueSceneFallback(fallbackTemplates);
                    Diagnose(
                        $"[Scene] sentence-fallback scene={sceneId} chunk={chunkIndex}/{chunkCount} "
                        + $"reason={reason} attempts={attempts}/{maxAttempts} "
                        + $"templates={fallbackTemplates.Count} queued={queued}");
                },
                onChunkCancelled: (chunkIndex, chunkCount) =>
                    Diagnose($"[Scene] cancelled scene={sceneId} chunk={chunkIndex}/{chunkCount}"),
                onRejected: (chunkIndex, chunkCount, attempt, rejectionReason) =>
                    Diagnose(
                        $"[Scene] response rejected scene={sceneId} chunk={chunkIndex}/{chunkCount} "
                        + $"attempt={attempt}/{maxAttempts} reason={rejectionReason}"),
                onPreempted: (chunkIndex, chunkCount, attempt) =>
                    Diagnose(
                        $"[Scene] preempted-for-urgent scene={sceneId} chunk={chunkIndex}/{chunkCount} "
                        + $"attempt={attempt}/{maxAttempts}"),
                shutdownToken: token).ConfigureAwait(false);

            if (allSucceeded && clearRetryMarkerOnSuccess)
                sceneRetries.TryComplete(sceneId, expectedFailureEpoch);
        }
    }

    private async Task PeriodicScanAsync(TranslationWorkQueue workQueue, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(translatePeriod, token).ConfigureAwait(false);
                lock (lifecycleGate)
                {
                    if (!ReferenceEquals(queue, workQueue)) continue;
                    SchedulePriorityBacklog(workQueue);
                    foreach (var template in cache.PendingSnapshot())
                    {
                        var pendingGeneration = PendingGeneration(template);
                        retryPolicy.TrySchedule(
                            template,
                            () => workQueue.EnqueueBackground(template, pendingGeneration));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task StatusLoopAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(statusPeriod, token).ConfigureAwait(false);
                var snapshot = progress.Snapshot();
                Diagnose($"pending={cache.PendingSnapshot().Count}, completed={snapshot.Completed}, in-flight={snapshot.InFlight}, failed={snapshot.Failed}, blocked={retryPolicy.BlockedCount}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private bool HasValidCachedTranslation(string template) =>
        cache.TryGetGenerated(template, out var existing)
        && TextTemplate.HasSamePlaceholders(template, existing)
        && TextTemplate.HasSameMarkup(template, existing);

    private long PendingGeneration(string template) =>
        cache.TryGetPendingGeneration(template, out var generation) ? generation : 0;

    private void SchedulePriorityBacklog(TranslationWorkQueue workQueue)
    {
        foreach (var work in priorityBacklog.DrainWork())
        {
            var pendingGeneration = work.PendingGeneration > 0
                ? work.PendingGeneration
                : PendingGeneration(work.Template);
            var accepted = retryPolicy.TrySchedule(
                work.Template,
                () => workQueue.EnqueuePriority(
                    work.Template,
                    pendingGeneration),
                out var retryLater);
            if (!accepted && retryLater)
                priorityBacklog.Enqueue(work.Template, work.PendingGeneration);
        }
    }

    private void Diagnose(string message)
    {
        try
        {
            diagnostic?.Invoke(message);
        }
        catch
        {
        }
    }
}
