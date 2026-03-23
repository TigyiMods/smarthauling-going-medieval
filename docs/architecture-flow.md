# SmartHauling Runtime Flow

## High-level flow

1. `SmartHaulingPlugin` initializes `RuntimeServices`.
2. Harmony patch adapters intercept the game's hauling lifecycle.
3. `Coordination/StockpileTaskBoard` builds and leases centralized hauling work.
4. `Planning/*` components evaluate source eligibility, slice source patches, score candidates, and build destination plans.
5. `Execution/CoordinatedStockpileExecutor` executes the assigned plan in phases:
   - pickup
   - local refill
   - drop
   - cleanup
6. `Infrastructure/*` provides narrow runtime boundaries for:
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

This keeps the hauling flow readable without introducing a heavy DI container or interface-per-class ceremony.
