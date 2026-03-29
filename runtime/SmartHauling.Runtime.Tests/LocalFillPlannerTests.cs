using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class LocalFillPlannerTests
{
    [Fact]
    public void GetAnchorDistance_WhenMultipleAnchorsExist_ReturnsNearestDistance()
    {
        // Arrange
        var anchorPositions = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(10f, 0f, 0f)
        };
        Vector3? fallbackAnchorPosition = null;
        var candidatePosition = new Vector3(3f, 0f, 0f);

        // Act
        var distance = LocalFillPlanner.GetAnchorDistance(anchorPositions, fallbackAnchorPosition, candidatePosition);

        // Assert
        Assert.Equal(3f, distance, 3);
    }

    [Fact]
    public void GetAnchorDistance_WhenNoAnchorsExist_UsesFallbackAnchor()
    {
        // Arrange
        var anchorPositions = System.Array.Empty<Vector3>();
        Vector3? fallbackAnchorPosition = new Vector3(5f, 0f, 0f);
        var candidatePosition = new Vector3(8f, 0f, 0f);

        // Act
        var distance = LocalFillPlanner.GetAnchorDistance(anchorPositions, fallbackAnchorPosition, candidatePosition);

        // Assert
        Assert.Equal(3f, distance, 3);
    }

    [Fact]
    public void GetAnchorDistance_WhenNoAnchorInformationExists_ReturnsZero()
    {
        // Arrange
        var anchorPositions = System.Array.Empty<Vector3>();
        Vector3? fallbackAnchorPosition = null;
        var candidatePosition = new Vector3(8f, 0f, 0f);

        // Act
        var distance = LocalFillPlanner.GetAnchorDistance(anchorPositions, fallbackAnchorPosition, candidatePosition);

        // Assert
        Assert.Equal(0f, distance, 3);
    }

    [Fact]
    public void CalculateCandidateScore_WhenPatchIsCloser_ReturnsHigherScore()
    {
        // Arrange
        var nearPatchScore = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 4f,
            patchDistance: 1f,
            hasExistingDropPlan: false);
        var farPatchScore = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 4f,
            patchDistance: 6f,
            hasExistingDropPlan: false);

        // Act
        var scoreDelta = nearPatchScore - farPatchScore;

        // Assert
        Assert.True(scoreDelta > 0f);
    }

    [Fact]
    public void CalculateCandidateScore_WhenDropPlanAlreadyExists_ReturnsHigherScore()
    {
        // Arrange
        var scoreWithoutExistingPlan = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 4f,
            patchDistance: 1f,
            hasExistingDropPlan: false);
        var scoreWithExistingPlan = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 4f,
            patchDistance: 1f,
            hasExistingDropPlan: true);

        // Act
        var scoreDelta = scoreWithExistingPlan - scoreWithoutExistingPlan;

        // Assert
        Assert.True(scoreDelta > 0f);
    }

    [Fact]
    public void CalculateCandidateScore_WhenFarPileIsOnlyLargeByCount_PrefersVeryLocalCandidate()
    {
        // Arrange
        var localScore = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 1f,
            patchDistance: 1f,
            hasExistingDropPlan: false);
        var farScore = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 98,
            distance: 12f,
            patchDistance: 15.5f,
            hasExistingDropPlan: false);

        // Act
        var scoreDelta = localScore - farScore;

        // Assert
        Assert.True(scoreDelta > 0f);
    }
}
