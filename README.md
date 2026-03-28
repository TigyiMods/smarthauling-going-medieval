# SmartHauling: Going Medieval Mod

```text
Settler without SmartHauling

   (o_o)
   <)   )/
    /   \

carries one thing

...walks back...

Settler with SmartHauling

   (o_o)
  <)   )>
   /   \

carries mixed loads

...fills the bag...
```

SmartHauling is an unofficial hauling mod for Going Medieval.

## What It Does

SmartHauling changes hauling from mostly local worker decisions to centrally planned stockpile work, with mixed-resource pickup and carry-capacity maximization.

- one central board leases hauling tasks
- settlers do not compete for the same hauling source at the same time
- settlers can carry more than one resource type in the same haul
- pickup tries to fill remaining carry capacity
- drop tries to empty the carry before new pickup work starts

## Intent Rules

SmartHauling does not treat every stockpile haul the same way.

- `PlayerForced`
  - right-click `Prioritize hauling to stockpile`
  - keeps the selected item as the first pickup anchor
  - may extend the haul with nearby worthwhile pickups if there is room, including mixed loads when that helps fill remaining carry capacity
  - avoids large retargets away from the chosen local area
- `LocalCleanup`
  - short follow-up cleanup after nearby production or gathering work
  - tracked separately from general autonomous hauling so immediate cleanup can be reasoned about differently
- `AutonomousHaul`
  - general stockpile hauling not tied to a forced player order or immediate local cleanup
  - this is the main path where the smart planner takes over

## Smart Unloading

`Smart unloading` is the safe unload phase after a worker is already carrying items.

- it does not mean "go find more items"
- it means "finish storing what is already in the worker's inventory"
- the goal is to avoid bad carry cleanup and reduce cases where items are dropped or left half-finished

## Install

1. Install BepInEx 5 into the game directory.
2. Build `SmartHauling.Runtime.dll` from this repository, or use a packaged build if you have one.
3. Copy the DLL into:

```text
<Going Medieval>\BepInEx\plugins\SmartHauling.Runtime\
```

4. Restart the game.

## Configuration

- The mod writes its settings to:

```text
<Going Medieval>\BepInEx\config\hu.tigyi.goingmedieval.smarthauling.runtime.cfg
```

- To control diagnostic logging, set:

```ini
[Tracing]
EnableDiagnosticTrace = true
DiagnosticTraceLevel = Off

[Behaviour]
EnableStallWatchdog = true
StallWatchdogTimeoutSeconds = 10
```

- Valid values for `DiagnosticTraceLevel`: `Off`, `Error`, `Info`, `Trace`
- `StallWatchdogTimeoutSeconds` is clamped to a safe minimum to avoid over-aggressive goal aborts

## Unofficial

This project is not official and is not affiliated with, endorsed by, or supported by the game developers or publisher.

## Disclaimer

- This mod changes Going Medieval runtime behaviour and can cause unexpected behaviour, errors, or mod conflicts.
- Use it at your own risk.
- Back up your saves before testing new versions.
- The software is provided without warranty; see [LICENSE](LICENSE).

## Author

- [tigyijanos](https://github.com/tigyijanos)

## Dependencies

- [Going Medieval](https://mythwright.com/games/going-medieval)
- [BepInEx 5](https://docs.bepinex.dev/)
- [Windows](https://www.microsoft.com/en-us/windows/)

## Legal

- This repository's own code is licensed under [MIT](LICENSE).
- Third-party dependencies and platform terms are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- This repository is source-only.
- It does not include game binaries, copied game assets, or decompiled game source.
- You need your own licensed local game installation to build the project.

## Documentation

- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Developer guide: [docs/developer-guide.md](docs/developer-guide.md)
- Development setup: [docs/development-setup.md](docs/development-setup.md)
- Runtime flow: [docs/architecture-flow.md](docs/architecture-flow.md)
- Hauling mental map: [docs/hauling-mental-map.md](docs/hauling-mental-map.md)
- Feasibility notes: [docs/smart-hauling-feasibility.md](docs/smart-hauling-feasibility.md)
