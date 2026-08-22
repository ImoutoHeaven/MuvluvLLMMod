using HarmonyLib;
using TMPro;
using Xunit;

namespace MuvluvLLMMod.IntegrationTests;

public sealed class ProductionRenderIntegrationTests
{
    [Fact]
    public void Real_prefix_and_fake_setter_fail_closed_for_nested_external_and_stale_lifecycle_writes()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;
        const string source = "確認する";
        const string translated = "确定";
        Assert.True(cache.StoreGenerated(source, translated));

        var victim = new TMP_Text();
        var other = new TMP_Text();
        Config.DebugLogSeenText.Value = true;
        var nested = 0;
        Plugin.Log.OnLog = _ =>
        {
            if (Interlocked.Exchange(ref nested, 1) != 0)
                return;

            // Both calls are synchronous, unguarded assignments reached while the real Prefix
            // is still resolving/logging the outer setter. They intentionally use equal content.
            other.text = translated;
            victim.text = translated;
        };

        victim.text = source;
        Plugin.Log.OnLog = null;
        Assert.Equal(translated, victim.text);
        Assert.Equal(translated, other.text);

        Config.Translation.Value = false;
        Patch.RefreshAllTmpText();
        Assert.Equal(translated, victim.text);
        Assert.Equal(translated, other.text);

        // A setter with no intervening external write remains eligible for the guarded refresh
        // path and restores exactly once.
        var owned = new TMP_Text();
        Config.Translation.Value = true;
        owned.text = source;
        Assert.Equal(translated, owned.text);
        Config.Translation.Value = false;
        Patch.RefreshAllTmpText();
        Assert.Equal(source, owned.text);

        // Retire the first generation, write during the unpatched window, and reload. The old
        // provenance must not revive even though object identity and value are unchanged.
        var stale = new TMP_Text();
        Config.Translation.Value = true;
        stale.text = source;
        Assert.Equal(translated, stale.text);
        Assert.True(fixture.Plugin.Unload());

        stale.text = translated;
        Assert.Equal(translated, stale.text);

        fixture.Plugin.Load();
        Config.Translation.Value = false;
        Patch.RefreshAllTmpText();
        Assert.Equal(translated, stale.text);
    }

    [Fact]
    public void Skill_description_is_not_pretranslated_and_uses_the_single_final_tmp_path()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;
        const string source = "技能を説明する";
        const string translated = "技能说明";
        Assert.True(cache.StoreGenerated(source, translated));

        var description = FakeSkillDescriptionBuilder.GetDescription();
        Assert.Equal(source, description);

        var label = new TMP_Text();
        TMP_Text.ResetCounters();
        label.text = description;
        Assert.Equal(1, TMP_Text.SetterCalls);
        Assert.Equal(translated, label.text);

        Config.Translation.Value = false;
        Patch.RefreshAllTmpText();
        Assert.Equal(source, label.text);
    }

    [Fact]
    public void Missing_required_patch_verification_rolls_back_before_hotkey_persistence_or_worker_start()
    {
        var plugin = ProductionHarness.PreparePluginForLoad();
        Harmony.SkipPatchTarget = "TMPro.TMP_Text.set_text";

        try
        {
            var exception = Record.Exception(plugin.Load);
            try
            {
                Assert.IsType<HarmonyPatchVerificationException>(exception);
                Assert.Equal(0, plugin.AddComponentCalls);
                Assert.Equal(0, UnityEngine.Application.CallbackCount);
                Assert.Null(Plugin.Instance);
                Assert.Null(Plugin.CurrentCache);
                Assert.Null(Plugin.CurrentResolver);
                Assert.Equal(1, Harmony.UnpatchSelfCalls);
            }
            finally
            {
                if (Plugin.Instance != null || UnityEngine.Application.CallbackCount != 0)
                    _ = plugin.Unload();
            }
        }
        finally
        {
            Harmony.SkipPatchTarget = null;
        }
    }

    private static class FakeSkillDescriptionBuilder
    {
        public static string GetDescription() => "技能を説明する";
    }
}
