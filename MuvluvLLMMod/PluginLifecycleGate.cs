namespace MuvluvLLMMod;

public enum PluginLifecycleState
{
    NotLoaded,
    Loading,
    Running,
    Stopping,
    Stopped,
    Failed
}

/// <summary>
/// One named, best-effort teardown operation. Later operations still run when an earlier one
/// fails so rollback can release every resource that was initialized before a failure.
/// </summary>
public readonly record struct PluginCleanupStep(string Name, Action Action);

/// <summary>
/// Loader-free owner of one plugin generation.
///
/// A generation has a cancellation boundary and counts load/activation stages. Cleanup moves
/// the generation to Stopping before waiting for those stages, so a stage that was paused by a
/// concurrent cleanup cannot publish a later resource. Cleanup callers all wait on the same
/// completion source. A failed teardown is terminal (Failed) and cannot be covered by a later
/// load.
/// </summary>
public sealed class PluginLifecycleGate
{
    private static readonly Task<bool> SuccessfulNoop = Task.FromResult(true);

    private readonly object gate = new();
    private readonly TimeSpan quiescenceTimeout;
    private PluginGeneration? current;
    private PluginLifecycleState state = PluginLifecycleState.NotLoaded;
    private long nextGeneration;
    private TaskCompletionSource<bool>? cleanupCompletion;
    private bool lastCleanupSucceeded = true;

    public PluginLifecycleGate(TimeSpan? quiescenceTimeout = null)
    {
        this.quiescenceTimeout = quiescenceTimeout is { } value && value > TimeSpan.Zero
            ? (value < TranslationBudget.MaxLifecycleQuiescenceTimeout
                ? value
                : TranslationBudget.MaxLifecycleQuiescenceTimeout)
            : TranslationBudget.DefaultLifecycleQuiescenceTimeout;
    }

    public PluginLifecycleState State
    {
        get { lock (gate) return state; }
    }

    public bool IsCleaningUp
    {
        get
        {
            lock (gate)
                return state is PluginLifecycleState.Stopping or PluginLifecycleState.Failed;
        }
    }

    /// <summary>
    /// Returns the generation object for the current or most recently completed generation.
    /// The object is intentionally retained as a stale token; all operations validate its epoch.
    /// </summary>
    public PluginGeneration? CurrentGeneration
    {
        get { lock (gate) return current; }
    }

    public long? CurrentGenerationId
    {
        get { lock (gate) return current?.Id; }
    }

    public TimeSpan QuiescenceTimeout => quiescenceTimeout;

    public Exception? Failure
    {
        get { lock (gate) return current?.FailureUnsafe; }
    }

    public bool TryBeginLoad() => TryBeginLoad(out _);

    public bool TryBeginLoad(out PluginGeneration generation)
    {
        lock (gate)
        {
            if (state is not (PluginLifecycleState.NotLoaded or PluginLifecycleState.Stopped))
            {
                generation = null!;
                return false;
            }

            generation = new PluginGeneration(this, ++nextGeneration);
            current = generation;
            cleanupCompletion = null;
            lastCleanupSucceeded = true;
            state = PluginLifecycleState.Loading;
            return true;
        }
    }

    /// <summary>
    /// Returns the currently published owner only while its generation is Running. Stopping,
    /// stopped, failed, and loading owners are never exposed through this side of the API.
    /// </summary>
    public T? CurrentOwner<T>() where T : class
    {
        lock (gate)
        {
            if (state != PluginLifecycleState.Running || current?.Owner is not T owner)
                return null;
            return owner;
        }
    }

    public bool TryPublishRunning(PluginGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (gate)
        {
            if (!ReferenceEquals(current, generation)
                || state != PluginLifecycleState.Loading
                || generation.IsCancellationRequestedUnsafe)
                return false;

            state = PluginLifecycleState.Running;
            return true;
        }
    }

    public bool IsCurrentRunning(PluginGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (gate)
            return ReferenceEquals(current, generation) && state == PluginLifecycleState.Running;
    }

    /// <summary>
    /// Records an asynchronous resource fault without recursively entering teardown. The owning
    /// transition calls this notification after releasing its own lock; cleanup remains the only
    /// place that performs Unity/filesystem teardown. A failed generation is quarantined
    /// immediately and cannot be followed by a new load, even before its cleanup callback runs.
    /// </summary>
    public bool RecordFailure(PluginGeneration generation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(exception);
        lock (gate)
        {
            if (!ReferenceEquals(current, generation))
                return false;

            generation.RecordFailureUnsafe(exception);
            if (!generation.CleanupCompleteUnsafe)
            {
                generation.RequestStopUnsafe();
                state = PluginLifecycleState.Failed;
            }
            else
            {
                // A late notification for this same stale generation must still prevent a
                // replacement load. A newer generation would have replaced current and returned
                // false above.
                state = PluginLifecycleState.Failed;
                lastCleanupSucceeded = false;
            }
            return true;
        }
    }

    /// <summary>
    /// Performs the supplied teardown exactly once. If another caller is already cleaning up,
    /// this call waits for that caller's result rather than returning a default value.
    /// </summary>
    public bool Cleanup(
        IReadOnlyList<PluginCleanupStep> steps,
        Action<string, Exception>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return Cleanup(
            generation =>
            {
                var succeeded = true;
                foreach (var step in steps)
                {
                    try
                    {
                        step.Action();
                    }
                    catch (Exception exception)
                    {
                        succeeded = false;
                        try
                        {
                            diagnostic?.Invoke(step.Name, exception);
                        }
                        catch
                        {
                        }
                    }
                }

                return succeeded;
            },
            null);
    }

    /// <summary>
    /// Runs one generation-owned teardown callback. The first caller supplies the callback;
    /// all subsequent callers observe its exact completion/result.
    /// </summary>
    public bool Cleanup(
        Func<PluginGeneration, bool> teardown,
        Action<Exception>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(teardown);

        Task<bool>? completion;
        PluginGeneration? generation;
        lock (gate)
        {
            if (state == PluginLifecycleState.NotLoaded)
                return true;
            if (cleanupCompletion != null)
            {
                completion = cleanupCompletion.Task;
                generation = null;
            }
            else if ((state == PluginLifecycleState.Stopped || state == PluginLifecycleState.Failed)
                && current?.CleanupCompleteUnsafe == true)
            {
                return lastCleanupSucceeded;
            }
            else
            {
                generation = current;
                if (generation == null)
                    return false;

                if (!generation.IsFaultedUnsafe)
                    state = PluginLifecycleState.Stopping;
                else
                    state = PluginLifecycleState.Failed;
                generation.RequestStopUnsafe();
                cleanupCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                completion = cleanupCompletion.Task;
            }
        }

        if (generation == null)
            return completion!.GetAwaiter().GetResult();

        var succeeded = true;
        var deadline = DateTime.UtcNow + quiescenceTimeout;
        try
        {
            // All resources created by a stage are normally visible to the owner before
            // teardown. A non-cooperative boundary cannot be allowed to hold up independent
            // freeze/flush/unpatch work forever, so the same injectable deadline covers both
            // admitted stages and config callbacks.
            if (!generation.WaitForStages(Remaining(deadline)))
            {
                succeeded = false;
                RecordQuiescenceTimeout(
                    generation,
                    "load stages",
                    diagnostic);
            }
            if (!generation.WaitForCallbacks(Remaining(deadline)))
            {
                succeeded = false;
                RecordQuiescenceTimeout(
                    generation,
                    "configuration callbacks",
                    diagnostic);
            }

            try
            {
                succeeded = teardown(generation) && succeeded;
            }
            catch (Exception exception)
            {
                succeeded = false;
                Report(diagnostic, exception);
            }
        }
        catch (Exception exception)
        {
            succeeded = false;
            Report(diagnostic, exception);
        }

        lock (gate)
        {
            succeeded = succeeded && generation.FailureUnsafe == null;
            lastCleanupSucceeded = succeeded;
            state = succeeded ? PluginLifecycleState.Stopped : PluginLifecycleState.Failed;
            generation.MarkCleanupCompleteUnsafe();
            cleanupCompletion!.TrySetResult(succeeded);
        }

        return completion!.GetAwaiter().GetResult();
    }

    private void RecordQuiescenceTimeout(
        PluginGeneration generation,
        string operation,
        Action<Exception>? diagnostic)
    {
        var exception = new TimeoutException(
            operation + " did not quiesce before the plugin cleanup deadline");
        lock (gate)
        {
            if (ReferenceEquals(current, generation))
                generation.QuiescenceTimedOutUnsafe = true;
        }
        RecordFailure(generation, exception);
        Report(diagnostic, exception);
    }

    private static void Report(Action<Exception>? diagnostic, Exception exception)
    {
        try
        {
            diagnostic?.Invoke(exception);
        }
        catch
        {
        }
    }

    private static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static bool WaitBounded(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return true;
        }

        var boundedTimeout = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
        var completed = Task.WhenAny(task, Task.Delay(boundedTimeout))
            .GetAwaiter()
            .GetResult();
        if (!ReferenceEquals(completed, task))
        {
            Observe(task);
            return false;
        }

        task.GetAwaiter().GetResult();
        return true;
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Exposes the same shared cleanup result to async callers. The work itself is deliberately
    /// executed by the first caller so Unity-bound teardown stays on that caller's thread.
    /// </summary>
    public Task<bool> CleanupAsync(
        Func<PluginGeneration, bool> teardown,
        Action<Exception>? diagnostic = null)
    {
        // Cleanup is synchronous by design, but the returned task is the generation's shared
        // completion task. This overload is useful to tests and callers that already have an
        // async composition boundary.
        Cleanup(teardown, diagnostic);
        lock (gate)
            return cleanupCompletion?.Task ?? SuccessfulNoop;
    }

    internal bool TryAttachOwner(PluginGeneration generation, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (gate)
        {
            if (!ReferenceEquals(current, generation)
                || state != PluginLifecycleState.Loading
                || generation.Owner != null)
                return false;
            generation.Owner = owner;
            return true;
        }
    }

    internal PluginStage? TryEnterStage(PluginGeneration generation, bool allowRunning)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (gate)
        {
            if (!ReferenceEquals(current, generation)
                || (state != PluginLifecycleState.Loading
                    && !(allowRunning && state == PluginLifecycleState.Running)))
                return null;

            if (generation.ActiveStagesUnsafe == 0)
            {
                generation.StagesDrainedUnsafe = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            generation.ActiveStagesUnsafe++;
            return new PluginStage(generation);
        }
    }

    internal bool IsGenerationRunning(PluginGeneration generation)
    {
        lock (gate)
            return ReferenceEquals(current, generation) && state == PluginLifecycleState.Running;
    }

    internal PluginGenerationLease? TryAcquireLease(PluginGeneration generation)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, generation) || state != PluginLifecycleState.Running)
                return null;
            var lease = new PluginGenerationLease(this, generation);
            generation.LeasesUnsafe.Add(lease);
            return lease;
        }
    }

    internal bool IsLeaseActive(PluginGenerationLease lease)
    {
        lock (gate)
        {
            return Volatile.Read(ref lease.DisposedUnsafe) == 0
                && ReferenceEquals(current, lease.Generation)
                && state == PluginLifecycleState.Running;
        }
    }

    internal PluginCallbackLease? TryEnterCallback(PluginGenerationLease lease)
    {
        lock (gate)
        {
            if (Volatile.Read(ref lease.DisposedUnsafe) != 0
                || !ReferenceEquals(current, lease.Generation)
                || state != PluginLifecycleState.Running)
                return null;

            lease.ActiveCallbacksUnsafe++;
            return new PluginCallbackLease(this, lease);
        }
    }

    internal void DisposeLease(PluginGenerationLease lease)
    {
        Task? completion;
        bool waitForCompletion;
        lock (gate)
        {
            lease.DeactivatedUnsafe = true;
            completion = lease.ActiveCallbacksUnsafe == 0
                ? null
                : (lease.CallbacksDrainedUnsafe ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            waitForCompletion = completion != null
                && !lease.Generation.QuiescenceTimedOutUnsafe;
        }

        if (completion == null || !waitForCompletion)
            return;
        if (!WaitBounded(completion, quiescenceTimeout))
        {
            var exception = new TimeoutException(
                "configuration callback did not quiesce before the plugin cleanup deadline");
            RecordFailure(lease.Generation, exception);
        }
    }

    internal void ExitCallback(PluginGenerationLease lease)
    {
        lock (gate)
        {
            if (lease.ActiveCallbacksUnsafe > 0)
                lease.ActiveCallbacksUnsafe--;
            if (lease.ActiveCallbacksUnsafe == 0)
                lease.CallbacksDrainedUnsafe?.TrySetResult(true);
        }
    }

    internal void ExitStage(PluginGeneration generation)
    {
        lock (gate)
        {
            if (generation.ActiveStagesUnsafe > 0)
                generation.ActiveStagesUnsafe--;
            if (generation.ActiveStagesUnsafe == 0)
                generation.StagesDrainedUnsafe?.TrySetResult(true);
        }
    }

    public sealed class PluginGeneration
    {
        internal readonly PluginLifecycleGate owner;
        private readonly CancellationTokenSource cancellation = new();
        private readonly CancellationToken cancellationToken;

        internal PluginGeneration(PluginLifecycleGate owner, long id)
        {
            this.owner = owner;
            Id = id;
            cancellationToken = cancellation.Token;
            StagesDrainedUnsafe = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            StagesDrainedUnsafe.TrySetResult(true);
        }

        public long Id { get; }
        public CancellationToken CancellationToken => cancellationToken;
        public bool IsCancellationRequested => cancellationToken.IsCancellationRequested;

        internal object? Owner { get; set; }
        internal int ActiveStagesUnsafe { get; set; }
        internal List<PluginGenerationLease> LeasesUnsafe { get; } = new();
        internal TaskCompletionSource<bool>? StagesDrainedUnsafe { get; set; }
        internal Exception? FailureUnsafe { get; set; }
        internal bool QuiescenceTimedOutUnsafe { get; set; }
        internal bool CleanupCompleteUnsafe { get; set; }
        internal bool IsFaultedUnsafe => FailureUnsafe != null;
        internal bool IsCancellationRequestedUnsafe => cancellationToken.IsCancellationRequested;

        public bool AttachOwner(object owner) => this.owner.TryAttachOwner(this, owner);

        public T? GetOwner<T>() where T : class
        {
            lock (this.owner.gate)
                return Owner as T;
        }

        public PluginStage? TryEnterStage() => owner.TryEnterStage(this, false);

        public PluginStage? TryEnterRunningStage() => owner.TryEnterStage(this, true);

        public PluginGenerationLease? TryAcquireRunningLease() => owner.TryAcquireLease(this);

        public bool IsRunning => owner.IsGenerationRunning(this);

        internal void RequestStopUnsafe()
        {
            if (CleanupCompleteUnsafe)
                return;
            if (!cancellation.IsCancellationRequested)
                cancellation.Cancel();
        }

        internal void RecordFailureUnsafe(Exception exception)
        {
            FailureUnsafe ??= exception;
        }

        internal bool WaitForStages(TimeSpan timeout)
        {
            Task wait;
            lock (owner.gate)
            {
                wait = ActiveStagesUnsafe == 0
                    ? Task.CompletedTask
                    : (StagesDrainedUnsafe ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            return WaitBounded(wait, timeout);
        }

        internal bool WaitForCallbacks(TimeSpan timeout)
        {
            Task[] waits;
            lock (owner.gate)
            {
                waits = LeasesUnsafe
                    .Select(static lease => lease.CallbackCompletionTask())
                    .Where(static task => !task.IsCompleted)
                    .ToArray();
            }
            return WaitBounded(
                waits.Length == 0 ? Task.CompletedTask : Task.WhenAll(waits),
                timeout);
        }

        internal void MarkCleanupCompleteUnsafe()
        {
            CleanupCompleteUnsafe = true;
            // Keep the owner on the stale token for cleanup diagnostics, but it is no longer
            // reachable from CurrentOwner because the gate state is Stopped/Failed.
            try
            {
                cancellation.Dispose();
            }
            catch
            {
            }
        }
    }

    public sealed class PluginStage : IDisposable
    {
        private readonly PluginGeneration generation;
        private int disposed;

        internal PluginStage(PluginGeneration generation) => this.generation = generation;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                generation.owner.ExitStage(generation);
        }
    }

    public sealed class PluginGenerationLease : IDisposable
    {
        private readonly PluginLifecycleGate owner;
        internal readonly PluginGeneration Generation;
        internal int DisposedUnsafe;
        internal int ActiveCallbacksUnsafe;
        internal bool DeactivatedUnsafe;
        internal TaskCompletionSource<bool>? CallbacksDrainedUnsafe;

        internal PluginGenerationLease(PluginLifecycleGate owner, PluginGeneration generation)
        {
            this.owner = owner;
            Generation = generation;
        }

        public long GenerationId => Generation.Id;
        public bool IsActive => owner.IsLeaseActive(this);

        public bool TryEnter(out PluginCallbackLease callback)
        {
            callback = owner.TryEnterCallback(this)!;
            return callback != null;
        }

        public T? GetOwner<T>() where T : class =>
            IsActive ? Generation.GetOwner<T>() : null;

        internal Task CallbackCompletionTask()
        {
            lock (owner.gate)
            {
                return ActiveCallbacksUnsafe == 0
                    ? Task.CompletedTask
                    : (CallbacksDrainedUnsafe ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref DisposedUnsafe, 1) == 0)
                owner.DisposeLease(this);
        }
    }

    public sealed class PluginCallbackLease : IDisposable
    {
        private readonly PluginLifecycleGate owner;
        private readonly PluginGenerationLease lease;
        private int disposed;

        internal PluginCallbackLease(
            PluginLifecycleGate owner,
            PluginGenerationLease lease)
        {
            this.owner = owner;
            this.lease = lease;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.ExitCallback(lease);
        }
    }
}
