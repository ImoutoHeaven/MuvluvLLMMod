using System.Net.Http;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace MuvluvLLMMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "muvluv.llmmod";
    public const string PluginName = "MuvluvLLMMod";
    public const string PluginVersion = "1.0.0";

    internal static new ManualLogSource Log = null!;
    internal static TranslationCache Cache = null!;
    internal static TranslationResolver Resolver = null!;
    internal static MonoBehaviour? Instance { get; private set; }

    private static readonly HttpClient LlmHttpClient = OpenAiHttpClientFactory.CreateClient();
    private static readonly MachineTranslatorLifecycle MachineLifecycle = new(
        exception => Logger.Error("[LLM] Lifecycle failure: " + exception.GetType().Name));
    private static CancellationTokenSource? persistenceCancellation;
    private static Task persistenceTask = Task.CompletedTask;
    private static MachineTranslator? currentMachine;
    private static Harmony? harmony;
    private static int loadStarted;
    private static int cleanupStarted;
    private static int cleanupSucceeded;
    private static readonly Action ApplicationQuittingHandler = OnApplicationQuitting;
    private static Il2CppSystem.Action? applicationQuittingHandler;

    [ThreadStatic]
    private static bool observingEnqueue;
    [ThreadStatic]
    private static bool enqueueAccepted;

    private sealed record MachineSettings(
        bool Enabled,
        string Endpoint,
        string Model,
        string ApiKey,
        int TimeoutSeconds,
        int RetryCount,
        int RequestsPerSecond,
        int MaxInFlight,
        float TranslatePeriodSeconds);

    internal static (int Completed, int InFlight, int Failed) ProgressSnapshot =>
        Volatile.Read(ref currentMachine)?.ProgressSnapshot ?? (0, 0, 0);

    internal static bool IsCleaningUp => Volatile.Read(ref cleanupStarted) != 0;

    public override void Load()
    {
        if (Interlocked.CompareExchange(ref loadStarted, 1, 0) != 0)
        {
            if (Log != null)
                Logger.Warn("Load called more than once; keeping the existing Hotkey component");
            return;
        }

        Interlocked.Exchange(ref cleanupStarted, 0);
        Volatile.Write(ref cleanupSucceeded, 0);

        try
        {
            TrySetUtf8Console();

            Log = base.Log;
            Logger.Info($"Plugin {PluginGuid} is loading");
            MuvluvLLMMod.Config.Initialize(base.Config);

            Cache = new TranslationCache(
                ResolvePluginPath(MuvluvLLMMod.Config.CacheDirectory.Value),
                message => Logger.Warn("Translation cache " + message));
            Cache.Load();
            Resolver = new TranslationResolver(Cache, EnqueuePriority, EnqueueNormal, CancelTranslation);

            // Patch and inject the component before starting any background workers. If either
            // stage fails, the catch below can use the single normal cleanup path.
            harmony = new Harmony(PluginGuid);
            Patch.Initialize(harmony);
            Instance = AddComponent<Hotkey>();
            RegisterApplicationQuittingHandler();

            persistenceCancellation = new CancellationTokenSource();
            persistenceTask = Cache.RunPersistenceLoopAsync(persistenceCancellation.Token);
            ObserveBackgroundTask(persistenceTask, "cache persistence");

            var machineSettings = CaptureMachineSettings();
            var retryPolicy = new TranslationRetryPolicy();
            MachineLifecycle.Initialize(
                machineSettings.Enabled,
                machineSettings.RequestsPerSecond,
                (limiter, backlog) => CreateMachineTranslator(machineSettings, limiter, backlog, retryPolicy),
                retryPolicy);

            Logger.Info($"Plugin {PluginGuid} loaded successfully");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public override bool Unload() => Cleanup();

    internal static bool Cleanup()
    {
        if (Interlocked.CompareExchange(ref cleanupStarted, 1, 0) != 0)
            return Volatile.Read(ref cleanupSucceeded) != 0;

        var succeeded = true;
        CleanupStep("remove application-quit handler", RemoveApplicationQuittingHandler, ref succeeded);

        CleanupStep("disable Hotkey", () =>
        {
            var instance = Instance;
            Instance = null;
            if (instance == null)
                return;

            instance.enabled = false;
            UnityEngine.Object.Destroy(instance);
        }, ref succeeded);

        // Keep this order: freeze → stop workers → cancel persistence → flush → unpatch.
        CleanupStep("freeze cache mutations", () =>
        {
            if (Cache != null)
                Cache.FreezeMutations();
        }, ref succeeded);
        CleanupStep("shutdown machine translator", MachineLifecycle.Shutdown, ref succeeded);

        CancellationTokenSource? persistenceSource = null;
        CleanupStep("cancel cache persistence", () =>
        {
            persistenceSource = Interlocked.Exchange(ref persistenceCancellation, null);
            if (persistenceSource == null)
                return;

            persistenceSource.Cancel();
            var task = persistenceTask;
            _ = task.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    persistenceSource.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }, ref succeeded);

        CleanupStep("flush cache", () =>
        {
            if (Cache == null || Cache.FlushTerminal())
                return;

            Logger.Error(
                $"[LLM] Terminal cache flush failed after {TranslationCache.TerminalFlushMaxAttempts} attempts; "
                + "dirty cache data may be unrecoverable");
            throw new InvalidOperationException("terminal cache flush failed");
        }, ref succeeded);
        CleanupStep("unpatch Harmony", () =>
        {
            harmony?.UnpatchSelf();
            harmony = null;
        }, ref succeeded);
        CleanupStep("shutdown configuration", MuvluvLLMMod.Config.Shutdown, ref succeeded);
        Volatile.Write(ref currentMachine, null);
        Volatile.Write(ref cleanupSucceeded, succeeded ? 1 : 0);
        Volatile.Write(ref loadStarted, 0);

        if (succeeded)
            Logger.Info($"Plugin {PluginGuid} unloaded");
        else
            Logger.Error($"Plugin {PluginGuid} cleanup completed with errors");
        return succeeded;
    }

    private static void CleanupStep(string name, Action action, ref bool succeeded)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            succeeded = false;
            try
            {
                Logger.Error($"[LLM] Cleanup step '{name}' failed: {exception.GetType().Name}");
            }
            catch
            {
            }
        }
    }

    private static void RegisterApplicationQuittingHandler()
    {
        // Il2CppInterop creates a native delegate wrapper during this conversion. Retain
        // that exact wrapper so removal does not perform a second, unequal conversion.
        var handler = (Il2CppSystem.Action)ApplicationQuittingHandler;
        applicationQuittingHandler = handler;
        Application.add_quitting(handler);
    }

    private static void RemoveApplicationQuittingHandler()
    {
        var handler = Volatile.Read(ref applicationQuittingHandler);
        if (handler == null)
            return;

        Application.remove_quitting(handler);
        // The field is deliberately cleared only after remove_quitting returns successfully.
        Interlocked.CompareExchange(ref applicationQuittingHandler, null, handler);
    }

    private static void OnApplicationQuitting() => Cleanup();

    internal static void ReloadMachineTranslator()
    {
        if (Cache == null)
            return;

        var settings = CaptureMachineSettings();
        var retryPolicy = new TranslationRetryPolicy();
        MachineLifecycle.Reload(
            settings.Enabled,
            settings.RequestsPerSecond,
            (limiter, backlog) => CreateMachineTranslator(settings, limiter, backlog, retryPolicy),
            retryPolicy);
    }

    internal static void BeginEnqueueObservation()
    {
        observingEnqueue = true;
        enqueueAccepted = false;
    }

    internal static bool EndEnqueueObservation()
    {
        var accepted = enqueueAccepted;
        observingEnqueue = false;
        enqueueAccepted = false;
        return accepted;
    }

    private static MachineTranslator CreateMachineTranslator(
        MachineSettings machineSettings,
        RequestRateLimiter limiter,
        TranslationPriorityBacklog priorityBacklog,
        TranslationRetryPolicy retryPolicy)
    {
        var settings = new OpenAiChatSettings(
            machineSettings.Endpoint,
            machineSettings.Model,
            machineSettings.ApiKey,
            machineSettings.TimeoutSeconds,
            machineSettings.RetryCount);
        var client = new OpenAiChatClient(
            LlmHttpClient,
            settings,
            limiter,
            message => Logger.Warn("[LLM] " + message));
        var machine = new MachineTranslator(
            Cache,
            client.TranslateAsync,
            machineSettings.MaxInFlight,
            TimeSpan.FromSeconds(machineSettings.TranslatePeriodSeconds),
            priorityBacklog,
            message => Logger.Info("[LLM] " + message),
            retryPolicy: retryPolicy);
        Volatile.Write(ref currentMachine, machine);
        return machine;
    }

    private static void EnqueuePriority(string template, long pendingGeneration)
    {
        var accepted = MachineLifecycle.EnqueuePriority(template, pendingGeneration);
        if (observingEnqueue && accepted)
            enqueueAccepted = true;
    }

    private static void EnqueueNormal(string template, long pendingGeneration)
    {
        var accepted = MachineLifecycle.EnqueueNormal(template, pendingGeneration);
        if (observingEnqueue && accepted)
            enqueueAccepted = true;
    }

    private static void CancelTranslation(string template, long pendingGeneration) =>
        MachineLifecycle.Cancel(template, pendingGeneration);

    private static MachineSettings CaptureMachineSettings() => new(
        MuvluvLLMMod.Config.LlmEnable.Value,
        MuvluvLLMMod.Config.LlmEndpoint.Value,
        MuvluvLLMMod.Config.LlmModel.Value,
        MuvluvLLMMod.Config.LlmApiKey.Value,
        Math.Max(1, MuvluvLLMMod.Config.LlmTimeoutSeconds.Value),
        Math.Max(1, MuvluvLLMMod.Config.LlmRetryCount.Value),
        Math.Max(1, MuvluvLLMMod.Config.LlmRequestsPerSecond.Value),
        Math.Max(1, MuvluvLLMMod.Config.LlmMaxInFlight.Value),
        Math.Max(0.1f, MuvluvLLMMod.Config.LlmTranslatePeriodSeconds.Value));

    private static string ResolvePluginPath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(Paths.PluginPath, path);

    private static void ObserveBackgroundTask(Task task, string name)
    {
        _ = task.ContinueWith(
            completed => Logger.Error(name + " failed: " + completed.Exception?.GetBaseException().GetType().Name),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void TrySetUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
        }
    }
}

public static class Core
{
    internal static ManualLogSource Log => Plugin.Log;
    internal static TranslationCache Cache => Plugin.Cache;
    internal static TranslationResolver Resolver => Plugin.Resolver;

    internal static void ReloadMachineTranslator() => Plugin.ReloadMachineTranslator();

    internal static void BeginEnqueueObservation() => Plugin.BeginEnqueueObservation();

    internal static bool EndEnqueueObservation() => Plugin.EndEnqueueObservation();
}
