using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MuvluvLLMMod.Tests;

/// <summary>
/// Regression coverage for source text whose leading or trailing line break is part of the scene
/// text. A trailing newline protects as a trailing token, so any step that trims the restored value
/// makes the markup comparison fail and silently rejects an otherwise valid scene.
///
/// Measured against the shipping corpus: 16 of 38183 dialogue lines end with a line break, and all
/// 16 were rejected before this was fixed.
/// </summary>
public sealed class SceneTranslationWhitespaceTests
{
    private static (SceneTranslationBatch Batch, string Id, string Protected) Create(string source)
    {
        var batch = SceneTranslationBatch.TryCreate(40003301, new[] { source })!;
        var target = JsonNode.Parse(batch.Prompt)!["targets"]!.AsArray()[0]!;
        return (batch, target["id"]!.GetValue<string>(), target["source"]!.GetValue<string>());
    }

    private static string Response(string id, string text) =>
        "{\"version\":1,\"sceneId\":40003301,\"translations\":[{\"id\":\"" + id
        + "\",\"text\":" + JsonSerializer.Serialize(text) + "}]}";

    [Fact]
    public void Accepts_a_source_that_ends_with_a_line_break()
    {
        var (batch, id, protectedSource) = Create("それじゃ、みんなでいっちゃいましょう！\n");

        // The trailing break is a protected token; keep it and change only the prose.
        var translated = "译" + protectedSource[1..];

        Assert.True(batch.TryParseResponse(Response(id, translated), out var translations));
        Assert.Equal("译れじゃ、みんなでいっちゃいましょう！\n", translations["それじゃ、みんなでいっちゃいましょう！\n"]);
    }

    [Fact]
    public void Preserves_the_trailing_line_break_in_the_result()
    {
        var (batch, id, protectedSource) = Create("おはよう\n");

        Assert.True(batch.TryParseResponse(Response(id, "早__MLM_FMT_0__"), out var translations));
        Assert.EndsWith("\n", translations["おはよう\n"], StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_translation_that_drops_the_trailing_line_break()
    {
        var (batch, id, _) = Create("おはよう\n");

        // Structured the same but with the break removed: the line structure changed.
        Assert.False(batch.TryParseResponse(Response(id, "早上好"), out _));
    }

    [Fact]
    public void Accepts_a_source_with_an_embedded_line_break()
    {
        var (batch, id, protectedSource) = Create("いちぎょうめ\nにぎょうめ");

        Assert.True(batch.TryParseResponse(Response(id, protectedSource + "译"), out var translations));
        Assert.Contains("\n", translations["いちぎょうめ\nにぎょうめ"], StringComparison.Ordinal);
    }
}
