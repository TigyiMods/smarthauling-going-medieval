using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class HaulGeometryTests
{
    [Fact]
    public void GetNearestPatchDistance_WhenCandidateIsNearMultipleAnchors_ReturnsShortestDistance()
    {
        // Arrange
        var anchors = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(10f, 0f, 0f),
            new Vector3(3f, 4f, 0f)
        };
        var candidatePosition = new Vector3(3f, 0f, 0f);

        // Act
        var distance = HaulGeometry.GetNearestPatchDistance(anchors, candidatePosition);

        // Assert
        Assert.Equal(3f, distance, 3);
    }

    [Fact]
    public void GetPatchExtent_WhenPatchHasFarthestPoint_ReturnsThatDistance()
    {
        // Arrange
        var anchorPosition = new Vector3(0f, 0f, 0f);
        var patchPositions = new[]
        {
            new Vector3(0.1f, 0f, 0f),
            new Vector3(5f, 0f, 0f)
        };

        // Act
        var extent = HaulGeometry.GetPatchExtent(anchorPosition, patchPositions);

        // Assert
        Assert.Equal(5f, extent, 3);
    }

    [Fact]
    public void GetPatchExtent_WhenPatchCollapsesToAnchor_ReturnsMinimumExtent()
    {
        // Arrange
        var anchorPosition = new Vector3(0f, 0f, 0f);
        var patchPositions = new[] { new Vector3(0f, 0f, 0f) };

        // Act
        var extent = HaulGeometry.GetPatchExtent(anchorPosition, patchPositions);

        // Assert
        Assert.Equal(1f, extent, 3);
    }

    [Fact]
    public void GetDetourBudget_WhenTargetDistanceIsZero_UsesClusterExtent()
    {
        // Arrange
        const float sourceToTargetDistance = 0f;
        const float sourceClusterExtent = 12f;
        const float detourBudgetMultiplier = 0.65f;
        const float minimumDetourBudget = 8f;
        const float maximumDetourBudget = 48f;

        // Act
        var budget = HaulGeometry.GetDetourBudget(
            sourceToTargetDistance,
            sourceClusterExtent,
            detourBudgetMultiplier,
            minimumDetourBudget,
            maximumDetourBudget);

        // Assert
        Assert.Equal(12f, budget, 3);
    }

    [Fact]
    public void GetDetourBudget_WhenScaledDistanceIsBelowMinimum_ClampsToMinimum()
    {
        // Arrange
        const float sourceToTargetDistance = 5f;

        // Act
        var budget = HaulGeometry.GetDetourBudget(sourceToTargetDistance, 12f, 0.65f, 8f, 48f);

        // Assert
        Assert.Equal(8f, budget, 3);
    }

    [Fact]
    public void GetDetourBudget_WhenScaledDistanceIsAboveMaximum_ClampsToMaximum()
    {
        // Arrange
        const float sourceToTargetDistance = 200f;

        // Act
        var budget = HaulGeometry.GetDetourBudget(sourceToTargetDistance, 12f, 0.65f, 8f, 48f);

        // Assert
        Assert.Equal(48f, budget, 3);
    }

    [Fact]
    public void GetDetourBudget_WhenScaledDistanceIsWithinRange_ReturnsScaledValue()
    {
        // Arrange
        const float sourceToTargetDistance = 40f;

        // Act
        var budget = HaulGeometry.GetDetourBudget(sourceToTargetDistance, 12f, 0.65f, 8f, 48f);

        // Assert
        Assert.Equal(26f, budget, 3);
    }

    [Fact]
    public void ShouldUsePatchSweep_WhenResourceIsSapling_ReturnsTrue()
    {
        // Arrange
        const string resourceId = "oak_sapling";

        // Act
        var shouldUsePatchSweep = HaulGeometry.ShouldUsePatchSweep(resourceId, 100, 1, 25, 3);

        // Assert
        Assert.True(shouldUsePatchSweep);
    }

    [Fact]
    public void ShouldUsePatchSweep_WhenCountThresholdIsMetBelowAmountThreshold_ReturnsTrue()
    {
        // Arrange
        const string resourceId = "wood";

        // Act
        var shouldUsePatchSweep = HaulGeometry.ShouldUsePatchSweep(resourceId, 20, 4, 25, 3);

        // Assert
        Assert.True(shouldUsePatchSweep);
    }

    [Fact]
    public void ShouldUsePatchSweep_WhenAmountThresholdIsExceeded_ReturnsFalse()
    {
        // Arrange
        const string resourceId = "wood";

        // Act
        var shouldUsePatchSweep = HaulGeometry.ShouldUsePatchSweep(resourceId, 40, 4, 25, 3);

        // Assert
        Assert.False(shouldUsePatchSweep);
    }

    [Fact]
    public void ShouldUsePatchSweep_WhenCountThresholdIsNotMet_ReturnsFalse()
    {
        // Arrange
        const string resourceId = "wood";

        // Act
        var shouldUsePatchSweep = HaulGeometry.ShouldUsePatchSweep(resourceId, 20, 2, 25, 3);

        // Assert
        Assert.False(shouldUsePatchSweep);
    }

    [Fact]
    public void GetAdditionalDetour_WhenCandidateCreatesOffset_ReturnsAdditionalDistance()
    {
        // Arrange
        var firstPosition = new Vector3(0f, 0f, 0f);
        var candidatePosition = new Vector3(3f, 4f, 0f);
        var targetPosition = new Vector3(10f, 0f, 0f);

        // Act
        var detour = HaulGeometry.GetAdditionalDetour(firstPosition, candidatePosition, targetPosition);

        // Assert
        Assert.Equal(3.062258f, detour, 3);
    }

    [Fact]
    public void IsRouteWorthwhile_WhenTargetIsMissing_UsesClusterExtent()
    {
        // Arrange
        var firstPosition = new Vector3(0f, 0f, 0f);
        var candidatePosition = new Vector3(5f, 0f, 0f);

        // Act
        var isWorthwhile = HaulGeometry.IsRouteWorthwhile(
            firstPosition,
            candidatePosition,
            targetPosition: null,
            detourBudget: 20f,
            sourceClusterExtent: 6f,
            out var detourCost);

        // Assert
        Assert.True(isWorthwhile);
        Assert.Equal(5f, detourCost, 3);
    }

    [Fact]
    public void IsRouteWorthwhile_WhenDetourFitsBudget_ReturnsTrue()
    {
        // Arrange
        var firstPosition = new Vector3(0f, 0f, 0f);
        var candidatePosition = new Vector3(3f, 4f, 0f);
        var targetPosition = new Vector3(10f, 0f, 0f);

        // Act
        var isWorthwhile = HaulGeometry.IsRouteWorthwhile(
            firstPosition,
            candidatePosition,
            targetPosition,
            detourBudget: 4f,
            sourceClusterExtent: 6f,
            out var detourCost);

        // Assert
        Assert.True(isWorthwhile);
        Assert.Equal(3.062258f, detourCost, 3);
    }

    [Fact]
    public void IsRouteWorthwhile_WhenDetourExceedsBudget_ReturnsFalse()
    {
        // Arrange
        var firstPosition = new Vector3(0f, 0f, 0f);
        var candidatePosition = new Vector3(3f, 4f, 0f);
        var targetPosition = new Vector3(10f, 0f, 0f);

        // Act
        var isWorthwhile = HaulGeometry.IsRouteWorthwhile(
            firstPosition,
            candidatePosition,
            targetPosition,
            detourBudget: 2f,
            sourceClusterExtent: 6f,
            out _);

        // Assert
        Assert.False(isWorthwhile);
    }
}
