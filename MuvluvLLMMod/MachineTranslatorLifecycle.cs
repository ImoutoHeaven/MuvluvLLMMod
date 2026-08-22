namespace MuvluvLLMMod;

public sealed class MachineTranslatorLifecycle
{
    private readonly object gate = new();
    private readonly TranslationPriorityBacklog priorityBacklog = new();
    private readonly Dictionary<string, (long Backlog, long Pending)> transitionCancellationCutoffs = new(StringComparer.Ordinal);
    private readonly Action<Exception>? diagnostic;
    private TranslationRetryPolicy? retryPolicy;
    private MachineTranslator? current;
    private MachineTranslator? stopping;
    private RequestRateLimiter? limiter;
    private Task transition = Task.CompletedTask;
    private Task? shutdownTask;
    private int transitionsInFlight;
    private int version;
    private bool shutdown;

    public MachineTranslatorLifecycle(
        Action<Exception>? diagnostic = null,
        TranslationRetryPolicy? retryPolicy = null)
    {
        this.diagnostic = diagnostic;
        this.retryPolicy = retryPolicy;
    }

    public Task TransitionTask
    {
        get { lock (gate) return shutdownTask ?? transition; }
    }

    public bool IsShutdown
    {
        get { lock (gate) return shutdown; }
    }

    public void Initialize(
        bool enabled,
        int requestsPerSecond,
        Func<RequestRateLimiter, TranslationPriorityBacklog, MachineTranslator>? factory,
        TranslationRetryPolicy? nextRetryPolicy = null)
    {
        lock (gate)
        {
            if (shutdown)
                return;

            var generation = ++version;
            if (nextRetryPolicy != null)
                retryPolicy = nextRetryPolicy;
            if (!enabled || factory == null)
                return;

            limiter = new RequestRateLimiter(Math.Max(1, requestsPerSecond));
            current = factory(limiter, priorityBacklog);
            if (!shutdown && generation == version)
                current.Start();
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
            if (shutdown)
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
                transitionCancellationCutoffs[template] = (backlogCancellation.Cutoff, pendingGeneration);
            else
                transitionCancellationCutoffs[template] = (
                    Math.Max(existing.Backlog, backlogCancellation.Cutoff),
                    Math.Max(existing.Pending, pendingGeneration));
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
        try
        {
            await pendingTransition.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Diagnose(exception);
        }

        await StopSafelyAsync(active).ConfigureAwait(false);
        if (stoppingMachine != null && !ReferenceEquals(stoppingMachine, active))
            await StopSafelyAsync(stoppingMachine).ConfigureAwait(false);

        lock (gate)
        {
            current = null;
            stopping = null;
            limiter = null;
            transitionCancellationCutoffs.Clear();
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
                if (shutdown || generation != version)
                    return;
                old = current;
                current = null;
                stopping = old;
            }

            if (old != null)
                await old.StopAsync().ConfigureAwait(false);
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
                await StopSafelyAsync(next).ConfigureAwait(false);
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
                    transitionCancellationCutoffs.Clear();
            }
        }
    }

    private async Task StopSafelyAsync(MachineTranslator? machine)
    {
        if (machine == null)
            return;

        try
        {
            await machine.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Diagnose(exception);
        }
    }

    private void FilterTransitionCancellationsUnsafe()
    {
        foreach (var entry in transitionCancellationCutoffs)
            priorityBacklog.CancelThrough(entry.Key, entry.Value.Backlog, entry.Value.Pending);
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
