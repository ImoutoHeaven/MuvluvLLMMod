using System.Runtime.CompilerServices;

namespace MuvluvLLMMod;

/// <summary>
/// The origin of a TMP assignment observed by the plugin.
/// </summary>
public enum TmpTextAssignmentOrigin
{
    ExternalSetter,
    PluginRefresh
}

/// <summary>
/// A validated identity and assignment generation for one TMP write path.
/// </summary>
public readonly struct TmpTextAssignment
{
    internal TmpTextAssignment(object instance, int instanceId, long generation)
    {
        Instance = instance;
        InstanceId = instanceId;
        Generation = generation;
    }

    public object Instance { get; }
    public int InstanceId { get; }
    public long Generation { get; }
}

/// <summary>
/// Bounded, object-free restore decision state for translations applied to TMP instances.
/// The caller supplies the Unity object as an opaque identity; this type has no Unity dependency.
/// </summary>
public sealed class TmpTranslationProvenance
{
    private sealed class Slot
    {
        public Slot(object instance, int instanceId, long generation)
        {
            Instance = instance;
            InstanceId = instanceId;
            Generation = generation;
        }

        public object Instance { get; }
        public int InstanceId { get; set; }
        public long Generation { get; set; }
        public Entry? Entry { get; set; }
    }

    private sealed class Entry
    {
        public Entry(long generation, string translatedValue, string source)
        {
            Generation = generation;
            TranslatedValue = translatedValue;
            Source = source;
        }

        public long Generation { get; }
        public string TranslatedValue { get; }
        public string Source { get; }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private readonly object gate = new();
    private readonly int capacity;
    private readonly Dictionary<object, LinkedListNode<Slot>> slots =
        new(ReferenceComparer.Instance);
    private readonly LinkedList<Slot> lru = new();
    private int provenanceCount;
    private long nextGeneration;

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
                return provenanceCount;
        }
    }

    /// <summary>
    /// Starts an unguarded setter observation. Every such call is a new assignment, even when
    /// its incoming value is byte-identical to the value recorded for the previous assignment.
    /// </summary>
    public TmpTextAssignment BeginExternalSetter(object instance, int instanceId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (gate)
        {
            var slot = GetOrCreateSlotUnsafe(instance, instanceId);
            ClearEntryUnsafe(slot);
            slot.InstanceId = instanceId;
            slot.Generation = NextGenerationUnsafe();
            TouchUnsafe(slot);
            return ToAssignment(slot);
        }
    }

    /// <summary>
    /// Starts a lookup on the guarded plugin refresh path. It does not create a new content
    /// generation: the lookup must validate against the assignment that is currently displayed.
    /// </summary>
    public TmpTextAssignment BeginPluginRefresh(object instance, int instanceId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (gate)
        {
            var slot = GetOrCreateSlotUnsafe(instance, instanceId);
            if (slot.InstanceId != instanceId)
            {
                ClearEntryUnsafe(slot);
                slot.InstanceId = instanceId;
                slot.Generation = NextGenerationUnsafe();
            }

            TouchUnsafe(slot);
            return ToAssignment(slot);
        }
    }

    /// <summary>
    /// Records a translation only for the still-current identity and generation.
    /// </summary>
    public void Record(TmpTextAssignment assignment, string translatedValue, string source)
    {
        if (string.IsNullOrEmpty(translatedValue)
            || string.IsNullOrEmpty(source)
            || string.Equals(translatedValue, source, StringComparison.Ordinal))
            return;

        lock (gate)
        {
            if (!TryGetCurrentSlotUnsafe(assignment, out var slot))
                return;

            ClearEntryUnsafe(slot);
            slot.Entry = new Entry(assignment.Generation, translatedValue, source);
            provenanceCount++;
            TouchUnsafe(slot);
        }
    }

    /// <summary>
    /// Decides whether the guarded refresh path may restore the current value. Any mismatch,
    /// failed reverse lookup, or lookup exception permanently discards the entry.
    /// </summary>
    public bool TryRestore(
        TmpTextAssignment assignment,
        string currentText,
        TmpTextAssignmentOrigin origin,
        Func<string, string?> sourceLookup,
        out string source)
    {
        ArgumentNullException.ThrowIfNull(sourceLookup);
        source = string.Empty;

        lock (gate)
        {
            if (!TryGetCurrentSlotUnsafe(assignment, out var slot))
                return false;

            if (origin != TmpTextAssignmentOrigin.PluginRefresh)
            {
                // Defensive invalidation keeps this method safe even if a caller forgets to
                // gate restoration. Patch still invalidates before resolving the external call.
                ClearEntryUnsafe(slot);
                return false;
            }

            var entry = slot.Entry;
            if (entry == null || entry.Generation != assignment.Generation)
                return false;

            if (!string.Equals(entry.TranslatedValue, currentText, StringComparison.Ordinal))
            {
                ClearEntryUnsafe(slot);
                return false;
            }

            string? resolvedSource;
            try
            {
                resolvedSource = sourceLookup(entry.TranslatedValue);
            }
            catch
            {
                ClearEntryUnsafe(slot);
                return false;
            }

            if (!string.Equals(resolvedSource, entry.Source, StringComparison.Ordinal))
            {
                ClearEntryUnsafe(slot);
                return false;
            }

            source = entry.Source;
            ClearEntryUnsafe(slot);
            return true;
        }
    }

    private Slot GetOrCreateSlotUnsafe(object instance, int instanceId)
    {
        if (slots.TryGetValue(instance, out var existing))
            return existing.Value;

        while (slots.Count >= capacity)
        {
            var oldest = lru.First!;
            lru.RemoveFirst();
            slots.Remove(oldest.Value.Instance);
            ClearEntryUnsafe(oldest.Value);
        }

        var slot = new Slot(instance, instanceId, NextGenerationUnsafe());
        slots[instance] = lru.AddLast(slot);
        return slot;
    }

    private bool TryGetCurrentSlotUnsafe(
        TmpTextAssignment assignment,
        out Slot slot)
    {
        if (assignment.Instance != null
            && slots.TryGetValue(assignment.Instance, out var node)
            && ReferenceEquals(node.Value.Instance, assignment.Instance)
            && node.Value.InstanceId == assignment.InstanceId
            && node.Value.Generation == assignment.Generation)
        {
            slot = node.Value;
            return true;
        }

        slot = null!;
        return false;
    }

    private TmpTextAssignment ToAssignment(Slot slot) =>
        new(slot.Instance, slot.InstanceId, slot.Generation);

    private long NextGenerationUnsafe() => ++nextGeneration;

    private void TouchUnsafe(Slot slot)
    {
        if (slots.TryGetValue(slot.Instance, out var node))
        {
            lru.Remove(node);
            lru.AddLast(node);
        }
    }

    private void ClearEntryUnsafe(Slot slot)
    {
        if (slot.Entry == null)
            return;

        slot.Entry = null;
        provenanceCount--;
    }
}
