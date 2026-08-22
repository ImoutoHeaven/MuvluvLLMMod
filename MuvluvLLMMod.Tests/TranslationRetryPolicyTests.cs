using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TranslationRetryPolicyTests
{
    [Fact]
    public void Failures_back_off_exponentially_and_block_at_the_process_limit()
    {
        var nowTicks = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        DateTimeOffset UtcNow() => new(Interlocked.Read(ref nowTicks), TimeSpan.Zero);
        var policy = new TranslationRetryPolicy(
            3,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10),
            UtcNow);

        var first = policy.RecordFailure("失敗する");
        Assert.Equal(1, first.Failures);
        Assert.Equal(TimeSpan.FromSeconds(2), first.Delay);
        Assert.False(first.Blocked);
        Assert.False(policy.CanAttempt("失敗する"));

        Interlocked.Add(ref nowTicks, TimeSpan.FromSeconds(2).Ticks);
        Assert.True(policy.CanAttempt("失敗する"));
        var second = policy.RecordFailure("失敗する");
        Assert.Equal(2, second.Failures);
        Assert.Equal(TimeSpan.FromSeconds(4), second.Delay);
        Assert.False(second.Blocked);

        Interlocked.Add(ref nowTicks, TimeSpan.FromSeconds(4).Ticks);
        var third = policy.RecordFailure("失敗する");
        Assert.Equal(3, third.Failures);
        Assert.Equal(TimeSpan.Zero, third.Delay);
        Assert.True(third.Blocked);
        Assert.Equal(1, policy.BlockedCount);

        Interlocked.Add(ref nowTicks, TimeSpan.FromDays(365).Ticks);
        Assert.False(policy.CanAttempt("失敗する"));
        Assert.True(new TranslationRetryPolicy(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)).CanAttempt("失敗する"));
    }

    [Fact]
    public void Success_clears_a_non_blocked_failure_state()
    {
        var policy = new TranslationRetryPolicy(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        policy.RecordFailure("回復する");

        policy.RecordSuccess("回復する");

        Assert.True(policy.CanAttempt("回復する"));
        Assert.Equal(0, policy.BlockedCount);
    }

    [Fact]
    public async Task Scheduling_is_atomic_with_failure_recording()
    {
        var policy = new TranslationRetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var scheduleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSchedule = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduled = Task.Run(() => policy.TrySchedule("競合する", () =>
        {
            scheduleEntered.TrySetResult();
            releaseSchedule.Task.GetAwaiter().GetResult();
            return true;
        }));
        await scheduleEntered.Task;

        var failure = Task.Run(() => policy.RecordFailure("競合する"));
        Assert.False(failure.IsCompleted);
        releaseSchedule.TrySetResult();

        Assert.True(await scheduled);
        Assert.True((await failure).Blocked);
        Assert.False(policy.TrySchedule("競合する", () => true));
    }
}
