using System.Collections.Concurrent;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class PluginLifecycleGateTests
{
    [Fact]
    public void Cleanup_runs_the_teardown_sequence_in_declared_order()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad());
        var events = new List<string>();
        var steps = new[]
        {
            Step("config unsubscribe", events),
            Step("freeze", events),
            Step("machine shutdown", events),
            Step("cancel persistence", events),
            Step("flush", events),
            Step("unpatch", events)
        };

        Assert.True(gate.Cleanup(steps));
        Assert.Equal(
            new[]
            {
                "config unsubscribe",
                "freeze",
                "machine shutdown",
                "cancel persistence",
                "flush",
                "unpatch"
            },
            events);
        Assert.True(gate.IsCleaningUp);
    }

    [Fact]
    public void Concurrent_cleanup_invocation_executes_each_step_at_most_once()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad());
        var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var steps = new[]
        {
            new PluginCleanupStep("config", () => Increment(counts, "config")),
            new PluginCleanupStep("freeze", () => Increment(counts, "freeze")),
            new PluginCleanupStep("machine", () => Increment(counts, "machine")),
            new PluginCleanupStep("cancel", () => Increment(counts, "cancel")),
            new PluginCleanupStep("flush", () => Increment(counts, "flush")),
            new PluginCleanupStep("unpatch", () => Increment(counts, "unpatch"))
        };

        Parallel.For(0, 128, _ => gate.Cleanup(steps));

        Assert.All(steps, step => Assert.Equal(1, counts[step.Name]));
    }

    [Fact]
    public void Partial_initialization_failure_can_roll_back_and_allow_a_later_load()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad());
        var events = new List<string>();

        try
        {
            events.Add("Harmony patched");
            events.Add("Hotkey injected");
            throw new InvalidOperationException("component setup failed");
        }
        catch (InvalidOperationException)
        {
            Assert.True(gate.Cleanup(new[]
            {
                Step("config unsubscribe", events),
                Step("freeze", events),
                Step("machine shutdown", events),
                Step("cancel persistence", events),
                Step("flush", events),
                Step("unpatch", events)
            }));
        }

        Assert.Equal(
            new[]
            {
                "Harmony patched",
                "Hotkey injected",
                "config unsubscribe",
                "freeze",
                "machine shutdown",
                "cancel persistence",
                "flush",
                "unpatch"
            },
            events);
        Assert.True(gate.TryBeginLoad());
    }

    [Fact]
    public void A_failed_step_does_not_skip_later_rollback_steps()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad());
        var events = new List<string>();
        var diagnostics = new List<string>();

        var succeeded = gate.Cleanup(
            new[]
            {
                Step("before", events),
                new PluginCleanupStep("broken", () =>
                {
                    events.Add("broken");
                    throw new InvalidOperationException();
                }),
                Step("after", events)
            },
            (name, _) => diagnostics.Add(name));

        Assert.False(succeeded);
        Assert.Equal(new[] { "before", "broken", "after" }, events);
        Assert.Equal(new[] { "broken" }, diagnostics);
    }

    private static PluginCleanupStep Step(string name, ICollection<string> events) =>
        new(name, () => events.Add(name));

    private static void Increment(ConcurrentDictionary<string, int> counts, string name) =>
        counts.AddOrUpdate(name, 1, static (_, value) => value + 1);
}
