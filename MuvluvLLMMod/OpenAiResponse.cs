#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MuvluvLLMMod;

internal static class OpenAiResponse
{
    private static readonly Encoding LaxUtf8 = new UTF8Encoding(false, false);

    // Stream-parse SSE events incrementally and accept recognized JSON envelopes or SSE streams of
    // any size; bound only unrecognized non-JSON/non-SSE streams so malformed garbage fails early.
    internal static async Task<string?> ReadAsync(
        Stream stream,
        int maxUnrecognizedBytes,
        Func<Exception> createTooLargeException,
        CancellationToken cancellationToken = default)
    {
        var byteBuffer = ArrayPool<byte>.Shared.Rent(8192);
        var charBuffer = ArrayPool<char>.Shared.Rent(LaxUtf8.GetMaxCharCount(8192));
        try
        {
            var decoder = LaxUtf8.GetDecoder();
            var mode = 0; // 0 = undecided, 1 = json, 2 = sse
            var sseRecognized = false;
            var unrecognizedBytes = 0;
            StringBuilder? jsonBuilder = null;

            var text = new StringBuilder();
            var data = new StringBuilder();
            var line = new StringBuilder();
            var skipNextLf = false;
            var complete = false;
            var stopReading = false;

            bool ConsumeEvent()
            {
                if (data.Length == 0) return false;
                var payload = data.ToString();
                data.Clear();
                if (payload.Trim() == "[DONE]")
                {
                    complete = true;
                    return true;
                }
                using var document = JsonDocument.Parse(payload);
                foreach (var choice in Choices(document.RootElement).EnumerateArray())
                {
                    if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0)
                        continue;
                    if (choice.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var content)
                        && content.ValueKind != JsonValueKind.Null)
                        text.Append(content.GetString());
                    if (choice.TryGetProperty("finish_reason", out var finish)
                        && finish.ValueKind != JsonValueKind.Null)
                    {
                        if (finish.GetString() != "stop")
                            throw new JsonException("Chat completion did not finish normally.");
                        complete = true;
                    }
                }
                return false;
            }

            bool FinishLine()
            {
                if (line.Length == 0)
                    return ConsumeEvent();

                if (StartsWithDataPrefix(line))
                {
                    sseRecognized = true;
                    var offset = line.Length > 5 && line[5] == ' ' ? 6 : 5;
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(line, offset, line.Length - offset);
                    line.Clear();
                    return false;
                }

                if (!sseRecognized && StartsWithOtherSsePrefix(line))
                    sseRecognized = true;

                line.Clear();
                return false;
            }

            void AccountUnrecognizedByte(int bytes)
            {
                if (sseRecognized)
                    return;
                unrecognizedBytes += bytes;
                if (unrecognizedBytes > maxUnrecognizedBytes)
                    throw createTooLargeException();
            }

            void FeedSseChars(char[] chars, int start, int count)
            {
                var end = start + count;
                for (var i = start; i < end; i++)
                {
                    var ch = chars[i];
                    if (ch == '\r')
                    {
                        AccountUnrecognizedByte(1);
                        skipNextLf = true;
                        if (FinishLine())
                        {
                            stopReading = true;
                            return;
                        }
                        continue;
                    }
                    if (ch == '\n')
                    {
                        AccountUnrecognizedByte(1);
                        if (skipNextLf)
                        {
                            skipNextLf = false;
                            continue;
                        }
                        if (FinishLine())
                        {
                            stopReading = true;
                            return;
                        }
                        continue;
                    }

                    skipNextLf = false;
                    line.Append(ch);
                    if (!sseRecognized)
                    {
                        if (StartsWithRecognizedSsePrefix(line))
                            sseRecognized = true;
                        else
                            AccountUnrecognizedByte(Utf8CodeUnitBytes(ch));
                    }
                }
            }

            while (!stopReading)
            {
                var read = await stream.ReadAsync(byteBuffer.AsMemory(0, 8192), cancellationToken).ConfigureAwait(false);
                var flush = read == 0;
                var decoded = decoder.GetChars(byteBuffer, 0, read, charBuffer, 0, flush);

                if (mode == 0)
                {
                    var idx = 0;
                    while (idx < decoded && (char.IsWhiteSpace(charBuffer[idx]) || charBuffer[idx] == '\uFEFF'))
                    {
                        AccountUnrecognizedByte(Utf8CodeUnitBytes(charBuffer[idx]));
                        idx++;
                    }

                    if (idx == decoded)
                    {
                        if (flush)
                            break;
                        continue;
                    }

                    if (charBuffer[idx] == '{')
                    {
                        mode = 1;
                        jsonBuilder = new StringBuilder();
                        jsonBuilder.Append(charBuffer, idx, decoded - idx);
                    }
                    else
                    {
                        mode = 2;
                        FeedSseChars(charBuffer, idx, decoded - idx);
                    }
                }
                else if (mode == 1)
                {
                    jsonBuilder!.Append(charBuffer, 0, decoded);
                }
                else
                {
                    FeedSseChars(charBuffer, 0, decoded);
                }

                if (flush)
                    break;
            }

            if (mode == 0)
                throw new JsonException("Incomplete chat completion stream.");

            if (mode == 1)
                return ReadJsonCompletion(jsonBuilder!.ToString());

            if (!stopReading && line.Length > 0)
                _ = FinishLine();
            if (!stopReading)
                _ = ConsumeEvent();
            if (!complete || text.Length == 0)
                throw new JsonException("Incomplete chat completion stream.");
            return text.ToString();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer);
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    internal static string? Read(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ReadAsync(
                stream,
                int.MaxValue,
                static () => new InvalidOperationException("Response exceeded ceiling."))
            .GetAwaiter()
            .GetResult();
    }

    private static string? ReadJsonCompletion(string body)
    {
        using var document = JsonDocument.Parse(body);
        var choice = Choices(document.RootElement)[0];
        return choice.GetProperty("message").GetProperty("content").GetString();
    }

    private static bool StartsWithRecognizedSsePrefix(StringBuilder builder) =>
        (builder.Length >= 1 && builder[0] == ':')
        || StartsWithDataPrefix(builder)
        || StartsWithOtherSsePrefix(builder);

    private static bool StartsWithOtherSsePrefix(StringBuilder builder) =>
        StartsWithAscii(builder, "event:")
        || StartsWithAscii(builder, "id:")
        || StartsWithAscii(builder, "retry:");

    private static bool StartsWithDataPrefix(StringBuilder builder) =>
        StartsWithAscii(builder, "data:");

    private static bool StartsWithAscii(StringBuilder builder, string prefix)
    {
        if (builder.Length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (builder[i] != prefix[i])
                return false;
        }
        return true;
    }

    private static int Utf8CodeUnitBytes(char ch) =>
        ch <= 0x7F ? 1 : (ch <= 0x7FF ? 2 : (char.IsSurrogate(ch) ? 2 : 3));

    private static JsonElement Choices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)
            || (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False))
            throw new JsonException("Invalid chat completion response.");
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            return choices;
        // Keep compatibility with one-level gateway envelopes, including {"data": {...}}.
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("choices", out choices)
                && choices.ValueKind == JsonValueKind.Array)
                return choices;
        throw new JsonException("Chat completion choices are missing.");
    }
}
