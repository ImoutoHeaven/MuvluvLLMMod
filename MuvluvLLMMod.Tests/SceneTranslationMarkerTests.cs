using Xunit;

namespace MuvluvLLMMod.Tests;

/// <summary>
/// Scene rewrite markers. This is the bounded backstop for reverse-index eviction, so the
/// contract is: exact generation matches are suppressed, other generations are not, and the
/// retained set never exceeds its capacity.
/// </summary>
public sealed class SceneTranslationMarkerTests
{
    [Fact]
    public void IsApplied_is_false_before_marking()
    {
        var marker = new SceneTranslationMarker(8);

        Assert.False(marker.IsApplied(100, 1));
        Assert.Equal(0, marker.Count);
    }

    [Fact]
    public void IsApplied_is_true_for_the_same_generation()
    {
        var marker = new SceneTranslationMarker(8);
        marker.MarkApplied(100, 1);

        Assert.True(marker.IsApplied(100, 1));
    }

    [Fact]
    public void IsApplied_is_false_for_a_newer_generation_of_the_same_scene()
    {
        var marker = new SceneTranslationMarker(8);
        marker.MarkApplied(100, 1);

        // A re-fetched scene may carry different frame documents, so it must be reprocessed.
        Assert.False(marker.IsApplied(100, 2));
    }

    [Fact]
    public void IsApplied_is_false_for_an_unrelated_scene()
    {
        var marker = new SceneTranslationMarker(8);
        marker.MarkApplied(100, 1);

        Assert.False(marker.IsApplied(101, 1));
    }

    [Fact]
    public void MarkApplied_twice_for_the_same_scene_updates_the_generation()
    {
        var marker = new SceneTranslationMarker(8);
        marker.MarkApplied(100, 1);
        marker.MarkApplied(100, 2);

        Assert.False(marker.IsApplied(100, 1));
        Assert.True(marker.IsApplied(100, 2));
        Assert.Equal(1, marker.Count);
    }

    [Fact]
    public void Retained_set_never_exceeds_capacity()
    {
        var marker = new SceneTranslationMarker(4);
        for (var sceneId = 0; sceneId < 100; sceneId++)
            marker.MarkApplied(sceneId, 1);

        Assert.Equal(4, marker.Count);
        Assert.True(marker.IsApplied(99, 1));
        Assert.False(marker.IsApplied(0, 1));
    }

    [Fact]
    public void Clear_drops_every_marker()
    {
        var marker = new SceneTranslationMarker(8);
        marker.MarkApplied(100, 1);
        marker.Clear();

        Assert.Equal(0, marker.Count);
        Assert.False(marker.IsApplied(100, 1));
    }

    [Fact]
    public void Capacity_is_clamped_to_the_bounded_maximum()
    {
        var marker = new SceneTranslationMarker(int.MaxValue);
        for (var sceneId = 0; sceneId < TranslationBudget.MaxAppliedSceneEntries + 64; sceneId++)
            marker.MarkApplied(sceneId, 1);

        Assert.Equal(TranslationBudget.MaxAppliedSceneEntries, marker.Count);
    }

    [Fact]
    public void Capacity_is_at_least_one()
    {
        var marker = new SceneTranslationMarker(0);
        marker.MarkApplied(100, 1);

        Assert.Equal(1, marker.Count);
    }

    [Fact]
    public void Concurrent_marking_keeps_the_retained_set_bounded()
    {
        var marker = new SceneTranslationMarker(64);
        Parallel.For(0, 2000, sceneId => marker.MarkApplied(sceneId, 1));

        Assert.True(marker.Count <= 64);
    }
}
