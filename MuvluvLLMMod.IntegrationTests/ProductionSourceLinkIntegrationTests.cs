using HarmonyLib;
using Xunit;

namespace MuvluvLLMMod.IntegrationTests;

public sealed class ProductionSourceLinkIntegrationTests
{
    [Fact]
    public void Integration_target_links_the_current_production_wildcard_and_executes_its_budget_behavior()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(
            Path.Combine(root, "MuvluvLLMMod.IntegrationTests", "MuvluvLLMMod.IntegrationTests.csproj"));

        Assert.Contains(
            "<Compile Include=\"..\\MuvluvLLMMod\\*.cs\" Link=\"Production\\%(Filename)%(Extension)\" />",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Compile Remove=\"..\\MuvluvLLMMod\\CompilerServices.cs\" />",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Compile Include=\"Production\\",
            project,
            StringComparison.Ordinal);

        // This is a behavioral sentinel for the checked-in PF-5 production source, not a copied
        // test analogue: the linked TextTemplate must reject the expanded final render.
        Assert.Null(
            TextTemplate.Fill(
                new string('中', 4080) + "{0}",
                new[] { new string('9', 100) },
                0));
    }

    [Fact]
    public void Exact_source_harness_applies_only_final_tmp_and_scenario_priority_patches()
    {
        using var fixture = ProductionHarness.LoadPlugin();
        var applications = Harmony.SnapshotApplications();

        Assert.Equal(3, applications.Count);
        Assert.All(applications, application =>
            Assert.Equal(Plugin.PluginGuid, application.Owner));
        Assert.Contains(
            applications,
            application => application.PatchMethod == "TranslateTmpSetter"
                && application.Target == "TMP_Text.set_text");
        Assert.Contains(
            applications,
            application => application.PatchMethod == "SetIsPlayingScenario"
                && application.Target == "ScenarioController.Refresh");
        Assert.Contains(
            applications,
            application => application.PatchMethod == "SetIsNotPlayingScenario"
                && application.Target == "ScenarioController.Leave");
        Assert.DoesNotContain(
            applications,
            application => application.PatchMethod.Contains("Skill", StringComparison.OrdinalIgnoreCase)
                || application.Target.Contains("Description", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository root was not found from the test output");
    }
}
