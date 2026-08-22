using BepInEx.Configuration;

namespace MuvluvLLMMod;

public static class Config
{
    private static ConfigFile? config;

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

        configFile.SettingChanged += OnSettingChanged;
        Logger.Info("Translation: " + (Translation.Value ? "Enabled" : "Disabled"));
        Logger.Info("[LLM] Enable: " + (LlmEnable.Value ? "Enabled" : "Disabled"));
    }

    public static void Shutdown()
    {
        if (config != null)
            config.SettingChanged -= OnSettingChanged;
        config = null;
    }

    private static void OnSettingChanged(object? sender, SettingChangedEventArgs args)
    {
        var setting = args.ChangedSetting;
        if (ReferenceEquals(setting, LlmApiKey))
            Logger.Info("[LLM] ApiKey changed");
        else
            Logger.Info($"[{setting.Definition.Section}] {setting.Definition.Key} => {setting.BoxedValue}");

        // Display changes reload the worker configuration too, but never disable production.
        Core.ReloadMachineTranslator();
    }
}
