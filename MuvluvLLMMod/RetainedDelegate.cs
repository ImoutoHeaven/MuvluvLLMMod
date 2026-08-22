namespace MuvluvLLMMod;

/// <summary>
/// Retains one converted delegate and clears it only after the caller confirms removal succeeded.
/// This keeps interop delegate identity stable across add/remove without depending on IL2CPP.
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
        T? retained;
        lock (gate)
            retained = value;
        if (retained == null)
            return true;

        // A thrown callback is also a failed removal: leave the reference retained so a later
        // cleanup attempt can retry the exact same delegate.
        if (!remove(retained))
            return false;

        lock (gate)
        {
            if (ReferenceEquals(value, retained))
                value = null;
        }
        return true;
    }
}
