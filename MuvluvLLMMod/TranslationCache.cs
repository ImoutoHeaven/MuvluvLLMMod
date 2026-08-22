using System.Text;
using System.Text.Json;

namespace MuvluvLLMMod;

public sealed class TranslationCache
{
    public readonly record struct PendingObservation(bool ShouldEnqueue, long Generation);

    public readonly record struct BudgetSnapshot(
        int GeneratedCount,
        long GeneratedUtf8Bytes,
        int PendingCount,
        long PendingUtf8Bytes,
        int RawCount,
        long RawUtf8Bytes,
        int ReverseCount,
        long ReverseUtf8Bytes,
        int PromotedPendingCount,
        long PromotedPendingUtf8Bytes);

    public const int TerminalFlushMaxAttempts = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object gate = new();
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private readonly SemaphoreSlim dirtySignal = new(0);
    private readonly Action<string>? diagnostic;
    private readonly BoundedDiagnostic budgetDiagnostic;
    private readonly Func<string, string, bool>? writeAtomic;
    private readonly TimeSpan persistenceRetryDelay;

    private Dictionary<string, string> generated = new(StringComparer.Ordinal);
    private readonly LinkedList<string> generatedLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> generatedNodes = new(StringComparer.Ordinal);
    private long generatedUtf8Bytes;

    private readonly List<string> pending = new();
    private readonly HashSet<string> pendingSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pendingGenerations = new(StringComparer.Ordinal);
    private long pendingUtf8Bytes;

    private readonly HashSet<string> raw = new(StringComparer.Ordinal);
    private long rawUtf8Bytes;

    private readonly Dictionary<string, string> sourceByTranslatedValue = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownTranslatedValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguousTranslatedValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>> reverseIndexNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> reverseIndexEntryBytes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> reverseIndexLru = new();
    private long reverseIndexUtf8Bytes;

    private readonly HashSet<string> runtimePromotedPending = new(StringComparer.Ordinal);
    private long runtimePromotedPendingUtf8Bytes;

    private bool dirty;
    private bool acceptingMutations = true;
    private long mutationVersion;
    private long nextPendingGeneration;

    public sealed class DurableSnapshot
    {
        public int Version { get; set; }
        public Dictionary<string, string>? Generated { get; set; }
        public string[]? Pending { get; set; }
        public string[]? Raw { get; set; }
    }

    private sealed record LoadedSnapshot(
        Dictionary<string, string> Generated,
        string[] Pending,
        string[] Raw,
        bool Cleaned);

    public TranslationCache(
        string root,
        Action<string>? diagnostic = null,
        Func<string, string, bool>? writeAtomic = null,
        TimeSpan? persistenceRetryDelay = null)
    {
        Root = root;
        this.diagnostic = diagnostic;
        budgetDiagnostic = new BoundedDiagnostic(diagnostic);
        this.writeAtomic = writeAtomic;
        this.persistenceRetryDelay = persistenceRetryDelay > TimeSpan.Zero
            ? persistenceRetryDelay.Value
            : TimeSpan.FromMilliseconds(250);
        GeneratedPath = Path.Combine(root, "generated.zh_Hans.json");
        PendingPath = Path.Combine(root, "pending.zh_Hans.json");
        RawPath = Path.Combine(root, "dump", "ui_raw.json");
        StatePath = Path.Combine(root, "cache.state.v1.json");
        StateTemporaryPath = StatePath + ".tmp";
        StateBackupPath = StatePath + ".bak";
    }

    public string Root { get; }
    public string GeneratedPath { get; }
    public string PendingPath { get; }
    public string RawPath { get; }
    public string StatePath { get; }
    public string StateTemporaryPath { get; }
    public string StateBackupPath { get; }

    public BudgetSnapshot RetainedSnapshot
    {
        get
        {
            lock (gate)
            {
                return new BudgetSnapshot(
                    generated.Count,
                    generatedUtf8Bytes,
                    pending.Count,
                    pendingUtf8Bytes,
                    raw.Count,
                    rawUtf8Bytes,
                    reverseIndexNodes.Count,
                    reverseIndexUtf8Bytes,
                    runtimePromotedPending.Count,
                    runtimePromotedPendingUtf8Bytes);
            }
        }
    }

    public bool TryGetGenerated(string normalizedTemplate, out string translatedTemplate)
    {
        lock (gate)
        {
            if (!generated.TryGetValue(normalizedTemplate, out translatedTemplate!))
                return false;
            TouchGeneratedUnsafe(normalizedTemplate);
            return true;
        }
    }

    public bool ObservePriority(string original, string normalizedTemplate) => Observe(original, normalizedTemplate, true);
    public bool ObserveNormal(string original, string normalizedTemplate) => Observe(original, normalizedTemplate, false);
    public PendingObservation ObservePriorityGeneration(string original, string normalizedTemplate) =>
        ObserveGeneration(original, normalizedTemplate, true);
    public PendingObservation ObserveNormalGeneration(string original, string normalizedTemplate) =>
        ObserveGeneration(original, normalizedTemplate, false);

    public bool StoreGenerated(string normalizedTemplate, string translatedTemplate)
    {
        if (!IsValidGeneratedPair(normalizedTemplate, translatedTemplate))
            return false;

        lock (gate)
        {
            if (!acceptingMutations
                || !TryStoreGeneratedUnsafe(normalizedTemplate, translatedTemplate))
                return false;
            RemovePendingUnsafe(normalizedTemplate);
            runtimePromotedPending.Remove(normalizedTemplate);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public bool StoreGeneratedIfPending(string normalizedTemplate, long pendingGeneration, string translatedTemplate)
    {
        if (!IsValidGeneratedPair(normalizedTemplate, translatedTemplate))
            return false;

        lock (gate)
        {
            if (!acceptingMutations
                || !pendingGenerations.TryGetValue(normalizedTemplate, out var currentGeneration)
                || currentGeneration != pendingGeneration
                || !TryStoreGeneratedUnsafe(normalizedTemplate, translatedTemplate))
                return false;
            RemovePendingUnsafe(normalizedTemplate);
            runtimePromotedPending.Remove(normalizedTemplate);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public void RemoveGenerated(string normalizedTemplate)
    {
        lock (gate)
        {
            if (!acceptingMutations || !RemoveGeneratedUnsafe(normalizedTemplate))
                return;
            MarkDirtyUnsafe();
        }
    }

    public bool CancelPending(string normalizedTemplate) => CancelPending(normalizedTemplate, out _);

    public bool CancelPending(string normalizedTemplate, out long pendingGeneration)
    {
        lock (gate)
        {
            if (!acceptingMutations
                || !pendingSet.Contains(normalizedTemplate)
                || !pendingGenerations.TryGetValue(normalizedTemplate, out pendingGeneration))
            {
                pendingGeneration = 0;
                return false;
            }

            RemovePendingUnsafe(normalizedTemplate);
            runtimePromotedPending.Remove(normalizedTemplate);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public void RememberResolution(string source, string translatedValue)
    {
        if (!TranslationBudget.TryGetPairBytes(source, translatedValue, out _)
            || string.Equals(source, translatedValue, StringComparison.Ordinal))
            return;
        lock (gate)
        {
            if (acceptingMutations)
                RememberResolutionUnsafe(source, translatedValue);
        }
    }

    public bool IsKnownTranslatedValue(string value)
    {
        if (!TranslationBudget.IsTextWithinBudget(value))
            return false;
        lock (gate)
        {
            if (!knownTranslatedValues.Contains(value))
                return false;
            TouchReverseIndexUnsafe(value);
            return true;
        }
    }

    public bool TryGetSourceForTranslatedValue(string translatedValue, out string source)
    {
        source = string.Empty;
        if (!TranslationBudget.IsTextWithinBudget(translatedValue))
            return false;
        lock (gate)
        {
            if (!sourceByTranslatedValue.TryGetValue(translatedValue, out source!))
                return false;
            TouchReverseIndexUnsafe(translatedValue);
            return true;
        }
    }

    public void ConfirmSourceIdentity(string source)
    {
        if (!TranslationBudget.IsTextWithinBudget(source))
            return;
        lock (gate)
        {
            if (!acceptingMutations || !sourceByTranslatedValue.Remove(source))
                return;
            knownTranslatedValues.Add(source);
            ambiguousTranslatedValues.Add(source);
            SetReverseEntryBytesUnsafe(source, Utf8Bytes(source));
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
        var state = LoadDurableSnapshot(out var hasStateArtifacts);
        Dictionary<string, string> loadedGenerated;
        string[] loadedPending;
        string[] loadedRaw;
        var cleaned = false;
        if (state != null)
        {
            loadedGenerated = state.Generated;
            loadedPending = state.Pending;
            loadedRaw = state.Raw;
            cleaned = state.Cleaned;
        }
        else if (hasStateArtifacts)
        {
            loadedGenerated = new Dictionary<string, string>(StringComparer.Ordinal);
            loadedPending = Array.Empty<string>();
            loadedRaw = Array.Empty<string>();
            cleaned = true;
        }
        else
        {
            loadedGenerated = LoadGenerated(out cleaned);
            var loadedPendingCleaned = false;
            loadedPending = LoadPending(ref loadedPendingCleaned);
            loadedRaw = LoadRaw(ref loadedPendingCleaned);
            cleaned |= loadedPendingCleaned;
        }
        lock (gate)
        {
            generated.Clear();
            generatedLru.Clear();
            generatedNodes.Clear();
            generatedUtf8Bytes = 0;
            foreach (var entry in loadedGenerated)
            {
                if (!TryStoreGeneratedUnsafe(entry.Key, entry.Value))
                    cleaned = true;
            }

            pending.Clear();
            pendingSet.Clear();
            pendingGenerations.Clear();
            pendingUtf8Bytes = 0;
            nextPendingGeneration = 0;
            foreach (var template in loadedPending)
            {
                if (!TextTemplate.IsTranslationCandidate(template)
                    || generated.ContainsKey(template)
                    || !TryAddPendingUnsafe(template))
                {
                    cleaned = true;
                    continue;
                }
                pendingGenerations[template] = ++nextPendingGeneration;
            }

            raw.Clear();
            rawUtf8Bytes = 0;
            foreach (var value in loadedRaw)
            {
                if (!TextTemplate.IsTranslationCandidate(value))
                {
                    cleaned = true;
                    continue;
                }
                var normalized = TextTemplate.Normalize(value).Template;
                if (!string.Equals(value, normalized, StringComparison.Ordinal)
                    || !TryAddRawUnsafe(normalized))
                    cleaned = true;
            }

            sourceByTranslatedValue.Clear();
            knownTranslatedValues.Clear();
            ambiguousTranslatedValues.Clear();
            reverseIndexNodes.Clear();
            reverseIndexEntryBytes.Clear();
            reverseIndexLru.Clear();
            reverseIndexUtf8Bytes = 0;
            runtimePromotedPending.Clear();
            runtimePromotedPendingUtf8Bytes = 0;
            foreach (var entry in generated)
                RememberResolutionUnsafe(entry.Key, entry.Value);
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

    public bool FlushTerminal(TimeSpan? budget = null)
    {
        var totalBudget = budget is { } value && value > TimeSpan.Zero
            ? value
            : TranslationBudget.DefaultTerminalFlushBudget;
        var deadline = DateTime.UtcNow + totalBudget;
        var delay = TimeSpan.FromMilliseconds(40);
        for (var attempt = 0; attempt < TerminalFlushMaxAttempts; attempt++)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            if (FlushCore(remaining)) return true;
            if (attempt + 1 >= TerminalFlushMaxAttempts)
                break;
            remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            Thread.Sleep(remaining < delay ? remaining : delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(500, delay.TotalMilliseconds * 2));
        }
        return false;
    }

    public bool Flush() => FlushCore(null);

    private bool FlushCore(TimeSpan? writerWait)
    {
        var entered = writerWait.HasValue
            ? writerGate.Wait(writerWait.Value)
            : writerGate.Wait(Timeout.InfiniteTimeSpan);
        if (!entered)
        {
            ReportBudget("cache-writer-gate", "cache persistence writer was still busy at the terminal deadline.");
            return false;
        }

        try
        {
            Dictionary<string, string> generatedSnapshot;
            string[] pendingSnapshot;
            string[] rawSnapshot;
            long snapshotVersion;
            lock (gate)
            {
                if (!dirty) return true;
                generatedSnapshot = new Dictionary<string, string>(generated, StringComparer.Ordinal);
                pendingSnapshot = pending.ToArray();
                rawSnapshot = raw.ToArray();
                snapshotVersion = mutationVersion;
            }

            var stateJson = SerializeBounded(
                new DurableSnapshot
                {
                    Version = 1,
                    Generated = generatedSnapshot,
                    Pending = pendingSnapshot,
                    Raw = rawSnapshot
                },
                StatePath);
            var generatedJson = SerializeBounded(generatedSnapshot, GeneratedPath);
            var pendingJson = SerializeBounded(pendingSnapshot, PendingPath);
            var rawJson = SerializeBounded(
                rawSnapshot.ToDictionary(value => value, _ => string.Empty, StringComparer.Ordinal),
                RawPath);
            var succeeded = stateJson != null
                && generatedJson != null
                && pendingJson != null
                && rawJson != null
                // The single state file is authoritative. The three legacy mirrors remain for
                // older releases, but a crash between mirrors can never create a mixed load.
                && TryWriteAtomic(StatePath, stateJson, preserveRecovery: true)
                && TryWriteAtomic(GeneratedPath, generatedJson)
                && TryWriteAtomic(PendingPath, pendingJson)
                && TryWriteAtomic(RawPath, rawJson);
            lock (gate)
            {
                if (succeeded && mutationVersion == snapshotVersion)
                {
                    dirty = false;
                    return true;
                }

                SignalDirtyUnsafe();
                return false;
            }
        }
        finally
        {
            writerGate.Release();
        }
    }

    private bool Observe(string original, string normalizedTemplate, bool priority) =>
        ObserveGeneration(original, normalizedTemplate, priority).ShouldEnqueue;

    private PendingObservation ObserveGeneration(string original, string normalizedTemplate, bool priority)
    {
        if (!TextTemplate.IsTranslationCandidate(original)
            || !TranslationBudget.IsTextWithinBudget(normalizedTemplate)
            || !TextTemplate.IsTranslationCandidate(normalizedTemplate))
        {
            if (TextTemplate.IsTranslationCandidate(original))
                ReportBudget("cache-template", "translation template exceeded the bounded cache budget; observation was skipped.");
            return default;
        }

        lock (gate)
        {
            if (!acceptingMutations)
                return default;

            var rawSample = TextTemplate.Normalize(original).Template;
            var changed = TryAddRawUnsafe(rawSample);
            if (generated.ContainsKey(normalizedTemplate))
            {
                if (changed) MarkDirtyUnsafe();
                return default;
            }

            var addedPending = false;
            if (!pendingSet.Contains(normalizedTemplate))
            {
                if (!TryAddPendingUnsafe(normalizedTemplate))
                {
                    ReportBudgetUnsafe(
                        "cache-pending",
                        "translation pending backlog reached its bounded budget; text was left unchanged.");
                    if (changed) MarkDirtyUnsafe();
                    return default;
                }
                pendingGenerations[normalizedTemplate] = ++nextPendingGeneration;
                changed = true;
                addedPending = true;
            }

            var promote = false;
            if (priority && !runtimePromotedPending.Contains(normalizedTemplate))
            {
                if (TryAddPromotedPendingUnsafe(normalizedTemplate))
                    promote = true;
                else
                    ReportBudgetUnsafe(
                        "cache-priority",
                        "translation priority backlog reached its bounded budget; text remains durably pending.");
            }

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
        if (string.Equals(source, translatedValue, StringComparison.Ordinal)
            || !TranslationBudget.TryGetPairBytes(source, translatedValue, out _))
            return;

        if (ambiguousTranslatedValues.Contains(translatedValue))
        {
            TouchReverseIndexUnsafe(translatedValue);
            return;
        }

        if (sourceByTranslatedValue.TryGetValue(translatedValue, out var existing))
        {
            if (string.Equals(existing, source, StringComparison.Ordinal))
            {
                TouchReverseIndexUnsafe(translatedValue);
                return;
            }

            sourceByTranslatedValue.Remove(translatedValue);
            ambiguousTranslatedValues.Add(translatedValue);
            SetReverseEntryBytesUnsafe(translatedValue, Utf8Bytes(translatedValue));
            TouchReverseIndexUnsafe(translatedValue);
            return;
        }

        var entryBytes = Utf8Bytes(translatedValue) + Utf8Bytes(source);
        if (!EnsureReverseCapacityUnsafe(translatedValue, entryBytes))
            return;
        knownTranslatedValues.Add(translatedValue);
        sourceByTranslatedValue[translatedValue] = source;
        SetReverseEntryBytesUnsafe(translatedValue, entryBytes);
        TouchReverseIndexUnsafe(translatedValue);
    }

    private bool EnsureReverseCapacityUnsafe(string translatedValue, int entryBytes)
    {
        if (entryBytes > TranslationBudget.MaxReverseUtf8Bytes)
            return false;
        if (reverseIndexNodes.ContainsKey(translatedValue))
            return true;

        while (reverseIndexNodes.Count >= TranslationBudget.MaxReverseEntries
            || reverseIndexUtf8Bytes + entryBytes > TranslationBudget.MaxReverseUtf8Bytes)
        {
            if (reverseIndexLru.First == null)
                return false;
            RemoveReverseUnsafe(reverseIndexLru.First.Value);
        }
        return true;
    }

    private void TouchReverseIndexUnsafe(string translatedValue)
    {
        if (reverseIndexNodes.TryGetValue(translatedValue, out var existing))
        {
            reverseIndexLru.Remove(existing);
            reverseIndexLru.AddLast(existing);
            return;
        }

        var entryBytes = Utf8Bytes(translatedValue);
        if (!EnsureReverseCapacityUnsafe(translatedValue, entryBytes))
            return;
        var node = reverseIndexLru.AddLast(translatedValue);
        reverseIndexNodes[translatedValue] = node;
        reverseIndexEntryBytes[translatedValue] = entryBytes;
        reverseIndexUtf8Bytes += entryBytes;
    }

    private void SetReverseEntryBytesUnsafe(string translatedValue, int entryBytes)
    {
        if (!reverseIndexNodes.ContainsKey(translatedValue))
            return;
        if (entryBytes == reverseIndexEntryBytes[translatedValue])
            return;
        reverseIndexUtf8Bytes += entryBytes - reverseIndexEntryBytes[translatedValue];
        reverseIndexEntryBytes[translatedValue] = entryBytes;
    }

    private void RemoveReverseUnsafe(string translatedValue)
    {
        if (reverseIndexNodes.Remove(translatedValue, out var node))
            reverseIndexLru.Remove(node);
        if (reverseIndexEntryBytes.Remove(translatedValue, out var entryBytes))
            reverseIndexUtf8Bytes -= entryBytes;
        sourceByTranslatedValue.Remove(translatedValue);
        knownTranslatedValues.Remove(translatedValue);
        ambiguousTranslatedValues.Remove(translatedValue);
    }

    private bool TryStoreGeneratedUnsafe(string normalizedTemplate, string translatedTemplate)
    {
        var entryBytes = Utf8Bytes(normalizedTemplate) + Utf8Bytes(translatedTemplate);
        if (entryBytes > TranslationBudget.MaxGeneratedUtf8Bytes)
        {
            ReportBudgetUnsafe(
                "cache-generated-entry",
                "generated translation exceeded the bounded entry budget; it was left pending.");
            return false;
        }

        RemoveGeneratedUnsafe(normalizedTemplate);
        while (generated.Count >= TranslationBudget.MaxGeneratedEntries
            || generatedUtf8Bytes + entryBytes > TranslationBudget.MaxGeneratedUtf8Bytes)
        {
            if (generatedLru.First == null)
                return false;
            RemoveGeneratedUnsafe(generatedLru.First.Value);
        }

        generated[normalizedTemplate] = translatedTemplate;
        var node = generatedLru.AddLast(normalizedTemplate);
        generatedNodes[normalizedTemplate] = node;
        generatedUtf8Bytes += entryBytes;
        RememberResolutionUnsafe(normalizedTemplate, translatedTemplate);
        return true;
    }

    private bool RemoveGeneratedUnsafe(string normalizedTemplate)
    {
        if (!generated.Remove(normalizedTemplate, out var translated))
            return false;
        if (generatedNodes.Remove(normalizedTemplate, out var node))
            generatedLru.Remove(node);
        generatedUtf8Bytes -= Utf8Bytes(normalizedTemplate) + Utf8Bytes(translated);
        return true;
    }

    private void TouchGeneratedUnsafe(string normalizedTemplate)
    {
        if (!generatedNodes.TryGetValue(normalizedTemplate, out var node))
            return;
        generatedLru.Remove(node);
        generatedLru.AddLast(node);
    }

    private bool TryAddPendingUnsafe(string template)
    {
        var bytes = Utf8Bytes(template);
        if (pending.Count >= TranslationBudget.MaxPendingEntries
            || pendingUtf8Bytes + bytes > TranslationBudget.MaxPendingUtf8Bytes)
            return false;
        pendingSet.Add(template);
        pending.Add(template);
        pendingUtf8Bytes += bytes;
        return true;
    }

    private void RemovePendingUnsafe(string template)
    {
        if (!pendingSet.Remove(template))
            return;
        pendingGenerations.Remove(template);
        pending.RemoveAll(value => string.Equals(value, template, StringComparison.Ordinal));
        pendingUtf8Bytes -= Utf8Bytes(template);
        if (pendingUtf8Bytes < 0) pendingUtf8Bytes = 0;
        if (runtimePromotedPending.Remove(template))
            runtimePromotedPendingUtf8Bytes -= Utf8Bytes(template);
    }

    private bool TryAddRawUnsafe(string value)
    {
        if (raw.Contains(value))
            return false;
        var bytes = Utf8Bytes(value);
        if (raw.Count >= TranslationBudget.MaxRawEntries
            || rawUtf8Bytes + bytes > TranslationBudget.MaxRawUtf8Bytes)
            return false;
        raw.Add(value);
        rawUtf8Bytes += bytes;
        return true;
    }

    private bool TryAddPromotedPendingUnsafe(string template)
    {
        var bytes = Utf8Bytes(template);
        if (runtimePromotedPending.Count >= TranslationBudget.MaxPendingEntries
            || runtimePromotedPendingUtf8Bytes + bytes > TranslationBudget.MaxPendingUtf8Bytes)
            return false;
        runtimePromotedPending.Add(template);
        runtimePromotedPendingUtf8Bytes += bytes;
        return true;
    }

    private LoadedSnapshot? LoadDurableSnapshot(out bool hasStateArtifacts)
    {
        hasStateArtifacts = File.Exists(StatePath)
            || File.Exists(StateTemporaryPath)
            || File.Exists(StateBackupPath);
        if (!hasStateArtifacts)
            return null;

        foreach (var path in new[] { StatePath, StateTemporaryPath, StateBackupPath })
        {
            var cleaned = false;
            var text = ReadBoundedText(path, ref cleaned);
            if (text == null)
                continue;
            try
            {
                var snapshot = JsonSerializer.Deserialize<DurableSnapshot>(text);
                if (snapshot?.Version != 1
                    || snapshot.Generated == null
                    || snapshot.Pending == null
                    || snapshot.Raw == null)
                {
                    ReportBudget("cache-state", "cache state snapshot was incomplete; trying the next recovery epoch.");
                    continue;
                }

                var generated = FilterGenerated(snapshot.Generated, ref cleaned);
                var pending = FilterPending(snapshot.Pending, ref cleaned);
                var raw = FilterRaw(snapshot.Raw, ref cleaned);
                if (!string.Equals(path, StatePath, StringComparison.Ordinal))
                    cleaned = true;
                return new LoadedSnapshot(generated, pending, raw, cleaned);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                SafeDiagnostic(path, exception);
            }
        }

        // Do not combine legacy files after a state artifact was observed: that would create a
        // generated/new-pending/raw mixed epoch. The next observable render can safely rebuild
        // missing entries, and the next flush writes one coherent state.
        return new LoadedSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<string>(),
            Array.Empty<string>(),
            true);
    }

    private Dictionary<string, string> FilterGenerated(
        IReadOnlyDictionary<string, string> loaded,
        ref bool cleaned)
    {
        var valid = new Dictionary<string, string>(StringComparer.Ordinal);
        var retainedBytes = 0L;
        foreach (var entry in loaded)
        {
            if (!IsValidGeneratedPair(entry.Key, entry.Value))
            {
                cleaned = true;
                continue;
            }
            var bytes = Utf8Bytes(entry.Key) + Utf8Bytes(entry.Value!);
            if (valid.Count >= TranslationBudget.MaxGeneratedEntries
                || retainedBytes + bytes > TranslationBudget.MaxGeneratedUtf8Bytes)
            {
                cleaned = true;
                continue;
            }
            valid[entry.Key] = entry.Value!;
            retainedBytes += bytes;
        }
        return valid;
    }

    private string[] FilterPending(IEnumerable<string?> loaded, ref bool cleaned)
    {
        var valid = new List<string>();
        var bytes = 0L;
        foreach (var template in loaded)
        {
            if (template == null
                || !TextTemplate.IsTranslationCandidate(template)
                || valid.Count >= TranslationBudget.MaxPendingEntries
                || bytes + Utf8Bytes(template) > TranslationBudget.MaxPendingUtf8Bytes
                || valid.Contains(template, StringComparer.Ordinal))
            {
                cleaned = true;
                continue;
            }
            valid.Add(template);
            bytes += Utf8Bytes(template);
        }
        return valid.ToArray();
    }

    private string[] FilterRaw(IEnumerable<string> loaded, ref bool cleaned)
    {
        var valid = new List<string>();
        var bytes = 0L;
        foreach (var value in loaded)
        {
            if (!TextTemplate.IsTranslationCandidate(value))
            {
                cleaned = true;
                continue;
            }
            var normalized = TextTemplate.Normalize(value).Template;
            var normalizedBytes = Utf8Bytes(normalized);
            if (valid.Count >= TranslationBudget.MaxRawEntries
                || bytes + normalizedBytes > TranslationBudget.MaxRawUtf8Bytes)
            {
                cleaned = true;
                continue;
            }
            if (!valid.Contains(normalized, StringComparer.Ordinal))
            {
                valid.Add(normalized);
                bytes += normalizedBytes;
            }
        }
        return valid.ToArray();
    }

    private Dictionary<string, string> LoadGenerated(out bool cleaned)
    {
        cleaned = false;
        var text = ReadBoundedText(GeneratedPath, ref cleaned);
        if (text == null) return new(StringComparer.Ordinal);
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string?>>(text);
            var valid = new Dictionary<string, string>(StringComparer.Ordinal);
            var retainedBytes = 0L;
            if (loaded == null) return valid;
            foreach (var entry in loaded)
            {
                if (!IsValidGeneratedPair(entry.Key, entry.Value))
                {
                    cleaned = true;
                    continue;
                }
                var bytes = Utf8Bytes(entry.Key) + Utf8Bytes(entry.Value!);
                if (valid.Count >= TranslationBudget.MaxGeneratedEntries
                    || retainedBytes + bytes > TranslationBudget.MaxGeneratedUtf8Bytes)
                {
                    cleaned = true;
                    continue;
                }
                valid[entry.Key] = entry.Value!;
                retainedBytes += bytes;
            }
            return valid;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            cleaned = true;
            SafeDiagnostic(GeneratedPath, exception);
            return new(StringComparer.Ordinal);
        }
    }

    private string[] LoadPending(ref bool cleaned)
    {
        var text = ReadBoundedText(PendingPath, ref cleaned);
        if (text == null) return Array.Empty<string>();
        try
        {
            var loaded = JsonSerializer.Deserialize<string[]>(text) ?? Array.Empty<string>();
            var valid = new List<string>(Math.Min(loaded.Length, TranslationBudget.MaxPendingEntries));
            var bytes = 0L;
            foreach (var template in loaded)
            {
                if (!TextTemplate.IsTranslationCandidate(template)
                    || valid.Count >= TranslationBudget.MaxPendingEntries
                    || bytes + Utf8Bytes(template) > TranslationBudget.MaxPendingUtf8Bytes
                    || valid.Contains(template, StringComparer.Ordinal))
                {
                    cleaned = true;
                    continue;
                }
                valid.Add(template);
                bytes += Utf8Bytes(template);
            }
            return valid.ToArray();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            cleaned = true;
            SafeDiagnostic(PendingPath, exception);
            return Array.Empty<string>();
        }
    }

    private string[] LoadRaw(ref bool cleaned)
    {
        var text = ReadBoundedText(RawPath, ref cleaned);
        if (text == null) return Array.Empty<string>();
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (values == null) return Array.Empty<string>();
            var valid = new List<string>(Math.Min(values.Count, TranslationBudget.MaxRawEntries));
            var bytes = 0L;
            foreach (var value in values.Keys)
            {
                if (!TextTemplate.IsTranslationCandidate(value))
                {
                    cleaned = true;
                    continue;
                }
                var normalized = TextTemplate.Normalize(value).Template;
                var normalizedBytes = Utf8Bytes(normalized);
                if (valid.Count >= TranslationBudget.MaxRawEntries
                    || bytes + normalizedBytes > TranslationBudget.MaxRawUtf8Bytes)
                {
                    cleaned = true;
                    continue;
                }
                if (!valid.Contains(normalized, StringComparer.Ordinal))
                {
                    valid.Add(normalized);
                    bytes += normalizedBytes;
                }
                else
                    cleaned = true;
            }
            return valid.ToArray();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            cleaned = true;
            SafeDiagnostic(RawPath, exception);
            return Array.Empty<string>();
        }
    }

    private string? ReadBoundedText(string path, ref bool cleaned)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > TranslationBudget.MaxCacheFileBytes)
            {
                cleaned = true;
                ReportBudget("cache-file", Path.GetFileName(path) + " exceeded the bounded cache file size; it was ignored.");
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var bytes = new MemoryStream((int)Math.Min(info.Length, TranslationBudget.MaxCacheFileBytes));
            var buffer = new byte[8192];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (bytes.Length + read > TranslationBudget.MaxCacheFileBytes)
                {
                    cleaned = true;
                    ReportBudget("cache-file", Path.GetFileName(path) + " exceeded the bounded cache file size; it was ignored.");
                    return null;
                }
                bytes.Write(buffer, 0, read);
            }
            var text = StrictUtf8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            cleaned = true;
            SafeDiagnostic(path, exception);
            return null;
        }
    }

    private string? SerializeBounded<T>(T value, string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            if (StrictUtf8.GetByteCount(json) > TranslationBudget.MaxCacheSnapshotBytes)
            {
                ReportBudget("cache-snapshot", Path.GetFileName(path) + " exceeded the bounded snapshot size; persistence was rejected.");
                return null;
            }
            return json;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            SafeDiagnostic(path, exception);
            return null;
        }
    }

    private bool TryWriteAtomic(string path, string json, bool preserveRecovery = false)
    {
        if (StrictUtf8.GetByteCount(json) > TranslationBudget.MaxCacheFileBytes)
        {
            ReportBudget("cache-write-size", Path.GetFileName(path) + " exceeded the bounded cache file size; persistence was rejected.");
            return false;
        }

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
            if (preserveRecovery && File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    SafeDiagnostic(path, exception);
                }
            }
            File.WriteAllText(temporary, json, StrictUtf8);
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SafeDiagnostic(path, exception);
            // Keep the canonical temp snapshot as a recoverable journal. Legacy mirrors use the
            // old cleanup behavior because they are never selected over a valid state snapshot.
            if (!preserveRecovery)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
            return false;
        }
    }

    private bool IsValidGeneratedPair(string? normalizedTemplate, string? translatedTemplate)
    {
        if (!TranslationBudget.TryGetPairBytes(normalizedTemplate, translatedTemplate, out _)
            || string.Equals(normalizedTemplate, translatedTemplate, StringComparison.Ordinal)
            || !TextTemplate.HasSamePlaceholders(normalizedTemplate!, translatedTemplate!)
            || !TextTemplate.HasSameMarkup(normalizedTemplate!, translatedTemplate!))
        {
            if (normalizedTemplate != null && translatedTemplate != null
                && (!TranslationBudget.IsTextWithinBudget(normalizedTemplate)
                    || !TranslationBudget.IsTextWithinBudget(translatedTemplate)))
                ReportBudget("cache-generated-input", "generated translation input exceeded the bounded text budget; it was ignored.");
            return false;
        }
        return true;
    }

    private int Utf8Bytes(string value) => StrictUtf8.GetByteCount(value);

    private void ReportBudget(string code, string message) => budgetDiagnostic.Report(code, message);

    private void ReportBudgetUnsafe(string code, string message) => budgetDiagnostic.Report(code, message);

    private void SafeDiagnostic(string path, Exception exception) =>
        diagnostic?.Invoke(Path.GetFileName(path) + ": " + exception.GetType().Name);
}
