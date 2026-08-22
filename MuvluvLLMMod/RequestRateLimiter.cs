namespace MuvluvLLMMod;

public sealed class RequestRateLimiter
{
    private readonly object stateGate = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly TimeSpan interval;
    private DateTime nextStart;

    public RequestRateLimiter(int requestsPerSecond, DateTime nextStart = default)
    {
        interval = TimeSpan.FromSeconds(1d / Math.Max(1, requestsPerSecond));
        this.nextStart = nextStart;
    }

    public DateTime NextStart
    {
        get { lock (stateGate) return nextStart; }
    }

    public TimeSpan Reserve(DateTime now)
    {
        lock (stateGate)
        {
            var start = now > nextStart ? now : nextStart;
            nextStart = start + interval;
            return start - now;
        }
    }

    public async Task<T> StartAsync<T>(Func<Task<T>> startRequest, CancellationToken token)
    {
        await startGate.WaitAsync(token).ConfigureAwait(false);
        Task<T> request;
        try
        {
            var delay = Reserve(DateTime.UtcNow);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
            request = startRequest();
        }
        finally
        {
            startGate.Release();
        }
        return await request.ConfigureAwait(false);
    }
}
