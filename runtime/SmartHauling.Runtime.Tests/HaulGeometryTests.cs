using SmartHauling.Runtime;
using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class HaulGeometryTests
{
    [Fact]
    public void GetNearestPatchDistance_ReturnsSmallestDistance()
    {
        var distance = HaulGeometry.GetNearestPatchDistance(
            new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(10f, 0f, 0f),
                new Vector3(3f, 4f, 0f)
            },
            new Vector3(3f, 0f, 0f));

        Assert.Equal(3f, distance, 3);
    }

    [Fact]
    public void GetPatchExtent_UsesFarthestPointAndMinimumOne()
    {
        var extent = HaulGeometry.GetPatchExtent(
            new Vector3(0f, 0f, 0f),
            new[]
            {
                new Vector3(0.1f, 0f, 0f),
                new Vector3(5f, 0f, 0f)
            });

        Assert.Equal(5f, extent, 3);

        var minExtent = HaulGeometry.GetPatchExtent(
            new Vector3(0f, 0f, 0f),
            new[] { new Vector3(0f, 0f, 0f) });

        Assert.Equal(1f, minExtent, 3);
    }

    [Fact]
    public void GetDetourBudget_UsesClusterExtentWhenNoTargetDistance()
    {
        var budget = HaulGeometry.GetDetourBudget(
            sourceToTargetDistance: 0f,
            sourceClusterExtent: 12f,
            detourBudgetMultiplier: 0.65f,
            minimumDetourBudget: 8f,
            maximumDetourBudget: 48f);

        Assert.Equal(12f, budget, 3);
    }

    [Fact]
    public void GetDetourBudget_ClampsScaledDistance()
    {
        var low = HaulGeometry.GetDetourBudget(5f, 12f, 0.65f, 8f, 48f);
        var high = HaulGeometry.GetDetourBudget(200f, 12f, 0.65f, 8f, 48f);
        var middle = HaulGeometry.GetDetourBudget(40f, 12f, 0.65f, 8f, 48f);

        Assert.Equal(8f, low, 3);
        Assert.Equal(48f, high, 3);
        Assert.Equal(26f, middle, 3);
    }

    [Fact]
    public void ShouldUsePatchSweep_ForcesSaplingAndSupportsThresholdRule()
    {
        Assert.True(HaulGeometry.ShouldUsePatchSweep("oak_sapling", 100, 1, 25, 3));
        Assert.True(HaulGeometry.ShouldUsePatchSweep("wood", 20, 4, 25, 3));
        Assert.False(HaulGeometry.ShouldUsePatchSweep("wood", 40, 4, 25, 3));
        Assert.False(HaulGeometry.ShouldUsePatchSweep("wood", 20, 2, 25, 3));
    }

    [Fact]
    public void GetAdditionalDetour_ReturnsExtraPathOverDirectRoute()
    {
        var detour = HaulGeometry.GetAdditionalDetour(
            new Vector3(0f, 0f, 0f),
            new Vector3(3f, 4f, 0f),
            new Vector3(10f, 0f, 0f));

        Assert.Equal(3.062258f, detour, 3);
    }

    [Fact]
    public void IsRouteWorthwhile_UsesClusterExtentWithoutTarget()
    {
        var worthwhile = HaulGeometry.IsRouteWorthwhile(
            new Vector3(0f, 0f, 0f),
            new Vector3(5f, 0f, 0f),
            targetPosition: null,
            detourBudget: 20f,
            sourceClusterExtent: 6f,
            out var detourCost);

        Assert.True(worthwhile);
        Assert.Equal(5f, detourCost, 3);
    }

    [Fact]
    public void IsRouteWorthwhile_UsesDetourBudgetWithTarget()
    {
        var worthwhile = HaulGeometry.IsRouteWorthwhile(
            new Vector3(0f, 0f, 0f),
            new Vector3(3f, 4f, 0f),
            new Vector3(10f, 0f, 0f),
            detourBudget: 4f,
            sourceClusterExtent: 6f,
            out var detourCost);

        Assert.True(worthwhile);
        Assert.Equal(3.062258f, detourCost, 3);

        var tooFar = HaulGeometry.IsRouteWorthwhile(
            new Vector3(0f, 0f, 0f),
            new Vector3(3f, 4f, 0f),
            new Vector3(10f, 0f, 0f),
            detourBudget: 2f,
            sourceClusterExtent: 6f,
            out _);

        Assert.False(tooFar);
    }
}
