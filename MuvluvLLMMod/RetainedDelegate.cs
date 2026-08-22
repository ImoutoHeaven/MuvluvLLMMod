namespace MuvluvLLMMod;

public enum NativeDelegateRegistrationState
{
    Unregistered,
    Registering,
    Registered,
    Removing,
    Failed
}

/// <summary>
/// Owns one converted native delegate and serializes conversion, native add, native remove,
/// and retention as a single state machine. The native operation intentionally runs while the
/// coordinator lock is held: another generation cannot clear or replace the exact delegate
/// between add/remove and publication of the corresponding state.
/// </summary>
public sealed class NativeDelegateCoordinator<T> where T : class
{
    private readonly object gate = new();
    private T? value;
    private long ownerGeneration;
    private NativeDelegateRegistrationState state = NativeDelegateRegistrationState.Unregistered;

    public NativeDelegateRegistrationState State
    {
        get { lock (gate) return state; }
    }

    public T? Current
    {
        get { lock (gate) return value; }
    }

    public long OwnerGeneration
    {
        get { lock (gate) return ownerGeneration; }
    }

    public bool TryRegister(
        long generation,
        Func<T> convert,
        Action<T> add)
    {
        ArgumentNullException.ThrowIfNull(convert);
        ArgumentNullException.ThrowIfNull(add);

        lock (gate)
        {
            if (state == NativeDelegateRegistrationState.Registered)
                return ownerGeneration == generation;
            if (state is NativeDelegateRegistrationState.Registering
                or NativeDelegateRegistrationState.Removing
                or NativeDelegateRegistrationState.Failed)
                return false;

            state = NativeDelegateRegistrationState.Registering;
            ownerGeneration = generation;
            try
            {
                // Conversion and native add are both inside the ownership transition. If add
                // throws, retain the exact converted value and quarantine this coordinator.
                value = convert();
                if (value == null)
                    throw new InvalidOperationException("native delegate conversion returned null");
                add(value);
                state = NativeDelegateRegistrationState.Registered;
                return true;
            }
            catch
            {
                state = NativeDelegateRegistrationState.Failed;
                throw;
            }
        }
    }

    /// <summary>
    /// Removes the exact retained delegate. A false/throwing native removal leaves both the
    /// delegate and Failed state intact; no later generation may register over that uncertainty.
    /// </summary>
    public bool TryRemove(long generation, Func<T, bool> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);

        lock (gate)
        {
            if (state == NativeDelegateRegistrationState.Unregistered)
                return true;
            if (ownerGeneration != generation
                || value == null
                || state is NativeDelegateRegistrationState.Registering
                or NativeDelegateRegistrationState.Removing)
                return false;

            var retained = value;
            state = NativeDelegateRegistrationState.Removing;
            try
            {
                if (!remove(retained))
                {
                    state = NativeDelegateRegistrationState.Failed;
                    return false;
                }

                value = null;
                ownerGeneration = 0;
                state = NativeDelegateRegistrationState.Unregistered;
                return true;
            }
            catch
            {
                state = NativeDelegateRegistrationState.Failed;
                return false;
            }
        }
    }

    /// <summary>
    /// Retries a failed removal with the same generation and exact delegate identity. This is
    /// deliberately separate from registration so a failed cleanup cannot silently re-add.
    /// </summary>
    public bool RetryRemove(long generation, Func<T, bool> remove) => TryRemove(generation, remove);
}

/// <summary>
/// Compatibility holder for loader-free callers that only need sequential retention. Production
/// native registration uses <see cref="NativeDelegateCoordinator{T}"/> above, not this helper.
/// </summary>
public sealed class RetainedDelegate<T> where T : class
{
    private readonly object gate = new();
    private T? value;

    public T? Current
    {
        get
        {
            lock (gate)
                return value;
        }
    }

    public T GetOrCreate(Func<T> convert)
    {
        ArgumentNullException.ThrowIfNull(convert);
        lock (gate)
            return value ??= convert();
    }

    public bool TryRemove(Func<T, bool> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        lock (gate)
        {
            if (value == null)
                return true;

            var retained = value;
            if (!remove(retained))
                return false;

            if (ReferenceEquals(value, retained))
                value = null;
            return true;
        }
    }
}
