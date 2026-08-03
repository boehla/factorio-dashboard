# Supply Chain Planner – Planungsdokument

## Ziel

Eine interaktive Übersicht, in der man für jedes Item definieren kann, **wie** es bereitgestellt wird (lokal gebaut vs. per Train), mit rekursiver Auflösung der Zutaten, automatischer Train-Netzwerk-Prüfung und persistenter Speicherung in der Datenbank.

---

## 1. Datenmodell (SQLite)

### Neue Tabelle: `supply_chains`

Speichert eine vom Benutzer konfigurierte "Versorgungskette" für ein Ziel-Item.

```sql
CREATE TABLE supply_chains (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    target_item TEXT NOT NULL,          -- z.B. "py-science-pack-2"
    recipe_name   TEXT NOT NULL,          -- gewähltes Rezept
    name          TEXT,                   -- optionaler Freitextname für den Benutzer
    surface       TEXT NOT NULL DEFAULT 'nauvis',
    target_per_min REAL,                  -- gewünschte Zielrate (items/min), null = keine Vorgabe
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
```

### Neue Tabelle: `supply_chain_nodes`

Die einzelnen Knoten im Baum. Jeder Knoten entspricht einem Ingredient des übergeordneten Recipes.

```sql
CREATE TABLE supply_chain_nodes (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    chain_id        INTEGER NOT NULL REFERENCES supply_chains(id) ON DELETE CASCADE,
    parent_node_id  INTEGER REFERENCES supply_chain_nodes(id) ON DELETE CASCADE, -- NULL = root
    item_name       TEXT NOT NULL,          -- dieses Ingredient
    item_type       TEXT NOT NULL DEFAULT 'item',  -- 'item' | 'fluid'
    amount_per_craft REAL NOT NULL,         -- Menge pro Craft des Parent-Rezepts
    source          TEXT NOT NULL DEFAULT 'local', -- 'local' | 'train' | 'ignore'
    child_recipe    TEXT,                   -- falls local: gewähltes Rezept zur Herstellung
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_supply_chain_nodes_chain ON supply_chain_nodes(chain_id);
CREATE INDEX idx_supply_chain_nodes_parent ON supply_chain_nodes(parent_node_id);
```

### Neue Tabelle: `train_network_cache`

Cache der im Train-Netzwerk verfügbaren Items, aktualisiert bei jedem Snapshot.

```sql
CREATE TABLE train_network_cache (
    surface     TEXT NOT NULL,
    item_name   TEXT NOT NULL,
    item_type   TEXT NOT NULL DEFAULT 'item',  -- 'item' | 'fluid'
    role        TEXT NOT NULL,                 -- 'provide' | 'request'
    station_count INTEGER NOT NULL DEFAULT 1,
    last_seen   TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (surface, item_name, item_type, role)
);
```

---

## 2. Backend (C# – Fdash.Api / Fdash.Analysis)

### 2.1 Neue API-Endpunkte

Alle unter `/api/supply-chain/`:

| Methode | Pfad | Beschreibung |
|---------|------|-------------|
| `GET` | `/api/supply-chain` | Liste aller gespeicherten Chains (target_item, recipe, name, updated) |
| `GET` | `/api/supply-chain/{id}` | Ganzen Baum einer Chain laden |
| `POST` | `/api/supply-chain` | Neue Chain anlegen (target_item, recipe_name, surface, target_per_min) |
| `PUT` | `/api/supply-chain/{id}` | Chain umbenennen / Rezept ändern / target_per_min |
| `DELETE` | `/api/supply-chain/{id}` | Chain samt aller Nodes löschen |
| `PUT` | `/api/supply-chain/{id}/nodes` | Gesamten Node-Baum ersetzen (Batch-Update) |
| `GET` | `/api/supply-chain/train-availability?surface=nauvis` | Alle im Train-Netz verfügbaren Items (liefern + abnehmen) |
| `GET` | `/api/supply-chain/recipes/{item}` | Alle Rezepte für ein Item, mit erforscht/verfügbar-Status |
| `GET` | `/api/supply-chain/plan/{id}` | ProductionPlan für eine Chain berechnen (via ProductionPlanner) |

### 2.2 Neue Service-Klassen

**`SupplyChainService`** (in `Fdash.Api` oder neues Projekt `Fdash.SupplyChain`):
- CRUD für `supply_chains` und `supply_chain_nodes`
- Baut den Baum aus gespeicherten Nodes zusammen
- Validiert: Recipe muss existieren, Ingredients müssen zum Recipe passen
- Nutzt vorhandene `RecipeQuery` für Rezeptdaten und `PrototypeExporter` für Prototypen

**`TrainNetworkCacheService`**:
- Wird bei jedem Stations-Snapshot (`stations@surface`) aus dem `SnapshotBus` getriggert
- Parst Station-Namen via `StationNames` und schreibt in `train_network_cache`
- `GetAvailableItems(surface)` liefert deduplizierte Liste aller Items mit `provide`-Rolle

### 2.3 Erweiterung bestehender Klassen

**`Program.cs`**:
- DI-Registrierung für `SupplyChainService`, `TrainNetworkCacheService`
- Neue Route-Mapping-Gruppe `app.MapSupplyChainRoutes()`

**`RecipeQuery`** (vorhanden in `Fdash.Analysis`):
- Neue Methode: `GetRecipesForItem(itemName, surface)` – liefert alle Rezepte, die dieses Item produzieren, mit Zusatzinfo ob das Rezept bereits in Nutzung ist (Maschinen laufen) und ob die Technologie erforscht ist

---

## 3. Frontend (React/TypeScript – `web/`)

### 3.1 Neue Seite / View

Erweiterung in `App.tsx`: Dritter View-Modus `"planner"` neben `"dashboard"` und `"graph"`.

Neue Komponente: **`SupplyChainPlanner`** (`web/src/components/SupplyChainPlanner.tsx`)

### 3.2 Komponenten-Hierarchie

```
SupplyChainPlanner
├── ChainList (linke Seitenleiste)
│   ├── "Neue Chain" Button
│   └── Liste gespeicherter Chains (target_item, recipe, last_updated)
│       └── pro Eintrag: auswählen, umbenennen, löschen
│
└── ChainEditor (Hauptbereich)
    ├── ChainHeader
    │   ├── Item-Selektor (SearchInput für Item-Name)
    │   └── Recipe-Selektor (Dropdown/Umschalter wenn >1 Rezept)
    │
    └── IngredientTree (rekursiver Baum)
        └── IngredientNode (rekursiv)
            ├── ItemIcon + Name + Typ (item/fluid)
            ├── Menge pro Craft
            ├── Source-Toggle: [Lokal bauen] | [Train] | [Bus/Import]
            ├── Status-Indikator:
            │   ├── 🟢 Train: im Netzwerk verfügbar (provide)
            │   ├── 🟡 Train: nur als Request vorhanden
            │   ├── 🔴 Train: nicht im Netzwerk
            │   ├── 🟢 Local: Rezept konfiguriert, Zutaten definiert
            │   ├── 🟡 Local: Rezept gewählt, aber Zutaten noch offen
            │   └── ⚪ Local: kein Rezept gewählt
            ├── Wenn Local → Recipe-Selektor für dieses Item
            │   └── Dann rekursiv IngredientTree für dessen Zutaten
            └── Aktionen: [Entfernen] [Als erledigt markieren]
```

### 3.3 Datenfluss & State-Management

```typescript
// Types (in lib/types.ts ergänzen)

interface SupplyChain {
  id: number;
  targetItem: string;
  recipeName: string;
  name: string | null;
  surface: string;
  nodes: SupplyChainNode[];
  createdAt: string;
  updatedAt: string;
}

interface SupplyChainNode {
  id?: number;
  itemName: string;
  itemType: 'item' | 'fluid';
  amountPerCraft: number;
  source: 'local' | 'train' | 'bus';
  childRecipe: string | null;
  children: SupplyChainNode[];
}

interface TrainAvailability {
  provides: Record<string, { itemType: string; stationCount: number }>;
  requests: Record<string, { itemType: string; stationCount: number }>;
}

interface RecipeOption {
  name: string;
  isResearched: boolean;
  isProducing: boolean;        // Maschinen laufen bereits damit
  machineCount: number;
}
```

**State im `SupplyChainPlanner`:**

```typescript
const [chains, setChains] = useState<SupplyChain[]>([]);
const [activeChainId, setActiveChainId] = useState<number | null>(null);
const [trainAvailability, setTrainAvailability] = useState<TrainAvailability>({ provides: {}, requests: {} });
```

**Datenabruf:**
- Beim Mount: `GET /api/supply-chain` (Liste) + `GET /api/supply-chain/train-availability` (Train-Daten)
- Bei Auswahl: `GET /api/supply-chain/{id}` (ganzer Baum)
- Train-Daten werden zusätzlich via SignalR live aktualisiert (Snapshot `stations@nauvis` → Client parst die Station-Namen neu)

### 3.4 Live-Update-Mechanismus

1. SignalR-Snapshot für `stations@surface` kommt rein
2. Client parst die Station-Namen (analog zu `StationNames.cs` auf dem Server, aber in TypeScript)
3. Vergleicht mit vorherigem Stand → wenn neues Item als `provide` auftaucht → `trainAvailability` updaten
4. `IngredientNode`-Komponenten reagieren via `useMemo`/`useEffect`: wenn vorher 🔴 und jetzt in `trainAvailability.provides` → wird 🟢
5. Optionale `react-hot-toast` Notification: "🟢 iron-plate is now available via train"

### 3.5 Interaktionen

- **Item auswählen**: `SearchInput` mit Autocomplete aus `items`-Prototypen + Icon-Vorschau
- **Rezept wechseln**: Tabs/Buttons, "Use this recipe" setzt `recipe_name` in der Chain
- **Source togglen**: Click auf `[Lokal]`/`[Train]` wechselt den Modus
  - Wechsel zu Train → `childRecipe` = null, `children` = [] (keine weitere Auflösung)
  - Wechsel zu Local → wenn noch kein Recipe → zeigt Recipe-Auswahl
- **Baum expandieren/kollabieren**: Click auf Pfeil-Icon
- **Speichern**: "Save" Button → `PUT /api/supply-chain/{id}/nodes` mit gesamten aktuellen Baum
- **Auto-Save**: Optional nach jeder Änderung mit Debounce (2s)

---

## 4. Implementierungsschritte

### Phase 1 – Datenbank & Backend-Grundgerüst
1. SQLite-Migration: Tabellen `supply_chains`, `supply_chain_nodes`, `train_network_cache` anlegen
2. `SupplyChainService` mit CRUD-Operationen
3. `TrainNetworkCacheService` mit Parser für Station-Namen
4. API-Routen einrichten

### Phase 2 – Rezept-Integration
5. `RecipeQuery.GetRecipesForItem()` erweitern
6. `/api/supply-chain/recipes/{item}` Endpoint
7. `/api/supply-chain/train-availability` Endpoint

### Phase 3 – Frontend
8. TypeScript-Types ergänzen
9. `SupplyChainPlanner`-Komponente (Grundgerüst mit ChainList + ChainEditor)
10. `IngredientNode`-Komponente (rekursiv)
11. `ItemSelector`/`RecipeSelector`-Komponenten
12. Train-Status-Indikatoren + Live-Update via SignalR

### Phase 4 – Feinschliff
13. Auto-Save mit Debounce
14. Export/Import einer Chain als JSON
15. Visuelle Aufbereitung (Icons, Farben, Tooltips)
16. Integration in die Hauptnavigation (neuer "Planner"-Tab)

---

## 5. Technische Details & Entscheidungen

### 5.1 Rezept-Auflösung

- Nutzt die bereits existierende `RecipeQuery` aus `Fdash.Analysis`
- Prototypen liegen bereits als `prototypes.json` vor und werden von `PrototypeExporter` geladen
- Für den Client reichen wir die relevanten Rezept-Strukturen als JSON durch

### 5.2 Train-Netzwerk-Erkennung

- Die Mod liefert in `stations` bereits gruppierte Station-Namen
- `StationNames.cs` parst Rich-Text-Markup `[item=...]` / `[fluid=...]` / `[virtual-signal=signal-output]`
- Diese Logik wird 1:1 für den C#-Cache und als TypeScript-Port fürs Frontend-Live-Update benötigt

### 5.3 Forschung / Verfügbarkeit

- Der Mod liefert im `research_state` Job sowohl `current` (laufende Forschung) als auch `candidates` (forschbare Technologien)
- `TechGraph` in `Fdash.Analysis` kann auflösen, welche Technologien bereits erforscht sind
- Daraus lässt sich ableiten, welche Rezepte bereits **freigeschaltet** sind
- Ein Rezept gilt als **verfügbar**, wenn:
  - Die Technologie erforscht ist **oder**
  - Bereits Maschinen mit diesem Rezept laufen (via `assemblers`-Snapshot)

### 5.4 Datenhaltung & Persistenz

- SQLite reicht aus – die Chains sind kleine JSON-Bäume
- Alternativ wäre eine reine JSON-Datei denkbar, aber SQLite ist bereits im Stack
- `supply_chain_nodes` als adjacency list (parent_node_id) statt nested JSON – einfacher queryable, standard SQL

### 5.5 SignalR Live-Updates

- Der `stations@nauvis` Snapshot wird bereits jetzt über SignalR gepusht
- Wir hängen uns im Frontend in diesen Snapshot ein und parsen die Station-Namen clientseitig
- Das vermeidet zusätzlichen Server-Roundtrip und ist sofort reaktiv

---

## 6. Beispielablauf

1. Benutzer öffnet "Planner", klickt "Neue Chain"
2. Wählt via SearchInput: `py-science-pack-2`
3. Es gibt 1 Rezept → wird automatisch ausgewählt
4. Ingredients erscheinen:
   - `blank-tech-card` → Benutzer stellt auf **Train**
     - → 🔴 Nicht im Netzwerk → klickt auf "Rezept wählen"
     - → Wählt Rezept, setzt auf **Local**
     - → dessen Zutaten erscheinen rekursiv
   - `advanced-circuit` → bereits auf 🔵 **Bus** (manuell setzbar)
   - `chemical-science-pack` → 🟢 **Train** (wird erkannt, da im Netzwerk)
5. Später baut jemand eine Station für `blank-tech-card` → Live-Update macht den Train-Indikator 🔴→🟢
6. Benutzer speichert → Chain ist nach Server-Neustart wieder da

---

## 7. Offene Fragen / Entscheidungen

- [ ] **Fluids**: Immer per Pipe? Oder via Train (fluid wagon)? → Vermutlich auch `local`/`train`-Umschalter
- [ ] **Bus/Import**: Brauchen wir eine dritte Source-Option für "kommt vom Main-Bus"?
- [ ] **Mengen-Berechnung**: Soll die Chain auch Mengen skalieren können? ("Ich brauche 60/min science pack 2, wie viele Maschinen pro Stufe?") – könnte `ProductionPlanner` integrieren
- [ ] **Export/Share**: Soll man Chains exportieren/importieren können? (JSON-Download)
- [ ] **Mehrere Chains parallel anzeigen**: Tab-Toggle oder Multi-View?

---

## 8. Zusammenfassung

| Aspekt | Lösung |
|--------|--------|
| Datenhaltung | SQLite (`supply_chains` + `supply_chain_nodes`) |
| Rezept-Daten | Wiederverwendung `RecipeQuery` + `prototypes.json` |
| Train-Erkennung | `StationNames`-Parser (C# Server + TS Client) |
| Live-Update | SignalR `stations@` Snapshot → Client parst Station-Namen |
| Forschung | `research_state` + `assemblers` → abgeleitet |
| Frontend | Neue React-Komponente, rekursiver Baum |
| Persistenz | Automatisch via API → SQLite |
