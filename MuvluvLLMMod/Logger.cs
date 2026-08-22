namespace MuvluvLLMMod;

/// <summary>
/// Provides a single logging entry point for the plugin.
/// </summary>
public static class Logger
{
    public static void Info(string message)
    {
        try { Plugin.Log?.LogInfo(message); } catch { }
    }

    public static void Warn(string message)
    {
        try { Plugin.Log?.LogWarning(message); } catch { }
    }

    public static void Error(string message)
    {
        try { Plugin.Log?.LogError(message); } catch { }
    }
}
