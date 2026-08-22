namespace MuvluvLLMMod;

/// <summary>
/// Loader-free formatting boundary for configuration events. Secret detection is based on the
/// event definition, not on the current ConfigEntry reference, so stale events cannot fall
/// through to a generic BoxedValue log after a generation handoff.
/// </summary>
public static class ConfigChangePolicy
{
    public static bool IsApiKey(string? section, string? key) =>
        string.Equals(section, "LLM", StringComparison.OrdinalIgnoreCase)
        && string.Equals(key, "ApiKey", StringComparison.OrdinalIgnoreCase);

    public static string FormatLog(string? section, string? key, object? boxedValue)
    {
        if (IsApiKey(section, key))
            return "[LLM] ApiKey changed";
        return $"[{section}] {key} => {boxedValue}";
    }
}
