using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TmpTranslationProvenanceTests
{
    [Fact]
    public void Restores_only_the_recorded_instance_when_values_collide()
    {
        var provenance = new TmpTranslationProvenance();
        var recordedText = "确定";
        var otherInstanceText = "确定";
        provenance.Record(101, recordedText, "確認する");

        if (provenance.TryRestore(101, recordedText, _ => "確認する", out var source))
            recordedText = source;
        if (provenance.TryRestore(202, otherInstanceText, _ => "確認する", out source))
            otherInstanceText = source;

        Assert.Equal("確認する", recordedText);
        Assert.Equal("确定", otherInstanceText);
    }

    [Fact]
    public void Does_not_restore_text_overwritten_after_recording()
    {
        var provenance = new TmpTranslationProvenance();
        provenance.Record(7, "确定", "確認する");

        var currentText = "外部文本";
        var restored = provenance.TryRestore(7, currentText, _ => "確認する", out var source);

        Assert.False(restored);
        Assert.Equal("外部文本", currentText);
        Assert.Equal(string.Empty, source);
        Assert.False(provenance.TryRestore(7, "确定", _ => "確認する", out _));
    }

    [Fact]
    public void Leaves_text_alone_when_the_recorded_source_is_unresolvable()
    {
        var provenance = new TmpTranslationProvenance();
        var currentText = "确定";
        provenance.Record(7, currentText, "確認する");

        var restored = provenance.TryRestore(7, currentText, _ => null, out var source);

        Assert.False(restored);
        Assert.Equal("确定", currentText);
        Assert.Equal(string.Empty, source);
    }

    [Fact]
    public void Evicts_oldest_instances_and_remains_bounded()
    {
        var provenance = new TmpTranslationProvenance(capacity: 3);
        for (var instanceId = 1; instanceId <= 100; instanceId++)
            provenance.Record(instanceId, "译文" + instanceId, "原文" + instanceId);

        Assert.Equal(3, provenance.Count);
        Assert.False(provenance.TryRestore(1, "译文1", _ => "原文1", out _));
        Assert.True(provenance.TryRestore(98, "译文98", _ => "原文98", out var source));
        Assert.Equal("原文98", source);
        Assert.True(provenance.TryRestore(99, "译文99", _ => "原文99", out _));
        Assert.True(provenance.TryRestore(100, "译文100", _ => "原文100", out _));
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Record_and_lookup_are_safe_under_concurrency()
    {
        var provenance = new TmpTranslationProvenance(capacity: 64);

        Parallel.For(0, 20_000, index =>
        {
            var instanceId = index % 128;
            var translated = "译文" + instanceId;
            var source = "原文" + instanceId;
            provenance.Record(instanceId, translated, source);
            _ = provenance.TryRestore(instanceId, translated, _ => null, out _);
            provenance.InvalidateIfTextChanged(instanceId, "外部文本");
            _ = provenance.Count;
        });

        Assert.InRange(provenance.Count, 0, 64);
    }
}
