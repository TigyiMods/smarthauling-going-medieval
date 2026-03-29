using NSMedieval;
using NSMedieval.State;

namespace SmartHauling.Runtime.Tests;

public sealed class StorageAllocationPlanBuilderTests
{
    [Fact]
    public void BuildFromCandidates_WhenRequestedAmountExceedsFirstCapacity_SplitsAcrossStoragesInOrder()
    {
        // Arrange
        var firstStorage = CreateStorage(ZonePriority.High);
        var secondStorage = CreateStorage(ZonePriority.High);
        var candidates = new[]
        {
            new StorageCandidatePlanner.StorageCandidate(
                firstStorage,
                estimatedCapacity: 2,
                distance: 2f,
                fitRatio: 0.2f,
                preferredOrderRank: 0,
                position: null,
                leasedAmount: 0),
            new StorageCandidatePlanner.StorageCandidate(
                secondStorage,
                estimatedCapacity: 50,
                distance: 3f,
                fitRatio: 1f,
                preferredOrderRank: 1,
                position: null,
                leasedAmount: 0)
        };

        // Act
        var allocations = StorageAllocationPlanBuilder.BuildFromCandidates(candidates, requestedAmount: 52);

        // Assert
        Assert.Collection(
            allocations,
            allocation =>
            {
                Assert.Same(firstStorage, allocation.Storage);
                Assert.Equal(2, allocation.RequestedAmount);
            },
            allocation =>
            {
                Assert.Same(secondStorage, allocation.Storage);
                Assert.Equal(50, allocation.RequestedAmount);
            });
    }

    [Fact]
    public void MergeAllocations_WhenStorageRepeats_MergesAmountsWithoutChangingFirstSeenOrder()
    {
        // Arrange
        var firstStorage = CreateStorage(ZonePriority.Medium);
        var secondStorage = CreateStorage(ZonePriority.High);
        var allocations = new[]
        {
            new StockpileStorageAllocation(firstStorage, 2),
            new StockpileStorageAllocation(secondStorage, 10),
            new StockpileStorageAllocation(firstStorage, 5)
        };

        // Act
        var merged = StorageAllocationPlanBuilder.MergeAllocations(allocations);

        // Assert
        Assert.Collection(
            merged,
            allocation =>
            {
                Assert.Same(firstStorage, allocation.Storage);
                Assert.Equal(7, allocation.RequestedAmount);
            },
            allocation =>
            {
                Assert.Same(secondStorage, allocation.Storage);
                Assert.Equal(10, allocation.RequestedAmount);
            });
    }

    [Fact]
    public void ResourcePlan_WhenAllocationsArePresent_UsesAllocationOrderForActiveStorages()
    {
        // Arrange
        var firstStorage = CreateStorage(ZonePriority.Medium);
        var secondStorage = CreateStorage(ZonePriority.High);
        var plan = new StockpileDestinationResourcePlan(
            resourceId: "wood",
            orderedStorages: new[] { secondStorage, firstStorage },
            requestedAmount: 52,
            plannedAllocations: new[]
            {
                new StockpileStorageAllocation(firstStorage, 2),
                new StockpileStorageAllocation(secondStorage, 50)
            });

        // Act
        var activeStorages = plan.GetActiveStorages();

        // Assert
        Assert.Equal(new[] { firstStorage, secondStorage }, activeStorages);
    }

    private static IStorage CreateStorage(ZonePriority priority)
    {
        var storage = A.Fake<IStorage>();
        A.CallTo(() => storage.Priority).Returns(priority);
        A.CallTo(() => storage.HasDisposed).Returns(false);
        return storage;
    }
}
