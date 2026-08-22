namespace MuvluvLLMMod;

public readonly record struct DebugTextLogDecision(
    bool ShouldLog,
    string? Text,
    bool ContainsKana,
    bool DurablyPending,
    bool AcceptedByLiveWorker,
    int SuppressedLines);

/// <summary>
/// Thread-safe bounded deduplication and token-bucket throttling for diagnostic text logs.
/// </summary>
public sealed class DebugTextLogPolicy
{
    public const int DefaultTextLimit = 80;

    private readonly object gate = new();
    private readonly int capacity;
    private readonly double linesPerSecond;
    private readonly TimeSpan summaryInterval;
    private readonly Dictionary<string, LinkedListNode<string>> seen = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = new();
    private DateTimeOffset lastRefill;
    private DateTimeOffset nextSummary;
    private double tokens;
    private int suppressed;

    public DebugTextLogPolicy(
        int capacity = 2048,
        double linesPerSecond = 10,
        TimeSpan? summaryInterval = null,
        DateTimeOffset? startTime = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (linesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(linesPerSecond));

        this.capacity = capacity;
        this.linesPerSecond = linesPerSecond;
        this.summaryInterval = summaryInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : TimeSpan.FromSeconds(1);
        lastRefill = startTime ?? DateTimeOffset.UtcNow;
        nextSummary = lastRefill + this.summaryInterval;
        tokens = linesPerSecond;
    }

    public int SeenCount
    {
        get
        {
            lock (gate)
                return seen.Count;
        }
    }

    public int SuppressedCount
    {
        get
        {
            lock (gate)
                return suppressed;
        }
    }

    public DebugTextLogDecision Observe(
        string? text,
        bool containsKana,
        bool durablyPending,
        bool acceptedByLiveWorker,
        DateTimeOffset now)
    {
        var key = text ?? string.Empty;
        lock (gate)
        {
            RefillUnsafe(now);
            if (seen.TryGetValue(key, out var existing))
            {
                // This is a bounded LRU rather than a process-lifetime HashSet.
                lru.Remove(existing);
                lru.AddLast(existing);
                return new DebugTextLogDecision(
                    false,
                    null,
                    false,
                    false,
                    false,
                    TakeSuppressedSummaryUnsafe(now));
            }

            AddSeenUnsafe(key);
            var summary = TakeSuppressedSummaryUnsafe(now);
            if (tokens < 1)
            {
                suppressed++;
                return new DebugTextLogDecision(
                    false,
                    null,
                    false,
                    false,
                    false,
                    summary);
            }

            tokens--;
            return new DebugTextLogDecision(
                true,
                Truncate(key),
                containsKana,
                durablyPending,
                acceptedByLiveWorker,
                summary);
        }
    }

    public static string Truncate(string text, int limit = DefaultTextLimit)
    {
        if (limit <= 0)
            return string.Empty;
        return text.Length > limit ? text[..limit] : text;
    }

    private void RefillUnsafe(DateTimeOffset now)
    {
        if (now <= lastRefill)
            return;

        var elapsed = (now - lastRefill).TotalSeconds;
        tokens = Math.Min(linesPerSecond, tokens + elapsed * linesPerSecond);
        lastRefill = now;
    }

    private int TakeSuppressedSummaryUnsafe(DateTimeOffset now)
    {
        if (suppressed == 0 || now < nextSummary || tokens < 1)
            return 0;

        var summary = suppressed;
        suppressed = 0;
        tokens--;
        nextSummary = now + summaryInterval;
        return summary;
    }

    private void AddSeenUnsafe(string key)
    {
        var node = lru.AddLast(key);
        seen[key] = node;
        if (seen.Count <= capacity)
            return;

        var oldest = lru.First!;
        lru.RemoveFirst();
        seen.Remove(oldest.Value);
    }
}
