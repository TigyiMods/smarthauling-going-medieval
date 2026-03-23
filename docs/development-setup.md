# Development Setup

## Prerequisites

- Windows
- .NET SDK
- a local Going Medieval installation
- BepInEx 5 in the game directory if you want to run the built plugin immediately

## GameDir

The project compiles against the game's local managed DLLs:

- `Assembly-CSharp.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`

`GameDir` is the path to your local Going Medieval installation.

### Option A: pass it on the command line

```powershell
dotnet build .\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Going Medieval"
```

### Option B: use a local `Directory.Build.props`

Create a `Directory.Build.props` file in the repository root:

```xml
<Project>
  <PropertyGroup>
    <GameDir>D:\SteamLibrary\steamapps\common\Going Medieval</GameDir>
  </PropertyGroup>
</Project>
```

`Directory.Build.props` is gitignored, so this stays local to your machine.

## Build

If the game is installed in the default Steam path:

```powershell
dotnet build .\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj -c Release
```

Otherwise use `-p:GameDir=...` or `Directory.Build.props`.

## Test

```powershell
dotnet test .\runtime\SmartHauling.Runtime.Tests\SmartHauling.Runtime.Tests.csproj -c Release
```

## Format

```powershell
dotnet format .\smarthauling.slnx
```

## Manual Deploy

Copy the built DLL to:

```text
<Going Medieval>\BepInEx\plugins\SmartHauling.Runtime\
```

## Runtime Configuration

The plugin uses the standard BepInEx config file:

```text
<Going Medieval>\BepInEx\config\hu.tigyi.goingmedieval.smarthauling.runtime.cfg
```

Example:

```ini
[Tracing]
EnableDiagnosticTrace = true
DiagnosticTraceLevel = Off
```

`DiagnosticTraceLevel` supports `Off`, `Error`, `Info`, and `Trace`.

## Repository Notes

- Runtime code: `runtime/SmartHauling.Runtime/`
- Tests: `runtime/SmartHauling.Runtime.Tests/`
- Contribution rules: [AGENTS.md](../AGENTS.md)

## Architecture Docs

- Runtime flow: [architecture-flow.md](architecture-flow.md)
- Hauling mental map: [hauling-mental-map.md](hauling-mental-map.md)
