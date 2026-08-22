namespace MuvluvLLMMod;

/// <summary>
/// Classifies configuration changes that require rebuilding the machine translator.
/// Display and diagnostic settings deliberately do not belong to this policy.
/// </summary>
public static class ConfigReloadPolicy
{
    public static bool RequiresMachineTranslatorReload(string? section, string? key)
    {
        if (!string.Equals(section, "LLM", StringComparison.Ordinal))
            return false;

        return key is "Enable"
            or "Endpoint"
            or "Model"
            or "ApiKey"
            or "TimeoutSeconds"
            or "RetryCount"
            or "RequestsPerSecond"
            or "MaxInFlight"
            or "TranslatePeriodSeconds";
    }
}
