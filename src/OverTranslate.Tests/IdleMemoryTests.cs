using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class IdleMemoryTests
{
    [Fact]
    public void CollectionWaitsOnlyWhileSomethingIsOnScreen()
    {
        Assert.False(IdleMemory.ShouldWait(null));
        Assert.False(IdleMemory.ShouldWait(() => false));
        Assert.True(IdleMemory.ShouldWait(() => true));
    }

    [Fact]
    public void AQuestionThatThrowsDoesNotHoldTheCollectionForever()
    {
        Assert.False(IdleMemory.ShouldWait(() => throw new InvalidOperationException()));
    }

    [Fact]
    public void TheWaitIsLongEnoughToStayOutOfABurstOfCaptures()
    {
        // A blocking collection stops the interface thread too; five seconds put it on top of the
        // next capture often enough to be felt.
        Assert.True(IdleMemory.Delay >= TimeSpan.FromSeconds(30));
    }
}
