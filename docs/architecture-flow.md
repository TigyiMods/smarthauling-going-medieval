# SmartHauling Runtime Flow

## High-level flow

1. `SmartHaulingPlugin` initializes `RuntimeServices`.
2. Harmony patch adapters intercept the game's hauling lifecycle.
3. `Coordination/StockpileTaskBoard` builds and leases centralized hauling work.
4. `Coordination/RecentGoalOriginStore` captures the recent-goal context for later provenance checks.
5. `Patches/Stockpile/HaulingDecisionTracePatch` classifies each stockpile haul as:
   - `PlayerForced`
   - `LocalCleanup`
   - `AutonomousHaul`
6. `Planning/*` components evaluate source eligibility, slice source patches, score candidates, and build destination plans.
7. `Execution/CoordinatedStockpileExecutor` executes the assigned plan in phases:
   - pickup
   - local refill
   - drop
   - cleanup
8. `Infrastructure/*` provides narrow runtime boundaries for:
   - time
   - world snapshots
   - reservations
   - tracing/common helpers

## Folder responsibilities

- `Composition/`: runtime composition root and service wiring.
- `Coordination/`: shared orchestration state and task leasing.
- `Planning/`: pure or mostly-pure planning rules and score calculations.
- `Execution/`: hauling task execution state and helpers.
- `Patches/`: Harmony adapters that translate game callbacks into runtime services.
  - `Stockpile/`: stockpile hauling orchestration hooks.
  - `Production/`: production collect hooks.
  - `Lifecycle/`: goal lifecycle and scheduler hooks.
  - `Shared/`: cross-cutting resource action hooks.
- `Infrastructure/`: engine-facing adapters and low-level support.

## Design intent

The target shape is:

- patches stay thin
- planning stays deterministic and testable
- execution owns phase transitions
- infrastructure isolates direct game API calls

The current stockpile intent split is:

- `PlayerForced` keeps the player's first pickup anchor and may extend locally to use the remaining carry budget
- `LocalCleanup` marks immediate nearby cleanup context so it can be handled separately from general hauling
- `AutonomousHaul` is the main free-form smart-takeover path

This keeps the hauling flow readable without introducing a heavy DI container or interface-per-class ceremony.

