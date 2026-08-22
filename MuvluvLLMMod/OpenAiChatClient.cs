using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MuvluvLLMMod;

public sealed record OpenAiChatSettings(
    string Endpoint,
    string Model,
    string ApiKey,
    int TimeoutSeconds,
    int RetryCount);

public static class EndpointPolicy
{
    public static bool TryValidate(string endpoint, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
        {
            uri = parsed;
            return true;
        }
        return false;
    }
}

public sealed class OpenAiChatClient
{
    private const string SystemPrompt = "Translate Japanese game text into Simplified Chinese. Return only the translation. Tokens such as __MLM_FMT_0__ are protected formatting. Preserve every protected token exactly once and in its original order. Do not translate, remove, duplicate, or move these tokens.";
    private readonly HttpClient client;
    private readonly OpenAiChatSettings settings;
    private readonly RequestRateLimiter limiter;
    private readonly Action<string>? diagnostic;

    public OpenAiChatClient(
        HttpClient client,
        OpenAiChatSettings settings,
        RequestRateLimiter limiter,
        Action<string>? diagnostic = null)
    {
        this.client = client;
        this.settings = settings;
        this.limiter = limiter;
        this.diagnostic = diagnostic;
    }

    public async Task<string?> TranslateAsync(string sourceTemplate, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.Model) || !EndpointPolicy.TryValidate(settings.Endpoint, out var endpoint))
        {
            diagnostic?.Invoke("LLM endpoint or model is invalid; remote plaintext HTTP is not permitted.");
            return null;
        }

        var protectedText = TextTemplate.ProtectForLlm(sourceTemplate);
        var attempts = Math.Max(1, settings.RetryCount);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
                var output = await SendAsync(endpoint, protectedText.Prompt, timeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(output)
                    && TextTemplate.TryRestoreLlm(output, protectedText, out var translated)
                    && TextTemplate.HasSamePlaceholders(sourceTemplate, translated)
                    && TextTemplate.HasSameMarkup(sourceTemplate, translated)
                    && !string.Equals(sourceTemplate, translated, StringComparison.Ordinal)) return translated;
                diagnostic?.Invoke($"LLM response format invalid ({attempt}/{attempts}).");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException
                or OperationCanceledException
                or JsonException
                or KeyNotFoundException
                or InvalidOperationException
                or IndexOutOfRangeException
                or ArgumentOutOfRangeException)
            {
                diagnostic?.Invoke($"LLM request failed ({attempt}/{attempts}): {exception.GetType().Name}");
            }

            if (attempt < attempts) await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), token).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<string?> SendAsync(Uri endpoint, string text, CancellationToken token)
    {
        var body = new
        {
            model = settings.Model,
            temperature = 0,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = text }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        using var response = await limiter.StartAsync(() => client.SendAsync(request, token), token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("LLM endpoint returned a non-success status.");
        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }
}
