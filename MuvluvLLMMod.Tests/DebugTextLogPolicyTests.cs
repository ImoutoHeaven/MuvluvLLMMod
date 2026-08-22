using System.Collections.Concurrent;
using System.Reflection;
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
            policy.Observe("文本" + index, containsKana: true, durablyPending: true, acceptedByScheduler: true, now: Start);

        Assert.Equal(32, policy.SeenCount);
    }

    [Fact]
    public void Deduper_retains_only_fixed_size_keys_and_truncated_display_text_for_long_inputs()
    {
        var policy = new DebugTextLogPolicy(capacity: 4, linesPerSecond: 100_000, startTime: Start);

        for (var index = 0; index < 20; index++)
            policy.Observe(new string('字', 1_000) + index, true, true, true, Start);

        Assert.Equal(4, policy.SeenCount);
        Assert.Equal(80, policy.MaxSeenTextLength);

        // A count cap alone is not a memory cap. The retained dictionary key must be the
        // fixed-size hash, never one of the arbitrarily long source strings.
        var seenField = typeof(DebugTextLogPolicy).GetField(
            "seen",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(seenField);
        Assert.Equal(typeof(ulong), seenField!.FieldType.GetGenericArguments()[0]);
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
            acceptedByScheduler: false,
            now: Start);

        Assert.True(decision.ShouldLog);
        Assert.Equal(80, decision.Text!.Length);
        Assert.Equal(text[..80], decision.Text);
        Assert.True(decision.ContainsKana);
        Assert.True(decision.DurablyPending);
        Assert.False(decision.AcceptedByScheduler);
    }

    [Fact]
    public async Task Unique_concurrent_inputs_at_one_timestamp_use_only_the_available_tokens()
    {
        const int workerCount = 16;
        const int inputsPerWorker = 1_000;
        const int capacity = 64;
        const int availableTokens = 37;
        var policy = new DebugTextLogPolicy(
            capacity,
            linesPerSecond: availableTokens,
            summaryInterval: TimeSpan.FromDays(1),
            startTime: Start);
        var decisions = new ConcurrentBag<DebugTextLogDecision>();
        using var barrier = new Barrier(workerCount);

        var workers = Enumerable.Range(0, workerCount).Select(worker =>
            Task.Factory.StartNew(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                for (var index = 0; index < inputsPerWorker; index++)
                {
                    // Keep the distinguishing part inside the long input's hash domain. This
                    // proves that unique, long values do not collapse to their 80-char display.
                    var input = $"{worker:D2}:{index:D4}|" + new string('長', 5_000);
                    decisions.Add(policy.Observe(
                        input,
                        containsKana: true,
                        durablyPending: true,
                        acceptedByScheduler: true,
                        now: Start));
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers);

        var normalLines = decisions.Count(decision => decision.ShouldLog);
        var summaryLines = decisions.Count(decision => decision.SuppressedLines > 0);
        Assert.Equal(workerCount * inputsPerWorker, decisions.Count);
        Assert.Equal(availableTokens, normalLines + summaryLines);
        Assert.Equal(capacity, policy.SeenCount);
        Assert.Equal(DebugTextLogPolicy.DefaultTextLimit, policy.MaxSeenTextLength);

        var seenField = typeof(DebugTextLogPolicy).GetField(
            "seen",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(seenField);
        Assert.Equal(typeof(ulong), seenField!.FieldType.GetGenericArguments()[0]);
    }
}
