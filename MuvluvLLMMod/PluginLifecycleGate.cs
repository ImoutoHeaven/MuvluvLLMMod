namespace MuvluvLLMMod;

/// <summary>
/// One named, best-effort teardown operation. Later operations still run when an earlier one
/// fails so rollback can release every resource that was initialized before a failure.
/// </summary>
public readonly record struct PluginCleanupStep(string Name, Action Action);

/// <summary>
/// Loader-free state and idempotence guard for the plugin's single load/cleanup lifecycle.
/// </summary>
public sealed class PluginLifecycleGate
{
    private int loadStarted;
    private int cleanupStarted;
    private int cleanupSucceeded;

    public bool IsCleaningUp => Volatile.Read(ref cleanupStarted) != 0;

    public bool TryBeginLoad()
    {
        if (Interlocked.CompareExchange(ref loadStarted, 1, 0) != 0)
            return false;

        Volatile.Write(ref cleanupStarted, 0);
        Volatile.Write(ref cleanupSucceeded, 0);
        return true;
    }

    public bool Cleanup(
        IReadOnlyList<PluginCleanupStep> steps,
        Action<string, Exception>? diagnostic = null)
    {
        if (Interlocked.CompareExchange(ref cleanupStarted, 1, 0) != 0)
            return Volatile.Read(ref cleanupSucceeded) != 0;

        var succeeded = true;
        foreach (var step in steps)
        {
            try
            {
                step.Action();
            }
            catch (Exception exception)
            {
                succeeded = false;
                try
                {
                    diagnostic?.Invoke(step.Name, exception);
                }
                catch
                {
                }
            }
        }

        Volatile.Write(ref cleanupSucceeded, succeeded ? 1 : 0);
        Volatile.Write(ref loadStarted, 0);
        return succeeded;
    }
}
