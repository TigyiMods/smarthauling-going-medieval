using System.Runtime.CompilerServices;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using SmartHauling.Runtime.Patches;

namespace SmartHauling.Runtime.Tests;

public sealed class ProductionDeliveryRoutingTests
{
    [Fact]
    public void ShouldUseSmartProductionDelivery_ReturnsFalse_WhenGoalHasNoPlan()
    {
        var goal = CreateGoal();

        Assert.False(ResourceActionPatch.ShouldUseSmartProductionDelivery(goal));
    }

    [Fact]
    public void ShouldUseSmartProductionDelivery_ReturnsFalse_WhenPlanStartsSingleResource()
    {
        var goal = CreateGoal();
        MixedCollectPlanStore.Set(goal, new Dictionary<string, int>
        {
            ["sticks"] = 3
        });

        Assert.False(ResourceActionPatch.ShouldUseSmartProductionDelivery(goal));
    }

    [Fact]
    public void ShouldUseSmartProductionDelivery_RemainsTrue_AfterMixedPlanShrinksToSingleResource()
    {
        var goal = CreateGoal();
        MixedCollectPlanStore.Set(goal, new Dictionary<string, int>
        {
            ["sticks"] = 1,
            ["cabbage"] = 2
        });

        Assert.True(MixedCollectPlanStore.TryGet(goal, out var plan));
        plan.RequestedByResourceId.Remove("sticks");

        Assert.True(ResourceActionPatch.ShouldUseSmartProductionDelivery(goal));
    }

    private static Goal CreateGoal()
    {
        return (Goal)RuntimeHelpers.GetUninitializedObject(typeof(StockpileHaulingGoal));
    }
}
