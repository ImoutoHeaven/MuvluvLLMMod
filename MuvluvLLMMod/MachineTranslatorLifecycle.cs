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
    private readonly Action<Exception>? terminalFailure;
    private readonly TimeSpan shutdownTimeout;
    private TranslationRetryPolicy? retryPolicy;
    private readonly HashSet<MachineTranslator> stoppingWorkers = new();
    private MachineTranslator? current;
    private RequestRateLimiter? limiter;
    private Task transition = Task.CompletedTask;
    private Task? shutdownTask;
    private int transitionsInFlight;
    private int version;
    private bool initialized;
    private bool shutdown;
    private bool shutdownTimedOut;
    private Exception? terminalException;
    private bool terminalFailureNotified;

    public MachineTranslatorLifecycle(
        Action<Exception>? diagnostic = null,
        TranslationRetryPolicy? retryPolicy = null,
        TimeSpan? shutdownTimeout = null,
        Action<Exception>? terminalFailure = null)
    {
        this.diagnostic = diagnostic;
        this.terminalFailure = terminalFailure;
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

    public bool IsFaulted
    {
        get { lock (gate) return terminalException != null; }
    }

    public Exception? TerminalFailure
    {
        get { lock (gate) return terminalException; }
    }

    public int TrackedStoppingWorkerCount
    {
        get { lock (gate) return stoppingWorkers.Count; }
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
            if (shutdown
                || terminalException != null
                || initialized
                || current != null
                || stoppingWorkers.Count != 0
                || transitionsInFlight != 0)
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
            if (shutdown
                || terminalException != null
                || !initialized
                || transitionsInFlight >= TranslationBudget.MaxTransitions)
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
            if (shutdown || terminalException != null)
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
            if (shutdown || terminalException != null)
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
            return !shutdown
                && terminalException == null
                && (current?.EnqueueNormal(template) ?? false);
        }
    }

    public bool EnqueueNormal(string template, long pendingGeneration)
    {
        lock (gate)
        {
            return !shutdown
                && terminalException == null
                && (current?.EnqueueNormal(template, pendingGeneration) ?? false);
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
            if (shutdown || terminalException != null)
                return false;

            var canceled = (current?.Cancel(template, pendingGeneration) ?? false);
            foreach (var worker in stoppingWorkers.ToArray())
                canceled |= worker.Cancel(template, pendingGeneration);
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
            var stoppingMachines = stoppingWorkers.ToArray();
            current = null;
            var pendingTransition = transition;
            shutdownTask = CompleteShutdownAsync(active, stoppingMachines, pendingTransition);
            Observe(shutdownTask);
            return shutdownTask;
        }
    }

    /// <summary>
    /// Rolls back the machine resource without rewriting the lifecycle's original failure. A
    /// timed-out shutdown remains faulted for ordinary lifecycle callers, but once every tracked
    /// worker has actually completed, a later resource retry can settle its reservation.
    /// </summary>
    public void ShutdownForResourceRollback()
    {
        Task? existingShutdown;
        lock (gate)
            existingShutdown = shutdownTask;

        if (existingShutdown is { IsCompleted: true, IsFaulted: true }
            && NoTrackedStoppingWorkersRemain())
            return;

        // On the first attempt this preserves the original failure. A later retry observes the
        // already-faulted task above and only then treats the machine reservation as settled.
        Shutdown();
    }

    private bool NoTrackedStoppingWorkersRemain()
    {
        lock (gate)
            return stoppingWorkers.Count == 0;
    }

    private async Task CompleteShutdownAsync(
        MachineTranslator? active,
        MachineTranslator[] stoppingMachines,
        Task pendingTransition)
    {
        var deadline = DateTime.UtcNow + shutdownTimeout;
        Exception? failure = await AwaitBounded(
            pendingTransition,
            Remaining(deadline),
            "machine transition shutdown").ConfigureAwait(false);

        var machines = new HashSet<MachineTranslator>();
        if (active != null)
            machines.Add(active);
        foreach (var machine in stoppingMachines)
            machines.Add(machine);
        lock (gate)
        {
            foreach (var machine in stoppingWorkers)
                machines.Add(machine);
        }

        foreach (var machine in machines)
        {
            var machineFailure = await StopSafelyAsync(
                machine,
                Remaining(deadline)).ConfigureAwait(false);
            failure ??= machineFailure;
        }

        lock (gate)
        {
            current = null;
            limiter = null;
            shutdownTimedOut = failure is TimeoutException
                || failure?.InnerException is TimeoutException;
            ClearTransitionCancellationsUnsafe();
        }

        if (failure != null)
        {
            Fault(failure);
            throw new InvalidOperationException("machine translator shutdown failed", failure);
        }
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
                if (shutdown || terminalException != null || generation != version)
                    return;
                old = current;
                current = null;
                if (old != null)
                    stoppingWorkers.Add(old);
            }

            if (old != null)
            {
                var stopFailure = await StopSafelyAsync(old, shutdownTimeout).ConfigureAwait(false);
                if (stopFailure != null)
                {
                    throw new TerminalTransitionException(
                        "old machine translator did not stop before the transition budget",
                        stopFailure);
                }
            }

            RequestRateLimiter nextLimiter;
            lock (gate)
            {
                if (shutdown || terminalException != null || generation != version || !enabled || factory == null)
                    return;
                nextLimiter = new RequestRateLimiter(Math.Max(1, requestsPerSecond), limiter?.NextStart ?? default);
                limiter = nextLimiter;
            }

            var next = factory(nextLimiter, priorityBacklog);
            var accepted = false;
            lock (gate)
            {
                if (!shutdown && terminalException == null && generation == version)
                {
                    FilterTransitionCancellationsUnsafe();
                    current = next;
                    next.Start();
                    accepted = true;
                }
                else
                {
                    stoppingWorkers.Add(next);
                }
            }

            if (!accepted)
            {
                var stopFailure = await StopSafelyAsync(next, shutdownTimeout).ConfigureAwait(false);
                if (stopFailure != null)
                {
                    throw new TerminalTransitionException(
                        "machine translator replacement did not stop before the transition budget",
                        stopFailure);
                }
            }
        }
        catch (TerminalTransitionException exception)
        {
            Fault(exception);
            Diagnose(exception);
            throw;
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
                ObserveStoppedWorker(machine);
                return timeoutException;
            }
            RemoveStoppingWorker(machine);
            return null;
        }
        catch (Exception exception)
        {
            Diagnose(exception);
            ObserveStoppedWorker(machine);
            return exception;
        }
    }

    private void ObserveStoppedWorker(MachineTranslator machine)
    {
        Task completion;
        lock (gate)
        {
            stoppingWorkers.Add(machine);
            completion = machine.StopCompletion;
        }

        _ = completion.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                RemoveStoppingWorker(machine);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RemoveStoppingWorker(MachineTranslator machine)
    {
        lock (gate)
            stoppingWorkers.Remove(machine);
    }

    private sealed class TerminalTransitionException : InvalidOperationException
    {
        public TerminalTransitionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private void Fault(Exception exception)
    {
        Action<Exception>? notification = null;
        lock (gate)
        {
            if (terminalException == null)
            {
                terminalException = exception;
                ++version;
                if (!terminalFailureNotified)
                {
                    terminalFailureNotified = true;
                    notification = terminalFailure;
                }
            }
        }

        if (notification == null)
            return;

        try
        {
            // The notification is deliberately only a state signal.  In particular, the
            // production recipient records the owning plugin generation as failed; it must not
            // synchronously call back into Cleanup while this transition is unwinding.
            notification(exception);
        }
        catch (Exception notificationFailure)
        {
            Diagnose(notificationFailure);
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
