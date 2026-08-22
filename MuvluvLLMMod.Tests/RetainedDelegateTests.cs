using Xunit;

namespace MuvluvLLMMod.Tests;

public sealed class RetainedDelegateTests
{
    private sealed class DelegateToken
    {
    }

    [Fact]
    public void Conversion_happens_once_and_remove_uses_that_same_reference()
    {
        var retained = new RetainedDelegate<DelegateToken>();
        var conversions = 0;
        var first = retained.GetOrCreate(() =>
        {
            conversions++;
            return new DelegateToken();
        });
        var second = retained.GetOrCreate(() =>
        {
            conversions++;
            return new DelegateToken();
        });

        Assert.Same(first, second);
        Assert.Equal(1, conversions);

        DelegateToken? removed = null;
        Assert.False(retained.TryRemove(candidate =>
        {
            removed = candidate;
            Assert.Same(candidate, retained.Current);
            return false;
        }));
        Assert.Same(first, removed);
        Assert.Same(first, retained.Current);

        Assert.True(retained.TryRemove(candidate =>
        {
            removed = candidate;
            Assert.Same(candidate, retained.Current);
            return true;
        }));
        Assert.Same(first, removed);
        Assert.Null(retained.Current);
    }

    [Fact]
    public void Failed_removal_retains_the_delegate_for_a_later_retry()
    {
        var retained = new RetainedDelegate<DelegateToken>();
        var original = retained.GetOrCreate(static () => new DelegateToken());

        Assert.Throws<InvalidOperationException>(() => retained.TryRemove(candidate =>
        {
            Assert.Same(original, candidate);
            throw new InvalidOperationException("native removal failed");
        }));
        Assert.Same(original, retained.Current);

        DelegateToken? retried = null;
        Assert.True(retained.TryRemove(candidate =>
        {
            retried = candidate;
            return true;
        }));
        Assert.Same(original, retried);
        Assert.Null(retained.Current);
    }
}
