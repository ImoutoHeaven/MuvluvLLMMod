using System.Text;
using System.Text.Json;

namespace MuvluvLLMMod;

public enum TranslationPriority
{
    Background = 0,
    Foreground = 1,
    SceneFallback = 2,
    Urgent = 3,
}

public enum TranslationReservationStatus
{
    Reserved,
    ReservedFromPending,
    AlreadyCached,
    AlreadyInFlight,
    AlreadyReserved,
    BudgetExceeded,
}

public readonly record struct ScenePromptResponse(
    bool TransportFailed,
    string? Content,
    string? FailureReason = null);

internal sealed class AsyncPulse
{
    private readonly object gate = new();
    private TaskCompletionSource<bool> waiter = NewSource();

    public Task WaitAsync()
    {
        lock (gate)
            return waiter.Task;
    }

    public void PulseAll()
    {
        TaskCompletionSource<bool> current;
        lock (gate)
        {
            current = waiter;
            waiter = NewSource();
        }
        current.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class TranslationReservationScope : IDisposable
{
    private readonly TranslationWorkQueue queue;
    private readonly object gate = new();
    private readonly HashSet<string> owned;

    public TranslationReservationScope(TranslationWorkQueue queue, IEnumerable<string> templates)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(templates);
        this.queue = queue;
        owned = new HashSet<string>(templates, StringComparer.Ordinal);
    }

    public void Release(IEnumerable<string> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var toRelease = new List<string>();
        lock (gate)
        {
            foreach (var template in templates)
            {
                if (owned.Remove(template))
                    toRelease.Add(template);
            }
        }

        foreach (var template in toRelease)
            queue.ReleaseReservation(template);
    }

    public void Dispose()
    {
        string[] remaining;
        lock (gate)
        {
            if (owned.Count == 0)
                return;
            remaining = owned.ToArray();
            owned.Clear();
        }

        foreach (var template in remaining)
            queue.ReleaseReservation(template);
    }
}

public sealed class RequestInFlightGate
{
    private readonly object gate = new();
    private readonly AsyncPulse pulse = new();
    private int count;

    public int CurrentCount
    {
        get
        {
            lock (gate)
                return count;
        }
    }

    public event Action? StateChanged;

    public bool HasAvailableSlot(int maximumInFlight)
    {
        lock (gate)
            return count < Math.Max(1, maximumInFlight);
    }

    public bool TryAcquire(int maximumInFlight)
    {
        var limit = Math.Max(1, maximumInFlight);
        lock (gate)
        {
            if (count >= limit)
                return false;
            count++;
            return true;
        }
    }

    public async Task<bool> WaitAcquireAsync(int maximumInFlight, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Task waitTask;
            lock (gate)
            {
                if (count < Math.Max(1, maximumInFlight))
                {
                    count++;
                    return true;
                }
                waitTask = pulse.WaitAsync();
            }

            await waitTask.WaitAsync(token).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        return false;
    }

    public void Release()
    {
        lock (gate)
        {
            if (count > 0)
                count--;
        }

        pulse.PulseAll();
        StateChanged?.Invoke();
    }
}

public sealed class SceneRetryRegistry
{
    public const int MaxEntries = 1024;
    public const int TerminalFlushMaxAttempts = 5;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object gate = new();
    private readonly string? filePath;
    private readonly Action<string>? diagnostic;
    private readonly Func<string, string, bool>? writeAtomic;
    private readonly HashSet<long> scenes = new();
    private readonly LinkedList<long> order = new();
    private readonly Dictionary<long, LinkedListNode<long>> nodes = new();
    private readonly Dictionary<long, long> failureEpochs = new();
    private Task? activeWriterTask;
    private long nextFailureEpoch;
    private long mutationVersion;
    private bool dirty;
    private bool acceptingMutations = true;

    public SceneRetryRegistry(
        IEnumerable<long>? initialScenes = null,
        string? filePath = null,
        Action<string>? diagnostic = null,
        Func<string, string, bool>? writeAtomic = null)
    {
        this.filePath = filePath;
        this.diagnostic = diagnostic;
        this.writeAtomic = writeAtomic;
        if (initialScenes != null)
        {
            foreach (var sceneId in initialScenes)
            {
                if (sceneId <= 0)
                    continue;
                failureEpochs[sceneId] = ++nextFailureEpoch;
                AddUnsafe(sceneId);
            }
        }
    }

    public int FailureEpochCount
    {
        get
        {
            lock (gate)
                return failureEpochs.Count;
        }
    }

    public void Load()
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length <= 0 || info.Length > TranslationBudget.MaxTextUtf8Bytes * 4)
                return;
            var raw = File.ReadAllText(filePath, Utf8NoBom);
            if (!TryDeserialize(raw, out var loaded))
                return;

            lock (gate)
            {
                if (!acceptingMutations)
                    return;
                scenes.Clear();
                order.Clear();
                nodes.Clear();
                failureEpochs.Clear();
                foreach (var sceneId in loaded.Snapshot())
                {
                    failureEpochs[sceneId] = ++nextFailureEpoch;
                    AddUnsafe(sceneId);
                }
            }
        }
        catch (Exception exception)
        {
            Diagnose("scene retry load failed: " + exception.GetType().Name);
        }
    }

    public void Freeze()
    {
        lock (gate)
            acceptingMutations = false;
    }

    public bool Flush() => FlushTerminal(Timeout.InfiniteTimeSpan);

    public bool FlushTerminal(TimeSpan? budget = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return true;

        var totalBudget = budget ?? TranslationBudget.DefaultTerminalFlushBudget;
        if (totalBudget != Timeout.InfiniteTimeSpan && totalBudget <= TimeSpan.Zero)
        {
            lock (gate)
                return !dirty;
        }

        var unbounded = totalBudget == Timeout.InfiniteTimeSpan;
        var deadline = unbounded ? DateTime.MaxValue : DateTime.UtcNow + totalBudget;
        var delay = TimeSpan.FromMilliseconds(40);
        for (var attempt = 0; attempt < TerminalFlushMaxAttempts; attempt++)
        {
            Task writer;
            lock (gate)
            {
                if (!dirty)
                    return true;
                writer = EnsureWriterStartedUnsafe();
            }

            if (unbounded)
            {
                writer.GetAwaiter().GetResult();
            }
            else
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !writer.Wait(remaining))
                {
                    Diagnose("scene retry persistence writer did not complete before the terminal deadline.");
                    return false;
                }
            }

            lock (gate)
            {
                if (!dirty)
                    return true;
            }

            if (attempt + 1 >= TerminalFlushMaxAttempts)
                break;

            if (unbounded)
            {
                Thread.Sleep(delay);
            }
            else
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;
                Thread.Sleep(remaining < delay ? remaining : delay);
            }
            delay = TimeSpan.FromMilliseconds(Math.Min(500, delay.TotalMilliseconds * 2));
        }

        lock (gate)
            return !dirty;
    }

    private Task EnsureWriterStartedUnsafe()
    {
        if (activeWriterTask is { IsCompleted: false })
            return activeWriterTask;

        activeWriterTask = Task.Run(RunWriterPass);
        return activeWriterTask;
    }

    private void RunWriterPass()
    {
        while (true)
        {
            long version;
            long[] snapshot;
            lock (gate)
            {
                if (!dirty || string.IsNullOrWhiteSpace(filePath))
                {
                    activeWriterTask = null;
                    return;
                }
                version = mutationVersion;
                snapshot = order.ToArray();
            }

            var written = WriteAtomic(filePath!, JsonSerializer.Serialize(snapshot));
            lock (gate)
            {
                if (!written)
                {
                    activeWriterTask = null;
                    return;
                }

                if (mutationVersion == version)
                {
                    dirty = false;
                    activeWriterTask = null;
                    return;
                }
            }
        }
    }

    public bool Contains(long sceneId)
    {
        if (sceneId <= 0)
            return false;
        lock (gate)
            return scenes.Contains(sceneId);
    }

    public long GetFailureEpoch(long sceneId)
    {
        if (sceneId <= 0)
            return 0;
        lock (gate)
            return failureEpochs.TryGetValue(sceneId, out var epoch) ? epoch : 0;
    }

    public bool Mark(long sceneId, bool flushImmediately = false)
    {
        if (sceneId <= 0)
            return false;

        bool added;
        var shouldWait = false;
        lock (gate)
        {
            if (!acceptingMutations)
                return false;

            added = AddUnsafe(sceneId);
            failureEpochs[sceneId] = ++nextFailureEpoch;
            if (added && !string.IsNullOrWhiteSpace(filePath))
            {
                MarkDirtyUnsafe();
                _ = EnsureWriterStartedUnsafe();
                shouldWait = flushImmediately;
            }
        }

        if (shouldWait)
            _ = Flush();
        return added;
    }

    public bool Complete(long sceneId, bool flushImmediately = false)
    {
        if (sceneId <= 0)
            return false;

        bool removed;
        var shouldWait = false;
        lock (gate)
        {
            if (!acceptingMutations)
                return false;

            removed = RemoveUnsafe(sceneId);
            if (removed && !string.IsNullOrWhiteSpace(filePath))
            {
                MarkDirtyUnsafe();
                _ = EnsureWriterStartedUnsafe();
                shouldWait = flushImmediately;
            }
        }

        if (shouldWait)
            _ = Flush();
        return removed;
    }

    public bool TryComplete(long sceneId, long expectedFailureEpoch, bool flushImmediately = false)
    {
        if (sceneId <= 0)
            return false;

        bool removed;
        var shouldWait = false;
        lock (gate)
        {
            if (!acceptingMutations)
                return false;

            var current = failureEpochs.TryGetValue(sceneId, out var epoch) ? epoch : 0;
            if (current != expectedFailureEpoch)
                return false;

            removed = RemoveUnsafe(sceneId);
            if (removed && !string.IsNullOrWhiteSpace(filePath))
            {
                MarkDirtyUnsafe();
                _ = EnsureWriterStartedUnsafe();
                shouldWait = flushImmediately;
            }
        }

        if (shouldWait)
            _ = Flush();
        return removed;
    }

    public long[] Snapshot()
    {
        lock (gate)
            return order.ToArray();
    }

    public string Serialize()
    {
        lock (gate)
            return JsonSerializer.Serialize(order.ToArray());
    }

    public static bool TryDeserialize(string? json, out SceneRetryRegistry registry)
    {
        registry = new SceneRetryRegistry();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var values = JsonSerializer.Deserialize<long[]>(json);
            if (values == null)
                return false;
            registry = new SceneRetryRegistry(values.Where(id => id > 0));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void MarkDirtyUnsafe()
    {
        mutationVersion++;
        dirty = true;
    }

    private bool AddUnsafe(long sceneId)
    {
        if (sceneId <= 0)
            return false;

        if (nodes.TryGetValue(sceneId, out var existing))
        {
            order.Remove(existing);
            order.AddLast(existing);
            return false;
        }

        while (scenes.Count >= MaxEntries && order.First != null)
            RemoveUnsafe(order.First.Value);

        scenes.Add(sceneId);
        nodes[sceneId] = order.AddLast(sceneId);
        return true;
    }

    private bool RemoveUnsafe(long sceneId)
    {
        failureEpochs.Remove(sceneId);
        if (!scenes.Remove(sceneId))
            return false;
        if (nodes.Remove(sceneId, out var node))
            order.Remove(node);
        return true;
    }

    private bool WriteAtomic(string path, string json)
    {
        if (writeAtomic != null)
        {
            try
            {
                return writeAtomic(path, json);
            }
            catch (Exception exception)
            {
                Diagnose("scene retry save failed: " + exception.GetType().Name);
                return false;
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, Utf8NoBom);
            File.Move(temp, path, true);
            return true;
        }
        catch (Exception exception)
        {
            Diagnose("scene retry save failed: " + exception.GetType().Name);
            return false;
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

public sealed class SceneAttemptCoordinator : IDisposable
{
    private readonly RequestInFlightGate gate;
    private readonly TranslationWorkQueue queue;
    private readonly object sync = new();
    private readonly AsyncPulse statePulse = new();
    private readonly SortedSet<long> waitingSequences = new();
    private readonly List<ActiveAttempt> activeAttempts = new();
    private DateTimeOffset? urgentBlockedSince;
    private long nextSequence;
    private int waitingCount;
    private int disposed;

    public SceneAttemptCoordinator(RequestInFlightGate gate, TranslationWorkQueue queue)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(queue);
        this.gate = gate;
        this.queue = queue;
        gate.StateChanged += NotifyStateChanged;
        queue.StateChanged += NotifyStateChanged;
    }

    public long NextSequence() => Interlocked.Increment(ref nextSequence);

    public bool ShouldDeferNonUrgentDispatch =>
        Volatile.Read(ref waitingCount) > 0 && !queue.HasUrgentWork;

    public async Task WaitWhileDeferredAsync(CancellationToken token)
    {
        while (ShouldDeferNonUrgentDispatch)
        {
            var wait = statePulse.WaitAsync();
            if (!ShouldDeferNonUrgentDispatch)
                return;
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async Task<(bool Executed, bool PreemptedByUrgent, T? Result)> RunAttemptAsync<T>(
        Func<int> maximumInFlight,
        TimeSpan urgentPreemptGracePeriod,
        Func<bool> isActive,
        Func<CancellationToken, Task<T>> executeAsync,
        long sequence = 0,
        CancellationToken waitCancellationToken = default,
        CancellationToken shutdownToken = default,
        Action? onPreemptedBeforeRelease = null,
        Action<Func<bool>>? bindDispatchCommit = null)
    {
        using var combinedWaitCts = CancellationTokenSource.CreateLinkedTokenSource(
            waitCancellationToken,
            shutdownToken);
        var combinedWaitToken = combinedWaitCts.Token;
        var attemptSequence = sequence > 0 ? sequence : NextSequence();
        var acquired = false;

        while (isActive() && !combinedWaitToken.IsCancellationRequested)
        {
            var waitUrgent = statePulse.WaitAsync();
            if (queue.HasUrgentWork)
            {
                try
                {
                    await waitUrgent.WaitAsync(combinedWaitToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            lock (sync)
                waitingSequences.Add(attemptSequence);
            Interlocked.Increment(ref waitingCount);
            statePulse.PulseAll();
            try
            {
                while (isActive()
                    && !combinedWaitToken.IsCancellationRequested)
                {
                    var waitSlot = statePulse.WaitAsync();
                    if (queue.HasUrgentWork)
                        break;

                    lock (sync)
                    {
                        if (waitingSequences.Min == attemptSequence
                            && gate.TryAcquire(Math.Max(1, maximumInFlight())))
                        {
                            acquired = true;
                            break;
                        }
                    }

                    try
                    {
                        await waitSlot.WaitAsync(combinedWaitToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                lock (sync)
                    waitingSequences.Remove(attemptSequence);
                Interlocked.Decrement(ref waitingCount);
                statePulse.PulseAll();
            }

            if (acquired)
                break;
        }

        if (!acquired
            || !isActive()
            || shutdownToken.IsCancellationRequested
            || waitCancellationToken.IsCancellationRequested)
        {
            if (acquired)
                gate.Release();
            return (false, false, default);
        }

        var dispatchGate = new object();
        var dispatchCommitted = false;
        var abortedBeforeDispatch = false;
        using var preDispatchAbortCts = new CancellationTokenSource();
        bindDispatchCommit?.Invoke(() =>
        {
            lock (dispatchGate)
            {
                if (dispatchCommitted)
                    return true;

                if (abortedBeforeDispatch || waitCancellationToken.IsCancellationRequested)
                {
                    abortedBeforeDispatch = true;
                    try
                    {
                        preDispatchAbortCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    return false;
                }

                dispatchCommitted = true;
                return true;
            }
        });

        using var preDispatchRegistration = waitCancellationToken.CanBeCanceled
            ? waitCancellationToken.Register(() =>
            {
                var cancelNow = false;
                lock (dispatchGate)
                {
                    if (!dispatchCommitted)
                    {
                        abortedBeforeDispatch = true;
                        cancelNow = true;
                    }
                }
                if (cancelNow)
                {
                    try
                    {
                        preDispatchAbortCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            })
            : default;

        using var preemptCts = new CancellationTokenSource();
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownToken,
            preemptCts.Token,
            preDispatchAbortCts.Token);
        using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        var preempted = 0;
        var activeAttempt = new ActiveAttempt(
            attemptSequence,
            () =>
            {
                if (Interlocked.Exchange(ref preempted, 1) != 0)
                    return false;
                try
                {
                    preemptCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                return true;
            });
        lock (sync)
            activeAttempts.Add(activeAttempt);

        var monitorTask = MonitorUrgentContentionAsync(
            activeAttempt,
            maximumInFlight,
            urgentPreemptGracePeriod,
            isActive,
            doneCts.Token);

        try
        {
            var result = await executeAsync(attemptCts.Token).ConfigureAwait(false);
            lock (dispatchGate)
            {
                if (abortedBeforeDispatch && !dispatchCommitted)
                    return (false, false, default);
            }
            return (true, false, result);
        }
        catch (OperationCanceledException) when (
            preDispatchAbortCts.IsCancellationRequested
            && !shutdownToken.IsCancellationRequested)
        {
            return (false, false, default);
        }
        catch (OperationCanceledException) when (
            Volatile.Read(ref preempted) == 1 && !shutdownToken.IsCancellationRequested)
        {
            onPreemptedBeforeRelease?.Invoke();
            return (true, true, default);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested || !isActive())
        {
            return (false, false, default);
        }
        finally
        {
            try
            {
                doneCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            lock (sync)
                activeAttempts.Remove(activeAttempt);
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch
            {
            }
            gate.Release();
        }
    }

    public async Task<(bool Succeeded, string FallbackReason, int Attempts, IReadOnlyList<string> UnresolvedTemplates)> ExecuteBatchWithRetriesAsync(
        SceneTranslationBatch batch,
        int maximumAttempts,
        Func<int> maximumInFlight,
        TimeSpan urgentPreemptGracePeriod,
        Func<bool> isActive,
        Func<int, string, Action, CancellationToken, Task<ScenePromptResponse>> sendPromptAsync,
        Func<int, IReadOnlyDictionary<string, string>, IReadOnlyCollection<string>> tryCommitTranslations,
        Func<int, bool, string, int> onRetryScheduled,
        Action<int, string>? onRejected = null,
        Action<int>? onPreempted = null,
        long sequence = 0,
        CancellationToken waitCancellationToken = default,
        CancellationToken shutdownToken = default,
        Action<string>? onTerminalFailure = null)
    {
        var batchSequence = sequence > 0 ? sequence : NextSequence();
        var fallbackReason = "request-failed";
        var attempts = 0;
        var boundedMaxAttempts = Math.Max(1, maximumAttempts);

        for (var attempt = 1;
            attempt <= boundedMaxAttempts && isActive() && !shutdownToken.IsCancellationRequested;
            attempt++)
        {
            var slotWaitToken = attempt == 1 ? waitCancellationToken : CancellationToken.None;
            if (slotWaitToken.IsCancellationRequested)
                return (false, "aborted-before-start", 0, batch.TargetTemplates);

            attempts = attempt;
            var preempted = false;
            var isFinalAttempt = attempt == boundedMaxAttempts;
            var previousFailure = attempt == 1
                || fallbackReason is "preempted-by-urgent"
                    or "request-exception"
                    or "response-over-budget"
                    or "invalid-completion-envelope"
                ? null
                : fallbackReason;
            var requestPrompt = SceneTranslationBatch.BuildRetryPrompt(
                batch.Prompt,
                previousFailure);
            IReadOnlyList<string> unresolvedTemplates = Array.Empty<string>();
            Func<bool>? commitDispatch = null;

            var outcome = await RunAttemptAsync(
                maximumInFlight,
                urgentPreemptGracePeriod,
                isActive,
                async ct =>
                {
                    ScenePromptResponse response;
                    try
                    {
                        response = await sendPromptAsync(
                                attempt,
                                requestPrompt,
                                () =>
                                {
                                    if (commitDispatch != null && !commitDispatch())
                                        ct.ThrowIfCancellationRequested();
                                },
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        fallbackReason = "request-exception";
                        if (isFinalAttempt)
                            onTerminalFailure?.Invoke(fallbackReason);
                        return false;
                    }

                    if (response.TransportFailed)
                    {
                        fallbackReason = response.FailureReason ?? "request-exception";
                        if (isFinalAttempt)
                            onTerminalFailure?.Invoke(fallbackReason);
                        return false;
                    }

                    if (!string.IsNullOrEmpty(response.FailureReason))
                    {
                        fallbackReason = response.FailureReason!;
                        onRejected?.Invoke(attempt, fallbackReason);
                        if (isFinalAttempt || fallbackReason == "prompt-over-budget")
                            onTerminalFailure?.Invoke(fallbackReason);
                        return false;
                    }

                    if (!batch.TryParseResponse(
                            response.Content,
                            out var translations,
                            out var rejectionReason))
                    {
                        fallbackReason = rejectionReason;
                        onRejected?.Invoke(attempt, rejectionReason);
                        if (isFinalAttempt)
                            onTerminalFailure?.Invoke(fallbackReason);
                        return false;
                    }

                    if (batch.TargetCount > 0 && translations.Count == 0)
                    {
                        fallbackReason = "no-accepted-entries";
                        onRejected?.Invoke(attempt, fallbackReason);
                        if (isFinalAttempt)
                            onTerminalFailure?.Invoke(fallbackReason);
                        return false;
                    }

                    var persisted = tryCommitTranslations(attempt, translations)
                        ?? Array.Empty<string>();
                    if (translations.Count > 0 && persisted.Count == 0)
                    {
                        fallbackReason = "persist-failed";
                        if (isFinalAttempt)
                            onTerminalFailure?.Invoke(fallbackReason);
                        return false;
                    }

                    var persistedSet = persisted as IReadOnlySet<string>
                        ?? new HashSet<string>(persisted, StringComparer.Ordinal);
                    unresolvedTemplates = batch.TargetTemplates
                        .Where(template => !persistedSet.Contains(template))
                        .ToArray();
                    fallbackReason = "ok";
                    return true;
                },
                batchSequence,
                slotWaitToken,
                shutdownToken,
                onPreemptedBeforeRelease: () =>
                {
                    if (isFinalAttempt)
                        onTerminalFailure?.Invoke("preempted-by-urgent");
                },
                bindDispatchCommit: fn => commitDispatch = fn).ConfigureAwait(false);

            if (!outcome.Executed || !isActive() || shutdownToken.IsCancellationRequested)
            {
                if (slotWaitToken.IsCancellationRequested
                    && isActive()
                    && !shutdownToken.IsCancellationRequested)
                {
                    return (false, "aborted-before-start", 0, batch.TargetTemplates);
                }
                return (false, "cancelled", attempts, Array.Empty<string>());
            }

            if (outcome.PreemptedByUrgent)
            {
                preempted = true;
                fallbackReason = "preempted-by-urgent";
                onPreempted?.Invoke(attempt);
            }
            else if (outcome.Result)
            {
                return (true, "ok", attempts, unresolvedTemplates);
            }

            if (fallbackReason == "prompt-over-budget")
                break;

            if (attempt < boundedMaxAttempts && isActive() && !shutdownToken.IsCancellationRequested)
            {
                var retryDelayMs = onRetryScheduled(attempt, preempted, fallbackReason);
                if (retryDelayMs > 0)
                {
                    try
                    {
                        await Task.Delay(retryDelayMs, shutdownToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return (false, "cancelled", attempts, Array.Empty<string>());
                    }
                }
            }
        }

        return (false, fallbackReason, attempts, batch.TargetTemplates);
    }

    public async Task<bool> ExecuteScenePlansAsync(
        IReadOnlyList<SceneBatchPlan> plans,
        TranslationReservationScope reservationScope,
        int maximumAttempts,
        Func<int> maximumInFlight,
        TimeSpan urgentPreemptGracePeriod,
        Func<bool> isActive,
        Func<int, int, int, string, Action, CancellationToken, Task<ScenePromptResponse>> sendPromptAsync,
        Func<int, int, int, IReadOnlyDictionary<string, string>, IReadOnlyCollection<string>> tryCommitTranslations,
        Func<int, int, int, bool, string, int> onRetryScheduled,
        Action<int, int, IReadOnlyList<string>, string, int>? onChunkFallback,
        Action<int, int>? onChunkCancelled = null,
        Action<int, int, int, string>? onRejected = null,
        Action<int, int, int>? onPreempted = null,
        CancellationToken shutdownToken = default)
    {
        if (plans == null || plans.Count == 0)
            return true;

        var allSucceeded = true;
        string? upstreamAbortReason = null;
        using var unstartedAbortCts = new CancellationTokenSource();
        var chunkTasks = new List<Task<bool>>(plans.Count);
        var chunkCount = plans.Count;

        for (var index = 0; index < chunkCount; index++)
        {
            var chunkIndex = index + 1;
            var plan = plans[index];
            if (!isActive() || shutdownToken.IsCancellationRequested)
            {
                allSucceeded = false;
                for (var remaining = index; remaining < chunkCount; remaining++)
                    reservationScope.Release(plans[remaining].ReservedTemplates);
                break;
            }

            if (unstartedAbortCts.IsCancellationRequested)
            {
                allSucceeded = false;
                reservationScope.Release(plan.ReservedTemplates);
                if (isActive() && !shutdownToken.IsCancellationRequested)
                {
                    onChunkFallback?.Invoke(
                        chunkIndex,
                        chunkCount,
                        plan.ReservedTemplates,
                        $"upstream-chunk-{upstreamAbortReason ?? "request-failed"}",
                        0);
                }
                continue;
            }

            var sequence = NextSequence();
            var firstDispatchSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            chunkTasks.Add(Task.Run(async () =>
            {
                var succeeded = false;
                var fallbackReason = "request-failed";
                var attempts = 0;
                IReadOnlyList<string> unresolvedTemplates = Array.Empty<string>();
                try
                {
                    var result = await ExecuteBatchWithRetriesAsync(
                        plan.Batch,
                        maximumAttempts,
                        maximumInFlight,
                        urgentPreemptGracePeriod,
                        isActive,
                        (attempt, requestPrompt, onAttemptDispatch, ct) => sendPromptAsync(
                            chunkIndex,
                            chunkCount,
                            attempt,
                            requestPrompt,
                            () =>
                            {
                                onAttemptDispatch();
                                firstDispatchSignal.TrySetResult();
                            },
                            ct),
                        (attempt, translations) => tryCommitTranslations(
                            chunkIndex,
                            chunkCount,
                            attempt,
                            translations),
                        (attempt, preempted, reason) => onRetryScheduled(
                            chunkIndex,
                            chunkCount,
                            attempt,
                            preempted,
                            reason),
                        onRejected == null
                            ? null
                            : (attempt, reason) => onRejected(chunkIndex, chunkCount, attempt, reason),
                        onPreempted == null
                            ? null
                            : attempt => onPreempted(chunkIndex, chunkCount, attempt),
                        sequence,
                        unstartedAbortCts.Token,
                        shutdownToken,
                        reason =>
                        {
                            if (reason is "request-exception" or "empty-response" or "preempted-by-urgent")
                            {
                                Interlocked.CompareExchange(ref upstreamAbortReason, reason, null);
                                try
                                {
                                    unstartedAbortCts.Cancel();
                                }
                                catch (ObjectDisposedException)
                                {
                                }
                            }
                        }).ConfigureAwait(false);

                    succeeded = result.Succeeded;
                    fallbackReason = result.FallbackReason;
                    attempts = result.Attempts;
                    unresolvedTemplates = result.UnresolvedTemplates;
                }
                finally
                {
                    firstDispatchSignal.TrySetResult();
                    reservationScope.Release(plan.ReservedTemplates);
                }

                if (succeeded)
                {
                    if (unresolvedTemplates.Count > 0
                        && isActive()
                        && !shutdownToken.IsCancellationRequested)
                    {
                        onChunkFallback?.Invoke(
                            chunkIndex,
                            chunkCount,
                            unresolvedTemplates,
                            "partial-entry-drop",
                            attempts);
                        return false;
                    }
                    return true;
                }

                if (fallbackReason == "cancelled")
                {
                    onChunkCancelled?.Invoke(chunkIndex, chunkCount);
                    return false;
                }

                if (fallbackReason == "aborted-before-start")
                {
                    if (isActive() && !shutdownToken.IsCancellationRequested)
                    {
                        onChunkFallback?.Invoke(
                            chunkIndex,
                            chunkCount,
                            plan.ReservedTemplates,
                            $"upstream-chunk-{upstreamAbortReason ?? "request-failed"}",
                            0);
                    }
                    return false;
                }

                if (isActive() && !shutdownToken.IsCancellationRequested)
                {
                    onChunkFallback?.Invoke(
                        chunkIndex,
                        chunkCount,
                        plan.ReservedTemplates,
                        fallbackReason,
                        attempts);
                }
                return false;
            }));

            await firstDispatchSignal.Task.ConfigureAwait(false);
        }

        var outcomes = await Task.WhenAll(chunkTasks).ConfigureAwait(false);
        for (var i = 0; i < outcomes.Length; i++)
        {
            if (!outcomes[i])
                allSucceeded = false;
        }

        return allSucceeded;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        gate.StateChanged -= NotifyStateChanged;
        queue.StateChanged -= NotifyStateChanged;
        statePulse.PulseAll();
    }

    private void NotifyStateChanged() => statePulse.PulseAll();

    private async Task MonitorUrgentContentionAsync(
        ActiveAttempt currentAttempt,
        Func<int> maximumInFlight,
        TimeSpan gracePeriod,
        Func<bool> isActive,
        CancellationToken stopToken)
    {
        var effectiveGrace = gracePeriod < TimeSpan.Zero ? TimeSpan.Zero : gracePeriod;
        while (!stopToken.IsCancellationRequested && isActive())
        {
            var waitState = statePulse.WaitAsync();
            var urgentBlocked = queue.HasUrgentWork
                && !gate.HasAvailableSlot(Math.Max(1, maximumInFlight()));
            TimeSpan waitDuration;
            lock (sync)
            {
                if (!urgentBlocked)
                {
                    urgentBlockedSince = null;
                    waitDuration = Timeout.InfiniteTimeSpan;
                }
                else
                {
                    var now = DateTimeOffset.UtcNow;
                    urgentBlockedSince ??= now;
                    var elapsed = now - urgentBlockedSince.Value;
                    if (elapsed >= effectiveGrace && IsLatestActiveAttemptNoLock(currentAttempt))
                    {
                        urgentBlockedSince = now;
                        if (currentAttempt.TryPreempt())
                            return;
                    }
                    var remaining = effectiveGrace - elapsed;
                    waitDuration = remaining <= TimeSpan.Zero
                        ? TimeSpan.FromMilliseconds(5)
                        : (remaining < TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20));
                }
            }

            try
            {
                if (waitDuration == Timeout.InfiniteTimeSpan)
                    await waitState.WaitAsync(stopToken).ConfigureAwait(false);
                else
                    await Task.WhenAny(waitState, Task.Delay(waitDuration, stopToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool IsLatestActiveAttemptNoLock(ActiveAttempt candidate)
    {
        var maxSequence = long.MinValue;
        for (var i = 0; i < activeAttempts.Count; i++)
        {
            if (activeAttempts[i].Sequence > maxSequence)
                maxSequence = activeAttempts[i].Sequence;
        }
        return candidate.Sequence == maxSequence;
    }

    private sealed record ActiveAttempt(long Sequence, Func<bool> TryPreempt);
}
