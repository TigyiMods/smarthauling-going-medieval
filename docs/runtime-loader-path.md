# Runtime Mod Loader Path

## Decision

A helyi buildhez a runtime mod utvonalhoz a praktikus valasztas a `BepInEx 5` vonal.

Miert:

- a jatek Unity `2022.3.62f2` Mono build,
- a helyi `Managed` mappaban van `netstandard.dll`,
- a BepInEx hivatalos release oldala szerint az `5.4.x` tovabbra is hasznalhato uj Unity Mono jatekokhoz,
- a `5.4.23.4` a jelenlegi LTS release.

## Learning Note

Ez egy loader-valasztasi dontes.
A hasznalt minta: a jatek runtime-jat, a cel frameworkot es a loader release politikajat egyutt nezem.
Ez azert jo, mert Unity moddingnal nem eleg csak a Unity verziot nezni, a Mono/IL2CPP es a target framework is szamit.
Alternativa lett volna BepInEx 6 vagy kezi Doorstop + sajat bootstrap.
A tradeoff: a BepInEx 5 kevesbe modern, viszont Mono jatekokra ma is a stabilabb ut.

## Recommended Loader

- Projekt: `BepInEx`
- Release csatorna: `5.4.x LTS`
- Javasolt verzio: `5.4.23.4`
- Platform: `win_x64`

Elsodleges forras:

- `https://github.com/BepInEx/BepInEx/releases`

## What This Repo Contains

Ez a repo most egy minimalis runtime plugin scaffoldot tartalmaz:

- [SmartHauling.Runtime.csproj](C:\Users\tigyi\Documents\GitHub\temp\gamemods\goingtomedieval\smarthauling\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj)
- [SmartHaulingPlugin.cs](C:\Users\tigyi\Documents\GitHub\temp\gamemods\goingtomedieval\smarthauling\runtime\SmartHauling.Runtime\SmartHaulingPlugin.cs)
- [ProductionCollectProbePatch.cs](C:\Users\tigyi\Documents\GitHub\temp\gamemods\goingtomedieval\smarthauling\runtime\SmartHauling.Runtime\Patches\ProductionCollectProbePatch.cs)
- [Deploy-To-BepInEx.ps1](C:\Users\tigyi\Documents\GitHub\temp\gamemods\goingtomedieval\smarthauling\runtime\Deploy-To-BepInEx.ps1)

Ez meg nem smart hauling implementacio.
Csak a runtime lancot kesziti elo es egy no-op probe patch-et ad.
A projekt `netstandard2.1` targettel mar sikeresen buildel a helyi `1.0.52` jatekbuiltre.

## Build

A projekt a helyi Steam telepitesre mutat alapbol:

- `C:\Program Files (x86)\Steam\steamapps\common\Going Medieval`

Build:

```powershell
dotnet build .\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj
```

Ha a jatek nem ezen az utvonalon van, build kozben felulirhato:

```powershell
dotnet build .\runtime\SmartHauling.Runtime\SmartHauling.Runtime.csproj -p:GameDir="D:\SteamLibrary\steamapps\common\Going Medieval"
```

## Install Flow

1. Toltsd le a `BepInEx_win_x64_5.4.23.4.zip` csomagot a hivatalos release oldalrol.
2. Csomagold ki a jatek gyokermappajaba:
   - `C:\Program Files (x86)\Steam\steamapps\common\Going Medieval`
3. Inditsd el egyszer a jatekot, hogy a `BepInEx` mappastruktura letrejojjon.
4. Buildeld ezt a projektet.
5. A plugin DLL-t masold ide:
   - `...\Going Medieval\BepInEx\plugins\SmartHauling.Runtime\`

Automatizalhato ezzel is:

```powershell
.\runtime\Deploy-To-BepInEx.ps1
```

## Next Implementation Targets

A smart hauling tenyleges patchpontjai tovabbra is ezek:

1. `NSMedieval.Goap.Goals.ProductionBaseGoal.PrepareCollectStep`
2. `NSMedieval.Goap.Goals.HaulingBaseGoal.InjectPilesInProximityRange`
3. `NSMedieval.Goap.Goals.StockpileHaulingGoal.FindAndProcessTargets`
4. `NSMedieval.Manager.ReservationManager`
