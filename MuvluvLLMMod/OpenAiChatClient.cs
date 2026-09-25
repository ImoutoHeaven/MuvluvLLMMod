using System.Buffers;
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
    private sealed class ResponseTooLargeException : InvalidOperationException
    {
    }

    private const string SystemPrompt = "Translate Japanese game text into Simplified Chinese. Return only the translation. Tokens such as __MLM_FMT_0__ are protected formatting. Preserve every protected token exactly once and in its original order. Do not translate, remove, duplicate, or move these tokens.";

    private const string SceneSystemPrompt =
        "Translate Japanese game dialogue into Simplified Chinese. You receive a JSON object with "
        + "version, sceneId, and targets. Return a JSON object with the same version, the same "
        + "sceneId, and a translations array holding one object per target, in the same order, each "
        + "with the target's id and the translated text. Translate every target; keep the speaker's "
        + "tone and terminology consistent across the whole scene. Strings such as __MLM_FMT_0__ are "
        + "protected formatting: preserve each one exactly once, in its original order, and never "
        + "translate, remove, duplicate, or move them. Return only the JSON object.";

    /// <summary>
    /// A scene is a single larger request, so it gets a longer ceiling than one line.
    /// </summary>
    private static readonly TimeSpan SceneTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient client;
    private readonly OpenAiChatSettings settings;
    private readonly RequestRateLimiter limiter;
    private readonly Action<string>? diagnostic;
    private readonly BoundedDiagnostic budgetDiagnostic;

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
        budgetDiagnostic = new BoundedDiagnostic(diagnostic);
    }

    public async Task<string?> TranslateAsync(string sourceTemplate, CancellationToken token)
    {
        return await TranslateSingleAsync(sourceTemplate, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one whole-scene batch. The scene request reuses the same bounded request path as a
    /// single string; only the prompt differs, so rate limiting, timeouts, retry, and the response
    /// body budget all still apply. The caller validates the body against the batch.
    /// </summary>
    public async Task<string?> SendSceneAsync(SceneTranslationBatch batch, CancellationToken token = default)
    {
        if (!TranslationBudget.IsSceneWithinBudget(batch.Prompt))
        {
            budgetDiagnostic.Report(
                "llm-scene-budget",
                "Scene batch exceeded the bounded prompt budget; the scene request was skipped.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.Model)
            || !EndpointPolicy.TryValidate(settings.Endpoint, out var endpoint))
        {
            diagnostic?.Invoke("LLM endpoint or model is invalid; remote plaintext HTTP is not permitted.");
            return null;
        }

        var attempts = Math.Clamp(settings.RetryCount, 1, 8);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(SceneTimeout);
                return await SendAsync(endpoint, batch.Prompt, timeout.Token, SceneSystemPrompt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (ResponseTooLargeException)
            {
                budgetDiagnostic.Report(
                    "llm-scene-response-budget",
                    "Scene response body exceeded the bounded response budget; the response was discarded.");
            }
            catch (Exception exception) when (exception is HttpRequestException
                or OperationCanceledException
                or JsonException
                or KeyNotFoundException
                or InvalidOperationException
                or IndexOutOfRangeException
                or ArgumentOutOfRangeException)
            {
                diagnostic?.Invoke($"LLM scene request failed ({attempt}/{attempts}): {exception.GetType().Name}");
            }

            if (attempt < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), token).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string?> TranslateSingleAsync(string sourceTemplate, CancellationToken token)
    {
        if (!TranslationBudget.IsTextWithinBudget(sourceTemplate))
        {
            budgetDiagnostic.Report(
                "llm-source-budget",
                "LLM source text exceeded the bounded input budget; translation was skipped.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.Model) || !EndpointPolicy.TryValidate(settings.Endpoint, out var endpoint))
        {
            diagnostic?.Invoke("LLM endpoint or model is invalid; remote plaintext HTTP is not permitted.");
            return null;
        }

        var protectedText = TextTemplate.ProtectForLlm(sourceTemplate);
        var attempts = Math.Clamp(settings.RetryCount, 1, 8);
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
            catch (ResponseTooLargeException)
            {
                budgetDiagnostic.Report(
                    "llm-response-budget",
                    "LLM response body exceeded the bounded response budget; the response was discarded.");
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

    private async Task<string> ReadContentBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > TranslationBudget.MaxResponseBodyBytes)
            throw new ResponseTooLargeException();

        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(8192, TranslationBudget.MaxResponseBodyBytes));
        try
        {
            using var result = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, TranslationBudget.MaxResponseBodyBytes)),
                    token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (result.Length + read > TranslationBudget.MaxResponseBodyBytes)
                    throw new ResponseTooLargeException();
                result.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(result.GetBuffer(), 0, checked((int)result.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string?> SendAsync(Uri endpoint, string text, CancellationToken token) =>
        await SendAsync(endpoint, text, token, SystemPrompt).ConfigureAwait(false);

    private async Task<string?> SendAsync(Uri endpoint, string text, CancellationToken token, string systemPrompt)
    {
        var body = new
        {
            model = settings.Model,
            temperature = 0,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
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

        using var response = await limiter.StartAsync(
            () => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
            token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("LLM endpoint returned a non-success status.");
        var json = await ReadContentBoundedAsync(response.Content, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }
}
