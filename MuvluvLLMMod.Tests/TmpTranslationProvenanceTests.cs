using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TmpTranslationProvenanceTests
{
    [Fact]
    public void Restores_only_the_recorded_instance_when_values_collide()
    {
        var provenance = new TmpTranslationProvenance();
        var recordedInstance = new object();
        var otherInstance = new object();
        var recordedText = "确定";
        var otherInstanceText = "确定";
        var assignment = provenance.BeginPluginRefresh(recordedInstance, 101);
        provenance.Record(assignment, recordedText, "確認する");

        if (provenance.TryRestore(
                provenance.BeginPluginRefresh(recordedInstance, 101),
                recordedText,
                TmpTextAssignmentOrigin.PluginRefresh,
                _ => "確認する",
                out var source))
        {
            recordedText = source;
        }

        if (provenance.TryRestore(
                provenance.BeginPluginRefresh(otherInstance, 202),
                otherInstanceText,
                TmpTextAssignmentOrigin.PluginRefresh,
                _ => "確認する",
                out source))
        {
            otherInstanceText = source;
        }

        Assert.Equal("確認する", recordedText);
        Assert.Equal("确定", otherInstanceText);
    }

    [Fact]
    public void Does_not_restore_text_overwritten_after_recording()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var assignment = provenance.BeginPluginRefresh(instance, 7);
        provenance.Record(assignment, "确定", "確認する");

        var currentText = "外部文本";
        var externalAssignment = provenance.BeginExternalSetter(instance, 7);
        var restored = provenance.TryRestore(
            externalAssignment,
            currentText,
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "確認する",
            out var source);

        Assert.False(restored);
        Assert.Equal("外部文本", currentText);
        Assert.Equal(string.Empty, source);
        Assert.False(provenance.TryRestore(
            provenance.BeginPluginRefresh(instance, 7),
            "确定",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "確認する",
            out _));
    }

    [Fact]
    public void Leaves_text_alone_when_the_recorded_source_is_unresolvable()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var assignment = provenance.BeginPluginRefresh(instance, 7);
        var currentText = "确定";
        provenance.Record(assignment, currentText, "確認する");

        var restored = provenance.TryRestore(
            provenance.BeginPluginRefresh(instance, 7),
            currentText,
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => null,
            out var source);

        Assert.False(restored);
        Assert.Equal("确定", currentText);
        Assert.Equal(string.Empty, source);
    }

    [Fact]
    public void Evicts_oldest_instances_and_remains_bounded()
    {
        var provenance = new TmpTranslationProvenance(capacity: 3);
        var assignments = new Dictionary<int, TmpTextAssignment>();
        for (var instanceId = 1; instanceId <= 100; instanceId++)
        {
            var instance = new object();
            var assignment = provenance.BeginPluginRefresh(instance, instanceId);
            provenance.Record(assignment, "译文" + instanceId, "原文" + instanceId);
            assignments[instanceId] = assignment;
        }

        Assert.Equal(3, provenance.Count);
        Assert.False(provenance.TryRestore(
            assignments[1],
            "译文1",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "原文1",
            out _));
        Assert.True(provenance.TryRestore(
            assignments[98],
            "译文98",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "原文98",
            out var source));
        Assert.Equal("原文98", source);
        Assert.True(provenance.TryRestore(
            assignments[99],
            "译文99",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "原文99",
            out _));
        Assert.True(provenance.TryRestore(
            assignments[100],
            "译文100",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "原文100",
            out _));
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Record_and_lookup_are_safe_under_concurrency()
    {
        var provenance = new TmpTranslationProvenance(capacity: 64);
        var instances = Enumerable.Range(0, 128).Select(_ => new object()).ToArray();

        Parallel.For(0, 20_000, index =>
        {
            var instanceId = index % instances.Length;
            var instance = instances[instanceId];
            var assignment = provenance.BeginExternalSetter(instance, instanceId);
            provenance.Record(assignment, "译文" + instanceId, "原文" + instanceId);
            _ = provenance.TryRestore(
                assignment,
                "译文" + instanceId,
                TmpTextAssignmentOrigin.ExternalSetter,
                _ => "原文" + instanceId,
                out _);
            _ = provenance.Count;
        });

        Assert.InRange(provenance.Count, 0, 64);
    }
}
