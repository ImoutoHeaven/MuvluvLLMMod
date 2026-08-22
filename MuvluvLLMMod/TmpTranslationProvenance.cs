namespace MuvluvLLMMod;

/// <summary>
/// Bounded, object-free provenance for translations applied to TMP instances.
/// </summary>
public sealed class TmpTranslationProvenance
{
    private sealed class Entry
    {
        public Entry(int instanceId, string translatedValue, string source)
        {
            InstanceId = instanceId;
            TranslatedValue = translatedValue;
            Source = source;
        }

        public int InstanceId { get; }
        public string TranslatedValue { get; }
        public string Source { get; }
    }

    private readonly object gate = new();
    private readonly int capacity;
    private readonly Dictionary<int, LinkedListNode<Entry>> entries = new();
    private readonly LinkedList<Entry> lru = new();

    public TmpTranslationProvenance(int capacity = 2048)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    public void Record(int instanceId, string translatedValue, string source)
    {
        if (string.IsNullOrEmpty(translatedValue) || string.IsNullOrEmpty(source)
            || string.Equals(translatedValue, source, StringComparison.Ordinal))
            return;

        lock (gate)
        {
            if (entries.Remove(instanceId, out var existing))
                lru.Remove(existing);

            var node = lru.AddLast(new Entry(instanceId, translatedValue, source));
            entries[instanceId] = node;
            while (entries.Count > capacity)
            {
                var oldest = lru.First!;
                lru.RemoveFirst();
                entries.Remove(oldest.Value.InstanceId);
            }
        }
    }

    public void InvalidateIfTextChanged(int instanceId, string currentText)
    {
        lock (gate)
        {
            if (entries.TryGetValue(instanceId, out var entry)
                && !string.Equals(entry.Value.TranslatedValue, currentText, StringComparison.Ordinal))
            {
                RemoveUnsafe(instanceId, entry);
            }
        }
    }

    public bool TryRestore(
        int instanceId,
        string currentText,
        Func<string, string?> sourceLookup,
        out string source)
    {
        ArgumentNullException.ThrowIfNull(sourceLookup);
        lock (gate)
        {
            if (!entries.TryGetValue(instanceId, out var entry))
            {
                source = string.Empty;
                return false;
            }

            if (!string.Equals(entry.Value.TranslatedValue, currentText, StringComparison.Ordinal))
            {
                RemoveUnsafe(instanceId, entry);
                source = string.Empty;
                return false;
            }

            var resolvedSource = sourceLookup(entry.Value.TranslatedValue);
            if (!string.Equals(resolvedSource, entry.Value.Source, StringComparison.Ordinal))
            {
                source = string.Empty;
                return false;
            }

            source = entry.Value.Source;
            RemoveUnsafe(instanceId, entry);
            return true;
        }
    }

    private void RemoveUnsafe(int instanceId, LinkedListNode<Entry> entry)
    {
        entries.Remove(instanceId);
        lru.Remove(entry);
    }
}
