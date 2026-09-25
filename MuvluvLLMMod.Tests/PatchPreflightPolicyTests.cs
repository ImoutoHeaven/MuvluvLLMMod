using System.Reflection;
using Xunit;

namespace MuvluvLLMMod.Tests;

/// <summary>
/// Unit tests for the pre-install target gate: per-site resolution rules (missing type, missing
/// method, unresolved overload, exact argument matching) and batch aggregation. No BepInEx, Harmony,
/// or game assemblies are required, because the policy depends on reflection only.
/// </summary>
public sealed class PatchPreflightPolicyTests
{
    private static readonly Assembly[] SearchAssemblies = { typeof(string).Assembly };

    // DeclaredOnly lookup requires the member to be declared on the target type itself.
    private static readonly string DeclaredType = typeof(DateTime).FullName!;
    private const string DeclaredMethod = nameof(DateTime.AddDays);

    private static PatchPreflightPolicy.PatchTargetSpec Spec(
        string declaringType,
        string method,
        params Type[] argumentTypes) =>
        new(
            "TestPatch",
            "site",
            declaringType,
            method,
            argumentTypes.Length == 0 ? null : argumentTypes);

    [Fact]
    public void Resolve_returns_the_unique_method_for_an_unambiguous_name()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(DeclaredType, DeclaredMethod, typeof(double)));

        Assert.True(resolution.Ok);
        Assert.Equal(DeclaredMethod, resolution.Target!.Name);
    }

    [Fact]
    public void Resolve_reports_a_missing_declaring_type()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec("No.Such.Type", "Whatever"));

        Assert.False(resolution.Ok);
        Assert.Equal("missing-type:No.Such.Type", resolution.Failure);
    }

    [Fact]
    public void Resolve_reports_a_missing_method()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(DeclaredType, "NoSuchMethod"));

        Assert.False(resolution.Ok);
        Assert.StartsWith("missing-method:", resolution.Failure);
    }

    [Fact]
    public void Resolve_reports_ambiguity_when_overloads_are_not_disambiguated()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(typeof(int).FullName!, nameof(int.Parse)));

        Assert.False(resolution.Ok);
        Assert.StartsWith("ambiguous-target:", resolution.Failure);
    }

    [Fact]
    public void Resolve_uses_argument_types_to_select_one_overload()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(typeof(int).FullName!, nameof(int.Parse), typeof(string)));

        Assert.True(resolution.Ok);
        Assert.Single(resolution.Target!.GetParameters());
        Assert.Equal(typeof(string), resolution.Target.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Resolve_reports_a_missing_method_when_argument_types_match_nothing()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            Spec(typeof(int).FullName!, nameof(int.Parse), typeof(Exception), typeof(Guid)));

        Assert.False(resolution.Ok);
        Assert.StartsWith("missing-method:", resolution.Failure);
    }

    [Fact]
    public void Resolve_reports_a_missing_type_for_an_empty_spec()
    {
        var resolution = PatchPreflightPolicy.Resolve(
            SearchAssemblies,
            new PatchPreflightPolicy.PatchTargetSpec("TestPatch", "site", string.Empty, string.Empty));

        Assert.False(resolution.Ok);
        Assert.StartsWith("missing-type:", resolution.Failure);
    }

    [Fact]
    public void Check_aggregates_every_failure_in_input_order()
    {
        var report = PatchPreflightPolicy.Check(
            new[]
            {
                Spec("No.Such.Type", "First"),
                Spec(DeclaredType, DeclaredMethod, typeof(double)),
                Spec(DeclaredType, "NoSuchMethod"),
                Spec("Also.Missing", "Last"),
            },
            SearchAssemblies);

        Assert.False(report.Ok);
        Assert.Equal(4, report.SiteCount);
        Assert.Equal(3, report.Failures.Count);
        Assert.StartsWith("TestPatch::site => missing-type:No.Such.Type", report.Failures[0]);
        Assert.StartsWith("TestPatch::site => missing-method:", report.Failures[1]);
        Assert.StartsWith("TestPatch::site => missing-type:Also.Missing", report.Failures[2]);
        Assert.Single(report.Targets);
    }

    [Fact]
    public void Check_deduplicates_repeated_targets()
    {
        var report = PatchPreflightPolicy.Check(
            new[]
            {
                Spec(DeclaredType, DeclaredMethod, typeof(double)),
                Spec(DeclaredType, DeclaredMethod, typeof(double)),
                Spec(typeof(object).FullName!, nameof(object.ToString)),
            },
            SearchAssemblies);

        Assert.True(report.Ok);
        Assert.Equal(3, report.SiteCount);
        Assert.Equal(2, report.Targets.Count);
    }

    [Fact]
    public void Preflight_failure_reports_every_failing_site_in_its_message()
    {
        var failure = new HarmonyPatchPreflightException(
            new[] { "Patch::a => missing-type:X", "Patch::b => missing-method:Y" });

        Assert.Contains("Patch::a => missing-type:X", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Patch::b => missing-method:Y", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, failure.Failures.Count);
    }
}
