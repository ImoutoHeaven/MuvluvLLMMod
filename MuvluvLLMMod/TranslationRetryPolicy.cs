namespace MuvluvLLMMod;

public readonly record struct TranslationRetryDecision(int Failures, TimeSpan Delay, bool Blocked);

public sealed class TranslationRetryPolicy
{
    private sealed record State(int Failures, DateTimeOffset RetryAfter, bool Blocked);

    private readonly object gate = new();
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private readonly LinkedList<string> stateLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> stateNodes = new(StringComparer.Ordinal);
    private long retainedUtf8Bytes;
    private readonly int maxFailures;
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan maximumDelay;
    private readonly Func<DateTimeOffset> utcNow;

    public TranslationRetryPolicy(
        int maxFailures = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.maxFailures = Math.Max(1, maxFailures);
        this.initialDelay = PositiveOrDefault(initialDelay, TimeSpan.FromSeconds(5));
        this.maximumDelay = PositiveOrDefault(maximumDelay, TimeSpan.FromMinutes(1));
        if (this.maximumDelay < this.initialDelay) this.maximumDelay = this.initialDelay;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public int BlockedCount
    {
        get
        {
            lock (gate) return states.Values.Count(state => state.Blocked);
        }
    }

    public int StateCount
    {
        get { lock (gate) return states.Count; }
    }

    public long RetainedUtf8Bytes
    {
        get { lock (gate) return retainedUtf8Bytes; }
    }

    public bool CanAttempt(string template)
    {
        if (!TranslationBudget.IsTextWithinBudget(template)) return false;
        lock (gate)
        {
            TouchUnsafe(template);
            return CanAttemptUnsafe(template);
        }
    }

    public bool TrySchedule(string template, Func<bool> schedule)
    {
        return TrySchedule(template, schedule, out _);
    }

    public bool TrySchedule(string template, Func<bool> schedule, out bool retryLater)
    {
        if (!TranslationBudget.IsTextWithinBudget(template))
        {
            retryLater = false;
            return false;
        }
        lock (gate)
        {
            if (!states.TryGetValue(template, out var state))
            {
                retryLater = false;
                return schedule();
            }
            TouchUnsafe(template);
            if (state.Blocked)
            {
                retryLater = false;
                return false;
            }
            if (utcNow() < state.RetryAfter)
            {
                retryLater = true;
                return false;
            }
            retryLater = false;
            return schedule();
        }
    }

    public bool TryRetain(string template, Func<bool> retain)
    {
        if (!TranslationBudget.IsTextWithinBudget(template)) return false;
        lock (gate)
        {
            if (states.TryGetValue(template, out var state))
            {
                TouchUnsafe(template);
                if (state.Blocked) return false;
            }
            return retain();
        }
    }

    public TranslationRetryDecision RecordFailure(string template)
    {
        lock (gate)
        {
            var failures = states.TryGetValue(template, out var previous) ? previous.Failures + 1 : 1;
            var blocked = failures >= maxFailures;
            var delay = blocked ? TimeSpan.Zero : BackoffDelay(failures);
            if (TranslationBudget.IsTextWithinBudget(template)
                && TranslationBudget.TryGetTextBytes(template, out var bytes)
                && EnsureCapacityUnsafe(bytes, template))
            {
                states[template] = new State(failures, utcNow() + delay, blocked);
                TouchUnsafe(template);
            }
            return new TranslationRetryDecision(failures, delay, blocked);
        }
    }

    public void RecordSuccess(string template)
    {
        lock (gate)
        {
            if (states.TryGetValue(template, out var state) && !state.Blocked)
                RemoveUnsafe(template);
        }
    }

    private TimeSpan BackoffDelay(int failures)
    {
        var multiplier = Math.Pow(2, failures - 1);
        return TimeSpan.FromMilliseconds(Math.Min(maximumDelay.TotalMilliseconds, initialDelay.TotalMilliseconds * multiplier));
    }

    private static TimeSpan PositiveOrDefault(TimeSpan? value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value.Value : fallback;

    private bool CanAttemptUnsafe(string template) =>
        !states.TryGetValue(template, out var state)
        || (!state.Blocked && utcNow() >= state.RetryAfter);

    private bool EnsureCapacityUnsafe(int bytes, string template)
    {
        if (bytes > TranslationBudget.MaxRetryStateUtf8Bytes)
            return false;
        if (stateNodes.ContainsKey(template))
            return true;
        while (stateNodes.Count >= TranslationBudget.MaxRetryStates
            || retainedUtf8Bytes + bytes > TranslationBudget.MaxRetryStateUtf8Bytes)
        {
            if (stateLru.First == null)
                return false;
            RemoveUnsafe(stateLru.First.Value);
        }
        return true;
    }

    private void TouchUnsafe(string template)
    {
        if (stateNodes.TryGetValue(template, out var node))
        {
            stateLru.Remove(node);
            stateLru.AddLast(node);
            return;
        }

        if (!states.ContainsKey(template)
            || !TranslationBudget.TryGetTextBytes(template, out var bytes)
            || !EnsureCapacityUnsafe(bytes, template))
            return;
        var added = stateLru.AddLast(template);
        stateNodes[template] = added;
        retainedUtf8Bytes += bytes;
    }

    private void RemoveUnsafe(string template)
    {
        states.Remove(template);
        if (stateNodes.Remove(template, out var node))
            stateLru.Remove(node);
        if (TranslationBudget.TryGetTextBytes(template, out var bytes))
            retainedUtf8Bytes = Math.Max(0, retainedUtf8Bytes - bytes);
    }
}
