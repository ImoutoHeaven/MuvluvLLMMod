using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TranslationResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.Resolver.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Curated_wins_over_generated_and_equal_curated_falls_through()
    {
        var cache = new TranslationCache(root);
        cache.StoreGenerated("スキル {0}", "本地技能 {0}");
        var resolver = new TranslationResolver(cache, _ => { }, _ => { });

        Assert.Equal("远程技能 12", resolver.Resolve("スキル 12", "远程技能 12"));
        Assert.Equal("本地技能 12", resolver.Resolve("スキル 12", "スキル 12"));
    }

    [Fact]
    public void Missing_scene_or_key_still_uses_generated_then_priority_fallback()
    {
        var priority = new List<string>();
        var cache = new TranslationCache(root);
        cache.StoreGenerated("場面 {0}", "场景 {0}");
        var resolver = new TranslationResolver(cache, priority.Add, _ => { });
        IReadOnlyDictionary<string, string> missingScene = new Dictionary<string, string>();

        Assert.Equal("场景 7", resolver.ResolveFromDictionary("場面 7", missingScene));
        Assert.Equal("新しい台詞", resolver.ResolveFromDictionary("新しい台詞", new Dictionary<string, string> { ["別"] = "其他" }));
        Assert.Equal(new[] { "新しい台詞" }, priority);
        Assert.Equal(new[] { "新しい台詞" }, cache.PendingSnapshot());
    }

    [Fact]
    public void Pending_scene_defers_generated_and_priority_fallback()
    {
        var priority = new List<string>();
        var cache = new TranslationCache(root);
        cache.StoreGenerated("既存する", "已有翻译");
        var resolver = new TranslationResolver(cache, priority.Add, _ => { });

        Assert.Equal("既存する", resolver.ResolveFromDictionary("既存する", null));
        Assert.Equal("新規する", resolver.ResolveFromDictionary("新規する", null));
        Assert.Empty(priority);
        Assert.Empty(cache.PendingSnapshot());
    }

    [Fact]
    public void Curated_publication_cancels_covered_pending_work()
    {
        var priority = new List<string>();
        var canceled = new List<string>();
        var cache = new TranslationCache(root);
        var resolver = new TranslationResolver(cache, priority.Add, _ => { }, canceled.Add);
        resolver.Resolve("予約する");

        var curated = new Dictionary<string, string> { ["予約する"] = "远程译文" };
        Assert.Equal("远程译文", resolver.ResolveFromDictionary("予約する", curated));
        Assert.Empty(cache.PendingSnapshot());
        Assert.Equal(new[] { "予約する" }, canceled);
    }

    [Fact]
    public void Curated_numeric_variant_does_not_cancel_uncovered_sibling_template()
    {
        var queue = new TranslationWorkQueue();
        var cache = new TranslationCache(root);
        var resolver = new TranslationResolver(
            cache,
            value => queue.EnqueuePriority(value),
            value => queue.Enqueue(value),
            value => queue.Cancel(value));
        resolver.Resolve("攻撃を20回する");
        var curated = new Dictionary<string, string> { ["攻撃を10回する"] = "攻击10次" };

        Assert.Equal("攻击10次", resolver.ResolveFromDictionary("攻撃を10回する", curated));
        Assert.Equal(new[] { "攻撃を{0}回する" }, cache.PendingSnapshot());
        Assert.True(queue.TryDequeue(out var uncovered));
        Assert.Equal("攻撃を{0}回する", uncovered);
    }

    [Fact]
    public void Publishing_a_scene_reconciles_covered_work_not_seen_during_the_request()
    {
        var canceled = new List<string>();
        var cache = new TranslationCache(root);
        var resolver = new TranslationResolver(cache, _ => { }, _ => { }, canceled.Add);
        resolver.Resolve("以前から保留する");

        resolver.PublishCurated(
            new Dictionary<string, string> { ["以前から保留する"] = "远程译文" });
        Assert.Empty(cache.PendingSnapshot());
        Assert.Equal(new[] { "以前から保留する" }, canceled);
    }

    [Fact]
    public void Scenario_generic_lookup_uses_generated_without_enqueuing()
    {
        var priority = new List<string>();
        var cache = new TranslationCache(root);
        cache.StoreGenerated("完全する", "完整译文");
        var resolver = new TranslationResolver(cache, priority.Add, _ => { });

        Assert.Equal("完整译文", resolver.Lookup("完全する"));
        Assert.Equal("途中する", resolver.Lookup("途中する"));
        Assert.Empty(priority);
        Assert.Empty(cache.PendingSnapshot());
    }

    [Fact]
    public void Invalid_generated_format_falls_through_while_non_kana_never_queues()
    {
        var priority = new List<string>();
        var cache = new TranslationCache(root);
        cache.StoreGenerated("スキル {0}", "技能");
        var resolver = new TranslationResolver(cache, priority.Add, _ => { });

        Assert.Equal("スキル 12", resolver.Resolve("スキル 12"));
        Assert.Equal("確認", resolver.Resolve("確認"));
        Assert.Equal(new[] { "スキル {0}" }, priority);
    }

    [Fact]
    public void Static_context_is_exact_and_unchanged_static_work_is_normal()
    {
        var normal = new List<string>();
        var priority = new List<string>();
        var cache = new TranslationCache(root);
        var resolver = new TranslationResolver(cache, priority.Add, normal.Add);
        var catalog = StaticTranslationCatalog.Parse(
            @"{ ""SkillMaster"": { ""Name"": { ""烈火スキル"": ""烈火技能"", ""未翻訳する"": ""未翻訳する"" } } }");

        Assert.Equal("烈火技能", resolver.ResolveStatic("烈火スキル", catalog, "SkillMaster", "Name"));
        Assert.Equal("烈火スキル", resolver.ResolveStatic("烈火スキル", catalog, "ItemMaster", "Name"));
        foreach (var template in catalog.UnchangedTemplates) resolver.ObserveNormal(template);
        Assert.Equal(new[] { "烈火スキル" }, priority);
        Assert.Equal(new[] { "未翻訳する" }, normal);
    }

    [Fact]
    public void Repeated_runtime_miss_waits_for_periodic_normal_retry_after_failure()
    {
        var queue = new TranslationWorkQueue();
        var cache = new TranslationCache(root);
        var resolver = new TranslationResolver(cache, value => queue.EnqueuePriority(value), value => queue.Enqueue(value));

        Assert.Equal("失敗する", resolver.Resolve("失敗する"));
        Assert.True(queue.TryDequeue(out var first));
        queue.Finish(first, false);

        Assert.Equal("失敗する", resolver.Resolve("失敗する"));
        Assert.False(queue.TryDequeue(out _));

        Assert.True(queue.Enqueue(first));
        Assert.True(queue.TryDequeue(out var retry));
        Assert.Equal("失敗する", retry);
    }

    [Fact]
    public void Runtime_encounter_promotes_static_and_durable_normal_pending_once()
    {
        var queue = new TranslationWorkQueue();
        var cache = new TranslationCache(root);
        cache.ObserveNormal("先行する", "先行する");
        cache.ObserveNormal("永続する", "永続する");
        cache.Flush();
        cache = new TranslationCache(root);
        cache.Load();
        cache.ObserveNormal("静的する", "静的する");
        queue.Enqueue("先行する");
        queue.Enqueue("永続する");
        queue.Enqueue("静的する");
        var resolver = new TranslationResolver(cache, value => queue.EnqueuePriority(value), value => queue.Enqueue(value));

        resolver.Resolve("永続する");
        resolver.Resolve("静的する");
        resolver.Resolve("永続する");
        resolver.Resolve("静的する");

        Assert.True(queue.TryDequeue(out var durable));
        Assert.Equal("永続する", durable);
        Assert.True(queue.TryDequeue(out var staticWork));
        Assert.Equal("静的する", staticWork);
        Assert.True(queue.TryDequeue(out var normal));
        Assert.Equal("先行する", normal);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void Kana_bearing_generated_output_is_idempotent_and_not_requeued()
    {
        var priority = new List<string>();
        var cache = new TranslationCache(root);
        cache.StoreGenerated("原文する", "翻訳する");
        var resolver = new TranslationResolver(cache, priority.Add, _ => { });

        var generated = resolver.Resolve("原文する");
        var repeated = resolver.Resolve(generated);

        Assert.Equal("翻訳する", generated);
        Assert.Equal("翻訳する", repeated);
        Assert.Empty(priority);
    }

    [Fact]
    public void Later_curated_publication_overrides_an_existing_generated_display()
    {
        var cache = new TranslationCache(root);
        cache.StoreGenerated("原文する", "生成翻訳する");
        var resolver = new TranslationResolver(cache, _ => { }, _ => { });
        var displayed = resolver.Resolve("原文する");
        var curated = new Dictionary<string, string> { ["原文する"] = "人工翻译" };

        Assert.Equal("人工翻译", resolver.ResolveFromDictionary(displayed, curated));
    }

    [Fact]
    public void Ambiguous_shared_output_never_uses_the_wrong_source_for_curated_lookup()
    {
        var priority = new List<string>();
        var cache = new TranslationCache(root);
        cache.StoreGenerated("一つする", "共通する");
        var resolver = new TranslationResolver(cache, priority.Add, _ => { });
        var firstDisplay = resolver.Resolve("一つする");
        cache.StoreGenerated("二つする", "共通する");
        var wrongCurated = new Dictionary<string, string> { ["二つする"] = "错误译文" };

        Assert.False(cache.TryGetSourceForTranslatedValue("共通する", out _));
        Assert.Equal("共通する", resolver.Resolve(firstDisplay, "错误译文"));
        Assert.Equal("共通する", resolver.ResolveFromDictionary(firstDisplay, wrongCurated));
        Assert.Empty(priority);
    }

    [Fact]
    public void Generated_output_observed_as_a_real_source_invalidates_reverse_identity()
    {
        var cache = new TranslationCache(root);
        cache.StoreGenerated("最初する", "別物する");
        var resolver = new TranslationResolver(cache, _ => { }, _ => { });
        var displayed = resolver.Resolve("最初する");
        var currentContext = new Dictionary<string, string>
        {
            ["最初する"] = "错误译文",
            ["別物する"] = "正确译文"
        };

        Assert.Equal("正确译文", resolver.ResolveFromDictionary(displayed, currentContext));
        Assert.False(cache.TryGetSourceForTranslatedValue("別物する", out _));
        Assert.Equal("別物する", resolver.ResolveFromDictionary(displayed, new Dictionary<string, string> { ["最初する"] = "错误译文" }));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
