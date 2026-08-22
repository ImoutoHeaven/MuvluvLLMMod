using Xunit;

namespace MuvluvLLMMod.IntegrationTests;

public sealed class ProductionBudgetAndShutdownIntegrationTests
{
    [Fact]
    public void Oversized_render_input_is_rejected_fail_closed_and_not_retained()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var source = new string('あ', TranslationBudget.MaxTextUtf16CodeUnits + 1);
        var label = new TMPro.TMP_Text();

        label.text = source;

        Assert.Equal(source, label.text);
        Assert.Empty(Plugin.CurrentCache!.PendingSnapshot());
        Assert.Equal(0, Plugin.CurrentCache.RetainedSnapshot.PendingCount);
    }

    [Fact]
    public void Terminal_flush_recovers_from_one_transient_write_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.flush." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var writes = 0;
            var cache = new TranslationCache(
                root,
                writeAtomic: (_, _) =>
                {
                    writes++;
                    return writes != 1;
                });
            Assert.True(cache.ObserveNormal("一つを確認する", "一つを確認する"));

            Assert.True(cache.FlushTerminal(TimeSpan.FromSeconds(1)));
            // The first state write fails; the second attempt writes state plus three legacy
            // mirrors. The exact production writer therefore needs five calls.
            Assert.Equal(5, writes);
            Assert.True(cache.Flush());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Persistent_terminal_flush_failure_is_a_quarantined_cleanup_result()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.flush-fail." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root, writeAtomic: static (_, _) => false);
            Assert.True(cache.ObserveNormal("一つを確認する", "一つを確認する"));
            var gate = new PluginLifecycleGate();
            Assert.True(gate.TryBeginLoad());
            var later = new List<string>();

            Assert.False(gate.Cleanup(new[]
            {
                new PluginCleanupStep("freeze", cache.FreezeMutations),
                new PluginCleanupStep("terminal flush", () =>
                {
                    if (!cache.FlushTerminal(TimeSpan.FromMilliseconds(10)))
                        throw new InvalidOperationException("terminal persistence failed");
                }),
                new PluginCleanupStep("unpatch", () => later.Add("unpatch"))
            }));

            Assert.Equal(new[] { "unpatch" }, later);
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
            Assert.False(gate.TryBeginLoad(out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Timed_out_reload_is_terminal_and_retains_the_old_worker_for_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.reload-fault." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        MachineTranslator? workerA = null;
        var created = 0;
        var laterSteps = new List<string>();
        var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(100));
        MachineTranslatorLifecycle? lifecycle = null;
        Assert.True(gate.TryBeginLoad(out var generation));
        try
        {
            var cache = new TranslationCache(root);
            lifecycle = new MachineTranslatorLifecycle(
                shutdownTimeout: TimeSpan.FromMilliseconds(40),
                terminalFailure: exception => gate.RecordFailure(generation, exception));

            MachineTranslator Factory(RequestRateLimiter _, TranslationPriorityBacklog backlog)
            {
                var number = Interlocked.Increment(ref created);
                var machine = new MachineTranslator(
                    cache,
                    async (_, _) =>
                    {
                        entered.TrySetResult();
                        return await release.Task.ConfigureAwait(false);
                    },
                    1,
                    TimeSpan.FromMilliseconds(10),
                    backlog);
                if (number == 1)
                    workerA = machine;
                return machine;
            }

            Assert.True(lifecycle.Initialize(true, 1, Factory));
            Assert.True(lifecycle.EnqueuePriority("重新加载する"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(lifecycle.Reload(true, 1, Factory));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => lifecycle.TransitionTask.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.True(lifecycle.IsFaulted);
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
            Assert.False(lifecycle.Reload(true, 1, Factory));
            Assert.False(lifecycle.EnqueuePriority("第二次重新加载する"));
            Assert.Equal(1, created);
            Assert.Equal(1, lifecycle.TrackedStoppingWorkerCount);

            Assert.False(gate.Cleanup(_ =>
            {
                var machineFailed = false;
                try
                {
                    lifecycle.Shutdown();
                }
                catch (InvalidOperationException)
                {
                    machineFailed = true;
                }
                laterSteps.Add("flush");
                laterSteps.Add("unpatch");
                return !machineFailed;
            }));

            Assert.Equal(new[] { "flush", "unpatch" }, laterSteps);
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
            Assert.False(gate.TryBeginLoad(out _));
        }
        finally
        {
            release.TrySetResult(null);
            if (workerA != null && lifecycle != null)
            {
                await workerA.StopCompletion.WaitAsync(TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => lifecycle.TrackedStoppingWorkerCount == 0);
                Assert.Equal(0, lifecycle.TrackedStoppingWorkerCount);
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Noncooperative_machine_shutdown_times_out_and_later_cleanup_steps_still_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.timeout." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var cache = new TranslationCache(root);
            var template = "一つを確認する";
            Assert.True(cache.ObserveNormal(template, template));
            var lifecycle = new MachineTranslatorLifecycle(
                shutdownTimeout: TimeSpan.FromMilliseconds(60));
            Assert.True(lifecycle.Initialize(
                true,
                1,
                (_, backlog) => new MachineTranslator(
                    cache,
                    async (_, _) =>
                    {
                        entered.TrySetResult();
                        return await release.Task.ConfigureAwait(false);
                    },
                    1,
                    TimeSpan.FromMilliseconds(10),
                    backlog),
                new TranslationRetryPolicy()));
            // Start schedules the durable pending item itself; a duplicate enqueue may be
            // correctly rejected because the work is already owned by the queue.
            _ = lifecycle.EnqueueNormal(template, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var gate = new PluginLifecycleGate();
            Assert.True(gate.TryBeginLoad());
            var later = new List<string>();
            var started = DateTime.UtcNow;
            var succeeded = gate.Cleanup(new[]
            {
                new PluginCleanupStep("machine shutdown", lifecycle.Shutdown),
                new PluginCleanupStep("terminal flush", () => later.Add("flush")),
                new PluginCleanupStep("unpatch", () => later.Add("unpatch"))
            });
            var elapsed = DateTime.UtcNow - started;

            Assert.False(succeeded);
            Assert.True(lifecycle.ShutdownTimedOut);
            Assert.Equal(new[] { "flush", "unpatch" }, later);
            Assert.True(elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
        }
        finally
        {
            release.TrySetResult("翻译");
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
