using BepInEx.Configuration;

namespace MuvluvLLMMod;

public static class Config
{
    private static readonly object lifecycleGate = new();
    private static ConfigFile? config;
    private static PluginLifecycleGate.PluginGenerationLease? activeLease;
    private static EventHandler<SettingChangedEventArgs>? activeHandler;

    public static ConfigEntry<bool> Translation { get; private set; } = null!;
    public static ConfigEntry<bool> LlmEnable { get; private set; } = null!;
    public static ConfigEntry<string> LlmEndpoint { get; private set; } = null!;
    public static ConfigEntry<string> LlmModel { get; private set; } = null!;
    public static ConfigEntry<string> LlmApiKey { get; private set; } = null!;
    public static ConfigEntry<int> LlmTimeoutSeconds { get; private set; } = null!;
    public static ConfigEntry<int> LlmRetryCount { get; private set; } = null!;
    public static ConfigEntry<int> LlmRequestsPerSecond { get; private set; } = null!;
    public static ConfigEntry<int> LlmMaxInFlight { get; private set; } = null!;
    public static ConfigEntry<float> LlmTranslatePeriodSeconds { get; private set; } = null!;
    public static ConfigEntry<float> LlmRefreshPeriodSeconds { get; private set; } = null!;
    public static ConfigEntry<string> CacheDirectory { get; private set; } = null!;
    public static ConfigEntry<bool> DebugLogSeenText { get; private set; } = null!;

    public static void Initialize(ConfigFile configFile) => _ = InitializeCore(configFile, null);

    internal static bool Initialize(
        ConfigFile configFile,
        PluginLifecycleGate.PluginGeneration generation) =>
        InitializeCore(configFile, generation);

    internal static int StaticEntryCount
    {
        get
        {
            lock (lifecycleGate)
                return StaticEntryCountUnsafe();
        }
    }

    internal static bool StaticRootsDetached
    {
        get
        {
            lock (lifecycleGate)
                return config == null && StaticEntryCountUnsafe() == 0;
        }
    }

    private static int StaticEntryCountUnsafe() => new ConfigEntryBase?[]
    {
        Translation,
        LlmEnable,
        LlmEndpoint,
        LlmModel,
        LlmApiKey,
        LlmTimeoutSeconds,
        LlmRetryCount,
        LlmRequestsPerSecond,
        LlmMaxInFlight,
        LlmTranslatePeriodSeconds,
        LlmRefreshPeriodSeconds,
        CacheDirectory,
        DebugLogSeenText
    }.Count(entry => entry != null);

    private static void ClearStaticEntriesUnsafe()
    {
        Translation = null!;
        LlmEnable = null!;
        LlmEndpoint = null!;
        LlmModel = null!;
        LlmApiKey = null!;
        LlmTimeoutSeconds = null!;
        LlmRetryCount = null!;
        LlmRequestsPerSecond = null!;
        LlmMaxInFlight = null!;
        LlmTranslatePeriodSeconds = null!;
        LlmRefreshPeriodSeconds = null!;
        CacheDirectory = null!;
        DebugLogSeenText = null!;
    }

    private static bool InitializeCore(
        ConfigFile configFile,
        PluginLifecycleGate.PluginGeneration? generation)
    {
        ArgumentNullException.ThrowIfNull(configFile);
        if (generation?.IsCancellationRequested == true)
            return false;

        // A new generation should never inherit an event handler. This is also safe for a
        // defensive re-initialize from a loader that did not complete the previous rollback.
        Shutdown();

        lock (lifecycleGate)
        {
            if (generation?.IsCancellationRequested == true)
                return false;

            config = configFile;

            Translation = configFile.Bind(
                "Translation",
                "Enable",
                true,
                "是否开启汉化");

            LlmEnable = configFile.Bind(
                "LLM",
                "Enable",
                false,
                "启用LLM翻译回退");
            LlmEndpoint = configFile.Bind(
                "LLM",
                "Endpoint",
                "http://127.0.0.1:11434/v1/chat/completions",
                "OpenAI兼容接口");
            LlmModel = configFile.Bind(
                "LLM",
                "Model",
                "qwen2.5:7b",
                "模型名称");
            LlmApiKey = configFile.Bind(
                "LLM",
                "ApiKey",
                string.Empty,
                "API密钥");
            LlmTimeoutSeconds = configFile.Bind(
                "LLM",
                "TimeoutSeconds",
                30,
                "请求超时秒数");
            LlmRetryCount = configFile.Bind(
                "LLM",
                "RetryCount",
                3,
                "请求尝试总次数");
            LlmRequestsPerSecond = configFile.Bind(
                "LLM",
                "RequestsPerSecond",
                2,
                "每秒请求上限");
            LlmMaxInFlight = configFile.Bind(
                "LLM",
                "MaxInFlight",
                5,
                "并发请求上限");
            LlmTranslatePeriodSeconds = configFile.Bind(
                "LLM",
                "TranslatePeriodSeconds",
                5f,
                "失败任务重试扫描周期");
            LlmRefreshPeriodSeconds = configFile.Bind(
                "LLM",
                "RefreshPeriodSeconds",
                0.5f,
                "TMP刷新周期");
            CacheDirectory = configFile.Bind(
                "LLM",
                "CacheDirectory",
                "MuvluvLLMMod/cache",
                "LLM缓存目录，默认相对于插件目录，也可使用绝对路径");

            DebugLogSeenText = configFile.Bind(
                "Translation.Debug",
                "DebugLogSeenText",
                false,
                "诊断用途：是否记录观察到的文本");
        }

        if (generation?.IsCancellationRequested == true)
        {
            Shutdown();
            return false;
        }

        SafeInfo("Translation: " + (Translation.Value ? "Enabled" : "Disabled"));
        SafeInfo("[LLM] Enable: " + (LlmEnable.Value ? "Enabled" : "Disabled"));
        return true;
    }

    /// <summary>
    /// Subscribes only after the owning generation is fully Running. The lease is captured by
    /// the handler, so an event already dispatched before unsubscribe cannot act on a new load.
    /// </summary>
    public static bool Activate(
        PluginLifecycleGate.PluginGenerationLease lease,
        Action<PluginLifecycleGate.PluginGenerationLease> reload)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(reload);
        lock (lifecycleGate)
        {
            if (config == null || !lease.IsActive || activeLease != null)
                return false;

            var handler = new EventHandler<SettingChangedEventArgs>(
                (sender, args) => OnSettingChanged(lease, reload, sender, args));
            activeLease = lease;
            activeHandler = handler;
            try
            {
                config.SettingChanged += handler;
                return true;
            }
            catch
            {
                activeHandler = null;
                activeLease = null;
                throw;
            }
        }
    }

    public static void Shutdown()
    {
        ConfigFile? oldConfig;
        EventHandler<SettingChangedEventArgs>? oldHandler;
        PluginLifecycleGate.PluginGenerationLease? oldLease;
        lock (lifecycleGate)
        {
            oldConfig = config;
            oldHandler = activeHandler;
            oldLease = activeLease;
            activeHandler = null;
            activeLease = null;
            if (oldConfig != null && oldHandler != null)
                oldConfig.SettingChanged -= oldHandler;
            config = null;
            ClearStaticEntriesUnsafe();
        }

        // Revoking the lease waits for a handler that was already inside the callback. Merely
        // removing the event delegate is not sufficient because the event may already be queued.
        oldLease?.Dispose();
    }

    private static void OnSettingChanged(
        PluginLifecycleGate.PluginGenerationLease lease,
        Action<PluginLifecycleGate.PluginGenerationLease> reload,
        object? sender,
        SettingChangedEventArgs args)
    {
        if (!lease.TryEnter(out var callback))
            return;

        using (callback)
        {
            var setting = args.ChangedSetting;
            // Never send BoxedValue to the generic logger for ApiKey. Definition-based matching
            // also covers a stale event whose ConfigEntry is no longer the current static field.
            SafeInfo(ConfigChangePolicy.FormatLog(
                setting.Definition.Section,
                setting.Definition.Key,
                setting.BoxedValue));

            if (ConfigReloadPolicy.RequiresMachineTranslatorReload(
                    setting.Definition.Section,
                    setting.Definition.Key)
                && lease.IsActive)
            {
                reload(lease);
            }
        }
    }

    private static void SafeInfo(string message)
    {
        try
        {
            if (Plugin.Log != null)
                Logger.Info(message);
        }
        catch
        {
        }
    }
}
