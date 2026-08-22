using System.Runtime.CompilerServices;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class PluginSurfaceTests
{
    [Fact]
    public void Application_quit_registration_uses_the_generation_owned_atomic_coordinator()
    {
        var plugin = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MuvluvLLMMod", "Plugin.cs"));

        Assert.Contains(
            "NativeDelegateCoordinator<Il2CppSystem.Action>",
            plugin,
            StringComparison.Ordinal);
        Assert.Contains("TryRegister", plugin, StringComparison.Ordinal);
        Assert.Contains("() => (Il2CppSystem.Action)ApplicationQuittingHandler", plugin, StringComparison.Ordinal);
        Assert.Contains("Application.add_quitting(handler)", plugin, StringComparison.Ordinal);
        Assert.Contains("TryRemove", plugin, StringComparison.Ordinal);
        Assert.Contains("Application.remove_quitting(handler)", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrCreate", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.quitting = Application.quitting +", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.quitting = Application.quitting -", plugin, StringComparison.Ordinal);
    }

    [Fact]
    public void Cleanup_is_generation_owned_and_preserves_teardown_order()
    {
        var plugin = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MuvluvLLMMod", "Plugin.cs"));
        var configurationShutdown = plugin.IndexOf(
            "RunCleanupStep(\"shutdown configuration\"",
            StringComparison.Ordinal);
        var retire = plugin.IndexOf(
            "RunCleanupStep(\"retire TMP translation state\"",
            StringComparison.Ordinal);
        var freeze = plugin.IndexOf("\"freeze cache mutations\"", StringComparison.Ordinal);
        var machineShutdown = plugin.IndexOf(
            "\"shutdown machine translator\"",
            StringComparison.Ordinal);
        var cancelPersistence = plugin.IndexOf(
            "\"cancel cache persistence\"",
            StringComparison.Ordinal);
        var flush = plugin.IndexOf("\"flush cache\"", StringComparison.Ordinal);
        var unpatch = plugin.IndexOf("\"unpatch Harmony\"", StringComparison.Ordinal);

        Assert.True(configurationShutdown >= 0);
        Assert.True(retire > configurationShutdown);
        Assert.True(freeze > retire);
        Assert.True(machineShutdown > freeze);
        Assert.True(cancelPersistence > machineShutdown);
        Assert.True(flush > cancelPersistence);
        Assert.True(unpatch > flush);
        Assert.Contains("lifecycleGate.Cleanup(", plugin, StringComparison.Ordinal);
        Assert.Contains("TryPublishRunning", plugin, StringComparison.Ordinal);
        Assert.Contains("TryAcquireRunningLease", plugin, StringComparison.Ordinal);
        Assert.Contains("RequireLoadStage", plugin, StringComparison.Ordinal);
        Assert.Contains("RequireRunningStage", plugin, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_rolls_back_partial_initialization_through_the_single_cleanup_path()
    {
        var plugin = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MuvluvLLMMod", "Plugin.cs"));
        Assert.Contains("TryBeginLoad(out var generation)", plugin, StringComparison.Ordinal);

        var load = plugin.IndexOf("public override void Load()", StringComparison.Ordinal);
        var failure = plugin.IndexOf("catch", load, StringComparison.Ordinal);
        var cleanup = plugin.IndexOf("Cleanup();", failure, StringComparison.Ordinal);
        var rethrow = plugin.IndexOf("throw;", cleanup, StringComparison.Ordinal);

        Assert.True(load >= 0);
        Assert.True(failure > load);
        Assert.True(cleanup > failure);
        Assert.True(rethrow > cleanup);
        Assert.Equal(1, CountOccurrences(plugin, "internal static bool Cleanup()"));
    }

    [Fact]
    public void Reload_uses_the_lease_owner_not_a_replaceable_static_machine()
    {
        var plugin = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MuvluvLLMMod", "Plugin.cs"));
        var reload = plugin.IndexOf("internal static void ReloadMachineTranslator", StringComparison.Ordinal);
        Assert.True(reload >= 0);
        Assert.Contains("PluginGenerationLease lease", plugin, StringComparison.Ordinal);
        Assert.Contains("lease.GetOwner<GenerationResources>()", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("currentMachine", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("private static MachineTranslatorLifecycle machineLifecycle", plugin, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
