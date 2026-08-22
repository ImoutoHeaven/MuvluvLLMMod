using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TranslationBudgetTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "MuvluvLLMMod.Budget.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Long_runtime_inputs_fail_closed_without_retention()
    {
        var diagnostics = new List<string>();
        var cache = new TranslationCache(root, diagnostics.Add);
        var longKana = new string('あ', TranslationBudget.MaxTextUtf16CodeUnits + 1);
        var longChinese = new string('译', TranslationBudget.MaxTextUtf16CodeUnits + 1);

        Assert.False(TextTemplate.IsTranslationCandidate(longKana));
        Assert.False(cache.ObserveNormal(longKana, longKana));
        Assert.False(cache.StoreGenerated(longKana, longChinese));
        cache.RememberResolution(longKana, longChinese);
        var provenance = new TmpTranslationProvenance();
        var assignment = provenance.BeginExternalSetter(new object(), 1);
        provenance.Record(assignment, longChinese, longKana);

        var snapshot = cache.RetainedSnapshot;
        Assert.Equal(0, snapshot.GeneratedCount);
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal(0, snapshot.RawCount);
        Assert.Equal(0, snapshot.ReverseCount);
        Assert.Equal(0, provenance.Count);
        Assert.Equal(0, provenance.RetainedUtf8Bytes);
        Assert.InRange(diagnostics.Count, 0, 2);
    }

    [Fact]
    public void Cache_working_sets_are_bounded_by_count_and_utf8_payload()
    {
        var cache = new TranslationCache(root);
        for (var index = 0; index < TranslationBudget.MaxPendingEntries + 500; index++)
        {
            var source = "表示する" + index;
            cache.ObservePriority(source, source);
        }

        for (var index = 0; index < TranslationBudget.MaxGeneratedEntries + 500; index++)
        {
            var source = "生成する" + index;
            Assert.True(cache.StoreGenerated(source, "生成译" + index));
        }

        for (var index = 0; index < TranslationBudget.MaxReverseEntries + 500; index++)
            cache.RememberResolution("原文する" + index, "反向译" + index);

        var snapshot = cache.RetainedSnapshot;
        Assert.InRange(snapshot.GeneratedCount, 0, TranslationBudget.MaxGeneratedEntries);
        Assert.InRange(snapshot.GeneratedUtf8Bytes, 0, TranslationBudget.MaxGeneratedUtf8Bytes);
        Assert.InRange(snapshot.PendingCount, 0, TranslationBudget.MaxPendingEntries);
        Assert.InRange(snapshot.PendingUtf8Bytes, 0, TranslationBudget.MaxPendingUtf8Bytes);
        Assert.InRange(snapshot.RawCount, 0, TranslationBudget.MaxRawEntries);
        Assert.InRange(snapshot.RawUtf8Bytes, 0, TranslationBudget.MaxRawUtf8Bytes);
        Assert.InRange(snapshot.ReverseCount, 0, TranslationBudget.MaxReverseEntries);
        Assert.InRange(snapshot.ReverseUtf8Bytes, 0, TranslationBudget.MaxReverseUtf8Bytes);
        Assert.InRange(snapshot.PromotedPendingCount, 0, TranslationBudget.MaxPendingEntries);
        Assert.InRange(snapshot.PromotedPendingUtf8Bytes, 0, TranslationBudget.MaxPendingUtf8Bytes);
    }

    [Fact]
    public void Worker_bookkeeping_is_bounded_without_reflection()
    {
        var queue = new TranslationWorkQueue();
        for (var index = 0; index < TranslationBudget.MaxWorkItems + 500; index++)
        {
            var template = "キューする" + index;
            if (!queue.Enqueue(template))
                continue;
            Assert.True(queue.TryDequeue(out var dequeuedTemplate));
            queue.Cancel(dequeuedTemplate);
        }

        var queueSnapshot = queue.RetainedSnapshot;
        Assert.InRange(queueSnapshot.ScheduledCount, 0, TranslationBudget.MaxWorkItems);
        Assert.InRange(queueSnapshot.ScheduledUtf8Bytes, 0, TranslationBudget.MaxWorkItemUtf8Bytes);
        Assert.InRange(queueSnapshot.CanceledCount, 0, TranslationBudget.MaxCancellationEntries);
        Assert.InRange(queueSnapshot.CanceledUtf8Bytes, 0, TranslationBudget.MaxCancellationUtf8Bytes);

        var backlog = new TranslationPriorityBacklog();
        var retry = new TranslationRetryPolicy(1);
        var progress = new TranslationProgress();
        for (var index = 0; index < TranslationBudget.MaxPriorityBacklogItems + 500; index++)
        {
            var template = "優先する" + index;
            backlog.Enqueue(template, index + 1);
            retry.RecordFailure(template);
            progress.Start();
            progress.Fail(template);
        }

        var backlogSnapshot = backlog.RetainedSnapshot;
        Assert.InRange(backlogSnapshot.ScheduledCount, 0, TranslationBudget.MaxPriorityBacklogItems);
        Assert.InRange(backlogSnapshot.ScheduledUtf8Bytes, 0, TranslationBudget.MaxPriorityBacklogUtf8Bytes);
        Assert.InRange(backlogSnapshot.CanceledPendingCount, 0, TranslationBudget.MaxCancellationEntries);
        Assert.InRange(backlogSnapshot.CanceledPendingUtf8Bytes, 0, TranslationBudget.MaxCancellationUtf8Bytes);
        Assert.InRange(retry.StateCount, 0, TranslationBudget.MaxRetryStates);
        Assert.InRange(retry.RetainedUtf8Bytes, 0, TranslationBudget.MaxRetryStateUtf8Bytes);
        Assert.InRange(progress.FailedCount, 0, TranslationBudget.MaxFailedProgressEntries);
        Assert.InRange(progress.RetainedUtf8Bytes, 0, TranslationBudget.MaxFailedProgressUtf8Bytes);
    }

    [Fact]
    public void Oversized_cache_file_is_rejected_before_json_materialization()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "generated.zh_Hans.json");
        File.WriteAllText(
            path,
            "{\"あする\":\"" + new string('译', TranslationBudget.MaxCacheFileBytes) + "\"}",
            Encoding.UTF8);
        var diagnostics = new List<string>();
        var cache = new TranslationCache(root, diagnostics.Add);

        cache.Load();

        Assert.Equal(0, cache.GeneratedCount);
        Assert.Contains(diagnostics, value => value.Contains("exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public void Concurrent_observation_never_exceeds_the_cache_budget()
    {
        var cache = new TranslationCache(root);
        Parallel.For(0, TranslationBudget.MaxPendingEntries * 3, index =>
        {
            var source = "同時する" + index;
            cache.ObserveNormal(source, source);
            cache.RememberResolution(source, "同時译" + index);
        });

        var snapshot = cache.RetainedSnapshot;
        Assert.InRange(snapshot.PendingCount, 0, TranslationBudget.MaxPendingEntries);
        Assert.InRange(snapshot.PendingUtf8Bytes, 0, TranslationBudget.MaxPendingUtf8Bytes);
        Assert.InRange(snapshot.ReverseCount, 0, TranslationBudget.MaxReverseEntries);
        Assert.InRange(snapshot.ReverseUtf8Bytes, 0, TranslationBudget.MaxReverseUtf8Bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}

public sealed class OpenAiBudgetTests
{
    [Fact]
    public async Task Oversized_response_body_is_rejected_before_unbounded_string_read()
    {
        var calls = 0;
        var diagnostics = new List<string>();
        using var http = new HttpClient(new DelegateHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            var content = new string('译', TranslationBudget.MaxResponseBodyBytes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }),
                    Encoding.UTF8,
                    "application/json")
            });
        }));
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 1),
            new RequestRateLimiter(1000),
            diagnostics.Add);

        Assert.Null(await client.TranslateAsync("スキル", CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Single(diagnostics);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
