namespace MuvluvLLMMod;

public static class OpenAiHttpClientFactory
{
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    public static HttpClient CreateClient() => new(CreateHandler(), true);
}
