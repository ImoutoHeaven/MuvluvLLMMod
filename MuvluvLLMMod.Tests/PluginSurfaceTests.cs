using System.Runtime.CompilerServices;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class PluginSurfaceTests
{
    [Fact]
    public void Application_quit_registration_reuses_the_same_il2cpp_delegate()
    {
        var plugin = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MuvluvLLMMod", "Plugin.cs"));

        Assert.Contains("private static Il2CppSystem.Action? applicationQuittingHandler", plugin, StringComparison.Ordinal);
        Assert.Contains("var handler = (Il2CppSystem.Action)ApplicationQuittingHandler", plugin, StringComparison.Ordinal);
        Assert.Contains("Application.add_quitting(handler)", plugin, StringComparison.Ordinal);
        Assert.Contains("Application.remove_quitting(handler)", plugin, StringComparison.Ordinal);
        Assert.Contains("CompareExchange(ref applicationQuittingHandler, null, handler)", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.quitting = Application.quitting +", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.quitting = Application.quitting -", plugin, StringComparison.Ordinal);
    }

    [Fact]
    public void Cleanup_unsubscribes_config_before_freezing_and_load_gets_a_fresh_lifecycle()
    {
        var plugin = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MuvluvLLMMod", "Plugin.cs"));
        var configurationShutdown = plugin.IndexOf("CleanupStep(\"shutdown configuration\"", StringComparison.Ordinal);
        var freeze = plugin.IndexOf("Cache.FreezeMutations()", StringComparison.Ordinal);
        var machineShutdown = plugin.IndexOf("CleanupStep(\"shutdown machine translator\"", StringComparison.Ordinal);

        Assert.True(configurationShutdown >= 0);
        Assert.True(freeze > configurationShutdown);
        Assert.True(machineShutdown > freeze);
        Assert.Equal(1, CountOccurrences(plugin, "Cache.FreezeMutations()"));
        Assert.Contains("Volatile.Write(ref machineLifecycle, CreateMachineLifecycle())", plugin, StringComparison.Ordinal);
        Assert.Contains("if (IsCleaningUp || Cache == null)", plugin, StringComparison.Ordinal);
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
