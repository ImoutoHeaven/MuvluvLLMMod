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
    public void Refresh_time_nested_external_assignment_is_not_overwritten_when_display_is_off()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;
        const string source = "更新確認する";
        const string translated = "更新确认";
        Assert.True(cache.StoreGenerated(source, translated));

        var label = new TMP_Text();
        Config.Translation.Value = true;
        label.text = source;
        Assert.Equal(translated, label.text);

        Config.Translation.Value = false;
        Config.DebugLogSeenText.Value = true;
        var nested = 0;
        Plugin.Log.OnLog = _ =>
        {
            if (Interlocked.Exchange(ref nested, 1) == 0)
                label.text = "外部中文";
        };

        try
        {
            Patch.RefreshAllTmpText();
        }
        finally
        {
            Plugin.Log.OnLog = null;
        }

        Assert.Equal(1, nested);
        Assert.Equal("外部中文", label.text);
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
    public void Over_budget_generated_expansion_stays_source_even_when_f2_is_off()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        Config.Translation.Value = true;
        var cache = Plugin.CurrentCache!;
        var source = "スキル " + new string('9', 100);
        var normalized = TextTemplate.Normalize(source);
        var translatedTemplate = new string('中', 4080) + "{0}";
        Assert.True(cache.StoreGenerated(normalized.Template, translatedTemplate));

        var label = new TMP_Text();
        label.text = source;
        Assert.Equal(source, label.text);
        Assert.False(cache.TryGetGenerated(normalized.Template, out _));
        Assert.Equal(new[] { normalized.Template }, cache.PendingSnapshot());

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

    [Fact]
    public void Periodic_refresh_scans_only_active_tmp_objects()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;

        var activeLabel = new TMP_Text();
        var disabledComponentOnActiveObject = new TMP_Text { enabled = false, gameObjectActive = true };
        var inactiveLabel = new TMP_Text { gameObjectActive = false };
        activeLabel.text = "有効な表示です";
        disabledComponentOnActiveObject.text = "無効コンポーネントです";
        inactiveLabel.text = "非表示の項目です";

        Assert.True(cache.StoreGenerated("有効な表示です", "有效显示"));
        Assert.True(cache.StoreGenerated("無効コンポーネントです", "禁用组件仍属于活动对象"));
        Assert.True(cache.StoreGenerated("非表示の項目です", "隐藏项目"));

        Patch.RefreshAllTmpText();
        Assert.Equal("有效显示", activeLabel.text);
        Assert.Equal("禁用组件仍属于活动对象", disabledComponentOnActiveObject.text);
        Assert.Equal("非表示の項目です", inactiveLabel.text);

        inactiveLabel.gameObjectActive = true;
        Patch.RefreshAllTmpText();
        Assert.Equal("隐藏项目", inactiveLabel.text);
    }

    [Fact]
    public void Sentence_fallback_is_not_baked_into_frame_documents_while_llm_is_off_so_reentry_upgrades_it()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;
        var resolver = Plugin.CurrentResolver!;
        var retries = Plugin.CurrentSceneRetries!;

        const string kanaFreeFallbackSource = "最初の台詞です";
        const string kanaBearingFallbackSource = "ファリスの声です";
        Assert.True(cache.ObservePriority(kanaFreeFallbackSource, kanaFreeFallbackSource));
        Assert.True(cache.TryGetPendingGeneration(kanaFreeFallbackSource, out var gen1));
        Assert.True(cache.StoreGeneratedIfPending(kanaFreeFallbackSource, gen1, "第一句逐句译文"));

        Assert.True(cache.ObservePriority(kanaBearingFallbackSource, kanaBearingFallbackSource));
        Assert.True(cache.TryGetPendingGeneration(kanaBearingFallbackSource, out var gen2));
        Assert.True(cache.StoreGeneratedIfPending(kanaBearingFallbackSource, gen2, "ファリス的声音"));

        var masters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                ConfigurationJson = "{\"Phrase\":{\"Text\":\"最初の台詞です\",\"SpeakerName\":\"純夏\"}}"
            },
            new Assets.Api.Client.SceneFrameMaster
            {
                ConfigurationJson = "{\"Phrase\":{\"Text\":\"ファリスの声です\",\"SpeakerName\":\"ファリス\"}}"
            });

        // Re-entry while LLM is disabled must leave sentence fallbacks out of ConfigurationJson
        // (the TMP render path still serves them), so re-enabling LLM on the same cached masters
        // targets both lines for scene-level upgrade.
        var whileOff = SceneTranslationCoordinator.Prepare(
            masters,
            40000201,
            sceneTranslationEnabled: true,
            displayEnabled: true,
            llmEnabled: false,
            cache,
            resolver,
            retries);
        Assert.Null(whileOff);
        Assert.Contains("最初の台詞です", masters[0].ConfigurationJson, StringComparison.Ordinal);
        Assert.Contains("ファリスの声です", masters[1].ConfigurationJson, StringComparison.Ordinal);

        var whileOn = SceneTranslationCoordinator.Prepare(
            masters,
            40000201,
            sceneTranslationEnabled: true,
            displayEnabled: true,
            llmEnabled: true,
            cache,
            resolver,
            retries);
        Assert.NotNull(whileOn);
        Assert.Equal(new[] { 0, 1 }, whileOn!.Value.TargetIndices);
    }

    [Fact]
    public void Scene_translation_upgrades_displayed_sentence_fallback_and_preserves_f2_source_restore()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;

        const string source = "ファリスの声";
        Assert.True(cache.ObservePriority(source, source));
        Assert.True(cache.TryGetPendingGeneration(source, out var pendingGeneration));
        Assert.True(cache.StoreGeneratedIfPending(source, pendingGeneration, "法里斯的声音"));

        var label = new TMP_Text();
        label.text = source;
        Assert.Equal("法里斯的声音", label.text);

        // Authoritative scene batch completes and overrides the earlier sentence fallback.
        Assert.True(cache.StoreGenerated(source, "法莉丝的声音"));
        Patch.RefreshAllTmpText();
        Assert.Equal("法莉丝的声音", label.text);

        // F2 display-off must still restore the original Japanese source, not the intermediate
        // sentence translation.
        Config.Translation.Value = false;
        Patch.RefreshAllTmpText();
        Assert.Equal(source, label.text);
    }

    [Fact]
    public void Active_scene_reservation_holds_displayed_line_from_duplicate_per_string_enqueue()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;

        var machine = new MachineTranslator(
            cache,
            static (_, _) => Task.FromResult<string?>("逐句译文"),
            maxInFlight: 2,
            translatePeriod: TimeSpan.FromSeconds(10),
            sendScenePrompt: async (_, onDispatched, ct) =>
            {
                onDispatched?.Invoke();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new ScenePromptResponse(false, null);
            });
        machine.Start();
        try
        {
            var resolver = new TranslationResolver(
                cache,
                (template, gen) => machine.EnqueuePriority(template, gen),
                (template, gen) => machine.EnqueueNormal(template, gen),
                (template, gen) => machine.Cancel(template, gen),
                machine.IsReserved);

            var lines = new[] { new SceneDialogueLine("予約された台詞です", "純夏") };
            Assert.Equal(1, machine.SubmitScene(40000101, lines, new[] { 0 }));
            Assert.True(machine.IsReserved("予約された台詞です"));

            Assert.Equal("予約された台詞です", resolver.Resolve("予約された台詞です", priority: true));
            Assert.Empty(cache.PendingSnapshot());
        }
        finally
        {
            machine.Stop();
        }
    }

    private static class FakeSkillDescriptionBuilder
    {
        public static string GetDescription() => "技能を説明する";
    }
}
