using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class ConfigReloadPolicyTests
{
    [Theory]
    [InlineData("Enable")]
    [InlineData("Endpoint")]
    [InlineData("Model")]
    [InlineData("ApiKey")]
    [InlineData("TimeoutSeconds")]
    [InlineData("RetryCount")]
    [InlineData("RequestsPerSecond")]
    [InlineData("MaxInFlight")]
    [InlineData("TranslatePeriodSeconds")]
    public void Machine_affecting_llm_settings_require_a_worker_reload(string key)
    {
        Assert.True(ConfigReloadPolicy.RequiresMachineTranslatorReload("LLM", key));
    }

    [Theory]
    [InlineData("Translation", "Enable")]
    [InlineData("Translation.Debug", "DebugLogSeenText")]
    [InlineData("LLM", "RefreshPeriodSeconds")]
    [InlineData("LLM", "CacheDirectory")]
    [InlineData("Other", "Endpoint")]
    [InlineData("LLM", "endpoint")]
    [InlineData(null, "Enable")]
    [InlineData("LLM", null)]
    public void Display_diagnostic_refresh_and_unknown_settings_do_not_reload(
        string? section,
        string? key)
    {
        Assert.False(ConfigReloadPolicy.RequiresMachineTranslatorReload(section, key));
    }
}
