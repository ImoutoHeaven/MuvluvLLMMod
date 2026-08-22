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
    internal static MonoBehaviour? Instance => RunningResources?.Hotkey;

    private static readonly HttpClient LlmHttpClient = OpenAiHttpClientFactory.CreateClient();
    private static readonly PluginLifecycleGate lifecycleGate = new();
    private static readonly NativeDelegateCoordinator<Il2CppSystem.Action>
        applicationQuittingCoordinator = new();
    private static readonly Action ApplicationQuittingHandler = OnApplicationQuitting;

    [ThreadStatic]
    private static bool observingEnqueue;
    [ThreadStatic]
    private static bool enqueueAccepted;

    private sealed class GenerationResources
    {
        public GenerationResources(long generationId) => GenerationId = generationId;

        public long GenerationId { get; }
        public TranslationCache? Cache { get; set; }
        public TranslationResolver? Resolver { get; set; }
        public MachineTranslatorLifecycle? MachineLifecycle { get; set; }
        public Harmony? Harmony { get; set; }
        public Hotkey? Hotkey { get; set; }
        public CancellationTokenSource? PersistenceCancellation { get; set; }
        public Task PersistenceTask { get; set; } = Task.CompletedTask;
        public PluginLifecycleGate.PluginGenerationLease? ConfigLease { get; set; }
    }

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

    private static GenerationResources? RunningResources =>
        lifecycleGate.CurrentOwner<GenerationResources>();

    internal static TranslationCache? CurrentCache => RunningResources?.Cache;
    internal static TranslationResolver? CurrentResolver => RunningResources?.Resolver;

    internal static (int Completed, int InFlight, int Failed) ProgressSnapshot =>
        RunningResources?.MachineLifecycle?.ProgressSnapshot ?? (0, 0, 0);

    internal static bool IsCleaningUp => lifecycleGate.IsCleaningUp;

    public override void Load()
    {
        if (!lifecycleGate.TryBeginLoad(out var generation))
        {
            SafeWarn("Load called while another generation is active or quarantined; refusing a duplicate load");
            return;
        }

        Log = base.Log;
        var resources = new GenerationResources(generation.Id);

        try
        {
            if (!generation.AttachOwner(resources))
                throw new InvalidOperationException("plugin generation lost ownership before load started");

            using (RequireLoadStage(generation))
            {
                TrySetUtf8Console();
                Logger.Info($"Plugin {PluginGuid} is loading (generation {generation.Id})");
                MuvluvLLMMod.Config.Initialize(base.Config);
                resources.MachineLifecycle = CreateMachineLifecycle();
            }

            using (RequireLoadStage(generation))
            {
                var cache = new TranslationCache(
                    ResolvePluginPath(MuvluvLLMMod.Config.CacheDirectory.Value),
                    message => SafeWarn("Translation cache " + message));
                resources.Cache = cache;
                cache.Load();
                resources.Resolver = new TranslationResolver(
                    cache,
                    (template, pendingGeneration) => EnqueuePriority(
                        generation,
                        template,
                        pendingGeneration),
                    (template, pendingGeneration) => EnqueueNormal(
                        generation,
                        template,
                        pendingGeneration),
                    (template, pendingGeneration) => CancelTranslation(
                        generation,
                        template,
                        pendingGeneration));
            }

            using (RequireLoadStage(generation))
            {
                resources.Harmony = new Harmony(PluginGuid);
                Patch.Initialize(resources.Harmony);
            }

            using (RequireLoadStage(generation))
            {
                resources.Hotkey = AddComponent<Hotkey>();
                if (resources.Hotkey == null)
                    throw new InvalidOperationException("Hotkey component injection returned null");
            }

            using (RequireLoadStage(generation))
                RegisterApplicationQuittingHandler(generation.Id);

            using (RequireLoadStage(generation))
            {
                var cache = resources.Cache
                    ?? throw new InvalidOperationException("cache was not initialized");
                var cancellation = new CancellationTokenSource();
                resources.PersistenceCancellation = cancellation;
                resources.PersistenceTask = cache.RunPersistenceLoopAsync(cancellation.Token);
                ObserveBackgroundTask(resources.PersistenceTask, "cache persistence");
            }

            using (RequireLoadStage(generation))
            {
                var settings = CaptureMachineSettings();
                var retryPolicy = new TranslationRetryPolicy();
                var machineLifecycle = resources.MachineLifecycle
                    ?? throw new InvalidOperationException("machine lifecycle was not initialized");
                if (!machineLifecycle.Initialize(
                        settings.Enabled,
                        settings.RequestsPerSecond,
                        (limiter, backlog) => CreateMachineTranslator(
                            resources,
                            settings,
                            limiter,
                            backlog,
                            retryPolicy),
                        retryPolicy))
                {
                    throw new InvalidOperationException("machine lifecycle duplicate initialization");
                }
            }

            if (!lifecycleGate.TryPublishRunning(generation))
                throw new OperationCanceledException(
                    "plugin load was canceled before the generation could become Running",
                    generation.CancellationToken);

            using (RequireRunningStage(generation))
            {
                Patch.Activate();
                var lease = generation.TryAcquireRunningLease()
                    ?? throw new OperationCanceledException(
                        "plugin load was canceled during activation",
                        generation.CancellationToken);
                resources.ConfigLease = lease;
                if (!MuvluvLLMMod.Config.Activate(lease, ReloadMachineTranslator))
                    throw new InvalidOperationException("configuration activation was rejected");
            }

            if (!generation.IsRunning)
                throw new OperationCanceledException(
                    "plugin load was canceled after activation",
                    generation.CancellationToken);

            Logger.Info($"Plugin {PluginGuid} loaded successfully (generation {generation.Id})");
        }
        catch
        {
            // If another caller already owns cleanup, this waits for the same generation result.
            // A failed result quarantines the gate and prevents a new load over leaked resources.
            Cleanup();
            throw;
        }
    }

    public override bool Unload() => Cleanup();

    internal static bool Cleanup() => lifecycleGate.Cleanup(
        CleanupGeneration,
        exception => SafeError("[LLM] generation cleanup failed: " + exception.GetType().Name));

    private static bool CleanupGeneration(PluginLifecycleGate.PluginGeneration generation)
    {
        var resources = generation.GetOwner<GenerationResources>();
        var succeeded = true;

        // Stop event producers first. Config.Shutdown revokes its generation lease and waits for
        // a handler that was already inside the callback; removing the event delegate alone is
        // not enough to prevent a stale reload.
        RunCleanupStep("shutdown configuration", MuvluvLLMMod.Config.Shutdown, ref succeeded);
        RunCleanupStep(
            "remove application-quit handler",
            () => RemoveApplicationQuittingHandler(generation.Id),
            ref succeeded);
        RunCleanupStep(
            "disable Hotkey",
            () =>
            {
                if (resources == null)
                    return;
                var hotkey = resources.Hotkey;
                resources.Hotkey = null;
                if (hotkey == null)
                    return;
                hotkey.enabled = false;
                UnityEngine.Object.Destroy(hotkey);
            },
            ref succeeded);
        RunCleanupStep("retire TMP translation state", Patch.Retire, ref succeeded);

        // Keep this semantic order: config stop -> patch/component retirement -> freeze ->
        // machine stop -> persistence cancellation -> terminal flush -> unpatch.
        RunCleanupStep(
            "freeze cache mutations",
            () => resources?.Cache?.FreezeMutations(),
            ref succeeded);
        RunCleanupStep(
            "shutdown machine translator",
            () => resources?.MachineLifecycle?.Shutdown(),
            ref succeeded);
        RunCleanupStep(
            "cancel cache persistence",
            () => StopPersistence(resources),
            ref succeeded);
        RunCleanupStep(
            "flush cache",
            () =>
            {
                if (resources?.Cache == null || resources.Cache.FlushTerminal())
                    return;
                SafeError(
                    $"[LLM] Terminal cache flush failed within the {TranslationBudget.DefaultTerminalFlushBudget.TotalSeconds:F0}s "
                    + $"shutdown budget ({TranslationCache.TerminalFlushMaxAttempts} paced attempts); "
                    + "dirty cache data may be recoverable from the state journal");
                throw new InvalidOperationException("terminal cache flush failed");
            },
            ref succeeded);
        RunCleanupStep(
            "unpatch Harmony",
            () =>
            {
                if (resources == null)
                    return;
                var harmony = resources.Harmony;
                if (harmony == null)
                    return;
                harmony.UnpatchSelf();
                resources.Harmony = null;
            },
            ref succeeded);

        if (succeeded)
            SafeInfo($"Plugin {PluginGuid} unloaded (generation {generation.Id})");
        else
            SafeError($"Plugin {PluginGuid} generation {generation.Id} is quarantined after cleanup errors");
        return succeeded;
    }

    private static void RunCleanupStep(string name, Action action, ref bool succeeded)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            succeeded = false;
            SafeError($"[LLM] Cleanup step '{name}' failed: {exception.GetType().Name}");
        }
    }

    private static void StopPersistence(GenerationResources? resources)
    {
        if (resources?.PersistenceCancellation == null)
            return;

        var cancellation = resources.PersistenceCancellation;
        var persistenceTask = resources.PersistenceTask;
        resources.PersistenceCancellation = null;
        cancellation.Cancel();
        if (!persistenceTask.Wait(TranslationBudget.DefaultShutdownTimeout))
        {
            // A writer may be in synchronous filesystem code. Do not dispose its CTS while the
            // task can still observe it; retain an observation/cleanup continuation and let the
            // terminal flush report the generation failure if the gate remains occupied.
            _ = persistenceTask.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException("cache persistence did not stop before the shutdown budget");
        }

        try
        {
            persistenceTask.GetAwaiter().GetResult();
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void RegisterApplicationQuittingHandler(long generation)
    {
        if (!applicationQuittingCoordinator.TryRegister(
                generation,
                () => (Il2CppSystem.Action)ApplicationQuittingHandler,
                handler => Application.add_quitting(handler)))
        {
            throw new InvalidOperationException(
                $"application-quit delegate registration is unavailable ({applicationQuittingCoordinator.State})");
        }
    }

    private static void RemoveApplicationQuittingHandler(long generation)
    {
        if (!applicationQuittingCoordinator.TryRemove(
                generation,
                handler =>
                {
                    Application.remove_quitting(handler);
                    return true;
                }))
        {
            throw new InvalidOperationException(
                $"application-quit delegate removal failed ({applicationQuittingCoordinator.State})");
        }
    }

    private static void OnApplicationQuitting() => Cleanup();

    internal static void ReloadMachineTranslator(PluginLifecycleGate.PluginGenerationLease lease)
    {
        if (!lease.IsActive)
            return;

        var resources = lease.GetOwner<GenerationResources>();
        var machineLifecycle = resources?.MachineLifecycle;
        var cache = resources?.Cache;
        if (resources == null || machineLifecycle == null || cache == null || !lease.IsActive)
            return;

        var settings = CaptureMachineSettings();
        var retryPolicy = new TranslationRetryPolicy();
        _ = machineLifecycle.Reload(
            settings.Enabled,
            settings.RequestsPerSecond,
            (limiter, backlog) => CreateMachineTranslator(
                resources,
                settings,
                limiter,
                backlog,
                retryPolicy),
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
        GenerationResources resources,
        MachineSettings machineSettings,
        RequestRateLimiter limiter,
        TranslationPriorityBacklog priorityBacklog,
        TranslationRetryPolicy retryPolicy)
    {
        var cache = resources.Cache
            ?? throw new InvalidOperationException("machine translator cache owner is missing");
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
            message => SafeWarn("[LLM] " + message));
        return new MachineTranslator(
            cache,
            client.TranslateAsync,
            machineSettings.MaxInFlight,
            TimeSpan.FromSeconds(machineSettings.TranslatePeriodSeconds),
            priorityBacklog,
            message => SafeInfo("[LLM] " + message),
            retryPolicy: retryPolicy);
    }

    private static void EnqueuePriority(
        PluginLifecycleGate.PluginGeneration generation,
        string template,
        long pendingGeneration)
    {
        var resources = GetRunningResources(generation);
        var accepted = resources?.MachineLifecycle?.EnqueuePriority(template, pendingGeneration) == true;
        if (observingEnqueue && accepted)
            enqueueAccepted = true;
    }

    private static void EnqueueNormal(
        PluginLifecycleGate.PluginGeneration generation,
        string template,
        long pendingGeneration)
    {
        var resources = GetRunningResources(generation);
        var accepted = resources?.MachineLifecycle?.EnqueueNormal(template, pendingGeneration) == true;
        if (observingEnqueue && accepted)
            enqueueAccepted = true;
    }

    private static void CancelTranslation(
        PluginLifecycleGate.PluginGeneration generation,
        string template,
        long pendingGeneration)
    {
        GetRunningResources(generation)?.MachineLifecycle?.Cancel(template, pendingGeneration);
    }

    private static GenerationResources? GetRunningResources(
        PluginLifecycleGate.PluginGeneration generation) =>
        generation.IsRunning ? generation.GetOwner<GenerationResources>() : null;

    private static MachineTranslatorLifecycle CreateMachineLifecycle() => new(
        exception => SafeError("[LLM] Lifecycle failure: " + exception.GetType().Name),
        shutdownTimeout: TranslationBudget.DefaultShutdownTimeout);

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
            completed => SafeError(
                name + " failed: " + completed.Exception?.GetBaseException().GetType().Name),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static PluginLifecycleGate.PluginStage RequireLoadStage(
        PluginLifecycleGate.PluginGeneration generation) =>
        generation.TryEnterStage()
        ?? throw new OperationCanceledException(
            "plugin generation is no longer Loading",
            generation.CancellationToken);

    private static PluginLifecycleGate.PluginStage RequireRunningStage(
        PluginLifecycleGate.PluginGeneration generation) =>
        generation.TryEnterRunningStage()
        ?? throw new OperationCanceledException(
            "plugin generation is no longer Running",
            generation.CancellationToken);

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

    private static void SafeInfo(string message)
    {
        try
        {
            if (Log != null)
                Logger.Info(message);
        }
        catch
        {
        }
    }

    private static void SafeWarn(string message)
    {
        try
        {
            if (Log != null)
                Logger.Warn(message);
        }
        catch
        {
        }
    }

    private static void SafeError(string message)
    {
        try
        {
            if (Log != null)
                Logger.Error(message);
        }
        catch
        {
        }
    }
}

public static class Core
{
    internal static TranslationCache Cache =>
        Plugin.CurrentCache ?? throw new InvalidOperationException("plugin cache is not Running");

    internal static TranslationResolver Resolver =>
        Plugin.CurrentResolver ?? throw new InvalidOperationException("plugin resolver is not Running");

    internal static void BeginEnqueueObservation() => Plugin.BeginEnqueueObservation();

    internal static bool EndEnqueueObservation() => Plugin.EndEnqueueObservation();
}
