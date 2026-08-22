using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MuvluvLLMMod.IntegrationTests;

public sealed class ProductionBudgetAndShutdownIntegrationTests
{
    [Fact]
    public void Durable_cache_recovery_chooses_the_newest_valid_journal_epoch()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.journal." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            Assert.True(cache.StoreGenerated("旧する", "旧译"));
            Assert.True(cache.Flush());
            var oldState = File.ReadAllText(cache.StatePath);

            cache.RemoveGenerated("旧する");
            Assert.True(cache.StoreGenerated("新する", "新译"));
            Assert.True(cache.Flush());
            var newState = File.ReadAllText(cache.StatePath);
            Assert.True(
                JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(newState)!.Epoch
                > JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(oldState)!.Epoch);

            File.WriteAllText(cache.StatePath, oldState, new UTF8Encoding(false));
            File.WriteAllText(cache.StateTemporaryPath, newState, new UTF8Encoding(false));

            var recovered = new TranslationCache(root);
            recovered.Load();
            Assert.True(recovered.TryGetGenerated("新する", out var translated));
            Assert.Equal("新译", translated);
            Assert.False(recovered.TryGetGenerated("旧する", out _));
            Assert.False(File.Exists(recovered.StateTemporaryPath));

            var restarted = new TranslationCache(root);
            restarted.Load();
            Assert.True(restarted.TryGetGenerated("新する", out var restartedTranslation));
            Assert.Equal("新译", restartedTranslation);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Maximum_epoch_is_terminal_and_flush_never_claims_an_invalid_success()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.max-epoch." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var seed = new TranslationCache.DurableSnapshot
            {
                Version = 1,
                Epoch = long.MaxValue,
                TransactionId = new string('m', 32),
                Generated = new Dictionary<string, string>
                {
                    ["旧する"] = "旧译"
                },
                // The checksum/schema is valid; load sanitizes the duplicate mirror and therefore
                // leaves a dirty state that can exercise both normal and terminal flush guards.
                Pending = new[] { "待機する", "待機する" },
                Raw = Array.Empty<string>()
            };
            seed.Checksum = ComputeChecksum(seed);
            var cache = new TranslationCache(root);
            File.WriteAllText(
                cache.StatePath,
                JsonSerializer.Serialize(seed, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            cache.Load();
            Assert.True(cache.IsDurableMutationBlocked);
            Assert.True(cache.TryGetGenerated("旧する", out var oldTranslation));
            Assert.Equal("旧译", oldTranslation);
            var before = cache.RetainedSnapshot;

            Assert.False(cache.StoreGenerated("新する", "新译"));
            Assert.False(cache.ObserveNormal("新的观察する", "新的观察する"));
            Assert.Equal(before, cache.RetainedSnapshot);
            Assert.False(cache.Flush());
            Assert.False(cache.FlushTerminal(TimeSpan.FromSeconds(1)));

            var persisted = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(
                File.ReadAllText(cache.StatePath));
            Assert.NotNull(persisted);
            Assert.Equal(long.MaxValue, persisted!.Epoch);

            var restarted = new TranslationCache(root);
            restarted.Load();
            Assert.True(restarted.TryGetGenerated("旧する", out var restartedTranslation));
            Assert.Equal("旧译", restartedTranslation);
            Assert.False(restarted.TryGetGenerated("新する", out _));
            Assert.False(restarted.Flush());
            Assert.False(restarted.FlushTerminal(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Full_cap_cjk_pairs_are_rejected_before_an_unwritable_authoritative_state()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.snapshot-admission." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            const int pairCount = 4096;
            var accepted = 0;
            var rejected = 0;
            for (var index = 0; index < pairCount; index++)
            {
                var source = CjkPairPart(0x4e00 + index, 170, '字');
                var translation = CjkPairPart(0x6000 + index, 171, '译');
                if (cache.StoreGenerated(source, translation))
                    accepted++;
                else
                    rejected++;
            }

            Assert.Equal(pairCount, accepted + rejected);
            Assert.InRange(accepted, 1, pairCount);
            if (accepted < pairCount)
            {
                var before = cache.RetainedSnapshot;
                var oversizedAggregateSource = new string('あ', TranslationBudget.MaxTextUtf16CodeUnits);
                Assert.False(cache.ObserveNormal(oversizedAggregateSource, oversizedAggregateSource));
                Assert.Equal(before.PendingCount, cache.RetainedSnapshot.PendingCount);
                Assert.Equal(before.RawCount, cache.RetainedSnapshot.RawCount);
            }

            Assert.True(cache.Flush());
            Assert.True(cache.FlushTerminal(TimeSpan.FromSeconds(1)));
            Assert.True(File.Exists(cache.StatePath));
            Assert.InRange(
                new FileInfo(cache.StatePath).Length,
                1,
                TranslationBudget.MaxCacheSnapshotBytes);

            var restarted = new TranslationCache(root);
            restarted.Load();
            Assert.Equal(accepted, restarted.GeneratedCount);
            Assert.True(restarted.TryGetGenerated(
                CjkPairPart(0x4e00, 170, '字'),
                out var firstTranslation));
            Assert.Equal(CjkPairPart(0x6000, 171, '译'), firstTranslation);
            Assert.True(restarted.Flush());
            Assert.True(restarted.FlushTerminal(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Hostile_json_escapes_and_all_durable_collections_share_the_admission()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.snapshot-hostile." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            var source = "かな " + '"' + '\u0001' + '"' + " <b>{0}</b>\n";
            var translation = "译 " + '"' + '\u0001' + '"' + " <b>{0}</b>\n";
            Assert.True(cache.StoreGenerated(source, translation));

            for (var index = 0; index < 300; index++)
            {
                var value = "待機する" + (char)(0x7000 + index) + " " + '"' + '\u0002' + '"' + " <i>";
                Assert.True(cache.ObserveNormal(value, value));
            }

            var beforeFlush = cache.RetainedSnapshot;
            Assert.True(beforeFlush.GeneratedCount > 0);
            Assert.True(beforeFlush.PendingCount > 0);
            Assert.True(beforeFlush.RawCount > 0);
            Assert.True(cache.Flush());
            Assert.InRange(
                new FileInfo(cache.StatePath).Length,
                1,
                TranslationBudget.MaxCacheSnapshotBytes);
            Assert.True(cache.FlushTerminal(TimeSpan.FromSeconds(1)));

            var restarted = new TranslationCache(root);
            restarted.Load();
            Assert.True(restarted.TryGetGenerated(source, out var restored));
            Assert.Equal(translation, restored);
            Assert.Equal(beforeFlush.PendingCount, restarted.RetainedSnapshot.PendingCount);
            Assert.Equal(beforeFlush.RawCount, restarted.RetainedSnapshot.RawCount);
            Assert.True(restarted.Flush());
            Assert.True(restarted.FlushTerminal(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Reverse_index_retains_source_and_translation_bytes_for_admission()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.reverse." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            var source = new string('あ', 4000);
            cache.RememberResolution(source, "译");
            var expected = Encoding.UTF8.GetByteCount(source) + Encoding.UTF8.GetByteCount("译");
            Assert.Equal(expected, cache.RetainedSnapshot.ReverseUtf8Bytes);
            Assert.True(cache.RetainedSnapshot.ReverseUtf8Bytes >= Encoding.UTF8.GetByteCount(source));
            Assert.True(cache.TryGetSourceForTranslatedValue("译", out var retainedSource));
            Assert.Equal(source, retainedSource);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Authoritative_write_preserves_the_previous_epoch_for_backup_recovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.backup." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            Assert.True(cache.StoreGenerated("旧する", "旧译"));
            Assert.True(cache.Flush());
            var oldState = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(
                File.ReadAllText(cache.StatePath))!;

            cache.RemoveGenerated("旧する");
            Assert.True(cache.StoreGenerated("新する", "新译"));
            Assert.True(cache.Flush());
            Assert.True(File.Exists(cache.StateBackupPath));
            var backup = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(
                File.ReadAllText(cache.StateBackupPath))!;
            Assert.Equal(oldState.Epoch, backup.Epoch);

            // Remove every current mirror and canonical file. Recovery must still find the
            // previous coherent epoch in the preserved backup rather than silently falling back
            // to an empty/legacy epoch.
            File.Delete(cache.StatePath);
            File.Delete(cache.GeneratedPath);
            File.Delete(cache.PendingPath);
            File.Delete(cache.RawPath);

            var recovered = new TranslationCache(root);
            recovered.Load();
            Assert.True(recovered.TryGetGenerated("旧する", out var oldTranslation));
            Assert.Equal("旧译", oldTranslation);
            Assert.False(recovered.TryGetGenerated("新する", out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Newer_invalid_checksum_is_rejected_in_favor_of_a_valid_canonical_epoch()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.checksum." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            Assert.True(cache.StoreGenerated("稳定する", "稳定译"));
            Assert.True(cache.Flush());
            var oldState = File.ReadAllText(cache.StatePath);

            cache.RemoveGenerated("稳定する");
            Assert.True(cache.StoreGenerated("损坏する", "损坏译"));
            Assert.True(cache.Flush());
            var newer = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(
                File.ReadAllText(cache.StatePath))!;
            var checksum = newer.Checksum!;
            newer.Checksum = (checksum[0] == '0' ? '1' : '0') + checksum[1..];
            File.WriteAllText(cache.StatePath, oldState, new UTF8Encoding(false));
            File.WriteAllText(
                cache.StateTemporaryPath,
                JsonSerializer.Serialize(newer),
                new UTF8Encoding(false));

            var recovered = new TranslationCache(root);
            recovered.Load();
            Assert.True(recovered.TryGetGenerated("稳定する", out var stableTranslation));
            Assert.Equal("稳定译", stableTranslation);
            Assert.False(recovered.TryGetGenerated("损坏する", out _));
            Assert.False(File.Exists(recovered.StateTemporaryPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Negative_epoch_journal_is_rejected_even_when_its_checksum_matches_its_payload()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.epoch." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root);
            Assert.True(cache.StoreGenerated("不应恢复する", "不应恢复译"));
            Assert.True(cache.Flush());
            var invalid = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(
                File.ReadAllText(cache.StatePath))!;
            File.Delete(cache.StatePath);
            File.Delete(cache.StateBackupPath);
            invalid.Epoch = -1;
            invalid.Checksum = ComputeChecksum(invalid);
            File.WriteAllText(
                cache.StateTemporaryPath,
                JsonSerializer.Serialize(invalid),
                new UTF8Encoding(false));

            var recovered = new TranslationCache(root);
            recovered.Load();
            Assert.False(recovered.TryGetGenerated("不应恢复する", out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Response_body_budget_stops_unknown_length_producer_before_full_buffer()
    {
        const int totalBytes = 512 * 1024;
        var content = new CountingChunkedContent(totalBytes);
        using var http = new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 1),
            new RequestRateLimiter(1000));

        Assert.Null(content.Headers.ContentLength);
        Assert.Null(await client.TranslateAsync("スキル", CancellationToken.None));
        Assert.InRange(
            content.ProducedBytes,
            TranslationBudget.MaxResponseBodyBytes,
            TranslationBudget.MaxResponseBodyBytes + 8192);
        Assert.True(content.ProducedBytes < totalBytes);
    }

    [Fact]
    public void Oversized_render_input_is_rejected_fail_closed_and_not_retained()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var source = new string('あ', TranslationBudget.MaxTextUtf16CodeUnits + 1);
        var label = new TMPro.TMP_Text();

        label.text = source;

        Assert.Equal(source, label.text);
        Assert.Empty(Plugin.CurrentCache!.PendingSnapshot());
        Assert.Equal(0, Plugin.CurrentCache.RetainedSnapshot.PendingCount);
    }

    [Fact]
    public void Terminal_flush_recovers_from_one_transient_write_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.flush." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var writes = 0;
            var cache = new TranslationCache(
                root,
                writeAtomic: (_, _) =>
                {
                    writes++;
                    return writes != 1;
                });
            Assert.True(cache.ObserveNormal("一つを確認する", "一つを確認する"));

            Assert.True(cache.FlushTerminal(TimeSpan.FromSeconds(1)));
            // The first state write fails; the second attempt writes state plus three legacy
            // mirrors. The exact production writer therefore needs five calls.
            Assert.Equal(5, writes);
            Assert.True(cache.Flush());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Persistent_terminal_flush_failure_is_a_quarantined_cleanup_result()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.flush-fail." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new TranslationCache(root, writeAtomic: static (_, _) => false);
            Assert.True(cache.ObserveNormal("一つを確認する", "一つを確認する"));
            var gate = new PluginLifecycleGate();
            Assert.True(gate.TryBeginLoad());
            var later = new List<string>();

            Assert.False(gate.Cleanup(new[]
            {
                new PluginCleanupStep("freeze", cache.FreezeMutations),
                new PluginCleanupStep("terminal flush", () =>
                {
                    if (!cache.FlushTerminal(TimeSpan.FromMilliseconds(10)))
                        throw new InvalidOperationException("terminal persistence failed");
                }),
                new PluginCleanupStep("unpatch", () => later.Add("unpatch"))
            }));

            Assert.Equal(new[] { "unpatch" }, later);
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
            Assert.False(gate.TryBeginLoad(out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Timed_out_reload_is_terminal_and_retains_the_old_worker_for_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.reload-fault." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        MachineTranslator? workerA = null;
        var created = 0;
        var laterSteps = new List<string>();
        var gate = new PluginLifecycleGate(TimeSpan.FromMilliseconds(100));
        MachineTranslatorLifecycle? lifecycle = null;
        Assert.True(gate.TryBeginLoad(out var generation));
        try
        {
            var cache = new TranslationCache(root);
            lifecycle = new MachineTranslatorLifecycle(
                shutdownTimeout: TimeSpan.FromMilliseconds(40),
                terminalFailure: exception => gate.RecordFailure(generation, exception));

            MachineTranslator Factory(RequestRateLimiter _, TranslationPriorityBacklog backlog)
            {
                var number = Interlocked.Increment(ref created);
                var machine = new MachineTranslator(
                    cache,
                    async (_, _) =>
                    {
                        entered.TrySetResult();
                        return await release.Task.ConfigureAwait(false);
                    },
                    1,
                    TimeSpan.FromMilliseconds(10),
                    backlog);
                if (number == 1)
                    workerA = machine;
                return machine;
            }

            Assert.True(lifecycle.Initialize(true, 1, Factory));
            Assert.True(lifecycle.EnqueuePriority("重新加载する"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(lifecycle.Reload(true, 1, Factory));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => lifecycle.TransitionTask.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.True(lifecycle.IsFaulted);
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
            Assert.False(lifecycle.Reload(true, 1, Factory));
            Assert.False(lifecycle.EnqueuePriority("第二次重新加载する"));
            Assert.Equal(1, created);
            Assert.Equal(1, lifecycle.TrackedStoppingWorkerCount);

            Assert.False(gate.Cleanup(_ =>
            {
                var machineFailed = false;
                try
                {
                    lifecycle.Shutdown();
                }
                catch (InvalidOperationException)
                {
                    machineFailed = true;
                }
                laterSteps.Add("flush");
                laterSteps.Add("unpatch");
                return !machineFailed;
            }));

            Assert.Equal(new[] { "flush", "unpatch" }, laterSteps);
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
            Assert.False(gate.TryBeginLoad(out _));
        }
        finally
        {
            release.TrySetResult(null);
            if (workerA != null && lifecycle != null)
            {
                await workerA.StopCompletion.WaitAsync(TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => lifecycle.TrackedStoppingWorkerCount == 0);
                Assert.Equal(0, lifecycle.TrackedStoppingWorkerCount);
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Noncooperative_machine_shutdown_times_out_and_later_cleanup_steps_still_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.timeout." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var cache = new TranslationCache(root);
            var template = "一つを確認する";
            Assert.True(cache.ObserveNormal(template, template));
            var lifecycle = new MachineTranslatorLifecycle(
                shutdownTimeout: TimeSpan.FromMilliseconds(60));
            Assert.True(lifecycle.Initialize(
                true,
                1,
                (_, backlog) => new MachineTranslator(
                    cache,
                    async (_, _) =>
                    {
                        entered.TrySetResult();
                        return await release.Task.ConfigureAwait(false);
                    },
                    1,
                    TimeSpan.FromMilliseconds(10),
                    backlog),
                new TranslationRetryPolicy()));
            // Start schedules the durable pending item itself; a duplicate enqueue may be
            // correctly rejected because the work is already owned by the queue.
            _ = lifecycle.EnqueueNormal(template, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var gate = new PluginLifecycleGate();
            Assert.True(gate.TryBeginLoad());
            var later = new List<string>();
            var started = DateTime.UtcNow;
            var succeeded = gate.Cleanup(new[]
            {
                new PluginCleanupStep("machine shutdown", lifecycle.Shutdown),
                new PluginCleanupStep("terminal flush", () => later.Add("flush")),
                new PluginCleanupStep("unpatch", () => later.Add("unpatch"))
            });
            var elapsed = DateTime.UtcNow - started;

            Assert.False(succeeded);
            Assert.True(lifecycle.ShutdownTimedOut);
            Assert.Equal(new[] { "flush", "unpatch" }, later);
            Assert.True(elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(PluginLifecycleState.Failed, gate.State);
        }
        finally
        {
            release.TrySetResult("翻译");
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class SnapshotIntegrityPayload
    {
        public int Version { get; set; }
        public long Epoch { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public SortedDictionary<string, string> Generated { get; set; } = new(StringComparer.Ordinal);
        public string[] Pending { get; set; } = Array.Empty<string>();
        public string[] Raw { get; set; } = Array.Empty<string>();
    }

    private static string ComputeChecksum(TranslationCache.DurableSnapshot snapshot)
    {
        var payload = new SnapshotIntegrityPayload
        {
            Version = snapshot.Version,
            Epoch = snapshot.Epoch,
            TransactionId = snapshot.TransactionId ?? string.Empty,
            Generated = new SortedDictionary<string, string>(
                snapshot.Generated ?? new Dictionary<string, string>(),
                StringComparer.Ordinal),
            Pending = snapshot.Pending ?? Array.Empty<string>(),
            Raw = snapshot.Raw ?? Array.Empty<string>()
        };
        var canonical = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed class CountingChunkedContent : HttpContent
    {
        private readonly int totalBytes;
        private int producedBytes;

        public CountingChunkedContent(int totalBytes) => this.totalBytes = totalBytes;
        public int ProducedBytes => Volatile.Read(ref producedBytes);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[totalBytes]);
            Interlocked.Exchange(ref producedBytes, totalBytes);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CountingReadStream(totalBytes, count => Interlocked.Add(ref producedBytes, count)));
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly int totalBytes;
        private readonly Action<int> produced;
        private int position;

        public CountingReadStream(int totalBytes, Action<int> produced)
        {
            this.totalBytes = totalBytes;
            this.produced = produced;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, totalBytes - position);
            if (read <= 0) return 0;
            Array.Clear(buffer, offset, read);
            position += read;
            produced(read);
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, totalBytes - position);
            if (read <= 0) return ValueTask.FromResult(0);
            buffer[..read].Span.Clear();
            position += read;
            produced(read);
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private static string CjkPairPart(int firstCodePoint, int length, char filler)
    {
        var chars = new char[length];
        chars[0] = (char)firstCodePoint;
        Array.Fill(chars, filler, 1, length - 1);
        return new string(chars);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
