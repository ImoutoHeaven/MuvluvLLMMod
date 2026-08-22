using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TmpPluginWriteOwnershipTests
{
    [Fact]
    public void Exact_plugin_refresh_setter_consumes_one_token_without_external_invalidation()
    {
        var provenance = new TmpTranslationProvenance();
        var ownership = new TmpPluginWriteOwnership(provenance);
        var target = new FakeTmp(11);
        var translatedAssignment = provenance.BeginExternalSetter(target, target.InstanceId);
        provenance.Record(translatedAssignment, "确定", "確認する");
        var refreshAssignment = provenance.BeginPluginRefresh(target, target.InstanceId);

        using (ownership.BeginPluginWrite(refreshAssignment))
        {
            Assert.True(ownership.TryConsume(target, target.InstanceId));
            target.Text = "确定";

            // A second setter on the same component is no longer covered by the consumed token.
            ExternalPrefix(provenance, ownership, target, "确定");
        }

        Assert.Equal(0, ownership.ActiveTokenCount);
        Assert.Equal(0, provenance.Count);
        Assert.Equal("确定", target.Text);
    }

    [Fact]
    public void Nested_other_tmp_external_setter_invalidates_stale_entry_during_resolution()
    {
        var provenance = new TmpTranslationProvenance();
        var ownership = new TmpPluginWriteOwnership(provenance);
        var outer = new FakeTmp(21);
        var victim = new FakeTmp(22);
        var assignment = provenance.BeginExternalSetter(victim, victim.InstanceId);
        provenance.Record(assignment, "确定", "確認する");

        // No token is active while a refresh resolves or logs. A synchronous callback that
        // writes another TMP must therefore enter the real external-setter path.
        _ = provenance.BeginExternalSetter(outer, outer.InstanceId);
        ExternalPrefix(provenance, ownership, victim, "确定");

        Assert.Equal("确定", victim.Text);
        Assert.False(TryRestore(provenance, victim, "确定"));
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Nested_same_tmp_external_setter_invalidates_byte_identical_entry()
    {
        var provenance = new TmpTranslationProvenance();
        var ownership = new TmpPluginWriteOwnership(provenance);
        var victim = new FakeTmp(31);
        var assignment = provenance.BeginExternalSetter(victim, victim.InstanceId);
        provenance.Record(assignment, "确定", "確認する");

        // This is the synchronous same-object callback case: it arrives without a plugin-write
        // token, even though the outer setter already recorded a translation.
        ExternalPrefix(provenance, ownership, victim, "确定");

        Assert.Equal("确定", victim.Text);
        Assert.False(TryRestore(provenance, victim, "确定"));
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Token_target_id_or_generation_mismatch_is_external_and_cannot_later_bypass()
    {
        var provenance = new TmpTranslationProvenance();
        var ownership = new TmpPluginWriteOwnership(provenance);
        var target = new FakeTmp(41);
        var other = new FakeTmp(42);
        var assignment = provenance.BeginPluginRefresh(target, target.InstanceId);
        provenance.Record(assignment, "确定", "確認する");

        using (ownership.BeginPluginWrite(assignment))
        {
            // Wrong object identity is not allowed to search through the token stack.
            Assert.False(ownership.TryConsume(other, other.InstanceId));
            ExternalPrefix(provenance, ownership, other, "确定");
        }

        var idAssignment = provenance.BeginPluginRefresh(target, target.InstanceId);
        provenance.Record(idAssignment, "确定", "確認する");
        using (ownership.BeginPluginWrite(idAssignment))
        {
            // The same object with a different native ID is an external assignment.
            Assert.False(ownership.TryConsume(target, target.InstanceId + 1));
            ExternalPrefix(provenance, ownership, target, "确定");
        }

        var generationAssignment = provenance.BeginPluginRefresh(target, target.InstanceId);
        provenance.Record(generationAssignment, "确定", "確認する");
        using (ownership.BeginPluginWrite(generationAssignment))
        {
            // A setter observed between token registration and the expected write advances the
            // assignment generation, so the old token can no longer bypass the Prefix.
            _ = provenance.BeginExternalSetter(target, target.InstanceId);
            Assert.False(ownership.TryConsume(target, target.InstanceId));
            ExternalPrefix(provenance, ownership, target, "确定");
        }

        Assert.Equal(0, ownership.ActiveTokenCount);
        Assert.Equal(0, provenance.Count);
        Assert.False(TryRestore(provenance, target, "确定"));
    }

    [Fact]
    public void Token_scope_cleans_up_when_no_prefix_consumes_it_or_the_setter_throws()
    {
        var provenance = new TmpTranslationProvenance();
        var ownership = new TmpPluginWriteOwnership(provenance);
        var target = new FakeTmp(51);
        var assignment = provenance.BeginPluginRefresh(target, target.InstanceId);

        using (ownership.BeginPluginWrite(assignment))
            Assert.Equal(1, ownership.ActiveTokenCount);
        Assert.Equal(0, ownership.ActiveTokenCount);

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (ownership.BeginPluginWrite(assignment))
                throw new InvalidOperationException("setter failed before prefix");
        }));
        Assert.Equal(0, ownership.ActiveTokenCount);

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (ownership.BeginPluginWrite(assignment))
            {
                Assert.True(ownership.TryConsume(target, target.InstanceId));
                throw new InvalidOperationException("setter failed after prefix");
            }
        }));
        Assert.Equal(0, ownership.ActiveTokenCount);
    }

    [Fact]
    public void Reset_invalidates_old_tokens_and_provenance_before_reload()
    {
        var provenance = new TmpTranslationProvenance();
        var ownership = new TmpPluginWriteOwnership(provenance);
        var target = new FakeTmp(61);
        var oldAssignment = provenance.BeginExternalSetter(target, target.InstanceId);
        provenance.Record(oldAssignment, "确定", "確認する");
        var oldRefresh = provenance.BeginPluginRefresh(target, target.InstanceId);
        var oldEpoch = oldRefresh.LifecycleEpoch;

        using (ownership.BeginPluginWrite(oldRefresh))
        {
            ownership.ResetForLifecycle();
            provenance.ResetForLifecycle();
            target.Text = "确定"; // An assignment while the hook is unpatched is not observed.

            Assert.False(ownership.TryConsume(target, target.InstanceId));
            Assert.False(TryRestore(provenance, target, "确定"));
        }

        var newRefresh = provenance.BeginPluginRefresh(target, target.InstanceId);
        Assert.NotEqual(oldEpoch, newRefresh.LifecycleEpoch);
        Assert.Equal("确定", target.Text);
        Assert.False(TryRestore(provenance, target, "确定"));
        Assert.Equal(0, provenance.Count);
    }

    [Fact]
    public void Normal_plugin_refresh_still_restores_a_valid_translation_once()
    {
        var provenance = new TmpTranslationProvenance();
        var target = new FakeTmp(71);
        var assignment = provenance.BeginExternalSetter(target, target.InstanceId);
        provenance.Record(assignment, "确定", "確認する");

        var refresh = provenance.BeginPluginRefresh(target, target.InstanceId);
        var restored = TmpRestoreDecision.Resolve(
            provenance,
            refresh,
            "确定",
            TmpTextAssignmentOrigin.PluginRefresh,
            value => value == "确定" ? "確認する" : null);

        Assert.Equal("確認する", restored);
        Assert.Equal(0, provenance.Count);
    }

    private static void ExternalPrefix(
        TmpTranslationProvenance provenance,
        TmpPluginWriteOwnership ownership,
        FakeTmp target,
        string value)
    {
        if (ownership.TryConsume(target, target.InstanceId))
        {
            target.Text = value;
            return;
        }

        var assignment = provenance.BeginExternalSetter(target, target.InstanceId);
        target.Text = value;
        Assert.False(TmpRestoreDecision.Resolve(
            provenance,
            assignment,
            value,
            TmpTextAssignmentOrigin.ExternalSetter,
            _ => "確認する") != value);
    }

    private static bool TryRestore(
        TmpTranslationProvenance provenance,
        FakeTmp target,
        string value)
    {
        var refresh = provenance.BeginPluginRefresh(target, target.InstanceId);
        return TmpRestoreDecision.Resolve(
            provenance,
            refresh,
            value,
            TmpTextAssignmentOrigin.PluginRefresh,
            translated => translated == "确定" ? "確認する" : null) != value;
    }

    private sealed class FakeTmp
    {
        public FakeTmp(int instanceId)
        {
            InstanceId = instanceId;
        }

        public int InstanceId { get; }
        public string Text { get; set; } = string.Empty;
    }
}
