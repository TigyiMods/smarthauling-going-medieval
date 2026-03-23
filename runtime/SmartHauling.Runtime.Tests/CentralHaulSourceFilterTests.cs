using NSMedieval;
using SmartHauling.Runtime.Infrastructure.World;

namespace SmartHauling.Runtime.Tests;

public sealed class CentralHaulSourceFilterTests
{
    [Fact]
    public void FilterWithSingleStorageSnapshot_WhenFilteringCandidates_UsesStorageSnapshotOnlyOnce()
    {
        // Arrange
        var snapshotCalls = 0;
        var snapshot = new List<IStorage>
        {
            A.Fake<IStorage>()
        };
        var candidates = new[] { "a", "b", "c" };
        IReadOnlyList<IStorage>? seenSnapshot = null;

        IReadOnlyList<IStorage> GetStorageSnapshot()
        {
            snapshotCalls++;
            return snapshot;
        }

        var allowedCandidates = new HashSet<string> { "a", "c" };

        // Act
        var result = CentralHaulSourceFilter.FilterWithSingleStorageSnapshot(
            candidates,
            GetStorageSnapshot,
            (candidate, storageSnapshot) =>
            {
                seenSnapshot = storageSnapshot;
                return allowedCandidates.Contains(candidate);
            });

        // Assert
        Assert.Equal(1, snapshotCalls);
        Assert.Same(snapshot, seenSnapshot);
        Assert.Equal(new[] { "a", "c" }, result);
    }
}
