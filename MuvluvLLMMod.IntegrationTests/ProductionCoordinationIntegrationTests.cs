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

        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
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

        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
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
