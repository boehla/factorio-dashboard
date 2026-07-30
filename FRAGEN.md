# Offene Fragen — bitte beantworten

> **Historisches Dokument.** Fragen und Antworten stammen aus der RCON-Fassung. Zwei Punkte
> sind durch den Mod-Umbau erledigt: Frage 1 (Achievements) stellt sich nicht mehr, weil der
> Mod ohne `/silent-command` auskommt — dafür deaktiviert schon seine bloße Anwesenheit als
> Nicht-Vanilla-Mod die Achievements. Und die Sorge um den Ressourcen-Voll-Scan ist mit dem
> rollierenden Chunk-Scan gegenstandslos. Der Rest gilt weiter.

Ich habe den Plan Phase 0–6 als lauffähiges Gerüst umgesetzt (C#-Backend baut, Tests grün; React-Frontend gebaut). An einigen Stellen musste ich Annahmen treffen oder brauche eine Entscheidung von dir, bevor es gegen deinen echten Server produktiv geht. Antworten am besten direkt unter der jeweiligen Frage.

## A. Blockierende Grundsatzfragen (aus Plan §10)

1. **Achievements** — `/silent-command` deaktiviert Achievements dauerhaft für das Save. Ist das mit deinen Mitspielern geklärt und ok?
   → Antwort: ja ok

2. **Erreichbarkeit / Auth** — Läuft der Collector auf derselben Maschine wie der Server (dann `--rcon-bind 127.0.0.1`, keine Auth nötig), oder muss das Dashboard über LAN/Reverse-Proxy erreichbar sein? Bei letzterem baue ich Basic Auth vor Hub + Research-Endpoint ein.
   → Antwort: ja auf der selben maschine

3. **Auto-Research aktiv schreiben** — Soll der Automatismus wirklich selbstständig Forschung setzen, oder erstmal nur Vorschau ("würde X setzen")? Aktuell: default OFF, Toggle im Frontend, Preview wenn OFF.
   → Antwort: wenn auto reasearch im forntent aktiv -> forschung setzen

## B. Verbindungs- & Serverdaten (brauche ich zum echten Testen)

4. **RCON-Zugang** — Host, Port, Passwort deines Servers (oder ist Port 27015 / lokal ok)? Steht in `src/Fdash.Api/appsettings.json` unter `Collector`.
   → Antwort: ja sollte

5. **Gemeinsamer Dateizugriff** — Kann der Collector das `script-output/`-Verzeichnis des Servers direkt lesen (gleiche Maschine/Volume)? Wenn ja, bitte den Pfad → schnellerer Prototyp-Export. Wenn nein, nutze ich den RCON-Fallback.
   → Antwort: ja sollte

6. **Save-Setup** — Nur ein Save, oder wechselt ihr zwischen mehreren? (Multi-Save via Seed-Fingerprint ist eingebaut; nur relevant, falls ihr zwei Saves aus demselben Seed fahrt — dann `save_name` in den Hash aufnehmen.)
   → Antwort: es könnte zwischen den saves gewechselt werden

## C. Kalibrierung (kann ich erst gegen euer echtes Save messen — Plan §10.2)

7. **Tick-Kosten** — Wie groß ist eure Basis grob (Anzahl Assembler / Karte)? Der Assembler-Full-Scan alle 5s und die Ressourcen-Chunk-Größe (Start: 200 Chunks/Poll) müssen gegen die echte Tick-Zeit kalibriert werden. Die adaptive Drosselung senkt die Chunk-Größe automatisch, aber ein Startwert von dir hilft.
   → Antwort: keine chuck abfrage -> wenns zu viel wird umbauen auf dedicated mod der im spiel mitläuft

8. **Poll-Intervalle** — Sind die Default-Intervalle ok (Power/Assembler/Züge/Logistik 5s, Produktion/Plattformen 10s, Drills 30s, Ressourcen-Vollzyklus ~100s)? Oder lieber schonender?
   → Antwort: passt

## D. Feature-Details, bei denen ich eine Annahme getroffen habe

9. **Alert-Schwellwerte** (§10.4) — Ich habe sie konfigurierbar gemacht mit Defaults: Strom-Warnung < 95 %, Treibstoff niedrig < 25 %, Stall-Warnung ab 60s. Passen die, oder andere Werte?
   → Antwort: ja passt

10. **Orbital Requests** (§3.7) — Die Space-Age-Platform-Logistics-API hat sich zwischen 2.0-Releases geändert. Ich habe das Snippet defensiv gebaut (`get_logistic_point` → sections/filters), aber es muss gegen deine konkrete Factorio-Version verifiziert werden. Welche Version läuft (z.B. 2.0.x)?
   → Antwort: es soll auf die aktuelle (2.1.12 experimental laufen)

11. **Plattform-Treibstoffkapazität** — Ich nehme aktuell 24000 als Thruster-Fluid-Max an (Platzhalter). Kennst du den echten Wert / soll ich ihn aus den Thruster-Prototypen + verbundenen Tanks summieren (aufwändiger)?
   → Antwort: ok, einfachhalber 

12. **Icons** (§10.6) — Modded Item-Icons aus den Mod-Zips extrahieren (Aufwand) oder erstmal weglassen und nur Textnamen zeigen? Aktuell: nur Text.
   → Antwort: bitte extrahieren

13. **Getaggte Circuits** (§3.9) — Konvention ist Präfix `FDASH:` in der Combinator-Beschreibung. Passt das, oder lieber Backer-Name/anderes Präfix?
   → Antwort: passt

## E. Technische Nachfragen zum Weiterbau

14. **DB-Ablageort** — SQLite-Datei liegt aktuell relativ zum Api-Prozess (`fdash.db`). Soll sie an einen festen Pfad (z.B. neben dem Save oder in ein Datenverzeichnis)?
   → Antwort: passt

15. **Deployment-Ziel** — Linux oder Windows für den Collector? Ich kann ein fertiges `dotnet publish`-Profil (`linux-x64` bzw. `win-x64`, self-contained) hinterlegen.
   → Antwort: derzeit windows

16. **Prioritäten für den nächsten Schritt** — Was soll ich zuerst gegen den echten Server härten, sobald du die Zugangsdaten hast? (Vorschlag: Phase 0 Verbindungstest → Power/Assembler → dann der riskante Ressourcen-Scan, Plan §9.)
   → Antwort: ja machen wir sobald diese fragen implementiert sind

