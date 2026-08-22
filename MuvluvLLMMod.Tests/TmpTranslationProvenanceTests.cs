using System.Collections.Concurrent;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TmpTranslationProvenanceTests
{
    [Fact]
    public void Pooled_reuse_same_instance_and_same_value_cannot_restore_stale_source()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var translated = "确定";

        var pluginAssignment = provenance.BeginPluginRefresh(instance, 101);
        provenance.Record(pluginAssignment, translated, "確認する");

        // Pooling reuses the same component, so its native instance ID is unchanged. The
        // unrelated assignment is still an external setter event and must retire the record.
        var externalAssignment = provenance.BeginExternalSetter(instance, 101);
        Assert.Equal(0, provenance.Count);

        var displayed = translated;
        if (provenance.TryRestore(
                externalAssignment,
                displayed,
                TmpTextAssignmentOrigin.ExternalSetter,
                _ => "確認する",
                out var source))
        {
            displayed = source;
        }

        Assert.Equal(translated, displayed);
        Assert.False(provenance.TryRestore(
            provenance.BeginPluginRefresh(instance, 101),
            translated,
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "確認する",
            out _));
    }

    [Fact]
    public void External_setter_equal_to_recorded_translation_invalidates_before_resolution()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var firstAssignment = provenance.BeginExternalSetter(instance, 7);
        provenance.Record(firstAssignment, "确定", "確認する");

        // Equality with the old translated value is deliberately not a continuity signal.
        var replacementAssignment = provenance.BeginExternalSetter(instance, 7);
        Assert.Equal(0, provenance.Count);
        Assert.False(provenance.TryRestore(
            replacementAssignment,
            "确定",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "確認する",
            out _));
    }

    [Fact]
    public void External_origin_can_never_restore_even_with_intact_provenance()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var assignment = provenance.BeginPluginRefresh(instance, 8);
        provenance.Record(assignment, "确定", "確認する");

        var displayed = "确定";
        if (provenance.TryRestore(
                assignment,
                displayed,
                TmpTextAssignmentOrigin.ExternalSetter,
                _ => "確認する",
                out var source))
        {
            displayed = source;
        }

        Assert.Equal("确定", displayed);
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Guarded_refresh_lookup_restores_only_intact_provenance()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var assignment = provenance.BeginPluginRefresh(instance, 11);
        provenance.Record(assignment, "确定", "確認する");

        var displayed = "确定";
        var refreshAssignment = provenance.BeginPluginRefresh(instance, 11);
        if (provenance.TryRestore(
                refreshAssignment,
                displayed,
                TmpTextAssignmentOrigin.PluginRefresh,
                _ => "確認する",
                out var source))
        {
            displayed = source;
        }

        Assert.Equal("確認する", displayed);
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Failed_source_validation_discards_the_entry_permanently()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var assignment = provenance.BeginPluginRefresh(instance, 12);
        provenance.Record(assignment, "确定", "確認する");

        Assert.False(provenance.TryRestore(
            provenance.BeginPluginRefresh(instance, 12),
            "确定",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => null,
            out _));
        Assert.Equal(0, provenance.Count);

        // A later reverse-map reappearance cannot resurrect the failed record.
        Assert.False(provenance.TryRestore(
            provenance.BeginPluginRefresh(instance, 12),
            "确定",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "確認する",
            out _));
    }

    [Fact]
    public void Unresolvable_value_is_left_untouched()
    {
        var provenance = new TmpTranslationProvenance();
        var instance = new object();
        var displayed = "确定";
        var assignment = provenance.BeginPluginRefresh(instance, 13);
        provenance.Record(assignment, displayed, "確認する");

        if (provenance.TryRestore(
                provenance.BeginPluginRefresh(instance, 13),
                displayed,
                TmpTextAssignmentOrigin.PluginRefresh,
                _ => "別の原文",
                out var source))
        {
            displayed = source;
        }

        Assert.Equal("确定", displayed);
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Capacity_eviction_is_a_no_op_and_never_restores_the_evicted_source()
    {
        var provenance = new TmpTranslationProvenance(capacity: 2);
        var firstInstance = new object();
        var secondInstance = new object();
        var thirdInstance = new object();
        var first = provenance.BeginPluginRefresh(firstInstance, 1);
        provenance.Record(first, "译文1", "原文1");
        var second = provenance.BeginPluginRefresh(secondInstance, 2);
        provenance.Record(second, "译文2", "原文2");
        var third = provenance.BeginPluginRefresh(thirdInstance, 3);
        provenance.Record(third, "译文3", "原文3");

        var evictedText = "译文1";
        if (provenance.TryRestore(
                first,
                evictedText,
                TmpTextAssignmentOrigin.PluginRefresh,
                _ => "原文1",
                out var wrongSource))
        {
            evictedText = wrongSource;
        }

        Assert.Equal("译文1", evictedText);
        Assert.True(provenance.TryRestore(
            provenance.BeginPluginRefresh(thirdInstance, 3),
            "译文3",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "原文3",
            out var source));
        Assert.Equal("原文3", source);
    }

    [Fact]
    public void Evicted_identity_cannot_revalidate_when_the_same_object_returns()
    {
        var provenance = new TmpTranslationProvenance(capacity: 1);
        var reusedInstance = new object();
        var otherInstance = new object();
        var oldAssignment = provenance.BeginPluginRefresh(reusedInstance, 21);
        provenance.Record(oldAssignment, "译文", "原文");

        var otherAssignment = provenance.BeginPluginRefresh(otherInstance, 22);
        provenance.Record(otherAssignment, "别的译文", "别的原文");
        var newAssignment = provenance.BeginPluginRefresh(reusedInstance, 21);

        Assert.False(provenance.TryRestore(
            oldAssignment,
            "译文",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "原文",
            out _));
        Assert.False(provenance.TryRestore(
            newAssignment,
            "译文",
            TmpTextAssignmentOrigin.PluginRefresh,
            _ => "原文",
            out _));
    }

    [Fact]
    public void Concurrent_record_and_lookup_never_returns_a_wrong_source()
    {
        const int instanceCount = 128;
        var provenance = new TmpTranslationProvenance(capacity: 64);
        var instances = Enumerable.Range(0, instanceCount).Select(_ => new object()).ToArray();
        var wrongSources = new ConcurrentBag<string>();

        Parallel.For(0, 20_000, index =>
        {
            var instanceId = index % instanceCount;
            var instance = instances[instanceId];
            var translated = "译文" + instanceId;
            var expectedSource = "原文" + instanceId;
            var assignment = provenance.BeginExternalSetter(instance, instanceId);
            provenance.Record(assignment, translated, expectedSource);

            var refreshAssignment = provenance.BeginPluginRefresh(instance, instanceId);
            if (provenance.TryRestore(
                    refreshAssignment,
                    translated,
                    TmpTextAssignmentOrigin.PluginRefresh,
                    value => value == translated ? expectedSource : null,
                    out var source)
                && source != expectedSource)
            {
                wrongSources.Add(source);
            }
        });

        Assert.Empty(wrongSources);
        Assert.InRange(provenance.Count, 0, 64);
    }
}
