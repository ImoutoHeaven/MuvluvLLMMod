using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TmpRestoreDecisionTests
{
    [Fact]
    public void External_same_value_assignment_is_left_alone_but_guarded_refresh_restores()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        const int instanceId = 42;
        const string source = "確認する";
        const string translated = "确定";
        var lookups = 0;

        // This is the plugin-owned translation that establishes provenance for the component.
        var translatedAssignment = provenance.BeginExternalSetter(instance, instanceId);
        provenance.Record(translatedAssignment, translated, source);

        // A pooled component can receive an unrelated external assignment with exactly the
        // same value. The production decision must not consult the reverse map or restore here.
        var externalAssignment = provenance.BeginExternalSetter(instance, instanceId);
        var externalResult = TmpRestoreDecision.Resolve(
            provenance,
            externalAssignment,
            translated,
            TmpTextAssignmentOrigin.ExternalSetter,
            _ =>
            {
                lookups++;
                return source;
            });

        Assert.Equal(translated, externalResult);
        Assert.Equal(0, lookups);
        Assert.Equal(0, provenance.Count);

        // A later plugin-owned refresh may restore only a still-valid record. Re-establish the
        // record through the same external setter path used by the Harmony translation prefix.
        var nextTranslatedAssignment = provenance.BeginExternalSetter(instance, instanceId);
        provenance.Record(nextTranslatedAssignment, translated, source);
        var refreshAssignment = provenance.BeginPluginRefresh(instance, instanceId);
        var refreshResult = TmpRestoreDecision.Resolve(
            provenance,
            refreshAssignment,
            translated,
            TmpTextAssignmentOrigin.PluginRefresh,
            value =>
            {
                lookups++;
                return value == translated ? source : null;
            });

        Assert.Equal(source, refreshResult);
        Assert.Equal(1, lookups);
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Failed_refresh_validation_is_a_no_op_and_cannot_be_reactivated()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var assignment = provenance.BeginExternalSetter(instance, 43);
        provenance.Record(assignment, "确定", "確認する");

        var refresh = provenance.BeginPluginRefresh(instance, 43);
        Assert.Equal(
            "确定",
            TmpRestoreDecision.Resolve(
                provenance,
                refresh,
                "确定",
                TmpTextAssignmentOrigin.PluginRefresh,
                _ => null));
        Assert.Equal(0, provenance.Count);

        Assert.Equal(
            "确定",
            TmpRestoreDecision.Resolve(
                provenance,
                provenance.BeginPluginRefresh(instance, 43),
                "确定",
                TmpTextAssignmentOrigin.PluginRefresh,
                _ => "確認する"));
    }
}
