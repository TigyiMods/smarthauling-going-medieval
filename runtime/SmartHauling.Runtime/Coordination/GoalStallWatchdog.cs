using System.Runtime.CompilerServices;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using SmartHauling.Runtime.Configuration;

namespace SmartHauling.Runtime;

/// <summary>
/// Detects hauling goals that stop making meaningful progress and terminates them conservatively.
/// </summary>
internal static class GoalStallWatchdog
{
    private static readonly ConditionalWeakTable<Goal, StallState> States = new();

    internal readonly struct AnchorSignature : IEquatable<AnchorSignature>
    {
        public AnchorSignature(
            string goalType,
            int positionX,
            int positionY,
            int positionZ,
            int carryCount)
        {
            GoalType = goalType;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            CarryCount = carryCount;
        }

        public string GoalType { get; }

        public int PositionX { get; }

        public int PositionY { get; }

        public int PositionZ { get; }

        public int CarryCount { get; }

        public bool Equals(AnchorSignature other)
        {
            return GoalType == other.GoalType &&
                   PositionX == other.PositionX &&
                   PositionY == other.PositionY &&
                   PositionZ == other.PositionZ &&
                   CarryCount == other.CarryCount;
        }

        public override bool Equals(object? obj)
        {
            return obj is AnchorSignature other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(GoalType, PositionX, PositionY, PositionZ, CarryCount);
        }
    }

    internal readonly struct Signature : IEquatable<Signature>
    {
        public Signature(
            string actionId,
            int positionX,
            int positionY,
            int positionZ,
            int carryCount,
            int pickupQueueCount,
            int targetAIdentity,
            int targetBIdentity,
            int dropFailures,
            bool dropPhaseLocked)
        {
            ActionId = actionId;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            CarryCount = carryCount;
            PickupQueueCount = pickupQueueCount;
            TargetAIdentity = targetAIdentity;
            TargetBIdentity = targetBIdentity;
            DropFailures = dropFailures;
            DropPhaseLocked = dropPhaseLocked;
        }

        public string ActionId { get; }

        public int PositionX { get; }

        public int PositionY { get; }

        public int PositionZ { get; }

        public int CarryCount { get; }

        public int PickupQueueCount { get; }

        public int TargetAIdentity { get; }

        public int TargetBIdentity { get; }

        public int DropFailures { get; }

        public bool DropPhaseLocked { get; }

        public bool Equals(Signature other)
        {
            return ActionId == other.ActionId &&
                   PositionX == other.PositionX &&
                   PositionY == other.PositionY &&
                   PositionZ == other.PositionZ &&
                   CarryCount == other.CarryCount &&
                   PickupQueueCount == other.PickupQueueCount &&
                   TargetAIdentity == other.TargetAIdentity &&
                   TargetBIdentity == other.TargetBIdentity &&
                   DropFailures == other.DropFailures &&
                   DropPhaseLocked == other.DropPhaseLocked;
        }

        public override bool Equals(object? obj)
        {
            return obj is Signature other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                HashCode.Combine(
                    ActionId,
                    PositionX,
                    PositionY,
                    PositionZ,
                    CarryCount),
                HashCode.Combine(
                    PickupQueueCount,
                    TargetAIdentity,
                    TargetBIdentity,
                    DropFailures,
                    DropPhaseLocked));
        }
    }

    internal readonly struct Evaluation
    {
        public Evaluation(bool hasProgressed, bool isStalled, float lastProgressAt, float stallDuration)
        {
            HasProgressed = hasProgressed;
            IsStalled = isStalled;
            LastProgressAt = lastProgressAt;
            StallDuration = stallDuration;
        }

        public bool HasProgressed { get; }

        public bool IsStalled { get; }

        public float LastProgressAt { get; }

        public float StallDuration { get; }
    }

    private sealed class StallState
    {
        public Signature Signature { get; set; }

        public float LastProgressAt { get; set; }

        public AnchorSignature AnchorSignature { get; set; }

        public float LastAnchorProgressAt { get; set; }

        public float LastSnapshotLoggedAt { get; set; } = -1f;
    }

    public static bool TryAbortStalledGoal(Goal goal)
    {
        if (!SmartHaulingSettings.EnableStallWatchdog ||
            goal == null ||
            !IsRelevant(goal) ||
            goal.AgentOwner is not CreatureBase creature ||
            goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return false;
        }

        var signature = BuildSignature(goal, creature, storageAgent.Storage.GetTotalStoredCount());
        var anchorSignature = BuildAnchorSignature(goal, creature, signature.CarryCount);
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        var evaluation = Observe(goal, signature, now, SmartHaulingSettings.StallWatchdogTimeoutSeconds);
        var anchorEvaluation = ObserveAnchor(goal, anchorSignature, now, SmartHaulingSettings.StallWatchdogTimeoutSeconds);
        TryLogPotentialStall(goal, signature, anchorEvaluation, now);
        if (!evaluation.IsStalled)
        {
            return false;
        }

        var condition = SelectTerminalCondition(signature);
        DiagnosticTrace.Info(
            "watchdog",
            $"Recovered stalled {goal.GetType().Name} for {goal.AgentOwner}: action={signature.ActionId}, stall={evaluation.StallDuration:0.0}s, carry={signature.CarryCount}, pickupQueue={signature.PickupQueueCount}, condition={condition}",
            80);
        goal.EndGoalWith(condition);
        return true;
    }

    public static void Clear(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        States.Remove(goal);
    }

    internal static Evaluation Evaluate(
        Signature? previousSignature,
        float previousProgressAt,
        Signature currentSignature,
        float now,
        float timeoutSeconds)
    {
        if (previousSignature == null || !previousSignature.Value.Equals(currentSignature))
        {
            return new Evaluation(
                hasProgressed: true,
                isStalled: false,
                lastProgressAt: now,
                stallDuration: 0f);
        }

        var stallDuration = now - previousProgressAt;
        return new Evaluation(
            hasProgressed: false,
            isStalled: stallDuration >= timeoutSeconds,
            lastProgressAt: previousProgressAt,
            stallDuration: stallDuration);
    }

    internal static Evaluation EvaluateAnchor(
        AnchorSignature? previousSignature,
        float previousProgressAt,
        AnchorSignature currentSignature,
        float now,
        float timeoutSeconds)
    {
        if (previousSignature == null || !previousSignature.Value.Equals(currentSignature))
        {
            return new Evaluation(
                hasProgressed: true,
                isStalled: false,
                lastProgressAt: now,
                stallDuration: 0f);
        }

        var stallDuration = now - previousProgressAt;
        return new Evaluation(
            hasProgressed: false,
            isStalled: stallDuration >= timeoutSeconds,
            lastProgressAt: previousProgressAt,
            stallDuration: stallDuration);
    }

    private static Evaluation Observe(
        Goal goal,
        Signature signature,
        float now,
        float timeoutSeconds)
    {
        if (!States.TryGetValue(goal, out var state))
        {
            state = States.GetOrCreateValue(goal);
            state.Signature = signature;
            state.LastProgressAt = now;
            return new Evaluation(
                hasProgressed: true,
                isStalled: false,
                lastProgressAt: now,
                stallDuration: 0f);
        }

        var evaluation = Evaluate(
            state.Signature,
            state.LastProgressAt,
            signature,
            now,
            timeoutSeconds);

        if (evaluation.HasProgressed)
        {
            state.Signature = signature;
            state.LastProgressAt = evaluation.LastProgressAt;
        }

        return evaluation;
    }

    private static Evaluation ObserveAnchor(
        Goal goal,
        AnchorSignature signature,
        float now,
        float timeoutSeconds)
    {
        if (!States.TryGetValue(goal, out var state))
        {
            state = States.GetOrCreateValue(goal);
            state.AnchorSignature = signature;
            state.LastAnchorProgressAt = now;
            return new Evaluation(
                hasProgressed: true,
                isStalled: false,
                lastProgressAt: now,
                stallDuration: 0f);
        }

        var evaluation = EvaluateAnchor(
            state.AnchorSignature,
            state.LastAnchorProgressAt,
            signature,
            now,
            timeoutSeconds);

        if (evaluation.HasProgressed)
        {
            state.AnchorSignature = signature;
            state.LastAnchorProgressAt = evaluation.LastProgressAt;
            state.LastSnapshotLoggedAt = -1f;
        }

        return evaluation;
    }

    private static bool IsRelevant(Goal goal)
    {
        return goal is StockpileHaulingGoal || goal is SmartHauling.Runtime.Goals.SmartUnloadGoal;
    }

    private static Signature BuildSignature(Goal goal, CreatureBase creature, int carryCount)
    {
        var position = creature.GetPosition();
        var pickupQueueCount = goal is StockpileHaulingGoal
            ? goal.GetTargetQueue(TargetIndex.A).Count
            : 0;

        var dropFailures = 0;
        var dropPhaseLocked = false;
        if (CoordinatedStockpileExecutionStore.TryGet(goal, out var executionState))
        {
            dropFailures = executionState.ConsecutiveDropFailures;
            dropPhaseLocked = executionState.DropPhaseLocked;
        }

        return new Signature(
            goal.CurrentAction?.Id ?? "<none>",
            positionX: (int)System.MathF.Round(position.x),
            positionY: (int)System.MathF.Round(position.y),
            positionZ: (int)System.MathF.Round(position.z),
            carryCount: carryCount,
            pickupQueueCount: pickupQueueCount,
            targetAIdentity: GetTargetIdentity(goal.GetTarget(TargetIndex.A)),
            targetBIdentity: GetTargetIdentity(goal.GetTarget(TargetIndex.B)),
            dropFailures: dropFailures,
            dropPhaseLocked: dropPhaseLocked);
    }

    private static AnchorSignature BuildAnchorSignature(Goal goal, CreatureBase creature, int carryCount)
    {
        var position = creature.GetPosition();
        return new AnchorSignature(
            goal.GetType().Name,
            positionX: (int)System.MathF.Round(position.x),
            positionY: (int)System.MathF.Round(position.y),
            positionZ: (int)System.MathF.Round(position.z),
            carryCount: carryCount);
    }

    private static void TryLogPotentialStall(Goal goal, Signature signature, Evaluation anchorEvaluation, float now)
    {
        if (!anchorEvaluation.IsStalled ||
            !States.TryGetValue(goal, out var state) ||
            now - state.LastSnapshotLoggedAt < SmartHaulingSettings.StallWatchdogTimeoutSeconds)
        {
            return;
        }

        state.LastSnapshotLoggedAt = now;
        DiagnosticTrace.Info(
            "watchdog.snapshot",
            $"Potential visual stall on {goal.GetType().Name} for {goal.AgentOwner}: action={signature.ActionId}, stall={anchorEvaluation.StallDuration:0.0}s, carry={signature.CarryCount}, pickupQueue={signature.PickupQueueCount}, targetA={signature.TargetAIdentity}, targetB={signature.TargetBIdentity}, dropFailures={signature.DropFailures}, dropLocked={signature.DropPhaseLocked}",
            120);
    }

    private static int GetTargetIdentity(TargetObject target)
    {
        return target.ObjectInstance == null
            ? 0
            : RuntimeHelpers.GetHashCode(target.ObjectInstance);
    }

    private static GoalCondition SelectTerminalCondition(Signature signature)
    {
        return signature.CarryCount == 0 && signature.PickupQueueCount == 0
            ? GoalCondition.Succeeded
            : GoalCondition.Incompletable;
    }
}
