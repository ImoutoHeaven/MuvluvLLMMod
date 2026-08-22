using System.Diagnostics;
using System.Text;
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
    public void Terminal_retries_are_paced_and_complete_before_the_budget()
    {
        var attempts = new List<DateTime>();
        var cache = new TranslationCache(root, writeAtomic: (path, _) =>
        {
            if (Path.GetFileName(path) != "generated.zh_Hans.json") return true;
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
            if (Path.GetFileName(path) == "generated.zh_Hans.json")
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
            writeAtomic: (path, _) => Path.GetFileName(path) != "generated.zh_Hans.json");
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
