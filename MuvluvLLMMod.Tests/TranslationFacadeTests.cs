using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TranslationFacadeTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "MuvluvLLMMod.Translation.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<string> priority = new();
    private readonly List<string> normal = new();
    private readonly TranslationCache cache;

    public TranslationFacadeTests()
    {
        cache = new TranslationCache(root);
        Core.Cache = cache;
        Core.AcceptEnqueues = true;
        Core.Resolver = new TranslationResolver(
            cache,
            (template, _) =>
            {
                priority.Add(template);
                Core.MarkEnqueueAccepted();
            },
            (template, _) =>
            {
                normal.Add(template);
                Core.MarkEnqueueAccepted();
            });
        Config.Translation.Value = true;
        Patch.isPlayingScenario = false;
    }

    [Fact]
    public void Display_off_leaves_text_unresolved_but_still_observes_normal_work()
    {
        Config.Translation.Value = false;
        var original = "表示する";

        Assert.Equal(original, Translation.ResolveAny(original));
        Assert.Empty(normal);

        Assert.True(Translation.ObserveForTranslation(original, priority: false));
        Assert.Equal(new[] { original }, normal);
        Assert.Equal(new[] { original }, cache.PendingSnapshot());
        Assert.Equal(new EnqueueObservation(true, true), Translation.LastEnqueueObservation);
        Assert.True(Translation.LastResolveAcceptedByScheduler);
    }

    [Fact]
    public void Display_off_keeps_durable_pending_state_when_no_worker_accepts_it()
    {
        Config.Translation.Value = false;
        Core.AcceptEnqueues = false;

        var accepted = Translation.ObserveForTranslation("保留する", priority: true);

        Assert.False(accepted);
        Assert.Equal(new[] { "保留する" }, priority);
        Assert.Equal(new[] { "保留する" }, cache.PendingSnapshot());
        Assert.Equal(new EnqueueObservation(true, false), Translation.LastEnqueueObservation);
        Assert.False(Translation.LastResolveAcceptedByScheduler);
    }

    [Fact]
    public void Display_on_returns_generated_text_without_enqueuing()
    {
        cache.StoreGenerated("生成する", "生成翻訳");

        Assert.Equal("生成翻訳", Translation.ResolveAny("生成する"));
        Assert.Empty(priority);
        Assert.Empty(normal);
        Assert.Equal(new EnqueueObservation(false, false), Translation.LastEnqueueObservation);
    }

    [Fact]
    public void Scenario_state_routes_new_work_to_the_priority_observer()
    {
        Patch.isPlayingScenario = true;

        Assert.Equal("優先する", Translation.ResolveAny("優先する"));
        Assert.Equal(new[] { "優先する" }, priority);
        Assert.Empty(normal);
        Assert.Equal(new EnqueueObservation(true, true), Translation.LastEnqueueObservation);
        Assert.True(Translation.LastResolveAcceptedByScheduler);
    }

    [Fact]
    public void Display_off_priority_backlog_is_reported_as_scheduler_acceptance()
    {
        Config.Translation.Value = false;

        Assert.True(Translation.ObserveForTranslation("场景优先する", priority: true));
        Assert.Equal(new EnqueueObservation(true, true), Translation.LastEnqueueObservation);
        Assert.True(Translation.LastResolveAcceptedByScheduler);
    }

    public void Dispose()
    {
        Core.Cache = null!;
        Core.Resolver = null!;
        Core.AcceptEnqueues = true;
        Config.Translation.Value = true;
        Patch.isPlayingScenario = false;
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
