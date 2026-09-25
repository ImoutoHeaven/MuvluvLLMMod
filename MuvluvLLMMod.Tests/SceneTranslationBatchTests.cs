using Xunit;

namespace MuvluvLLMMod.Tests;

/// <summary>
/// Whole-scene batch protocol. The contract under test is all-or-nothing: any structural or
/// per-target defect must reject the entire scene so the caller can fall back to the per-string
/// path instead of publishing a partially translated scene.
/// </summary>
public sealed class SceneTranslationBatchTests
{
    private const long SceneId = 40003301;

    private static SceneTranslationBatch Create(params string[] sources) =>
        SceneTranslationBatch.TryCreate(SceneId, sources)!;

    private static string Response(long sceneId, params string[] entries)
    {
        var items = entries.Select(entry => entry).ToArray();
        return "{\"version\":1,\"sceneId\":" + sceneId
            + ",\"translations\":[" + string.Join(",", items) + "]}";
    }

    private static string Entry(string id, string text) =>
        "{\"id\":\"" + id + "\",\"text\":\"" + text + "\"}";

    [Fact]
    public void TryCreate_returns_null_when_nothing_is_a_candidate()
    {
        Assert.Null(SceneTranslationBatch.TryCreate(SceneId, new[] { "hello", string.Empty }));
    }

    [Fact]
    public void TryCreate_deduplicates_repeated_sources()
    {
        var batch = Create("こんにちは", "こんにちは", "さようなら");

        Assert.Equal(2, batch.TargetCount);
    }

    [Fact]
    public void TryCreate_skips_text_without_kana()
    {
        // Pure kanji and latin are not candidates; katakana is kana and therefore is one.
        var batch = Create("潜航", "hello", "こんにちは");

        Assert.Equal(1, batch.TargetCount);
    }

    [Fact]
    public void TryCreate_keeps_katakana_only_text()
    {
        var batch = Create("シリウスシュガー");

        Assert.Equal(1, batch.TargetCount);
    }

    [Fact]
    public void Prompt_carries_the_version_scene_and_protected_targets()
    {
        var batch = Create("こんにちは");

        Assert.Contains("\"version\":1", batch.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"sceneId\":" + SceneId, batch.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"t0000\"", batch.Prompt, StringComparison.Ordinal);
        Assert.Contains("こんにちは", batch.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_protects_markup_and_line_breaks()
    {
        // A ruby line and a break must both become protected tokens before the model sees them.
        var batch = Create("<r=ダイブ>潜航</r>\n開始せよ");

        Assert.Contains("__MLM_FMT_0__", batch.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<r=", batch.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseResponse_maps_each_target_to_its_translation()
    {
        var batch = Create("こんにちは", "さようなら");

        var ok = batch.TryParseResponse(
            Response(SceneId, Entry("t0000", "你好"), Entry("t0001", "再见")),
            out var translations);

        Assert.True(ok);
        Assert.Equal("你好", translations["こんにちは"]);
        Assert.Equal("再见", translations["さようなら"]);
    }

    [Fact]
    public void TryParseResponse_accepts_a_code_fenced_body()
    {
        var batch = Create("こんにちは");
        var body = "```json\n" + Response(SceneId, Entry("t0000", "你好")) + "\n```";

        Assert.True(batch.TryParseResponse(body, out var translations));
        Assert.Equal("你好", translations["こんにちは"]);
    }

    [Fact]
    public void TryParseResponse_rejects_a_mismatched_scene_id()
    {
        var batch = Create("こんにちは");

        Assert.False(batch.TryParseResponse(Response(SceneId + 1, Entry("t0000", "你好")), out _));
    }

    [Fact]
    public void TryParseResponse_rejects_a_mismatched_version()
    {
        var batch = Create("こんにちは");
        var body = "{\"version\":2,\"sceneId\":" + SceneId
            + ",\"translations\":[" + Entry("t0000", "你好") + "]}";

        Assert.False(batch.TryParseResponse(body, out _));
    }

    [Fact]
    public void TryParseResponse_rejects_a_missing_entry()
    {
        var batch = Create("こんにちは", "さようなら");

        Assert.False(batch.TryParseResponse(Response(SceneId, Entry("t0000", "你好")), out _));
    }

    [Fact]
    public void TryParseResponse_rejects_a_surplus_entry()
    {
        var batch = Create("こんにちは");

        Assert.False(
            batch.TryParseResponse(
                Response(SceneId, Entry("t0000", "你好"), Entry("t0001", "多余")),
                out _));
    }

    [Fact]
    public void TryParseResponse_rejects_reordered_entries()
    {
        var batch = Create("こんにちは", "さようなら");

        // Same count and ids, wrong order: accepting this would shift text onto the wrong lines.
        Assert.False(
            batch.TryParseResponse(
                Response(SceneId, Entry("t0001", "再见"), Entry("t0000", "你好")),
                out _));
    }

    [Fact]
    public void TryParseResponse_rejects_a_duplicated_id()
    {
        var batch = Create("こんにちは", "さようなら");

        Assert.False(
            batch.TryParseResponse(
                Response(SceneId, Entry("t0000", "你好"), Entry("t0000", "你好")),
                out _));
    }

    [Fact]
    public void TryParseResponse_rejects_an_empty_translation()
    {
        var batch = Create("こんにちは");

        Assert.False(batch.TryParseResponse(Response(SceneId, Entry("t0000", "   ")), out _));
    }

    [Fact]
    public void TryParseResponse_rejects_a_dropped_protected_token()
    {
        // The source carries a ruby tag; losing it would corrupt the rendered line.
        var batch = Create("<r=ダイブ>潜航</r>を開始せよ");

        Assert.False(
            batch.TryParseResponse(Response(SceneId, Entry("t0000", "开始潜航")), out _));
    }

    [Fact]
    public void TryParseResponse_rejects_a_reordered_protected_token()
    {
        // Ruby readings are katakana, so this source is a candidate; the tags become tokens.
        var batch = Create("前<r=あ>甲</r>後<r=い>乙</r>");

        // Tokens present but in the wrong order must not be accepted.
        Assert.False(
            batch.TryParseResponse(
                Response(SceneId, Entry("t0000", "后__MLM_FMT_1__后__MLM_FMT_0__")),
                out _));
    }

    [Fact]
    public void TryParseResponse_preserves_a_ruby_tag_that_survives()
    {
        var batch = Create("潜航<r=ダイブ>開始</r>");

        var ok = batch.TryParseResponse(
            Response(SceneId, Entry("t0000", "开始__MLM_FMT_0____MLM_FMT_1__")),
            out var translations);

        Assert.True(ok);
        Assert.Contains("<r=ダイブ>", translations["潜航<r=ダイブ>開始</r>"], StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseResponse_rejects_an_identity_translation()
    {
        var batch = Create("こんにちは");

        Assert.False(batch.TryParseResponse(Response(SceneId, Entry("t0000", "こんにちは")), out _));
    }

    [Fact]
    public void TryParseResponse_rejects_malformed_json()
    {
        var batch = Create("こんにちは");

        Assert.False(batch.TryParseResponse("{not json", out _));
        Assert.False(batch.TryParseResponse(null, out _));
        Assert.False(batch.TryParseResponse("   ", out _));
    }

    [Fact]
    public void TryParseResponse_rejects_a_dropped_line_break()
    {
        var batch = Create("いち\nに");

        Assert.False(
            batch.TryParseResponse(Response(SceneId, Entry("t0000", "一二")), out _));
    }
}
