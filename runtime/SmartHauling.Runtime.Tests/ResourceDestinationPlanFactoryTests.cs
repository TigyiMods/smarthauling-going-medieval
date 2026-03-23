using SmartHauling.Runtime.Patches;

namespace SmartHauling.Runtime.Tests;

public sealed class ResourceDestinationPlanFactoryTests
{
    [Fact]
    public void OrderResourceIds_WhenPrimaryResourceIsPresent_PutsPrimaryFirstThenSortsByRequestedAmount()
    {
        // Arrange
        var requestedAmountByResourceId = new Dictionary<string, int>
        {
            ["wood"] = 90,
            ["hay"] = 120,
            ["packaged_meal"] = 10
        };

        // Act
        var orderedResourceIds = ResourceDestinationPlanFactory.OrderResourceIds(
            requestedAmountByResourceId,
            primaryResourceId: "packaged_meal");

        // Assert
        Assert.Equal(new[] { "packaged_meal", "hay", "wood" }, orderedResourceIds);
    }

    [Fact]
    public void Describe_WhenPlansAreEmptyAndResourcesWerePruned_DescribesBothConditions()
    {
        // Arrange
        var build = new ResourceDestinationBuild(
            resourcePlans: Array.Empty<StockpileDestinationResourcePlan>(),
            candidatePlans: Array.Empty<StorageCandidatePlanner.StorageCandidatePlan>(),
            unsupportedResourceIds: Array.Empty<string>(),
            requestedAmountByResourceId: new Dictionary<string, int>());

        // Act
        var description = ResourceDestinationPlanFactory.Describe(build, new[] { "wood", "hay" });

        // Assert
        Assert.Contains("plans=[]", description);
        Assert.Contains("pruned=[wood, hay]", description);
    }
}
