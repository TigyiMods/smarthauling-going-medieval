using NSMedieval.State.WorkerJobs;

namespace SmartHauling.Runtime;

/// <summary>
/// Decides whether stockpile hauling is allowed to bypass vanilla goal selection for an idle worker.
/// </summary>
internal static class HaulingGoalPriorityGate
{
    private const float DisabledPriorityThreshold = float.MaxValue / 8f;
    private const float PriorityEqualityEpsilon = 0.0001f;

    private static readonly JobType[] CompetingJobs = BuildCompetingJobs();

    public static bool TryAllowForcedHauling(
        Func<JobType, float> getJobPriority,
        JobType haulingJob,
        out JobType? blockingJob,
        out float blockingPriority)
    {
        blockingJob = null;
        blockingPriority = float.MaxValue;

        var haulingPriority = getJobPriority(haulingJob);
        if (!IsUsablePriority(haulingPriority))
        {
            return false;
        }

        foreach (var job in CompetingJobs)
        {
            if (job == haulingJob)
            {
                continue;
            }

            var priority = getJobPriority(job);
            if (!IsUsablePriority(priority) || priority > haulingPriority + PriorityEqualityEpsilon)
            {
                continue;
            }

            blockingJob = job;
            blockingPriority = priority;
            return false;
        }

        return true;
    }

    private static bool IsUsablePriority(float priority)
    {
        return !float.IsNaN(priority) &&
               !float.IsInfinity(priority) &&
               priority < DisabledPriorityThreshold;
    }

    private static JobType[] BuildCompetingJobs()
    {
        var values = (JobType[])Enum.GetValues(typeof(JobType));
        var jobs = new List<JobType>(values.Length);
        foreach (var job in values)
        {
            if (job == JobType.None || job == JobType.Hauling)
            {
                continue;
            }

            jobs.Add(job);
        }

        return jobs.ToArray();
    }
}
