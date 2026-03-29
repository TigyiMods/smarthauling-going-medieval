namespace SmartHauling.Runtime.Tests;

public sealed class InvalidPickupRecoveryTests
{
    [Fact]
    public void CreatePlan_WhenCurrentTargetMatchesQueuedPile_ReleasesCurrentAndRemovesMatchingQueueEntry()
    {
        // Arrange
        var first = new object();
        var second = new object();

        // Act
        var plan = InvalidPickupRecovery.CreatePlan(
            hasCurrentTarget: true,
            currentTarget: second,
            queuedTargets: new[] { first, second });

        // Assert
        Assert.True(plan.ReleaseCurrentTarget);
        Assert.Equal(1, plan.QueueIndexToDrop);
        Assert.True(plan.HasAnyAction);
    }

    [Fact]
    public void CreatePlan_WhenCurrentTargetIsStaleButQueueHasEntries_DropsQueueHead()
    {
        // Arrange
        var stale = new object();
        var queued = new object();

        // Act
        var plan = InvalidPickupRecovery.CreatePlan(
            hasCurrentTarget: true,
            currentTarget: stale,
            queuedTargets: new[] { queued });

        // Assert
        Assert.True(plan.ReleaseCurrentTarget);
        Assert.Equal(0, plan.QueueIndexToDrop);
        Assert.True(plan.HasAnyAction);
    }

    [Fact]
    public void CreatePlan_WhenOnlyQueueHeadExists_DropsQueueHeadWithoutCurrentRelease()
    {
        // Arrange
        var queued = new object();

        // Act
        var plan = InvalidPickupRecovery.CreatePlan(
            hasCurrentTarget: false,
            currentTarget: null,
            queuedTargets: new[] { queued });

        // Assert
        Assert.False(plan.ReleaseCurrentTarget);
        Assert.Equal(0, plan.QueueIndexToDrop);
        Assert.True(plan.HasAnyAction);
    }
}
