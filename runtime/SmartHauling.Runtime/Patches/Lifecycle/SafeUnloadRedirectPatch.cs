using System.Runtime.CompilerServices;
using HarmonyLib;
using NSMedieval.Components;
using NSMedieval.Controllers;
using NSMedieval.Goap;
using NSMedieval.State;
using NSMedieval.Village.Map.Pathfinding;
using SmartHauling.Runtime.Goals;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch]
internal static class SafeUnloadRedirectPatch
{
    [ThreadStatic]
    private static Stack<GoalEndContext>? goalEndContexts;

    private static readonly HashSet<WorkerGoapAgent> SchedulingAgents = new();
    private static readonly ConditionalWeakTable<Goal, object?> HandledGoals = new();

    [HarmonyPatch(typeof(WorkerGoapAgent), "OnGoalEnded")]
    [HarmonyPrefix]
    private static void OnGoalEndedPrefix(WorkerGoapAgent __instance, Goal goal, GoalCondition condition)
    {
        if (!ShouldPrepareRedirect(__instance, goal))
        {
            return;
        }

        var creature = (CreatureBase)__instance.AgentOwner;
        var storageAgent = (IStorageAgent)__instance.AgentOwner;
        var storage = storageAgent.Storage;
        var remaining = storage?.GetTotalStoredCount() ?? 0;
        if (remaining <= 0)
        {
            return;
        }

        if (storage == null || !CanAttemptSmartUnload(__instance, goal, creature, storage))
        {
            DiagnosticTrace.Info(
                "unload",
                $"Skipping safe unload for {creature} after {goal.GetType().Name}: no storage candidate for carried=[{CarrySummaryUtil.Summarize(storage)}]",
                20);
            return;
        }

        var contexts = goalEndContexts ??= new Stack<GoalEndContext>();
        contexts.Push(new GoalEndContext(__instance, creature, goal, condition, remaining));
        DiagnosticTrace.Info(
            "unload",
            $"Preparing safe unload for {creature} after {goal.GetType().Name} ended with {condition}, carry={remaining}",
            20);
    }

    [HarmonyPatch(typeof(WorkerGoapAgent), "OnGoalEnded")]
    [HarmonyPostfix]
    private static void OnGoalEndedPostfix(WorkerGoapAgent __instance, Goal goal)
    {
        if (goal is SmartUnloadGoal)
        {
            HandleSmartUnloadEnd(__instance, goal);
            return;
        }

        var context = PopContext(__instance, goal);
        if (context == null)
        {
            return;
        }

        if (__instance.HasDisposed || LoadingController.IsSceneTransition)
        {
            return;
        }

        if (__instance.AgentOwner is not CreatureBase creature ||
            creature.HasDisposed ||
            __instance.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return;
        }

        var remaining = storageAgent.Storage.GetTotalStoredCount();
        if (remaining <= 0)
        {
            UnloadCarryContextStore.Clear(creature);
            DiagnosticTrace.Info("unload", $"Safe unload no longer needed for {creature}; carry already empty.", 20);
            return;
        }

        if (HaulingPriorityRules.TryGetGoalSourcePriority(goal, creature, out var sourcePriority))
        {
            UnloadCarryContextStore.SetSourcePriority(creature, sourcePriority);
        }
        else
        {
            UnloadCarryContextStore.Clear(creature);
        }

        lock (SchedulingAgents)
        {
            if (!SchedulingAgents.Add(__instance))
            {
                DiagnosticTrace.Info("unload", $"Skipping nested unload schedule for {creature}", 20);
                return;
            }
        }

        try
        {
            if (HandledGoals.TryGetValue(goal, out _))
            {
                DiagnosticTrace.Info("unload", $"Skipping duplicate unload scheduling for {creature} on {goal.GetType().Name}", 20);
                return;
            }

            HandledGoals.Add(goal, null);
            DiagnosticTrace.Info(
                "unload",
                $"Scheduling SmartUnloadGoal for {creature} after {goal.GetType().Name}, carry={remaining}, sourcePriority={(HaulingPriorityRules.TryGetGoalSourcePriority(goal, creature, out sourcePriority) ? sourcePriority.ToString() : "None")}",
                20);
            __instance.ForceNextGoal(new SmartUnloadGoal(__instance));
        }
        finally
        {
            lock (SchedulingAgents)
            {
                SchedulingAgents.Remove(__instance);
            }
        }
    }

    private static void HandleSmartUnloadEnd(WorkerGoapAgent agent, Goal goal)
    {
        if (!RuntimeActivation.IsActive ||
            agent == null ||
            agent.HasDisposed ||
            agent.AgentOwner is not CreatureBase creature ||
            creature.HasDisposed ||
            agent.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return;
        }

        var remaining = storageAgent.Storage.GetTotalStoredCount();
        if (remaining <= 0)
        {
            UnloadCarryContextStore.Clear(creature);
            return;
        }

        if (CanAttemptSmartUnload(agent, goal, creature, storageAgent.Storage))
        {
            DiagnosticTrace.Info(
                "unload",
                $"SmartUnloadGoal ended with carry still present for {creature}, leaving retry to next idle tick: carry={remaining}",
                20);
            return;
        }

        DiagnosticTrace.Info(
            "unload",
            $"SmartUnloadGoal ended with no valid storage for {creature}, dropping carry to ground: carry={remaining}, carried=[{CarrySummaryUtil.Summarize(storageAgent.Storage)}]",
            20);
        UnloadCarryContextStore.Clear(creature);
        creature.DropStorage();
    }

    [HarmonyPatch(typeof(CreatureBase), nameof(CreatureBase.DropStorage))]
    [HarmonyPrefix]
    private static bool DropStoragePrefix(CreatureBase __instance)
    {
        if (!RuntimeActivation.IsActive)
        {
            return true;
        }

        var context = PeekContext();
        if (context == null || !ReferenceEquals(context.Creature, __instance))
        {
            return true;
        }

        DiagnosticTrace.Info(
            "unload",
            $"Suppressing DropStorage for {__instance} during {context.Goal.GetType().Name} end, carry={context.CarryCount}",
            20);
        return false;
    }

    private static GoalEndContext? PeekContext()
    {
        return goalEndContexts != null && goalEndContexts.Count > 0 ? goalEndContexts.Peek() : null;
    }

    private static GoalEndContext? PopContext(WorkerGoapAgent agent, Goal goal)
    {
        if (goalEndContexts == null || goalEndContexts.Count == 0)
        {
            return null;
        }

        var context = goalEndContexts.Peek();
        if (!ReferenceEquals(context.Agent, agent) || !ReferenceEquals(context.Goal, goal))
        {
            return null;
        }

        goalEndContexts.Pop();
        return context;
    }

    private static bool ShouldPrepareRedirect(WorkerGoapAgent agent, Goal goal)
    {
        if (!RuntimeActivation.IsActive ||
            agent.HasDisposed ||
            LoadingController.IsSceneTransition ||
            goal is SmartUnloadGoal ||
            agent.AgentOwner is not CreatureBase creature ||
            creature.HasDisposed ||
            agent.AgentOwner is not IStorageAgent { Storage: not null } storageAgent ||
            storageAgent.Storage.IsEmpty())
        {
            return false;
        }

        lock (SchedulingAgents)
        {
            return !SchedulingAgents.Contains(agent);
        }
    }

    private static bool CanAttemptSmartUnload(WorkerGoapAgent agent, Goal goal, CreatureBase creature, Storage storage)
    {
        if (agent.AgentOwner is not IPathfindingAgent pathfindingAgent)
        {
            return false;
        }

        var firstResource = storage.GetSingleResource();
        if (firstResource == null)
        {
            return false;
        }

        var minimumPriority = ZonePriority.None;
        if (HaulingPriorityRules.TryGetGoalSourcePriority(goal, creature, out var sourcePriority))
        {
            minimumPriority = HaulingPriorityRules.GetRequiredMinimumPriority(sourcePriority, minimumPriority);
        }

        return PathfinderUtil.FindNearestStorage(pathfindingAgent, firstResource, minimumPriority, false) != null;
    }

    private sealed class GoalEndContext
    {
        public GoalEndContext(WorkerGoapAgent agent, CreatureBase creature, Goal goal, GoalCondition condition, int carryCount)
        {
            Agent = agent;
            Creature = creature;
            Goal = goal;
            Condition = condition;
            CarryCount = carryCount;
        }

        public WorkerGoapAgent Agent { get; }

        public CreatureBase Creature { get; }

        public Goal Goal { get; }

        public GoalCondition Condition { get; }

        public int CarryCount { get; }
    }
}
