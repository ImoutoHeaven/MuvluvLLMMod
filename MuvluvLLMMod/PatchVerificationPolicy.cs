using System.Reflection;

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

    /// <summary>
    /// Pre-install resolution: every declared target must resolve to an existing method before
    /// Harmony installs anything. A game update that removes or re-signatures a seam is therefore
    /// reported per site and aborts the generation, instead of publishing a partial patch set
    /// whose hooks silently no-op. Ownership is established afterwards by <see cref="Verify"/>,
    /// which runs against the same target list.
    /// </summary>
    public static IReadOnlyList<string> UnresolvedTargets(
        IEnumerable<(string Label, MethodBase? Method)> targets) =>
        targets.Where(target => target.Method is null).Select(target => target.Label).ToArray();
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

/// <summary>
/// Raised before Harmony installs anything when a declared target no longer resolves against the
/// loaded game assemblies. The per-site failures are retained so the loader can make a structured
/// rollback decision instead of inferring failure from a log message.
/// </summary>
public sealed class HarmonyPatchPreflightException : InvalidOperationException
{
    public HarmonyPatchPreflightException(IReadOnlyList<string> failures)
        : base(FormatMessage(failures))
    {
        ArgumentNullException.ThrowIfNull(failures);
        Failures = failures;
    }

    public IReadOnlyList<string> Failures { get; }

    private static string FormatMessage(IReadOnlyList<string> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return $"Harmony patch target precheck failed ({failures.Count} sites): "
            + string.Join(" | ", failures);
    }
}
