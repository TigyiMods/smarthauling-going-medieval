# Going Medieval Smart Hauling: Feasibility Notes

## Context

Ezek a megállapítások a helyi gépen lévő telepítés alapján készültek.

- Játék útvonal: `C:\Program Files (x86)\Steam\steamapps\common\Going Medieval`
- Játékverzió: `1.0.52` a `Player.log` alapján
- Hivatalos lokális mod mappa: `Documents\Foxy Voxel\Going Medieval\Mods`
- Workshop modok: `C:\Program Files (x86)\Steam\steamapps\workshop\content\1029780`

## Rövid válasz

A kívánt viselkedés, vagyis:

- a settler ne csak egyféle kupacot húzzon össze,
- production közben is vegyesen vegyen fel más releváns nyersanyagokat,
- "ha már arra megy" alapon opportunista pickupot csináljon,

nem látszik megoldhatónak pusztán a hivatalos JSON modrétegen keresztül.

Részleges hauling QoL javítás igen.
Valódi smart hauling nagy valószínűséggel csak runtime patch-csel.

## Learning Note

Ez egy dekompiláció-alapú megvalósíthatósági vizsgálat.
A használt minta: először a data-driven modréteget térképezem fel, utána azonosítom a valódi kódbeli patchpontokat.
Ez azért lett választva, mert a játék hivatalos modtámogatása JSON repository override-ra épül, nem dokumentált script API-ra.
Reális alternatíva lett volna a vak JSON-próbálgatás vagy az azonnali Harmony patch-elés.
A fő tradeoff: lassabb indulás, viszont nem rossz rétegben kezdünk el modot írni.

## Amit a hivatalos modréteg biztosan tud

Az aktuális hivatalos guide szerint a játék JSON repository-kat tölt be, és a modok ezekhez tudnak fájlokat adni vagy meglévőket felülírni.

Releváns támogatott fájlok a hauling témához:

- `GOAP/Job.json`
- `GOAP/JobPriority.json`
- `Worker/GoalPreference.json`
- `Worker/WorkerBase.json`
- `Human/HumanType.json`
- `Data/ObjectActionData.json`
- `Creature/ScheduleModelRepository.json`
- `Constructables/UniversalStorage.json`
- `Resources/Production.json`

Helyi bizonyíték:

- a `Player.log` szerint a játék tényleg a `Data\Models\*.json` modfájlokat tölti be,
- a gépen lévő workshop modok is ezt a mintát használják,
- például a `Settlers - Stronger Workers` csak a `HumanType.json`-t override-olja.

## Ami már most is látszik a saját gépen

A gépen jelenleg aktív a `Settlers - Stronger Workers` mod, ami a worker carry capacity-t `60`-ról `90`-re emeli.

Ez fontos, mert:

- a capacity buff önmagában már ki van próbálva,
- ha a fájdalompont még mindig fennáll, akkor a core probléma tényleg nem pusztán carry capacity.

## Dekompliált viselkedés: a lényeg

### 1. `HaulingBaseGoal`

A hauling alaplogika külön osztályban van:

- `NSMedieval.Goap.Goals.HaulingBaseGoal`

Talált viselkedés:

- van beépített multi-pickup közeli kupacokra,
- a közelségi limit `9f`,
- mozgás közben hívja az `InjectPilesInProximityRange(...)` logikát,
- ez csak ugyanazon `Blueprint` típusú kupacokat injektálja a queue-ba,
- csak olyan kupacot vesz figyelembe, ami `CanBeHauled`.

Következmény:

- vanilla hauling már tud "szedj fel még ugyanebből a típusból" viselkedést,
- vegyes item pickup nincs ebben a kódban.

### 2. `StockpileHaulingGoal`

A normál hauling cél:

- `NSMedieval.Goap.Goals.StockpileHaulingGoal`

Talált viselkedés:

- kiválaszt egy haulolható kupacot,
- lefoglalja,
- kiszámolja a maximális hordható mennyiséget,
- ha még van hely, keres a közelben további kupacokat,
- de megint csak ugyanazon `Blueprint` típusból.

Következmény:

- az alap "bulk haul" itt valóban létezik,
- de ez egytípusú pickup.

### 3. `ProductionBaseGoal.PrepareCollectStep`

A production input összeszedése külön pipeline:

- `NSMedieval.Goap.Goals.ProductionBaseGoal`
- különösen: `PrepareCollectStep(...)`

Talált viselkedés:

- a production collect nem a normál `StockpileHaulingGoal` útját használja,
- végigkeresi az elérhető resource pile-okat,
- kiszűri azokat a stockpile/storage helyeket, amelyek `CanBeUsedInProduction == false`,
- reservationt rak a jelöltekre,
- de a queue végén csak az első kiválasztott `Blueprint` típushoz tartozó kupacokat tartja meg,
- a többi reservált, de más típusú kupacot elengedi,
- az `collectAmount` is egyetlen pickup-menet logikájára van felhúzva.

Következmény:

- a production collect jelenleg szándékosan egytípusú pickup felé tolja a viselkedést,
- pont ez üti a "menet közben hozzon még más raw materialt is" igényt.

### 4. `TryToHaulProducedItems`

Ugyanebben a production goalban van egy külön QoL ág:

- `TryToHaulProducedItems(...)`

Talált viselkedés:

- production végén, ha a workernek aktív a hauling jobja,
- a rendszer képes átváltani `StockpileHaulingGoal`-ra,
- és megpróbálja a frissen elkészült terméket elhordatni.

Következmény:

- a fejlesztők az ilyen QoL viselkedéseket célzott, külön kódrétegekben oldják meg,
- nem egyetlen globális hauling paraméterrel.

### 5. `StockpileInstance` és production engedély

Talált property:

- `NSMedieval.Stockpiles.StockpileInstance.CanBeUsedInProduction`

Következmény:

- a production rendszer külön figyeli, hogy egy tárolóhely használható-e production inputnak,
- tehát a "dedikált production input" logika tényleg külön kezelt rendszer.

### 6. `Storage`

A belső inventory/storage réteg:

- `NSMedieval.Components.Storage`

Talált viselkedés:

- a storage nem egyetlen slotból áll,
- több `ResourceInstance` elemet képes tárolni,
- tehát elvi szinten a worker inventory tud többféle erőforrást is tartani egyszerre.

Következmény:

- a vegyes pickup nem fizikai inventory-korlát miatt hiányzik,
- hanem goal/collect/reservation logika miatt.

## Mit lehet JSON moddal reálisan javítani

- worker carry capacity
- storage kategóriák és priority-k
- bizonyos goal preference / job grouping súlyok
- speciális storage-k hozzáadása
- állatok hauling képessége

Ezek hasznosak lehetnek, de nem adják meg a kívánt smart haulingot.

## Mit nem valószínű, hogy megold JSON-ból

- mixed-item opportunistic pickup production közben
- "ha már a konyhába megy, vigyen még másik raw materialt is"
- production reservation és haul batching közös optimalizálása
- több célpont láncolása egyetlen okos útvonal alapján

## Javasolt implementációs irány

Ha tényleg a kívánt viselkedést akarjuk, a legjobb következő lépés egy runtime mod prototípus.

Elsődleges patchpontok:

1. `NSMedieval.Goap.Goals.ProductionBaseGoal.PrepareCollectStep`
2. `NSMedieval.Goap.Goals.HaulingBaseGoal.InjectPilesInProximityRange`
3. szükség esetén `NSMedieval.Goap.Goals.StockpileHaulingGoal.FindAndProcessTargets`
4. reservation oldalon: `NSMedieval.Manager.ReservationManager`

## Reális terv

1. Harmony/BepInEx alapú proof of concept a `1.0.52` buildre.
2. A production collect logika átírása úgy, hogy többféle blueprintet is queue-olhasson.
3. Per-pile / per-resource pickup count kezelés, mert a jelenlegi `collectAmount` túl egyszerű ehhez.
4. Tesztelés:
   - konyha
   - műhely
   - hordó/fuel
   - production-only storage
   - manual haul és auto haul

## Gyakorlati végkövetkeztetés

Ha az a cél, hogy "jobb legyen a hauling", arra a JSON mod jó.

Ha az a cél, hogy:

- a settler vegyesen pakoljon,
- production menetben opportunistán optimalizáljon,
- és tényleg útvonalalapon hordjon,

akkor ezt a jelenlegi adatok alapján runtime patch-ként érdemes kezelni, nem sima workshop JSON modként.

## Külső források

- Hivatalos modding guide: `https://steamcommunity.com/sharedfiles/filedetails/?id=3362039703`
- Steam guide frissítési dátum a guide oldalon: eredetileg `2024-11-11`, frissítve `2026-01-28`
- A guide támogatott JSON listája tartalmazza a releváns AI/GOAP/Human/Worker repository-kat, de nem ad külön scripted hauling API-t
- Steam news / patch notes gyűjtőoldalról látszik, hogy `2024-06-03` körül ismert issue volt: a settlerek nem mindig a legközelebbi production buildinget választják
