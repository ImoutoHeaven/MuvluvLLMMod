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
/// the generation to Stopping before waiting for those stages. Every external side effect is
/// reserved by its stage before entry, committed only after the boundary returns, and retained
/// for rollback if the boundary returns late. Cleanup callers all wait on the same completion
/// source. A failed teardown is terminal (Failed) and cannot be covered by a later load.
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
    private TaskCompletionSource<bool>? resourceRetryCompletion;
    private Action<Exception>? resourceDiagnostic;
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
            resourceRetryCompletion = null;
            resourceDiagnostic = null;
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
    /// this call waits for that caller's result rather than returning a default value. A failed
    /// generation may subsequently run only retained resource rollbacks; the teardown graph is
    /// never repeated.
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
        var retryResources = false;
        lock (gate)
        {
            resourceDiagnostic ??= diagnostic;
            if (state == PluginLifecycleState.NotLoaded)
                return true;
            if (cleanupCompletion != null)
            {
                var completedGeneration = current;
                if (state == PluginLifecycleState.Failed
                    && completedGeneration?.CleanupCompleteUnsafe == true
                    && completedGeneration.HasOutstandingResourcesUnsafe)
                {
                    if (resourceRetryCompletion != null)
                    {
                        completion = resourceRetryCompletion.Task;
                        generation = null;
                    }
                    else
                    {
                        resourceRetryCompletion = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        completion = resourceRetryCompletion.Task;
                        generation = completedGeneration;
                        retryResources = true;
                    }
                }
                else
                {
                    completion = cleanupCompletion.Task;
                    generation = null;
                }
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
                generation.MarkResourcesForRollbackUnsafe();
                cleanupCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                completion = cleanupCompletion.Task;
            }
        }

        if (generation == null)
            return completion!.GetAwaiter().GetResult();

        if (retryResources)
        {
            // A late resource may have failed after the shared cleanup result was published.
            // Retry only those callbacks; never re-run the whole teardown graph or wait for a
            // stage that is still executing its external boundary.
            var drained = generation.owner.DrainResourceRollbacks(generation);
            lock (gate)
            {
                if (!drained)
                    lastCleanupSucceeded = false;
                resourceRetryCompletion!.TrySetResult(false);
                resourceRetryCompletion = null;
            }
            return completion!.GetAwaiter().GetResult();
        }

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

            // Explicit teardown normally releases resources at its semantic ordering points.
            // This final drain is the safety net for a newly added stage, and leaves a pending
            // boundary retained for its late completion rather than pretending cleanup is done.
            if (!generation.owner.EnableAndDrainResourceRollbacks(generation))
                succeeded = false;
        }
        catch (Exception exception)
        {
            succeeded = false;
            Report(diagnostic, exception);
        }

        lock (gate)
        {
            // A pending reservation is itself a cleanup failure. In particular, no cleanup path
            // may report Stopped while a non-cooperative stage can still publish a resource.
            succeeded = succeeded
                && generation.FailureUnsafe == null
                && !generation.HasOutstandingResourcesUnsafe;
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
            return new PluginStage(generation, allowRunning);
        }
    }

    internal PluginGenerationResource? TryRegisterResource(
        PluginStage stage,
        string name,
        Action rollback,
        Func<bool>? deferRollback)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("resource name is required", nameof(name));
        ArgumentNullException.ThrowIfNull(rollback);
        lock (gate)
        {
            if (!ReferenceEquals(current, stage.Generation)
                || stage.IsDisposedUnsafe
                || stage.Generation.CleanupCompleteUnsafe
                || stage.Generation.CleanupRequestedUnsafe)
                return null;

            var resource = new PluginGenerationResource(
                this,
                stage.Generation,
                stage,
                name,
                rollback,
                deferRollback);
            stage.Generation.ResourcesUnsafe.Add(resource);
            return resource;
        }
    }

    internal void ReportResourceFailure(
        PluginGeneration generation,
        string name,
        Exception exception)
    {
        var wrapped = new InvalidOperationException(
            "generation resource '" + name + "' rollback failed",
            exception);
        RecordFailure(generation, wrapped);
        Action<Exception>? diagnostic;
        lock (gate)
            diagnostic = resourceDiagnostic;
        Report(diagnostic, wrapped);
    }

    internal bool DrainResourceRollbacks(PluginGeneration generation)
    {
        PluginGenerationResource[] resources;
        lock (gate)
        {
            resources = generation.ResourcesUnsafe.ToArray();
        }

        var succeeded = true;
        foreach (var resource in resources)
            succeeded = resource.RequestRollback() && succeeded;
        return succeeded;
    }

    internal bool EnableAndDrainResourceRollbacks(PluginGeneration generation)
    {
        lock (gate)
            generation.ResourceDrainEnabledUnsafe = true;
        return DrainResourceRollbacks(generation);
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
        var drain = false;
        lock (gate)
        {
            if (generation.ActiveStagesUnsafe > 0)
                generation.ActiveStagesUnsafe--;
            if (generation.ActiveStagesUnsafe == 0)
            {
                generation.StagesDrainedUnsafe?.TrySetResult(true);
                drain = generation.ResourceDrainEnabledUnsafe;
            }
        }

        // A timed-out stage can complete after the shared cleanup result was published. Its
        // completion is the safe point for any resource whose boundary never returned; do not
        // make the cleanup caller wait for this path.
        if (drain)
            _ = DrainResourceRollbacks(generation);
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
        internal List<PluginGenerationResource> ResourcesUnsafe { get; } = new();
        internal TaskCompletionSource<bool>? StagesDrainedUnsafe { get; set; }
        internal Exception? FailureUnsafe { get; set; }
        internal bool QuiescenceTimedOutUnsafe { get; set; }
        internal bool CleanupCompleteUnsafe { get; set; }
        internal bool CleanupRequestedUnsafe { get; set; }
        internal bool ResourceDrainEnabledUnsafe { get; set; }
        internal bool IsFaultedUnsafe => FailureUnsafe != null;
        internal bool IsCancellationRequestedUnsafe => cancellationToken.IsCancellationRequested;
        internal bool HasOutstandingResourcesUnsafe => ResourcesUnsafe.Count != 0;
        internal bool HasPendingResourceUnsafe => ResourcesUnsafe.Any(
            static resource => resource.IsPendingUnsafe);

        public int OutstandingResourceCount
        {
            get
            {
                lock (owner.gate)
                    return ResourcesUnsafe.Count;
            }
        }

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

        internal bool CanCommitResourceUnsafe(PluginGenerationResource resource)
        {
            if (!ReferenceEquals(owner.current, this)
                || CleanupCompleteUnsafe
                || IsFaultedUnsafe
                || IsCancellationRequestedUnsafe
                || CleanupRequestedUnsafe
                || resource.Stage.IsDisposedUnsafe)
                return false;

            return resource.Stage.AllowRunning
                ? owner.state == PluginLifecycleState.Running
                : owner.state == PluginLifecycleState.Loading;
        }

        internal void MarkResourcesForRollbackUnsafe() => CleanupRequestedUnsafe = true;

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

    /// <summary>
    /// A reservation for one external side effect. The reservation is installed before entering
    /// the boundary. Commit publishes ownership only after the boundary returns and the
    /// generation is still admissible; otherwise the local rollback runs immediately. Cleanup
    /// can request rollback without waiting for a non-cooperative boundary, and the reservation
    /// remains retained until its late completion settles.
    /// </summary>
    public sealed class PluginGenerationResource : IDisposable
    {
        private readonly PluginLifecycleGate owner;
        private readonly PluginGeneration generation;
        private readonly Action rollback;
        private readonly string name;
        private readonly Func<bool>? deferRollback;
        private bool completed;
        private bool committed;
        private bool rollbackInProgress;
        private bool rolledBack;
        private int disposed;

        internal PluginGenerationResource(
            PluginLifecycleGate owner,
            PluginGeneration generation,
            PluginStage stage,
            string name,
            Action rollback,
            Func<bool>? deferRollback)
        {
            this.owner = owner;
            this.generation = generation;
            Stage = stage;
            this.name = name;
            this.rollback = rollback;
            this.deferRollback = deferRollback;
        }

        internal PluginStage Stage { get; }

        public string Name => name;

        public bool IsCommitted
        {
            get { lock (owner.gate) return committed; }
        }

        public bool IsRolledBack
        {
            get { lock (owner.gate) return rolledBack; }
        }

        internal bool IsPendingUnsafe => !completed;

        public bool IsPending
        {
            get
            {
                lock (owner.gate)
                    return IsPendingUnsafe;
            }
        }

        /// <summary>
        /// Completes the external boundary and conditionally publishes its owner. A false
        /// result means the side effect returned after cancellation/failure; its rollback has
        /// already been attempted and the caller must not publish the local object elsewhere.
        /// </summary>
        public bool Commit(Action? publish = null)
        {
            var executeRollback = false;
            Exception? publicationFailure = null;
            lock (owner.gate)
            {
                if (completed)
                    return committed;

                completed = true;
                if (generation.CanCommitResourceUnsafe(this))
                {
                    try
                    {
                        publish?.Invoke();
                        committed = true;
                    }
                    catch (Exception exception)
                    {
                        publicationFailure = exception;
                    }
                }

                if (!committed)
                    executeRollback = true;
            }

            if (publicationFailure != null)
                owner.ReportResourceFailure(generation, name, publicationFailure);
            if (executeRollback)
                _ = ExecuteRollback();
            return committed;
        }

        /// <summary>
        /// Requests the owned resource's idempotent teardown. If the external boundary has not
        /// returned yet this only records the request; Commit or Dispose performs the callback
        /// after the boundary is known to be complete.
        /// </summary>
        public bool RequestRollback()
        {
            var executeRollback = false;
            lock (owner.gate)
            {
                generation.CleanupRequestedUnsafe = true;
                if (!completed
                    || rolledBack
                    || rollbackInProgress
                    || deferRollback?.Invoke() == true)
                    return true;
                executeRollback = true;
            }

            return !executeRollback || ExecuteRollback();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            var executeRollback = false;
            lock (owner.gate)
            {
                if (!completed)
                {
                    // The stage is leaving after the boundary returned or threw. There is no
                    // safe reason to retain an uncommitted local resource.
                    completed = true;
                    executeRollback = true;
                }
            }

            if (executeRollback)
                _ = ExecuteRollback();
        }

        private bool ExecuteRollback()
        {
            lock (owner.gate)
            {
                if (rolledBack || rollbackInProgress || !completed)
                    return true;
                rollbackInProgress = true;
            }

            Exception? failure = null;
            try
            {
                rollback();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            lock (owner.gate)
            {
                rollbackInProgress = false;
                if (failure == null)
                {
                    rolledBack = true;
                    generation.ResourcesUnsafe.Remove(this);
                }
            }

            if (failure != null)
            {
                owner.ReportResourceFailure(generation, name, failure);
                return false;
            }

            bool drain;
            lock (owner.gate)
                drain = generation.ResourceDrainEnabledUnsafe
                    && generation.CleanupRequestedUnsafe;
            if (drain)
                _ = owner.DrainResourceRollbacks(generation);
            return true;
        }
    }

    public sealed class PluginStage : IDisposable
    {
        private readonly PluginGeneration generation;
        private int disposed;

        internal PluginStage(PluginGeneration generation, bool allowRunning)
        {
            this.generation = generation;
            AllowRunning = allowRunning;
        }

        internal PluginGeneration Generation => generation;
        internal bool AllowRunning { get; }
        internal bool IsDisposedUnsafe => Volatile.Read(ref disposed) != 0;

        public PluginGenerationResource RegisterResource(string name, Action rollback) =>
            generation.owner.TryRegisterResource(
                this,
                name,
                rollback,
                deferRollback: null)
            ?? throw new OperationCanceledException(
                "plugin generation cannot register a resource after cleanup",
                generation.CancellationToken);

        /// <summary>
        /// Registers a rollback dependency. The predicate is evaluated only after the external
        /// boundary has completed; while it is true cleanup leaves this resource retained for a
        /// dependent late completion. Production uses this only to keep configuration shutdown
        /// behind a concurrently running activation boundary.
        /// </summary>
        internal PluginGenerationResource RegisterResource(
            string name,
            Action rollback,
            Func<bool> deferRollback) =>
            generation.owner.TryRegisterResource(
                this,
                name,
                rollback,
                deferRollback)
            ?? throw new OperationCanceledException(
                "plugin generation cannot register a resource after cleanup",
                generation.CancellationToken);

        /// <summary>
        /// Compatibility overload for callers that need to defer a rollback while any other
        /// reservation is still pending.
        /// </summary>
        public PluginGenerationResource RegisterResource(
            string name,
            Action rollback,
            bool deferWhileAnotherBoundaryPending) =>
            generation.owner.TryRegisterResource(
                this,
                name,
                rollback,
                deferWhileAnotherBoundaryPending
                    ? () => generation.HasPendingResourceUnsafe
                    : null)
            ?? throw new OperationCanceledException(
                "plugin generation cannot register a resource after cleanup",
                generation.CancellationToken);

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
