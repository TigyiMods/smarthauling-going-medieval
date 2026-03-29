using SmartHauling.Runtime.Infrastructure.World;

namespace SmartHauling.Runtime.Tests;

public sealed class IncrementalPredicateSetCacheTests
{
    [Fact]
    public void GetSnapshot_WhenSourceIsLarge_RefreshesMatchesIncrementally()
    {
        // Arrange
        var cache = new IncrementalPredicateSetCache<TestItem>(
            sourceLifetimeSeconds: 10f,
            sweepChunkSize: 2,
            coldStartSweepMultiplier: 1,
            TestItemIdComparer.Instance);
        var source = new[]
        {
            new TestItem("a", shouldInclude: true),
            new TestItem("b", shouldInclude: false),
            new TestItem("c", shouldInclude: true),
            new TestItem("d", shouldInclude: true)
        };

        // Act
        var firstSnapshot = cache.GetSnapshot(0f, () => source, item => item.ShouldInclude);
        var secondSnapshot = cache.GetSnapshot(1f, () => source, item => item.ShouldInclude);

        // Assert
        Assert.Collection(
            firstSnapshot.OrderBy(item => item.Id),
            item => Assert.Equal("a", item.Id));
        Assert.Collection(
            secondSnapshot.OrderBy(item => item.Id),
            item => Assert.Equal("a", item.Id),
            item => Assert.Equal("c", item.Id),
            item => Assert.Equal("d", item.Id));
    }

    [Fact]
    public void GetSnapshot_WhenSourceRefreshes_PrunesRemovedItemsAndEventuallyDropsNonMatchingItems()
    {
        // Arrange
        var cache = new IncrementalPredicateSetCache<TestItem>(
            sourceLifetimeSeconds: 1f,
            sweepChunkSize: 1,
            coldStartSweepMultiplier: 1,
            TestItemIdComparer.Instance);
        var keep = new TestItem("keep", shouldInclude: true);
        var stale = new TestItem("stale", shouldInclude: true);
        var late = new TestItem("late", shouldInclude: true);
        var source = (IReadOnlyList<TestItem>)new[] { keep, stale, late };
        cache.GetSnapshot(0f, () => source, item => item.ShouldInclude);
        cache.GetSnapshot(0.1f, () => source, item => item.ShouldInclude);
        cache.GetSnapshot(0.2f, () => source, item => item.ShouldInclude);

        source = new[] { keep, new TestItem("late", shouldInclude: false) };

        // Act
        var refreshedSnapshot = cache.GetSnapshot(2f, () => source, item => item.ShouldInclude);
        var settledSnapshot = cache.GetSnapshot(2.1f, () => source, item => item.ShouldInclude);

        // Assert
        Assert.Collection(
            refreshedSnapshot.OrderBy(item => item.Id),
            item => Assert.Equal("keep", item.Id),
            item => Assert.Equal("late", item.Id));
        Assert.Collection(
            settledSnapshot.OrderBy(item => item.Id),
            item => Assert.Equal("keep", item.Id));
    }

    private sealed class TestItem
    {
        public TestItem(string id, bool shouldInclude)
        {
            Id = id;
            ShouldInclude = shouldInclude;
        }

        public string Id { get; }

        public bool ShouldInclude { get; }
    }

    private sealed class TestItemIdComparer : IEqualityComparer<TestItem>
    {
        public static TestItemIdComparer Instance { get; } = new();

        public bool Equals(TestItem? x, TestItem? y)
        {
            return StringComparer.Ordinal.Equals(x?.Id, y?.Id);
        }

        public int GetHashCode(TestItem obj)
        {
            return StringComparer.Ordinal.GetHashCode(obj.Id);
        }
    }
}
