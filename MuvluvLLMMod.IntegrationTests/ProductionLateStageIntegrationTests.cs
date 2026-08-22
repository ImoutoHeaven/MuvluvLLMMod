using System.Reflection;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using Xunit;

namespace MuvluvLLMMod.IntegrationTests;

// Kept in a final-named class because this test intentionally quarantines the process-wide
// production generation after proving that a timed-out real load cannot leave late hooks.
public sealed class ZzzProductionLateStageIntegrationTests
{
    [Fact]
    public async Task Real_blocked_add_component_is_destroyed_after_timeout_and_late_completion()
    {
        try
        {
            var plugin = ProductionHarness.PreparePluginForLoad();
            BasePlugin.BlockAddComponent = true;

            var load = Task.Run(() => Record.Exception(plugin.Load));
            await BasePlugin.AddComponentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var unload = Task.Run(() => plugin.Unload());
            await WaitUntilAsync(() => Plugin.IsCleaningUp);
            Assert.False(await unload.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Empty(Harmony.SnapshotApplications());
            Assert.Equal(0, Application.CallbackCount);

            BasePlugin.AddComponentRelease.TrySetResult(true);
            var loadException = await load.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(loadException);
            Assert.Empty(UnityEngine.Object.FindObjectsByType<Hotkey>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None));
            Assert.Null(Plugin.Instance);
            Assert.False(plugin.Unload());
            Assert.True(Plugin.IsCleaningUp);
        }
        finally
        {
            BasePlugin.AddComponentRelease.TrySetResult(true);
            ResetQuarantinedGenerationForTestOrdering();
        }
    }

    [Fact]
    public async Task Real_blocked_native_registration_is_removed_after_timeout_and_late_completion()
    {
        try
        {
            var plugin = ProductionHarness.PreparePluginForLoad();
            Application.BlockAddQuitting = true;

            var load = Task.Run(() => Record.Exception(plugin.Load));
            await Application.AddQuittingEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var unload = Task.Run(() => plugin.Unload());
            await WaitUntilAsync(() => Plugin.IsCleaningUp);
            Assert.False(await unload.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Empty(Harmony.SnapshotApplications());
            Assert.Equal(0, Application.CallbackCount);

            Application.AddQuittingRelease.TrySetResult(true);
            var loadException = await load.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(loadException);
            Assert.Equal(1, Application.AddCalls);
            Assert.Equal(1, Application.RemoveCalls);
            Assert.Empty(Application.SnapshotHandlers());
            Assert.Empty(Harmony.SnapshotApplications());
            Assert.Null(Plugin.Instance);
            Assert.False(plugin.Unload());
            Assert.True(Plugin.IsCleaningUp);
        }
        finally
        {
            Application.AddQuittingRelease.TrySetResult(true);
            ResetQuarantinedGenerationForTestOrdering();
        }
    }

    [Fact]
    public async Task Real_blocked_patch_all_is_unpatched_after_timeout_and_late_completion()
    {
        try
        {
            var plugin = ProductionHarness.PreparePluginForLoad();
            Harmony.BlockPatchAll = true;
            Harmony.BlockAfterPatchAllPublication = true;

            var load = Task.Run(() => Record.Exception(plugin.Load));
            await Harmony.PatchAllEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var unload = Task.Run(() => plugin.Unload());
            await WaitUntilAsync(() => Plugin.IsCleaningUp);
            Assert.False(await unload.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Empty(Harmony.SnapshotApplications());
            var unpatchCallsBeforeLateCompletion = Harmony.UnpatchSelfCalls;
            Assert.Equal(1, unpatchCallsBeforeLateCompletion);
            // Repeated cleanup retries only retained reservations and does not repeat the
            // teardown graph or unpatch a boundary that has not returned yet.
            Assert.False(plugin.Unload());
            Assert.Equal(unpatchCallsBeforeLateCompletion, Harmony.UnpatchSelfCalls);

            // PatchAll publishes the exact three production hooks only after the external boundary
            // is released. The already-admitted stage must immediately unpatch its local Harmony
            // owner instead of committing it into the failed generation.
            Harmony.PatchAllRelease.TrySetResult(true);
            await Harmony.PatchAllPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, Harmony.SnapshotApplications().Count);
            Harmony.PatchAllPostPublishRelease.TrySetResult(true);
            var loadException = await load.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(loadException);
            Assert.True(Harmony.UnpatchSelfCalls > unpatchCallsBeforeLateCompletion);
            Assert.Empty(Harmony.SnapshotApplications());
            Assert.Null(Plugin.Instance);
            Assert.Null(Plugin.CurrentCache);
            Assert.Null(Plugin.CurrentResolver);
            Assert.False(Patch.isPlayingScenario);
            Assert.Equal(0, plugin.AddComponentCalls);
            Assert.Equal(0, Application.CallbackCount);
            Assert.False(plugin.Unload());
            Assert.Empty(Harmony.SnapshotApplications());
            Assert.True(Plugin.IsCleaningUp);
        }
        finally
        {
            Harmony.PatchAllRelease.TrySetResult(true);
            Harmony.PatchAllPostPublishRelease.TrySetResult(true);
            ResetQuarantinedGenerationForTestOrdering();
        }
    }

    private static void ResetQuarantinedGenerationForTestOrdering()
    {
        var gateField = typeof(Plugin).GetField(
            "lifecycleGate",
            BindingFlags.Static | BindingFlags.NonPublic);
        var stateField = typeof(PluginLifecycleGate).GetField(
            "state",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var gate = gateField?.GetValue(null);
        stateField?.SetValue(gate, PluginLifecycleState.NotLoaded);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
