namespace MuvluvLLMMod;

/// <summary>
/// Records which scenes have already had their frame documents rewritten, so a re-entered scene
/// does not rebuild and resend the same batch.
///
/// The reverse index in <see cref="TranslationCache"/> is the primary anti-re-entrancy defense,
/// but it is bounded (<see cref="TranslationBudget.MaxReverseEntries"/> covers only a fraction of
/// the corpus) and therefore evicts. This marker is the bounded scene-level backstop for that
/// lossy eviction: it is small, counts generations, and holds no text.
///
/// Pure logic: no game types, so the loader-free test project covers it.
/// </summary>
public sealed class SceneTranslationMarker
{
    private readonly object gate = new();
    private readonly Dictionary<long, long> appliedByScene = new();
    private readonly Queue<long> order = new();
    private readonly int capacity;

    public SceneTranslationMarker(int capacity)
    {
        this.capacity = Math.Clamp(capacity, 1, TranslationBudget.MaxAppliedSceneEntries);
    }

    public int Count
    {
        get { lock (gate) return appliedByScene.Count; }
    }

    /// <summary>
    /// True when this exact scene generation has already been rewritten. A different generation
    /// for the same scene id is treated as new, because the frame documents may have been
    /// re-fetched with different content.
    /// </summary>
    public bool IsApplied(long sceneId, long generation)
    {
        lock (gate)
            return appliedByScene.TryGetValue(sceneId, out var applied) && applied == generation;
    }

    public void MarkApplied(long sceneId, long generation)
    {
        lock (gate)
        {
            if (!appliedByScene.ContainsKey(sceneId))
            {
                while (order.Count >= capacity)
                {
                    var evicted = order.Dequeue();
                    appliedByScene.Remove(evicted);
                }

                order.Enqueue(sceneId);
            }

            appliedByScene[sceneId] = generation;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            appliedByScene.Clear();
            order.Clear();
        }
    }
}
