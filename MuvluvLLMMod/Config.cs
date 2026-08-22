using BepInEx.Configuration;

namespace MuvluvLLMMod;

public static class Config
{
    private static readonly object lifecycleGate = new();
    private static ConfigFile? config;
    private static PluginLifecycleGate.PluginGenerationLease? activeLease;
    private static Action<PluginLifecycleGate.PluginGenerationLease>? reloadMachineTranslator;

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

    public static void Initialize(ConfigFile configFile)
    {
        ArgumentNullException.ThrowIfNull(configFile);
        // A new generation should never inherit an event handler. This is also safe for a
        // defensive re-initialize from a loader that did not complete the previous rollback.
        Shutdown();

        lock (lifecycleGate)
        {
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

        SafeInfo("Translation: " + (Translation.Value ? "Enabled" : "Disabled"));
        SafeInfo("[LLM] Enable: " + (LlmEnable.Value ? "Enabled" : "Disabled"));
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

            activeLease = lease;
            reloadMachineTranslator = reload;
            config.SettingChanged += OnSettingChanged;
            return true;
        }
    }

    public static void Shutdown()
    {
        ConfigFile? oldConfig;
        PluginLifecycleGate.PluginGenerationLease? oldLease;
        lock (lifecycleGate)
        {
            oldConfig = config;
            oldLease = activeLease;
            activeLease = null;
            reloadMachineTranslator = null;
            if (oldConfig != null)
                oldConfig.SettingChanged -= OnSettingChanged;
            config = null;
        }

        // Revoking the lease waits for a handler that was already inside the callback. Merely
        // removing the event delegate is not sufficient because the event may already be queued.
        oldLease?.Dispose();
    }

    private static void OnSettingChanged(object? sender, SettingChangedEventArgs args)
    {
        PluginLifecycleGate.PluginGenerationLease? lease;
        Action<PluginLifecycleGate.PluginGenerationLease>? reload;
        lock (lifecycleGate)
        {
            lease = activeLease;
            reload = reloadMachineTranslator;
        }

        if (lease == null || reload == null || !lease.TryEnter(out var callback))
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
