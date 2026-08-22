namespace MuvluvLLMMod;

/// <summary>
/// Loader-free evidence for one Harmony target. A target is verified only when both its method
/// exists and this plugin owns a patch on that method.
/// </summary>
public readonly record struct HarmonyPatchTargetStatus(
    string Label,
    bool TargetFound,
    bool OwnedByHarmony,
    bool Required = true)
{
    public bool IsVerified => TargetFound && OwnedByHarmony;
}

/// <summary>
/// Structured verification output used to decide whether a plugin generation may continue.
/// </summary>
public sealed class HarmonyPatchVerificationResult
{
    public HarmonyPatchVerificationResult(IEnumerable<HarmonyPatchTargetStatus> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Targets = targets.ToArray();
    }

    public IReadOnlyList<HarmonyPatchTargetStatus> Targets { get; }

    public bool Succeeded => Targets.All(target => !target.Required || target.IsVerified);

    public IReadOnlyList<HarmonyPatchTargetStatus> MissingRequiredTargets => Targets
        .Where(target => target.Required && !target.IsVerified)
        .ToArray();

    public IReadOnlyList<HarmonyPatchTargetStatus> MissingOptionalTargets => Targets
        .Where(target => !target.Required && !target.IsVerified)
        .ToArray();
}

public static class HarmonyPatchVerificationPolicy
{
    public static HarmonyPatchVerificationResult Verify(
        IEnumerable<HarmonyPatchTargetStatus> targets) =>
        new(targets);
}

/// <summary>
/// Raised before runtime resources are created when a required Harmony target is unavailable or
/// is not owned by this plugin. The result is retained so the loader can make a structured
/// rollback decision instead of inferring failure from a log message.
/// </summary>
public sealed class HarmonyPatchVerificationException : InvalidOperationException
{
    public HarmonyPatchVerificationException(HarmonyPatchVerificationResult result)
        : base(FormatMessage(result))
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
    }

    public HarmonyPatchVerificationResult Result { get; }

    private static string FormatMessage(HarmonyPatchVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var missing = string.Join(
            ", ",
            result.MissingRequiredTargets.Select(target => target.Label));
        return "Required Harmony patch verification failed: " + missing;
    }
}
