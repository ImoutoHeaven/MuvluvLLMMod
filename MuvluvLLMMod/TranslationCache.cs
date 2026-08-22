using System.Security.Cryptography;
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
    private static readonly JsonSerializerOptions IntegrityJsonOptions = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SnapshotLayout DurableSnapshotLayout = CreateSnapshotLayout();
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
    // These counters are the exact UTF-8 sizes of the JSON-encoded collection items.  The
    // additive layout below also reserves the indented separators and the complete state
    // envelope/checksum metadata, so durable admission is independent of raw UTF-8 payload size.
    private long generatedSnapshotJsonUtf8Bytes;

    private readonly List<string> pending = new();
    private readonly HashSet<string> pendingSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pendingGenerations = new(StringComparer.Ordinal);
    private long pendingUtf8Bytes;
    private long pendingSnapshotJsonUtf8Bytes;

    private readonly HashSet<string> raw = new(StringComparer.Ordinal);
    private long rawUtf8Bytes;
    private long rawSnapshotJsonUtf8Bytes;

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
    private bool snapshotEpochExhausted;
    private long mutationVersion;
    private long nextPendingGeneration;
    // snapshotEpoch is the last authoritative epoch. A successor is reserved while its
    // snapshot is being written, but it is not committed until the authoritative write succeeds.
    private long snapshotEpoch;
    private long? reservedSnapshotEpoch;

    public sealed class DurableSnapshot
    {
        public int Version { get; set; }
        public long Epoch { get; set; }
        public string? TransactionId { get; set; }
        public string? Checksum { get; set; }
        public Dictionary<string, string>? Generated { get; set; }
        public string[]? Pending { get; set; }
        public string[]? Raw { get; set; }
    }

    private sealed class SnapshotIntegrityPayload
    {
        public int Version { get; set; }
        public long Epoch { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public SortedDictionary<string, string> Generated { get; set; } = new(StringComparer.Ordinal);
        public string[] Pending { get; set; } = Array.Empty<string>();
        public string[] Raw { get; set; } = Array.Empty<string>();
    }

    private readonly record struct SnapshotLayout(
        long EmptyBytes,
        long GeneratedFirstItemOverhead,
        long GeneratedAdditionalItemOverhead,
        long PendingFirstItemOverhead,
        long PendingAdditionalItemOverhead,
        long RawFirstItemOverhead,
        long RawAdditionalItemOverhead);

    private sealed record LoadedSnapshot(
        Dictionary<string, string> Generated,
        string[] Pending,
        string[] Raw,
        long Epoch,
        bool Cleaned);

    private sealed record SnapshotCandidate(
        string Path,
        LoadedSnapshot Snapshot,
        int Priority);

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
            if (!CanAcceptDurableMutationUnsafe()
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
            if (!CanAcceptDurableMutationUnsafe()
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
            if (!CanAcceptDurableMutationUnsafe() || !RemoveGeneratedUnsafe(normalizedTemplate))
                return;
            MarkDirtyUnsafe();
        }
    }

    public bool CancelPending(string normalizedTemplate) => CancelPending(normalizedTemplate, out _);

    public bool CancelPending(string normalizedTemplate, out long pendingGeneration)
    {
        lock (gate)
        {
            if (!CanAcceptDurableMutationUnsafe()
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

            var retainedBytes = sourceByTranslatedValue.TryGetValue(value, out var source)
                ? Utf8Bytes(value) + Utf8Bytes(source)
                : Utf8Bytes(value);
            if (TryTouchReverseIndexUnsafe(value, retainedBytes))
                return true;

            RemoveReverseUnsafe(value);
            return false;
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

            var retainedBytes = Utf8Bytes(translatedValue) + Utf8Bytes(source);
            if (TryTouchReverseIndexUnsafe(translatedValue, retainedBytes))
                return true;

            RemoveReverseUnsafe(translatedValue);
            source = string.Empty;
            return false;
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
            if (!SetReverseEntryBytesUnsafe(source, Utf8Bytes(source)))
                RemoveReverseUnsafe(source);
            else
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

    /// <summary>
    /// True when the authoritative journal has no valid successor epoch. The cache remains
    /// readable, but durable collection mutations are rejected until a new epoch series is
    /// explicitly introduced by a future protocol.
    /// </summary>
    public bool IsDurableMutationBlocked
    {
        get
        {
            lock (gate)
                return !CanAcceptDurableMutationUnsafe();
        }
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
            if (state != null)
            {
                snapshotEpoch = Math.Max(snapshotEpoch, state.Epoch);
                snapshotEpochExhausted |= state.Epoch == long.MaxValue;
            }

            generated.Clear();
            generatedLru.Clear();
            generatedNodes.Clear();
            generatedUtf8Bytes = 0;
            generatedSnapshotJsonUtf8Bytes = 0;
            pending.Clear();
            pendingSet.Clear();
            pendingGenerations.Clear();
            pendingUtf8Bytes = 0;
            pendingSnapshotJsonUtf8Bytes = 0;
            nextPendingGeneration = 0;
            raw.Clear();
            rawUtf8Bytes = 0;
            rawSnapshotJsonUtf8Bytes = 0;
            sourceByTranslatedValue.Clear();
            knownTranslatedValues.Clear();
            ambiguousTranslatedValues.Clear();
            reverseIndexNodes.Clear();
            reverseIndexEntryBytes.Clear();
            reverseIndexLru.Clear();
            reverseIndexUtf8Bytes = 0;
            runtimePromotedPending.Clear();
            runtimePromotedPendingUtf8Bytes = 0;

            foreach (var entry in loadedGenerated)
            {
                if (!TryStoreGeneratedUnsafe(entry.Key, entry.Value))
                    cleaned = true;
            }

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
            long snapshotEpoch;
            string transactionId;
            lock (gate)
            {
                if (!dirty) return true;
                if (!TryReserveSnapshotEpochUnsafe(out snapshotEpoch))
                {
                    ReportPersistenceFailure(
                        StatePath,
                        "authoritative cache epoch has no valid successor; the dirty state remains unsaved and retryable.");
                    SignalDirtyUnsafe();
                    return false;
                }
                generatedSnapshot = new Dictionary<string, string>(generated, StringComparer.Ordinal);
                pendingSnapshot = pending.ToArray();
                rawSnapshot = raw.ToArray();
                snapshotVersion = mutationVersion;
                transactionId = Guid.NewGuid().ToString("N");
            }

            var state = new DurableSnapshot
            {
                Version = 1,
                Epoch = snapshotEpoch,
                TransactionId = transactionId,
                Generated = generatedSnapshot,
                Pending = pendingSnapshot,
                Raw = rawSnapshot
            };
            state.Checksum = ComputeChecksum(state);
            var stateJson = SerializeBounded(state, StatePath);
            var generatedJson = SerializeBounded(generatedSnapshot, GeneratedPath);
            var pendingJson = SerializeBounded(pendingSnapshot, PendingPath);
            var rawJson = SerializeBounded(
                rawSnapshot.ToDictionary(value => value, _ => string.Empty, StringComparer.Ordinal),
                RawPath);

            // The state file is the only commit decision. Legacy files are migration mirrors:
            // once this write is durable, a mirror failure must not make this epoch look lost.
            var authoritativeSucceeded = stateJson != null
                && TryWriteAtomic(StatePath, stateJson, preserveRecovery: true);
            if (!authoritativeSucceeded)
                ReportPersistenceFailure(StatePath, "authoritative cache state write failed; the dirty epoch remains recoverable/retryable.");

            var mirrorsSucceeded = true;
            if (authoritativeSucceeded)
            {
                mirrorsSucceeded &= TryWriteMirror(GeneratedPath, generatedJson);
                mirrorsSucceeded &= TryWriteMirror(PendingPath, pendingJson);
                mirrorsSucceeded &= TryWriteMirror(RawPath, rawJson);
                if (!mirrorsSucceeded)
                    ReportPersistenceFailure(StatePath, "authoritative cache state committed, but one or more legacy mirrors failed; recovery will use the state epoch.");
            }

            lock (gate)
            {
                if (authoritativeSucceeded)
                    CommitSnapshotEpochUnsafe(snapshotEpoch);

                if (authoritativeSucceeded && mutationVersion == snapshotVersion)
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

    private bool TryWriteMirror(string path, string? json)
    {
        if (json == null)
            return false;
        return TryWriteAtomic(path, json);
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
            if (!CanAcceptDurableMutationUnsafe())
                return default;

            var rawSample = TextTemplate.Normalize(original).Template;
            var rawAdded = CanAddRawUnsafe(rawSample, out var rawBytes, out var rawJsonBytes);
            var generatedAlreadyKnown = generated.ContainsKey(normalizedTemplate);
            var pendingAlreadyKnown = pendingSet.Contains(normalizedTemplate);
            var pendingAdded = !generatedAlreadyKnown && !pendingAlreadyKnown;
            var pendingBytes = pendingAdded ? Utf8Bytes(normalizedTemplate) : 0;
            var pendingJsonBytes = pendingAdded
                ? JsonStringUtf8Bytes(normalizedTemplate)
                : 0;

            if (pendingAdded && !CanAddPendingCapacityUnsafe(pendingBytes))
            {
                ReportBudgetUnsafe(
                    "cache-pending",
                    "translation pending backlog reached its bounded budget; text was left unchanged.");
                return default;
            }

            var candidateRawCount = raw.Count + (rawAdded ? 1 : 0);
            var candidateRawBytes = rawUtf8Bytes + (rawAdded ? rawBytes : 0);
            var candidateRawJsonBytes = rawSnapshotJsonUtf8Bytes + (rawAdded ? rawJsonBytes : 0);
            var candidatePendingCount = pending.Count + (pendingAdded ? 1 : 0);
            var candidatePendingJsonBytes = pendingSnapshotJsonUtf8Bytes + pendingJsonBytes;
            if (!IsDurableStateWithinBudgetUnsafe(
                    generated.Count,
                    generatedSnapshotJsonUtf8Bytes,
                    candidatePendingCount,
                    candidatePendingJsonBytes,
                    candidateRawCount,
                    candidateRawJsonBytes))
            {
                ReportBudgetUnsafe(
                    "cache-snapshot-admission",
                    "the complete authoritative cache snapshot would exceed its bounded serialized budget; observation was rejected.");
                return default;
            }

            var changed = false;
            if (rawAdded)
            {
                AddRawUnsafe(rawSample, rawBytes, rawJsonBytes);
                changed = true;
            }

            var addedPending = false;
            if (pendingAdded)
            {
                AddPendingUnsafe(normalizedTemplate, pendingBytes, pendingJsonBytes);
                pendingGenerations[normalizedTemplate] = ++nextPendingGeneration;
                changed = true;
                addedPending = true;
            }

            if (generatedAlreadyKnown)
            {
                if (changed) MarkDirtyUnsafe();
                return default;
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

    private bool CanAcceptDurableMutationUnsafe() =>
        acceptingMutations
        && !snapshotEpochExhausted
        && snapshotEpoch < long.MaxValue
        // A terminal successor cannot safely coexist with another accepted mutation: once it
        // commits, long.MaxValue is deliberately read-only forever.
        && (!reservedSnapshotEpoch.HasValue || reservedSnapshotEpoch.Value != long.MaxValue);

    private bool TryReserveSnapshotEpochUnsafe(out long epoch)
    {
        if (reservedSnapshotEpoch is { } reserved)
        {
            // An authoritative failure leaves this exact valid successor available to the next
            // normal or terminal retry. It is not a new epoch and must not be skipped.
            epoch = reserved;
            return true;
        }

        if (snapshotEpochExhausted || snapshotEpoch == long.MaxValue)
        {
            epoch = 0;
            snapshotEpochExhausted = true;
            return false;
        }

        epoch = checked(snapshotEpoch + 1);
        reservedSnapshotEpoch = epoch;
        return true;
    }

    private void CommitSnapshotEpochUnsafe(long epoch)
    {
        if (reservedSnapshotEpoch is not { } reserved || reserved != epoch)
            throw new InvalidOperationException("cache snapshot epoch reservation was lost");

        snapshotEpoch = epoch;
        reservedSnapshotEpoch = null;
        snapshotEpochExhausted = epoch == long.MaxValue;
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

        var translatedBytes = Utf8Bytes(translatedValue);
        if (ambiguousTranslatedValues.Contains(translatedValue))
        {
            if (!TryTouchReverseIndexUnsafe(translatedValue, translatedBytes))
                RemoveReverseUnsafe(translatedValue);
            return;
        }

        if (sourceByTranslatedValue.TryGetValue(translatedValue, out var existing))
        {
            var existingBytes = translatedBytes + Utf8Bytes(existing);
            if (string.Equals(existing, source, StringComparison.Ordinal))
            {
                if (!TryTouchReverseIndexUnsafe(translatedValue, existingBytes))
                    RemoveReverseUnsafe(translatedValue);
                return;
            }

            // A shared translated value is retained only as an ambiguity marker: no source may
            // be restored from it. The pair's source bytes therefore leave the accounting here.
            sourceByTranslatedValue.Remove(translatedValue);
            ambiguousTranslatedValues.Add(translatedValue);
            if (!SetReverseEntryBytesUnsafe(translatedValue, translatedBytes))
                RemoveReverseUnsafe(translatedValue);
            else
                TouchReverseIndexUnsafe(translatedValue);
            return;
        }

        var entryBytes = translatedBytes + Utf8Bytes(source);
        if (!EnsureReverseCapacityUnsafe(translatedValue, entryBytes))
            return;

        // Admission and node creation happen before publishing the source mapping. This keeps
        // the retained byte metric equal to the actual source+translation strings at all times.
        AddReverseNodeUnsafe(translatedValue, entryBytes);
        knownTranslatedValues.Add(translatedValue);
        sourceByTranslatedValue[translatedValue] = source;
    }

    private bool EnsureReverseCapacityUnsafe(string translatedValue, int entryBytes)
    {
        if (entryBytes < 0 || entryBytes > TranslationBudget.MaxReverseUtf8Bytes)
            return false;

        if (reverseIndexEntryBytes.TryGetValue(translatedValue, out var currentBytes))
        {
            while (reverseIndexUtf8Bytes - currentBytes + entryBytes > TranslationBudget.MaxReverseUtf8Bytes)
            {
                var oldest = reverseIndexLru.First;
                while (oldest != null && string.Equals(oldest.Value, translatedValue, StringComparison.Ordinal))
                    oldest = oldest.Next;
                if (oldest == null)
                    return false;
                RemoveReverseUnsafe(oldest.Value);
            }
            return true;
        }

        while (reverseIndexNodes.Count >= TranslationBudget.MaxReverseEntries
            || reverseIndexUtf8Bytes + entryBytes > TranslationBudget.MaxReverseUtf8Bytes)
        {
            if (reverseIndexLru.First == null)
                return false;
            RemoveReverseUnsafe(reverseIndexLru.First.Value);
        }
        return true;
    }

    private bool TryTouchReverseIndexUnsafe(string translatedValue, int entryBytes)
    {
        if (!EnsureReverseCapacityUnsafe(translatedValue, entryBytes))
            return false;

        if (reverseIndexNodes.TryGetValue(translatedValue, out var existing))
        {
            var previousBytes = reverseIndexEntryBytes[translatedValue];
            reverseIndexUtf8Bytes += entryBytes - previousBytes;
            reverseIndexEntryBytes[translatedValue] = entryBytes;
            reverseIndexLru.Remove(existing);
            reverseIndexLru.AddLast(existing);
            return true;
        }

        AddReverseNodeUnsafe(translatedValue, entryBytes);
        return true;
    }

    private void TouchReverseIndexUnsafe(string translatedValue)
    {
        var entryBytes = sourceByTranslatedValue.TryGetValue(translatedValue, out var source)
            ? Utf8Bytes(translatedValue) + Utf8Bytes(source)
            : Utf8Bytes(translatedValue);
        _ = TryTouchReverseIndexUnsafe(translatedValue, entryBytes);
    }

    private bool SetReverseEntryBytesUnsafe(string translatedValue, int entryBytes)
    {
        if (!reverseIndexNodes.ContainsKey(translatedValue)
            || !EnsureReverseCapacityUnsafe(translatedValue, entryBytes))
            return false;
        var previousBytes = reverseIndexEntryBytes[translatedValue];
        reverseIndexUtf8Bytes += entryBytes - previousBytes;
        reverseIndexEntryBytes[translatedValue] = entryBytes;
        return true;
    }

    private void AddReverseNodeUnsafe(string translatedValue, int entryBytes)
    {
        var node = reverseIndexLru.AddLast(translatedValue);
        reverseIndexNodes[translatedValue] = node;
        reverseIndexEntryBytes[translatedValue] = entryBytes;
        reverseIndexUtf8Bytes += entryBytes;
    }

    private void RemoveReverseUnsafe(string translatedValue)
    {
        if (reverseIndexNodes.Remove(translatedValue, out var node))
            reverseIndexLru.Remove(node);
        if (reverseIndexEntryBytes.Remove(translatedValue, out var entryBytes))
        {
            reverseIndexUtf8Bytes -= entryBytes;
            if (reverseIndexUtf8Bytes < 0) reverseIndexUtf8Bytes = 0;
        }
        sourceByTranslatedValue.Remove(translatedValue);
        knownTranslatedValues.Remove(translatedValue);
        ambiguousTranslatedValues.Remove(translatedValue);
    }

    private bool TryStoreGeneratedUnsafe(string normalizedTemplate, string translatedTemplate)
    {
        var entryBytes = Utf8Bytes(normalizedTemplate) + Utf8Bytes(translatedTemplate);
        var entryJsonBytes = JsonStringUtf8Bytes(normalizedTemplate) + JsonStringUtf8Bytes(translatedTemplate);
        if (entryBytes > TranslationBudget.MaxGeneratedUtf8Bytes)
        {
            ReportBudgetUnsafe(
                "cache-generated-entry",
                "generated translation exceeded the bounded entry budget; it was left pending.");
            return false;
        }

        var replacing = generated.TryGetValue(normalizedTemplate, out var previousTranslation);
        var previousEntryBytes = replacing
            ? Utf8Bytes(normalizedTemplate) + Utf8Bytes(previousTranslation!)
            : 0;
        var previousEntryJsonBytes = replacing
            ? JsonStringUtf8Bytes(normalizedTemplate) + JsonStringUtf8Bytes(previousTranslation!)
            : 0;
        var candidateCount = generated.Count - (replacing ? 1 : 0) + 1;
        var candidateEntryBytes = generatedUtf8Bytes - previousEntryBytes + entryBytes;
        var candidateEntryJsonBytes = generatedSnapshotJsonUtf8Bytes - previousEntryJsonBytes + entryJsonBytes;
        var evictions = new List<string>();
        var node = generatedLru.First;
        while (candidateCount > TranslationBudget.MaxGeneratedEntries
            || candidateEntryBytes > TranslationBudget.MaxGeneratedUtf8Bytes)
        {
            if (node == null)
                return false;
            var candidate = node.Value;
            node = node.Next;
            if (replacing && string.Equals(candidate, normalizedTemplate, StringComparison.Ordinal))
                continue;

            evictions.Add(candidate);
            candidateCount--;
            candidateEntryBytes -= Utf8Bytes(candidate) + Utf8Bytes(generated[candidate]);
            candidateEntryJsonBytes -= JsonStringUtf8Bytes(candidate) + JsonStringUtf8Bytes(generated[candidate]);
        }

        var pendingRemoval = pendingSet.Contains(normalizedTemplate);
        var candidatePendingCount = pending.Count - (pendingRemoval ? 1 : 0);
        var candidatePendingJsonBytes = pendingSnapshotJsonUtf8Bytes
            - (pendingRemoval ? JsonStringUtf8Bytes(normalizedTemplate) : 0);
        if (!IsDurableStateWithinBudgetUnsafe(
                candidateCount,
                candidateEntryJsonBytes,
                candidatePendingCount,
                candidatePendingJsonBytes,
                raw.Count,
                rawSnapshotJsonUtf8Bytes))
        {
            ReportBudgetUnsafe(
                "cache-snapshot-admission",
                "the complete authoritative cache snapshot would exceed its bounded serialized budget; generated translation was rejected.");
            return false;
        }

        if (replacing)
            RemoveGeneratedUnsafe(normalizedTemplate);
        foreach (var evicted in evictions)
            RemoveGeneratedUnsafe(evicted);

        generated[normalizedTemplate] = translatedTemplate;
        var generatedNode = generatedLru.AddLast(normalizedTemplate);
        generatedNodes[normalizedTemplate] = generatedNode;
        generatedUtf8Bytes += entryBytes;
        generatedSnapshotJsonUtf8Bytes += entryJsonBytes;
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
        generatedSnapshotJsonUtf8Bytes -= JsonStringUtf8Bytes(normalizedTemplate) + JsonStringUtf8Bytes(translated);
        if (generatedUtf8Bytes < 0) generatedUtf8Bytes = 0;
        if (generatedSnapshotJsonUtf8Bytes < 0) generatedSnapshotJsonUtf8Bytes = 0;
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
        var jsonBytes = JsonStringUtf8Bytes(template);
        if (!CanAddPendingCapacityUnsafe(bytes)
            || !IsDurableStateWithinBudgetUnsafe(
                generated.Count,
                generatedSnapshotJsonUtf8Bytes,
                pending.Count + 1,
                pendingSnapshotJsonUtf8Bytes + jsonBytes,
                raw.Count,
                rawSnapshotJsonUtf8Bytes))
            return false;
        AddPendingUnsafe(template, bytes, jsonBytes);
        return true;
    }

    private bool CanAddPendingCapacityUnsafe(int bytes) =>
        pending.Count < TranslationBudget.MaxPendingEntries
        && pendingUtf8Bytes + bytes <= TranslationBudget.MaxPendingUtf8Bytes;

    private void AddPendingUnsafe(string template, int bytes, int jsonBytes)
    {
        pendingSet.Add(template);
        pending.Add(template);
        pendingUtf8Bytes += bytes;
        pendingSnapshotJsonUtf8Bytes += jsonBytes;
    }

    private void RemovePendingUnsafe(string template)
    {
        if (!pendingSet.Remove(template))
            return;
        pendingGenerations.Remove(template);
        pending.RemoveAll(value => string.Equals(value, template, StringComparison.Ordinal));
        pendingUtf8Bytes -= Utf8Bytes(template);
        pendingSnapshotJsonUtf8Bytes -= JsonStringUtf8Bytes(template);
        if (pendingUtf8Bytes < 0) pendingUtf8Bytes = 0;
        if (pendingSnapshotJsonUtf8Bytes < 0) pendingSnapshotJsonUtf8Bytes = 0;
        if (runtimePromotedPending.Remove(template))
            runtimePromotedPendingUtf8Bytes -= Utf8Bytes(template);
    }

    private bool TryAddRawUnsafe(string value)
    {
        var canAdd = CanAddRawUnsafe(value, out var bytes, out var jsonBytes);
        if (!canAdd
            || !IsDurableStateWithinBudgetUnsafe(
                generated.Count,
                generatedSnapshotJsonUtf8Bytes,
                pending.Count,
                pendingSnapshotJsonUtf8Bytes,
                raw.Count + 1,
                rawSnapshotJsonUtf8Bytes + jsonBytes))
            return false;
        AddRawUnsafe(value, bytes, jsonBytes);
        return true;
    }

    private bool CanAddRawUnsafe(string value, out int bytes, out int jsonBytes)
    {
        bytes = Utf8Bytes(value);
        jsonBytes = JsonStringUtf8Bytes(value);
        return !raw.Contains(value)
            && raw.Count < TranslationBudget.MaxRawEntries
            && rawUtf8Bytes + bytes <= TranslationBudget.MaxRawUtf8Bytes;
    }

    private void AddRawUnsafe(string value, int bytes, int jsonBytes)
    {
        raw.Add(value);
        rawUtf8Bytes += bytes;
        rawSnapshotJsonUtf8Bytes += jsonBytes;
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

    private bool IsDurableStateWithinBudgetUnsafe(
        int generatedCount,
        long generatedJsonBytes,
        int pendingCount,
        long pendingJsonBytes,
        int rawCount,
        long rawJsonBytes)
    {
        try
        {
            return EstimateDurableSnapshotUtf8Bytes(
                generatedCount,
                generatedJsonBytes,
                pendingCount,
                pendingJsonBytes,
                rawCount,
                rawJsonBytes) <= TranslationBudget.MaxCacheSnapshotBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long EstimateDurableSnapshotUtf8Bytes(
        int generatedCount,
        long generatedJsonBytes,
        int pendingCount,
        long pendingJsonBytes,
        int rawCount,
        long rawJsonBytes)
    {
        if (generatedCount < 0 || pendingCount < 0 || rawCount < 0
            || generatedJsonBytes < 0 || pendingJsonBytes < 0 || rawJsonBytes < 0)
            return long.MaxValue;

        var total = DurableSnapshotLayout.EmptyBytes;
        total = AddCollectionBytes(
            total,
            generatedCount,
            generatedJsonBytes,
            DurableSnapshotLayout.GeneratedFirstItemOverhead,
            DurableSnapshotLayout.GeneratedAdditionalItemOverhead);
        total = AddCollectionBytes(
            total,
            pendingCount,
            pendingJsonBytes,
            DurableSnapshotLayout.PendingFirstItemOverhead,
            DurableSnapshotLayout.PendingAdditionalItemOverhead);
        return AddCollectionBytes(
            total,
            rawCount,
            rawJsonBytes,
            DurableSnapshotLayout.RawFirstItemOverhead,
            DurableSnapshotLayout.RawAdditionalItemOverhead);
    }

    private static long AddCollectionBytes(
        long total,
        int count,
        long itemBytes,
        long firstItemOverhead,
        long additionalItemOverhead)
    {
        if (count == 0) return total;
        return checked(total
            + itemBytes
            + firstItemOverhead
            + checked((long)(count - 1) * additionalItemOverhead));
    }

    private static SnapshotLayout CreateSnapshotLayout()
    {
        var empty = CreateAdmissionSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<string>(),
            Array.Empty<string>());
        var oneGenerated = CreateAdmissionSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["generated-key"] = "generated-value"
            },
            Array.Empty<string>(),
            Array.Empty<string>());
        var twoGenerated = CreateAdmissionSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["generated-key"] = "generated-value",
                ["generated-key-2"] = "generated-value-2"
            },
            Array.Empty<string>(),
            Array.Empty<string>());
        var onePending = CreateAdmissionSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new[] { "pending-value" },
            Array.Empty<string>());
        var twoPending = CreateAdmissionSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new[] { "pending-value", "pending-value-2" },
            Array.Empty<string>());
        var oneRaw = CreateAdmissionSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<string>(),
            new[] { "raw-value" });
        var twoRaw = CreateAdmissionSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<string>(),
            new[] { "raw-value", "raw-value-2" });

        var emptyBytes = SerializedSnapshotUtf8Bytes(empty);
        return new SnapshotLayout(
            emptyBytes,
            checked(SerializedSnapshotUtf8Bytes(oneGenerated)
                - emptyBytes
                - JsonStringUtf8Bytes("generated-key")
                - JsonStringUtf8Bytes("generated-value")),
            checked(SerializedSnapshotUtf8Bytes(twoGenerated)
                - SerializedSnapshotUtf8Bytes(oneGenerated)
                - JsonStringUtf8Bytes("generated-key-2")
                - JsonStringUtf8Bytes("generated-value-2")),
            checked(SerializedSnapshotUtf8Bytes(onePending)
                - emptyBytes
                - JsonStringUtf8Bytes("pending-value")),
            checked(SerializedSnapshotUtf8Bytes(twoPending)
                - SerializedSnapshotUtf8Bytes(onePending)
                - JsonStringUtf8Bytes("pending-value-2")),
            checked(SerializedSnapshotUtf8Bytes(oneRaw)
                - emptyBytes
                - JsonStringUtf8Bytes("raw-value")),
            checked(SerializedSnapshotUtf8Bytes(twoRaw)
                - SerializedSnapshotUtf8Bytes(oneRaw)
                - JsonStringUtf8Bytes("raw-value-2")));
    }

    private static DurableSnapshot CreateAdmissionSnapshot(
        Dictionary<string, string> generated,
        string[] pending,
        string[] raw) => new()
        {
            Version = 1,
            Epoch = long.MaxValue,
            TransactionId = new string('t', 32),
            Checksum = new string('0', 64),
            Generated = generated,
            Pending = pending,
            Raw = raw
        };

    private static long SerializedSnapshotUtf8Bytes(DurableSnapshot snapshot) =>
        StrictUtf8.GetByteCount(JsonSerializer.Serialize(snapshot, JsonOptions));

    private static int JsonStringUtf8Bytes(string value) =>
        StrictUtf8.GetByteCount(JsonSerializer.Serialize(value, JsonOptions));

    private LoadedSnapshot? LoadDurableSnapshot(out bool hasStateArtifacts)
    {
        hasStateArtifacts = File.Exists(StatePath)
            || File.Exists(StateTemporaryPath)
            || File.Exists(StateBackupPath);
        if (!hasStateArtifacts)
            return null;

        var candidates = new List<SnapshotCandidate>();
        var paths = new[]
        {
            (Path: StatePath, Priority: 0),
            (Path: StateTemporaryPath, Priority: 1),
            (Path: StateBackupPath, Priority: 2)
        };
        foreach (var candidatePath in paths)
        {
            var cleaned = false;
            var text = ReadBoundedText(candidatePath.Path, ref cleaned);
            if (text == null)
                continue;

            try
            {
                var snapshot = JsonSerializer.Deserialize<DurableSnapshot>(text);
                if (!IsValidDurableSnapshot(snapshot))
                {
                    ReportBudget(
                        "cache-state",
                        Path.GetFileName(candidatePath.Path) + " failed cache state integrity/schema validation; trying the next recovery epoch.");
                    continue;
                }

                var generated = FilterGenerated(snapshot!.Generated!, ref cleaned);
                var pending = FilterPending(snapshot.Pending!, ref cleaned);
                var raw = FilterRaw(snapshot.Raw!, ref cleaned);
                // Epoch-zero is the accepted shape of the pre-journal V1 file. Mark it dirty so
                // the next successful commit migrates it to the checksummed journal format. A
                // successfully promoted temp/backup is already authoritative and need not be
                // rewritten merely because its filename was a recovery artifact.
                cleaned |= snapshot.Epoch == 0;
                candidates.Add(new SnapshotCandidate(
                    candidatePath.Path,
                    new LoadedSnapshot(generated, pending, raw, snapshot.Epoch, cleaned),
                    candidatePath.Priority));
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                SafeDiagnostic(candidatePath.Path, exception);
            }
        }

        if (candidates.Count == 0)
        {
            // Do not combine legacy files after a state artifact was observed: that would create a
            // generated/new-pending/raw mixed epoch. The next observable render can safely rebuild
            // missing entries, and the next flush writes one coherent state.
            return new LoadedSnapshot(
                new Dictionary<string, string>(StringComparer.Ordinal),
                Array.Empty<string>(),
                Array.Empty<string>(),
                0,
                true);
        }

        // Epoch is authoritative. Path priority is only a deterministic tie-breaker for exact
        // duplicate epochs, with canonical winning over its journal and backup.
        var selected = candidates
            .OrderByDescending(candidate => candidate.Snapshot.Epoch)
            .ThenBy(candidate => candidate.Priority)
            .First();
        var selectedCleaned = selected.Snapshot.Cleaned;
        var canonicalIsValid = candidates.Any(candidate =>
            string.Equals(candidate.Path, StatePath, StringComparison.Ordinal));
        if (!string.Equals(selected.Path, StatePath, StringComparison.Ordinal)
            && !PromoteRecoveredState(selected.Path, canonicalIsValid))
        {
            selectedCleaned = true;
            ReportPersistenceFailure(
                selected.Path,
                "valid cache recovery epoch could not be promoted; it remains available for the next restart.");
        }

        // A lower/corrupt temp is never allowed to shadow the selected epoch on a later start.
        // Keep the selected temp if promotion failed; otherwise it is safe to remove.
        if (!string.Equals(selected.Path, StateTemporaryPath, StringComparison.Ordinal)
            && File.Exists(StateTemporaryPath))
            TryDeleteRecoveryTemporary();

        return selected.Snapshot with { Cleaned = selectedCleaned };
    }

    private bool IsValidDurableSnapshot(DurableSnapshot? snapshot)
    {
        if (snapshot?.Version != 1
            || snapshot.Generated == null
            || snapshot.Pending == null
            || snapshot.Raw == null
            || snapshot.Epoch < 0)
            return false;

        // A V1 snapshot written before the journal protocol has no epoch or metadata and is
        // deterministically treated as epoch zero. New snapshots must carry all transaction
        // metadata and a checksum; accepting only one field would make torn metadata valid.
        if (snapshot.Epoch == 0)
            return snapshot.TransactionId == null && snapshot.Checksum == null;
        if (string.IsNullOrWhiteSpace(snapshot.TransactionId)
            || snapshot.TransactionId.Length > 128
            || string.IsNullOrWhiteSpace(snapshot.Checksum))
            return false;

        try
        {
            var expected = ComputeChecksum(snapshot);
            return string.Equals(expected, snapshot.Checksum, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string ComputeChecksum(DurableSnapshot snapshot)
    {
        if (snapshot.Generated == null || snapshot.Pending == null || snapshot.Raw == null)
            throw new ArgumentException("snapshot payload is incomplete", nameof(snapshot));

        var generated = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Generated)
        {
            if (entry.Key == null || entry.Value == null)
                throw new ArgumentException("snapshot payload contains null generated text", nameof(snapshot));
            generated.Add(entry.Key, entry.Value);
        }

        if (snapshot.Pending.Any(value => value == null)
            || snapshot.Raw.Any(value => value == null))
            throw new ArgumentException("snapshot payload contains null mirror text", nameof(snapshot));

        var payload = new SnapshotIntegrityPayload
        {
            Version = snapshot.Version,
            Epoch = snapshot.Epoch,
            TransactionId = snapshot.TransactionId ?? string.Empty,
            Generated = generated,
            Pending = snapshot.Pending,
            Raw = snapshot.Raw
        };
        var canonical = JsonSerializer.Serialize(payload, IntegrityJsonOptions);
        return Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(canonical)));
    }

    private bool PromoteRecoveredState(string sourcePath, bool canonicalIsValid)
    {
        if (string.Equals(sourcePath, StatePath, StringComparison.Ordinal))
            return true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            if (string.Equals(sourcePath, StateTemporaryPath, StringComparison.Ordinal))
            {
                // Preserve the previous canonical only when it was itself a valid candidate. A
                // corrupt canonical must not destroy a usable backup during recovery.
                if (canonicalIsValid && File.Exists(StatePath))
                    File.Copy(StatePath, StateBackupPath, true);
                File.Move(StateTemporaryPath, StatePath, true);
                return true;
            }

            if (!string.Equals(sourcePath, StateBackupPath, StringComparison.Ordinal))
                return false;
            // Copy backup to the journal first, then replace canonical. The backup remains in
            // place as a second recovery candidate if the replacement is interrupted.
            File.Copy(StateBackupPath, StateTemporaryPath, true);
            File.Move(StateTemporaryPath, StatePath, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SafeDiagnostic(sourcePath, exception);
            return false;
        }
    }

    private void TryDeleteRecoveryTemporary()
    {
        try
        {
            File.Delete(StateTemporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SafeDiagnostic(StateTemporaryPath, exception);
        }
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

    private void ReportPersistenceFailure(string path, string message)
    {
        try
        {
            diagnostic?.Invoke(Path.GetFileName(path) + ": " + message);
        }
        catch
        {
        }
    }

    private void SafeDiagnostic(string path, Exception exception) =>
        ReportPersistenceFailure(path, exception.GetType().Name);
}
