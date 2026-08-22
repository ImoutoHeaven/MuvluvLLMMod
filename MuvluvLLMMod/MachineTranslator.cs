namespace MuvluvLLMMod;

public sealed class MachineTranslator : IDisposable
{
    private readonly object lifecycleGate = new();
    private readonly TranslationCache cache;
    private readonly Func<string, CancellationToken, Task<string?>> translate;
    private readonly int maxInFlight;
    private readonly TimeSpan translatePeriod;
    private readonly Action<string>? diagnostic;
    private readonly TimeSpan statusPeriod;
    private readonly TranslationPriorityBacklog priorityBacklog;
    private readonly TranslationRetryPolicy retryPolicy;
    private readonly TranslationProgress progress = new();
    private CancellationTokenSource? cancellation;
    private TranslationWorkQueue? queue;
    private TranslationWorkQueue? stoppingQueue;
    private Task[] tasks = Array.Empty<Task>();
    private Task<bool>? stopTask;
    private int stopFinalized;

    public MachineTranslator(
        TranslationCache cache,
        Func<string, CancellationToken, Task<string?>> translate,
        int maxInFlight,
        TimeSpan translatePeriod,
        TranslationPriorityBacklog? priorityBacklog = null,
        Action<string>? diagnostic = null,
        TimeSpan? statusPeriod = null,
        TranslationRetryPolicy? retryPolicy = null)
    {
        this.cache = cache;
        this.translate = translate;
        this.maxInFlight = TranslationBudget.ClampMaxInFlight(maxInFlight);
        this.translatePeriod = translatePeriod > TimeSpan.Zero ? translatePeriod : TimeSpan.FromMilliseconds(100);
        this.priorityBacklog = priorityBacklog ?? new TranslationPriorityBacklog();
        this.diagnostic = diagnostic;
        this.statusPeriod = statusPeriod > TimeSpan.Zero ? statusPeriod.Value : TimeSpan.FromSeconds(30);
        this.retryPolicy = retryPolicy ?? new TranslationRetryPolicy();
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

    public (int Completed, int InFlight, int Failed) ProgressSnapshot => progress.Snapshot();

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
            SchedulePriorityBacklog(queue);
            foreach (var template in cache.PendingSnapshot())
            {
                var pendingGeneration = PendingGeneration(template);
                retryPolicy.TrySchedule(template, () => queue.Enqueue(template, pendingGeneration));
            }
            var running = new List<Task>(maxInFlight + 2);
            for (var index = 0; index < maxInFlight; index++) running.Add(WorkerLoopAsync(queue, cancellation.Token));
            running.Add(PeriodicScanAsync(queue, cancellation.Token));
            if (diagnostic != null) running.Add(StatusLoopAsync(cancellation.Token));
            tasks = running.ToArray();
        }
    }

    public Task<bool> StopAsync(TimeSpan? timeout = null)
    {
        CancellationTokenSource? source;
        TranslationWorkQueue? workQueue;
        Task[] running;
        lock (lifecycleGate)
        {
            if (stopTask != null)
                return stopTask;
            source = cancellation;
            if (source == null)
                return Task.FromResult(true);
            workQueue = queue;
            if (workQueue != null)
            {
                foreach (var work in workQueue.DrainPriorityWork())
                    priorityBacklog.Enqueue(work.Template, work.PendingGeneration);
            }
            cancellation = null;
            queue = null;
            stoppingQueue = workQueue;
            running = tasks;
            tasks = Array.Empty<Task>();
            source.Cancel();
            Volatile.Write(ref stopFinalized, 0);
            stopTask = StopCoreAsync(source, workQueue, running, timeout);
            return stopTask;
        }
    }

    private async Task<bool> StopCoreAsync(
        CancellationTokenSource source,
        TranslationWorkQueue? workQueue,
        Task[] running,
        TimeSpan? timeout)
    {
        var all = Task.WhenAll(running);
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
                    ObserveEventualStop(all, source, workQueue);
                    return false;
                }
            }

            await all.ConfigureAwait(false);
            return true;
        }
        catch
        {
            if (!all.IsCompleted)
                ObserveEventualStop(all, source, workQueue);
            throw;
        }
        finally
        {
            if (all.IsCompleted)
                FinalizeStop(source, workQueue);
        }
    }

    private void ObserveEventualStop(
        Task all,
        CancellationTokenSource source,
        TranslationWorkQueue? workQueue)
    {
        _ = all.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                FinalizeStop(source, workQueue);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void FinalizeStop(
        CancellationTokenSource source,
        TranslationWorkQueue? workQueue)
    {
        if (Interlocked.Exchange(ref stopFinalized, 1) != 0)
            return;
        lock (lifecycleGate)
        {
            if (ReferenceEquals(stoppingQueue, workQueue)) stoppingQueue = null;
        }
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

    private async Task WorkerLoopAsync(TranslationWorkQueue workQueue, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var work = await workQueue.DequeueAsync(token).ConfigureAwait(false);
                var template = work.Template;
                var succeeded = false;
                var translationFailed = false;
                var finished = false;
                if (workQueue.IsCanceled(work))
                {
                    workQueue.Finish(work, false);
                    continue;
                }
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
                        succeeded = workQueue.TryIfNotCanceled(
                            work,
                            () => cache.StoreGeneratedIfPending(template, work.PendingGeneration, translated));
                        if (succeeded)
                        {
                            retryPolicy.RecordSuccess(template);
                            progress.Complete(template);
                        }
                        else progress.Fail(template);
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
                    throw;
                }
                catch
                {
                    if (!token.IsCancellationRequested && !workQueue.IsCanceled(work)) translationFailed = true;
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
                                        priorityBacklog.Enqueue(deferred.Value.Template, deferred.Value.PendingGeneration);
                                }
                            }
                        }
                        if (decision?.Blocked == true)
                            Diagnose($"template blocked after {decision.Value.Failures} failed work cycles for this process.");
                    }
                    if (!finished && !succeeded && token.IsCancellationRequested && work.Priority)
                        workQueue.TryIfNotCanceled(
                            work,
                            () => priorityBacklog.Enqueue(template, work.PendingGeneration));
                    if (!finished) workQueue.Finish(work, succeeded);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
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
                            () => workQueue.Enqueue(template, pendingGeneration));
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
            if (!accepted && retryLater) priorityBacklog.Enqueue(work.Template, work.PendingGeneration);
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
