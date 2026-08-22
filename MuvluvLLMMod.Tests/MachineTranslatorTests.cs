using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class MachineTranslatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.Machine.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Priority_backlog_runs_before_start_and_success_is_cached()
    {
        var cache = new TranslationCache(root);
        cache.ObservePriority("スキル", "スキル");
        var calls = new List<string>();
        var machine = new MachineTranslator(cache, (text, _) =>
        {
            calls.Add(text);
            return Task.FromResult<string?>("技能");
        }, 1, TimeSpan.FromSeconds(1));

        Assert.True(machine.EnqueuePriority("スキル"));
        machine.Start();
        await WaitUntilAsync(() => cache.TryGetGenerated("スキル", out _));
        await machine.StopAsync();

        Assert.Equal(new[] { "スキル" }, calls);
        Assert.Empty(cache.PendingSnapshot());
        Assert.Equal((1, 0, 0), machine.ProgressSnapshot);
    }

    [Fact]
    public async Task Curated_reconciliation_cancels_priority_backlog_before_start()
    {
        var cache = new TranslationCache(root);
        var calls = new List<string>();
        var machine = new MachineTranslator(cache, (text, _) =>
        {
            calls.Add(text);
            return Task.FromResult<string?>("不应调用");
        }, 1, TimeSpan.FromSeconds(1));
        var resolver = new TranslationResolver(
            cache,
            value => machine.EnqueuePriority(value),
            value => machine.EnqueueNormal(value),
            value => machine.Cancel(value));
        resolver.Resolve("取消する");

        resolver.PublishCurated(new Dictionary<string, string> { ["取消する"] = "远程翻译" });
        machine.Start();
        await Task.Delay(30);
        await machine.StopAsync();

        Assert.Empty(calls);
        Assert.Empty(cache.PendingSnapshot());
    }

    [Fact]
    public async Task Curated_reconciliation_cancels_queued_normal_work()
    {
        var cache = new TranslationCache(root);
        cache.ObserveNormal("先行する", "先行する");
        cache.ObserveNormal("取消する", "取消する");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var machine = new MachineTranslator(cache, async (text, _) =>
        {
            lock (calls) calls.Add(text);
            if (text == "先行する")
            {
                started.TrySetResult();
                return await release.Task;
            }
            return "不应调用";
        }, 1, TimeSpan.FromSeconds(1));
        var resolver = new TranslationResolver(
            cache,
            value => machine.EnqueuePriority(value),
            value => machine.EnqueueNormal(value),
            value => machine.Cancel(value));
        machine.Start();
        await started.Task;

        resolver.PublishCurated(new Dictionary<string, string> { ["取消する"] = "远程翻译" });
        release.SetResult("先行翻译");
        await WaitUntilAsync(() => cache.TryGetGenerated("先行する", out _));
        await Task.Delay(30);
        await machine.StopAsync();

        lock (calls) Assert.Equal(new[] { "先行する" }, calls);
        Assert.Empty(cache.PendingSnapshot());
    }

    [Fact]
    public async Task Curated_pending_removal_linearizes_before_worker_publication()
    {
        var cache = new TranslationCache(root);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTransport = new TaskCompletionSource<string?>();
        var secondTransport = new TaskCompletionSource<string?>();
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCancel = new ManualResetEventSlim();
        var machine = new MachineTranslator(cache, (template, _) =>
        {
            if (template == "競合する")
            {
                requestStarted.TrySetResult();
                return firstTransport.Task;
            }
            secondStarted.TrySetResult();
            return secondTransport.Task;
        }, 1, TimeSpan.FromSeconds(1));
        var resolver = new TranslationResolver(
            cache,
            value => machine.EnqueuePriority(value),
            value => machine.EnqueueNormal(value),
            (value, pendingGeneration) =>
            {
                cancelEntered.TrySetResult();
                releaseCancel.Wait();
                machine.Cancel(value, pendingGeneration);
            });
        machine.Start();
        resolver.Resolve("競合する");
        await requestStarted.Task;
        resolver.Resolve("次する");

        var publication = Task.Run(() =>
            resolver.PublishCurated(new Dictionary<string, string> { ["競合する"] = "远程翻译" }));
        await cancelEntered.Task;
        firstTransport.SetResult("不应写入");
        await secondStarted.Task;

        try
        {
            Assert.False(cache.TryGetGenerated("競合する", out _));
        }
        finally
        {
            releaseCancel.Set();
            secondTransport.TrySetResult(null);
            await publication;
            await machine.StopAsync();
        }
    }

    [Fact]
    public async Task Fresh_same_template_generation_survives_blocked_curated_cancellation()
    {
        var cache = new TranslationCache(root);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<string?>();
        var releaseFresh = new TaskCompletionSource<string?>();
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCancel = new ManualResetEventSlim();
        var attempts = 0;
        var machine = new MachineTranslator(cache, async (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstStarted.TrySetResult();
                return await releaseFirst.Task;
            }
            freshStarted.TrySetResult();
            return await releaseFresh.Task;
        }, 1, TimeSpan.FromSeconds(10));
        var resolver = new TranslationResolver(
            cache,
            value => machine.EnqueuePriority(value),
            value => machine.EnqueueNormal(value),
            (value, pendingGeneration) =>
            {
                cancelEntered.TrySetResult();
                releaseCancel.Wait();
                machine.Cancel(value, pendingGeneration);
            });
        machine.Start();
        resolver.Resolve("再観測する");
        await firstStarted.Task;

        var publication = Task.Run(() =>
            resolver.PublishCurated(new Dictionary<string, string> { ["再観測する"] = "远程翻译" }));
        await cancelEntered.Task;
        resolver.Resolve("再観測する");
        releaseFirst.SetResult("旧翻译");

        try
        {
            await WaitUntilAsync(() => cache.TryGetGenerated("再観測する", out _) || freshStarted.Task.IsCompleted);
            Assert.False(cache.TryGetGenerated("再観測する", out var stale) && stale == "旧翻译");
            Assert.Contains("再観測する", cache.PendingSnapshot());
            releaseCancel.Set();
            await publication;
            await freshStarted.Task;
            Assert.Contains("再観測する", cache.PendingSnapshot());
            releaseFresh.SetResult("新翻译");
            await WaitUntilAsync(() => cache.TryGetGenerated("再観測する", out var value) && value == "新翻译");
            Assert.Empty(cache.PendingSnapshot());
            Assert.Equal(2, attempts);
        }
        finally
        {
            releaseCancel.Set();
            releaseFresh.TrySetResult(null);
            await publication;
            await machine.StopAsync();
        }
    }

    [Fact]
    public async Task Invalid_old_generation_does_not_back_off_a_newer_deferred_priority()
    {
        var cache = new TranslationCache(root);
        Assert.True(cache.ObservePriority("更新する", "更新する"));
        Assert.True(cache.TryGetPendingGeneration("更新する", out var oldGeneration));
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<string?>();
        var attempts = 0;
        var retryPolicy = new TranslationRetryPolicy(
            3,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
        var machine = new MachineTranslator(
            cache,
            async (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    oldStarted.TrySetResult();
                    return await releaseOld.Task;
                }
                return "更新翻译";
            },
            1,
            TimeSpan.FromSeconds(10),
            retryPolicy: retryPolicy);
        machine.Start();
        await oldStarted.Task;

        Assert.True(cache.CancelPending("更新する", out var canceledGeneration));
        Assert.Equal(oldGeneration, canceledGeneration);
        Assert.True(cache.ObservePriority("更新する", "更新する"));
        Assert.True(cache.TryGetPendingGeneration("更新する", out var freshGeneration));
        Assert.True(freshGeneration > oldGeneration);
        Assert.False(machine.EnqueuePriority("更新する", freshGeneration));
        releaseOld.SetResult(null);

        await WaitUntilAsync(() => cache.TryGetGenerated("更新する", out _));
        await machine.StopAsync();

        Assert.Equal(2, attempts);
        Assert.Equal(0, retryPolicy.BlockedCount);
        Assert.True(cache.TryGetGenerated("更新する", out var translated));
        Assert.Equal("更新翻译", translated);
    }

    [Fact]
    public async Task Delayed_observation_enqueue_is_rejected_after_curated_cancellation()
    {
        var cache = new TranslationCache(root);
        var enqueueEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEnqueue = new ManualResetEventSlim();
        var attempts = 0;
        bool? accepted = null;
        var machine = new MachineTranslator(cache, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult<string?>("翻译");
        }, 1, TimeSpan.FromSeconds(10));
        var resolver = new TranslationResolver(
            cache,
            (value, pendingGeneration) => machine.EnqueuePriority(value, pendingGeneration),
            (value, pendingGeneration) =>
            {
                enqueueEntered.TrySetResult();
                releaseEnqueue.Wait();
                accepted = machine.EnqueueNormal(value, pendingGeneration);
            },
            (value, pendingGeneration) => machine.Cancel(value, pendingGeneration));
        machine.Start();
        var observation = Task.Run(() => resolver.Resolve("遅延する", priority: false));
        await enqueueEntered.Task;

        resolver.PublishCurated(new Dictionary<string, string> { ["遅延する"] = "远程翻译" });
        releaseEnqueue.Set();
        await observation;

        await machine.StopAsync();
        Assert.False(accepted);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Failed_work_remains_pending_and_periodic_scan_retries_it()
    {
        var cache = new TranslationCache(root);
        cache.ObserveNormal("回復する", "回復する");
        var attempts = 0;
        var machine = new MachineTranslator(
            cache,
            (_, _) => Task.FromResult<string?>(++attempts == 1 ? null : "进行恢复"),
            1,
            TimeSpan.FromMilliseconds(10),
            retryPolicy: new TranslationRetryPolicy(
                3,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20)));

        machine.Start();
        await WaitUntilAsync(() => cache.TryGetGenerated("回復する", out _));
        await machine.StopAsync();

        Assert.True(attempts >= 2);
        Assert.Empty(cache.PendingSnapshot());
    }

    [Fact]
    public async Task Process_blocked_template_is_not_scanned_or_enqueued_until_a_new_policy_exists()
    {
        var cache = new TranslationCache(root);
        cache.ObserveNormal("封鎖する", "封鎖する");
        var sharedPolicy = new TranslationRetryPolicy(
            1,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));
        var firstAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempts = 0;
        var first = new MachineTranslator(
            cache,
            (_, _) =>
            {
                Interlocked.Increment(ref firstAttempts);
                firstAttempted.TrySetResult();
                return Task.FromResult<string?>(null);
            },
            1,
            TimeSpan.FromMilliseconds(10),
            retryPolicy: sharedPolicy);

        first.Start();
        await firstAttempted.Task;
        await WaitUntilAsync(() => sharedPolicy.BlockedCount == 1);
        Assert.False(first.EnqueueNormal("封鎖する"));
        Assert.False(first.EnqueuePriority("封鎖する"));
        await Task.Delay(50);
        await first.StopAsync();
        Assert.Equal(1, firstAttempts);
        Assert.Equal(new[] { "封鎖する" }, cache.PendingSnapshot());

        var replacementAttempts = 0;
        var replacement = new MachineTranslator(
            cache,
            (_, _) =>
            {
                Interlocked.Increment(ref replacementAttempts);
                return Task.FromResult<string?>("不应调用");
            },
            1,
            TimeSpan.FromMilliseconds(10),
            retryPolicy: sharedPolicy);
        replacement.Start();
        await Task.Delay(50);
        await replacement.StopAsync();
        Assert.Equal(0, replacementAttempts);

        var nextProcess = new MachineTranslator(
            cache,
            (_, _) => Task.FromResult<string?>("解除封锁"),
            1,
            TimeSpan.FromMilliseconds(10),
            retryPolicy: new TranslationRetryPolicy(
                1,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10)));
        nextProcess.Start();
        await WaitUntilAsync(() => cache.TryGetGenerated("封鎖する", out _));
        await nextProcess.StopAsync();

        Assert.True(cache.TryGetGenerated("封鎖する", out var translated));
        Assert.Equal("解除封锁", translated);
    }

    [Fact]
    public async Task Production_default_blocks_after_exactly_three_failed_work_cycles()
    {
        var cache = new TranslationCache(root);
        cache.ObserveNormal("三回失敗する", "三回失敗する");
        var retryPolicy = new TranslationRetryPolicy(
            initialDelay: TimeSpan.FromMilliseconds(10),
            maximumDelay: TimeSpan.FromMilliseconds(10));
        var attempts = 0;
        var machine = new MachineTranslator(
            cache,
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult<string?>(null);
            },
            1,
            TimeSpan.FromMilliseconds(5),
            retryPolicy: retryPolicy);

        machine.Start();
        await WaitUntilAsync(() => retryPolicy.BlockedCount == 1);
        await Task.Delay(30);
        await machine.StopAsync();

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { "三回失敗する" }, cache.PendingSnapshot());
    }

    [Fact]
    public async Task Stop_cancels_inflight_translation_without_throwing()
    {
        var cache = new TranslationCache(root);
        cache.ObservePriority("停止する", "停止する");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new MachineTranslator(cache, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }, 1, TimeSpan.FromSeconds(1));
        machine.Start();
        await started.Task;

        await machine.StopAsync();
        Assert.Equal(new[] { "停止する" }, cache.PendingSnapshot());
    }

    [Fact]
    public async Task Shutdown_cancellation_does_not_count_or_block_and_priority_survives_restart()
    {
        var cache = new TranslationCache(root);
        cache.ObservePriority("再開する", "再開する");
        var backlog = new TranslationPriorityBacklog();
        var retryPolicy = new TranslationRetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>();
        var machine = new MachineTranslator(
            cache,
            async (_, token) =>
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                started.TrySetResult();
                return await release.Task;
            },
            1,
            TimeSpan.FromSeconds(1),
            backlog,
            retryPolicy: retryPolicy);
        Assert.True(machine.EnqueuePriority("再開する"));
        machine.Start();
        await started.Task;

        var stopping = machine.StopAsync();
        await canceled.Task;
        release.SetResult(null);
        await stopping;

        Assert.Equal(0, retryPolicy.BlockedCount);
        var replacement = new MachineTranslator(
            cache,
            (_, _) => Task.FromResult<string?>("重新开始"),
            1,
            TimeSpan.FromSeconds(1),
            backlog,
            retryPolicy: retryPolicy);
        replacement.Start();
        await WaitUntilAsync(() => cache.TryGetGenerated("再開する", out _));
        await replacement.StopAsync();

        Assert.True(cache.TryGetGenerated("再開する", out var translated));
        Assert.Equal("重新开始", translated);
    }

    [Fact]
    public async Task Status_loop_reports_counts_only()
    {
        var cache = new TranslationCache(root);
        var diagnostics = new List<string>();
        var machine = new MachineTranslator(
            cache,
            (_, _) => Task.FromResult<string?>(null),
            1,
            TimeSpan.FromSeconds(1),
            diagnostic: diagnostics.Add,
            statusPeriod: TimeSpan.FromMilliseconds(20));

        machine.Start();
        await WaitUntilAsync(() => diagnostics.Count > 0);
        await machine.StopAsync();

        Assert.Contains("pending=0", diagnostics[0]);
        Assert.Contains("completed=0", diagnostics[0]);
        Assert.Contains("in-flight=0", diagnostics[0]);
        Assert.Contains("failed=0", diagnostics[0]);
    }

    [Fact]
    public async Task Cancelled_worker_does_not_publish_a_late_translation()
    {
        var cache = new TranslationCache(root);
        cache.ObservePriority("遅延する", "遅延する");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new MachineTranslator(cache, (_, _) =>
        {
            started.SetResult();
            return release.Task;
        }, 1, TimeSpan.FromSeconds(1));
        machine.Start();
        await started.Task;

        await machine.StopAsync(TimeSpan.FromMilliseconds(10));
        release.SetResult("延迟");
        await Task.Delay(20);

        Assert.False(cache.TryGetGenerated("遅延する", out _));
        Assert.Equal(new[] { "遅延する" }, cache.PendingSnapshot());
    }

    [Fact]
    public async Task Source_identical_worker_output_remains_pending()
    {
        var cache = new TranslationCache(root);
        cache.ObservePriority("同じする", "同じする");
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new MachineTranslator(cache, (text, _) =>
        {
            attempted.TrySetResult();
            return Task.FromResult<string?>(text);
        }, 1, TimeSpan.FromSeconds(1));

        machine.Start();
        await attempted.Task;
        await Task.Delay(20);
        await machine.StopAsync();

        Assert.False(cache.TryGetGenerated("同じする", out _));
        Assert.Equal(new[] { "同じする" }, cache.PendingSnapshot());
    }

    [Fact]
    public async Task Cache_freeze_rejects_worker_publication_racing_shutdown()
    {
        var cache = new TranslationCache(root);
        cache.ObservePriority("凍結する", "凍結する");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new MachineTranslator(cache, (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        }, 1, TimeSpan.FromSeconds(1));
        machine.Start();
        await started.Task;

        Assert.True(cache.StoreGenerated("保存する", "保存"));
        cache.FreezeMutations();
        release.SetResult("冻结");
        await WaitUntilAsync(() => machine.ProgressSnapshot.Completed + machine.ProgressSnapshot.Failed > 0);
        Assert.Equal(1, machine.ProgressSnapshot.Failed);
        machine.Stop();
        cache.Flush();

        var loaded = new TranslationCache(root);
        loaded.Load();
        Assert.False(loaded.TryGetGenerated("凍結する", out _));
        Assert.True(loaded.TryGetGenerated("保存する", out var accepted));
        Assert.Equal("保存", accepted);
        Assert.Equal(new[] { "凍結する" }, loaded.PendingSnapshot());
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
