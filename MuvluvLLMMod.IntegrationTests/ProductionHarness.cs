using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MuvluvLLMMod.IntegrationTests;

internal static class ProductionHarness
{
    public static LoadedPlugin LoadPlugin()
    {
        Harmony.Reset();
        Application.Reset();
        BasePlugin.ResetBoundaries();
        UnityEngine.Object.ResetObjects();
        TMP_Text.ResetCounters();
        Keyboard.current = null;

        var root = Path.Combine(
            Path.GetTempPath(),
            "MuvluvLLMMod.integration." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Paths.PluginPath = root;

        var plugin = new Plugin();
        plugin.Load();
        return new LoadedPlugin(plugin, root);
    }

    public static Plugin PreparePluginForLoad()
    {
        Harmony.Reset();
        Application.Reset();
        BasePlugin.ResetBoundaries();
        UnityEngine.Object.ResetObjects();
        TMP_Text.ResetCounters();
        Keyboard.current = null;
        Paths.PluginPath = Path.Combine(
            Path.GetTempPath(),
            "MuvluvLLMMod.integration." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Paths.PluginPath);
        return new Plugin();
    }

    internal sealed class LoadedPlugin : IDisposable
    {
        private int disposed;

        public LoadedPlugin(Plugin plugin, string root)
        {
            Plugin = plugin;
            Root = root;
        }

        public Plugin Plugin { get; }
        public string Root { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            if (Plugin.Instance != null || Application.CallbackCount != 0)
                _ = Plugin.Unload();

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
