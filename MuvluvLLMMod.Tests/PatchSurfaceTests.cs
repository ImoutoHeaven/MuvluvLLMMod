using System.Runtime.CompilerServices;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class PatchSurfaceTests
{
    [Fact]
    public void Patch_contains_only_render_fallback_targets()
    {
        var patch = ReadProductionSource("Patch.cs");

        Assert.Contains("typeof(TMP_Text), \"set_text\"", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("SkillDescriptionBuilder", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDescription", patch, StringComparison.Ordinal);
        Assert.Contains("FindObjectsByType<TMP_Text>", patch, StringComparison.Ordinal);
        Assert.Contains("HARD RULE", patch, StringComparison.Ordinal);
        Assert.Contains("DebugLogSeenText", patch, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchVerificationPolicy.Verify", patch, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchVerificationException", patch, StringComparison.Ordinal);
        Assert.Contains("PatchPreflightPolicy.Check", patch, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchPreflightException", patch, StringComparison.Ordinal);
        Assert.Contains("Patch.Preflight()", ReadProductionSource("Plugin.cs"), StringComparison.Ordinal);
        Assert.Contains(
            "[HarmonyPrefix]\n    [HarmonyPatch(typeof(TMP_Text), \"set_text\")]",
            patch,
            StringComparison.Ordinal);

        foreach (var forbidden in new[]
        {
            "ScenarioHistoryCell",
            "ScenarioChoiceElementComponent",
            "LoadMasterData",
            "Mosaic",
            "EnableSkipButton",
            "VoiceInterruption",
            "AutoSkipBattle",
            "FontBundle",
            "GenericFont",
            "ScenarioTextStyle",
            "RemoteCatalog"
        })
        {
            Assert.DoesNotContain(forbidden, patch, StringComparison.Ordinal);
        }

        // Scene translation is a second, deliberate seam: GenerateFrames is the only place the game
        // consumes ConfigurationJson, and it runs before Refresh publishes the frame view models.
        Assert.Contains(
            "[HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.GenerateFrames))]",
            patch,
            StringComparison.Ordinal);
        Assert.Contains("TranslateSceneFrames", patch, StringComparison.Ordinal);
        Assert.Contains("sceneCoordinator.Prepare", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Scene_path_registers_translations_before_marking_the_scene_applied()
    {
        var patch = ReadProductionSource("Patch.cs");

        // Registering into the reverse index first is what stops the per-string path from
        // re-enqueueing these lines once they render; marking applied skips the whole scene next
        // time. Reversing the order would reopen the re-entry window.
        var register = patch.IndexOf("RememberResolution", StringComparison.Ordinal);
        var mark = patch.IndexOf("sceneCoordinator.MarkApplied", StringComparison.Ordinal);
        Assert.True(register >= 0);
        Assert.True(mark > register);

        // A rejected or failed batch must leave the frames untouched and fall back to per-string.
        Assert.Contains("batch.TryParseResponse", patch, StringComparison.Ordinal);
        Assert.Contains("response rejected", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_has_no_upstream_dependency_or_dependency_attribute()
    {
        var root = FindRepositoryRoot();
        foreach (var source in Directory.EnumerateFiles(
                     Path.Combine(root, "MuvluvLLMMod"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var contents = File.ReadAllText(source);
            Assert.DoesNotContain("MuvluvMod", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("[BepInDependency", contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Patch_routes_external_setters_and_refreshes_through_the_restore_decision()
    {
        var patch = ReadProductionSource("Patch.cs");

        Assert.Contains("BeginExternalSetter", patch, StringComparison.Ordinal);
        Assert.Contains("BeginPluginRefresh", patch, StringComparison.Ordinal);
        Assert.Contains("TmpTextAssignmentOrigin.ExternalSetter", patch, StringComparison.Ordinal);
        Assert.Contains("TmpTextAssignmentOrigin.PluginRefresh", patch, StringComparison.Ordinal);
        Assert.Contains("TmpRestoreDecision.Resolve", patch, StringComparison.Ordinal);
        Assert.Contains("tmpWriteOwnership.TryConsume(__instance, instanceId)", patch, StringComparison.Ordinal);
        Assert.Contains("tmpWriteOwnership.BeginPluginWrite(assignment)", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("translatingTmp", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidateIfTextChanged", patch, StringComparison.Ordinal);

        var setter = patch.IndexOf("public static void TranslateTmpSetter", StringComparison.Ordinal);
        var consume = setter >= 0
            ? patch.IndexOf("tmpWriteOwnership.TryConsume(__instance, instanceId)", setter, StringComparison.Ordinal)
            : -1;
        var external = setter >= 0
            ? patch.IndexOf("tmpProvenance.BeginExternalSetter(__instance, instanceId)", setter, StringComparison.Ordinal)
            : -1;
        Assert.True(setter >= 0);
        Assert.True(consume > setter);
        Assert.True(external > consume);

        var refresh = patch.IndexOf("public static void RefreshAllTmpText", StringComparison.Ordinal);
        var resolve = refresh >= 0
            ? patch.IndexOf("ResolveTmpValue(ref value, assignment, TmpTextAssignmentOrigin.PluginRefresh)", refresh, StringComparison.Ordinal)
            : -1;
        var beginWrite = refresh >= 0
            ? patch.IndexOf("using (tmpWriteOwnership.BeginPluginWrite(assignment))", refresh, StringComparison.Ordinal)
            : -1;
        var exactWrite = beginWrite >= 0
            ? patch.IndexOf("text.text = value;", beginWrite, StringComparison.Ordinal)
            : -1;
        Assert.True(refresh >= 0);
        Assert.True(resolve > refresh);
        Assert.True(beginWrite > resolve);
        Assert.True(exactWrite > beginWrite);
    }

    [Fact]
    public void Lifecycle_retirement_clears_tmp_state_before_unpatch()
    {
        var patch = ReadProductionSource("Patch.cs");
        var plugin = ReadProductionSource("Plugin.cs");
        Assert.Contains("tmpWriteOwnership.ResetForLifecycle()", patch, StringComparison.Ordinal);
        Assert.Contains("tmpProvenance.ResetForLifecycle()", patch, StringComparison.Ordinal);
        Assert.Contains("RunCleanupStep(\"retire TMP translation state\", Patch.Retire", plugin, StringComparison.Ordinal);

        var initialize = patch.IndexOf("public static void Initialize", StringComparison.Ordinal);
        var reset = patch.IndexOf("ResetRuntimeState();", initialize, StringComparison.Ordinal);
        var patchAll = patch.IndexOf("harmony.PatchAll", initialize, StringComparison.Ordinal);
        Assert.True(reset > initialize);
        Assert.True(patchAll > reset);

        var retire = plugin.IndexOf("Patch.Retire", StringComparison.Ordinal);
        var unpatch = plugin.IndexOf("\"unpatch Harmony\"", StringComparison.Ordinal);
        Assert.True(retire >= 0);
        Assert.True(unpatch > retire);
    }

    [Fact]
    public void Required_patch_verification_is_before_runtime_component_creation()
    {
        var patch = ReadProductionSource("Patch.cs");
        var initialize = patch.IndexOf("public static void Initialize", StringComparison.Ordinal);
        var preflightCall = patch.IndexOf("var preflight = Preflight();", initialize, StringComparison.Ordinal);
        var preflightFailure = patch.IndexOf("if (!preflight.Ok)", preflightCall, StringComparison.Ordinal);
        var throwPreflight = patch.IndexOf("throw new HarmonyPatchPreflightException", preflightFailure, StringComparison.Ordinal);
        var patchAll = patch.IndexOf("harmony.PatchAll(typeof(Patch))", initialize, StringComparison.Ordinal);
        var verification = patch.IndexOf("var verification = VerifyPatches(harmony.Id, preflight.Targets)", initialize, StringComparison.Ordinal);
        var failure = patch.IndexOf("if (!verification.Succeeded)", verification, StringComparison.Ordinal);
        var throwFailure = patch.IndexOf("throw new HarmonyPatchVerificationException(verification)", failure, StringComparison.Ordinal);

        Assert.True(initialize >= 0);
        // Targets resolve before any Harmony install, and ownership is checked after it.
        Assert.True(preflightCall > initialize);
        Assert.True(preflightFailure > preflightCall);
        Assert.True(throwPreflight > preflightFailure);
        Assert.True(patchAll > throwPreflight);
        Assert.True(verification > patchAll);
        Assert.True(failure > verification);
        Assert.True(throwFailure > failure);

        // The loader runs the precheck before any configuration side effect, so a moved seam
        // aborts the generation with nothing allocated.
        var plugin = ReadProductionSource("Plugin.cs");
        var loaderPreflight = plugin.IndexOf("var preflight = Patch.Preflight();", StringComparison.Ordinal);
        var loaderFailure = plugin.IndexOf("if (!preflight.Ok)", loaderPreflight, StringComparison.Ordinal);
        var loaderThrow = plugin.IndexOf("throw new HarmonyPatchPreflightException", loaderFailure, StringComparison.Ordinal);
        var configuration = plugin.IndexOf("Config.Initialize(base.Config, generation)", StringComparison.Ordinal);
        var patchCall = plugin.IndexOf("Patch.Initialize(harmony)", StringComparison.Ordinal);
        var hotkey = plugin.IndexOf("hotkey = AddComponent<Hotkey>()", patchCall, StringComparison.Ordinal);
        var persistence = plugin.IndexOf("RunPersistenceLoopAsync", patchCall, StringComparison.Ordinal);
        var machine = plugin.IndexOf("machineLifecycle.Initialize", patchCall, StringComparison.Ordinal);

        Assert.True(loaderPreflight >= 0);
        Assert.True(loaderFailure > loaderPreflight);
        Assert.True(loaderThrow > loaderFailure);
        Assert.True(configuration > loaderThrow);
        Assert.True(patchCall >= 0);
        Assert.True(hotkey > patchCall);
        Assert.True(persistence > patchCall);
        Assert.True(machine > patchCall);
    }

    [Fact]
    public void Config_contains_only_the_plugin_owned_settings()
    {
        var config = ReadProductionSource("Config.cs");
        foreach (var upstreamSetting in new[]
        {
            "DynamicMosaic",
            "EnableSkipButton",
            "VoiceInterruption",
            "AutoSkipBattle",
            "TranslationCDN",
            "FontBundlePath",
            "FontAssetName"
        })
        {
            Assert.DoesNotContain(upstreamSetting, config, StringComparison.Ordinal);
        }

        foreach (var required in new[]
        {
            "DebugLogSeenText",
            "RefreshPeriodSeconds",
            "TranslatePeriodSeconds"
        })
        {
            Assert.Contains(required, config, StringComparison.Ordinal);
        }
    }

    private static string ReadProductionSource(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MuvluvLLMMod", fileName));

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
