# Changelog

All notable changes to this project will be documented in this file.

This file follows a lightweight Keep a Changelog style.

## [2.3.0] - 2026-03-28

### Added

- Provenance-aware stockpile haul routing for `PlayerForced`, `LocalCleanup`, and `AutonomousHaul` contexts.
- Short-lived worker intent tracking for board-triggered and player-forced hauling decisions.
- Recent-goal origin tracking and targeted tests for haul origin classification.

### Changed

- Refactored stockpile planning helpers out of the main hauling patch into dedicated policy, state, topology, sweep, mixed-source, and diagnostics components.
- Board-owned smart executor takeover is now gated more narrowly to explicitly coordinated stockpile tasks.
- Player-forced hauling documentation now reflects anchor-first pickup with local carry-filling extension behavior.
- Smart hauling status text handling now normalizes leaked placeholder keys through localized fallback labels.

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
