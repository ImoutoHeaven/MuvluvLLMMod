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
    private int transitionsInFlight;
    private int version;

    public MachineTranslatorLifecycle(
        Action<Exception>? diagnostic = null,
        TranslationRetryPolicy? retryPolicy = null)
    {
        this.diagnostic = diagnostic;
        this.retryPolicy = retryPolicy;
    }

    public Task TransitionTask
    {
        get { lock (gate) return transition; }
    }

    public void Initialize(
        bool enabled,
        int requestsPerSecond,
        Func<RequestRateLimiter, TranslationPriorityBacklog, MachineTranslator>? factory,
        TranslationRetryPolicy? nextRetryPolicy = null)
    {
        var generation = Interlocked.Increment(ref version);
        lock (gate)
        {
            if (nextRetryPolicy != null) retryPolicy = nextRetryPolicy;
            if (!enabled || factory == null) return;
            limiter = new RequestRateLimiter(Math.Max(1, requestsPerSecond));
            current = factory(limiter, priorityBacklog);
            if (generation == Volatile.Read(ref version)) current.Start();
        }
    }

    public void Reload(
        bool enabled,
        int requestsPerSecond,
        Func<RequestRateLimiter, TranslationPriorityBacklog, MachineTranslator>? factory,
        TranslationRetryPolicy? nextRetryPolicy = null)
    {
        var generation = Interlocked.Increment(ref version);
        lock (gate)
        {
            if (nextRetryPolicy != null) retryPolicy = nextRetryPolicy;
            transitionsInFlight++;
            transition = RunTransitionAsync(transition, generation, enabled, requestsPerSecond, factory);
            Observe(transition);
        }
    }

    public bool EnqueuePriority(string template)
    {
        lock (gate)
        {
            return current?.EnqueuePriority(template)
                ?? retryPolicy?.TryRetain(template, () => priorityBacklog.Enqueue(template))
                ?? priorityBacklog.Enqueue(template);
        }
    }

    public bool EnqueuePriority(string template, long pendingGeneration)
    {
        lock (gate)
        {
            return current?.EnqueuePriority(template, pendingGeneration)
                ?? retryPolicy?.TryRetain(template, () => priorityBacklog.Enqueue(template, pendingGeneration))
                ?? priorityBacklog.Enqueue(template, pendingGeneration);
        }
    }

    public bool EnqueueNormal(string template)
    {
        lock (gate) return current?.EnqueueNormal(template) ?? false;
    }

    public bool EnqueueNormal(string template, long pendingGeneration)
    {
        lock (gate) return current?.EnqueueNormal(template, pendingGeneration) ?? false;
    }

    public bool Cancel(string template)
    {
        return Cancel(template, long.MaxValue);
    }

    public bool Cancel(string template, long pendingGeneration)
    {
        lock (gate)
        {
            var canceled = (current?.Cancel(template, pendingGeneration) ?? false)
                | (stopping?.Cancel(template, pendingGeneration) ?? false);
            var backlogCancellation = priorityBacklog.CancelAndGetCutoff(template, pendingGeneration);
            canceled |= backlogCancellation.Removed;
            if (transitionsInFlight == 0) return canceled;
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
        Interlocked.Increment(ref version);
        MachineTranslator? machine;
        lock (gate)
        {
            machine = current;
            current = null;
        }
        machine?.Stop();
    }

    private async Task RunTransitionAsync(
        Task previous,
        int generation,
        bool enabled,
        int requestsPerSecond,
        Func<RequestRateLimiter, TranslationPriorityBacklog, MachineTranslator>? factory)
    {
        await Task.Yield();
        try
        {
            await previous.ConfigureAwait(false);
            MachineTranslator? old;
            lock (gate)
            {
                old = current;
                current = null;
                stopping = old;
            }
            if (old != null) await old.StopAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(stopping, old)) stopping = null;
            }
            if (generation != Volatile.Read(ref version) || !enabled || factory == null) return;

            RequestRateLimiter nextLimiter;
            lock (gate)
            {
                if (generation != Volatile.Read(ref version)) return;
                nextLimiter = new RequestRateLimiter(Math.Max(1, requestsPerSecond), limiter?.NextStart ?? default);
                limiter = nextLimiter;
            }
            var next = factory(nextLimiter, priorityBacklog);
            lock (gate)
            {
                if (generation != Volatile.Read(ref version))
                {
                    next.Stop();
                    return;
                }
                FilterTransitionCancellationsUnsafe();
                current = next;
                next.Start();
            }
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
                if (transitionsInFlight == 0) transitionCancellationCutoffs.Clear();
            }
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
