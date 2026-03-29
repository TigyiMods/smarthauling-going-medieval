using SmartHauling.Runtime.Infrastructure.World;

namespace SmartHauling.Runtime.Tests;

public sealed class CentralHaulSourceFilterTests
{
    [Fact]
    public void MergeCandidates_IncludesPreferredAndEligibleKnownCandidates()
    {
        // Arrange
        var preferred = new Candidate("preferred", isStored: false);
        var shared = new Candidate("shared", isStored: true);
        var storedKnown = new Candidate("stored-known", isStored: true);
        var ignoredKnown = new Candidate("ignored-known", isStored: false);

        // Act
        var merged = CentralHaulSourceFilter.MergeCandidates(
            new[] { preferred, shared },
            new[] { shared, storedKnown, ignoredKnown },
            candidate => candidate.IsStored);

        // Assert
        Assert.Equal(3, merged.Count);
        Assert.Contains(preferred, merged);
        Assert.Contains(shared, merged);
        Assert.Contains(storedKnown, merged);
        Assert.DoesNotContain(ignoredKnown, merged);
    }

    [Fact]
    public void MergeCandidates_DeduplicatesCandidatesUsingProvidedComparer()
    {
        // Arrange
        var preferred = new Candidate("shared", isStored: false);
        var known = new Candidate("shared", isStored: true);

        // Act
        var merged = CentralHaulSourceFilter.MergeCandidates(
            new[] { preferred },
            new[] { known },
            candidate => candidate.IsStored,
            CandidateIdComparer.Instance);

        // Assert
        Assert.Single(merged);
    }

    private sealed class Candidate
    {
        public Candidate(string id, bool isStored)
        {
            Id = id;
            IsStored = isStored;
        }

        public string Id { get; }

        public bool IsStored { get; }
    }

    private sealed class CandidateIdComparer : IEqualityComparer<Candidate>
    {
        public static CandidateIdComparer Instance { get; } = new();

        public bool Equals(Candidate? x, Candidate? y)
        {
            return StringComparer.Ordinal.Equals(x?.Id, y?.Id);
        }

        public int GetHashCode(Candidate obj)
        {
            return StringComparer.Ordinal.GetHashCode(obj.Id);
        }
    }
}
