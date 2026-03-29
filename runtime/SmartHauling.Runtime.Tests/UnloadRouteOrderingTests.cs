using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class UnloadRouteOrderingTests
{
    [Fact]
    public void OrderCandidates_WhenTargetPriorityDiffers_PrefersHighestPriorityBeforeRoute()
    {
        // Arrange
        var candidates = new[]
        {
            new RouteCandidate("near-medium", PriorityRank: 1, TargetPriority: ZonePriority.Medium, NearestDistance: 2f, Amount: 20),
            new RouteCandidate("far-veryhigh", PriorityRank: 0, TargetPriority: ZonePriority.VeryHigh, NearestDistance: 8f, Amount: 20)
        };

        // Act
        var ordered = UnloadRouteOrdering.OrderCandidates(
            candidates,
            new Vector3(0f, 0f, 0f),
            candidate => candidate.PriorityRank,
            candidate => candidate.TargetPriority,
            candidate => candidate.AnchorPosition,
            candidate => candidate.NearestDistance,
            candidate => candidate.Amount);

        // Assert
        Assert.Equal(new[] { "far-veryhigh", "near-medium" }, ordered.Select(candidate => candidate.Id));
    }

    [Fact]
    public void OrderCandidates_WhenPriorityRankMatches_StillPrefersHigherTargetPriority()
    {
        // Arrange
        var candidates = new[]
        {
            new RouteCandidate("medium-near", PriorityRank: 0, TargetPriority: ZonePriority.Medium, NearestDistance: 2f, Amount: 20),
            new RouteCandidate("veryhigh-far", PriorityRank: 0, TargetPriority: ZonePriority.VeryHigh, NearestDistance: 6f, Amount: 20)
        };

        // Act
        var ordered = UnloadRouteOrdering.OrderCandidates(
            candidates,
            new Vector3(0f, 0f, 0f),
            candidate => candidate.PriorityRank,
            candidate => candidate.TargetPriority,
            candidate => candidate.AnchorPosition,
            candidate => candidate.NearestDistance,
            candidate => candidate.Amount);

        // Assert
        Assert.Equal(new[] { "veryhigh-far", "medium-near" }, ordered.Select(candidate => candidate.Id));
    }

    [Fact]
    public void OrderCandidates_WhenTargetsSharePriorityBand_GroupsNearbyStopsInsteadOfZigZagging()
    {
        // Arrange
        var candidates = new[]
        {
            new RouteCandidate("A", PriorityRank: 1, TargetPriority: ZonePriority.Medium, NearestDistance: 1f, Amount: 20, AnchorPosition: new Vector3(1f, 0f, 0f)),
            new RouteCandidate("C", PriorityRank: 1, TargetPriority: ZonePriority.Medium, NearestDistance: 6f, Amount: 20, AnchorPosition: new Vector3(10f, 0f, 0f)),
            new RouteCandidate("B", PriorityRank: 1, TargetPriority: ZonePriority.Medium, NearestDistance: 2f, Amount: 20, AnchorPosition: new Vector3(2f, 0f, 0f)),
            new RouteCandidate("D", PriorityRank: 1, TargetPriority: ZonePriority.Medium, NearestDistance: 7f, Amount: 20, AnchorPosition: new Vector3(11f, 0f, 0f))
        };

        // Act
        var ordered = UnloadRouteOrdering.OrderCandidates(
            candidates,
            new Vector3(0f, 0f, 0f),
            candidate => candidate.PriorityRank,
            candidate => candidate.TargetPriority,
            candidate => candidate.AnchorPosition,
            candidate => candidate.NearestDistance,
            candidate => candidate.Amount);

        // Assert
        Assert.Equal(new[] { "A", "B", "C", "D" }, ordered.Select(candidate => candidate.Id));
    }

    private sealed record RouteCandidate(
        string Id,
        int PriorityRank,
        ZonePriority TargetPriority,
        float NearestDistance,
        int Amount,
        Vector3? AnchorPosition = null);
}
