using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Xunit;

namespace MuvluvLLMMod.IntegrationTests;

public sealed class ProductionCoordinationIntegrationTests
{
    [Fact]
    public void Real_plugin_registers_and_removes_the_same_native_quit_delegate()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        Assert.Equal(1, Application.AddCalls);
        Assert.Equal(1, Application.CallbackCount);

        // The fake event invokes the exact retained production handler, not a test cleanup
        // shortcut; that handler must run the same idempotent cleanup path.
        Application.InvokeQuitting();
        Assert.Equal(0, Application.CallbackCount);
        Assert.Null(Plugin.Instance);

        Assert.True(fixture.Plugin.Unload());
        Assert.Equal(1, Application.RemoveCalls);
        Assert.Equal(0, Application.CallbackCount);
        Assert.False(Plugin.IsCleaningUp);
    }

    [Fact]
    public async Task Native_registration_coordinator_blocks_forced_remove_register_interleaving()
    {
        Harmony.Reset();
        Application.Reset();
        var coordinator = new NativeDelegateCoordinator<Il2CppSystem.Action>();
        var first = new Il2CppSystem.Action(static () => { });
        var removeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(coordinator.TryRegister(1, () => first, Application.add_quitting));
        var remove = Task.Run(() => coordinator.TryRemove(1, candidate =>
        {
            removeEntered.TrySetResult();
            releaseRemove.Task.GetAwaiter().GetResult();
            Application.remove_quitting(candidate);
            return true;
        }));
        await removeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = Task.Run(() => coordinator.TryRegister(
            2,
            static () => new Il2CppSystem.Action(static () => { }),
            Application.add_quitting));
        await Task.Delay(20);
        Assert.False(replacement.IsCompleted);
        // The native callback is still registered while removal is in progress, but no
        // replacement can publish or add a second callback before the coordinator lock opens.
        Assert.Equal(1, Application.CallbackCount);

        releaseRemove.TrySetResult();
        Assert.True(await remove.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await replacement.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(NativeDelegateRegistrationState.Registered, coordinator.State);
        Assert.NotSame(first, coordinator.Current);
        Assert.Equal(1, Application.CallbackCount);
        Assert.Same(coordinator.Current, Assert.Single(Application.SnapshotHandlers()));

        Assert.True(coordinator.TryRemove(2, candidate =>
        {
            Application.remove_quitting(candidate);
            return true;
        }));
        Assert.Equal(0, Application.CallbackCount);
    }

    [Fact]
    public async Task Cleanup_during_loading_stops_late_stages_and_rolls_back_owned_resources()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad(out var generation));
        var owner = new FakeGenerationResources();
        Assert.True(generation.AttachOwner(owner));
        using var loadStage = generation.TryEnterStage();
        Assert.NotNull(loadStage);

        var cleanup = Task.Run(() => gate.Cleanup(_ =>
        {
            owner.CleanupCalls++;
            owner.ResourcesStopped = true;
            return true;
        }));
        await WaitUntilAsync(() => gate.State == PluginLifecycleState.Stopping);

        Assert.True(generation.IsCancellationRequested);
        var lateStage = generation.TryEnterStage();
        owner.LateResourcePublished = lateStage != null;
        lateStage?.Dispose();
        Assert.False(owner.LateResourcePublished);
        Assert.Equal(0, owner.CleanupCalls);

        loadStage!.Dispose();
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(owner.ResourcesStopped);
        Assert.Equal(1, owner.CleanupCalls);
        Assert.Equal(PluginLifecycleState.Stopped, gate.State);
    }

    [Theory]
    [InlineData("configuration initialization")]
    [InlineData("Harmony patches")]
    [InlineData("injected Hotkey component")]
    [InlineData("application-quit delegate")]
    [InlineData("cache persistence task")]
    [InlineData("machine worker startup")]
    public async Task Every_late_stage_resource_rolls_back_without_publishing(
        string resourceName)
    {
        var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(40));
        Assert.True(gate.TryBeginLoad(out var generation));
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rollbackCalls = 0;
        var published = false;

        var stageTask = Task.Run(() =>
        {
            using var stage = generation.TryEnterStage();
            Assert.NotNull(stage);
            using var resource = stage!.RegisterResource(
                resourceName,
                () =>
                {
                    Interlocked.Increment(ref rollbackCalls);
                    published = false;
                });
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            Assert.False(resource.Commit(() => published = true));
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cleanup = Task.Run(() => gate.Cleanup(_ => true));
        Assert.Same(cleanup, await Task.WhenAny(cleanup, Task.Delay(500)));
        Assert.False(await cleanup);
        Assert.False(published);
        Assert.Null(generation.TryEnterStage());

        // The stage is allowed to finish after the cleanup response. Its local resource is still
        // retained by the generation and must be rolled back at the post-side-effect commit.
        release.TrySetResult(true);
        await stageTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, rollbackCalls);
        Assert.False(published);
        Assert.Equal(0, generation.OutstandingResourceCount);

        // The failed generation remains quarantined and a repeated cleanup cannot reopen it.
        Assert.False(gate.Cleanup(_ => true));
        Assert.False(gate.TryBeginLoad(out _));
    }

    [Fact]
    public async Task Late_persistence_resource_cancels_the_task_after_cleanup_timeout()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MuvluvLLMMod.late-persistence." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            var cancellation = new CancellationTokenSource();
            var persistenceTask = Task.CompletedTask;
            var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(40));
            Assert.True(gate.TryBeginLoad(out var generation));
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var stageTask = Task.Run(() =>
            {
                using var stage = generation.TryEnterStage();
                Assert.NotNull(stage);
                using var resource = stage!.RegisterResource(
                    "cache persistence task",
                    () =>
                    {
                        cancellation.Cancel();
                        persistenceTask.GetAwaiter().GetResult();
                        cancellation.Dispose();
                    });
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                persistenceTask = cache.RunPersistenceLoopAsync(cancellation.Token);
                Assert.False(resource.Commit());
            });

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var cleanup = Task.Run(() => gate.Cleanup(_ => true));
            await WaitUntilAsync(() => gate.State == PluginLifecycleState.Stopping);
            Assert.False(await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));

            release.TrySetResult(true);
            await stageTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(persistenceTask.IsCompleted);
            Assert.Equal(0, generation.OutstandingResourceCount);
            Assert.False(gate.Cleanup(_ => true));
            Assert.False(gate.TryBeginLoad(out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Late_machine_resource_stops_the_worker_after_cleanup_timeout()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MuvluvLLMMod.late-machine." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            var lifecycle = new MachineTranslatorLifecycle(shutdownTimeout: TimeSpan.FromSeconds(1));
            var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(40));
            Assert.True(gate.TryBeginLoad(out var generation));
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var stageTask = Task.Run(() =>
            {
                using var stage = generation.TryEnterStage();
                Assert.NotNull(stage);
                using var resource = stage!.RegisterResource(
                    "machine worker startup",
                    lifecycle.Shutdown);
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                Assert.True(lifecycle.Initialize(
                    true,
                    1,
                    (limiter, backlog) => new MachineTranslator(
                        cache,
                        static (_, _) => Task.FromResult<string?>("worker"),
                        1,
                        TimeSpan.FromSeconds(1),
                        backlog),
                    new TranslationRetryPolicy()));
                Assert.False(resource.Commit());
            });

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var cleanup = Task.Run(() => gate.Cleanup(_ => true));
            await WaitUntilAsync(() => gate.State == PluginLifecycleState.Stopping);
            Assert.False(await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));

            release.TrySetResult(true);
            await stageTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(lifecycle.IsShutdown);
            Assert.Equal(0, generation.OutstandingResourceCount);
            Assert.False(gate.Cleanup(_ => true));
            Assert.False(gate.TryBeginLoad(out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Timed_out_machine_resource_settles_after_eventual_worker_stop_without_reopening_generation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MuvluvLLMMod.machine-settlement." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        MachineTranslator? worker = null;
        try
        {
            var cache = new TranslationCache(root);
            const string template = "最终停止する";
            Assert.True(cache.ObservePriority(template, template));
            var lifecycle = new MachineTranslatorLifecycle(
                shutdownTimeout: TimeSpan.FromMilliseconds(40));
            var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(100));
            Assert.True(gate.TryBeginLoad(out var generation));
            Assert.True(generation.AttachOwner(new object()));

            PluginLifecycleGate.PluginGenerationResource resource;
            using (var stage = generation.TryEnterStage())
            {
                Assert.NotNull(stage);
                resource = stage!.RegisterResource(
                    "machine worker startup",
                    lifecycle.ShutdownForResourceRollback);
                Assert.True(lifecycle.Initialize(
                    true,
                    1,
                    (limiter, backlog) => worker = new MachineTranslator(
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
                Assert.True(resource.Commit());
            }

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gate.Cleanup(_ => true));
            Assert.Null(generation.GetOwner<object>());
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
            Assert.Equal(1, lifecycle.TrackedStoppingWorkerCount);
            Assert.Equal(1, generation.OutstandingResourceCount);
            var originalFailure = gate.Failure;
            Assert.NotNull(originalFailure);

            release.TrySetResult("晚到翻译");
            await worker!.StopCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => lifecycle.TrackedStoppingWorkerCount == 0);
            Assert.Equal(0, lifecycle.TrackedStoppingWorkerCount);

            // Only the retained machine reservation is retried. The original failed generation
            // remains quarantined even though the machine callback can now settle.
            Assert.False(gate.Cleanup(_ => true));
            Assert.Equal(0, generation.OutstandingResourceCount);
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
            Assert.Same(originalFailure, gate.Failure);
            Assert.False(gate.TryBeginLoad(out _));
        }
        finally
        {
            release.TrySetResult(null);
            if (worker != null)
            {
                try { await worker.StopCompletion.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Late_resource_rollback_failure_is_quarantined_and_retryable()
    {
        var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(40));
        Assert.True(gate.TryBeginLoad(out var generation));
        using var stage = generation.TryEnterStage();
        Assert.NotNull(stage);
        var rollbackCalls = 0;
        var resource = stage!.RegisterResource(
            "late rollback failure",
            () =>
            {
                if (Interlocked.Increment(ref rollbackCalls) == 1)
                    throw new InvalidOperationException("simulated late rollback failure");
            });
        var cleanup = Task.Run(() => gate.Cleanup(_ => true));
        await WaitUntilAsync(() => gate.State == PluginLifecycleState.Stopping);
        Assert.False(await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.False(resource.Commit());
        Assert.True(generation.OutstandingResourceCount > 0);
        Assert.NotNull(gate.Failure);
        Assert.False(gate.Cleanup(_ => true));
        Assert.Equal(2, rollbackCalls);
        Assert.Equal(0, generation.OutstandingResourceCount);
        Assert.False(gate.TryBeginLoad(out _));
    }

    [Fact]
    public async Task Cleanup_stage_wait_deadline_quarantines_and_continues_later_teardown()
    {
        var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(40));
        Assert.True(gate.TryBeginLoad(out var generation));
        using var blockedStage = generation.TryEnterStage();
        Assert.NotNull(blockedStage);
        var later = new List<string>();

        var first = Task.Run(() => gate.Cleanup(_ =>
        {
            later.Add("freeze");
            later.Add("flush");
            later.Add("unpatch");
            return true;
        }));
        await WaitUntilAsync(() => gate.State == PluginLifecycleState.Stopping);
        var second = Task.Run(() => gate.Cleanup(_ => true));

        await AssertQuarantinedWithinDeadline(
            first,
            blockedStage.Dispose,
            "load-stage cleanup exceeded its bounded quiescence deadline");
        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { "freeze", "flush", "unpatch" }, later);
        Assert.Equal(PluginLifecycleState.Failed, gate.State);

        // The admitted stage may finish late, but the failed generation cannot publish or reopen.
        blockedStage!.Dispose();
        Assert.False(gate.TryPublishRunning(generation));
        Assert.False(generation.TryEnterStage() is not null);
        Assert.False(gate.TryBeginLoad(out _));
    }

    [Fact]
    public async Task Cleanup_callback_wait_deadline_has_shared_failure_and_late_callback_is_inert()
    {
        var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(40));
        Assert.True(gate.TryBeginLoad(out var generation));
        Assert.True(generation.AttachOwner(new object()));
        Assert.True(gate.TryPublishRunning(generation));
        var lease = generation.TryAcquireRunningLease();
        Assert.NotNull(lease);
        Assert.True(lease!.TryEnter(out var blockedCallback));
        var later = new List<string>();

        var first = Task.Run(() => gate.Cleanup(_ =>
        {
            later.Add("flush");
            later.Add("unpatch");
            return true;
        }));
        await WaitUntilAsync(() => gate.State == PluginLifecycleState.Stopping);
        var second = Task.Run(() => gate.Cleanup(_ => true));

        await AssertQuarantinedWithinDeadline(
            first,
            blockedCallback!.Dispose,
            "configuration-callback cleanup exceeded its bounded quiescence deadline");
        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { "flush", "unpatch" }, later);
        Assert.Equal(PluginLifecycleState.Failed, gate.State);
        Assert.False(lease.IsActive);
        Assert.False(lease.TryEnter(out _));

        // Releasing the late callback only drains its retained lease; it cannot revive the
        // generation or obtain a resource stage after quarantine.
        blockedCallback!.Dispose();
        lease.Dispose();
        Assert.False(generation.TryEnterRunningStage() is not null);
        Assert.False(gate.TryBeginLoad(out _));
    }

    [Fact]
    public void Stale_config_event_snapshot_cannot_reload_a_new_generation()
    {
        var configFile = new ConfigFile();
        Config.Initialize(configFile);
        var oldEndpoint = Config.LlmEndpoint;
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad(out var oldGeneration));
        Assert.True(oldGeneration.AttachOwner(new FakeGenerationResources()));
        Assert.True(gate.TryPublishRunning(oldGeneration));
        var oldLease = oldGeneration.TryAcquireRunningLease();
        Assert.NotNull(oldLease);

        var reloadCalls = 0;
        Assert.True(Config.Activate(oldLease!, _ => Interlocked.Increment(ref reloadCalls)));
        var staleHandlers = configFile.CaptureSettingHandlers();
        oldEndpoint.Value = "http://127.0.0.1:11435/v1/chat/completions";
        Assert.Equal(1, reloadCalls);

        Assert.True(gate.Cleanup(_ =>
        {
            Config.Shutdown();
            return true;
        }));
        Assert.True(gate.TryBeginLoad(out var newGeneration));
        Assert.True(newGeneration.AttachOwner(new FakeGenerationResources()));
        Assert.True(gate.TryPublishRunning(newGeneration));

        foreach (var handler in staleHandlers)
            handler(configFile, new SettingChangedEventArgs(oldEndpoint));

        Assert.Equal(1, reloadCalls);
        Config.Shutdown();
        Assert.True(gate.Cleanup(_ => true));
    }

    [Fact]
    public void Duplicate_machine_initialize_is_rejected_without_replacing_the_owned_worker()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.lifecycle." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            var lifecycle = new MachineTranslatorLifecycle(shutdownTimeout: TimeSpan.FromSeconds(1));
            var factoryCalls = 0;
            Assert.True(lifecycle.Initialize(
                true,
                1,
                (limiter, backlog) =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return new MachineTranslator(
                        cache,
                        static (_, _) => Task.FromResult<string?>("翻译"),
                        1,
                        TimeSpan.FromMilliseconds(10),
                        backlog);
                },
                new TranslationRetryPolicy()));

            Assert.False(lifecycle.Initialize(
                true,
                1,
                (_, backlog) => new MachineTranslator(
                    cache,
                    static (_, _) => Task.FromResult<string?>("错误替换"),
                    1,
                    TimeSpan.FromMilliseconds(10),
                    backlog),
                new TranslationRetryPolicy()));
            Assert.Equal(1, factoryCalls);
            lifecycle.Shutdown();
            Assert.True(lifecycle.IsShutdown);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Failed_cleanup_quarantines_the_generation_and_blocks_reopen()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad());
        var laterSteps = new List<string>();

        Assert.False(gate.Cleanup(new[]
        {
            new PluginCleanupStep("quit remove", () => throw new InvalidOperationException("native remove failed")),
            new PluginCleanupStep("flush", () => laterSteps.Add("flush")),
            new PluginCleanupStep("unpatch", () => laterSteps.Add("unpatch"))
        }));

        Assert.Equal(new[] { "flush", "unpatch" }, laterSteps);
        Assert.Equal(PluginLifecycleState.Failed, gate.State);
        Assert.False(gate.TryBeginLoad(out _));
    }

    private static async Task AssertQuarantinedWithinDeadline(
        Task<bool> cleanup,
        Action release,
        string message)
    {
        var completed = await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromMilliseconds(500)));
        if (!ReferenceEquals(completed, cleanup))
        {
            // Release the test boundary so a deliberately unbounded mutant cannot leave a
            // background cleanup task holding the process open after this assertion.
            release();
            _ = await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Fail(message);
        }

        Assert.False(await cleanup);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class FakeGenerationResources
    {
        public int CleanupCalls;
        public bool LateResourcePublished;
        public bool ResourcesStopped;
    }
}
