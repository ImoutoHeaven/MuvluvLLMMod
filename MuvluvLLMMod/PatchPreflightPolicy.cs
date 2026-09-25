using System.Reflection;

namespace MuvluvLLMMod;

/// <summary>
/// Pre-install resolution of every declared Harmony target. A game update that removes, renames,
/// or re-signatures a seam is reported per site and aborts the generation before Harmony installs
/// anything, so a partially patched plugin cannot reach the runtime.
///
/// Pure logic: this type depends on <see cref="System.Reflection"/> only, never on HarmonyLib or
/// the game assemblies, so it compiles into the loader-free test project.
/// </summary>
public static class PatchPreflightPolicy
{
    public const string MissingType = "missing-type";
    public const string MissingMethod = "missing-method";
    public const string AmbiguousTarget = "ambiguous-target";

    /// <summary>One declared target: declaring type, method name, optional exact argument types.</summary>
    public readonly struct PatchTargetSpec
    {
        public PatchTargetSpec(
            string patchClass,
            string site,
            string declaringTypeName,
            string methodName,
            Type[]? argumentTypes = null)
        {
            PatchClass = patchClass;
            Site = site;
            DeclaringTypeName = declaringTypeName;
            MethodName = methodName;
            ArgumentTypes = argumentTypes;
        }

        public string PatchClass { get; }
        public string Site { get; }
        public string DeclaringTypeName { get; }
        public string MethodName { get; }
        public Type[]? ArgumentTypes { get; }

        public string Label => PatchClass + "::" + Site;
    }

    /// <summary>Single-site resolution: a unique target, or the reason it could not be resolved.</summary>
    public readonly struct SiteResolution
    {
        internal SiteResolution(MethodBase? target, string? failure)
        {
            Target = target;
            Failure = failure;
        }

        public MethodBase? Target { get; }
        public string? Failure { get; }
        public bool Ok => Failure == null;
    }

    /// <summary>Aggregate report. <see cref="Ok"/> is the gate: no failures means install may proceed.</summary>
    public sealed class Report
    {
        internal Report(
            int siteCount,
            IReadOnlyList<MethodBase> targets,
            IReadOnlyList<string> failures)
        {
            SiteCount = siteCount;
            Targets = targets;
            Failures = failures;
        }

        public int SiteCount { get; }
        public IReadOnlyList<MethodBase> Targets { get; }
        public IReadOnlyList<string> Failures { get; }
        public bool Ok => Failures.Count == 0;
    }

    public static Report Check(
        IEnumerable<PatchTargetSpec> specs,
        IEnumerable<Assembly> searchAssemblies)
    {
        var assemblies = searchAssemblies.ToArray();
        var failures = new List<string>();
        var targets = new List<MethodBase>();
        var siteCount = 0;

        foreach (var spec in specs)
        {
            siteCount++;
            var resolution = Resolve(assemblies, spec);
            if (!resolution.Ok)
            {
                failures.Add(spec.Label + " => " + resolution.Failure);
                continue;
            }

            if (!targets.Contains(resolution.Target!))
                targets.Add(resolution.Target!);
        }

        return new Report(siteCount, targets, failures);
    }

    /// <summary>
    /// Resolves one target. A missing type, a missing method, and an overload that the declaration
    /// does not disambiguate are all failures; only a single exact match passes.
    /// </summary>
    public static SiteResolution Resolve(
        IEnumerable<Assembly> searchAssemblies,
        PatchTargetSpec spec)
    {
        if (string.IsNullOrEmpty(spec.DeclaringTypeName) || string.IsNullOrEmpty(spec.MethodName))
        {
            return new SiteResolution(
                null,
                MissingType + ":" + (spec.DeclaringTypeName ?? "?") + "::" + (spec.MethodName ?? "?"));
        }

        var declaringType = FindType(searchAssemblies, spec.DeclaringTypeName);
        if (declaringType == null)
            return new SiteResolution(null, MissingType + ":" + spec.DeclaringTypeName);

        // DeclaredOnly: Harmony targets the exactly declared method, so an inherited member with
        // the same name is not the seam this declaration names.
        var candidates = declaringType
            .GetMethods(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == spec.MethodName)
            .Where(
                method =>
                    spec.ArgumentTypes == null
                    || Parameters(method).SequenceEqual(spec.ArgumentTypes))
            .ToArray();

        if (candidates.Length == 0)
            return new SiteResolution(null, MissingMethod + ":" + Describe(spec));

        if (candidates.Length > 1)
        {
            return new SiteResolution(
                null,
                AmbiguousTarget + ":" + Describe(spec) + ":candidates=" + candidates.Length);
        }

        return new SiteResolution(candidates[0], null);
    }

    private static string Describe(PatchTargetSpec spec) =>
        spec.DeclaringTypeName
        + "::"
        + spec.MethodName
        + (spec.ArgumentTypes == null || spec.ArgumentTypes.Length == 0
            ? string.Empty
            : "(" + string.Join(", ", spec.ArgumentTypes.Select(type => type.FullName)) + ")");

    private static Type? FindType(IEnumerable<Assembly> assemblies, string fullName)
    {
        foreach (var assembly in assemblies)
        {
            Type? type;
            try
            {
                type = assembly.GetType(fullName, false, false);
            }
            catch (Exception)
            {
                // One damaged or partially generated interop assembly must not stop the lookup.
                continue;
            }

            if (type != null)
                return type;
        }

        return null;
    }

    private static Type[] Parameters(MethodInfo method) =>
        method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
}
