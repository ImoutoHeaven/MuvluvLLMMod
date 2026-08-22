using System.Runtime.CompilerServices;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class PatchSurfaceTests
{
    [Fact]
    public void Patch_contains_only_render_fallback_targets()
    {
        var patch = ReadProductionSource("Patch.cs");

        Assert.Contains("typeof(TMP_Text), \"set_text\"", patch, StringComparison.Ordinal);
        Assert.Contains("SkillDescriptionBuilder.GetDescription", patch, StringComparison.Ordinal);
        Assert.Contains("FindObjectsByType<TMP_Text>", patch, StringComparison.Ordinal);
        Assert.Contains("HARD RULE", patch, StringComparison.Ordinal);
        Assert.Contains("DebugLogSeenText", patch, StringComparison.Ordinal);

        foreach (var forbidden in new[]
        {
            "GenerateFrames",
            "ScenarioHistoryCell",
            "ScenarioChoiceElementComponent",
            "LoadMasterData",
            "DownloadSceneFrameMasters",
            "Mosaic",
            "EnableSkipButton",
            "VoiceInterruption",
            "AutoSkipBattle",
            "FontBundle",
            "GenericFont",
            "ScenarioTextStyle",
            "RemoteCatalog",
            "SceneRequest"
        })
        {
            Assert.DoesNotContain(forbidden, patch, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Production_has_no_upstream_dependency_or_dependency_attribute()
    {
        var root = FindRepositoryRoot();
        foreach (var source in Directory.EnumerateFiles(
                     Path.Combine(root, "MuvluvLLMMod"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var contents = File.ReadAllText(source);
            Assert.DoesNotContain("MuvluvMod", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("[BepInDependency", contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Config_contains_only_the_plugin_owned_settings()
    {
        var config = ReadProductionSource("Config.cs");
        foreach (var upstreamSetting in new[]
        {
            "DynamicMosaic",
            "EnableSkipButton",
            "VoiceInterruption",
            "AutoSkipBattle",
            "TranslationCDN",
            "FontBundlePath",
            "FontAssetName"
        })
        {
            Assert.DoesNotContain(upstreamSetting, config, StringComparison.Ordinal);
        }

        foreach (var required in new[]
        {
            "DebugLogSeenText",
            "RefreshPeriodSeconds",
            "TranslatePeriodSeconds"
        })
        {
            Assert.Contains(required, config, StringComparison.Ordinal);
        }
    }

    private static string ReadProductionSource(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MuvluvLLMMod", fileName));

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
