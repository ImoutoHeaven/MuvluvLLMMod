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
        public Harmony? PendingHarmony { get; set; }
        public Hotkey? Hotkey { get; set; }
        public CancellationTokenSource? PersistenceCancellation { get; set; }
        public Task PersistenceTask { get; set; } = Task.CompletedTask;
        public PluginLifecycleGate.PluginGenerationLease? ConfigLease { get; set; }

        public PluginLifecycleGate.PluginGenerationResource? ConfigurationResource { get; set; }
        public PluginLifecycleGate.PluginGenerationResource? MachineOwnerResource { get; set; }
        public PluginLifecycleGate.PluginGenerationResource? CacheResource { get; set; }
        public PluginLifecycleGate.PluginGenerationResource? HarmonyResource { get; set; }
        public PluginLifecycleGate.PluginGenerationResource? HotkeyResource { get; set; }
        public PluginLifecycleGate.PluginGenerationResource? QuittingResource { get; set; }
        public PluginLifecycleGate.PluginGenerationResource? PersistenceResource { get; set; }
        public PluginLifecycleGate.PluginGenerationResource? MachineStartupResource { get; set; }
        public PluginLifecycleGate.PluginGenerationResource? ActivationResource { get; set; }

        public void Detach()
        {
            Cache = null;
            Resolver = null;
            MachineLifecycle = null;
            Harmony = null;
            PendingHarmony = null;
            Hotkey = null;
            PersistenceCancellation = null;
            PersistenceTask = Task.CompletedTask;
            ConfigLease = null;
            ConfigurationResource = null;
            MachineOwnerResource = null;
            CacheResource = null;
            HarmonyResource = null;
            HotkeyResource = null;
            QuittingResource = null;
            PersistenceResource = null;
            MachineStartupResource = null;
            ActivationResource = null;
        }
    }

    private sealed class PersistenceHandle
    {
        public CancellationTokenSource? Cancellation { get; set; }
        public Task Task { get; set; } = Task.CompletedTask;
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
    internal static PluginLifecycleGate.PluginGeneration? CurrentGeneration =>
        lifecycleGate.CurrentGeneration;
    internal static object? CurrentGenerationOwner =>
        lifecycleGate.CurrentGeneration?.GetOwner<object>();
    internal static PluginLifecycleState CurrentGenerationState => lifecycleGate.State;

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

            using (var stage = RequireLoadStage(generation))
            {
                // Precheck before any configuration side effect or resource allocation: a game update
                // that moved a patch seam must abort the generation with nothing to roll back.
                var preflight = Patch.Preflight();
                if (!preflight.Ok)
                {
                    foreach (var failure in preflight.Failures)
                        SafeError("[LLM] precheck " + failure);

                    throw new HarmonyPatchPreflightException(preflight.Failures);
                }

                using var configurationResource = stage.RegisterResource(
                    "configuration initialization",
                    () => MuvluvLLMMod.Config.Shutdown(),
                    () => resources.ActivationResource?.IsPendingUnsafe == true);
                resources.ConfigurationResource = configurationResource;

                TrySetUtf8Console();
                Logger.Info($"Plugin {PluginGuid} is loading (generation {generation.Id})");
                if (!MuvluvLLMMod.Config.Initialize(base.Config, generation))
                    throw CanceledGeneration(generation, "configuration initialization");
                if (!configurationResource.Commit())
                    throw CanceledGeneration(generation, "configuration initialization");

                var machineLifecycle = CreateMachineLifecycle(generation);
                using var machineOwnerResource = stage.RegisterResource(
                    "machine lifecycle owner",
                    () =>
                    {
                        if (ReferenceEquals(resources.MachineLifecycle, machineLifecycle))
                            resources.MachineLifecycle = null;
                    });
                resources.MachineOwnerResource = machineOwnerResource;
                if (!machineOwnerResource.Commit(
                        () => resources.MachineLifecycle = machineLifecycle))
                {
                    throw CanceledGeneration(generation, "machine lifecycle owner");
                }
            }

            using (var stage = RequireLoadStage(generation))
            {
                TranslationCache? cache = null;
                using var cacheResource = stage.RegisterResource(
                    "translation cache",
                    () => cache?.FreezeMutations());
                resources.CacheResource = cacheResource;

                cache = new TranslationCache(
                    ResolvePluginPath(MuvluvLLMMod.Config.CacheDirectory.Value),
                    message => SafeWarn("Translation cache " + message));
                cache.Load();
                var resolver = new TranslationResolver(
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
                if (!cacheResource.Commit(
                        () =>
                        {
                            resources.Cache = cache;
                            resources.Resolver = resolver;
                        }))
                {
                    throw CanceledGeneration(generation, "translation cache");
                }
            }

            using (var stage = RequireLoadStage(generation))
            {
                var harmony = new Harmony(PluginGuid);
                using var harmonyResource = stage.RegisterResource(
                    "Harmony patches",
                    () =>
                    {
                        // Retire before and after the external unpatch. PatchAll may have
                        // published hooks on another thread immediately before this disposer
                        // runs; either ordering must leave the static body inert.
                        Patch.Retire();
                        try
                        {
                            harmony.UnpatchSelf();
                        }
                        finally
                        {
                            Patch.Retire();
                            if (ReferenceEquals(resources.Harmony, harmony))
                                resources.Harmony = null;
                            if (ReferenceEquals(resources.PendingHarmony, harmony))
                                resources.PendingHarmony = null;
                        }
                    });
                resources.PendingHarmony = harmony;
                resources.HarmonyResource = harmonyResource;
                Patch.Initialize(harmony);
                if (!harmonyResource.Commit(
                        () =>
                        {
                            resources.Harmony = harmony;
                            resources.PendingHarmony = null;
                        }))
                    throw CanceledGeneration(generation, "Harmony patches");
            }

            using (var stage = RequireLoadStage(generation))
            {
                Hotkey? hotkey = null;
                using var hotkeyResource = stage.RegisterResource(
                    "injected Hotkey component",
                    () => DestroyHotkey(hotkey, resources));
                resources.HotkeyResource = hotkeyResource;
                hotkey = AddComponent<Hotkey>();
                if (hotkey == null)
                    throw new InvalidOperationException("Hotkey component injection returned null");
                if (!hotkeyResource.Commit(() => resources.Hotkey = hotkey))
                    throw CanceledGeneration(generation, "injected Hotkey component");
            }

            using (var stage = RequireLoadStage(generation))
            {
                using var quittingResource = stage.RegisterResource(
                    "application-quit delegate",
                    () => RemoveApplicationQuittingHandler(generation.Id));
                resources.QuittingResource = quittingResource;
                RegisterApplicationQuittingHandler(generation.Id);
                if (!quittingResource.Commit())
                    throw CanceledGeneration(generation, "application-quit delegate");
            }

            using (var stage = RequireLoadStage(generation))
            {
                var cache = resources.Cache
                    ?? throw new InvalidOperationException("cache was not initialized");
                var persistence = new PersistenceHandle
                {
                    Cancellation = new CancellationTokenSource()
                };
                using var persistenceResource = stage.RegisterResource(
                    "cache persistence task",
                    () => StopPersistence(persistence));
                resources.PersistenceResource = persistenceResource;
                persistence.Task = cache.RunPersistenceLoopAsync(
                    persistence.Cancellation.Token);
                ObserveBackgroundTask(persistence.Task, "cache persistence");
                if (!persistenceResource.Commit(
                        () =>
                        {
                            resources.PersistenceCancellation = persistence.Cancellation;
                            resources.PersistenceTask = persistence.Task;
                        }))
                {
                    throw CanceledGeneration(generation, "cache persistence task");
                }
            }

            using (var stage = RequireLoadStage(generation))
            {
                var settings = CaptureMachineSettings();
                var retryPolicy = new TranslationRetryPolicy();
                var machineLifecycle = resources.MachineLifecycle
                    ?? throw new InvalidOperationException("machine lifecycle was not initialized");
                using var machineStartupResource = stage.RegisterResource(
                    "machine worker startup",
                    machineLifecycle.ShutdownForResourceRollback);
                resources.MachineStartupResource = machineStartupResource;
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
                if (!machineStartupResource.Commit())
                    throw CanceledGeneration(generation, "machine worker startup");
            }

            if (!lifecycleGate.TryPublishRunning(generation))
                throw CanceledGeneration(generation, "generation activation");

            using (var stage = RequireRunningStage(generation))
            {
                PluginLifecycleGate.PluginGenerationLease? lease = null;
                using var activationResource = stage.RegisterResource(
                    "runtime/config activation",
                    () =>
                    {
                        // Config.Activate can publish its event handler before returning. The
                        // ordinary configuration rollback may already have run while this
                        // stage was blocked, so late rollback must revoke activation itself.
                        try
                        {
                            MuvluvLLMMod.Config.Shutdown();
                        }
                        finally
                        {
                            lease?.Dispose();
                            Patch.Retire();
                            if (ReferenceEquals(resources.ConfigLease, lease))
                                resources.ConfigLease = null;
                        }
                    });
                resources.ActivationResource = activationResource;
                Patch.Activate();
                lease = generation.TryAcquireRunningLease()
                    ?? throw CanceledGeneration(generation, "configuration lease");
                if (!MuvluvLLMMod.Config.Activate(lease, ReloadMachineTranslator))
                    throw new InvalidOperationException("configuration activation was rejected");
                if (!activationResource.Commit(() => resources.ConfigLease = lease))
                    throw CanceledGeneration(generation, "runtime/config activation");
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

        // Stop event producers first. Resource requests are deliberately non-blocking while an
        // admitted external boundary is still pending; that boundary will commit-or-rollback
        // its local resource when it finally returns.
        RunCleanupResource(
            "shutdown configuration",
            resources?.ConfigurationResource,
            MuvluvLLMMod.Config.Shutdown,
            ref succeeded,
            runFallbackWhenPending: true);
        RunCleanupResource(
            "remove application-quit handler",
            resources?.QuittingResource,
            () => RemoveApplicationQuittingHandler(generation.Id),
            ref succeeded);
        RunCleanupResource(
            "disable Hotkey",
            resources?.HotkeyResource,
            () => DestroyHotkey(resources?.Hotkey, resources),
            ref succeeded);
        RunCleanupResource(
            "retire runtime/config activation",
            resources?.ActivationResource,
            static () => Patch.Retire(),
            ref succeeded);
        RunCleanupStep("retire TMP translation state", Patch.Retire, ref succeeded);

        // Keep this semantic order: config stop -> patch/component retirement -> freeze ->
        // machine stop -> persistence cancellation -> terminal flush -> unpatch. Each resource
        // callback is idempotent and remains retained if a late boundary has not returned.
        RunCleanupResource(
            "freeze cache mutations",
            resources?.CacheResource,
            () => resources?.Cache?.FreezeMutations(),
            ref succeeded);
        RunCleanupResource(
            "shutdown machine translator",
            resources?.MachineStartupResource,
            () => resources?.MachineLifecycle?.Shutdown(),
            ref succeeded);
        RunCleanupResource(
            "cancel cache persistence",
            resources?.PersistenceResource,
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
        RunCleanupResource(
            "unpatch Harmony",
            resources?.HarmonyResource,
            () => UnpatchHarmony(resources),
            ref succeeded,
            runFallbackWhenPending: true);

        if (succeeded)
            SafeInfo($"Plugin {PluginGuid} unloaded (generation {generation.Id})");
        else
            SafeError($"Plugin {PluginGuid} generation {generation.Id} is quarantined after cleanup errors");

        resources?.Detach();
        Log = null!;
        return succeeded;
    }

    private static void RunCleanupResource(
        string name,
        PluginLifecycleGate.PluginGenerationResource? resource,
        Action fallback,
        ref bool succeeded,
        bool runFallbackWhenPending = false)
    {
        if (resource != null)
        {
            var pending = resource.IsPending;
            if (!resource.RequestRollback())
                succeeded = false;
            if (runFallbackWhenPending && pending)
                RunCleanupStep(name + " pending boundary", fallback, ref succeeded);
            return;
        }

        RunCleanupStep(name, fallback, ref succeeded);
    }

    private static void UnpatchHarmony(GenerationResources? resources)
    {
        var harmony = resources?.PendingHarmony ?? resources?.Harmony;
        if (harmony == null)
            return;
        try
        {
            harmony.UnpatchSelf();
        }
        finally
        {
            if (ReferenceEquals(resources?.PendingHarmony, harmony))
                resources.PendingHarmony = null;
            if (ReferenceEquals(resources?.Harmony, harmony))
                resources.Harmony = null;
        }
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

        var handle = new PersistenceHandle
        {
            Cancellation = resources.PersistenceCancellation,
            Task = resources.PersistenceTask
        };
        resources.PersistenceCancellation = null;
        StopPersistence(handle);
    }

    private static void StopPersistence(PersistenceHandle handle)
    {
        var cancellation = handle.Cancellation;
        if (cancellation == null)
            return;

        // Clear the handle before waiting. A late retry then observes that the continuation owns
        // disposal after a timeout instead of disposing a CTS from underneath its task.
        handle.Cancellation = null;
        var persistenceTask = handle.Task;
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

    private static MachineTranslatorLifecycle CreateMachineLifecycle(
        PluginLifecycleGate.PluginGeneration generation) => new(
        exception => SafeError("[LLM] Lifecycle failure: " + exception.GetType().Name),
        shutdownTimeout: TranslationBudget.DefaultShutdownTimeout,
        terminalFailure: exception =>
        {
            SafeError("[LLM] Machine translator generation faulted: " + exception.GetType().Name);
            lifecycleGate.RecordFailure(generation, exception);
        });

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
        ?? throw CanceledGeneration(generation, "load stage admission");

    private static PluginLifecycleGate.PluginStage RequireRunningStage(
        PluginLifecycleGate.PluginGeneration generation) =>
        generation.TryEnterRunningStage()
        ?? throw CanceledGeneration(generation, "running stage admission");

    private static OperationCanceledException CanceledGeneration(
        PluginLifecycleGate.PluginGeneration generation,
        string operation) => new(
        "plugin generation was canceled during " + operation,
        generation.CancellationToken);

    private static void DestroyHotkey(Hotkey? hotkey, GenerationResources? resources)
    {
        if (hotkey == null)
            return;
        hotkey.enabled = false;
        UnityEngine.Object.Destroy(hotkey);
        if (ReferenceEquals(resources?.Hotkey, hotkey))
            resources.Hotkey = null;
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
