using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class ReadmeSurfaceTests
{
    [Fact]
    public void Release_documentation_uses_only_the_generic_render_contract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "README.md")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var readme = File.ReadAllText(Path.Combine(directory!.FullName, "README.md"));

        foreach (var forbidden in new[]
        {
            "MuvluvMod",
            "GenerateFrames",
            "ScenarioHistoryCell",
            "ScenarioChoiceElementComponent",
            "LoadMasterData",
            "never fights",
            "always safe",
            "F3",
            "F4",
            "F5"
        })
        {
            Assert.DoesNotContain(forbidden, readme, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("kana-free incoming text is not queued", readme, StringComparison.Ordinal);
        Assert.Contains("font fallback and glyph coverage", readme, StringComparison.Ordinal);
        Assert.Contains("not a claim of complete", readme, StringComparison.Ordinal);
    }
}
