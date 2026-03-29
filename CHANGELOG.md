# Changelog

All notable changes to this project will be documented in this file.

This file follows a lightweight Keep a Changelog style.

## [2.3.0] - 2026-03-28

### Added

- Haul-origin aware routing for `PlayerForced`, `LocalCleanup`, and `AutonomousHaul`.
- Worker intent tracking for board-triggered and player-forced hauling takeover.
- Broader automated test coverage for routing, recovery, and capacity logic.

### Changed

- Refactored stockpile planning into clearer policy/state/topology/execution components.
- Destination choice now follows `priority-first` ordering with capacity-aware multi-storage allocation.
- Unload execution now supports incremental same-storage bursts and planned cross-storage spillover.
- Pickup/unload sequencing now uses shared route optimization to reduce zig-zag paths.
- Diagnostic tracing now defaults to opt-in and runs through batched background writing.

### Fixed

- Fixed pickup/drop loops caused by invalid or disposed pickup targets.
- Fixed dismantling/production interference by tightening smart mixed-collect takeover boundaries.
- Fixed urgent/priority hauling behavior so marked priority piles remain first-class in smart extension.
- Fixed multiple smart-hauling stall cases with stronger recovery and watchdog diagnostics.
- Fixed logging/performance regressions caused by synchronous high-volume trace output.

## [2.2.0] - 2026-03-24

### Changed

- Forced hauling availability probes now use a short-lived worker cache instead of rematerializing the same board decision every idle tick.
- Stockpile board assignment rebuilds now run only when board state changes instead of on every probe call.

### Fixed

- Reduced idle hauling overhead for colonies with many workers competing for stockpile tasks.

## [2.1] - 2026-03-24

### Changed

- Forced stockpile hauling now only bypasses vanilla goal selection when `Hauling` is the worker's unique highest priority.
- Tie and multi-highest priority setups now fall back to vanilla scheduling instead of forcing hauling immediately.
- Smart hauling actions are marked in the worker status text with a ` (smart)` suffix after localization.

### Fixed

- Smart hauling status text no longer shows raw placeholder action keys in the worker UI.

## [0.2.0] - 2026-03-23

### Added

- Configurable diagnostic trace levels: `Off`, `Error`, `Info`, `Trace`
- Configurable stall watchdog for recovering some stuck hauling and unload goals
- Construction delivery augmentation for nearby same-material building sites
- UI refresh patch so dropped items disappear from the worker inventory panel immediately

### Changed

- Coordinated unload now plans nearby drop targets more sensibly
- Smart unload fallback uses mixed-carry unload logic instead of a single-resource path
- Stockpile hauling uses better locality and carry-capacity fill behavior
- Runtime structure, documentation, and tests were cleaned up for public release

### Fixed

- Several cases where hauling goals could stall until manually reset
- Cases where workers kept carrying items after interrupted unload flows
- Cases where newly dropped inventory items stayed visible in the UI until the next inventory update
- Cases where construction delivery underused available carry capacity
