using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class PatchVerificationPolicyTests
{
    [Fact]
    public void All_required_targets_verified_is_a_successful_startup_result()
    {
        var result = HarmonyPatchVerificationPolicy.Verify(
            new[]
            {
                new HarmonyPatchTargetStatus("TMP", TargetFound: true, OwnedByHarmony: true),
                new HarmonyPatchTargetStatus("Refresh", TargetFound: true, OwnedByHarmony: true),
                new HarmonyPatchTargetStatus("Leave", TargetFound: true, OwnedByHarmony: true)
            });

        Assert.True(result.Succeeded);
        Assert.Empty(result.MissingRequiredTargets);
    }

    [Fact]
    public void Missing_target_or_owner_is_a_structured_required_failure()
    {
        var result = HarmonyPatchVerificationPolicy.Verify(
            new[]
            {
                new HarmonyPatchTargetStatus("TMP", TargetFound: false, OwnedByHarmony: false),
                new HarmonyPatchTargetStatus("Refresh", TargetFound: true, OwnedByHarmony: false),
                new HarmonyPatchTargetStatus("Leave", TargetFound: true, OwnedByHarmony: true)
            });

        Assert.False(result.Succeeded);
        Assert.Equal(
            new[] { "TMP", "Refresh" },
            result.MissingRequiredTargets.Select(target => target.Label));

        var failure = new HarmonyPatchVerificationException(result);
        Assert.Same(result, failure.Result);
    }

    [Fact]
    public void Verification_failure_rolls_back_loading_generation_before_later_resources()
    {
        var gate = new PluginLifecycleGate();
        Assert.True(gate.TryBeginLoad());
        var events = new List<string>();
        var verification = HarmonyPatchVerificationPolicy.Verify(
            new[] { new HarmonyPatchTargetStatus("TMP", TargetFound: false, OwnedByHarmony: false) });

        try
        {
            events.Add("harmony verification");
            if (!verification.Succeeded)
                throw new HarmonyPatchVerificationException(verification);

            events.Add("hotkey injection");
        }
        catch (HarmonyPatchVerificationException)
        {
            Assert.True(gate.Cleanup(new[]
            {
                new PluginCleanupStep("rollback", () => events.Add("rollback"))
            }));
        }

        Assert.Equal(new[] { "harmony verification", "rollback" }, events);
        Assert.Equal(PluginLifecycleState.Stopped, gate.State);
    }
}
