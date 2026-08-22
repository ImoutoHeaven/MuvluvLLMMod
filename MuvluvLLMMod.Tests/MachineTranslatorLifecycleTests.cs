using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class MachineTranslatorLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.Lifecycle.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Blocked_priority_is_rejected_while_lifecycle_has_no_current_translator()
    {
        var retryPolicy = new TranslationRetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Assert.True(retryPolicy.RecordFailure("封鎖する").Blocked);
        var lifecycle = new MachineTranslatorLifecycle(retryPolicy: retryPolicy);

        Assert.False(lifecycle.EnqueuePriority("封鎖する"));
    }

    [Fact]
    public async Task Reload_replaces_the_policy_used_for_priority_retention()
    {
        var oldPolicy = new TranslationRetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Assert.True(oldPolicy.RecordFailure("設定を直す").Blocked);
        var newPolicy = new TranslationRetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var lifecycle = new MachineTranslatorLifecycle(retryPolicy: oldPolicy);

        lifecycle.Reload(false, 1, null, newPolicy);

        Assert.True(lifecycle.EnqueuePriority("設定を直す"));
        Assert.Equal(1, oldPolicy.BlockedCount);
        Assert.Equal(0, newPolicy.BlockedCount);
        await lifecycle.TransitionTask;
    }

    [Fact]
    public async Task Delayed_priority_is_retained_while_lifecycle_has_no_current_translator()
    {
        var nowTicks = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        DateTimeOffset UtcNow() => new(Interlocked.Read(ref nowTicks), TimeSpan.Zero);
        var retryPolicy = new TranslationRetryPolicy(
            3,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            UtcNow);
        retryPolicy.RecordFailure("待機する");
        var cache = new TranslationCache(root);
        Assert.True(cache.ObservePriority("待機する", "待機する"));
        Assert.True(cache.TryGetPendingGeneration("待機する", out var pendingGeneration));
        var translated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new MachineTranslatorLifecycle(retryPolicy: retryPolicy);

        Assert.True(lifecycle.EnqueuePriority("待機する", pendingGeneration));
        lifecycle.Initialize(true, 1000, (_, backlog) => new MachineTranslator(
            cache,
            (_, _) =>
            {
                translated.TrySetResult();
                return Task.FromResult<string?>("等待");
            },
            1,
            TimeSpan.FromMilliseconds(10),
            backlog,
            retryPolicy: retryPolicy));
        await Task.Delay(30);
        Assert.False(translated.Task.IsCompleted);

        Interlocked.Add(ref nowTicks, TimeSpan.FromMinutes(1).Ticks);
        await translated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.Shutdown();
    }

    [Fact]
    public async Task Throwing_lifecycle_diagnostic_does_not_fault_reload_transition()
    {
        var lifecycle = new MachineTranslatorLifecycle(_ => throw new InvalidOperationException("diagnostic"));

        lifecycle.Reload(true, 1, (_, _) => throw new InvalidOperationException("factory"));

        await lifecycle.TransitionTask;
    }

    [Fact]
    public async Task Rapid_reloads_wait_for_blocked_old_worker_and_start_only_latest_generation()
    {
        var cache = new TranslationCache(root);
        cache.ObservePriority("再読込する", "再読込する");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var active = 0;
        var maxActive = 0;
        var lifecycle = new MachineTranslatorLifecycle();

        MachineTranslator Factory(RequestRateLimiter _, TranslationPriorityBacklog backlog)
        {
            var generation = Interlocked.Increment(ref created);
            return new MachineTranslator(cache, async (_, _) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, nowActive);
                try
                {
                    if (generation == 1)
                    {
                        firstStarted.TrySetResult();
                        return await releaseFirst.Task;
                    }
                    return "重新加载";
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, 1, TimeSpan.FromSeconds(1), backlog);
        }

        lifecycle.Initialize(true, 2, Factory);
        await firstStarted.Task;
        lifecycle.Reload(true, 2, Factory);
        lifecycle.Reload(true, 2, Factory);
        await Task.Delay(50);
        Assert.Equal(1, created);

        releaseFirst.SetResult(null);
        await lifecycle.TransitionTask;
        await WaitUntilAsync(() => cache.TryGetGenerated("再読込する", out _));

        Assert.Equal(2, created);
        Assert.Equal(1, maxActive);
        lifecycle.Shutdown();
    }

    [Fact]
    public async Task Reload_carries_request_rate_window_to_next_generation()
    {
        var cache = new TranslationCache(root);
        var lifecycle = new MachineTranslatorLifecycle();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var delays = new List<TimeSpan>();

        MachineTranslator Factory(RequestRateLimiter limiter, TranslationPriorityBacklog backlog)
        {
            delays.Add(limiter.Reserve(start));
            return new MachineTranslator(cache, (_, _) => Task.FromResult<string?>(null), 1, TimeSpan.FromSeconds(1), backlog);
        }

        lifecycle.Initialize(true, 2, Factory);
        lifecycle.Reload(true, 2, Factory);
        await lifecycle.TransitionTask;

        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(500) }, delays);
        lifecycle.Shutdown();
    }

    [Fact]
    public async Task Reload_preserves_in_flight_runtime_priority_ahead_of_normal_work()
    {
        var cache = new TranslationCache(root);
        cache.ObserveNormal("通常一する", "通常一する");
        cache.ObserveNormal("通常二する", "通常二する");
        cache.ObserveNormal("優先する", "優先する");
        cache.ObserveNormal("通常三する", "通常三する");
        var lifecycle = new MachineTranslatorLifecycle();
        var resolver = new TranslationResolver(cache, value => lifecycle.EnqueuePriority(value), _ => { });
        resolver.Resolve("優先する");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementOrder = new List<string>();
        var generation = 0;

        MachineTranslator Factory(RequestRateLimiter _, TranslationPriorityBacklog backlog)
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            return new MachineTranslator(cache, async (template, token) =>
            {
                if (currentGeneration == 1)
                {
                    using var registration = token.Register(() => firstCanceled.TrySetResult());
                    firstStarted.TrySetResult();
                    return await releaseFirst.Task;
                }
                lock (replacementOrder) replacementOrder.Add(template);
                return "翻译";
            }, 1, TimeSpan.FromSeconds(1), backlog);
        }

        lifecycle.Initialize(true, 2, Factory);
        await firstStarted.Task;
        lifecycle.Reload(true, 2, Factory);
        await firstCanceled.Task;
        releaseFirst.SetResult(null);
        await lifecycle.TransitionTask;
        await WaitUntilAsync(() =>
        {
            lock (replacementOrder) return replacementOrder.Count > 0;
        });

        lock (replacementOrder) Assert.Equal("優先する", replacementOrder[0]);
        lifecycle.Shutdown();
    }

    [Fact]
    public async Task Cancel_during_reload_handoff_cannot_resurrect_covered_priority_work()
    {
        var cache = new TranslationCache(root);
        var lifecycle = new MachineTranslatorLifecycle();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementCalls = new List<string>();
        var generation = 0;

        MachineTranslator Factory(RequestRateLimiter _, TranslationPriorityBacklog backlog)
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            return new MachineTranslator(cache, async (template, token) =>
            {
                if (currentGeneration == 1)
                {
                    using var registration = token.Register(() => firstCanceled.TrySetResult());
                    firstStarted.TrySetResult();
                    return await releaseFirst.Task;
                }
                lock (replacementCalls) replacementCalls.Add(template);
                return "翻译";
            }, 1, TimeSpan.FromSeconds(1), backlog);
        }

        lifecycle.Initialize(true, 2, Factory);
        lifecycle.EnqueuePriority("取消する");
        await firstStarted.Task;
        lifecycle.EnqueuePriority("残すする");
        lifecycle.Reload(true, 2, Factory);
        await firstCanceled.Task;

        Assert.True(lifecycle.Cancel("取消する"));
        releaseFirst.SetResult(null);
        await lifecycle.TransitionTask;
        await WaitUntilAsync(() =>
        {
            lock (replacementCalls) return replacementCalls.Contains("残すする");
        });
        await Task.Delay(50);

        lock (replacementCalls) Assert.Equal(new[] { "残すする" }, replacementCalls);
        lifecycle.Shutdown();
    }

    [Fact]
    public async Task Active_inflight_cancel_cannot_publish_and_future_generation_can_run()
    {
        var cache = new TranslationCache(root);
        var lifecycle = new MachineTranslatorLifecycle();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var attempt = 0;

        MachineTranslator Factory(RequestRateLimiter _, TranslationPriorityBacklog backlog) =>
            new(cache, async (template, _) =>
            {
                lock (calls) calls.Add(template);
                if (template == "取消する" && Interlocked.Increment(ref attempt) == 1)
                {
                    firstStarted.TrySetResult();
                    return await releaseFirst.Task;
                }
                return "后续翻译";
            }, 1, TimeSpan.FromSeconds(1), backlog);

        lifecycle.Initialize(true, 2, Factory);
        var resolver = new TranslationResolver(
            cache,
            value => lifecycle.EnqueuePriority(value),
            value => lifecycle.EnqueueNormal(value),
            value => lifecycle.Cancel(value));
        resolver.Resolve("取消する");
        await firstStarted.Task;

        resolver.PublishCurated(new Dictionary<string, string> { ["取消する"] = "远程翻译" });
        releaseFirst.SetResult("不应写入");
        await Task.Delay(50);
        Assert.False(cache.TryGetGenerated("取消する", out _));

        lifecycle.Reload(true, 2, Factory);
        await lifecycle.TransitionTask;
        await Task.Delay(30);
        lock (calls) Assert.Equal(new[] { "取消する" }, calls);

        Assert.True(cache.ObservePriority("取消する", "取消する"));
        Assert.True(lifecycle.EnqueuePriority("取消する"));
        await WaitUntilAsync(() => cache.TryGetGenerated("取消する", out _));
        lock (calls) Assert.Equal(new[] { "取消する", "取消する" }, calls);
        lifecycle.Shutdown();
    }

    [Fact]
    public async Task Same_template_priority_observed_after_handoff_cancel_survives_cutoff()
    {
        var cache = new TranslationCache(root);
        var lifecycle = new MachineTranslatorLifecycle();
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementCalls = new List<string>();
        var generation = 0;

        MachineTranslator Factory(RequestRateLimiter _, TranslationPriorityBacklog backlog)
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            return new MachineTranslator(cache, async (template, token) =>
            {
                if (currentGeneration == 1)
                {
                    using var registration = token.Register(() => oldCanceled.TrySetResult());
                    oldStarted.TrySetResult();
                    return await releaseOld.Task;
                }
                lock (replacementCalls) replacementCalls.Add(template);
                return "新翻译";
            }, 1, TimeSpan.FromSeconds(1), backlog);
        }

        cache.ObservePriority("再登録する", "再登録する");
        lifecycle.Initialize(true, 2, Factory);
        lifecycle.EnqueuePriority("再登録する");
        await oldStarted.Task;
        lifecycle.Reload(true, 2, Factory);
        await oldCanceled.Task;

        cache.CancelPending("再登録する");
        Assert.True(lifecycle.Cancel("再登録する"));
        Assert.True(cache.ObserveNormal("通常する", "通常する"));
        Assert.True(cache.ObservePriority("再登録する", "再登録する"));
        Assert.True(lifecycle.EnqueuePriority("再登録する"));
        releaseOld.SetResult("旧翻译");
        await lifecycle.TransitionTask;
        await WaitUntilAsync(() => cache.TryGetGenerated("再登録する", out _) && cache.TryGetGenerated("通常する", out _));

        lock (replacementCalls) Assert.Equal(new[] { "再登録する", "通常する" }, replacementCalls);
        Assert.True(cache.TryGetGenerated("再登録する", out var generated));
        Assert.Equal("新翻译", generated);
        lifecycle.Shutdown();
    }

    [Fact]
    public async Task Disabled_backlog_rejects_delayed_enqueue_after_curated_cancellation()
    {
        var cache = new TranslationCache(root);
        var lifecycle = new MachineTranslatorLifecycle();
        var enqueueEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEnqueue = new ManualResetEventSlim();
        bool? accepted = null;
        var resolver = new TranslationResolver(
            cache,
            (value, pendingGeneration) =>
            {
                enqueueEntered.TrySetResult();
                releaseEnqueue.Wait();
                accepted = lifecycle.EnqueuePriority(value, pendingGeneration);
            },
            (value, pendingGeneration) => lifecycle.EnqueueNormal(value, pendingGeneration),
            (value, pendingGeneration) => lifecycle.Cancel(value, pendingGeneration));
        var observation = Task.Run(() => resolver.Resolve("遅延待機する"));
        await enqueueEntered.Task;

        resolver.PublishCurated(new Dictionary<string, string> { ["遅延待機する"] = "远程翻译" });
        releaseEnqueue.Set();
        await observation;

        Assert.False(accepted);
        Assert.Empty(cache.PendingSnapshot());
    }

    [Fact]
    public async Task Reload_preserves_deferred_priority_fifo_before_normal_once()
    {
        var cache = new TranslationCache(root);
        Assert.True(cache.ObservePriority("再試行する", "再試行する"));
        Assert.True(cache.TryGetPendingGeneration("再試行する", out var oldPendingGeneration));
        var lifecycle = new MachineTranslatorLifecycle();
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementCalls = new List<string>();
        var machineGeneration = 0;

        MachineTranslator Factory(RequestRateLimiter _, TranslationPriorityBacklog backlog)
        {
            var currentGeneration = Interlocked.Increment(ref machineGeneration);
            return new MachineTranslator(cache, async (template, token) =>
            {
                if (currentGeneration == 1)
                {
                    using var registration = token.Register(() => oldCanceled.TrySetResult());
                    oldStarted.TrySetResult();
                    return await releaseOld.Task;
                }
                lock (replacementCalls) replacementCalls.Add(template);
                return "新翻译";
            }, 1, TimeSpan.FromSeconds(10), backlog);
        }

        lifecycle.Initialize(true, 2, Factory);
        await oldStarted.Task;
        Assert.True(cache.CancelPending("再試行する", out var canceledGeneration));
        Assert.Equal(oldPendingGeneration, canceledGeneration);
        Assert.True(cache.ObservePriority("再試行する", "再試行する"));
        Assert.True(cache.TryGetPendingGeneration("再試行する", out var freshPendingGeneration));
        Assert.True(freshPendingGeneration > oldPendingGeneration);
        Assert.False(lifecycle.EnqueuePriority("再試行する", freshPendingGeneration));
        Assert.True(cache.ObservePriority("別優先する", "別優先する"));
        Assert.True(cache.TryGetPendingGeneration("別優先する", out var otherPriorityGeneration));
        Assert.True(lifecycle.EnqueuePriority("別優先する", otherPriorityGeneration));
        Assert.True(cache.ObserveNormal("通常する", "通常する"));
        Assert.True(cache.TryGetPendingGeneration("通常する", out var normalGeneration));
        Assert.True(lifecycle.EnqueueNormal("通常する", normalGeneration));

        lifecycle.Reload(true, 2, Factory);
        await oldCanceled.Task;
        lifecycle.Cancel("再試行する", oldPendingGeneration);
        releaseOld.SetResult("旧翻译");
        await lifecycle.TransitionTask;
        await WaitUntilAsync(() =>
            cache.TryGetGenerated("再試行する", out _)
            && cache.TryGetGenerated("別優先する", out _)
            && cache.TryGetGenerated("通常する", out _));

        lock (replacementCalls) Assert.Equal(new[] { "再試行する", "別優先する", "通常する" }, replacementCalls);
        Assert.True(cache.TryGetGenerated("再試行する", out var generated));
        Assert.Equal("新翻译", generated);
        Assert.Empty(cache.PendingSnapshot());
        lifecycle.Shutdown();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
