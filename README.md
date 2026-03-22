# SmartHauling

SmartHauling is a coordinated hauling mod for Going Medieval built as a BepInEx runtime plugin.

The project focuses on:

- centralized stockpile task planning
- coordinated worker assignment
- mixed pickup and multi-drop hauling
- locality-aware refills
- safer, more testable planner code

## Status

The repository contains the active runtime plugin and a growing unit test suite for the pure planning and scoring layers.

This repository does not include:

- game binaries
- copied proprietary game code
- decompiled game sources

## Repository Layout

- `runtime/SmartHauling.Runtime/`: plugin source
- `runtime/SmartHauling.Runtime.Tests/`: unit tests for planner and helper logic
- `docs/`: project notes and build/runtime guidance

## Requirements

- Windows
- .NET SDK
- A local Going Medieval installation
- BepInEx 5 for the game runtime

## Build

```powershell
dotnet build .\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj -c Release
```

If your game is not installed in the default Steam path, pass `GameDir` explicitly:

```powershell
dotnet build .\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Going Medieval"
```

## Test

```powershell
dotnet test .\runtime\SmartHauling.Runtime.Tests\SmartHauling.Runtime.Tests.csproj -c Release
```

## Install

1. Install BepInEx 5 into the game directory.
2. Build the plugin.
3. Copy the generated `SmartHauling.Runtime.dll` into:

```text
<Going Medieval>\BepInEx\plugins\SmartHauling.Runtime\
```

## Development Notes

- The runtime plugin references the local game installation during build.
- Planner and scoring changes should be validated with both tests and in-game traces.
- Public-facing changes should follow the repository rules in `AGENTS.md`.

## Legal and Publishing Notes

- Keep the repository source-only.
- Do not add decompiled game code or copied game assets.
- Prefer original design notes and clean-room documentation over implementation dumps from the game.
