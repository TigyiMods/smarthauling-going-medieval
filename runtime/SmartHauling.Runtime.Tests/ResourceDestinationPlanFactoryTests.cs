using SmartHauling.Runtime;
using SmartHauling.Runtime.Patches;

namespace SmartHauling.Runtime.Tests;

public sealed class ResourceDestinationPlanFactoryTests
{
    [Fact]
    public void OrderResourceIds_PutsPrimaryFirstThenSortsByRequestedAmount()
    {
        var ordered = ResourceDestinationPlanFactory.OrderResourceIds(
            new Dictionary<string, int>
            {
                ["wood"] = 90,
                ["hay"] = 120,
                ["packaged_meal"] = 10
            },
            primaryResourceId: "packaged_meal");

        Assert.Equal(new[] { "packaged_meal", "hay", "wood" }, ordered);
    }

    [Fact]
    public void Describe_HandlesEmptyPlansAndPrunedResources()
    {
        var build = new ResourceDestinationBuild(
            resourcePlans: Array.Empty<StockpileDestinationResourcePlan>(),
            candidatePlans: Array.Empty<StorageCandidatePlanner.StorageCandidatePlan>(),
            unsupportedResourceIds: Array.Empty<string>(),
            requestedAmountByResourceId: new Dictionary<string, int>());

        var description = ResourceDestinationPlanFactory.Describe(build, new[] { "wood", "hay" });

        Assert.Contains("plans=[]", description);
        Assert.Contains("pruned=[wood, hay]", description);
    }
}
