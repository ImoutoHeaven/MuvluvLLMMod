namespace MuvluvLLMMod;

public sealed class MachineTranslatorLifecycle
{
    private readonly object gate = new();
    private readonly TranslationPriorityBacklog priorityBacklog = new();
    private readonly Dictionary<string, (long Backlog, long Pending)> transitionCancellationCutoffs = new(StringComparer.Ordinal);
    private readonly LinkedList<string> transitionCancellationOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> transitionCancellationNodes = new(StringComparer.Ordinal);
    private long transitionCancellationUtf8Bytes;
    private readonly Action<Exception>? diagnostic;
    private readonly TimeSpan shutdownTimeout;
    private TranslationRetryPolicy? retryPolicy;
    private MachineTranslator? current;
    private MachineTranslator? stopping;
    private RequestRateLimiter? limiter;
    private Task transition = Task.CompletedTask;
    private Task? shutdownTask;
    private int transitionsInFlight;
    private int version;
    private bool initialized;
    private bool shutdown;
    private bool shutdownTimedOut;

    public MachineTranslatorLifecycle(
        Action<Exception>? diagnostic = null,
        TranslationRetryPolicy? retryPolicy = null,
        TimeSpan? shutdownTimeout = null)
    {
        this.diagnostic = diagnostic;
        this.retryPolicy = retryPolicy;
        this.shutdownTimeout = shutdownTimeout is { } value && value > TimeSpan.Zero
            ? (value < TranslationBudget.MaxShutdownTimeout
                ? value
                : TranslationBudget.MaxShutdownTimeout)
            : TranslationBudget.DefaultShutdownTimeout;
    }

    public Task TransitionTask
    {
        get { lock (gate) return shutdownTask ?? transition; }
    }

    public bool IsShutdown
    {
        get { lock (gate) return shutdown; }
    }

    public long RetainedCancellationUtf8Bytes
    {
        get { lock (gate) return transitionCancellationUtf8Bytes; }
    }

    public bool ShutdownTimedOut
    {
        get { lock (gate) return shutdownTimedOut; }
    }

    public (int Completed, int InFlight, int Failed) ProgressSnapshot
    {
        get
        {
            lock (gate)
                return current?.ProgressSnapshot ?? (0, 0, 0);
        }
    }

    /// <summary>
    /// Initializes exactly once for this lifecycle object. A second initialize is rejected rather
    /// than replacing a worker or a transition-installed machine. Replacement belongs to Reload,
    /// whose serialized transition owns both sides of the handoff.
    /// </summary>
    public bool Initialize(
        bool enabled,
        int requestsPerSecond,
        Func<RequestRateLimiter, TranslationPriorityBacklog, MachineTranslator>? factory,
        TranslationRetryPolicy? nextRetryPolicy = null)
    {
        lock (gate)
        {
            if (shutdown || initialized || current != null || stopping != null || transitionsInFlight != 0)
                return false;

            initialized = true;
            ++version;
            if (nextRetryPolicy != null)
                retryPolicy = nextRetryPolicy;
            if (!enabled || factory == null)
                return true;

            MachineTranslator? next = null;
            try
            {
                limiter = new RequestRateLimiter(Math.Max(1, requestsPerSecond));
                next = factory(limiter, priorityBacklog)
                    ?? throw new InvalidOperationException("machine translator factory returned null");
                current = next;
                next.Start();
                return true;
            }
            catch
            {
                current = null;
                limiter = null;
                initialized = false;
                if (next != null)
                    _ = StopSafelyAsync(next);
                throw;
            }
        }
    }

    public bool Reload(
        bool enabled,
        int requestsPerSecond,
        Func<RequestRateLimiter, TranslationPriorityBacklog, MachineTranslator>? factory,
        TranslationRetryPolicy? nextRetryPolicy = null)
    {
        lock (gate)
        {
            if (shutdown || !initialized || transitionsInFlight >= TranslationBudget.MaxTransitions)
                return false;

            if (nextRetryPolicy != null)
                retryPolicy = nextRetryPolicy;
            var generation = ++version;
            transitionsInFlight++;
            transition = RunTransitionAsync(
                transition,
                generation,
                enabled,
                requestsPerSecond,
                factory);
            Observe(transition);
            return true;
        }
    }

    public bool EnqueuePriority(string template)
    {
        lock (gate)
        {
            if (shutdown)
                return false;
            return current?.EnqueuePriority(template)
                ?? retryPolicy?.TryRetain(template, () => priorityBacklog.Enqueue(template))
                ?? priorityBacklog.Enqueue(template);
        }
    }

    public bool EnqueuePriority(string template, long pendingGeneration)
    {
        lock (gate)
        {
            if (shutdown)
                return false;
            return current?.EnqueuePriority(template, pendingGeneration)
                ?? retryPolicy?.TryRetain(template, () => priorityBacklog.Enqueue(template, pendingGeneration))
                ?? priorityBacklog.Enqueue(template, pendingGeneration);
        }
    }

    public bool EnqueueNormal(string template)
    {
        lock (gate)
        {
            return !shutdown && (current?.EnqueueNormal(template) ?? false);
        }
    }

    public bool EnqueueNormal(string template, long pendingGeneration)
    {
        lock (gate)
        {
            return !shutdown && (current?.EnqueueNormal(template, pendingGeneration) ?? false);
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
            if (shutdown)
                return false;

            var canceled = (current?.Cancel(template, pendingGeneration) ?? false)
                | (stopping?.Cancel(template, pendingGeneration) ?? false);
            var backlogCancellation = priorityBacklog.CancelAndGetCutoff(template, pendingGeneration);
            canceled |= backlogCancellation.Removed;
            if (transitionsInFlight == 0)
                return canceled;
            if (!transitionCancellationCutoffs.TryGetValue(template, out var existing))
            {
                AddTransitionCancellationUnsafe(
                    template,
                    (backlogCancellation.Cutoff, pendingGeneration));
            }
            else
            {
                transitionCancellationCutoffs[template] = (
                    Math.Max(existing.Backlog, backlogCancellation.Cutoff),
                    Math.Max(existing.Pending, pendingGeneration));
                TouchTransitionCancellationUnsafe(template);
            }
            return true;
        }
    }

    public void Shutdown()
    {
        ShutdownAsync().GetAwaiter().GetResult();
    }

    public Task ShutdownAsync()
    {
        lock (gate)
        {
            if (shutdownTask != null)
                return shutdownTask;

            shutdown = true;
            ++version;
            var active = current;
            var stoppingMachine = stopping;
            current = null;
            var pendingTransition = transition;
            shutdownTask = CompleteShutdownAsync(active, stoppingMachine, pendingTransition);
            Observe(shutdownTask);
            return shutdownTask;
        }
    }

    private async Task CompleteShutdownAsync(
        MachineTranslator? active,
        MachineTranslator? stoppingMachine,
        Task pendingTransition)
    {
        var deadline = DateTime.UtcNow + shutdownTimeout;
        Exception? failure = await AwaitBounded(
            pendingTransition,
            Remaining(deadline),
            "machine transition shutdown").ConfigureAwait(false);

        var activeFailure = await StopSafelyAsync(active, Remaining(deadline)).ConfigureAwait(false);
        failure ??= activeFailure;
        if (stoppingMachine != null && !ReferenceEquals(stoppingMachine, active))
        {
            var stoppingFailure = await StopSafelyAsync(
                stoppingMachine,
                Remaining(deadline)).ConfigureAwait(false);
            failure ??= stoppingFailure;
        }

        lock (gate)
        {
            current = null;
            stopping = null;
            limiter = null;
            shutdownTimedOut = failure is TimeoutException
                || failure?.InnerException is TimeoutException;
            ClearTransitionCancellationsUnsafe();
        }

        if (failure != null)
            throw new InvalidOperationException("machine translator shutdown failed", failure);
    }

    private async Task RunTransitionAsync(
        Task previous,
        int generation,
        bool enabled,
        int requestsPerSecond,
        Func<RequestRateLimiter, TranslationPriorityBacklog, MachineTranslator>? factory)
    {
        // Keep transition continuations off Unity's synchronization context. Cleanup is a
        // synchronous BepInEx API and must be able to settle this task on the main thread.
        await Task.Run(static () => { }).ConfigureAwait(false);
        try
        {
            await previous.ConfigureAwait(false);

            MachineTranslator? old;
            lock (gate)
            {
                // A stale transition must not take ownership of a worker from a newer load.
                if (shutdown || generation != version)
                    return;
                old = current;
                current = null;
                stopping = old;
            }

            if (old != null
                && !await old.StopAsync(shutdownTimeout).ConfigureAwait(false))
                throw new TimeoutException("old machine translator did not stop before the transition budget");
            lock (gate)
            {
                if (ReferenceEquals(stopping, old))
                    stopping = null;
            }

            RequestRateLimiter nextLimiter;
            lock (gate)
            {
                if (shutdown || generation != version || !enabled || factory == null)
                    return;
                nextLimiter = new RequestRateLimiter(Math.Max(1, requestsPerSecond), limiter?.NextStart ?? default);
                limiter = nextLimiter;
            }

            var next = factory(nextLimiter, priorityBacklog);
            var accepted = false;
            lock (gate)
            {
                if (!shutdown && generation == version)
                {
                    FilterTransitionCancellationsUnsafe();
                    current = next;
                    next.Start();
                    accepted = true;
                }
            }

            if (!accepted)
                _ = await StopSafelyAsync(next, shutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Diagnose(exception);
        }
        finally
        {
            lock (gate)
            {
                FilterTransitionCancellationsUnsafe();
                transitionsInFlight--;
                if (transitionsInFlight == 0)
                    ClearTransitionCancellationsUnsafe();
            }
        }
    }

    private async Task<Exception?> StopSafelyAsync(
        MachineTranslator? machine,
        TimeSpan? timeout = null)
    {
        if (machine == null)
            return null;

        try
        {
            if (!await machine.StopAsync(timeout).ConfigureAwait(false))
            {
                var timeoutException = new TimeoutException(
                    "machine translator worker did not stop before the shutdown deadline");
                Diagnose(timeoutException);
                return timeoutException;
            }
            return null;
        }
        catch (Exception exception)
        {
            Diagnose(exception);
            return exception;
        }
    }

    private async Task<Exception?> AwaitBounded(
        Task task,
        TimeSpan timeout,
        string operation)
    {
        if (task.IsCompleted)
        {
            try
            {
                await task.ConfigureAwait(false);
                return null;
            }
            catch (Exception exception)
            {
                Diagnose(exception);
                return exception;
            }
        }

        var boundedTimeout = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
        if (await Task.WhenAny(task, Task.Delay(boundedTimeout)).ConfigureAwait(false) != task)
        {
            Observe(task);
            var timeoutException = new TimeoutException(operation + " exceeded the shutdown deadline");
            Diagnose(timeoutException);
            return timeoutException;
        }

        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            Diagnose(exception);
            return exception;
        }
    }

    private static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void FilterTransitionCancellationsUnsafe()
    {
        foreach (var entry in transitionCancellationCutoffs)
            priorityBacklog.CancelThrough(entry.Key, entry.Value.Backlog, entry.Value.Pending);
    }

    private void AddTransitionCancellationUnsafe(
        string template,
        (long Backlog, long Pending) cutoff)
    {
        if (!TextTemplate.IsTranslationCandidate(template)
            || !TranslationBudget.TryGetTextBytes(template, out var bytes))
            return;
        while (transitionCancellationCutoffs.Count >= TranslationBudget.MaxCancellationEntries
            || transitionCancellationUtf8Bytes + bytes > TranslationBudget.MaxCancellationUtf8Bytes)
        {
            if (transitionCancellationOrder.First == null)
                return;
            var oldest = transitionCancellationOrder.First.Value;
            transitionCancellationOrder.RemoveFirst();
            transitionCancellationNodes.Remove(oldest);
            transitionCancellationCutoffs.Remove(oldest);
            transitionCancellationUtf8Bytes -= TranslationBudget.TryGetTextBytes(oldest, out var oldBytes)
                ? oldBytes
                : 0;
        }
        transitionCancellationCutoffs[template] = cutoff;
        transitionCancellationNodes[template] = transitionCancellationOrder.AddLast(template);
        transitionCancellationUtf8Bytes += bytes;
    }

    private void TouchTransitionCancellationUnsafe(string template)
    {
        if (!transitionCancellationNodes.TryGetValue(template, out var node)) return;
        transitionCancellationOrder.Remove(node);
        transitionCancellationOrder.AddLast(node);
    }

    private void ClearTransitionCancellationsUnsafe()
    {
        transitionCancellationCutoffs.Clear();
        transitionCancellationOrder.Clear();
        transitionCancellationNodes.Clear();
        transitionCancellationUtf8Bytes = 0;
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Diagnose(Exception exception)
    {
        try
        {
            diagnostic?.Invoke(exception);
        }
        catch
        {
        }
    }
}
