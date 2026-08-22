namespace MuvluvLLMMod;

public readonly record struct TranslationRetryDecision(int Failures, TimeSpan Delay, bool Blocked);

public sealed class TranslationRetryPolicy
{
    private sealed record State(int Failures, DateTimeOffset RetryAfter, bool Blocked);

    private readonly object gate = new();
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
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

    public bool CanAttempt(string template)
    {
        lock (gate)
        {
            return CanAttemptUnsafe(template);
        }
    }

    public bool TrySchedule(string template, Func<bool> schedule)
    {
        return TrySchedule(template, schedule, out _);
    }

    public bool TrySchedule(string template, Func<bool> schedule, out bool retryLater)
    {
        lock (gate)
        {
            if (!states.TryGetValue(template, out var state))
            {
                retryLater = false;
                return schedule();
            }
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
        lock (gate)
        {
            return (!states.TryGetValue(template, out var state) || !state.Blocked) && retain();
        }
    }

    public TranslationRetryDecision RecordFailure(string template)
    {
        lock (gate)
        {
            var failures = states.TryGetValue(template, out var previous) ? previous.Failures + 1 : 1;
            var blocked = failures >= maxFailures;
            var delay = blocked ? TimeSpan.Zero : BackoffDelay(failures);
            states[template] = new State(failures, utcNow() + delay, blocked);
            return new TranslationRetryDecision(failures, delay, blocked);
        }
    }

    public void RecordSuccess(string template)
    {
        lock (gate)
        {
            if (states.TryGetValue(template, out var state) && !state.Blocked) states.Remove(template);
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
}
