using NSMedieval.State.WorkerJobs;

namespace SmartHauling.Runtime.Tests;

public sealed class HaulingGoalPriorityGateTests
{
    [Fact]
    public void TryAllowForcedHauling_ReturnsTrue_WhenHaulingIsUniqueHighestPriority()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = 1f;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.True(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsFalse_WhenHaulingPriorityIsDisabled()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = float.MaxValue;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.False(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsTrue_WhenHaulingIsBestNormalizedPriority()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: 0.7f);
        priorities[JobType.Hauling] = 0.4f;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.True(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsFalse_WhenAnotherJobHasBetterPriority()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: 0.7f);
        priorities[JobType.Hauling] = 0.5f;
        priorities[JobType.Research] = 0.4f;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.False(allowed);
        Assert.Equal(JobType.Research, blockingJob);
        Assert.Equal(0.4f, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsFalse_WhenAnotherJobSharesHighestPriority()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = 1f;
        priorities[JobType.Construction] = 1f;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.False(allowed);
        Assert.Equal(JobType.Construction, blockingJob);
        Assert.Equal(1f, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_IgnoresDisabledCompetingJobs()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: float.MaxValue);
        priorities[JobType.Hauling] = 0.5f;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.True(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    private static Dictionary<JobType, float> BuildPriorities(float defaultPriority)
    {
        var priorities = new Dictionary<JobType, float>();
        foreach (JobType job in Enum.GetValues(typeof(JobType)))
        {
            priorities[job] = defaultPriority;
        }

        return priorities;
    }
}
