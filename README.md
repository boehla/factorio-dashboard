# Factorio Dashboard

Zweitbildschirm-Dashboard für Factorio (2.0 / Space Age): **Factorio-Mod** als Datenquelle +
ASP.NET-Core-API mit SignalR + React-Frontend.

Das Repo enthält beide Hälften:

```
mod/fdash-exporter/   Der Factorio-Mod (Lua). Sammelt budgetiert und schreibt Snapshots.
src/                  Der Webserver (C#). Liest Snapshots, speichert Zeitreihen, serviert das UI.
web/                  Das Frontend (React + TS + Vite + Tailwind + uPlot).
```

## Warum ein Mod statt RCON

Die erste Fassung holte alles über RCON `/silent-command`. Das funktioniert — aber jeder Poll
führt die komplette Auswertung **in einem einzigen Game-Tick** aus. Ein
`find_entities_filtered{type="assembling-machine"}` läuft über jede Entity der Oberfläche; auf
einer großen modded Basis sind das sechsstellige Zahlen, mehrmals pro Minute. Das Ergebnis war
spürbares Ruckeln.

Der Mod dreht das um: Er hält seine eigene Sicht auf die Fabrik, aktualisiert sie pro Tick nur
ein Stück weit unter einem **harten Budget**, und gibt einen fertigen Snapshot heraus. Das
Abholen kostet dann nichts mehr, weil nichts mehr zu rechnen ist.

| | vorher (RCON-Snippets) | jetzt (Mod) |
| --- | --- | --- |
| Wo wird gerechnet | im Tick, beim Poll | über viele Ticks verteilt |
| Kosten pro Tick | wächst mit der Fabrik | fest (`fdash-entity-budget`) |
| Entity-Listen | jedes Mal neu gesucht | einmal gescannt, per Events gepflegt |
| Erz-Scan | Voll-Scan alle 60 s | rollierend, ein paar Chunks pro Tick |
| Erz unter Bohrern | ein Area-Scan **pro Bohrer**, alle 60 s | pro Bohrer gecacht, alle 10 min erneuert |
| Rezept-Ableitungen | bei jedem Poll über alle Rezepte | einmal pro Session |
| RCON nötig | ja | nur für Auto-Research |

Details und Tuning: [`mod/fdash-exporter/README.md`](mod/fdash-exporter/README.md).

## Einrichten

### 1. Mod bauen und installieren

```bash
powershell -ExecutionPolicy Bypass -File .\mod\build-mod.ps1 -Install
```

Das packt `mod/fdash-exporter` zu `mod/dist/fdash-exporter_<version>.zip` und legt sie in
`%APPDATA%\Factorio\mods`. Für einen dedizierten Server die ZIP dorthin kopieren, wo dessen
`mods`-Ordner liegt. Danach Factorio bzw. den Server neu starten.

Beim ersten Start scannt der Mod die Karte einmal durch (gestückelt). Auf großen Karten dauert
das ein bis zwei Minuten; solange meldet `meta.exporter.scanning` den Fortschritt.

### 2. Webserver konfigurieren

`src/Fdash.Api/appsettings.json`:

- `Collector:Transport` — `Auto` (Default), `File` oder `Rcon`.
- `Collector:ScriptOutputPath` — Factorios `script-output`-Verzeichnis. Der Mod legt darunter
  `fdash/` an. `%APPDATA%` wird aufgelöst.
- `Collector:PollIntervalMs` — wie oft nach einem neuen Snapshot gesehen wird (Default 1000).

**Transportwege**

- **Datei** (bevorzugt): Dashboard und Factorio sehen dasselbe Dateisystem. Kein
  RCON-Passwort, kein Tick-Slot pro Abruf.
- **RCON**: Dashboard läuft woanders. Braucht `Collector:RconPassword`. Der Mod rechnet auch
  hier nichts im Tick — die Kommandos geben nur fertige Strings zurück.

`Auto` nimmt die Datei, sobald der Mod dorthin schreibt, sonst RCON.

**Das RCON-Passwort gehört nicht ins Repo.** Entweder
`src/Fdash.Api/appsettings.Local.json` (per `.gitignore` ausgeschlossen, Vorlage liegt als
`appsettings.Local.example.json` daneben) oder als Umgebungsvariable:

```bash
Collector__RconPassword=geheim
```

RCON ist nur für **Auto-Research** zwingend — der einzige schreibende Pfad. Ohne RCON zeigt
das Dashboard weiterhin einen Forschungsvorschlag an, setzt ihn aber nicht.

### 3. Icons (optional)

Die Zuordnung Prototyp → PNG kommt aus `data-raw-dump.json`, weil die Runtime-API keine
Icon-Pfade herausgibt. Einmalig — und nach jeder Mod-Änderung — erzeugen:

```
"C:\Program Files\Factorio\bin\x64\factorio.exe" --dump-data
```

Landet in `%APPDATA%\Factorio\script-output\data-raw-dump.json`. Ohne den Dump wird nur über
den Dateinamen geraten; Items mit abweichendem Icon-Namen bleiben ohne Bild.

## Bauen und starten

```bash
dotnet build FactorioDashboard.sln
dotnet run --project tests/Fdash.Tests      # Selbsttests
```

Frontend (Build landet in `src/Fdash.Api/wwwroot` → ein Prozess, ein Port):

```bash
cd web
npm install
npm run build
```

Starten:

```bash
dotnet run --project src/Fdash.Api          # http://localhost:5000
```

Unter Windows tut es auch `start.bat` (nimmt die veröffentlichte EXE, falls vorhanden).
Dev-Frontend mit Hot-Reload separat: `cd web && npm run dev` (Port 5173, proxyt `/api` und
`/hub` auf 5000).

Self-contained veröffentlichen:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
.\publish\Fdash.Api.exe
```

## Prüfen, ob Daten ankommen

```
http://localhost:5000/api/health
```

zeigt Transportweg, ob geschrieben werden kann, ob die Prototypen geladen sind und den
Rohstatus des Mods.

Ohne laufendes Backend geht auch das Diagnose-Tool — es listet jeden Job mit Alter und
Payload-Größe:

```bash
FDASH_SCRIPT_OUTPUT="%APPDATA%\Factorio\script-output" dotnet run --project tests/Fdash.LiveCheck
```

Oder über RCON: `FDASH_HOST`, `FDASH_PORT`, `FDASH_PASS`.

Im Spiel: `/fdash-status`.

## Architektur

```
Factorio + fdash-exporter
   │  budgetierte Sweeps, Double-Buffer, vorserialisiertes JSON
   ▼
script-output/fdash/  ──oder──  RCON remote.call("fdash","snapshot")
   │
   ▼
GameLink (Transportwahl)  →  CollectorService
                                 ├─ SnapshotBus  → SignalR → React
                                 ├─ SqliteTimeSeriesStore (Roll-up-Tiers)
                                 ├─ StallDetector
                                 └─ AutoResearchService  ──RCON──▶  set_research
```

Die Job-Intervalle stehen im Mod, nicht mehr im Server — der Server liest nur ab, was da ist,
und erkennt an einem Zeitstempel pro Job, was sich geändert hat.

## Veröffentlichen

Vor dem ersten Upload auf das Mod-Portal: [`RELEASING.md`](RELEASING.md).

## Lizenz

MIT — siehe [`mod/fdash-exporter/LICENSE`](mod/fdash-exporter/LICENSE).
