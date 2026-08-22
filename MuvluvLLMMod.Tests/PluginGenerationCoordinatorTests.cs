using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class PluginGenerationCoordinatorTests
{
    private sealed class Owner
    {
        public int CleanupCalls;
        public int LateStageCalls;
    }

    [Fact]
    public async Task Cleanup_during_loading_waits_the_stage_and_rejects_later_publication()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad(out var generation));
        var owner = new Owner();
        Assert.True(generation.AttachOwner(owner));
        using var stage = generation.TryEnterStage();
        Assert.NotNull(stage);

        var cleanup = Task.Run(() => gate.Cleanup(_ =>
        {
            Interlocked.Increment(ref owner.CleanupCalls);
            return true;
        }));
        await WaitUntilAsync(() => gate.State == PluginLifecycleState.Stopping);

        Assert.True(generation.IsCancellationRequested);
        Assert.False(cleanup.IsCompleted);
        Assert.Null(generation.TryEnterStage());
        Interlocked.Increment(ref owner.LateStageCalls);
        Assert.Equal(0, owner.CleanupCalls);

        stage!.Dispose();
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, owner.CleanupCalls);
        Assert.Equal(PluginLifecycleState.Stopped, gate.State);
        Assert.False(generation.IsRunning);
    }

    [Fact]
    public async Task Concurrent_cleanup_callers_receive_the_same_final_result()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad(out _));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<bool> Cleanup() => Task.Run(() => gate.Cleanup(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return true;
        }));

        var first = Cleanup();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = Cleanup();
        await Task.Delay(20);
        Assert.False(second.IsCompleted);

        release.TrySetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, calls);
        Assert.Equal(PluginLifecycleState.Stopped, gate.State);
    }

    [Fact]
    public void Failed_cleanup_quarantines_the_generation_and_blocks_a_new_load()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad(out _));

        Assert.False(gate.Cleanup(_ => false));
        Assert.Equal(PluginLifecycleState.Failed, gate.State);
        Assert.False(gate.TryBeginLoad(out _));
        Assert.False(gate.Cleanup(_ => true));
    }

    [Fact]
    public async Task A_stale_config_lease_cannot_enter_or_obtain_the_new_generation_owner()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad(out var oldGeneration));
        var oldOwner = new Owner();
        Assert.True(oldGeneration.AttachOwner(oldOwner));
        Assert.True(gate.TryPublishRunning(oldGeneration));
        var lease = oldGeneration.TryAcquireRunningLease();
        Assert.NotNull(lease);
        Assert.True(lease!.TryEnter(out var callback));

        var cleanup = Task.Run(() => gate.Cleanup(_ => true));
        await WaitUntilAsync(() => gate.State == PluginLifecycleState.Stopping);
        Assert.False(lease.IsActive);
        Assert.False(lease.TryEnter(out _));
        callback!.Dispose();
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));
        lease.Dispose();

        Assert.True(gate.TryBeginLoad(out var newGeneration));
        var newOwner = new Owner();
        Assert.True(newGeneration.AttachOwner(newOwner));
        Assert.True(gate.TryPublishRunning(newGeneration));
        Assert.Null(lease.GetOwner<Owner>());
        Assert.Same(newOwner, gate.CurrentOwner<Owner>());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
