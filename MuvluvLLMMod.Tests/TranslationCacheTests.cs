using System.Text.Json;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TranslationCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Paths_and_json_shapes_round_trip_without_temporary_files()
    {
        var cache = new TranslationCache(root);
        Assert.Equal(Path.Combine(root, "generated.zh_Hans.json"), cache.GeneratedPath);
        Assert.Equal(Path.Combine(root, "pending.zh_Hans.json"), cache.PendingPath);
        Assert.Equal(Path.Combine(root, "dump", "ui_raw.json"), cache.RawPath);

        cache.ObservePriority("スキル 12", "スキル {0}");
        cache.ObservePriority("スキル 12", "スキル {0}");
        cache.ObserveNormal("回復する", "回復する");
        cache.StoreGenerated("スキル {0}", "技能 {0}");
        cache.Flush();

        var generated = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cache.GeneratedPath));
        var pending = JsonSerializer.Deserialize<string[]>(File.ReadAllText(cache.PendingPath));
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cache.RawPath));
        Assert.Equal("技能 {0}", generated!["スキル {0}"]);
        Assert.Equal(new[] { "回復する" }, pending);
        Assert.Equal(string.Empty, raw!["スキル {0}"]);
        Assert.Equal(string.Empty, raw["回復する"]);
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));

        var loaded = new TranslationCache(root);
        loaded.Load();
        Assert.True(loaded.TryGetGenerated("スキル {0}", out var translated));
        Assert.Equal("技能 {0}", translated);
        Assert.Equal(new[] { "回復する" }, loaded.PendingSnapshot());
    }

    [Fact]
    public void Load_cleans_pending_dedupes_and_removes_generated_or_non_kana_entries()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "generated.zh_Hans.json"), "{\"スキル {0}\":\"技能 {0}\"}");
        File.WriteAllText(Path.Combine(root, "pending.zh_Hans.json"), "[\"確認\",\"回復する\",\"回復する\",\"スキル {0}\"]");

        var cache = new TranslationCache(root);
        cache.Load();

        Assert.Equal(new[] { "回復する" }, cache.PendingSnapshot());
        cache.Flush();
        Assert.Equal(new[] { "回復する" }, JsonSerializer.Deserialize<string[]>(File.ReadAllText(cache.PendingPath)));
    }

    [Fact]
    public void Load_recovers_from_malformed_files_and_can_replace_them_atomically()
    {
        Directory.CreateDirectory(Path.Combine(root, "dump"));
        File.WriteAllText(Path.Combine(root, "generated.zh_Hans.json"), "{");
        File.WriteAllText(Path.Combine(root, "pending.zh_Hans.json"), "nope");
        File.WriteAllText(Path.Combine(root, "dump", "ui_raw.json"), "[]");

        var cache = new TranslationCache(root);
        cache.Load();
        Assert.Empty(cache.PendingSnapshot());
        Assert.False(cache.TryGetGenerated("スキル", out _));
        cache.ObservePriority("新しい", "新しい");
        cache.Flush();

        Assert.NotNull(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cache.GeneratedPath)));
        Assert.Equal(new[] { "新しい" }, JsonSerializer.Deserialize<string[]>(File.ReadAllText(cache.PendingPath)));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Load_ignores_null_or_empty_generated_entries()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "generated.zh_Hans.json"), "{\"スキル\":null,\"\":\"x\",\"回復する\":\"进行恢复\"}");

        var cache = new TranslationCache(root);
        cache.Load();

        Assert.False(cache.TryGetGenerated("スキル", out _));
        Assert.False(cache.TryGetGenerated(string.Empty, out _));
        Assert.True(cache.TryGetGenerated("回復する", out var translated));
        Assert.Equal("进行恢复", translated);
    }

    [Fact]
    public void Load_removes_source_identical_generated_entries_for_migration()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "generated.zh_Hans.json"), "{\"同じする\":\"同じする\"}");
        var cache = new TranslationCache(root);
        cache.Load();
        var priority = new List<string>();
        var resolver = new TranslationResolver(cache, priority.Add, _ => { });

        Assert.False(cache.TryGetGenerated("同じする", out _));
        Assert.Equal("同じする", resolver.Resolve("同じする"));
        Assert.Equal(new[] { "同じする" }, priority);
        Assert.Equal(new[] { "同じする" }, cache.PendingSnapshot());
        cache.Flush();
        Assert.Empty(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cache.GeneratedPath))!);
    }

    [Fact]
    public void Observe_does_not_requeue_an_already_generated_template()
    {
        var cache = new TranslationCache(root);
        cache.StoreGenerated("回復する", "进行恢复");

        Assert.False(cache.ObserveNormal("回復する", "回復する"));
        Assert.Empty(cache.PendingSnapshot());
    }

    [Fact]
    public void Pending_generation_action_runs_only_for_the_current_generation()
    {
        var cache = new TranslationCache(root);
        Assert.True(cache.ObservePriority("世代する", "世代する"));
        Assert.True(cache.TryGetPendingGeneration("世代する", out var oldGeneration));
        var calls = 0;

        Assert.True(cache.TryIfPendingGeneration("世代する", oldGeneration, () => calls++));
        Assert.True(cache.CancelPending("世代する"));
        Assert.True(cache.ObservePriority("世代する", "世代する"));
        Assert.False(cache.TryIfPendingGeneration("世代する", oldGeneration, () => calls++));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Runtime_raw_samples_are_normalized_and_bounded()
    {
        var cache = new TranslationCache(root);
        cache.ObserveNormal("数字 12する", "数字 {0}する");
        for (var index = 0; index < 5000; index++)
        {
            var source = "表示する-" + Suffix(index);
            cache.ObserveNormal(source, source);
        }

        cache.Flush();

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cache.RawPath));
        Assert.Contains("数字 {0}する", raw!.Keys);
        Assert.InRange(raw.Count, 1, 4096);
    }

    [Fact]
    public void Runtime_reverse_indexes_evict_the_least_recently_used_value()
    {
        var cache = new TranslationCache(root);
        for (var index = 0; index < 4096; index++)
        {
            var suffix = Suffix(index);
            cache.RememberResolution("源する-" + suffix, "译-" + suffix);
        }

        Assert.True(cache.TryGetSourceForTranslatedValue("译-" + Suffix(0), out _));
        cache.RememberResolution("源する-" + Suffix(4096), "译-" + Suffix(4096));

        Assert.True(cache.TryGetSourceForTranslatedValue("译-" + Suffix(0), out _));
        Assert.False(cache.TryGetSourceForTranslatedValue("译-" + Suffix(1), out _));
        Assert.False(cache.IsKnownTranslatedValue("译-" + Suffix(1)));
    }

    [Fact]
    public async Task Persistence_loop_retries_a_failed_write_without_another_mutation()
    {
        var generatedAttempts = 0;
        var cache = new TranslationCache(root, writeAtomic: (path, _) =>
        {
            if (Path.GetFileName(path) == "generated.zh_Hans.json") generatedAttempts++;
            return generatedAttempts > 1;
        });
        cache.ObservePriority("再試行する", "再試行する");
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var loop = cache.RunPersistenceLoopAsync(source.Token);
        while (Volatile.Read(ref generatedAttempts) < 2) await Task.Delay(10, source.Token);
        source.Cancel();
        await loop;

        Assert.True(generatedAttempts >= 2);
    }

    [Fact]
    public void Terminal_flush_retries_a_transient_final_write()
    {
        var generatedAttempts = 0;
        var cache = new TranslationCache(root, writeAtomic: (path, _) =>
        {
            if (Path.GetFileName(path) == "generated.zh_Hans.json")
                return ++generatedAttempts > 1;
            return true;
        });
        Assert.True(cache.ObservePriority("最後する", "最後する"));
        cache.FreezeMutations();

        Assert.True(cache.FlushTerminal());
        Assert.Equal(2, generatedAttempts);
    }

    [Fact]
    public async Task Persistent_write_failures_are_paced()
    {
        var attempts = new List<DateTime>();
        var cache = new TranslationCache(
            root,
            writeAtomic: (path, _) =>
            {
                if (Path.GetFileName(path) == "generated.zh_Hans.json")
                {
                    lock (attempts) attempts.Add(DateTime.UtcNow);
                }
                return false;
            },
            persistenceRetryDelay: TimeSpan.FromMilliseconds(80));
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var loop = cache.RunPersistenceLoopAsync(source.Token);
        cache.ObservePriority("継続する", "継続する");
        while (true)
        {
            lock (attempts)
            {
                if (attempts.Count >= 3) break;
            }
            await Task.Delay(10, source.Token);
        }
        source.Cancel();
        await loop;

        Assert.True(attempts[1] - attempts[0] >= TimeSpan.FromMilliseconds(60));
        Assert.True(attempts[2] - attempts[1] >= TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public async Task Mutation_during_write_is_followed_by_a_newer_snapshot()
    {
        var firstPendingWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingWrites = new List<string>();
        var cache = new TranslationCache(root, writeAtomic: (path, json) =>
        {
            if (Path.GetFileName(path) != "pending.zh_Hans.json") return true;
            lock (pendingWrites) pendingWrites.Add(json);
            if (pendingWrites.Count == 1)
            {
                firstPendingWrite.TrySetResult();
                releaseFirstWrite.Task.GetAwaiter().GetResult();
            }
            return true;
        });
        cache.ObservePriority("最初する", "最初する");
        var firstFlush = Task.Run(cache.Flush);
        await firstPendingWrite.Task;

        cache.ObservePriority("新規する", "新規する");
        releaseFirstWrite.SetResult();
        await firstFlush;
        cache.Flush();

        Assert.Equal(2, pendingWrites.Count);
        Assert.Equal(
            new[] { "最初する", "新規する" },
            JsonSerializer.Deserialize<string[]>(pendingWrites[^1]));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static string Suffix(int value)
    {
        var chars = new char[4];
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = (char)('a' + value % 26);
            value /= 26;
        }
        return new string(chars);
    }
}
