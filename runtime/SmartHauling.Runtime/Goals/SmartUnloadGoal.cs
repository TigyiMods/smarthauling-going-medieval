using NSMedieval.Goap;

namespace SmartHauling.Runtime.Goals;

internal sealed class SmartUnloadGoal : Goal
{
    public SmartUnloadGoal(Agent selfAgent)
        : base("SmartUnloadGoal", selfAgent)
    {
    }

    public override bool AgentTypeCheck()
    {
        return AgentOwner is IStorageAgent && AgentOwner is IPathfindingAgent;
    }

    public override bool CanStart(bool isForced = false)
    {
        return AgentOwner is IStorageAgent { Storage: not null } storageAgent && !storageAgent.Storage.IsEmpty();
    }

    protected override IEnumerable<GoapAction> GetNextAction()
    {
        return SmartUnloadExecutor.Build(this);
    }
}
