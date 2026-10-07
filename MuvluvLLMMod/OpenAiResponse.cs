#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MuvluvLLMMod;

internal static class OpenAiResponse
{
    // Buffer the transport response; callers still receive one complete translation.
    internal static string? Read(string body)
    {
        if (body.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(body);
            var choice = Choices(document.RootElement)[0];
            return choice.GetProperty("message").GetProperty("content").GetString();
        }

        var text = new StringBuilder();
        var data = new StringBuilder();
        var complete = false;
        using var reader = new StringReader(body);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0)
            {
                if (ConsumeEvent()) break;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Substring(5);
                if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
                if (data.Length > 0) data.Append('\n');
                data.Append(value);
            }
        }
        ConsumeEvent();
        if (!complete || text.Length == 0)
            throw new JsonException("Incomplete chat completion stream.");
        return text.ToString();

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
    }

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
