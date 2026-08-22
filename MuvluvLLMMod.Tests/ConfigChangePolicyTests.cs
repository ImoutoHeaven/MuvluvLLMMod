using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class ConfigChangePolicyTests
{
    [Fact]
    public void Api_key_log_is_definition_based_and_never_contains_the_secret()
    {
        var message = ConfigChangePolicy.FormatLog("LLM", "ApiKey", "super-secret-key");

        Assert.Equal("[LLM] ApiKey changed", message);
        Assert.DoesNotContain("super-secret-key", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_non_api_setting_can_still_be_diagnosed_without_secret_special_casing()
    {
        var message = ConfigChangePolicy.FormatLog("LLM", "Endpoint", "http://localhost");

        Assert.Contains("Endpoint", message, StringComparison.Ordinal);
        Assert.Contains("http://localhost", message, StringComparison.Ordinal);
    }
}
