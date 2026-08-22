using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class TextTemplateTests
{
    [Fact]
    public void Normalize_replaces_numbers_without_touching_tmp_tags()
    {
        var normalized = TextTemplate.Normalize("<color=#ff0>攻撃力 12.5%</color> を 3 回上昇");

        Assert.Equal("<color=#ff0>攻撃力 {0}%</color> を {1} 回上昇", normalized.Template);
        Assert.Equal(new[] { "12.5", "3" }, normalized.Values);
    }

    [Fact]
    public void Normalize_preserves_existing_placeholders_and_allocates_numeric_placeholders_after_them()
    {
        var normalized = TextTemplate.Normalize("<b>スキル {0}</b> は 12 秒、{1} 回");

        Assert.Equal("<b>スキル {0}</b> は {2} 秒、{1} 回", normalized.Template);
        Assert.Equal(new[] { "12" }, normalized.Values);
        Assert.Equal("<b>技能 {0}</b> 持续 12 秒、{1} 次", TextTemplate.Fill("<b>技能 {0}</b> 持续 {2} 秒、{1} 次", normalized.Values, normalized.NumericPlaceholderStart));
    }

    [Fact]
    public void Fill_leaves_existing_placeholders_untouched_when_there_are_no_numeric_values()
    {
        Assert.Equal("技能 {0} / {1}", TextTemplate.Fill("技能 {0} / {1}", Array.Empty<string>()));
    }

    [Fact]
    public void Fill_rejects_a_final_expansion_over_the_utf16_text_budget()
    {
        var translatedTemplate = new string('中', 4080) + "{0}";
        var numericValue = new string('9', 100);

        Assert.True(TranslationBudget.IsTextWithinBudget(translatedTemplate));
        Assert.True(TranslationBudget.IsTextWithinBudget(numericValue));
        Assert.Null(TextTemplate.Fill(translatedTemplate, new[] { numericValue }, 0));
    }

    [Fact]
    public void Fill_supports_non_contiguous_preexisting_placeholders()
    {
        var normalized = TextTemplate.Normalize("スキル {3} 12 7");

        Assert.Equal("スキル {3} {4} {5}", normalized.Template);
        Assert.Equal(4, normalized.NumericPlaceholderStart);
        Assert.Equal("技能 {3} 12 7", TextTemplate.Fill("技能 {3} {4} {5}", normalized.Values, normalized.NumericPlaceholderStart));
        Assert.Null(TextTemplate.Fill("技能 {3} {5} {4}", normalized.Values, normalized.NumericPlaceholderStart));
        Assert.Null(TextTemplate.Fill("技能 {3} {4} {4}", normalized.Values, normalized.NumericPlaceholderStart));
        Assert.Null(TextTemplate.Fill("技能 {3} {4} {6}", normalized.Values, normalized.NumericPlaceholderStart));
    }

    [Fact]
    public void Fill_restores_values_only_when_placeholders_are_complete_and_ordered()
    {
        Assert.Equal("攻击力提升 12.5% 并持续 3 回合", TextTemplate.Fill("攻击力提升 {0}% 并持续 {1} 回合", new[] { "12.5", "3" }));
        Assert.Null(TextTemplate.Fill("攻击力提升", new[] { "12.5" }));
        Assert.Null(TextTemplate.Fill("攻击力 {1}", new[] { "12.5" }));
        Assert.Null(TextTemplate.Fill("攻击力 {0} {0}", new[] { "12.5" }));
        Assert.Null(TextTemplate.Fill("{1} / {0}", new[] { "12.5", "3" }));
    }

    [Theory]
    [InlineData("{10000}")]
    [InlineData("{999999999999999999999999999999}")]
    public void Overflow_or_unsupported_placeholders_fail_safely(string placeholder)
    {
        var source = "スキル " + placeholder + " 12";
        var normalized = (Template: string.Empty, Values: Array.Empty<string>(), NumericPlaceholderStart: -1);
        string? filled = null;

        Assert.Null(Record.Exception(() => normalized = TextTemplate.Normalize(source)));
        Assert.Equal(source, normalized.Template);
        Assert.Empty(normalized.Values);
        Assert.Equal(-1, normalized.NumericPlaceholderStart);
        Assert.Null(Record.Exception(() => filled = TextTemplate.Fill("技能 " + placeholder + " {0}", new[] { "12" })));
        Assert.Null(filled);
        Assert.False(TextTemplate.HasSamePlaceholders(source, source));
    }

    [Fact]
    public void Formatting_validation_rejects_changed_placeholders_markup_and_line_structure()
    {
        Assert.True(TextTemplate.HasSamePlaceholders("{0} と {1}", "{0} 和 {1}"));
        Assert.False(TextTemplate.HasSamePlaceholders("{0} と {1}", "{1} 和 {0}"));
        Assert.False(TextTemplate.HasSamePlaceholders("{0} と {0}", "{0}"));
        Assert.True(TextTemplate.HasSameMarkup("<b>{0}</b>\r\n\r\n次", "<b>{0}</b>\r\n\r\n回"));
        Assert.False(TextTemplate.HasSameMarkup("<b>{0}</b>", "{0}"));
        Assert.False(TextTemplate.HasSameMarkup("A\r\nB", "A\nB"));
        Assert.False(TextTemplate.HasSameMarkup("A\n\nB", "A\nB\n"));
    }

    [Fact]
    public void Protected_llm_text_restores_exact_tags_placeholders_and_newlines()
    {
        var protectedText = TextTemplate.ProtectForLlm("<b>攻撃力 {0}</b>\r\n\n{1} 回");

        Assert.Equal("__MLM_FMT_0__攻撃力 __MLM_FMT_1____MLM_FMT_2____MLM_FMT_3____MLM_FMT_4____MLM_FMT_5__ 回", protectedText.Prompt);
        Assert.True(TextTemplate.TryRestoreLlm("攻击力 __MLM_FMT_0____MLM_FMT_1____MLM_FMT_2____MLM_FMT_3____MLM_FMT_4____MLM_FMT_5__ 次", protectedText, out var restored));
        Assert.Equal("攻击力 <b>{0}</b>\r\n\n{1} 次", restored);
    }

    [Theory]
    [InlineData("__MLM_FMT_0____MLM_FMT_2__")]
    [InlineData("__MLM_FMT_1____MLM_FMT_0____MLM_FMT_2__")]
    [InlineData("__MLM_FMT_0____MLM_FMT_1____MLM_FMT_1____MLM_FMT_2__")]
    [InlineData("__MLM_FMT_0____MLM_FMT_1____MLM_FMT_99____MLM_FMT_2__")]
    [InlineData("__MLM_FMT_0____MLM_FMT_1____MLM_FMT_2__\n")]
    [InlineData("__MLM_FMT_0__<i>__MLM_FMT_1____MLM_FMT_2__")]
    public void Protected_llm_text_rejects_missing_reordered_duplicated_unknown_or_raw_formatting(string response)
    {
        var protectedText = TextTemplate.ProtectForLlm("<b>{0}</b>");

        Assert.False(TextTemplate.TryRestoreLlm(response, protectedText, out _));
    }

    [Fact]
    public void Protected_llm_text_preserves_literal_escaped_line_breaks()
    {
        const string source = "<size=24>一行\\n二行\\r\\n三行{0}</size>";
        var protectedText = TextTemplate.ProtectForLlm(source);

        Assert.Equal(
            "__MLM_FMT_0__一行__MLM_FMT_1__二行__MLM_FMT_2__三行__MLM_FMT_3____MLM_FMT_4__",
            protectedText.Prompt);
        Assert.True(TextTemplate.TryRestoreLlm(
            "__MLM_FMT_0__第一行__MLM_FMT_1__第二行__MLM_FMT_2__第三行__MLM_FMT_3____MLM_FMT_4__",
            protectedText,
            out var restored));
        Assert.Equal("<size=24>第一行\\n第二行\\r\\n第三行{0}</size>", restored);
        Assert.True(TextTemplate.HasSameMarkup(source, restored));
    }

    [Fact]
    public void Literal_escaped_line_break_markup_must_not_be_dropped_or_normalized()
    {
        Assert.False(TextTemplate.HasSameMarkup("一行\\n二行", "第一行第二行"));
        Assert.False(TextTemplate.HasSameMarkup("一行\\r\\n二行", "第一行\\n第二行"));
        Assert.False(TextTemplate.HasSameMarkup("一行\\n二行", "第一行\n第二行"));
    }

    [Fact]
    public void Pending_product_descriptions_protect_all_twelve_format_tokens()
    {
        var sources = new[]
        {
            "<size=24>購入時に有償ジェム×{0},{1}と{2}日間、毎日無償ジェム×{3}（おまけ）が獲得できるパス\\n※{4}DAYジェムパスの有効期間中の重複購入はできません。購入から{5}日後の{6}:{7}以降に再度購入可能になります\\n※法令で定めがある場合を除き、キャンセルや返金、有効期間の延長などには一切応じておりません</size>",
            "<size=24>購入時に有償ジェム×{0},{1}と{2}日間、毎日補給物資×{3}（おまけ）が獲得できるパス\\n※{4}DAY生徒成長パスの有効期間中の重複購入はできません。購入から{5}日後の{6}:{7}以降に再度購入可能になります\\n※法令で定めがある場合を除き、キャンセルや返金、有効期間の延長などには一切応じておりません</size>"
        };

        foreach (var source in sources)
        {
            var protectedText = TextTemplate.ProtectForLlm(source);
            var response = string.Join("译", protectedText.Tokens);

            Assert.Equal(12, protectedText.Tokens.Length);
            Assert.DoesNotContain("\\n", protectedText.Prompt, StringComparison.Ordinal);
            Assert.True(TextTemplate.TryRestoreLlm(response, protectedText, out var restored));
            Assert.True(TextTemplate.HasSamePlaceholders(source, restored));
            Assert.True(TextTemplate.HasSameMarkup(source, restored));
        }
    }

    [Theory]
    [InlineData("確認", false)]
    [InlineData("あいう", true)]
    [InlineData("ゟ", true)]
    [InlineData("ヿ", true)]
    [InlineData("ｦ", true)]
    [InlineData("ﾝ", true)]
    [InlineData("技能 スキル", true)]
    [InlineData("ﾟ", false)]
    [InlineData("\u3099", false)]
    [InlineData("\u309c", false)]
    [InlineData("简体中文", false)]
    [InlineData("123 - {0}", false)]
    [InlineData("", false)]
    public void IsTranslationCandidate_requires_kana(string text, bool expected)
    {
        Assert.Equal(expected, TextTemplate.IsTranslationCandidate(text));
    }

    [Theory]
    [InlineData(0x3040, false, "hiragana lower neighbour")]
    [InlineData(0x3041, true, "hiragana lower endpoint")]
    [InlineData(0x3096, true, "hiragana upper endpoint")]
    [InlineData(0x3097, false, "hiragana upper neighbour")]
    [InlineData(0x309c, false, "hiragana iteration lower neighbour")]
    [InlineData(0x309d, true, "hiragana iteration lower endpoint")]
    [InlineData(0x309f, true, "hiragana iteration upper endpoint")]
    [InlineData(0x30a0, false, "hiragana iteration upper neighbour")]
    [InlineData(0x30a0, false, "katakana lower neighbour")]
    [InlineData(0x30a1, true, "katakana lower endpoint")]
    [InlineData(0x30fa, true, "katakana upper endpoint")]
    [InlineData(0x30fb, false, "katakana upper neighbour")]
    [InlineData(0x30fc, false, "katakana iteration lower neighbour")]
    [InlineData(0x30fd, true, "katakana iteration lower endpoint")]
    [InlineData(0x30ff, true, "katakana iteration upper endpoint")]
    [InlineData(0x3100, false, "katakana iteration upper neighbour")]
    [InlineData(0xff65, false, "half-width katakana lower neighbour")]
    [InlineData(0xff66, true, "half-width katakana lower endpoint")]
    [InlineData(0xff9d, true, "half-width katakana upper endpoint")]
    [InlineData(0xff9e, false, "half-width katakana upper neighbour")]
    [InlineData(0x3099, false, "combining mark U+3099")]
    [InlineData(0x309a, false, "combining mark U+309A")]
    [InlineData(0x309b, false, "combining mark U+309B")]
    [InlineData(0x309c, false, "combining mark U+309C")]
    public void IsTranslationCandidate_pins_kana_boundaries(int codePoint, bool expected, string boundary)
    {
        _ = boundary;
        Assert.Equal(expected, TextTemplate.IsTranslationCandidate(char.ConvertFromUtf32(codePoint)));
    }
}
