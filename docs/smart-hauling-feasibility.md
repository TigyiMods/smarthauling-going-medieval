# Smart Hauling Feasibility Notes

## Short Answer

The target behavior for this mod is beyond simple data tuning.

Examples of the desired behavior:

- coordinated hauling across multiple workers
- mixed pickup while a worker is already moving through an area
- capacity-aware refill before switching to drop-off
- priority-aware stockpile-to-stockpile moves without ping-pong

Those requirements justify a runtime plugin rather than a JSON-only mod.

## Why a Runtime Plugin

The official data-driven mod layer is useful for:

- carry-capacity tuning
- job priority tuning
- storage setup and categorization
- basic worker preference tuning

It is not a good fit for:

- centralized worker orchestration
- dynamic source patch slicing
- mixed pickup and multi-drop execution
- temporary leases and coordination between concurrent workers

## Chosen Approach

The repository uses a BepInEx runtime plugin with Harmony patches and a custom hauling orchestration layer.

The current design direction is:

1. central stockpile task board
2. worker-independent task seeds
3. coordinated executor for pickup and drop phases
4. testable planner and scoring helpers

## Practical Scope

The goal is not to replace the entire game AI.

The current implementation is intentionally scoped to stockpile hauling behavior:

- stockpile task planning
- worker assignment
- coordinated pickup
- coordinated drop-off
- unload recovery and fallback handling

## Tradeoff

This approach is more complex than static mod data overrides, but it gives control over the parts that actually matter for smart hauling:

- planning
- coordination
- capacity usage
- destination selection

That tradeoff is acceptable for a behavior-focused mod.
