using NSMedieval.State.WorkerJobs;

namespace SmartHauling.Runtime;

/// <summary>
/// Decides whether stockpile hauling is allowed to bypass vanilla goal selection for an idle worker.
/// </summary>
internal static class HaulingGoalPriorityGate
{
    private const float HighestPriorityValue = 1f;

    private static readonly JobType[] CompetingJobs = BuildCompetingJobs();

    public static bool TryAllowForcedHauling(
        Func<JobType, float> getJobPriority,
        out JobType? blockingJob,
        out float blockingPriority)
    {
        blockingJob = null;
        blockingPriority = float.MaxValue;

        var haulingPriority = getJobPriority(JobType.Hauling);
        if (haulingPriority != HighestPriorityValue)
        {
            return false;
        }

        foreach (var job in CompetingJobs)
        {
            var priority = getJobPriority(job);
            if (float.IsNaN(priority) || priority > haulingPriority)
            {
                continue;
            }

            blockingJob = job;
            blockingPriority = priority;
            return false;
        }

        return true;
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
