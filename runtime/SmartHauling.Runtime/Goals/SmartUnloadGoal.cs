using System.Collections.Generic;
using NSMedieval.Goap;
using NSMedieval.Goap.Actions;
using NSMedieval.Pathfinding;
using NSMedieval.State;
using NSMedieval.Village.Map.Pathfinding;

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
        var done = GeneralActions.Instant("SmartUnload.Done");
        var findBestStorage = StorageActions.FindBestStorage(TargetIndex.A);
        var selectNextTarget = GoalUtilActions.SelectNextTargetFromQueue(TargetIndex.A);
        var reserveAndQueueStorageSpaces = StorageActions.ReserveAndQueueStoragePlaces(TargetIndex.A, TargetIndex.A);
        var selectGoToTarget = GoalUtilActions.SelectNextTargetFromQueue(TargetIndex.A);
        var goToStorage = GoToActions.GoToTarget(TargetIndex.A, PathCompleteMode.ExactPosition)
            .SkipIfTargetDisposedForbidenOrNull(TargetIndex.A)
            .FailAtCondition(() => ((IStorageAgent)AgentOwner).Storage.IsEmpty());
        var storeResource = ResourceActions.StoreResourceOnStockpile(TargetIndex.A).SkipOnFailure();

        yield return StorageActions.CompleteIfOwnerStorageIsEmpty();
        yield return JumpActions.JumpIfNoTargetsInQueue(findBestStorage, TargetIndex.A);
        yield return selectNextTarget;
        yield return reserveAndQueueStorageSpaces
            .JumpOnCompletionIfNotStatus(findBestStorage, ActionCompletionStatus.Success)
            .JumpOnCompletion(selectGoToTarget, ActionCompletionStatus.Success);
        yield return findBestStorage.JumpOnCompletionIfHaveTargetsInQueue(selectNextTarget, TargetIndex.A, ActionCompletionStatus.Success);
        yield return selectGoToTarget;
        yield return goToStorage.JumpOnCompletionIfNotStatus(findBestStorage, ActionCompletionStatus.Success);
        yield return storeResource;
        yield return JumpActions.JumpIfHaveNoResourceInStorage(done);
        yield return JumpActions.JumpIfHaveTargetsInQueue(selectGoToTarget, TargetIndex.A);
        yield return JumpActions.Jump(findBestStorage);
        yield return done;
    }
}
