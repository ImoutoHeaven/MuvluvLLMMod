using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class SkillRenderFlowTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "MuvluvLLMMod.SkillRender.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Generated_skill_text_is_applied_at_render_assignment_and_can_restore_when_display_is_off()
    {
        const string source = "スキル説明する";
        const string translated = "技能说明";
        var cache = new TranslationCache(root);
        cache.StoreGenerated(source, translated);
        var resolver = new TranslationResolver(cache, _ => { }, _ => { });
        var provenance = new TmpTranslationProvenance();
        var text = new FakeTmp();

        // With no skill-description hook, the game result remains the source until the final
        // TMP assignment. This is the production route that owns the display provenance.
        var skillResult = source;
        Assert.Equal(source, skillResult);
        var assignment = provenance.BeginExternalSetter(text, text.InstanceId);
        text.Value = resolver.Resolve(skillResult);
        provenance.Record(assignment, text.Value, skillResult);

        Assert.Equal(translated, text.Value);
        Assert.Empty(cache.PendingSnapshot());
        Assert.True(cache.TryGetSourceForTranslatedValue(translated, out var mappedSource));
        Assert.Equal(source, mappedSource);

        // F2-off is represented by the plugin-owned refresh path. It may restore only the
        // provenance established by the final render assignment, never by a pre-render hook.
        var refresh = provenance.BeginPluginRefresh(text, text.InstanceId);
        var displayOff = TmpRestoreDecision.Resolve(
            provenance,
            refresh,
            text.Value,
            TmpTextAssignmentOrigin.PluginRefresh,
            value => cache.TryGetSourceForTranslatedValue(value, out var restored)
                ? restored
                : null);

        text.Value = displayOff;
        Assert.Equal(source, text.Value);
        Assert.Equal(0, provenance.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private sealed class FakeTmp
    {
        public int InstanceId { get; } = 7;
        public string Value { get; set; } = string.Empty;
    }
}
