using System.Collections.Concurrent;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class DebugTextLogPolicyTests
{
    private static readonly DateTimeOffset Start =
        new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Deduper_has_a_fixed_memory_ceiling_for_unique_text()
    {
        var policy = new DebugTextLogPolicy(capacity: 32, linesPerSecond: 100_000, startTime: Start);

        for (var index = 0; index < 10_000; index++)
            policy.Observe("文本" + index, containsKana: true, durablyPending: true, acceptedByLiveWorker: true, now: Start);

        Assert.Equal(32, policy.SeenCount);
    }

    [Fact]
    public void Token_bucket_limits_new_log_lines_until_time_advances()
    {
        var policy = new DebugTextLogPolicy(
            capacity: 1_000,
            linesPerSecond: 3,
            summaryInterval: TimeSpan.FromDays(1),
            startTime: Start);

        var initialLogs = Enumerable.Range(0, 100)
            .Count(index => policy.Observe("文本" + index, true, true, true, Start).ShouldLog);
        var logsAfterOneSecond = Enumerable.Range(100, 100)
            .Count(index => policy.Observe("文本" + index, true, true, true, Start.AddSeconds(1)).ShouldLog);

        Assert.Equal(3, initialLogs);
        Assert.Equal(3, logsAfterOneSecond);
    }

    [Fact]
    public void Emits_a_periodic_summary_for_suppressed_lines()
    {
        var policy = new DebugTextLogPolicy(
            capacity: 32,
            linesPerSecond: 1,
            summaryInterval: TimeSpan.FromSeconds(1),
            startTime: Start);

        Assert.True(policy.Observe("第一", true, false, false, Start).ShouldLog);
        Assert.False(policy.Observe("第二", true, false, false, Start).ShouldLog);
        Assert.False(policy.Observe("第三", true, false, false, Start).ShouldLog);

        var summary = policy.Observe("第二", true, false, false, Start.AddSeconds(1));

        Assert.False(summary.ShouldLog);
        Assert.Equal(2, summary.SuppressedLines);
        Assert.Equal(0, policy.SuppressedCount);
    }

    [Fact]
    public void Truncates_logged_text_to_eighty_characters_and_preserves_observation_flags()
    {
        var text = new string('字', 100);
        var policy = new DebugTextLogPolicy(capacity: 4, linesPerSecond: 1, startTime: Start);

        var decision = policy.Observe(
            text,
            containsKana: true,
            durablyPending: true,
            acceptedByLiveWorker: false,
            now: Start);

        Assert.True(decision.ShouldLog);
        Assert.Equal(80, decision.Text!.Length);
        Assert.Equal(text[..80], decision.Text);
        Assert.True(decision.ContainsKana);
        Assert.True(decision.DurablyPending);
        Assert.False(decision.AcceptedByLiveWorker);
    }

    [Fact]
    public void Deduplication_and_throttling_are_safe_under_concurrency()
    {
        var policy = new DebugTextLogPolicy(capacity: 64, linesPerSecond: 10_000, startTime: Start);
        var decisions = new ConcurrentBag<DebugTextLogDecision>();

        Parallel.For(0, 5_000, index =>
        {
            decisions.Add(policy.Observe(
                "并发文本" + index,
                containsKana: index % 2 == 0,
                durablyPending: true,
                acceptedByLiveWorker: true,
                now: Start));
        });

        Assert.Equal(5_000, decisions.Count);
        Assert.InRange(policy.SeenCount, 0, 64);
        Assert.InRange(decisions.Count(decision => decision.ShouldLog), 0, 5_000);
    }
}
