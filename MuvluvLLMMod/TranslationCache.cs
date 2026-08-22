using System.Text;
using System.Text.Json;

namespace MuvluvLLMMod;

public sealed class TranslationCache
{
    public readonly record struct PendingObservation(bool ShouldEnqueue, long Generation);

    private const int RawSampleCapacity = 4096;
    private const int RuntimeReverseIndexCapacity = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly object writerGate = new();
    private readonly SemaphoreSlim dirtySignal = new(0);
    private readonly Action<string>? diagnostic;
    private readonly Func<string, string, bool>? writeAtomic;
    private readonly TimeSpan persistenceRetryDelay;
    private Dictionary<string, string> generated = new(StringComparer.Ordinal);
    private readonly List<string> pending = new();
    private readonly HashSet<string> pendingSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pendingGenerations = new(StringComparer.Ordinal);
    private readonly HashSet<string> raw = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sourceByTranslatedValue = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownTranslatedValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguousTranslatedValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>> reverseIndexNodes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> reverseIndexLru = new();
    private readonly HashSet<string> runtimePromotedPending = new(StringComparer.Ordinal);
    private bool dirty;
    private bool acceptingMutations = true;
    private long mutationVersion;
    private long nextPendingGeneration;

    public TranslationCache(
        string root,
        Action<string>? diagnostic = null,
        Func<string, string, bool>? writeAtomic = null,
        TimeSpan? persistenceRetryDelay = null)
    {
        Root = root;
        this.diagnostic = diagnostic;
        this.writeAtomic = writeAtomic;
        this.persistenceRetryDelay = persistenceRetryDelay > TimeSpan.Zero
            ? persistenceRetryDelay.Value
            : TimeSpan.FromMilliseconds(250);
        GeneratedPath = Path.Combine(root, "generated.zh_Hans.json");
        PendingPath = Path.Combine(root, "pending.zh_Hans.json");
        RawPath = Path.Combine(root, "dump", "ui_raw.json");
    }

    public string Root { get; }
    public string GeneratedPath { get; }
    public string PendingPath { get; }
    public string RawPath { get; }

    public bool TryGetGenerated(string normalizedTemplate, out string translatedTemplate)
    {
        lock (gate) return generated.TryGetValue(normalizedTemplate, out translatedTemplate!);
    }

    public bool ObservePriority(string original, string normalizedTemplate) => Observe(original, normalizedTemplate, true);
    public bool ObserveNormal(string original, string normalizedTemplate) => Observe(original, normalizedTemplate, false);
    public PendingObservation ObservePriorityGeneration(string original, string normalizedTemplate) =>
        ObserveGeneration(original, normalizedTemplate, true);
    public PendingObservation ObserveNormalGeneration(string original, string normalizedTemplate) =>
        ObserveGeneration(original, normalizedTemplate, false);

    public bool StoreGenerated(string normalizedTemplate, string translatedTemplate)
    {
        lock (gate)
        {
            if (!acceptingMutations) return false;
            generated[normalizedTemplate] = translatedTemplate;
            RememberResolutionUnsafe(normalizedTemplate, translatedTemplate);
            if (pendingSet.Remove(normalizedTemplate)) pending.RemoveAll(value => string.Equals(value, normalizedTemplate, StringComparison.Ordinal));
            pendingGenerations.Remove(normalizedTemplate);
            runtimePromotedPending.Remove(normalizedTemplate);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public bool StoreGeneratedIfPending(string normalizedTemplate, long pendingGeneration, string translatedTemplate)
    {
        lock (gate)
        {
            if (!acceptingMutations
                || !pendingGenerations.TryGetValue(normalizedTemplate, out var currentGeneration)
                || currentGeneration != pendingGeneration)
                return false;
            generated[normalizedTemplate] = translatedTemplate;
            RememberResolutionUnsafe(normalizedTemplate, translatedTemplate);
            pendingSet.Remove(normalizedTemplate);
            pendingGenerations.Remove(normalizedTemplate);
            pending.RemoveAll(value => string.Equals(value, normalizedTemplate, StringComparison.Ordinal));
            runtimePromotedPending.Remove(normalizedTemplate);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public void RemoveGenerated(string normalizedTemplate)
    {
        lock (gate)
        {
            if (!acceptingMutations) return;
            if (generated.Remove(normalizedTemplate)) MarkDirtyUnsafe();
        }
    }

    public bool CancelPending(string normalizedTemplate)
    {
        return CancelPending(normalizedTemplate, out _);
    }

    public bool CancelPending(string normalizedTemplate, out long pendingGeneration)
    {
        lock (gate)
        {
            if (!acceptingMutations
                || !pendingSet.Remove(normalizedTemplate)
                || !pendingGenerations.Remove(normalizedTemplate, out pendingGeneration))
            {
                pendingGeneration = 0;
                return false;
            }
            pending.RemoveAll(value => string.Equals(value, normalizedTemplate, StringComparison.Ordinal));
            runtimePromotedPending.Remove(normalizedTemplate);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public void RememberResolution(string source, string translatedValue)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translatedValue)) return;
        lock (gate)
        {
            if (acceptingMutations) RememberResolutionUnsafe(source, translatedValue);
        }
    }

    public bool IsKnownTranslatedValue(string value)
    {
        lock (gate)
        {
            if (!knownTranslatedValues.Contains(value)) return false;
            TouchReverseIndexUnsafe(value);
            return true;
        }
    }

    public bool TryGetSourceForTranslatedValue(string translatedValue, out string source)
    {
        lock (gate)
        {
            if (!sourceByTranslatedValue.TryGetValue(translatedValue, out source!)) return false;
            TouchReverseIndexUnsafe(translatedValue);
            return true;
        }
    }

    public void ConfirmSourceIdentity(string source)
    {
        if (string.IsNullOrEmpty(source)) return;
        lock (gate)
        {
            if (!acceptingMutations) return;
            if (!sourceByTranslatedValue.Remove(source)) return;
            knownTranslatedValues.Add(source);
            ambiguousTranslatedValues.Add(source);
            TouchReverseIndexUnsafe(source);
        }
    }

    public IReadOnlyList<string> PendingSnapshot()
    {
        lock (gate) return pending.ToArray();
    }

    public bool TryGetPendingGeneration(string normalizedTemplate, out long pendingGeneration)
    {
        lock (gate) return pendingGenerations.TryGetValue(normalizedTemplate, out pendingGeneration);
    }

    public bool TryIfPendingGeneration(string normalizedTemplate, long pendingGeneration, Action action)
    {
        lock (gate)
        {
            if (!acceptingMutations
                || !pendingGenerations.TryGetValue(normalizedTemplate, out var currentGeneration)
                || currentGeneration != pendingGeneration)
                return false;
            action();
            return true;
        }
    }

    public int GeneratedCount
    {
        get { lock (gate) return generated.Count; }
    }

    public void FreezeMutations()
    {
        lock (gate) acceptingMutations = false;
    }

    public void Load()
    {
        var loadedGenerated = LoadGenerated(out var cleaned);
        var loadedPending = LoadPending();
        var loadedRaw = LoadRaw();
        lock (gate)
        {
            generated = loadedGenerated;
            pending.Clear();
            pendingSet.Clear();
            pendingGenerations.Clear();
            nextPendingGeneration = 0;
            foreach (var template in loadedPending)
            {
                if (!TextTemplate.IsTranslationCandidate(template) || generated.ContainsKey(template) || !pendingSet.Add(template))
                {
                    cleaned = true;
                    continue;
                }
                pending.Add(template);
                pendingGenerations[template] = ++nextPendingGeneration;
            }
            raw.Clear();
            foreach (var value in loadedRaw)
            {
                if (!TextTemplate.IsTranslationCandidate(value))
                {
                    cleaned = true;
                    continue;
                }
                var normalized = TextTemplate.Normalize(value).Template;
                if (!string.Equals(value, normalized, StringComparison.Ordinal)
                    || raw.Count >= RawSampleCapacity)
                    cleaned = true;
                if (raw.Count < RawSampleCapacity) raw.Add(normalized);
            }
            sourceByTranslatedValue.Clear();
            knownTranslatedValues.Clear();
            ambiguousTranslatedValues.Clear();
            reverseIndexNodes.Clear();
            reverseIndexLru.Clear();
            runtimePromotedPending.Clear();
            foreach (var entry in generated) RememberResolutionUnsafe(entry.Key, entry.Value);
            if (cleaned) MarkDirtyUnsafe();
        }
    }

    public async Task RunPersistenceLoopAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(1, token).ConfigureAwait(false);
            while (true)
            {
                await dirtySignal.WaitAsync(token).ConfigureAwait(false);
                Flush();
                bool retry;
                lock (gate) retry = dirty;
                if (retry) await Task.Delay(persistenceRetryDelay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    public void Flush()
    {
        lock (writerGate)
        {
            Dictionary<string, string> generatedSnapshot;
            string[] pendingSnapshot;
            Dictionary<string, string> rawSnapshot;
            long snapshotVersion;
            lock (gate)
            {
                if (!dirty) return;
                generatedSnapshot = new Dictionary<string, string>(generated, StringComparer.Ordinal);
                pendingSnapshot = pending.ToArray();
                rawSnapshot = raw.ToDictionary(value => value, _ => string.Empty, StringComparer.Ordinal);
                snapshotVersion = mutationVersion;
            }

            var succeeded = TryWriteAtomic(GeneratedPath, JsonSerializer.Serialize(generatedSnapshot, JsonOptions))
                && TryWriteAtomic(PendingPath, JsonSerializer.Serialize(pendingSnapshot, JsonOptions))
                && TryWriteAtomic(RawPath, JsonSerializer.Serialize(rawSnapshot, JsonOptions));
            lock (gate)
            {
                if (succeeded && mutationVersion == snapshotVersion) dirty = false;
                else SignalDirtyUnsafe();
            }
        }
    }

    private bool Observe(string original, string normalizedTemplate, bool priority) =>
        ObserveGeneration(original, normalizedTemplate, priority).ShouldEnqueue;

    private PendingObservation ObserveGeneration(string original, string normalizedTemplate, bool priority)
    {
        if (!TextTemplate.IsTranslationCandidate(original) || !TextTemplate.IsTranslationCandidate(normalizedTemplate))
            return default;
        lock (gate)
        {
            if (!acceptingMutations) return default;
            var rawSample = TextTemplate.Normalize(original).Template;
            var changed = raw.Count < RawSampleCapacity && raw.Add(rawSample);
            if (generated.ContainsKey(normalizedTemplate))
            {
                if (changed) MarkDirtyUnsafe();
                return default;
            }
            var addedPending = false;
            if (pendingSet.Add(normalizedTemplate))
            {
                pending.Add(normalizedTemplate);
                pendingGenerations[normalizedTemplate] = ++nextPendingGeneration;
                changed = true;
                addedPending = true;
            }
            var promote = priority && runtimePromotedPending.Add(normalizedTemplate);
            if (changed) MarkDirtyUnsafe();
            return new PendingObservation(
                priority ? promote : addedPending,
                pendingGenerations[normalizedTemplate]);
        }
    }

    private void MarkDirtyUnsafe()
    {
        mutationVersion++;
        if (dirty) return;
        dirty = true;
        dirtySignal.Release();
    }

    private void SignalDirtyUnsafe()
    {
        if (dirtySignal.CurrentCount == 0) dirtySignal.Release();
    }

    private void RememberResolutionUnsafe(string source, string translatedValue)
    {
        if (string.Equals(source, translatedValue, StringComparison.Ordinal)) return;
        knownTranslatedValues.Add(translatedValue);
        TouchReverseIndexUnsafe(translatedValue);
        if (ambiguousTranslatedValues.Contains(translatedValue)) return;
        if (sourceByTranslatedValue.TryGetValue(translatedValue, out var existing)
            && !string.Equals(existing, source, StringComparison.Ordinal))
        {
            sourceByTranslatedValue.Remove(translatedValue);
            ambiguousTranslatedValues.Add(translatedValue);
            return;
        }
        sourceByTranslatedValue[translatedValue] = source;
    }

    private void TouchReverseIndexUnsafe(string translatedValue)
    {
        if (reverseIndexNodes.TryGetValue(translatedValue, out var existing))
        {
            reverseIndexLru.Remove(existing);
            reverseIndexLru.AddLast(existing);
            return;
        }
        var node = reverseIndexLru.AddLast(translatedValue);
        reverseIndexNodes[translatedValue] = node;
        while (reverseIndexNodes.Count > RuntimeReverseIndexCapacity)
        {
            var oldest = reverseIndexLru.First!;
            reverseIndexLru.RemoveFirst();
            reverseIndexNodes.Remove(oldest.Value);
            sourceByTranslatedValue.Remove(oldest.Value);
            knownTranslatedValues.Remove(oldest.Value);
            ambiguousTranslatedValues.Remove(oldest.Value);
        }
    }

    private Dictionary<string, string> LoadGenerated(out bool cleaned)
    {
        cleaned = false;
        try
        {
            if (!File.Exists(GeneratedPath)) return new(StringComparer.Ordinal);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(GeneratedPath));
            var valid = new Dictionary<string, string>(StringComparer.Ordinal);
            if (loaded == null) return valid;
            foreach (var entry in loaded)
            {
                if (string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(entry.Value))
                {
                    cleaned = true;
                    continue;
                }
                if (string.Equals(entry.Key, entry.Value, StringComparison.Ordinal)
                    || !TextTemplate.HasSamePlaceholders(entry.Key, entry.Value)
                    || !TextTemplate.HasSameMarkup(entry.Key, entry.Value))
                {
                    cleaned = true;
                    continue;
                }
                valid[entry.Key] = entry.Value;
            }
            return valid;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            cleaned = true;
            SafeDiagnostic(GeneratedPath, exception);
            return new(StringComparer.Ordinal);
        }
    }

    private string[] LoadPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return Array.Empty<string>();
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(PendingPath)) ?? Array.Empty<string>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SafeDiagnostic(PendingPath, exception);
            return Array.Empty<string>();
        }
    }

    private string[] LoadRaw()
    {
        try
        {
            if (!File.Exists(RawPath)) return Array.Empty<string>();
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RawPath));
            return values?.Keys.ToArray() ?? Array.Empty<string>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SafeDiagnostic(RawPath, exception);
            return Array.Empty<string>();
        }
    }

    private bool TryWriteAtomic(string path, string json)
    {
        if (writeAtomic != null)
        {
            try { return writeAtomic(path, json); }
            catch (Exception exception)
            {
                SafeDiagnostic(path, exception);
                return false;
            }
        }
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SafeDiagnostic(path, exception);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return false;
        }
    }

    private void SafeDiagnostic(string path, Exception exception) =>
        diagnostic?.Invoke(Path.GetFileName(path) + ": " + exception.GetType().Name);
}
