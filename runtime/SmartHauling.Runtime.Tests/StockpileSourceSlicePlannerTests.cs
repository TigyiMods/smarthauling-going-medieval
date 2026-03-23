using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class StockpileSourceSlicePlannerTests
{
    [Fact]
    public void GetNominalSourceSliceWeightBudget_WhenWorkerBudgetIsLarge_ReturnsWorkerBudget()
    {
        // Arrange
        const float nominalWorkerFreeSpace = 90f;

        // Act
        var budget = StockpileSourceSlicePlanner.GetNominalSourceSliceWeightBudget(
            nominalWorkerFreeSpace,
            minimumSourceSliceWeightBudget: 32f,
            treatAsSingleHeavyItem: false,
            sampleWeight: 5f);

        // Assert
        Assert.Equal(90f, budget, 3);
    }

    [Fact]
    public void GetNominalSourceSliceWeightBudget_WhenWorkerBudgetIsSmall_ReturnsMinimumBudget()
    {
        // Arrange
        const float nominalWorkerFreeSpace = 10f;

        // Act
        var budget = StockpileSourceSlicePlanner.GetNominalSourceSliceWeightBudget(
            nominalWorkerFreeSpace,
            minimumSourceSliceWeightBudget: 32f,
            treatAsSingleHeavyItem: false,
            sampleWeight: 5f);

        // Assert
        Assert.Equal(32f, budget, 3);
    }

    [Fact]
    public void GetNominalSourceSliceWeightBudget_WhenItemIsSingleHeavy_UsesSingleItemWeight()
    {
        // Arrange
        const float sampleWeight = 7f;

        // Act
        var budget = StockpileSourceSlicePlanner.GetNominalSourceSliceWeightBudget(
            nominalWorkerFreeSpace: 90f,
            minimumSourceSliceWeightBudget: 32f,
            treatAsSingleHeavyItem: true,
            sampleWeight: sampleWeight);

        // Assert
        Assert.Equal(sampleWeight, budget, 3);
    }

    [Fact]
    public void GetNominalPileWeight_WhenItemIsNormal_UsesAmountTimesUnitWeight()
    {
        // Arrange
        const float unitWeight = 0.5f;
        const int amount = 10;

        // Act
        var weight = StockpileSourceSlicePlanner.GetNominalPileWeight(
            hasBlueprint: true,
            treatAsSingleHeavyItem: false,
            unitWeight: unitWeight,
            amount: amount);

        // Assert
        Assert.Equal(5f, weight, 3);
    }

    [Fact]
    public void GetNominalPileWeight_WhenItemIsSingleHeavy_UsesUnitWeightOnly()
    {
        // Arrange
        const float unitWeight = 12f;

        // Act
        var weight = StockpileSourceSlicePlanner.GetNominalPileWeight(
            hasBlueprint: true,
            treatAsSingleHeavyItem: true,
            unitWeight: unitWeight,
            amount: 10);

        // Assert
        Assert.Equal(unitWeight, weight, 3);
    }

    [Fact]
    public void BuildSlice_WhenCandidatesShareDistance_PrefersFirstThenNearestThenLargerAmount()
    {
        // Arrange
        var candidates = new[]
        {
            new SourceSliceCandidate<string>("far", new Vector3(10f, 0f, 0f), 20, 10f),
            new SourceSliceCandidate<string>("first", new Vector3(0f, 0f, 0f), 5, 10f),
            new SourceSliceCandidate<string>("near-small", new Vector3(2f, 0f, 0f), 5, 10f),
            new SourceSliceCandidate<string>("near-large", new Vector3(2f, 0f, 0f), 10, 10f)
        };

        // Act
        var slice = StockpileSourceSlicePlanner.BuildSlice(
            firstId: "first",
            firstPosition: new Vector3(0f, 0f, 0f),
            candidates: candidates,
            sliceBudgetWeight: 25f);

        // Assert
        Assert.Equal(new[] { "first", "near-large", "near-small" }, slice);
    }

    [Fact]
    public void BuildSlice_WhenLastPileOnlyPartiallyFits_KeepsThatPileInsteadOfStoppingEarly()
    {
        // Arrange
        var candidates = new[]
        {
            new SourceSliceCandidate<string>("first", new Vector3(0f, 0f, 0f), 2, 10f),
            new SourceSliceCandidate<string>("big", new Vector3(1f, 0f, 0f), 200, 100f)
        };

        // Act
        var slice = StockpileSourceSlicePlanner.BuildSlice(
            firstId: "first",
            firstPosition: new Vector3(0f, 0f, 0f),
            candidates: candidates,
            sliceBudgetWeight: 50f);

        // Assert
        Assert.Equal(new[] { "first", "big" }, slice);
    }

    [Fact]
    public void BuildSlice_WhenNoCandidateHasUsableWeight_FallsBackToFirstCandidate()
    {
        // Arrange
        var candidates = new[]
        {
            new SourceSliceCandidate<string>("first", new Vector3(0f, 0f, 0f), 0, 0f)
        };

        // Act
        var slice = StockpileSourceSlicePlanner.BuildSlice(
            firstId: "first",
            firstPosition: new Vector3(0f, 0f, 0f),
            candidates: candidates,
            sliceBudgetWeight: 50f);

        // Assert
        Assert.Equal(new[] { "first" }, slice);
    }
}
