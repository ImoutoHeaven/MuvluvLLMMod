using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class SceneTranslationPrimitivesTests
{
    [Fact]
    public void Active_scene_reservation_blocks_urgent_and_ui_enqueues_until_released_for_fallback()
    {
        var queue = new TranslationWorkQueue();
        for (var i = 0; i < 5; i++)
            Assert.True(queue.Enqueue($"う-ui-{i}"));
        for (var i = 0; i < 3; i++)
            Assert.True(queue.EnqueueSceneFallback($"の-scene-{i}"));

        Assert.True(queue.TryReserve("いま表示する", static () => false));
        Assert.False(queue.Enqueue("いま表示する"));
        Assert.False(queue.EnqueueSceneFallback("いま表示する"));
        Assert.False(queue.EnqueuePriority("いま表示する"));
        Assert.False(queue.HasUrgentWork);

        // Once the scene batch releases its reservation on fallback, the displayed line promotes
        // to Urgent and jumps ahead of both SceneFallback items and generic UI backlog.
        queue.ReleaseReservation("いま表示する");
        Assert.True(queue.EnqueueSceneFallback("いま表示する"));
        Assert.True(queue.EnqueuePriority("いま表示する"));
        Assert.True(queue.HasUrgentWork);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("いま表示する", first);
        Assert.False(queue.HasUrgentWork);
    }

    [Fact]
    public void Scene_reservation_reclaims_any_non_inflight_pending_including_urgent()
    {
        var queue = new TranslationWorkQueue();
        Assert.True(queue.EnqueuePriority("緊急台詞です"));
        Assert.True(queue.EnqueueSceneFallback("事前台詞です"));
        Assert.True(queue.EnqueueBackground("周期台詞です"));

        Assert.True(queue.TryReserve("緊急台詞です", static () => false, out var urgentStatus));
        Assert.Equal(TranslationReservationStatus.ReservedFromPending, urgentStatus);
        Assert.False(queue.HasUrgentWork);

        Assert.True(queue.TryReserve("事前台詞です", static () => false, out var sceneStatus));
        Assert.Equal(TranslationReservationStatus.ReservedFromPending, sceneStatus);

        Assert.True(queue.TryReserve("周期台詞です", static () => false, out var backgroundStatus));
        Assert.Equal(TranslationReservationStatus.ReservedFromPending, backgroundStatus);
        Assert.Equal(0, queue.QueuedCount);
        Assert.False(queue.HasQueuedWork);
    }

    [Fact]
    public void Scene_fallback_runs_ahead_of_generic_ui_and_background_with_fairness_and_complete_cached_cleans_up()
    {
        var queue = new TranslationWorkQueue();
        Assert.True(queue.EnqueueBackground("背景-0です"));
        Assert.True(queue.Enqueue("界面-0です"));
        Assert.True(queue.Enqueue("界面-1です"));
        for (var i = 0; i < 6; i++)
            Assert.True(queue.EnqueueSceneFallback($"台詞-{i}です"));

        // Four SceneFallback items run first, then Background gets its 4:1 turn, then UI gets its
        // 4:1 turn against SceneFallback.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(queue.TryDequeueWork(out var sceneWork));
            Assert.Equal($"台詞-{i}です", sceneWork.Template);
            queue.Finish(sceneWork, true);
        }

        Assert.True(queue.TryDequeueWork(out var backgroundWork));
        Assert.Equal("背景-0です", backgroundWork.Template);
        queue.Finish(backgroundWork, true);

        Assert.True(queue.TryDequeueWork(out var uiWork));
        Assert.Equal("界面-0です", uiWork.Template);
        queue.Finish(uiWork, true);

        queue.CompleteCached("台詞-4です");
        queue.CompleteCached("台詞-5です");
        queue.CompleteCached("界面-1です");
        Assert.False(queue.HasQueuedWork);
        Assert.Equal(0, queue.QueuedCount);
    }

    [Fact]
    public void Reservation_scope_releases_each_template_at_most_once_across_overlapping_scenes()
    {
        var queue = new TranslationWorkQueue();
        Assert.True(queue.TryReserve("共通です", static () => false));
        Assert.True(queue.TryReserve("末尾です", static () => false));

        var sceneAScope = new TranslationReservationScope(queue, new[] { "共通です", "末尾です" });
        sceneAScope.Release(new[] { "共通です" });

        // Scene B reserves "共通です" while Scene A's second chunk is still running.
        Assert.True(queue.TryReserve("共通です", static () => false));

        // Scene A's final Dispose must release "末尾です" without deleting Scene B's reservation.
        sceneAScope.Dispose();
        Assert.False(queue.TryReserve("共通です", static () => false, out var sharedStatus));
        Assert.Equal(TranslationReservationStatus.AlreadyReserved, sharedStatus);
        Assert.True(queue.TryReserve("末尾です", static () => false));
    }

    [Fact]
    public void Create_batches_splits_large_scenes_into_bounded_chunks_with_disjoint_reservations_and_sliced_context()
    {
        var distinctLines = new List<SceneDialogueLine>();
        for (var i = 0; i < 5; i++)
            distinctLines.Add(new SceneDialogueLine(new string((char)('あ' + i), 3) + "です", $"話者{i}"));

        var plans = SceneTranslationBatch.CreateBatches(
            40000101,
            distinctLines,
            maxTargetsPerBatch: 2,
            contextPaddingLines: 1);

        Assert.Equal(3, plans.Count);
        Assert.Equal(2, plans[0].Batch.TargetCount);
        Assert.Equal(2, plans[1].Batch.TargetCount);
        Assert.Equal(1, plans[2].Batch.TargetCount);

        var seenReserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            foreach (var reserved in plan.ReservedTemplates)
                Assert.True(seenReserved.Add(reserved));

            using var doc = JsonDocument.Parse(plan.Batch.Prompt);
            Assert.True(doc.RootElement.GetProperty("context").GetArrayLength() < distinctLines.Count);
        }

        // Duplicate normalized templates ("台詞その{0}です") stay grouped in one batch.
        var numericLines = Enumerable.Range(0, 60)
            .Select(i => new SceneDialogueLine($"台詞その{i}です", "レイラ"))
            .ToList();
        var groupedPlans = SceneTranslationBatch.CreateBatches(
            40000101,
            numericLines,
            maxTargetsPerBatch: 2);
        Assert.Single(groupedPlans);
        Assert.Equal(new[] { "台詞その{0}です" }, groupedPlans[0].ReservedTemplates);

        // Even when targets fit in a single remainder batch (1 repeated template in an 80-line
        // scene), CreateBatches slices surrounding context across all occurrences of that template.
        var sparseLines = Enumerable.Range(0, 80)
            .Select(i => new SceneDialogueLine($"背景行{i}です", null))
            .ToList();
        sparseLines[20] = new SceneDialogueLine("繰り返す台詞です", "A");
        sparseLines[35] = new SceneDialogueLine("繰り返す台詞です", "B");
        var singleSliced = SceneTranslationBatch.CreateBatches(
            40000101,
            sparseLines,
            targetLineIndices: new[] { 35 },
            maxTargetsPerBatch: 24,
            contextPaddingLines: 4);
        Assert.Single(singleSliced);
        using var slicedDoc = JsonDocument.Parse(singleSliced[0].Batch.Prompt);
        Assert.Equal(24, slicedDoc.RootElement.GetProperty("context").GetArrayLength());
    }

    [Fact]
    public async Task Coordinator_preempts_sole_slot_for_urgent_dialogue_without_exceeding_limit()
    {
        var gate = new RequestInFlightGate();
        var queue = new TranslationWorkQueue();
        using var coordinator = new SceneAttemptCoordinator(gate, queue);

        var sceneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sceneTask = coordinator.RunAttemptAsync<string?>(
            maximumInFlight: static () => 1,
            urgentPreemptGracePeriod: TimeSpan.FromMilliseconds(40),
            isActive: static () => true,
            executeAsync: async ct =>
            {
                sceneStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return "never";
            });

        await sceneStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(gate.HasAvailableSlot(1));

        Assert.True(queue.EnqueuePriority("表示中の台詞です"));

        var outcome = await sceneTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(outcome.Executed);
        Assert.True(outcome.PreemptedByUrgent);
        Assert.True(gate.TryAcquire(1));
        Assert.False(gate.TryAcquire(1));
        gate.Release();
    }

    [Fact]
    public async Task Concurrent_scene_chunks_preempt_only_the_latest_chunk_for_unreserved_urgent_work()
    {
        var gate = new RequestInFlightGate();
        var queue = new TranslationWorkQueue();
        using var coordinator = new SceneAttemptCoordinator(gate, queue);

        var seq1 = coordinator.NextSequence();
        var seq2 = coordinator.NextSequence();
        var bothRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishChunk1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;

        var chunk1 = coordinator.RunAttemptAsync(
            maximumInFlight: static () => 2,
            urgentPreemptGracePeriod: TimeSpan.FromMilliseconds(40),
            isActive: static () => true,
            executeAsync: async ct =>
            {
                if (Interlocked.Increment(ref running) == 2)
                    bothRunning.TrySetResult();
                await finishChunk1.Task.WaitAsync(ct);
                return "chunk-1";
            },
            sequence: seq1);

        var chunk2 = coordinator.RunAttemptAsync(
            maximumInFlight: static () => 2,
            urgentPreemptGracePeriod: TimeSpan.FromMilliseconds(40),
            isActive: static () => true,
            executeAsync: async ct =>
            {
                if (Interlocked.Increment(ref running) == 2)
                    bothRunning.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return "chunk-2";
            },
            sequence: seq2);

        await bothRunning.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(queue.EnqueuePriority("緊急の話者名です"));

        var outcome2 = await chunk2.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(outcome2.Executed);
        Assert.True(outcome2.PreemptedByUrgent);
        Assert.False(chunk1.IsCompleted);

        finishChunk1.TrySetResult();
        var outcome1 = await chunk1.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(outcome1.Executed);
        Assert.False(outcome1.PreemptedByUrgent);
        Assert.Equal("chunk-1", outcome1.Result);
    }

    [Fact]
    public async Task Execute_scene_plans_dispatches_chunks_concurrently_in_strict_script_order()
    {
        var gate = new RequestInFlightGate();
        var queue = new TranslationWorkQueue();
        using var coordinator = new SceneAttemptCoordinator(gate, queue);

        var lines = new List<SceneDialogueLine>();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 4; i++)
        {
            var text = new string((char)('あ' + i), 3) + "です";
            lines.Add(new SceneDialogueLine(text, "A"));
            Assert.True(queue.TryReserve(text, static () => false));
            reserved.Add(text);
        }

        var plans = SceneTranslationBatch.CreateBatches(40000102, lines, maxTargetsPerBatch: 1);
        Assert.Equal(4, plans.Count);

        using var scope = new TranslationReservationScope(queue, reserved);
        var dispatchOrder = new List<int>();
        var allInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentInFlight = 0;
        var committed = new Dictionary<string, string>(StringComparer.Ordinal);
        var sync = new object();

        var runTask = coordinator.ExecuteScenePlansAsync(
            plans,
            scope,
            maximumAttempts: 2,
            maximumInFlight: static () => 4,
            urgentPreemptGracePeriod: TimeSpan.FromSeconds(5),
            isActive: static () => true,
            sendPromptAsync: async (chunkIndex, _, _, _, onFirstDispatchReady, ct) =>
            {
                if (chunkIndex == 1)
                    await Task.Delay(50, ct);

                var responseTask = WaitForSimulatedHttpResponseAsync(
                    chunkIndex,
                    dispatchOrder,
                    sync,
                    () =>
                    {
                        if (Interlocked.Increment(ref concurrentInFlight) == 4)
                            allInFlight.TrySetResult();
                    },
                    releaseResponses.Task,
                    ct);
                onFirstDispatchReady();
                return await responseTask;
            },
            tryCommitTranslations: (_, _, _, chunkTranslations) =>
            {
                lock (sync)
                {
                    foreach (var pair in chunkTranslations)
                        committed[pair.Key] = pair.Value;
                }
                return chunkTranslations.Keys.ToArray();
            },
            onRetryScheduled: static (_, _, _, _, _) => 0,
            onChunkFallback: static (_, _, _, _, _) =>
                throw new InvalidOperationException("No fallback expected"));

        await allInFlight.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (sync)
            Assert.Equal(new[] { 1, 2, 3, 4 }, dispatchOrder);

        releaseResponses.TrySetResult();
        Assert.True(await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(4, committed.Count);
        Assert.True(gate.HasAvailableSlot(1));
        foreach (var line in reserved)
            Assert.True(queue.TryReserve(line, static () => false));

        static async Task<ScenePromptResponse> WaitForSimulatedHttpResponseAsync(
            int chunkIndex,
            List<int> order,
            object orderLock,
            Action onSent,
            Task barrier,
            CancellationToken ct)
        {
            lock (orderLock)
                order.Add(chunkIndex);
            onSent();
            await barrier.WaitAsync(ct);
            return new ScenePromptResponse(
                false,
                $"{{\"version\":1,\"sceneId\":40000102,\"translations\":[{{\"id\":\"t0000\",\"text\":\"译文{chunkIndex}\"}}]}}");
        }
    }

    [Fact]
    public async Task Execute_scene_plans_aborts_unstarted_chunks_when_an_earlier_chunk_hits_transport_failure()
    {
        var gate = new RequestInFlightGate();
        var queue = new TranslationWorkQueue();
        using var coordinator = new SceneAttemptCoordinator(gate, queue);

        var lines = new List<SceneDialogueLine>();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            var text = new string((char)('か' + i), 3) + "です";
            lines.Add(new SceneDialogueLine(text, "A"));
            Assert.True(queue.TryReserve(text, static () => false));
            reserved.Add(text);
        }

        var plans = SceneTranslationBatch.CreateBatches(40000103, lines, maxTargetsPerBatch: 1);
        using var scope = new TranslationReservationScope(queue, reserved);
        var fallbacks = new List<(int Chunk, string Reason, int Attempts)>();
        var networkCalls = 0;
        var sync = new object();

        var succeeded = await coordinator.ExecuteScenePlansAsync(
            plans,
            scope,
            maximumAttempts: 1,
            maximumInFlight: static () => 1,
            urgentPreemptGracePeriod: TimeSpan.FromSeconds(3),
            isActive: static () => true,
            sendPromptAsync: async (_, _, _, _, onFirstDispatchReady, _) =>
            {
                Interlocked.Increment(ref networkCalls);
                onFirstDispatchReady();
                await Task.Delay(30);
                return new ScenePromptResponse(true, null, "request-exception");
            },
            tryCommitTranslations: static (_, _, _, chunkTranslations) =>
                chunkTranslations.Keys.ToArray(),
            onRetryScheduled: static (_, _, _, _, _) => 0,
            onChunkFallback: (chunkIndex, _, fallbackTemplates, reason, attempts) =>
            {
                lock (sync)
                    fallbacks.Add((chunkIndex, reason, attempts));
                foreach (var template in fallbackTemplates)
                    Assert.True(queue.EnqueueSceneFallback(template));
            });

        Assert.False(succeeded);
        Assert.Equal(1, networkCalls);
        lock (sync)
        {
            Assert.Equal(3, fallbacks.Count);
            Assert.Contains((1, "request-exception", 1), fallbacks);
            Assert.Contains((2, "upstream-chunk-request-exception", 0), fallbacks);
            Assert.Contains((3, "upstream-chunk-request-exception", 0), fallbacks);
        }
        Assert.Equal(3, queue.QueuedCount);
    }

    [Fact]
    public async Task Scene_batch_retry_loop_recovers_after_structural_rejection_and_includes_rejection_reason()
    {
        var gate = new RequestInFlightGate();
        var queue = new TranslationWorkQueue();
        using var coordinator = new SceneAttemptCoordinator(gate, queue);

        var batch = SceneTranslationBatch.TryCreate(
            40000104,
            new[] { new SceneDialogueLine("%usernameusernameuserna%さん、おかえり", "純夏") })!;
        var prompts = new List<string>();
        IReadOnlyDictionary<string, string>? committed = null;

        var outcome = await coordinator.ExecuteBatchWithRetriesAsync(
            batch,
            maximumAttempts: 3,
            maximumInFlight: static () => 1,
            urgentPreemptGracePeriod: TimeSpan.FromSeconds(5),
            isActive: static () => true,
            sendPromptAsync: (_, prompt, onDispatched, _) =>
            {
                onDispatched();
                prompts.Add(prompt);
                if (prompts.Count == 1)
                {
                    return Task.FromResult(
                        new ScenePromptResponse(
                            false,
                            "{\"version\":1,\"sceneId\":40000104,\"translations\":[]}"));
                }

                return Task.FromResult(
                    new ScenePromptResponse(
                        false,
                        "{\"version\":1,\"sceneId\":40000104,\"translations\":[{\"id\":\"t0000\",\"text\":\"欢迎回来，__MLM_FMT_0__さん\"}]}"));
            },
            tryCommitTranslations: (_, translations) =>
            {
                committed = translations;
                return translations.Keys.ToArray();
            },
            onRetryScheduled: static (_, _, _) => 0);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, outcome.Attempts);
        Assert.Contains("target-count-mismatch", prompts[1], StringComparison.Ordinal);
        Assert.NotNull(committed);
        Assert.Equal(
            "欢迎回来，%usernameusernameuserna%さん",
            committed!["%usernameusernameuserna%さん、おかえり"]);
    }

    [Fact]
    public void Persistence_keeps_scene_translation_authoritative_over_sentence_in_memory_and_on_disk()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mlm-scene-authority-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new TranslationCache(root);

            // Order 1: Sentence completes first, then Scene completes -> Scene overwrites both memory and disk.
            Assert.True(cache.ObservePriority("ファリスの声", "ファリスの声"));
            Assert.True(cache.TryGetPendingGeneration("ファリスの声", out var firstGen));
            Assert.True(cache.StoreGeneratedIfPending("ファリスの声", firstGen, "法里斯的声音"));
            Assert.True(cache.IsSentenceGenerated("ファリスの声"));
            Assert.Equal("法里斯的声音", Lookup(cache, "ファリスの声"));

            Assert.True(cache.StoreGenerated("ファリスの声", "法莉丝的声音"));
            Assert.True(cache.StoreGenerated("二行目です", "整幕第二行"));
            Assert.True(cache.IsSceneAuthoritative("ファリスの声"));
            Assert.False(cache.IsSentenceGenerated("ファリスの声"));
            Assert.Equal("法莉丝的声音", Lookup(cache, "ファリスの声"));

            // Order 2: Scene already committed, then a late Sentence write arrives -> never clobbers Scene.
            Assert.True(cache.TryStoreSentenceGenerated("二行目です", "逐句覆盖失败", out var persistedLate));
            Assert.False(persistedLate);
            Assert.False(cache.StoreGeneratedIfPending("二行目です", 1, "逐句覆盖失败", out var superseded));
            Assert.True(superseded);
            Assert.Equal("整幕第二行", Lookup(cache, "二行目です"));

            Assert.True(cache.Flush());
            var reloaded = new TranslationCache(root);
            reloaded.Load();
            Assert.Equal("法莉丝的声音", Lookup(reloaded, "ファリスの声"));
            Assert.Equal("整幕第二行", Lookup(reloaded, "二行目です"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        static string Lookup(TranslationCache cache, string key) =>
            cache.TryGetGenerated(key, out var value) ? value : string.Empty;
    }

    [Fact]
    public void Scene_retry_registry_persists_across_restart_and_guards_against_concurrent_failures()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mlm-scene-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "scene-retries.v1.json");
            var registry = new SceneRetryRegistry(filePath: path);
            Assert.True(registry.Mark(40005201, flushImmediately: true));
            var epochBeforeConcurrentFailure = registry.GetFailureEpoch(40005201);

            // Simulate another concurrent scene run failing before the older run tries to clear.
            Assert.False(registry.Mark(40005201, flushImmediately: true));
            Assert.False(registry.TryComplete(40005201, epochBeforeConcurrentFailure, flushImmediately: true));
            Assert.True(registry.Contains(40005201));

            var restored = new SceneRetryRegistry(filePath: path);
            restored.Load();
            Assert.True(restored.Contains(40005201));

            var latestEpoch = registry.GetFailureEpoch(40005201);
            Assert.True(registry.TryComplete(40005201, latestEpoch, flushImmediately: true));
            Assert.False(registry.Contains(40005201));

            var restoredAfterClear = new SceneRetryRegistry(filePath: path);
            restoredAfterClear.Load();
            Assert.False(restoredAfterClear.Contains(40005201));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Scene_retry_registry_serializes_concurrent_persistence_and_bounds_failure_epochs()
    {
        var firstWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentWriters = 0;
        var maxConcurrentWriters = 0;
        string? durablePersistedJson = null;
        var sync = new object();

        var registry = new SceneRetryRegistry(
            filePath: "/virtual/scene-retries.v1.json",
            writeAtomic: (_, json) =>
            {
                var current = Interlocked.Increment(ref concurrentWriters);
                lock (sync)
                {
                    if (current > maxConcurrentWriters)
                        maxConcurrentWriters = current;
                }
                try
                {
                    if (firstWriteEntered.TrySetResult())
                        releaseFirstWrite.Task.GetAwaiter().GetResult();

                    // Record the durably committed payload at write completion (after any stall),
                    // so an uncoalesced stale writer finishing last would overwrite [1001, 1002]
                    // with [1001] and fail the assertion below.
                    lock (sync)
                        durablePersistedJson = json;
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref concurrentWriters);
                }
            });

        Assert.True(registry.Mark(1001));
        await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Second mark mutates state while the first write is still blocked; if EnsureWriterStartedUnsafe
        // failed to coalesce, a second concurrent writeAtomic invocation would enter immediately,
        // complete ahead of the blocked first write, and then be clobbered when firstWrite unblocks.
        Assert.True(registry.Mark(1002));
        await Task.Delay(30);
        Assert.Equal(1, Volatile.Read(ref concurrentWriters));
        releaseFirstWrite.TrySetResult();

        Assert.True(registry.Flush());
        lock (sync)
        {
            Assert.Equal(1, maxConcurrentWriters);
            Assert.NotNull(durablePersistedJson);
            Assert.True(SceneRetryRegistry.TryDeserialize(durablePersistedJson, out var persisted));
            Assert.Equal(new long[] { 1001, 1002 }, persisted.Snapshot());
        }

        // Repeated Mark + Complete cycles across > MaxEntries scene IDs must never leak epochs.
        var bounded = new SceneRetryRegistry();
        for (var id = 1L; id <= SceneRetryRegistry.MaxEntries * 3; id++)
        {
            Assert.True(bounded.Mark(id));
            var staleEpoch = bounded.GetFailureEpoch(id);
            Assert.True(bounded.Complete(id));
            Assert.Equal(0, bounded.FailureEpochCount);

            // Re-marking assigns a strictly newer epoch so a stale completion cannot clear it.
            Assert.True(bounded.Mark(id));
            Assert.False(bounded.TryComplete(id, staleEpoch));
            Assert.True(bounded.Complete(id));
        }
        Assert.Equal(0, bounded.FailureEpochCount);
    }

    [Fact]
    public async Task Upstream_abort_cancels_chunk_waiting_on_rate_limiter_after_slot_acquisition()
    {
        var gate = new RequestInFlightGate();
        var queue = new TranslationWorkQueue();
        using var coordinator = new SceneAttemptCoordinator(gate, queue);

        var lines = new[]
        {
            new SceneDialogueLine("あいうです", "A"),
            new SceneDialogueLine("かきくです", "B"),
        };
        var plans = SceneTranslationBatch.CreateBatches(40000109, lines, maxTargetsPerBatch: 1);
        foreach (var line in lines)
            Assert.True(queue.TryReserve(line.Text, static () => false));
        using var scope = new TranslationReservationScope(queue, lines.Select(line => line.Text));

        var chunk2EnteredRateWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failChunk1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualHttpDispatches = 0;
        var fallbacks = new List<(int Chunk, string Reason, int Attempts)>();
        var sync = new object();

        var runTask = coordinator.ExecuteScenePlansAsync(
            plans,
            scope,
            maximumAttempts: 1,
            maximumInFlight: static () => 2,
            urgentPreemptGracePeriod: TimeSpan.FromSeconds(5),
            isActive: static () => true,
            sendPromptAsync: async (chunkIndex, _, _, _, onFirstDispatchReady, ct) =>
            {
                if (chunkIndex == 1)
                {
                    onFirstDispatchReady();
                    Interlocked.Increment(ref actualHttpDispatches);
                    await failChunk1.Task.WaitAsync(ct);
                    return new ScenePromptResponse(true, null, "request-exception");
                }

                // Chunk 2 already acquired slot 2/2 (MaxInFlight = 2) and is waiting in the shared
                // rate limiter BEFORE calling onFirstDispatchReady / starting its HTTP send.
                chunk2EnteredRateWait.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                onFirstDispatchReady();
                Interlocked.Increment(ref actualHttpDispatches);
                return new ScenePromptResponse(false, "{}");
            },
            tryCommitTranslations: static (_, _, _, chunkTranslations) =>
                chunkTranslations.Keys.ToArray(),
            onRetryScheduled: static (_, _, _, _, _) => 0,
            onChunkFallback: (chunkIndex, _, _, reason, attempts) =>
            {
                lock (sync)
                    fallbacks.Add((chunkIndex, reason, attempts));
            });

        await chunk2EnteredRateWait.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failChunk1.TrySetResult();

        Assert.False(await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, actualHttpDispatches);
        lock (sync)
        {
            Assert.Equal(2, fallbacks.Count);
            Assert.Contains((1, "request-exception", 1), fallbacks);
            Assert.Contains((2, "upstream-chunk-request-exception", 0), fallbacks);
        }
    }

    [Fact]
    public async Task Mixed_cache_commit_rejection_marks_retry_and_falls_back_only_rejected_templates()
    {
        var gate = new RequestInFlightGate();
        var queue = new TranslationWorkQueue();
        using var coordinator = new SceneAttemptCoordinator(gate, queue);

        var lines = new[]
        {
            new SceneDialogueLine("短い台詞です", "A"),
            new SceneDialogueLine("大きすぎる台詞です", "B"),
        };
        var plans = SceneTranslationBatch.CreateBatches(40000110, lines, maxTargetsPerBatch: 4);
        foreach (var line in lines)
            Assert.True(queue.TryReserve(line.Text, static () => false));
        using var scope = new TranslationReservationScope(queue, lines.Select(line => line.Text));

        IReadOnlyList<string>? fallbackTemplates = null;
        string? fallbackReason = null;

        var succeeded = await coordinator.ExecuteScenePlansAsync(
            plans,
            scope,
            maximumAttempts: 2,
            maximumInFlight: static () => 1,
            urgentPreemptGracePeriod: TimeSpan.FromSeconds(5),
            isActive: static () => true,
            sendPromptAsync: static (_, _, _, _, onFirstDispatchReady, _) =>
            {
                onFirstDispatchReady();
                return Task.FromResult(new ScenePromptResponse(
                    false,
                    "{\"version\":1,\"sceneId\":40000110,\"translations\":["
                    + "{\"id\":\"t0000\",\"text\":\"短译文\"},"
                    + "{\"id\":\"t0001\",\"text\":\"被预算拒绝的译文\"}]}"));
            },
            tryCommitTranslations: static (_, _, _, _) => new[] { "短い台詞です" },
            onRetryScheduled: static (_, _, _, _, _) => 0,
            onChunkFallback: (_, _, templates, reason, _) =>
            {
                fallbackTemplates = templates.ToArray();
                fallbackReason = reason;
            });

        Assert.False(succeeded);
        Assert.Equal("partial-entry-drop", fallbackReason);
        Assert.Equal(new[] { "大きすぎる台詞です" }, fallbackTemplates);
    }

    [Fact]
    public async Task Saturated_pending_budget_skips_unstoreable_scene_fallback_requests()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mlm-saturated-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new TranslationCache(root);
            for (var i = 0; i < TranslationBudget.MaxPendingEntries; i++)
                Assert.True(cache.ObserveNormal($"満杯-{Suffix(i)}です", $"満杯-{Suffix(i)}です"));
            Assert.Equal(TranslationBudget.MaxPendingEntries, cache.RetainedSnapshot.PendingCount);

            var unstoreableCalls = 0;
            var blocker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var machine = new MachineTranslator(
                cache,
                (template, _) =>
                {
                    if (template == "溢れる台詞です")
                        Interlocked.Increment(ref unstoreableCalls);
                    return blocker.Task;
                },
                maxInFlight: 1,
                translatePeriod: TimeSpan.FromSeconds(30));
            machine.Start();
            try
            {
                Assert.Equal(0, machine.EnqueueSceneFallback(new[] { "溢れる台詞です" }));
                await Task.Delay(30);
                Assert.Equal(0, unstoreableCalls);
            }
            finally
            {
                blocker.TrySetResult(null);
                await machine.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        static string Suffix(int value)
        {
            var chars = new char[4];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)('a' + (value % 26));
                value /= 26;
            }
            return new string(chars);
        }
    }

    [Fact]
    public void Near_budget_retry_prompt_preserves_rejection_reason_within_scene_budget()
    {
        // Build a single-batch prompt near the 24 KiB scene ceiling using context lines.
        var lines = Enumerable.Range(0, 28)
            .Select(i => new SceneDialogueLine(new string((char)('あ' + (i % 40)), 750) + $"{i}です", "話者"))
            .ToList();
        var plans = SceneTranslationBatch.CreateBatches(
            40000111,
            lines,
            targetLineIndices: new[] { 14 },
            maxTargetsPerBatch: 24,
            contextPaddingLines: 14);

        var plan = Assert.Single(plans);
        var retried = SceneTranslationBatch.BuildRetryPrompt(
            plan.Batch.Prompt,
            "target-count-mismatch:expected=1,actual=0");
        Assert.Contains("target-count-mismatch:expected=1,actual=0", retried, StringComparison.Ordinal);
        Assert.True(TranslationBudget.IsSceneWithinBudget(retried));

        // Direct TryCreate prompt near the 48 KiB UTF-8 ceiling (5 x 3100 CJK/kana chars ~ 46.8 KiB UTF-8).
        var fiveLines = Enumerable.Range(0, 5)
            .Select(i => new SceneDialogueLine(new string('あ', 3100) + $"{(char)('ア' + i)}です", null))
            .ToArray();
        var directBatch = SceneTranslationBatch.TryCreate(40000112, fiveLines);
        Assert.NotNull(directBatch);
        var directRetry = SceneTranslationBatch.BuildRetryPrompt(directBatch!.Prompt, "invalid-json");
        Assert.Contains("invalid-json", directRetry, StringComparison.Ordinal);
        Assert.True(TranslationBudget.IsSceneWithinBudget(directRetry));
    }

    [Fact]
    public async Task Unplannable_over_scene_budget_target_marks_retry_and_enqueues_sentence_fallback()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mlm-unplanned-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new TranslationCache(root);
            var retries = new SceneRetryRegistry();
            var sentenceTranslated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Construct a single valid per-string candidate (4094 UTF-16 <= 4096) whose 2046
            // newline tokens expand __MLM_FMT_n__ (~27 KiB UTF-16) past the 24 KiB scene budget.
            var tagHeavyLine = "あ" + new string('\n', 2046) + "い";
            Assert.True(TextTemplate.IsTranslationCandidate(tagHeavyLine));
            Assert.Empty(SceneTranslationBatch.CreateBatches(
                40000199,
                new[] { new SceneDialogueLine(tagHeavyLine, "話者") },
                new[] { 0 }));

            var machine = new MachineTranslator(
                cache,
                (template, _) =>
                {
                    if (template == tagHeavyLine)
                        sentenceTranslated.TrySetResult();
                    return Task.FromResult<string?>("译" + new string('\n', 2046) + "文");
                },
                maxInFlight: 1,
                translatePeriod: TimeSpan.FromSeconds(10),
                sceneRetries: retries,
                sendScenePrompt: static (_, onDispatched, _) =>
                {
                    onDispatched?.Invoke();
                    throw new InvalidOperationException("Scene prompt should not run for unplannable target");
                });
            machine.Start();
            try
            {
                var queued = machine.SubmitScene(
                    40000199,
                    new[] { new SceneDialogueLine(tagHeavyLine, "話者") },
                    new[] { 0 });
                Assert.Equal(1, queued);
                Assert.True(retries.Contains(40000199));
                Assert.False(machine.IsReserved(tagHeavyLine));
                await sentenceTranslated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                await machine.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Already_dispatched_sibling_chunk_still_retries_after_upstream_abort_and_repeated_dispatch_callbacks()
    {
        var gate = new RequestInFlightGate();
        var queue = new TranslationWorkQueue();
        using var coordinator = new SceneAttemptCoordinator(gate, queue);

        var lines = new[]
        {
            new SceneDialogueLine("あいうです", "A"),
            new SceneDialogueLine("かきくです", "B"),
        };
        var plans = SceneTranslationBatch.CreateBatches(40000210, lines, maxTargetsPerBatch: 1);
        foreach (var line in lines)
            Assert.True(queue.TryReserve(line.Text, static () => false));
        using var scope = new TranslationReservationScope(queue, lines.Select(line => line.Text));

        var bothDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failChunk1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failChunk2Attempt1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = 0;
        var chunk2Attempts = 0;
        var committed = new Dictionary<string, string>(StringComparer.Ordinal);
        var sync = new object();

        var runTask = coordinator.ExecuteScenePlansAsync(
            plans,
            scope,
            maximumAttempts: 2,
            maximumInFlight: static () => 2,
            urgentPreemptGracePeriod: TimeSpan.FromSeconds(5),
            isActive: static () => true,
            sendPromptAsync: async (chunkIndex, _, attempt, _, onFirstDispatchReady, ct) =>
            {
                onFirstDispatchReady();
                if (attempt == 1 && Interlocked.Increment(ref dispatched) == 2)
                    bothDispatched.TrySetResult();

                if (chunkIndex == 1)
                {
                    if (attempt == 1)
                        await failChunk1.Task.WaitAsync(ct);
                    return new ScenePromptResponse(true, null, "request-exception");
                }

                Interlocked.Increment(ref chunk2Attempts);
                if (attempt == 1)
                {
                    await failChunk2Attempt1.Task.WaitAsync(ct);
                    // Simulate a caller invoking onFirstDispatchReady again after upstream abort.
                    onFirstDispatchReady();
                    return new ScenePromptResponse(true, null, "request-exception");
                }

                return new ScenePromptResponse(
                    false,
                    "{\"version\":1,\"sceneId\":40000210,\"translations\":[{\"id\":\"t0000\",\"text\":\"第二批成功\"}]}");
            },
            tryCommitTranslations: (_, _, _, chunkTranslations) =>
            {
                lock (sync)
                {
                    foreach (var pair in chunkTranslations)
                        committed[pair.Key] = pair.Value;
                }
                return chunkTranslations.Keys.ToArray();
            },
            onRetryScheduled: static (_, _, _, _, _) => 0,
            onChunkFallback: static (_, _, _, _, _) => { });

        await bothDispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failChunk1.TrySetResult();
        await Task.Delay(30);
        failChunk2Attempt1.TrySetResult();

        Assert.False(await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, chunk2Attempts);
        lock (sync)
            Assert.Equal("第二批成功", committed["かきくです"]);
    }

    [Fact]
    public async Task Overlapping_scene_skipping_active_reservation_marks_retry_across_restart()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mlm-overlap-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var retryPath = Path.Combine(root, "scene-retries.v1.json");
            var cache = new TranslationCache(root);
            var retries = new SceneRetryRegistry(filePath: retryPath);
            var sceneAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failSceneA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var machine = new MachineTranslator(
                cache,
                static (_, _) => Task.FromResult<string?>("逐句はい"),
                maxInFlight: 2,
                translatePeriod: TimeSpan.FromSeconds(10),
                sceneRetries: retries,
                sceneRetryCount: 0,
                sendScenePrompt: async (_, onDispatched, ct) =>
                {
                    onDispatched?.Invoke();
                    sceneAStarted.TrySetResult();
                    await failSceneA.Task.WaitAsync(ct);
                    return new ScenePromptResponse(true, null, "request-exception");
                });
            machine.Start();
            try
            {
                var sharedLines = new[] { new SceneDialogueLine("……はい。", "純夏") };
                Assert.True(machine.SubmitScene(101, sharedLines, new[] { 0 }) > 0);
                await sceneAStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                // Scene 202 enters while Scene 101 holds the reservation for "……はい。".
                Assert.Equal(0, machine.SubmitScene(202, sharedLines, new[] { 0 }));
                Assert.True(retries.Contains(202));

                failSceneA.TrySetResult();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (!cache.TryGetGenerated("……はい。", out _) && DateTime.UtcNow < deadline)
                    await Task.Delay(10);
                Assert.True(cache.TryGetGenerated("……はい。", out _));
                Assert.True(retries.Flush());
                Assert.True(cache.Flush());
            }
            finally
            {
                failSceneA.TrySetResult();
                await machine.StopAsync();
            }

            // Simulate process restart: sentenceGenerated is cold, so only persisted retry marker
            // 202 tells Scene 202 that "……はい。" came from a sentence fallback and needs upgrade.
            var reloadedCache = new TranslationCache(root);
            reloadedCache.Load();
            var reloadedRetries = new SceneRetryRegistry(filePath: retryPath);
            reloadedRetries.Load();
            Assert.True(reloadedRetries.Contains(202));
            Assert.False(reloadedCache.IsSceneAuthoritative("……はい。"));
            Assert.False(reloadedCache.IsSentenceGenerated("……はい。"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Blocked_scene_retry_writer_times_out_flush_terminal_even_on_first_write_and_exhausted_budget_does_not_reset()
    {
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeWriteAtomicCalls = 0;
        var maxWriteAtomicConcurrency = 0;
        var totalWriteAtomicCallsWhileBlocked = 0;
        var registry = new SceneRetryRegistry(
            filePath: "/virtual/scene-retries.v1.json",
            writeAtomic: (_, _) =>
            {
                var current = Interlocked.Increment(ref activeWriteAtomicCalls);
                Interlocked.Increment(ref totalWriteAtomicCallsWhileBlocked);
                int snapshotMax;
                do
                {
                    snapshotMax = Volatile.Read(ref maxWriteAtomicConcurrency);
                } while (current > snapshotMax
                    && Interlocked.CompareExchange(ref maxWriteAtomicConcurrency, current, snapshotMax) != snapshotMax);

                try
                {
                    writerEntered.TrySetResult();
                    releaseWriter.Task.GetAwaiter().GetResult();
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref activeWriteAtomicCalls);
                }
            });

        // Mark coalesces one background writer; flooding 200 additional scene IDs while blocked
        // must never invoke a second concurrent writeAtomic call.
        Assert.True(registry.Mark(701));
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var id = 702L; id <= 901L; id++)
            Assert.True(registry.Mark(id));
        await Task.Delay(30);
        Assert.Equal(1, Volatile.Read(ref totalWriteAtomicCallsWhileBlocked));
        Assert.Equal(1, Volatile.Read(ref maxWriteAtomicConcurrency));

        // Exhausted shared budget (TimeSpan.Zero) returns false immediately without resetting to 5s.
        Assert.False(registry.FlushTerminal(TimeSpan.Zero));
        registry.Freeze();

        var terminalTask = Task.Run(() => registry.FlushTerminal(TimeSpan.FromMilliseconds(80)));
        var completed = await Task.WhenAny(terminalTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(terminalTask, completed);
        Assert.False(await terminalTask);
        Assert.Equal(1, Volatile.Read(ref maxWriteAtomicConcurrency));

        releaseWriter.TrySetResult();
        Assert.True(registry.FlushTerminal(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, Volatile.Read(ref activeWriteAtomicCalls));
        Assert.Equal(1, Volatile.Read(ref maxWriteAtomicConcurrency));
    }

    [Fact]
    public async Task Reservation_budget_exceeded_marks_scene_retry_and_preserves_queued_sentence_work()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mlm-budget-deferred-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var retryPath = Path.Combine(root, "scene-retries.v1.json");
            var cache = new TranslationCache(root);
            var retries = new SceneRetryRegistry(filePath: retryPath);
            var holdFiller = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var bothSlotsOccupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var occupiedCount = 0;

            var machine = new MachineTranslator(
                cache,
                static (_, _) => Task.FromResult<string?>("逐句译文"),
                maxInFlight: 2,
                translatePeriod: TimeSpan.FromSeconds(30),
                sceneRetries: retries,
                sceneRetryCount: 0,
                sendScenePrompt: async (_, onDispatched, ct) =>
                {
                    onDispatched?.Invoke();
                    if (Interlocked.Increment(ref occupiedCount) == 2)
                        bothSlotsOccupied.TrySetResult();
                    await holdFiller.Task.WaitAsync(ct);
                    return new ScenePromptResponse(true, null, "request-exception");
                });
            machine.Start();
            try
            {
                // Fill the 1 MiB reservation byte cap with distinct non-numeric kana lines and wait
                // until both MaxInFlight slots are occupied so queued per-string work stays queued.
                var fillerLines = Enumerable.Range(0, 90)
                    .Select(i => new SceneDialogueLine(new string('あ', 4000) + (char)('ぁ' + (i % 80)) + (char)('ァ' + (i / 80)) + "です", "A"))
                    .ToArray();
                Assert.True(machine.SubmitScene(301, fillerLines, Enumerable.Range(0, fillerLines.Length).ToArray()) > 0);
                await bothSlotsOccupied.Task.WaitAsync(TimeSpan.FromSeconds(2));

                // Queue a normal per-string candidate, then submit Scene 302 whose 4000-kana line
                // exceeds the remaining reservation byte budget (BudgetExceeded): SubmitScene marks
                // Scene 302 retry-needed and promotes the queued item to SceneFallback (returning 1).
                var overflowLine = new string('い', 4000) + "です";
                Assert.True(cache.ObserveNormal(overflowLine, overflowLine));
                Assert.True(cache.TryGetPendingGeneration(overflowLine, out var gen));
                Assert.True(machine.EnqueueNormal(overflowLine, gen));

                Assert.Equal(1, machine.SubmitScene(302, new[] { new SceneDialogueLine(overflowLine, "B") }, new[] { 0 }));
                Assert.False(machine.IsReserved(overflowLine));
                Assert.True(retries.Contains(302));
                Assert.True(retries.Flush());

                var reloadedRetries = new SceneRetryRegistry(filePath: retryPath);
                reloadedRetries.Load();
                Assert.True(reloadedRetries.Contains(302));
            }
            finally
            {
                holdFiller.TrySetResult();
                await machine.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Long_surrogate_wrong_target_id_produces_exact_bounded_rejection_reason_on_retry()
    {
        var batch = SceneTranslationBatch.TryCreate(
            40000301,
            new[] { new SceneDialogueLine("こんにちは", "純夏") })!;
        var hostileId = string.Concat(Enumerable.Repeat("𠀋", 200));
        var malformedResponse =
            "{\"version\":1,\"sceneId\":40000301,\"translations\":[{\"id\":\""
            + hostileId
            + "\",\"text\":\"你好\"}]}";

        Assert.False(batch.TryParseResponse(malformedResponse, out _, out var rejectionReason));
        Assert.Equal("target-id-mismatch:index=0,expected=t0000", rejectionReason);
        Assert.DoesNotContain(hostileId, rejectionReason, StringComparison.Ordinal);

        var retryPrompt = SceneTranslationBatch.BuildRetryPrompt(batch.Prompt, rejectionReason);
        Assert.Contains(rejectionReason, retryPrompt, StringComparison.Ordinal);
        Assert.True(TranslationBudget.IsSceneWithinBudget(retryPrompt));
    }

    [Fact]
    public async Task Stalled_response_body_after_headers_honors_configured_timeout()
    {
        using var http = new HttpClient(new StalledBodyHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings(
                "https://example.test/v1/chat/completions",
                "model",
                string.Empty,
                TimeoutSeconds: 1,
                RetryCount: 1),
            new RequestRateLimiter(1000));

        var translateTask = client.TranslateAsync("待機する", CancellationToken.None);
        var completed = await Task.WhenAny(translateTask, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(translateTask, completed);
        Assert.Null(await translateTask);
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StalledContent(),
            });

        private sealed class StalledContent : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                await Task.Delay(Timeout.InfiniteTimeSpan);

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override Task<Stream> CreateContentReadStreamAsync() =>
                Task.FromResult<Stream>(new NeverEndingStream());
        }

        private sealed class NeverEndingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
