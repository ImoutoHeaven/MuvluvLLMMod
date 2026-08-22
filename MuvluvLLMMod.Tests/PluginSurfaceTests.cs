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

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
