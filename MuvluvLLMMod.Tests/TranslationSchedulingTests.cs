using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TranslationSchedulingTests
{
    [Fact]
    public void Queue_prioritizes_work_and_preserves_fifo_per_class()
    {
        var queue = new TranslationWorkQueue();
        queue.Enqueue("あ-normal-first");
        queue.Enqueue("い-normal-second");
        queue.EnqueuePriority("ア-priority-first");
        queue.EnqueuePriority("イ-priority-second");

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.True(queue.TryDequeue(out var third));
        Assert.True(queue.TryDequeue(out var fourth));
        Assert.Equal("ア-priority-first", first);
        Assert.Equal("イ-priority-second", second);
        Assert.Equal("あ-normal-first", third);
        Assert.Equal("い-normal-second", fourth);
    }

    [Fact]
    public void Queue_deduplicates_promotes_and_tracks_terminal_state()
    {
        var queue = new TranslationWorkQueue();
        Assert.True(queue.Enqueue("あ-first"));
        Assert.True(queue.Enqueue("い-promoted"));
        Assert.False(queue.Enqueue("い-promoted"));
        Assert.True(queue.EnqueuePriority("い-promoted"));
        Assert.False(queue.EnqueuePriority("い-promoted"));
        Assert.True(queue.TryDequeue(out var promoted));
        Assert.False(queue.Enqueue(promoted));
        queue.Finish(promoted, false);
        Assert.True(queue.Enqueue(promoted));
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("あ-first", first);
        queue.Finish(first, true);
        Assert.False(queue.Enqueue(first));
    }

    [Fact]
    public void Queue_rejects_non_kana_and_drains_priority_without_normal()
    {
        var queue = new TranslationWorkQueue();
        Assert.False(queue.Enqueue("確認"));
        Assert.False(queue.EnqueuePriority("简体中文"));
        queue.Enqueue("あ-normal");
        queue.EnqueuePriority("ア-priority");
        Assert.Equal(new[] { "ア-priority" }, queue.DrainPriority());
        Assert.True(queue.EnqueuePriority("ア-priority"));
        Assert.True(queue.TryDequeue(out var priority));
        Assert.Equal("ア-priority", priority);
        Assert.True(queue.TryDequeue(out var normal));
        Assert.Equal("あ-normal", normal);
    }

    [Fact]
    public void Queue_cancel_removes_priority_and_normal_work_before_processing()
    {
        var queue = new TranslationWorkQueue();
        queue.Enqueue("あ-normal-cancel");
        queue.Enqueue("い-normal-keep");
        queue.EnqueuePriority("ア-priority-cancel");
        queue.EnqueuePriority("イ-priority-keep");

        Assert.True(queue.Cancel("あ-normal-cancel"));
        Assert.True(queue.Cancel("ア-priority-cancel"));
        Assert.False(queue.Cancel("う-missing"));
        Assert.True(queue.TryDequeue(out var priority));
        Assert.Equal("イ-priority-keep", priority);
        Assert.True(queue.TryDequeue(out var normal));
        Assert.Equal("い-normal-keep", normal);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public async Task Failed_inflight_work_can_extract_a_newer_deferred_priority_generation()
    {
        var queue = new TranslationWorkQueue();
        Assert.True(queue.Enqueue("再試行する", 1));
        var current = await queue.DequeueAsync(CancellationToken.None);
        Assert.False(queue.EnqueuePriority("再試行する", 2));

        var deferred = queue.FinishAndTakeDeferred(current);

        Assert.True(deferred.HasValue);
        Assert.Equal("再試行する", deferred.Value.Template);
        Assert.True(deferred.Value.Priority);
        Assert.Equal(2, deferred.Value.PendingGeneration);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void Backlog_is_fifo_deduplicated_and_kana_only()
    {
        var backlog = new TranslationPriorityBacklog();
        Assert.False(backlog.Enqueue("確認"));
        Assert.True(backlog.Enqueue("あ-first"));
        Assert.False(backlog.Enqueue("あ-first"));
        Assert.True(backlog.Enqueue("い-second"));
        Assert.Equal(new[] { "あ-first", "い-second" }, backlog.Drain());
        Assert.Empty(backlog.Drain());
    }

    [Fact]
    public void Backlog_cancel_removes_only_the_covered_priority_work()
    {
        var backlog = new TranslationPriorityBacklog();
        backlog.Enqueue("あ-cancel");
        backlog.Enqueue("い-keep");

        Assert.True(backlog.Cancel("あ-cancel"));
        Assert.False(backlog.Cancel("う-missing"));
        Assert.Equal(new[] { "い-keep" }, backlog.Drain());
    }

    [Fact]
    public void Progress_counts_completed_inflight_and_unique_failed_templates()
    {
        var progress = new TranslationProgress();
        progress.Start();
        progress.Start();
        progress.Start();
        progress.Complete("あ-ok");
        progress.Fail("い-fail");
        progress.Fail("い-fail");
        Assert.Equal((1, 0, 1), progress.Snapshot());
    }

    [Fact]
    public void Limiter_reserves_starts_at_configured_intervals()
    {
        var limiter = new RequestRateLimiter(2);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(start));
        Assert.Equal(TimeSpan.FromMilliseconds(500), limiter.Reserve(start));
        Assert.Equal(TimeSpan.FromMilliseconds(750), limiter.Reserve(start.AddMilliseconds(250)));
    }

    [Fact]
    public void Reloaded_limiter_preserves_the_previous_request_window()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var original = new RequestRateLimiter(2);
        original.Reserve(start);

        var reloaded = new RequestRateLimiter(2, original.NextStart);

        Assert.Equal(TimeSpan.FromMilliseconds(500), reloaded.Reserve(start));
    }

    [Fact]
    public async Task Limiter_does_not_hold_start_gate_while_response_is_pending()
    {
        var limiter = new RequestRateLimiter(1000);
        var response = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = limiter.StartAsync(() => response.Task, CancellationToken.None);
        await Task.Delay(20);
        var secondStarted = false;
        var second = limiter.StartAsync(() => { secondStarted = true; return Task.FromResult(2); }, CancellationToken.None);
        await Task.Delay(20);
        Assert.True(secondStarted);
        response.SetResult(1);
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
    }
}
