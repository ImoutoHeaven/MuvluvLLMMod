using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class NativeDelegateCoordinatorTests
{
    private sealed class DelegateToken
    {
    }

    [Fact]
    public async Task Remove_and_new_register_are_atomic_and_failed_remove_retains_exact_delegate()
    {
        var coordinator = new NativeDelegateCoordinator<DelegateToken>();
        var original = new DelegateToken();
        var addCalls = 0;
        DelegateToken? removed = null;
        var removeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(coordinator.TryRegister(
            1,
            () => original,
            _ => Interlocked.Increment(ref addCalls)));

        var remove = Task.Run(() => coordinator.TryRemove(1, candidate =>
        {
            removed = candidate;
            removeEntered.TrySetResult();
            releaseRemove.Task.GetAwaiter().GetResult();
            return false;
        }));
        await removeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = Task.Run(() => coordinator.TryRegister(
            2,
            static () => new DelegateToken(),
            _ => Interlocked.Increment(ref addCalls)));
        await Task.Delay(20);
        Assert.False(replacement.IsCompleted);

        releaseRemove.TrySetResult();
        Assert.False(await remove.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await replacement.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(original, removed);
        Assert.Same(original, coordinator.Current);
        Assert.Equal(NativeDelegateRegistrationState.Failed, coordinator.State);
        Assert.Equal(1, addCalls);

        Assert.True(coordinator.RetryRemove(1, candidate => ReferenceEquals(candidate, original)));
        Assert.Equal(NativeDelegateRegistrationState.Unregistered, coordinator.State);
        Assert.True(coordinator.TryRegister(2, static () => new DelegateToken(), _ => addCalls++));
        Assert.Equal(2, addCalls);
    }

    [Fact]
    public void Native_add_failure_retains_identity_and_cleanup_can_quarantine_the_generation()
    {
        var coordinator = new NativeDelegateCoordinator<DelegateToken>();
        var original = new DelegateToken();
        Assert.Throws<InvalidOperationException>(() => coordinator.TryRegister(
            7,
            () => original,
            _ => throw new InvalidOperationException("add failed")));

        Assert.Equal(NativeDelegateRegistrationState.Failed, coordinator.State);
        Assert.Same(original, coordinator.Current);
        Assert.False(coordinator.TryRegister(8, static () => new DelegateToken(), _ => { }));

        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad(out _));
        Assert.False(gate.Cleanup(_ => coordinator.TryRemove(7, _ => false)));
        Assert.Equal(PluginLifecycleState.Failed, gate.State);
        Assert.False(gate.TryBeginLoad(out _));
    }
}
