using SmartHauling.Runtime;
using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class StockpileSourceSlicePlannerTests
{
    [Fact]
    public void GetNominalSourceSliceWeightBudget_UsesWorkerBudgetForNormalItems()
    {
        var budget = StockpileSourceSlicePlanner.GetNominalSourceSliceWeightBudget(
            nominalWorkerFreeSpace: 90f,
            minimumSourceSliceWeightBudget: 32f,
            treatAsSingleHeavyItem: false,
            sampleWeight: 5f);

        Assert.Equal(90f, budget, 3);
    }

    [Fact]
    public void GetNominalSourceSliceWeightBudget_UsesMinimumForSmallWorkerBudget()
    {
        var budget = StockpileSourceSlicePlanner.GetNominalSourceSliceWeightBudget(
            nominalWorkerFreeSpace: 10f,
            minimumSourceSliceWeightBudget: 32f,
            treatAsSingleHeavyItem: false,
            sampleWeight: 5f);

        Assert.Equal(32f, budget, 3);
    }

    [Fact]
    public void GetNominalSourceSliceWeightBudget_UsesSingleHeavyWeightForHeavyItems()
    {
        var budget = StockpileSourceSlicePlanner.GetNominalSourceSliceWeightBudget(
            nominalWorkerFreeSpace: 90f,
            minimumSourceSliceWeightBudget: 32f,
            treatAsSingleHeavyItem: true,
            sampleWeight: 7f);

        Assert.Equal(7f, budget, 3);
    }

    [Fact]
    public void GetNominalPileWeight_UsesAmountForNormalItems()
    {
        var weight = StockpileSourceSlicePlanner.GetNominalPileWeight(
            hasBlueprint: true,
            treatAsSingleHeavyItem: false,
            unitWeight: 0.5f,
            amount: 10);

        Assert.Equal(5f, weight, 3);
    }

    [Fact]
    public void GetNominalPileWeight_UsesUnitWeightForHeavyItems()
    {
        var weight = StockpileSourceSlicePlanner.GetNominalPileWeight(
            hasBlueprint: true,
            treatAsSingleHeavyItem: true,
            unitWeight: 12f,
            amount: 10);

        Assert.Equal(12f, weight, 3);
    }

    [Fact]
    public void BuildSlice_PrefersFirstThenNearestThenLargerAmount()
    {
        var slice = StockpileSourceSlicePlanner.BuildSlice(
            firstId: "first",
            firstPosition: new Vector3(0f, 0f, 0f),
            candidates: new[]
            {
                new SourceSliceCandidate<string>("far", new Vector3(10f, 0f, 0f), 20, 10f),
                new SourceSliceCandidate<string>("first", new Vector3(0f, 0f, 0f), 5, 10f),
                new SourceSliceCandidate<string>("near-small", new Vector3(2f, 0f, 0f), 5, 10f),
                new SourceSliceCandidate<string>("near-large", new Vector3(2f, 0f, 0f), 10, 10f)
            },
            sliceBudgetWeight: 25f);

        Assert.Equal(new[] { "first", "near-large", "near-small" }, slice);
    }

    [Fact]
    public void BuildSlice_IncludesPartialLastPileInsteadOfStoppingEarly()
    {
        var slice = StockpileSourceSlicePlanner.BuildSlice(
            firstId: "first",
            firstPosition: new Vector3(0f, 0f, 0f),
            candidates: new[]
            {
                new SourceSliceCandidate<string>("first", new Vector3(0f, 0f, 0f), 2, 10f),
                new SourceSliceCandidate<string>("big", new Vector3(1f, 0f, 0f), 200, 100f)
            },
            sliceBudgetWeight: 50f);

        Assert.Equal(new[] { "first", "big" }, slice);
    }

    [Fact]
    public void BuildSlice_FallsBackToFirstWhenNoValidCandidateWeight()
    {
        var slice = StockpileSourceSlicePlanner.BuildSlice(
            firstId: "first",
            firstPosition: new Vector3(0f, 0f, 0f),
            candidates: new[]
            {
                new SourceSliceCandidate<string>("first", new Vector3(0f, 0f, 0f), 0, 0f)
            },
            sliceBudgetWeight: 50f);

        Assert.Equal(new[] { "first" }, slice);
    }
}
