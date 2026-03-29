using NSMedieval;
using NSMedieval.State;

namespace SmartHauling.Runtime.Tests;

public sealed class StorageCandidateOrderingTests
{
    [Fact]
    public void OrderCandidates_PrefersHigherPriorityBeforeHigherCapacity()
    {
        // Arrange
        var mediumStorage = CreateStorage(ZonePriority.Medium);
        var highStorage = CreateStorage(ZonePriority.High);
        var candidates = new[]
        {
            new StorageCandidatePlanner.StorageCandidate(
                mediumStorage,
                estimatedCapacity: 40,
                distance: 12f,
                fitRatio: 1f,
                preferredOrderRank: int.MaxValue,
                position: null,
                leasedAmount: 0),
            new StorageCandidatePlanner.StorageCandidate(
                highStorage,
                estimatedCapacity: 10,
                distance: 12f,
                fitRatio: 1f,
                preferredOrderRank: int.MaxValue,
                position: null,
                leasedAmount: 0)
        };

        // Act
        var ordered = StorageCandidateOrdering.OrderCandidates(
            candidates,
            preferredStorage: null);

        // Assert
        Assert.Same(highStorage, ordered[0].Storage);
        Assert.Same(mediumStorage, ordered[1].Storage);
    }

    [Fact]
    public void OrderCandidates_WhenPreferredOrderConflicts_PrefersHigherPriorityFirst()
    {
        // Arrange
        var lowStorage = CreateStorage(ZonePriority.Low);
        var highStorage = CreateStorage(ZonePriority.High);
        var candidates = new[]
        {
            new StorageCandidatePlanner.StorageCandidate(
                lowStorage,
                estimatedCapacity: 40,
                distance: 12f,
                fitRatio: 1f,
                preferredOrderRank: 0,
                position: null,
                leasedAmount: 0),
            new StorageCandidatePlanner.StorageCandidate(
                highStorage,
                estimatedCapacity: 10,
                distance: 12f,
                fitRatio: 1f,
                preferredOrderRank: 1,
                position: null,
                leasedAmount: 0)
        };

        // Act
        var ordered = StorageCandidateOrdering.OrderCandidates(
            candidates,
            preferredStorage: lowStorage);

        // Assert
        Assert.Same(highStorage, ordered[0].Storage);
        Assert.Same(lowStorage, ordered[1].Storage);
    }

    private static IStorage CreateStorage(ZonePriority priority)
    {
        var storage = A.Fake<IStorage>();
        A.CallTo(() => storage.Priority).Returns(priority);
        return storage;
    }
}
