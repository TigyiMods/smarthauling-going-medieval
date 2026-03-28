namespace SmartHauling.Runtime.Tests;

public sealed class PlayerForcedPriorityPlannerTests
{
    [Fact]
    public void SelectPreferredAnchor_WhenPreferredIsValid_ReturnsPreferredEvenIfFarther()
    {
        var preferred = new Candidate("preferred", 8f, true);
        var nearby = new Candidate("nearby", 2f, true);

        var selected = PlayerForcedPriorityPlanner.SelectPreferredAnchor(
            preferred,
            new[] { nearby },
            isValid: candidate => candidate.Valid,
            getScore: candidate => candidate.Distance);

        Assert.Same(preferred, selected);
    }

    [Fact]
    public void SelectPreferredAnchor_WhenPreferredIsInvalid_FallsBackToBestValidCandidate()
    {
        var invalidPreferred = new Candidate("preferred", 1f, false);
        var farther = new Candidate("farther", 5f, true);
        var nearest = new Candidate("nearest", 2f, true);

        var selected = PlayerForcedPriorityPlanner.SelectPreferredAnchor(
            invalidPreferred,
            new[] { farther, nearest },
            isValid: candidate => candidate.Valid,
            getScore: candidate => candidate.Distance);

        Assert.Same(nearest, selected);
    }

    [Fact]
    public void ContainsCandidate_ReturnsTrueForNonAnchorPriorityCandidate()
    {
        var anchor = new Candidate("anchor", 0f, true);
        var secondary = new Candidate("secondary", 1f, true);

        Assert.True(PlayerForcedPriorityPlanner.ContainsCandidate(secondary, new[] { anchor, secondary }));
        Assert.False(PlayerForcedPriorityPlanner.ContainsCandidate(new Candidate("other", 2f, true), new[] { anchor, secondary }));
    }

    [Fact]
    public void MergePriorityOrder_WhenAnchorAlreadyExists_MovesItToFrontWithoutDuplicates()
    {
        var first = new Candidate("first", 0f, true);
        var second = new Candidate("second", 1f, true);
        var merged = PlayerForcedPriorityPlanner.MergePriorityOrder(
            second,
            new[] { first, second, first });

        Assert.Collection(
            merged,
            item => Assert.Same(second, item),
            item => Assert.Same(first, item));
    }

    [Fact]
    public void SelectLocalPrioritySeeds_WhenCandidatesAreLocal_KeepsAnchorFirstThenInputOrder()
    {
        var anchor = new Candidate("anchor", 0f, true);
        var nearbyOne = new Candidate("nearby-1", 2f, true);
        var nearbyTwo = new Candidate("nearby-2", 5.5f, true);

        var selected = PlayerForcedPriorityPlanner.SelectLocalPrioritySeeds(
            anchor,
            new[] { nearbyOne, nearbyTwo },
            maxDistance: 6f,
            isValid: candidate => candidate.Valid,
            getDistanceFromAnchor: candidate => candidate.Distance);

        Assert.Collection(
            selected,
            item => Assert.Same(anchor, item),
            item => Assert.Same(nearbyOne, item),
            item => Assert.Same(nearbyTwo, item));
    }

    [Fact]
    public void SelectLocalPrioritySeeds_WhenCandidateIsFarOrInvalid_ExcludesIt()
    {
        var anchor = new Candidate("anchor", 0f, true);
        var nearby = new Candidate("nearby", 3f, true);
        var far = new Candidate("far", 8f, true);
        var invalid = new Candidate("invalid", 1f, false);

        var selected = PlayerForcedPriorityPlanner.SelectLocalPrioritySeeds(
            anchor,
            new[] { nearby, far, invalid },
            maxDistance: 6f,
            isValid: candidate => candidate.Valid,
            getDistanceFromAnchor: candidate => candidate.Distance);

        Assert.Collection(
            selected,
            item => Assert.Same(anchor, item),
            item => Assert.Same(nearby, item));
    }

    private sealed class Candidate
    {
        public Candidate(string id, float distance, bool valid)
        {
            Id = id;
            Distance = distance;
            Valid = valid;
        }

        public string Id { get; }

        public float Distance { get; }

        public bool Valid { get; }
    }
}
