using Xunit;

namespace MuvluvLLMMod.Tests;

/// <summary>
/// Frame document surgery. The document is shared with the game, so the contract is that
/// unrelated fields survive and an untranslated line is never blanked.
/// </summary>
public sealed class SceneFrameDocumentTests
{
    // ConfigurationJson deserializes directly into ScenarioConfiguration, so Phrase is at the root.
    private static string Document(string text, string speaker = "威厳のある女性") =>
        "{\"Phrase\":{\"Text\":\"" + text + "\",\"SpeakerName\":\"" + speaker
        + "\",\"TeamName\":null,\"VoiceAssetIds\":[]},\"Background\":{},\"NeedsHideText\":false}";

    private static string Wrapped(string inner) => inner;

    [Fact]
    public void CollectTexts_returns_kana_dialogue_in_order()
    {
        var texts = SceneFrameDocument.CollectTexts(Wrapped(Document("こんにちは")));

        Assert.Single(texts);
        Assert.Equal("こんにちは", texts[0]);
    }

    [Fact]
    public void CollectTexts_skips_non_candidates_and_broken_documents()
    {
        Assert.Empty(SceneFrameDocument.CollectTexts(Wrapped(Document("hello"))));
        Assert.Empty(SceneFrameDocument.CollectTexts("{not json"));
        Assert.Empty(SceneFrameDocument.CollectTexts(null));
        Assert.Empty(SceneFrameDocument.CollectTexts(""));
        Assert.Empty(SceneFrameDocument.CollectTexts("[]"));
    }

    [Fact]
    public void TryRewrite_replaces_the_dialogue_text()
    {
        var rewritten = SceneFrameDocument.TryRewrite(
            Wrapped(Document("こんにちは")),
            new Dictionary<string, string> { ["こんにちは"] = "你好" });

        Assert.NotNull(rewritten);
        Assert.Contains("\"Text\":\"你好\"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRewrite_preserves_unrelated_fields()
    {
        var rewritten = SceneFrameDocument.TryRewrite(
            Wrapped(Document("こんにちは")),
            new Dictionary<string, string> { ["こんにちは"] = "你好" });

        Assert.NotNull(rewritten);
        Assert.Contains("\"SpeakerName\":\"威厳のある女性\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"NeedsHideText\":false", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"VoiceAssetIds\":[]", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRewrite_returns_null_when_nothing_changes()
    {
        var json = Wrapped(Document("こんにちは"));

        // No translation for this line: the caller must skip the write rather than blank it.
        Assert.Null(SceneFrameDocument.TryRewrite(json, new Dictionary<string, string>()));
        Assert.Null(SceneFrameDocument.TryRewrite(json, null));
        Assert.Null(SceneFrameDocument.TryRewrite(json, new Dictionary<string, string> { ["別"] = "其他" }));
    }

    [Fact]
    public void TryRewrite_keeps_the_original_when_the_translation_is_identical()
    {
        Assert.Null(
            SceneFrameDocument.TryRewrite(
                Wrapped(Document("こんにちは")),
                new Dictionary<string, string> { ["こんにちは"] = "こんにちは" }));
    }

    [Fact]
    public void TryRewrite_tolerates_a_document_without_a_phrase()
    {
        Assert.Null(SceneFrameDocument.TryRewrite("{\"Background\":{}}", new Dictionary<string, string> { ["x"] = "y" }));
        Assert.Null(SceneFrameDocument.TryRewrite("{\"Phrase\":null}", new Dictionary<string, string> { ["x"] = "y" }));
        Assert.Null(SceneFrameDocument.TryRewrite("{not json", new Dictionary<string, string> { ["x"] = "y" }));
        Assert.Null(SceneFrameDocument.TryRewrite("[]", new Dictionary<string, string> { ["x"] = "y" }));
    }

    [Fact]
    public void TryRewrite_round_trips_a_ruby_line()
    {
        const string source = "<r=ダイブ>潜航</r>を開始せよ";
        var json = Wrapped(Document(source));

        var rewritten = SceneFrameDocument.TryRewrite(
            json,
            new Dictionary<string, string> { [source] = "<r=ダイブ>潜航</r>，开始！" });

        Assert.NotNull(rewritten);
        Assert.Contains("<r=ダイブ>潜航</r>", rewritten, StringComparison.Ordinal);

        // The rewritten document must still expose the same source text for reverse lookup.
        Assert.Equal(new[] { "<r=ダイブ>潜航</r>，开始！" }, SceneFrameDocument.CollectTexts(rewritten));
    }
}
