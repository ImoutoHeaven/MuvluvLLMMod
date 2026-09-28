using System.Text.Json.Nodes;
using Xunit;

namespace MuvluvLLMMod.Tests;

/// <summary>
/// Whole-scene batch protocol. A structural defect rejects the batch, because entries can no
/// longer be matched to lines. A defect in one entry drops only that entry, so the rest of the
/// scene is kept and the dropped line stays on the per-string path.
/// </summary>
public sealed class SceneTranslationBatchTests
{
    private const long SceneId = 40003301;

    private static SceneTranslationBatch Create(params string[] sources) =>
        SceneTranslationBatch.TryCreate(SceneId, sources.Select(source => new SceneDialogueLine(source, null)))!;

    private static string Response(long sceneId, params string[] entries) =>
        "{\"version\":1,\"sceneId\":" + sceneId
        + ",\"translations\":[" + string.Join(",", entries) + "]}";

    private static string Entry(string id, string text) =>
        "{\"id\":\"" + id + "\",\"text\":\"" + text + "\"}";

    private static IReadOnlyDictionary<string, string> Accepted(SceneTranslationBatch batch, string response)
    {
        Assert.True(batch.TryParseResponse(response, out var translations));
        return translations;
    }

    [Fact]
    public void TryCreate_returns_null_when_nothing_is_a_candidate()
    {
        Assert.Null(SceneTranslationBatch.TryCreate(
            SceneId,
            new[] { new SceneDialogueLine("hello", null), new SceneDialogueLine(string.Empty, null) }));
    }

    [Fact]
    public void TryCreate_keeps_each_speaker_of_a_repeated_line()
    {
        // Corpus scene 10120109 gives this line to two speakers; both must reach the model.
        var batch = SceneTranslationBatch.TryCreate(SceneId, new[]
        {
            new SceneDialogueLine("それはそうだけど……", "レイラ"),
            new SceneDialogueLine("それはそうだけど……", "エヴィ"),
        })!;

        var targets = JsonNode.Parse(batch.Prompt)!["targets"]!.AsArray();
        Assert.Equal(2, batch.TargetCount);
        Assert.Equal("レイラ", targets[0]!["name"]!.GetValue<string>());
        Assert.Equal("エヴィ", targets[1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void TryParseResponse_keeps_the_first_valid_translation_of_a_repeated_line()
    {
        var batch = Create("こんにちは", "こんにちは");

        // An invalid first occurrence does not block a valid later one...
        Assert.Equal(
            "你好",
            Accepted(batch, Response(SceneId, Entry("t0000", "こんにちは"), Entry("t0001", "你好")))["こんにちは"]);

        // ...and between two valid ones the first wins.
        Assert.Equal(
            "你好",
            Accepted(batch, Response(SceneId, Entry("t0000", "你好"), Entry("t0001", "您好")))["こんにちは"]);
    }

    [Fact]
    public void Player_name_placeholder_is_protected()
    {
        const string source = "%usernameusernameuserna%さん、こんにちは";
        var batch = Create(source);

        Assert.DoesNotContain("%username", batch.Prompt, StringComparison.Ordinal);
        Assert.Empty(Accepted(batch, Response(SceneId, Entry("t0000", "你好，玩家"))));
        Assert.Equal(
            "%usernameusernameuserna%，你好",
            Accepted(batch, Response(SceneId, Entry("t0000", "__MLM_FMT_0__，你好")))[source]);
    }

    [Fact]
    public void TryParseResponse_drops_only_an_entry_whose_text_is_not_a_string()
    {
        var batch = Create("こんにちは", "さようなら");

        var translations = Accepted(
            batch,
            Response(SceneId, Entry("t0000", "你好"), "{\"id\":\"t0001\",\"text\":123}"));

        Assert.Equal("你好", Assert.Single(translations).Value);
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
    public void Prompt_carries_each_speaker_name_in_story_order()
    {
        var batch = SceneTranslationBatch.TryCreate(SceneId, new[]
        {
            new SceneDialogueLine("まさか……", "威厳のある女性"),
            new SceneDialogueLine("最後の言葉が、震える。", null),
            new SceneDialogueLine("すまない。", "？？？"),
        })!;

        var targets = JsonNode.Parse(batch.Prompt)!["targets"]!.AsArray();
        Assert.Equal(3, targets.Count);
        Assert.Equal("威厳のある女性", targets[0]!["name"]!.GetValue<string>());
        Assert.Null(targets[1]!["name"]);
        Assert.Equal("？？？", targets[2]!["name"]!.GetValue<string>());
        Assert.Equal("すまない。", targets[2]!["source"]!.GetValue<string>());
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
    public void Targets_are_normalized_templates()
    {
        // Numbers become placeholders exactly as on the per-string path, so the stored generated
        // template serves every rendering of the line.
        var batch = Create("あと3分です");

        var translations = Accepted(
            batch,
            Response(SceneId, Entry("t0000", "还有__MLM_FMT_0__分钟")));

        Assert.Equal("还有{0}分钟", translations["あと{0}分です"]);
    }

    [Fact]
    public void TryParseResponse_maps_each_target_to_its_translation()
    {
        var batch = Create("こんにちは", "さようなら");

        var translations = Accepted(
            batch,
            Response(SceneId, Entry("t0000", "你好"), Entry("t0001", "再见")));

        Assert.Equal("你好", translations["こんにちは"]);
        Assert.Equal("再见", translations["さようなら"]);
    }

    [Fact]
    public void TryParseResponse_accepts_a_code_fenced_body()
    {
        var batch = Create("こんにちは");
        var body = "```json\n" + Response(SceneId, Entry("t0000", "你好")) + "\n```";

        Assert.Equal("你好", Accepted(batch, body)["こんにちは"]);
    }

    [Fact]
    public void TryParseResponse_keeps_valid_entries_when_one_entry_is_invalid()
    {
        var batch = Create("こんにちは", "<r=ダイブ>潜航</r>を開始せよ", "さようなら");

        // The middle entry drops its ruby tokens; only that line is left to the per-string path.
        var translations = Accepted(
            batch,
            Response(SceneId, Entry("t0000", "你好"), Entry("t0001", "开始潜航"), Entry("t0002", "再见")));

        Assert.Equal(2, translations.Count);
        Assert.Equal("你好", translations["こんにちは"]);
        Assert.Equal("再见", translations["さようなら"]);
        Assert.False(translations.ContainsKey("<r=ダイブ>潜航</r>を開始せよ"));
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
    public void TryParseResponse_rejects_a_null_entry()
    {
        var batch = Create("こんにちは");

        Assert.False(batch.TryParseResponse(Response(SceneId, "null"), out _));
    }

    [Fact]
    public void TryParseResponse_drops_an_empty_translation()
    {
        Assert.Empty(Accepted(Create("こんにちは"), Response(SceneId, Entry("t0000", "   "))));
    }

    [Fact]
    public void TryParseResponse_drops_a_dropped_protected_token()
    {
        // The source carries a ruby tag; losing it would corrupt the rendered line.
        var batch = Create("<r=ダイブ>潜航</r>を開始せよ");

        Assert.Empty(Accepted(batch, Response(SceneId, Entry("t0000", "开始潜航"))));
    }

    [Fact]
    public void TryParseResponse_drops_a_reordered_protected_token()
    {
        // Ruby readings are katakana, so this source is a candidate; the tags become tokens.
        var batch = Create("前<r=あ>甲</r>後<r=い>乙</r>");

        Assert.Empty(Accepted(batch, Response(SceneId, Entry("t0000", "后__MLM_FMT_1__后__MLM_FMT_0__"))));
    }

    [Fact]
    public void TryParseResponse_preserves_a_ruby_tag_that_survives()
    {
        var batch = Create("潜航<r=ダイブ>開始</r>");

        var translations = Accepted(
            batch,
            Response(SceneId, Entry("t0000", "开始__MLM_FMT_0____MLM_FMT_1__")));

        Assert.Contains("<r=ダイブ>", translations["潜航<r=ダイブ>開始</r>"], StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseResponse_drops_an_identity_translation()
    {
        Assert.Empty(Accepted(Create("こんにちは"), Response(SceneId, Entry("t0000", "こんにちは"))));
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
    public void TryParseResponse_drops_a_dropped_line_break()
    {
        Assert.Empty(Accepted(Create("いち\nに"), Response(SceneId, Entry("t0000", "一二"))));
    }

    [Fact]
    public void An_accepted_template_resolves_the_rendered_source()
    {
        // Scene results must land where the per-string path looks them up; storing them anywhere
        // else leaves every rendered line untranslated.
        var root = Path.Combine(Path.GetTempPath(), "mlm-scene-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new TranslationCache(root);
            var resolver = new TranslationResolver(cache, _ => { }, _ => { });
            var batch = Create("あと3分です", "こんにちは");

            var translations = Accepted(
                batch,
                Response(SceneId, Entry("t0000", "还有__MLM_FMT_0__分钟"), Entry("t0001", "你好")));
            foreach (var pair in translations)
                Assert.True(cache.StoreGenerated(pair.Key, pair.Value));

            Assert.Equal("还有3分钟", resolver.Lookup("あと3分です"));
            Assert.Equal("还有7分钟", resolver.Lookup("あと7分です"));
            Assert.Equal("你好", resolver.Lookup("こんにちは"));
            Assert.True(cache.IsKnownTranslatedValue("你好"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
