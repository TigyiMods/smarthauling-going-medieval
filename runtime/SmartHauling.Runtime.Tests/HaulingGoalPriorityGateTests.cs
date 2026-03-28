using NSMedieval.State.WorkerJobs;

namespace SmartHauling.Runtime.Tests;

public sealed class HaulingGoalPriorityGateTests
{
    [Fact]
    public void TryAllowForcedHauling_ReturnsTrue_WhenHaulingIsUniqueHighestPriority()
    {
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = 1f;

        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            JobType.Hauling,
            out var blockingJob,
            out var blockingPriority);

        Assert.True(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsFalse_WhenHaulingPriorityIsDisabled()
    {
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = float.MaxValue;

        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            JobType.Hauling,
            out var blockingJob,
            out var blockingPriority);

        Assert.False(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsTrue_WhenHaulingIsBestNormalizedPriority()
    {
        var priorities = BuildPriorities(defaultPriority: 0.7f);
        priorities[JobType.Hauling] = 0.4f;

        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            JobType.Hauling,
            out var blockingJob,
            out var blockingPriority);

        Assert.True(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsFalse_WhenAnotherJobHasBetterPriority()
    {
        var priorities = BuildPriorities(defaultPriority: 0.7f);
        priorities[JobType.Hauling] = 0.5f;
        priorities[JobType.Research] = 0.4f;

        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            JobType.Hauling,
            out var blockingJob,
            out var blockingPriority);

        Assert.False(allowed);
        Assert.Equal(JobType.Research, blockingJob);
        Assert.Equal(0.4f, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsFalse_WhenAnotherJobSharesHighestPriority()
    {
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = 1f;
        priorities[JobType.Construction] = 1f;

        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            JobType.Hauling,
            out var blockingJob,
            out var blockingPriority);

        Assert.False(allowed);
        Assert.Equal(JobType.Construction, blockingJob);
        Assert.Equal(1f, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_IgnoresDisabledCompetingJobs()
    {
        var priorities = BuildPriorities(defaultPriority: float.MaxValue);
        priorities[JobType.Hauling] = 0.5f;

        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            JobType.Hauling,
            out var blockingJob,
            out var blockingPriority);

        Assert.True(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_UsesRequestedJobPriority_ForUrgentHaul()
    {
        var priorities = BuildPriorities(defaultPriority: float.MaxValue);
        priorities[JobType.UrgentHaul] = 0.2f;
        priorities[JobType.Mining] = 0.6f;

        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            JobType.UrgentHaul,
            out var blockingJob,
            out _);

        Assert.True(allowed);
        Assert.Null(blockingJob);
    }

    [Fact]
    public void TryAllowForcedHauling_BlocksWhenCompetingJobBeatsUrgentPriority()
    {
        var priorities = BuildPriorities(defaultPriority: float.MaxValue);
        priorities[JobType.UrgentHaul] = 0.5f;
        priorities[JobType.Mining] = 0.4f;

        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            JobType.UrgentHaul,
            out var blockingJob,
            out var blockingPriority);

        Assert.False(allowed);
        Assert.Equal(JobType.Mining, blockingJob);
        Assert.Equal(0.4f, blockingPriority);
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
