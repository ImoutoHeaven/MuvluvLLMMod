namespace MuvluvLLMMod;

/// <summary>
/// Owns the one-shot token used for a single plugin-authored TMP assignment.
///
/// A token is deliberately thread-local and short-lived: it is registered immediately before
/// the exact <c>text.text = value</c> call, consumed by the first matching setter prefix, and
/// retired by the surrounding scope. It is not a general recursion guard. In particular, a
/// nested setter is not allowed to inherit ownership from a resolver or logging callback.
/// </summary>
public sealed class TmpPluginWriteOwnership
{
    internal sealed class Token
    {
        public Token(
            TmpPluginWriteOwnership owner,
            TmpTextAssignment assignment,
            long lifecycleEpoch,
            Token? previous)
        {
            Owner = owner;
            Assignment = assignment;
            LifecycleEpoch = lifecycleEpoch;
            Previous = previous;
        }

        public readonly TmpPluginWriteOwnership Owner;
        public readonly TmpTextAssignment Assignment;
        public readonly long LifecycleEpoch;
        public readonly Token? Previous;
        public int Consumed;
        public int Invalid;
        public int Disposed;
    }

    [ThreadStatic]
    private static Token? current;

    private readonly TmpTranslationProvenance provenance;
    private long lifecycleEpoch = 1;

    public TmpPluginWriteOwnership(TmpTranslationProvenance provenance)
    {
        this.provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    /// <summary>
    /// Invalidates tokens from the retired plugin generation. Tokens on another thread are not
    /// touched directly; the generation check makes them fail closed on their next prefix hit.
    /// </summary>
    public void ResetForLifecycle()
    {
        Interlocked.Increment(ref lifecycleEpoch);
    }

    /// <summary>
    /// Registers a token for one exact assignment. The returned scope must surround only the
    /// property setter invocation and must be disposed in a finally block.
    /// </summary>
    public TmpPluginWriteScope BeginPluginWrite(TmpTextAssignment assignment)
    {
        var token = new Token(
            this,
            assignment,
            Volatile.Read(ref lifecycleEpoch),
            current);
        current = token;
        return new TmpPluginWriteScope(this, token);
    }

    /// <summary>
    /// Consumes the first prefix hit only when target identity, instance ID, assignment
    /// generation, provenance epoch, and lifecycle epoch all match. Any mismatch invalidates
    /// the token and returns false, so the caller must process that setter as external.
    /// </summary>
    public bool TryConsume(object instance, int instanceId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        PruneDisposed();

        var token = current;
        if (token == null)
            return false;

        if (Volatile.Read(ref token.Consumed) != 0
            || Volatile.Read(ref token.Invalid) != 0
            || !ReferenceEquals(token.Owner, this)
            || token.LifecycleEpoch != Volatile.Read(ref lifecycleEpoch)
            || !ReferenceEquals(token.Assignment.Instance, instance)
            || token.Assignment.InstanceId != instanceId
            || !provenance.IsCurrent(token.Assignment))
        {
            Volatile.Write(ref token.Invalid, 1);
            return false;
        }

        Volatile.Write(ref token.Consumed, 1);
        return true;
    }

    internal int ActiveTokenCount
    {
        get
        {
            PruneDisposed();
            var count = 0;
            for (var token = current; token != null; token = token.Previous)
            {
                if (ReferenceEquals(token.Owner, this)
                    && Volatile.Read(ref token.Disposed) == 0)
                    count++;
            }

            return count;
        }
    }

    private void Dispose(Token token)
    {
        Volatile.Write(ref token.Disposed, 1);
        PruneDisposed();
    }

    private static void PruneDisposed()
    {
        while (current != null && Volatile.Read(ref current.Disposed) != 0)
            current = current.Previous;
    }

    public sealed class TmpPluginWriteScope : IDisposable
    {
        private readonly TmpPluginWriteOwnership owner;
        private readonly Token token;
        private int disposed;

        internal TmpPluginWriteScope(TmpPluginWriteOwnership owner, Token token)
        {
            this.owner = owner;
            this.token = token;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Dispose(token);
        }
    }
}
