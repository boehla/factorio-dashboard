# Veröffentlichen

## Stand

| | |
| --- | --- |
| Mod-Name `fdash-exporter` auf dem Portal | frei (Stand 30.07.2026) |
| Thumbnail, Changelog, Locale, LICENSE | vorhanden |
| CI (`.github/workflows/ci.yml`) | baut Backend + Frontend, läuft Selbsttests und Lua-Prüfungen |
| Backend kompiliert | **ungeprüft** — auf der Entwicklungsmaschine war kein .NET-SDK installiert |
| Mod in Factorio geladen | **ungeprüft** — dort war kein Factorio installiert |

Die beiden letzten Punkte sind das eigentliche Restrisiko. Der erste Push löst den ersten
über CI; der zweite braucht einen echten Start des Spiels (siehe „Vor dem Upload testen").


## Vor dem allerersten Commit

Das Repo enthielt bis zum Mod-Umbau Dinge, die nicht öffentlich werden dürfen. `.gitignore`
fängt sie ab — aber nur, wenn sie noch nie committet wurden. In einem frischen Repo also
zuerst prüfen:

```bash
git status --short
```

Auf dieser Liste darf **nichts** davon auftauchen:

- `fdash.db`, `*.db-wal`, `*.db-shm` — die Zeitreihen-Datenbank (im Arbeitsstand ~460 MB;
  GitHub lehnt Dateien über 100 MB ohne LFS ab).
- `appsettings.Local.json` — enthält das RCON-Passwort.
- `publish/`, `bin/`, `obj/`, `node_modules/`.

Außerdem stand in `src/Fdash.Api/appsettings.json` bis zum Mod-Umbau ein RCON-Passwort im
Klartext. Der Wert ist jetzt leer. **Gilt dieses Passwort noch auf einem erreichbaren Server,
ändere es** — es hat lange in einer Datei gestanden, die inzwischen öffentlich ist. Eine
lokale Kopie liegt weiterhin in `publish/appsettings.json` (nicht im Repo, aber auf der
Platte). Und falls du dieses Verzeichnis je mit vorhandener Git-History veröffentlichst:
alte Commits enthalten es weiterhin — dann ist ein frisches Repo ohne History der
einfachere Weg.

## Platzhalter im Mod

Erledigt für `boehla` / `github.com/boehla/factorio-dashboard`: `author`, `contact` und
`homepage` in `mod/fdash-exporter/info.json` sowie die Copyright-Zeilen in
`mod/fdash-exporter/LICENSE` und der Root-`LICENSE`.

Nachziehen musst du nur, falls dein **Mod-Portal-Benutzername** vom GitHub-Namen abweicht —
dann `author` in `info.json` anpassen. Die CI warnt, solange irgendwo noch `SET-ME` steht.

Der interne Name `fdash-exporter` war am 30.07.2026 noch frei
(<https://mods.factorio.com/mod/fdash-exporter> → 404). Ist er inzwischen vergeben, muss er in `info.json`
**und** an drei Stellen im Code geändert werden (`remote.add_interface("fdash", …)` bleibt
davon unberührt — das ist der Interface-Name, nicht der Mod-Name):

```bash
grep -rn "fdash-exporter" mod/fdash-exporter/
```

Das Thumbnail (`mod/fdash-exporter/thumbnail.png`, 144×144) liegt bereit und wird von
`build-mod.ps1` automatisch mitgepackt. Neu erzeugen lässt es sich aus dem Quellbild in
`mod/assets/`:

```bash
powershell -ExecutionPolicy Bypass -File .\mod\make-thumbnail.ps1
```

Das Skript verkleinert schrittweise statt in einem Sprung — bei 1024 → 144 am Stück fransen
die dünnen Linien (Chart-Kurve, Fensterrahmen) sonst aus.

## Mod bauen

```bash
powershell -ExecutionPolicy Bypass -File .\mod\build-mod.ps1
```

Erzeugt `mod/dist/fdash-exporter_<version>.zip` mit genau einem Ordner
`fdash-exporter_<version>/` auf oberster Ebene — so will es Factorio.

Lokal testen vor dem Upload:

```bash
powershell -ExecutionPolicy Bypass -File .\mod\build-mod.ps1 -Install
```

## Vor dem Upload testen

1. Factorio starten, ein großes Save laden.
2. `/fdash-status` — `scanning` muss von `true` auf `false` gehen.
3. `script-output/fdash/` prüfen: `index.json`, `snapshot-N.json`, `prototypes.json`.
4. `dotnet run --project tests/Fdash.LiveCheck` mit gesetztem `FDASH_SCRIPT_OUTPUT`.
5. F5 im Spiel → Debug-Overlay, auf die Tick-Zeit schauen. Der Mod sollte im Rauschen
   verschwinden; falls nicht, `fdash-entity-budget` senken und den Unterschied vergleichen.
6. Save/Load-Zyklus: Speichern, laden, `/fdash-status` — die Registry-Zahlen müssen stehen
   bleiben, kein erneuter Erstscan.

## Version erhöhen

Bei jeder Änderung:

1. `version` in `mod/fdash-exporter/info.json`.
2. Neuen Block in `mod/fdash-exporter/changelog.txt`. Das Format ist streng — die Trennlinie
   muss **genau 99 Bindestriche** haben, `Date:` als `YYYY-MM-DD`, Kategorien eingerückt mit
   zwei Leerzeichen und Einträge mit vier.
3. Ändert sich das Snapshot-Format inkompatibel: `PROTOCOL` in
   `mod/fdash-exporter/scripts/publish.lua` **und** `ModSnapshotParser.SupportedProtocol` in
   `src/Fdash.Core/ModSnapshot.cs` erhöhen.

## Portal-Upload

<https://mods.factorio.com/upload>. Beschreibung: der Inhalt von
`mod/fdash-exporter/README.md` passt weitgehend, Portal-Markdown kann allerdings keine
Tabellen — die vorher in Listen umschreiben.

Wichtig für die Beschreibung: dass der Mod **ohne das Dashboard nichts tut**. Er hat keine
GUI, keine Entities, keine Rezepte. Wer ihn ohne Gegenstück installiert, sieht nur Dateien in
`script-output/`.
