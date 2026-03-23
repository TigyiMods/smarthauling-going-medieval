# Developer Guide

## What GOAP means here

GOAP stands for Goal-Oriented Action Planning.

In practice, for this mod it means:
- a worker has a current goal
- each game tick the worker can keep that goal, finish it, fail it, or switch to another goal
- a goal produces small actions like go to source, pick up, go to stockpile, store
- the worker loops through that cycle every tick

So this is not one long script. It is a repeated decision loop.

## Vanilla mental model

```text
Worker tick
  -> pick a goal
  -> stockpile hauling goal builds local targets
  -> goal returns next action
  -> action succeeds / fails
  -> next tick re-evaluates
```

Vanilla hauling is mostly worker-local and largely single-resource.

## SmartHauling mental model

```text
Worker tick
  -> goal trigger patch checks carry / idle state
  -> StockpileTaskBoard assigns a central hauling task
  -> planner builds source slice + destination plan
  -> custom executor runs pickup -> refill -> drop
  -> lifecycle patches clean leases and fallback state
```

The important change is that hauling is no longer mostly "whatever this worker sees right now".
It becomes "the board assigns a shared task, then the worker executes it".

## Where to read the code

1. Plugin bootstrap: [SmartHaulingPlugin.cs](../runtime/SmartHauling.Runtime/SmartHaulingPlugin.cs)
2. Runtime service wiring: [RuntimeServices.cs](../runtime/SmartHauling.Runtime/Composition/RuntimeServices.cs)
3. Goal trigger and lifecycle hooks: `runtime/SmartHauling.Runtime/Patches/Lifecycle/` and `runtime/SmartHauling.Runtime/Patches/Stockpile/`
4. Central board: [StockpileTaskBoard.cs](../runtime/SmartHauling.Runtime/Coordination/StockpileTaskBoard.cs)
5. Planning rules: `runtime/SmartHauling.Runtime/Planning/`
6. Executor state machine: [CoordinatedStockpileExecutor.cs](../runtime/SmartHauling.Runtime/Execution/CoordinatedStockpileExecutor.cs)

## Read order for the stockpile flow

```text
SmartHaulingPlugin
  -> CoordinatedStockpileGoalTriggerPatch
  -> StockpileTaskBoard
  -> HaulingDecisionTracePatch
  -> StockpileClusterAugmentor / ResourceDestinationPlanCoordinator
  -> CoordinatedStockpileExecutor
  -> GoalLifecycleTracePatch / SafeUnloadRedirectPatch
```

## Why the large classes were a problem

The big classes were not only long. They mixed too many responsibilities:
- Harmony patch entry
- planning rules
- queue mutation
- reservation handling
- diagnostics

The current refactor direction is:
- patch files stay as adapters
- planners own planning logic
- execution classes own action sequencing
- coordination classes own leases and shared task state
