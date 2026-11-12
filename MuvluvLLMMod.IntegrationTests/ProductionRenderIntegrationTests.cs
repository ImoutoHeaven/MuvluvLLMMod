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

    [Fact]
    public void Async_scene_completion_live_updates_active_and_future_frames_histories_and_f2_without_typewriter_queue_flood()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;

        var controller = new Assets.GameUi.Scenario.ScenarioController
        {
            sceneMasterId = 50001101,
            scenarioService = new FakeScenarioService("指揮官"),
        };
        var sentenceTmp = new TMP_Text();
        var ruby = new global::TextMeshProRuby { tmpText = sentenceTmp, fixedLineHeight = true };
        var textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent { sentenceText = ruby };
        var textTimer = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
        var scenarioComponent = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = controller,
            textView = textView,
            textTimer = textTimer,
        };
        var speakerTmp = new TMP_Text();

        const string source1 = "<r=ダイブ>潜航</r>開始、%usernameusernameuserna%！";
        const string source2 = "何をしてるんだ、あんなところで……？";
        var masters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 101,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"<r=ダイブ>潜航</r>開始、%usernameusernameuserna%！","SpeakerName":"リディア"}}"""
            },
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 102,
                Order = 2,
                ConfigurationJson = """{"Phrase":{"Text":"何をしてるんだ、あんなところで……？","SpeakerName":"白銀"}}"""
            });

        controller.frames = controller.GenerateFrames(masters, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        controller.Refresh();

        var frame1 = controller.GenerateFrame(new() { Current = 101 });
        speakerTmp.text = frame1.Text!.Name;
        scenarioComponent.HandoffFrameText(frame1);

        // Typewriter partial ticks and ruby/<line-height> expansion on sentenceText must never enter pending.
        Assert.True(textTimer.StepTick(1));
        Assert.True(textTimer.StepTick(2));
        Assert.False(textTimer.Disposed);
        Assert.Equal(0, controller.CompletedTextCount);
        Assert.Equal(
            "<line-height=2.000em>潜航<space=-1.000em><voffset=1em><size=50%>ダイブ</size></voffset>開",
            sentenceTmp.text);
        Assert.Equal(new[] { "リディア" }, cache.PendingSnapshot());

        // Chunk completes while Frame 1's typewriter is still active (!Disposed): timer.Apply restarts on Chinese
        // without immediately emitting completion.
        Assert.True(cache.StoreGenerated(source1, "<r=ダイブ>潜航</r>开始，%usernameusernameuserna%！"));
        Assert.True(cache.StoreGenerated(source2, "在那种地方做什么呢……？"));
        Assert.True(cache.StoreGenerated("リディア", "莉迪亚"));

        Patch.RefreshAllTmpText();
        Assert.Equal(2, textTimer.ApplyCalls);
        Assert.False(textTimer.Disposed);
        Assert.Equal(0, controller.CompletedTextCount);
        Assert.Equal("潜航开始，指揮官！", controller.histories[0].Text);
        Assert.Equal("莉迪亚", speakerTmp.text);

        // Repeated refresh before the restarted timer emits tick 1 must not reset the timer again.
        Patch.RefreshAllTmpText();
        Assert.Equal(2, textTimer.ApplyCalls);

        Assert.True(textTimer.StepTick(3));
        Assert.False(textTimer.Disposed);
        Assert.Equal(0, controller.CompletedTextCount);
        Assert.Equal(
            "<line-height=2.000em>潜航<space=-1.000em><voffset=1em><size=50%>ダイブ</size></voffset>开",
            sentenceTmp.text);

        textTimer.Complete();
        Assert.True(textTimer.Disposed);
        Assert.Equal(1, controller.CompletedTextCount);
        Assert.Equal(101L, controller.LastCompletedFrameId);
        Assert.Equal(
            "<line-height=2.000em>潜航<space=-1.000em><voffset=1em><size=50%>ダイブ</size></voffset>开始，指揮官！",
            sentenceTmp.text);

        // Once typing finishes (Disposed = true), pressing F2 updates sentenceText in-place via ApplySentence.
        var keyboard = new UnityEngine.InputSystem.Keyboard();
        UnityEngine.InputSystem.Keyboard.current = keyboard;
        keyboard[UnityEngine.InputSystem.Key.F2].wasPressedThisFrame = true;
        var update = typeof(Hotkey).GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        update.Invoke(Plugin.Instance, null);
        keyboard[UnityEngine.InputSystem.Key.F2].wasPressedThisFrame = false;

        Assert.False(Config.Translation.Value);
        Assert.Equal(2, textTimer.ApplyCalls);
        Assert.Equal(
            "<line-height=2.000em>潜航<space=-1.000em><voffset=1em><size=50%>ダイブ</size></voffset>開始、指揮官！",
            sentenceTmp.text);
        Assert.Equal("潜航開始、指揮官！", controller.histories[0].Text);
        Assert.Equal("リディア", speakerTmp.text);

        // Toggle F2 back on and step to Frame 2: Frame 2 is already translated before GenerateFrame runs.
        keyboard[UnityEngine.InputSystem.Key.F2].wasPressedThisFrame = true;
        update.Invoke(Plugin.Instance, null);
        keyboard[UnityEngine.InputSystem.Key.F2].wasPressedThisFrame = false;

        Assert.True(Config.Translation.Value);
        var frame2 = controller.GenerateFrame(new() { Current = 102 });
        Assert.Equal("在那种地方做什么呢……？", frame2.Text!.Sentence!.Text);
        Assert.Equal("在那种地方做什么呢……？", controller.histories[1].Text);
        scenarioComponent.HandoffFrameText(frame2);
        textTimer.Complete();
        Assert.Equal("<line-height=2.000em>在那种地方做什么呢……？", sentenceTmp.text);

        // Master documents were restored after GenerateFrames consumed them, keeping cached masters clean.
        Assert.Contains(source1, masters[0].ConfigurationJson, StringComparison.Ordinal);
        Assert.Contains(source2, masters[1].ConfigurationJson, StringComparison.Ordinal);

        controller.Leave();
        Assert.False(Patch.isPlayingScenario);
    }

    [Fact]
    public void Per_string_scenario_fallback_mode_enqueues_only_current_frame_source_without_ruby_or_typewriter_prefixes()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        Config.SceneTranslation.Value = false;
        var cache = Plugin.CurrentCache!;

        var controller = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001102 };
        var sentenceTmp = new TMP_Text();
        var ruby = new global::TextMeshProRuby { tmpText = sentenceTmp, fixedLineHeight = true };
        var textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent { sentenceText = ruby };
        var textTimer = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
        var scenarioComponent = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = controller,
            textView = textView,
            textTimer = textTimer,
        };

        const string source1 = "もうちょっと待ってね、ねこさん。";
        const string source2 = "次の台詞はまだ表示されていない。";
        var masters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 201,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"もうちょっと待ってね、ねこさん。"}}"""
            },
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 202,
                Order = 2,
                ConfigurationJson = """{"Phrase":{"Text":"次の台詞はまだ表示されていない。"}}"""
            });

        controller.frames = controller.GenerateFrames(masters, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        controller.Refresh();

        var frame1 = controller.GenerateFrame(new() { Current = 201 });
        scenarioComponent.HandoffFrameText(frame1);
        Assert.True(textTimer.StepTick(1));
        Assert.True(textTimer.StepTick(2));
        textTimer.Complete();
        Assert.Empty(cache.PendingSnapshot());

        Patch.RefreshAllTmpText();
        Assert.Equal(new[] { source1 }, cache.PendingSnapshot());
        Assert.DoesNotContain(source2, cache.PendingSnapshot());

        Assert.True(cache.TryGetPendingGeneration(source1, out var gen));
        Assert.True(cache.StoreGeneratedIfPending(source1, gen, "再稍微等一下哦，小猫咪。"));

        Patch.RefreshAllTmpText();
        Assert.Equal("<line-height=2.000em>再稍微等一下哦，小猫咪。", sentenceTmp.text);
        Assert.Equal("再稍微等一下哦，小猫咪。", controller.histories[0].Text);

        controller.Leave();
    }

    [Fact]
    public void Enabling_scene_translation_or_llm_production_mid_scene_queues_active_dialogue_without_stalling()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;

        // Case A: enter scene with SceneTranslation=false, enable SceneTranslation=true before first refresh.
        Config.SceneTranslation.Value = false;
        var controllerA = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001201 };
        var sentenceTmpA = new TMP_Text();
        var textViewA = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
        {
            sentenceText = new global::TextMeshProRuby { tmpText = sentenceTmpA, fixedLineHeight = true }
        };
        var textTimerA = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
        var componentA = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = controllerA,
            textView = textViewA,
            textTimer = textTimerA,
        };

        const string sourceA = "途中からシーン翻訳を有効化する台詞です。";
        var mastersA = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 301,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"途中からシーン翻訳を有効化する台詞です。"}}"""
            });

        controllerA.frames = controllerA.GenerateFrames(mastersA, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        controllerA.Refresh();
        var frameA = controllerA.GenerateFrame(new() { Current = 301 });
        componentA.HandoffFrameText(frameA);
        textTimerA.Complete();

        Config.SceneTranslation.Value = true;
        Patch.RefreshAllTmpText();
        Assert.Contains(sourceA, cache.PendingSnapshot());
        controllerA.Leave();

        // Case B: enter scene with LlmEnable=false (default) and SceneTranslation=true, observe with F2 off,
        // then enable LlmEnable and F2 on mid-scene.
        Config.LlmEnable.Value = false;
        Config.SceneTranslation.Value = true;
        Config.Translation.Value = false;

        var controllerB = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001202 };
        var sentenceTmpB = new TMP_Text();
        var textViewB = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
        {
            sentenceText = new global::TextMeshProRuby { tmpText = sentenceTmpB, fixedLineHeight = true }
        };
        var textTimerB = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
        var componentB = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = controllerB,
            textView = textViewB,
            textTimer = textTimerB,
        };

        const string sourceB = "途中から機械翻訳を有効化する台詞です。";
        var mastersB = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 302,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"途中から機械翻訳を有効化する台詞です。"}}"""
            });

        controllerB.frames = controllerB.GenerateFrames(mastersB, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        controllerB.Refresh();
        var frameB = controllerB.GenerateFrame(new() { Current = 302 });
        componentB.HandoffFrameText(frameB);
        textTimerB.Complete();

        Config.LlmEndpoint.Value = "http://127.0.0.1:1/v1/chat/completions";
        Config.LlmEnable.Value = true;
        Config.Translation.Value = true;
        Patch.RefreshAllTmpText();

        Assert.True(cache.TryGetPendingGeneration(sourceB, out var genB));
        Assert.True(cache.StoreGeneratedIfPending(sourceB, genB, "中途开启机翻的台词。"));
        Patch.RefreshAllTmpText();
        Assert.Equal("<line-height=2.000em>中途开启机翻的台词。", sentenceTmpB.text);
        controllerB.Leave();
    }

    [Fact]
    public void Pre_handoff_frame_transition_updates_target_model_without_overwriting_previous_view_or_interrupting_active_timer()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;

        var controller = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001301 };
        var sentenceTmp = new TMP_Text();
        var textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
        {
            sentenceText = new global::TextMeshProRuby { tmpText = sentenceTmp, fixedLineHeight = true }
        };
        var textTimer = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
        var component = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = controller,
            textView = textView,
            textTimer = textTimer,
        };

        const string source1 = "最初の台詞を表示中です。";
        const string source2 = "ああ、フェード後の次の台詞です。";
        const string source3 = "ああ、タイマー動作中のさらに次の台詞です。";
        var masters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 401,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"最初の台詞を表示中です。"}}"""
            },
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 402,
                Order = 2,
                ConfigurationJson = """{"Phrase":{"Text":"ああ、フェード後の次の台詞です。"}}"""
            },
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 403,
                Order = 3,
                ConfigurationJson = """{"Phrase":{"Text":"ああ、タイマー動作中のさらに次の台詞です。"}}"""
            });

        Assert.True(cache.StoreGenerated(source1, "正在显示第一句台词。"));
        controller.frames = controller.GenerateFrames(masters, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        controller.Refresh();

        var frame1 = controller.GenerateFrame(new() { Current = 401 });
        component.HandoffFrameText(frame1);
        textTimer.Complete();
        Assert.Equal("<line-height=2.000em>正在显示第一句台词。", sentenceTmp.text);
        Assert.Equal(1, controller.CompletedTextCount);

        // Begin transitioning to Frame 2: GenerateFrame sets Current=402 and creates frame2.Text BEFORE
        // ProcessFrame reaches the textView/textTimer handoff.
        var frame2 = controller.GenerateFrame(new() { Current = 402 });
        Assert.True(cache.StoreGenerated(source2, "啊啊，淡入淡出后的下一句台词。"));

        var applyCallsBeforeFadeSync = textTimer.ApplyCalls;
        Patch.RefreshAllTmpText();

        // Target model and history update in advance without marking AtEnd or leaking into Frame 1's view.
        Assert.Equal("啊啊，淡入淡出后的下一句台词。", frame2.Text!.Sentence!.Text);
        Assert.False(frame2.Text.Sentence.AtEnd);
        Assert.Equal("啊啊，淡入淡出后的下一句台词。", controller.histories[1].Text);
        Assert.Equal("<line-height=2.000em>正在显示第一句台词。", sentenceTmp.text);
        Assert.Equal(applyCallsBeforeFadeSync, textTimer.ApplyCalls);
        Assert.Equal(1, controller.CompletedTextCount);

        // NeedsHideTextView clearing the view during fade also stays blank across refresh until handoff.
        component.ClearTextView();
        Patch.RefreshAllTmpText();
        Assert.Equal("<line-height=2.000em>", sentenceTmp.text);
        Assert.False(frame2.Text.Sentence.AtEnd);

        // Actual ProcessFrame handoff now starts Frame 2's typewriter with the pre-updated Chinese text.
        component.HandoffFrameText(frame2);
        Assert.True(textTimer.StepTick(1));
        Assert.Equal("<line-height=2.000em>啊", sentenceTmp.text);
        Assert.Equal(1, controller.CompletedTextCount);

        // Subcase 2 (shared prefix): GenerateFrame(403) runs while Frame 2's timer is still active (!Disposed).
        // Even when Frame 2 emits "啊啊" (which is also a prefix of Frame 3's source and translation),
        // native TextViewModel identity keeps Frame 3 pre-handoff until HandoffFrameText(frame3).
        var frame3 = controller.GenerateFrame(new() { Current = 403 });
        Assert.True(cache.StoreGenerated(source3, "啊啊，打字机运行中的再下一句台词。"));
        var applyCallsWhileFrame2Typing = textTimer.ApplyCalls;

        Patch.RefreshAllTmpText();
        Assert.True(textTimer.StepTick(1));
        Patch.RefreshAllTmpText();

        Assert.Equal(applyCallsWhileFrame2Typing, textTimer.ApplyCalls);
        Assert.Equal("<line-height=2.000em>啊啊", sentenceTmp.text);
        Assert.Equal("啊啊，打字机运行中的再下一句台词。", frame3.Text!.Sentence!.Text);
        Assert.False(frame3.Text.Sentence.AtEnd);
        Assert.Equal(1, controller.CompletedTextCount);

        // Hand off Frame 3: normal typing and completion now run on Frame 3's translated text.
        component.HandoffFrameText(frame3);
        Assert.True(textTimer.StepTick(4));
        Assert.Equal("<line-height=2.000em>啊啊，打", sentenceTmp.text);
        textTimer.Complete();
        Assert.Equal("<line-height=2.000em>啊啊，打字机运行中的再下一句台词。", sentenceTmp.text);
        Assert.Equal(2, controller.CompletedTextCount);
        Assert.Equal(403L, controller.LastCompletedFrameId);

        controller.Leave();
    }

    [Fact]
    public void Pure_kanji_and_noncandidate_scene_frames_never_reverse_recover_unrelated_cached_sources_on_f2()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;

        Assert.True(cache.StoreGenerated("提供の割合", "提供割合"));
        Assert.True(cache.StoreGenerated("確率表示", "机率公示"));
        Assert.True(cache.StoreGenerated("確認をお願いします", "请确认"));

        var controller = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001401 };
        var sentenceTmp = new TMP_Text();
        var textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
        {
            sentenceText = new global::TextMeshProRuby { tmpText = sentenceTmp, fixedLineHeight = true }
        };
        var textTimer = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
        var component = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = controller,
            textView = textView,
            textTimer = textTimer,
        };

        var masters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 501,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"提供割合"}}"""
            },
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 502,
                Order = 2,
                ConfigurationJson = """{"Phrase":{"Text":"机率公示"}}"""
            },
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 503,
                Order = 3,
                ConfigurationJson = """{"Phrase":{"Text":"確認をお願いします"}}"""
            });

        controller.frames = controller.GenerateFrames(masters, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        controller.Refresh();

        var frame1 = controller.GenerateFrame(new() { Current = 501 });
        component.HandoffFrameText(frame1);
        textTimer.Complete();
        Assert.Equal("<line-height=2.000em>提供割合", sentenceTmp.text);

        var frame2 = controller.GenerateFrame(new() { Current = 502 });
        component.HandoffFrameText(frame2);
        textTimer.Complete();
        Assert.Equal("<line-height=2.000em>机率公示", sentenceTmp.text);

        Config.Translation.Value = false;
        Patch.RefreshAllTmpText();

        Assert.Equal("提供割合", controller.frames[0].PhraseConfiguration!.Text);
        Assert.Equal("提供割合", controller.histories[0].Text);
        Assert.Equal("机率公示", controller.frames[1].PhraseConfiguration!.Text);
        Assert.Equal("机率公示", controller.histories[1].Text);
        Assert.Equal("<line-height=2.000em>机率公示", sentenceTmp.text);
        Assert.Equal("確認をお願いします", controller.frames[2].PhraseConfiguration!.Text);

        controller.Leave();
    }

    [Fact]
    public async Task Concurrent_retirement_during_scene_prepare_bind_or_sync_rejects_stale_roots_and_restores_masters()
    {
        var pendingField = typeof(Patch).GetField(
            "pendingMasterSnapshots",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var activeField = typeof(Patch).GetField(
            "activeScenario",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        // 1. Retirement while TranslateSceneFrames is mid-Prepare: local snapshots restore and pendingMasterSnapshots stays null.
        using (var fixture = ProductionHarness.LoadPlugin())
        {
            Assert.True(Plugin.CurrentCache!.StoreGenerated("準備中の台詞です", "准备中的台词"));

            var enteredPrepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePrepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var master1 = new Assets.Api.Client.SceneFrameMaster
            {
                Id = 601,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"準備中の台詞です"}}"""
            };
            var master2 = new Assets.Api.Client.SceneFrameMaster
            {
                Id = 602,
                Order = 2,
                ConfigurationJson = """{"Phrase":{"Text":"二番目の台詞です"}}"""
            };
            var reads = 0;
            master2.OnReadConfigurationJson = () =>
            {
                // First read captures snapshots; second read is inside SceneTranslationCoordinator.Prepare
                // after master1 has already been rewritten to Chinese.
                if (Interlocked.Increment(ref reads) == 2)
                {
                    enteredPrepare.TrySetResult();
                    releasePrepare.Task.GetAwaiter().GetResult();
                }
            };

            var masters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
                master1,
                master2);
            var controller = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001501 };

            var generateTask = Task.Run(() =>
                controller.GenerateFrames(masters, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None));
            await enteredPrepare.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Prove Prepare genuinely rewrote master1 before retirement occurred.
            Assert.Contains("准备中的台词", master1.ConfigurationJson, StringComparison.Ordinal);

            Assert.True(fixture.Plugin.Unload());
            releasePrepare.TrySetResult();
            _ = await generateTask.WaitAsync(TimeSpan.FromSeconds(2));

            master2.OnReadConfigurationJson = null;
            Assert.Null(pendingField.GetValue(null));
            Assert.Null(activeField.GetValue(null));
            Assert.Contains("準備中の台詞です", master1.ConfigurationJson, StringComparison.Ordinal);
        }

        // 2. Stale SetIsPlayingScenario paused before BindActiveScenario cannot erase a replacement generation's scene.
        using (var fixture = ProductionHarness.LoadPlugin())
        {
            Assert.True(Plugin.CurrentCache!.StoreGenerated("旧世代の台詞です", "旧世代台词"));
            var staleMaster = new Assets.Api.Client.SceneFrameMaster
            {
                Id = 611,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"旧世代の台詞です"}}"""
            };
            var staleMasters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(staleMaster);
            var staleController = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001502 };
            staleController.frames = staleController.GenerateFrames(staleMasters, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);

            var enteredPreBind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePreBind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            staleMaster.OnWriteConfigurationJson = () =>
            {
                enteredPreBind.TrySetResult();
                releasePreBind.Task.GetAwaiter().GetResult();
            };

            var staleRefreshTask = Task.Run(staleController.Refresh);
            await enteredPreBind.Task.WaitAsync(TimeSpan.FromSeconds(2));
            staleMaster.OnWriteConfigurationJson = null;

            Assert.True(fixture.Plugin.Unload());
            fixture.Plugin.Load();

            var replacementMasters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
                new Assets.Api.Client.SceneFrameMaster
                {
                    Id = 612,
                    Order = 1,
                    ConfigurationJson = """{"Phrase":{"Text":"新世代の台詞です"}}"""
                });
            var replacementController = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001504 };
            replacementController.frames = replacementController.GenerateFrames(
                replacementMasters,
                Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
            replacementController.Refresh();

            var replacementActive = activeField.GetValue(null);
            Assert.NotNull(replacementActive);
            Assert.True(Patch.isPlayingScenario);

            // Releasing the stale pre-bind call must not clear replacementActive or isPlayingScenario.
            releasePreBind.TrySetResult();
            await staleRefreshTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Same(replacementActive, activeField.GetValue(null));
            Assert.True(Patch.isPlayingScenario);

            // An uncommitted bind that faults before publishing must not clear an already-published same-epoch scene.
            var faultingMaster = new Assets.Api.Client.SceneFrameMaster
            {
                Id = 613,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"例外発生前の台詞です"}}"""
            };
            var faultingController = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001505 };
            faultingController.frames = faultingController.GenerateFrames(
                new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(faultingMaster),
                Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);

            var enteredFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            faultingMaster.OnReadConfigurationJson = () =>
            {
                enteredFault.TrySetResult();
                releaseFault.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("simulated interop fault before publish");
            };

            var faultTask = Task.Run(faultingController.Refresh);
            await enteredFault.Task.WaitAsync(TimeSpan.FromSeconds(2));

            replacementController.Refresh();
            var rePublishedActive = activeField.GetValue(null);
            Assert.NotNull(rePublishedActive);

            releaseFault.TrySetResult();
            await faultTask.WaitAsync(TimeSpan.FromSeconds(2));
            faultingMaster.OnReadConfigurationJson = null;

            Assert.Same(rePublishedActive, activeField.GetValue(null));
            replacementController.Leave();
        }

        // 3. Re-entrant retirement inside UpdatePhraseDisplayText stops history, sentence model, and view writes immediately.
        using (var fixture = ProductionHarness.LoadPlugin())
        {
            var armed = false;
            var unloaded = false;
            var controller = new Assets.GameUi.Scenario.ScenarioController
            {
                sceneMasterId = 50001503,
                scenarioService = new FakeScenarioService(
                    "指揮官",
                    () =>
                    {
                        if (armed && !unloaded)
                        {
                            unloaded = true;
                            Assert.True(fixture.Plugin.Unload());
                        }
                    }),
            };
            var sentenceTmp = new TMP_Text();
            var textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
            {
                sentenceText = new global::TextMeshProRuby { tmpText = sentenceTmp, fixedLineHeight = true }
            };
            var textTimer = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
            var component = new Assets.GameUi.Scenario.ScenarioComponent
            {
                Controller = controller,
                textView = textView,
                textTimer = textTimer,
            };

            var masters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
                new Assets.Api.Client.SceneFrameMaster
                {
                    Id = 621,
                    Order = 1,
                    ConfigurationJson = """{"Phrase":{"Text":"最初の同期台詞です"}}"""
                },
                new Assets.Api.Client.SceneFrameMaster
                {
                    Id = 622,
                    Order = 2,
                    ConfigurationJson = """{"Phrase":{"Text":"停止後の二番目です"}}"""
                });

            controller.frames = controller.GenerateFrames(masters, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
            controller.Refresh();
            var frame1 = controller.GenerateFrame(new() { Current = 621 });
            component.HandoffFrameText(frame1);
            textTimer.Complete();

            var cache = Plugin.CurrentCache!;
            Assert.True(cache.StoreGenerated("最初の同期台詞です", "第一句同步译文"));
            Assert.True(cache.StoreGenerated("停止後の二番目です", "停止后的第二句译文"));

            armed = true;
            Patch.RefreshAllTmpText();

            Assert.True(unloaded);
            Assert.Null(activeField.GetValue(null));
            Assert.Equal("最初の同期台詞です", controller.histories[0].Text);
            Assert.Equal("最初の同期台詞です", frame1.Text!.Sentence!.Text);
            Assert.Equal("<line-height=2.000em>最初の同期台詞です", sentenceTmp.text);
            Assert.Equal("停止後の二番目です", controller.frames[1].PhraseConfiguration!.Text);
        }
    }

    [Fact]
    public void Component_resolution_ignores_unrelated_or_disabled_components_and_binds_only_matching_controller()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;

        var otherController = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001601 };
        var otherTmp = new TMP_Text();
        var otherComponent = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = otherController,
            textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
            {
                sentenceText = new global::TextMeshProRuby { tmpText = otherTmp, fixedLineHeight = true }
            },
            textTimer = new Assets.GameUi.Scenario.Text.ScenarioTextTimer(),
        };

        var activeController = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001602 };
        var disabledTmp = new TMP_Text();
        _ = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = activeController,
            enabled = false,
            textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
            {
                sentenceText = new global::TextMeshProRuby { tmpText = disabledTmp, fixedLineHeight = true }
            },
            textTimer = new Assets.GameUi.Scenario.Text.ScenarioTextTimer(),
        };

        var ownedTmp = new TMP_Text();
        var ownedTimer = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
        var ownedComponent = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = activeController,
            textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
            {
                sentenceText = new global::TextMeshProRuby { tmpText = ownedTmp, fixedLineHeight = true }
            },
            textTimer = ownedTimer,
        };

        otherTmp.text = "別の画面テキスト";
        Assert.Contains("別の画面テキスト", cache.PendingSnapshot());

        const string source = "正しいコンポーネントの台詞です。";
        var masters = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 701,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"正しいコンポーネントの台詞です。"}}"""
            });

        activeController.frames = activeController.GenerateFrames(masters, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        activeController.Refresh();

        var frame = activeController.GenerateFrame(new() { Current = 701 });
        ownedComponent.HandoffFrameText(frame);
        Assert.True(ownedTimer.StepTick(2));
        Assert.Equal(new[] { "別の画面テキスト" }, cache.PendingSnapshot());

        ownedTimer.Complete();
        Assert.True(cache.StoreGenerated(source, "正确组件的台词。"));
        Patch.RefreshAllTmpText();

        Assert.Equal("別の画面テキスト", otherTmp.text);
        Assert.Equal(0, otherComponent.textTimer!.ApplyCalls);
        Assert.Equal("<line-height=2.000em>正确组件的台词。", ownedTmp.text);

        activeController.Leave();
    }

    [Fact]
    public async Task Leaving_superseded_controller_preserves_replacement_scene_sync_guard_snapshots_and_invalidates_same_epoch_inflight_bind()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var cache = Plugin.CurrentCache!;
        var pendingField = typeof(Patch).GetField(
            "pendingMasterSnapshots",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var activeField = typeof(Patch).GetField(
            "activeScenario",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        Assert.True(cache.StoreGenerated("置換先の事前翻訳台詞です。", "替换场景的预译台词。"));

        // 1. Controller A binds; Controller B runs GenerateFrames; A.Leave() must not detach B's pending snapshots.
        var controllerA = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001701 };
        var mastersA = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 801,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"旧コントローラの台詞です。"}}"""
            });
        controllerA.frames = controllerA.GenerateFrames(mastersA, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        controllerA.Refresh();
        Assert.True(Patch.isPlayingScenario);

        var controllerB = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001702 };
        var tmpB = new TMP_Text();
        var timerB = new Assets.GameUi.Scenario.Text.ScenarioTextTimer();
        var componentB = new Assets.GameUi.Scenario.ScenarioComponent
        {
            Controller = controllerB,
            textView = new Assets.GameUi.Scenario.Text.ScenarioTextComponent
            {
                sentenceText = new global::TextMeshProRuby { tmpText = tmpB, fixedLineHeight = true }
            },
            textTimer = timerB,
        };
        var mastersB = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 811,
                Order = 1,
                ConfigurationJson = """{"Phrase":{"Text":"置換先の事前翻訳台詞です。"}}"""
            },
            new Assets.Api.Client.SceneFrameMaster
            {
                Id = 812,
                Order = 2,
                ConfigurationJson = """{"Phrase":{"Text":"置換先の未翻訳台詞です。"}}"""
            });

        controllerB.frames = controllerB.GenerateFrames(mastersB, Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);
        var pendingBatchForB = pendingField.GetValue(null);
        Assert.NotNull(pendingBatchForB);

        controllerA.Leave();
        Assert.Same(pendingBatchForB, pendingField.GetValue(null));

        // 2. Re-bind A so activeScenario=A, start B.Refresh() and pause during B's snapshot restoration
        // before BindActiveScenario, then run A.Leave(): A clears its own activeScenario without
        // invalidating B's pending bind sequence/pointer.
        controllerA.Refresh();
        Assert.NotNull(activeField.GetValue(null));
        Assert.Same(pendingBatchForB, pendingField.GetValue(null));

        var enteredRestoreB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestoreB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mastersB[0].OnWriteConfigurationJson = () =>
        {
            enteredRestoreB.TrySetResult();
            releaseRestoreB.Task.GetAwaiter().GetResult();
        };

        var refreshB = Task.Run(controllerB.Refresh);
        await enteredRestoreB.Task.WaitAsync(TimeSpan.FromSeconds(2));
        mastersB[0].OnWriteConfigurationJson = null;

        controllerA.Leave();
        Assert.Null(activeField.GetValue(null));

        releaseRestoreB.TrySetResult();
        await refreshB.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(pendingField.GetValue(null));
        Assert.Contains("置換先の事前翻訳台詞です。", mastersB[0].ConfigurationJson, StringComparison.Ordinal);
        var activeB = activeField.GetValue(null);
        Assert.NotNull(activeB);
        Assert.True(Patch.isPlayingScenario);

        controllerA.Leave();
        Assert.Same(activeB, activeField.GetValue(null));
        Assert.True(Patch.isPlayingScenario);

        var frameB2 = controllerB.GenerateFrame(new() { Current = 812 });
        componentB.HandoffFrameText(frameB2);
        Assert.True(timerB.StepTick(2));
        Assert.Empty(cache.PendingSnapshot());

        timerB.Complete();
        Assert.True(cache.StoreGenerated("置換先の未翻訳台詞です。", "替换场景的后到译文。"));
        Patch.RefreshAllTmpText();
        Assert.Equal("<line-height=2.000em>替换场景的后到译文。", tmpB.text);

        controllerB.Leave();
        Assert.False(Patch.isPlayingScenario);
        Assert.Null(activeField.GetValue(null));

        // 3. Same-epoch Leave() while C.Refresh() is blocked mid-bind prevents C from republishing after Leave().
        var masterC = new Assets.Api.Client.SceneFrameMaster
        {
            Id = 821,
            Order = 1,
            ConfigurationJson = """{"Phrase":{"Text":"同一世代の途中離脱台詞です。"}}"""
        };
        var controllerC = new Assets.GameUi.Scenario.ScenarioController { sceneMasterId = 50001703 };
        controllerC.frames = controllerC.GenerateFrames(
            new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster>(masterC),
            Assets.GameUi.Scenario.ScenarioController.FunctionFlags.None);

        var enteredBindC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBindC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        masterC.OnReadConfigurationJson = () =>
        {
            enteredBindC.TrySetResult();
            releaseBindC.Task.GetAwaiter().GetResult();
        };

        var refreshC = Task.Run(controllerC.Refresh);
        await enteredBindC.Task.WaitAsync(TimeSpan.FromSeconds(2));

        controllerC.Leave();
        releaseBindC.TrySetResult();
        await refreshC.WaitAsync(TimeSpan.FromSeconds(2));
        masterC.OnReadConfigurationJson = null;

        Assert.False(Patch.isPlayingScenario);
        Assert.Null(activeField.GetValue(null));
    }

    private sealed class FakeScenarioService : Assets.GameUi.Services.IScenarioService
    {
        private readonly string playerName;
        private readonly Action? onReplace;

        public FakeScenarioService(string playerName, Action? onReplace = null)
        {
            this.playerName = playerName;
            this.onReplace = onReplace;
        }

        public override string ReplaceUsernamePlaceholder(string text)
        {
            onReplace?.Invoke();
            return (text ?? string.Empty).Replace("%usernameusernameuserna%", playerName, StringComparison.Ordinal);
        }
    }

    private static class FakeSkillDescriptionBuilder
    {
        public static string GetDescription() => "技能を説明する";
    }
}
