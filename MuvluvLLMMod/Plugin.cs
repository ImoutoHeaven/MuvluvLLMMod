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
    private static readonly TranslationRetryPolicy LlmRetryPolicy = new();
    private static readonly MachineTranslatorLifecycle MachineLifecycle = new(
        exception => Logger.Error("[LLM] Lifecycle failure: " + exception.GetType().Name),
        LlmRetryPolicy);
    private static CancellationTokenSource? persistenceCancellation;
    private static Task persistenceTask = Task.CompletedTask;
    private static MachineTranslator? currentMachine;
    private static Harmony? harmony;

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

    public override void Load()
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

        persistenceCancellation = new CancellationTokenSource();
        persistenceTask = Cache.RunPersistenceLoopAsync(persistenceCancellation.Token);
        ObserveBackgroundTask(persistenceTask, "cache persistence");

        var machineSettings = CaptureMachineSettings();
        MachineLifecycle.Initialize(
            machineSettings.Enabled,
            machineSettings.RequestsPerSecond,
            (limiter, backlog) => CreateMachineTranslator(machineSettings, limiter, backlog));

        harmony = new Harmony(PluginGuid);
        Patch.Initialize(harmony);
        Instance = AddComponent<Hotkey>();

        Logger.Info($"Plugin {PluginGuid} loaded successfully");
    }

    public override bool Unload()
    {
        if (Cache != null)
        {
            Cache.FreezeMutations();
            MachineLifecycle.Shutdown();

            var persistenceSource = persistenceCancellation;
            persistenceCancellation = null;
            persistenceSource?.Cancel();
            if (persistenceSource != null)
            {
                _ = persistenceTask.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        persistenceSource.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            Cache.Flush();
        }

        MuvluvLLMMod.Config.Shutdown();
        harmony?.UnpatchSelf();
        harmony = null;
        Instance = null;
        Volatile.Write(ref currentMachine, null);
        Logger.Info($"Plugin {PluginGuid} unloaded");
        return base.Unload();
    }

    internal static void ReloadMachineTranslator()
    {
        if (Cache == null)
            return;

        var settings = CaptureMachineSettings();
        MachineLifecycle.Reload(
            settings.Enabled,
            settings.RequestsPerSecond,
            (limiter, backlog) => CreateMachineTranslator(settings, limiter, backlog));
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
        TranslationPriorityBacklog priorityBacklog)
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
            retryPolicy: LlmRetryPolicy);
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
