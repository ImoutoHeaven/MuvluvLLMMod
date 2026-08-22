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
}
