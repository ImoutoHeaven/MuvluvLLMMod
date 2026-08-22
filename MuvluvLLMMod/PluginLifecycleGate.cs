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
    private PluginGeneration? current;
    private PluginLifecycleState state = PluginLifecycleState.NotLoaded;
    private long nextGeneration;
    private TaskCompletionSource<bool>? cleanupCompletion;
    private bool lastCleanupSucceeded = true;

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
            if (state == PluginLifecycleState.Stopped || state == PluginLifecycleState.Failed)
                return lastCleanupSucceeded;

            if (cleanupCompletion != null)
            {
                completion = cleanupCompletion.Task;
                generation = null;
            }
            else
            {
                generation = current;
                if (generation == null)
                    return false;

                state = PluginLifecycleState.Stopping;
                generation.RequestStopUnsafe();
                cleanupCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                completion = cleanupCompletion.Task;
            }
        }

        if (generation == null)
            return completion!.GetAwaiter().GetResult();

        var succeeded = true;
        try
        {
            // All resources created by a stage are now visible to the owner. A stage that is
            // still in progress cannot be missed by cleanup and is not followed by a new load
            // stage because the state is already Stopping.
            generation.WaitForStages();
            generation.WaitForCallbacks();
            try
            {
                succeeded = teardown(generation);
            }
            catch (Exception exception)
            {
                succeeded = false;
                try
                {
                    diagnostic?.Invoke(exception);
                }
                catch
                {
                }
            }
        }
        catch (Exception exception)
        {
            succeeded = false;
            try
            {
                diagnostic?.Invoke(exception);
            }
            catch
            {
            }
        }

        lock (gate)
        {
            lastCleanupSucceeded = succeeded;
            state = succeeded ? PluginLifecycleState.Stopped : PluginLifecycleState.Failed;
            generation.MarkCleanupCompleteUnsafe();
            cleanupCompletion!.TrySetResult(succeeded);
        }

        return completion!.GetAwaiter().GetResult();
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
        lock (gate)
        {
            lease.DeactivatedUnsafe = true;
            completion = lease.ActiveCallbacksUnsafe == 0
                ? null
                : (lease.CallbacksDrainedUnsafe ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        completion?.GetAwaiter().GetResult();
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

        internal PluginGeneration(PluginLifecycleGate owner, long id)
        {
            this.owner = owner;
            Id = id;
            StagesDrainedUnsafe = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            StagesDrainedUnsafe.TrySetResult(true);
        }

        public long Id { get; }
        public CancellationToken CancellationToken => cancellation.Token;
        public bool IsCancellationRequested => cancellation.IsCancellationRequested;

        internal object? Owner { get; set; }
        internal int ActiveStagesUnsafe { get; set; }
        internal List<PluginGenerationLease> LeasesUnsafe { get; } = new();
        internal TaskCompletionSource<bool>? StagesDrainedUnsafe { get; set; }
        internal bool IsCancellationRequestedUnsafe => cancellation.IsCancellationRequested;

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
            if (!cancellation.IsCancellationRequested)
                cancellation.Cancel();
        }

        internal void WaitForStages()
        {
            Task wait;
            lock (owner.gate)
            {
                wait = ActiveStagesUnsafe == 0
                    ? Task.CompletedTask
                    : (StagesDrainedUnsafe ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            wait.GetAwaiter().GetResult();
        }

        internal void WaitForCallbacks()
        {
            PluginGenerationLease[] leases;
            lock (owner.gate)
                leases = LeasesUnsafe.ToArray();
            foreach (var lease in leases)
                lease.WaitForCallbacks();
        }

        internal void MarkCleanupCompleteUnsafe()
        {
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

        internal void WaitForCallbacks()
        {
            Task? completion;
            lock (owner.gate)
            {
                completion = ActiveCallbacksUnsafe == 0
                    ? null
                    : (CallbacksDrainedUnsafe ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            completion?.GetAwaiter().GetResult();
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
