# Runtime Loader Choice

## Decision

For this project, the practical runtime loader is the `BepInEx 5` line.

Why:

- Going Medieval is a Unity Mono game
- the project builds against the game's managed assemblies
- BepInEx 5 remains the stable choice for this type of Unity runtime

## Recommended Loader

- Project: `BepInEx`
- Release line: `5.4.x`
- Recommended version: `5.4.23.4`
- Platform: `win_x64`

Primary source:

- `https://github.com/BepInEx/BepInEx/releases`

## Build

```powershell
dotnet build .\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj -c Release
```

If your game is installed in a non-default location:

```powershell
dotnet build .\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Going Medieval"
```

## Install Flow

1. Install BepInEx into the game directory.
2. Start the game once so the BepInEx folder structure is created.
3. Build the plugin.
4. Copy `SmartHauling.Runtime.dll` into:

```text
<Going Medieval>\BepInEx\plugins\SmartHauling.Runtime\
```

## Why This Choice

This repository is focused on runtime behavior changes, not data-only tuning.

That means the loader must support:

- Harmony patching
- runtime plugin initialization
- stable interaction with the game's managed assemblies

BepInEx 5 is the lowest-risk option for that setup.
