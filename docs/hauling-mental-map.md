# Hauling Mental Map

This is a clean-room mental model of hauling.

- `Vanilla` = validated core flow plus a small amount of clearly marked inference.
- `SmartHauling` = the actual runtime shape of this mod.

## Vanilla hauling

```text
WorkerGoapAgent scheduler
    |
    v
pick next goal
    |
    v
StockpileHaulingGoal.FindAndProcessTargets
    |
    +--> choose source pile(s)
    +--> choose storage target(s)
    +--> queue A/B targets
    |
    v
HaulingBaseGoal.GetNextAction
    |
    v
vanilla hauling action chain
    |
    +--> go to source
    +--> pickup from pile
    +--> find best storage
    +--> reserve storage slots
    +--> go to storage
    +--> store resource on stockpile
    +--> complete if owner storage is empty
```

Notes:
- The stockpile target building and hauling action chain are validated against the local game assembly.
- The scheduler top part is a simplified mental model, not a copied source dump.
- Vanilla storage actions operate on `GetSingleResource()`, so the carry/store loop is effectively single-resource oriented.

## SmartHauling

```text
                        +----------------------+
                        | SmartHaulingPlugin   |
                        | RuntimeServices init |
                        +----------+-----------+
                                   |
                                   v
                     +-------------+--------------+
                     | Harmony patch entry points |
                     +-------------+--------------+
                                   |
          +------------------------+------------------------+
          |                         |                        |
          v                         v                        v
+----------------------+  +----------------------+  +----------------------+
| Lifecycle hooks      |  | Goal trigger hooks   |  | Stockpile plan hook  |
| Goal.Tick / End /    |  | WorkerGoapAgent.Tick |  | FindAndProcessTargets|
| unload redirect      |  | force next goal      |  | replace target build |
+----------+-----------+  +----------+-----------+  +----------+-----------+
           |                         |                         |
           +-------------------------+-------------------------+
                                     |
                                     v
                        +------------+-------------+
                        | StockpileTaskBoard       |
                        | central snapshot/leases  |
                        +------------+-------------+
                                     |
                                     v
                +--------------------+---------------------+
                | Planning services                         |
                | source policy / slice / score / dest plan|
                +--------------------+---------------------+
                                     |
                                     v
                        +------------+-------------+
                        | Coordinated task store   |
                        | immutable task on goal   |
                        +------------+-------------+
                                     |
                                     v
                        +------------+-------------+
                        | Custom executor          |
                        | pickup -> refill -> drop |
                        +------------+-------------+
                                     |
                                     v
                        +------------+-------------+
                        | Reservation / world /    |
                        | time infrastructure      |
                        +--------------------------+
```

## Where the patch enters the original flow

```text
WorkerGoapAgent.StartTicker
  -> RuntimeActivationPatch

WorkerGoapAgent.Tick
  -> CoordinatedStockpileGoalTriggerPatch

StockpileHaulingGoal.FindAndProcessTargets
  -> HaulingDecisionTracePatch

HaulingBaseGoal.GetNextAction
  -> CoordinatedStockpileExecutorPatch

ResourceActions.PickupResourceFromPile
  -> ResourceActionPatch

StorageActions.FindBestStorage
StorageActions.ReserveAndQueueStoragePlaces
StorageActions.CompleteIfOwnerStorageIsEmpty
ResourceActions.StoreResourceOnStockpile
  -> StorageDecisionTracePatch

Goal.Tick / Goal.EndGoalWith / WorkerGoapAgent.OnGoalEnded / CreatureBase.DropStorage
  -> lifecycle + unload patches
```

## Short version

```text
vanilla: worker-local goal + stockpile-local target finding + vanilla executor
smart:   worker trigger assist + central task board + custom executor
```

## Provenance categories

SmartHauling now treats stockpile hauling by origin, not just by goal type:

- `PlayerForced`
  - explicit player order
  - keep the selected item as the anchor pickup
  - optionally extend locally to use remaining carry capacity, including mixed loads when worthwhile
- `LocalCleanup`
  - immediate nearby cleanup after production/gathering style goals
  - treated as a separate context from general free-form hauling
- `AutonomousHaul`
  - normal free-form hauling
  - eligible for smart takeover
