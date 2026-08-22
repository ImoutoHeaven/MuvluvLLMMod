using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class CachePersistenceProtocolTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "MuvluvLLMMod.Persistence.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Canonical_state_snapshot_is_coherent_and_legacy_paths_remain_readable()
    {
        var cache = new TranslationCache(root);
        cache.ObserveNormal("保留する", "保留する");
        cache.StoreGenerated("生成する", "生成译");
        Assert.True(cache.Flush());

        Assert.True(File.Exists(cache.StatePath));
        Assert.NotNull(File.ReadAllText(cache.GeneratedPath));
        Assert.NotNull(File.ReadAllText(cache.PendingPath));
        Assert.NotNull(File.ReadAllText(cache.RawPath));

        var loaded = new TranslationCache(root);
        loaded.Load();
        Assert.True(loaded.TryGetGenerated("生成する", out var translated));
        Assert.Equal("生成译", translated);
        Assert.Equal(new[] { "保留する" }, loaded.PendingSnapshot());
    }

    [Fact]
    public void Missing_manifest_recovers_the_temporary_epoch_without_mixing_legacy_files()
    {
        var cache = new TranslationCache(root);
        Directory.CreateDirectory(Path.Combine(root, "dump"));
        File.WriteAllText(
            cache.StateTemporaryPath,
            "{\"Version\":1,\"Generated\":{\"新する\":\"新译\"},\"Pending\":[\"待機する\"],\"Raw\":[\"新する\"]}",
            Encoding.UTF8);
        File.WriteAllText(cache.GeneratedPath, "{\"旧する\":\"旧译\"}", Encoding.UTF8);
        File.WriteAllText(cache.PendingPath, "[\"旧待機する\"]", Encoding.UTF8);
        File.WriteAllText(cache.RawPath, "{\"旧する\":\"\"}", Encoding.UTF8);

        var diagnostics = new List<string>();
        var loaded = new TranslationCache(root, diagnostics.Add);
        loaded.Load();

        Assert.True(loaded.TryGetGenerated("新する", out var translated), string.Join(";", diagnostics));
        Assert.Equal("新译", translated);
        Assert.False(loaded.TryGetGenerated("旧する", out _));
        Assert.Equal(new[] { "待機する" }, loaded.PendingSnapshot());
    }

    [Fact]
    public void Partial_manifest_recovers_the_last_backup_epoch()
    {
        var original = new TranslationCache(root);
        original.ObserveNormal("备份する", "备份する");
        original.StoreGenerated("保存する", "保存译");
        Assert.True(original.Flush());
        File.Copy(original.StatePath, original.StateBackupPath, true);
        File.WriteAllText(original.StatePath, "{", Encoding.UTF8);
        File.WriteAllText(original.GeneratedPath, "{\"混合する\":\"混合译\"}", Encoding.UTF8);

        var loaded = new TranslationCache(root);
        loaded.Load();

        Assert.True(loaded.TryGetGenerated("保存する", out var translated));
        Assert.Equal("保存译", translated);
        Assert.False(loaded.TryGetGenerated("混合する", out _));
        Assert.Equal(new[] { "备份する" }, loaded.PendingSnapshot());
    }

    [Fact]
    public void Newer_valid_temporary_epoch_wins_over_old_canonical_and_restart_stays_coherent()
    {
        var cache = new TranslationCache(root);
        Assert.True(cache.StoreGenerated("古いする", "旧译"));
        Assert.True(cache.Flush());
        var oldState = File.ReadAllText(cache.StatePath);
        var oldSnapshot = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(oldState);

        cache.RemoveGenerated("古いする");
        Assert.True(cache.StoreGenerated("新しいする", "新译"));
        Assert.True(cache.Flush());
        var newerState = File.ReadAllText(cache.StatePath);
        var newerSnapshot = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(newerState);
        Assert.True(newerSnapshot!.Epoch > oldSnapshot!.Epoch);

        // Reproduce a successful journal write followed by a locked canonical replacement.
        File.WriteAllText(cache.StatePath, oldState, new UTF8Encoding(false));
        File.WriteAllText(cache.StateTemporaryPath, newerState, new UTF8Encoding(false));

        var recovered = new TranslationCache(root);
        recovered.Load();
        Assert.True(recovered.TryGetGenerated("新しいする", out var translated));
        Assert.Equal("新译", translated);
        Assert.False(recovered.TryGetGenerated("古いする", out _));
        Assert.False(File.Exists(recovered.StateTemporaryPath));
        Assert.Equal(
            newerSnapshot.Epoch,
            JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(File.ReadAllText(recovered.StatePath))!.Epoch);
        Assert.Equal(
            oldSnapshot.Epoch,
            JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(File.ReadAllText(recovered.StateBackupPath))!.Epoch);
        Assert.True(recovered.Flush());
        Assert.Equal(
            newerSnapshot.Epoch,
            JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(File.ReadAllText(recovered.StatePath))!.Epoch);

        var restarted = new TranslationCache(root);
        restarted.Load();
        Assert.True(restarted.TryGetGenerated("新しいする", out var restartedTranslation));
        Assert.Equal("新译", restartedTranslation);
        Assert.False(restarted.TryGetGenerated("古いする", out _));
    }

    [Fact]
    public void Newer_valid_backup_wins_when_canonical_is_missing()
    {
        var cache = new TranslationCache(root);
        Assert.True(cache.StoreGenerated("旧する", "旧译"));
        Assert.True(cache.Flush());
        var oldState = File.ReadAllText(cache.StatePath);
        var oldSnapshot = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(oldState);

        cache.RemoveGenerated("旧する");
        Assert.True(cache.StoreGenerated("备份新する", "备份新译"));
        Assert.True(cache.Flush());
        var newerState = File.ReadAllText(cache.StatePath);
        var newerSnapshot = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(newerState);
        File.Copy(cache.StatePath, cache.StateBackupPath, true);
        File.Delete(cache.StatePath);
        File.Delete(cache.StateTemporaryPath);

        var loaded = new TranslationCache(root);
        loaded.Load();
        Assert.True(loaded.TryGetGenerated("备份新する", out var translated));
        Assert.Equal("备份新译", translated);
        Assert.False(loaded.TryGetGenerated("旧する", out _));
        Assert.Equal(
            newerSnapshot!.Epoch,
            JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(File.ReadAllText(cache.StatePath))!.Epoch);
        Assert.True(newerSnapshot.Epoch > oldSnapshot!.Epoch);
    }

    [Fact]
    public void Corrupt_newer_temporary_epoch_is_ignored_in_favor_of_valid_canonical()
    {
        var cache = new TranslationCache(root);
        Assert.True(cache.StoreGenerated("稳定する", "稳定译"));
        Assert.True(cache.Flush());
        var oldState = File.ReadAllText(cache.StatePath);

        cache.RemoveGenerated("稳定する");
        Assert.True(cache.StoreGenerated("损坏する", "损坏译"));
        Assert.True(cache.Flush());
        var newerState = File.ReadAllText(cache.StatePath);
        var newer = JsonSerializer.Deserialize<TranslationCache.DurableSnapshot>(newerState)!;
        var corrupted = newerState.Replace(newer.Checksum![0], newer.Checksum[0] == '0' ? '1' : '0');
        File.WriteAllText(cache.StatePath, oldState, new UTF8Encoding(false));
        File.WriteAllText(cache.StateTemporaryPath, corrupted, new UTF8Encoding(false));

        var loaded = new TranslationCache(root);
        loaded.Load();
        Assert.True(loaded.TryGetGenerated("稳定する", out var translated));
        Assert.Equal("稳定译", translated);
        Assert.False(loaded.TryGetGenerated("损坏する", out _));
        Assert.False(File.Exists(loaded.StateTemporaryPath));
    }

    [Fact]
    public void Authoritative_epoch_survives_an_interrupted_legacy_mirror_write()
    {
        Directory.CreateDirectory(Path.Combine(root, "dump"));
        File.WriteAllText(
            Path.Combine(root, "generated.zh_Hans.json"),
            "{\"旧镜像する\":\"旧镜像译\"}",
            new UTF8Encoding(false));
        var diagnostics = new List<string>();
        var cache = new TranslationCache(
            root,
            diagnostics.Add,
            writeAtomic: (path, json) =>
            {
                if (Path.GetFileName(path) == "generated.zh_Hans.json")
                    return false;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json, new UTF8Encoding(false));
                return true;
            });
        Assert.True(cache.StoreGenerated("权威する", "权威译"));
        Assert.True(cache.Flush());
        Assert.Contains(diagnostics, value => value.Contains("legacy mirrors failed", StringComparison.Ordinal));

        var restarted = new TranslationCache(root);
        restarted.Load();
        Assert.True(restarted.TryGetGenerated("权威する", out var translated));
        Assert.Equal("权威译", translated);
        Assert.False(restarted.TryGetGenerated("旧镜像する", out _));
    }

    [Fact]
    public void Terminal_retries_are_paced_and_complete_before_the_budget()
    {
        var attempts = new List<DateTime>();
        var cache = new TranslationCache(root, writeAtomic: (path, _) =>
        {
            if (Path.GetFileName(path) != "cache.state.v1.json") return true;
            lock (attempts) attempts.Add(DateTime.UtcNow);
            return attempts.Count >= 3;
        });
        cache.ObserveNormal("锁定する", "锁定する");
        cache.FreezeMutations();

        var started = Stopwatch.GetTimestamp();
        Assert.True(cache.FlushTerminal(TimeSpan.FromSeconds(2)));
        var elapsed = TimeSpan.FromSeconds(
            (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);

        Assert.Equal(3, attempts.Count);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(100));
        Assert.True(elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Persistent_terminal_failure_is_reported_and_a_later_write_can_recover()
    {
        var writable = false;
        var attempts = 0;
        var cache = new TranslationCache(root, writeAtomic: (path, _) =>
        {
            if (Path.GetFileName(path) == "cache.state.v1.json")
            {
                attempts++;
                return writable;
            }
            return true;
        });
        cache.ObserveNormal("不可写する", "不可写する");
        cache.FreezeMutations();

        Assert.False(cache.FlushTerminal(TimeSpan.FromMilliseconds(250)));
        Assert.InRange(attempts, 2, TranslationCache.TerminalFlushMaxAttempts);

        writable = true;
        Assert.True(cache.FlushTerminal(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Terminal_failure_quarantines_the_generation_and_blocks_replacement_load()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad());
        var cache = new TranslationCache(
            root,
            writeAtomic: (path, _) => Path.GetFileName(path) != "cache.state.v1.json");
        cache.ObserveNormal("隔离する", "隔离する");
        cache.FreezeMutations();

        Assert.False(gate.Cleanup(_ => cache.FlushTerminal(TimeSpan.FromMilliseconds(200))));
        Assert.Equal(PluginLifecycleState.Failed, gate.State);
        Assert.False(gate.TryBeginLoad(out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
