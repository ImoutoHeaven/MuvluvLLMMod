using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class OpenAiChatClientTests
{
    [Theory]
    [InlineData("https://example.test/v1/chat/completions", true)]
    [InlineData("http://127.0.0.1:11434/v1/chat/completions", true)]
    [InlineData("http://localhost:11434/v1/chat/completions", true)]
    [InlineData("http://[::1]:11434/v1/chat/completions", true)]
    [InlineData("http://example.test/v1/chat/completions", false)]
    [InlineData("ftp://example.test/file", false)]
    [InlineData("/relative", false)]
    [InlineData("not a uri", false)]
    public void Endpoint_policy_allows_https_and_loopback_http_only(string endpoint, bool expected)
    {
        Assert.Equal(expected, EndpointPolicy.TryValidate(endpoint, out _));
    }

    [Fact]
    public async Task Request_uses_openai_schema_and_extracts_valid_content()
    {
        string? body = null;
        string? authorization = null;
        var handler = new DelegateHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            authorization = request.Headers.Authorization?.ToString();
            return JsonResponse("技能 __MLM_FMT_0____MLM_FMT_1__");
        });
        using var http = new HttpClient(handler);
        var settings = new OpenAiChatSettings("https://example.test/v1/chat/completions", "qwen-test", "secret-sentinel", 30, 3);
        var diagnostics = new List<string>();
        var client = new OpenAiChatClient(http, settings, new RequestRateLimiter(1000), diagnostics.Add);

        var translated = await client.TranslateAsync("スキル {0}\n", CancellationToken.None);

        Assert.Equal("技能 {0}\n", translated);
        Assert.Equal("Bearer secret-sentinel", authorization);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("qwen-test", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("temperature").GetInt32());
        Assert.False(document.RootElement.GetProperty("stream").GetBoolean());
        var messages = document.RootElement.GetProperty("messages");
        Assert.Contains("Simplified Chinese", messages[0].GetProperty("content").GetString());
        Assert.Equal("スキル __MLM_FMT_0____MLM_FMT_1__", messages[1].GetProperty("content").GetString());
        Assert.DoesNotContain(diagnostics, value => value.Contains("secret-sentinel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_protects_and_restores_literal_escaped_line_breaks()
    {
        string? body = null;
        var handler = new DelegateHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return JsonResponse("第一行__MLM_FMT_0__第二行");
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 1),
            new RequestRateLimiter(1000));

        var translated = await client.TranslateAsync("一行\\n二行", CancellationToken.None);

        Assert.Equal("第一行\\n第二行", translated);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal(
            "一行__MLM_FMT_0__二行",
            document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Request_preserves_literal_escaped_crlf()
    {
        using var http = new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(JsonResponse("第一行__MLM_FMT_0__第二行"))));
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 1),
            new RequestRateLimiter(1000));

        var translated = await client.TranslateAsync("一行\\r\\n二行", CancellationToken.None);

        Assert.Equal("第一行\\r\\n第二行", translated);
    }

    [Fact]
    public async Task Invalid_format_malformed_json_and_status_failures_retry_only_configured_attempts()
    {
        var attempt = 0;
        var handler = new DelegateHandler(_ =>
        {
            attempt++;
            return Task.FromResult(attempt switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") },
                _ => JsonResponse("技能 __MLM_FMT_0__")
            });
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 3),
            new RequestRateLimiter(1000));

        Assert.Null(await client.TranslateAsync("スキル {0}\n", CancellationToken.None));
        Assert.Equal(3, attempt);
    }

    [Fact]
    public async Task Later_valid_response_succeeds_and_cancellation_stops_backoff()
    {
        var attempt = 0;
        var handler = new DelegateHandler(_ => Task.FromResult(++attempt == 1
            ? JsonResponse("missing tokens")
            : JsonResponse("技能 __MLM_FMT_0__")));
        using var http = new HttpClient(handler);
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 3),
            new RequestRateLimiter(1000));
        Assert.Equal("技能 {0}", await client.TranslateAsync("スキル {0}", CancellationToken.None));
        Assert.Equal(2, attempt);

        var cancellationAttempts = 0;
        using var cancelHttp = new HttpClient(new DelegateHandler(_ =>
        {
            cancellationAttempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        var cancelClient = new OpenAiChatClient(
            cancelHttp,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 5),
            new RequestRateLimiter(1000));
        using var source = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelClient.TranslateAsync("スキル", source.Token));
        Assert.Equal(1, cancellationAttempts);
    }

    [Fact]
    public async Task Invalid_endpoint_is_rejected_before_http_handler_runs()
    {
        var called = false;
        using var http = new HttpClient(new DelegateHandler(_ => { called = true; return Task.FromResult(JsonResponse("x")); }));
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("http://example.test/v1/chat/completions", "model", "secret-sentinel", 30, 3),
            new RequestRateLimiter(1000));

        Assert.Null(await client.TranslateAsync("スキル", CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task Missing_openai_response_fields_are_retried_without_faulting()
    {
        var attempts = 0;
        using var http = new HttpClient(new DelegateHandler(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }));
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 2),
            new RequestRateLimiter(1000));

        Assert.Null(await client.TranslateAsync("スキル", CancellationToken.None));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Source_identical_translation_is_rejected()
    {
        var attempts = 0;
        using var http = new HttpClient(new DelegateHandler(_ =>
        {
            attempts++;
            return Task.FromResult(JsonResponse("スキル __MLM_FMT_0__"));
        }));
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 2),
            new RequestRateLimiter(1000));

        Assert.Null(await client.TranslateAsync("スキル {0}", CancellationToken.None));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Unknown_length_response_stops_the_producer_near_the_response_ceiling()
    {
        const int producedTotal = 512 * 1024;
        var content = new CountingChunkedContent(producedTotal);
        using var http = new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 1),
            new RequestRateLimiter(1000));

        Assert.Null(content.Headers.ContentLength);
        Assert.Null(await client.TranslateAsync("スキル", CancellationToken.None));
        Assert.InRange(
            content.ProducedBytes,
            TranslationBudget.MaxResponseBodyBytes,
            TranslationBudget.MaxResponseBodyBytes + 8192);
        Assert.True(content.ProducedBytes < producedTotal);
    }

    [Fact]
    public void Production_http_handler_disables_automatic_redirects()
    {
        using var handler = OpenAiHttpClientFactory.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Redirect_response_is_treated_as_a_non_success_attempt()
    {
        var attempts = 0;
        using var http = new HttpClient(new DelegateHandler(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://example.test/forbidden") }
            });
        }));
        var client = new OpenAiChatClient(
            http,
            new OpenAiChatSettings("https://example.test/v1/chat/completions", "model", string.Empty, 30, 2),
            new RequestRateLimiter(1000));

        Assert.Null(await client.TranslateAsync("スキル", CancellationToken.None));
        Assert.Equal(2, attempts);
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        var json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class CountingChunkedContent : HttpContent
    {
        private readonly int totalBytes;
        private int producedBytes;

        public CountingChunkedContent(int totalBytes) => this.totalBytes = totalBytes;
        public int ProducedBytes => Volatile.Read(ref producedBytes);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[8192];
            var remaining = totalBytes;
            while (remaining > 0)
            {
                var count = Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, count));
                Interlocked.Add(ref producedBytes, count);
                remaining -= count;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CountingReadStream(totalBytes, count => Interlocked.Add(ref producedBytes, count)));
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly int totalBytes;
        private readonly Action<int> produced;
        private int position;

        public CountingReadStream(int totalBytes, Action<int> produced)
        {
            this.totalBytes = totalBytes;
            this.produced = produced;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, totalBytes - position);
            if (read <= 0) return 0;
            Array.Clear(buffer, offset, read);
            position += read;
            produced(read);
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, totalBytes - position);
            if (read <= 0) return ValueTask.FromResult(0);
            buffer[..read].Span.Clear();
            position += read;
            produced(read);
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
