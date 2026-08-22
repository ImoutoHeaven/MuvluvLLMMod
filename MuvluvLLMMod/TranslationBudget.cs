using System.Text;

namespace MuvluvLLMMod;

/// <summary>
/// Process-wide limits for text, cache state, scheduling, and shutdown.  These are deliberately
/// fixed so a configuration file or an upstream render callback cannot expand the retained set.
/// UTF-8 payload bytes are used for budgets while UTF-16 code units protect regex and object
/// construction work on the .NET side.
/// </summary>
public static class TranslationBudget
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public const int MaxTextUtf16CodeUnits = 4096;
    public const int MaxTextUtf8Bytes = 16 * 1024;
    public const int MaxResponseBodyBytes = 128 * 1024;

    public const int MaxGeneratedEntries = 4096;
    public const int MaxGeneratedUtf8Bytes = 4 * 1024 * 1024;
    public const int MaxPendingEntries = 2048;
    public const int MaxPendingUtf8Bytes = 512 * 1024;
    public const int MaxRawEntries = 2048;
    public const int MaxRawUtf8Bytes = 512 * 1024;
    public const int MaxReverseEntries = 4096;
    public const int MaxReverseUtf8Bytes = 1024 * 1024;
    public const int MaxProvenanceEntries = 2048;
    public const int MaxProvenanceUtf8Bytes = 512 * 1024;

    public const int MaxRetryStates = 4096;
    public const int MaxRetryStateUtf8Bytes = 1024 * 1024;
    public const int MaxWorkItems = 4096;
    public const int MaxWorkItemUtf8Bytes = 1024 * 1024;
    public const int MaxPriorityBacklogItems = 2048;
    public const int MaxPriorityBacklogUtf8Bytes = 512 * 1024;
    public const int MaxCancellationEntries = 4096;
    public const int MaxCancellationUtf8Bytes = 1024 * 1024;
    public const int MaxFailedProgressEntries = 2048;
    public const int MaxFailedProgressUtf8Bytes = 512 * 1024;
    public const int MaxConcurrentWorkers = 16;
    public const int MaxTransitions = 16;

    public const int MaxCacheFileBytes = 8 * 1024 * 1024;
    public const int MaxCacheSnapshotBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxShutdownTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultTerminalFlushBudget = TimeSpan.FromSeconds(5);

    public static bool TryGetTextBytes(string? text, out int bytes)
    {
        bytes = 0;
        if (text == null || text.Length == 0 || text.Length > MaxTextUtf16CodeUnits)
            return false;

        try
        {
            bytes = StrictUtf8.GetByteCount(text);
            return bytes <= MaxTextUtf8Bytes;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsTextWithinBudget(string? text) => TryGetTextBytes(text, out _);

    public static bool TryGetPairBytes(
        string? first,
        string? second,
        out int bytes)
    {
        bytes = 0;
        if (!TryGetTextBytes(first, out var firstBytes)
            || !TryGetTextBytes(second, out var secondBytes))
            return false;

        bytes = firstBytes + secondBytes;
        return bytes >= firstBytes && bytes >= secondBytes;
    }

    public static int ClampMaxInFlight(int value) =>
        Math.Clamp(value, 1, MaxConcurrentWorkers);
}

/// <summary>
/// Emits a fixed-key diagnostic at most once per interval.  It never retains caller text, so a
/// stream of distinct over-budget strings cannot turn the diagnostic path into another cache.
/// </summary>
public sealed class BoundedDiagnostic
{
    private readonly object gate = new();
    private readonly Action<string>? sink;
    private readonly TimeSpan interval;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Dictionary<string, DateTimeOffset> nextAllowed = new(StringComparer.Ordinal);
    private DateTimeOffset lastPrune;

    public BoundedDiagnostic(
        Action<string>? sink,
        TimeSpan? interval = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.sink = sink;
        this.interval = interval is { } value && value > TimeSpan.Zero
            ? value
            : TimeSpan.FromSeconds(1);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        lastPrune = this.utcNow();
    }

    public void Report(string code, string message)
    {
        if (sink == null || string.IsNullOrEmpty(code))
            return;

        var now = utcNow();
        lock (gate)
        {
            if (now - lastPrune > TimeSpan.FromMinutes(1))
            {
                foreach (var key in nextAllowed.Keys.ToArray())
                {
                    if (nextAllowed[key] <= now)
                        nextAllowed.Remove(key);
                }
                lastPrune = now;
            }

            if (nextAllowed.TryGetValue(code, out var allowed) && now < allowed)
                return;
            if (!nextAllowed.ContainsKey(code)
                && nextAllowed.Count >= 32)
                return;
            nextAllowed[code] = now + interval;
        }

        try
        {
            sink(message);
        }
        catch
        {
        }
    }
}
