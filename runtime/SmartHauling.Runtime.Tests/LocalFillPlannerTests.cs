using SmartHauling.Runtime;
using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class LocalFillPlannerTests
{
    [Fact]
    public void GetAnchorDistance_UsesNearestAnchor()
    {
        var distance = LocalFillPlanner.GetAnchorDistance(
            new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(10f, 0f, 0f)
            },
            fallbackAnchorPosition: null,
            candidatePosition: new Vector3(3f, 0f, 0f));

        Assert.Equal(3f, distance, 3);
    }

    [Fact]
    public void GetAnchorDistance_UsesFallbackWhenAnchorsMissing()
    {
        var distance = LocalFillPlanner.GetAnchorDistance(
            System.Array.Empty<Vector3>(),
            new Vector3(5f, 0f, 0f),
            new Vector3(8f, 0f, 0f));

        Assert.Equal(3f, distance, 3);
    }

    [Fact]
    public void GetAnchorDistance_ReturnsZeroWithoutAnyAnchor()
    {
        var distance = LocalFillPlanner.GetAnchorDistance(
            System.Array.Empty<Vector3>(),
            fallbackAnchorPosition: null,
            candidatePosition: new Vector3(8f, 0f, 0f));

        Assert.Equal(0f, distance, 3);
    }

    [Fact]
    public void CalculateCandidateScore_PrefersCloserPatch()
    {
        var nearPatch = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 4f,
            patchDistance: 1f,
            hasExistingDropPlan: false);
        var farPatch = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 4f,
            patchDistance: 6f,
            hasExistingDropPlan: false);

        Assert.True(nearPatch > farPatch);
    }

    [Fact]
    public void CalculateCandidateScore_PrefersExistingDropPlan()
    {
        var withoutExistingPlan = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 4f,
            patchDistance: 1f,
            hasExistingDropPlan: false);
        var withExistingPlan = LocalFillPlanner.CalculateCandidateScore(
            requestedAmount: 20,
            distance: 4f,
            patchDistance: 1f,
            hasExistingDropPlan: true);

        Assert.True(withExistingPlan > withoutExistingPlan);
    }
}
