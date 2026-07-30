import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import ForceGraph2D from "react-force-graph-2d";
import { ItemIcon } from "./ItemIcon";
import { SearchInput } from "./SearchInput";
import { itemMatches } from "../lib/format";
import {
  GraphMeta, useGraphMeta, useRecipeMachineCounts, useActiveIngredients
} from "../lib/graphMeta";
import { ShortageTreeView, ROOT_COLOR } from "./ShortageTree";
import { buildShortageTree, useMissingMap, useRootCauseSet, type MissingMap } from "../lib/shortages";

interface GNode { id: string; isOre: boolean; isFluid: boolean }
interface GLink { source: string; target: string }

// Item-Icons fuer den Canvas vorladen (/api/icon/{name}). Kann kein <img>-DOM
// wiederverwenden, daher ein eigener Image-Cache. Fehlende Icons (viele Fluids,
// modded Items) bleiben unmarkiert -> Fallback-Kreis beim Zeichnen.
const iconCache = new Map<string, HTMLImageElement | null>();
function getIcon(name: string, onReady: () => void): HTMLImageElement | null {
  if(iconCache.has(name)) return iconCache.get(name) ?? null;
  const img = new Image();
  iconCache.set(name, null); // pending
  img.onload = () => { iconCache.set(name, img); onReady(); };
  img.onerror = () => { iconCache.set(name, null); };
  img.src = `/api/icon/${encodeURIComponent(name)}`;
  return null;
}

const ORE_COLOR = "#eab308";   // gelb: Startknoten (Erz)
const FLUID_COLOR = "#38bdf8"; // blau: Fluid
const ITEM_COLOR = "#9ca3af";  // neutral: normales Item
const SELECT_COLOR = "#f97316"; // orange: angeklickter Knoten
const MATCH_COLOR = "#22d3ee";  // cyan: Suchtreffer (kollidiert mit keiner anderen Rolle)

// Beziehung eines Nachbarn zum fokussierten Knoten, als Bitmaske:
// IN  = der Nachbar fliesst in das Item hinein (ist Zutat davon),
// OUT = der Nachbar entsteht aus dem Item (ist Produkt daraus),
// IN|OUT = beides, also ein Kreislauf (Recycling, Nebenprodukte).
const REL_IN = 1, REL_OUT = 2, REL_BOTH = 3;
const REL_COLOR: Record<number, string> = {
  [REL_IN]: "#a855f7",   // violett: Zutat
  [REL_OUT]: "#22c55e",  // gruen: Produkt
  [REL_BOTH]: "#ec4899"  // pink: beides
};
// Kantenfarben in derselben Logik, nur transparenter.
const REL_LINK: Record<number, string> = {
  [REL_IN]: "rgba(168,85,247,0.85)",
  [REL_OUT]: "rgba(34,197,94,0.85)"
};

function statusColor(name: string): string {
  if (name === "working") return "#22c55e";
  if (name === "full_output" || name === "output_full"
    || name === "waiting_for_space_in_destination" || name === "full_burned_result_output"
    || name === "disabled_by_control_behavior") return "#3b82f6";
  if (name === "no_power" || name === "low_power" || name === "no_fuel") return "#ef4444";
  if (name === "no_ingredients" || name === "item_ingredient_shortage"
    || name === "no_input_fluid" || name === "low_input_fluid"
    || name === "fluid_ingredient_shortage" || name === "waiting_for_source_items") return "#eab308";
  return "#6b7280"; // normal, disabled_by_script, …
}

function fmtPerMin(n: number | undefined): string {
  if (n == null || n === 0) return "—";
  if (n >= 100) return n.toFixed(0);
  if (n >= 10) return n.toFixed(1);
  return n.toFixed(2);
}

function ItemDetail({ itemName, meta, production, assemblers, missingMap, isRoot, onClose }: {
  itemName: string; meta: GraphMeta; production: any; assemblers: any;
  missingMap: MissingMap; isRoot: boolean; onClose: () => void;
}) {
  const itemData = (production?.items as any[])?.find((i: any) => i.item === itemName);
  const asmData = assemblers?.by_item?.[itemName] as any | undefined;
  const machineCounts = useRecipeMachineCounts(assemblers);
  const shortageTree = buildShortageTree(itemName, missingMap);
  const produced = itemData?.produced_per_min ?? 0;
  const consumed = itemData?.consumed_per_min ?? 0;
  const ratio = itemData?.ratio ?? 0;
  const total = asmData?.total ?? 0;
  const status = asmData?.status ?? {};
  const avgSpeed = asmData?.avg_speed ?? 0;
  const statusEntries = (Object.entries(status as Record<string, number>) as [string, number][])
    .filter(([, cnt]) => cnt > 0)
    .sort(([, a], [, b]) => b - a);
  // Nur Rezepte zeigen, die tatsaechlich in einer Maschine eingestellt sind —
  // die Prototyp-Liste enthaelt sonst hunderte nie gebaute Alternativrezepte.
  const madeBy = meta.recipes
    .filter(r => r.products.some(p => p.name === itemName) && (machineCounts.get(r.name) ?? 0) > 0)
    .sort((a, b) => (machineCounts.get(b.name) ?? 0) - (machineCounts.get(a.name) ?? 0));
  const usedIn = meta.recipes
    .filter(r => r.ingredients.some(i => i.name === itemName) && (machineCounts.get(r.name) ?? 0) > 0)
    .sort((a, b) => (machineCounts.get(b.name) ?? 0) - (machineCounts.get(a.name) ?? 0));

  const ratioColor = ratio < 0.95 ? "#ef4444" : ratio > 1.05 ? "#22c55e" : "#e5e7eb";

  return (
    <div className="flex flex-col gap-3 text-sm">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2 min-w-0">
          <ItemIcon name={itemName} size={22} />
          <span className="font-semibold truncate">{itemName}</span>
          {itemData?.type === "fluid" && (
            <span className="text-[10px] px-1 rounded shrink-0"
              style={{ color: FLUID_COLOR, border: `1px solid ${FLUID_COLOR}` }}>Fluid</span>
          )}
          {isRoot && (
            <span className="text-[10px] px-1 rounded shrink-0"
              title="Wurzel-Ursache: dieses Item fehlt anderen, ihm selbst fehlt nichts"
              style={{ color: ROOT_COLOR, border: `1px solid ${ROOT_COLOR}` }}>Ursache</span>
          )}
        </div>
        <button onClick={onClose} className="text-gray-500 hover:text-gray-300 text-lg leading-none">&times;</button>
      </div>

      <div>
        <div className="text-xs text-gray-500 mb-1">Produktion</div>
        <div className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5">
          <span className="text-gray-400">Prod/min</span>
          <span className="text-right text-ok tabular-nums">{fmtPerMin(produced)}</span>
          <span className="text-gray-400">Verbr/min</span>
          <span className="text-right tabular-nums">{consumed > 0 ? fmtPerMin(consumed) : <span className="text-gray-500">—</span>}</span>
          <span className="text-gray-400">Ratio</span>
          <span className="text-right tabular-nums" style={{ color: ratioColor }}>
            {ratio > 0 ? ratio.toFixed(2) : "—"} {ratio < 0.95 ? "↓" : ratio > 1.05 ? "↑" : "→"}
          </span>
          {avgSpeed > 0 && <>
            <span className="text-gray-400">Speed</span>
            <span className="text-right tabular-nums text-gray-300">{avgSpeed.toFixed(2)}x</span>
          </>}
        </div>
      </div>

      {total > 0 && (
        <div>
          <div className="text-xs text-gray-500 mb-1">Maschinen: {total}</div>
          <div className="flex flex-col gap-1 text-xs">
            {statusEntries.map(([name, cnt]) => {
              const color = statusColor(name);
              const pct = total > 0 ? (cnt / total) * 100 : 0;
              return (
                <div key={name} className="flex items-center gap-2">
                  <div className="flex-1 h-1.5 bg-gray-800 rounded overflow-hidden">
                    <div className="h-full rounded" style={{ width: `${pct}%`, backgroundColor: color }} />
                  </div>
                  <span className="w-6 text-right text-gray-300 tabular-nums">{cnt}</span>
                  <span className="text-gray-400 truncate" style={{ color }} title={name}>{name}</span>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {shortageTree.length > 0 && (
        <div>
          {/* Was den Maschinen dieses Items gerade fehlt, rekursiv bis zur Wurzel
              (rot). Quelle ist assemblers.by_item[*].missing des Snapshots. */}
          <div className="text-xs text-gray-500 mb-1">Fehlt gerade:</div>
          <div className="text-xs"><ShortageTreeView nodes={shortageTree} /></div>
        </div>
      )}

      {madeBy.length > 0 && (
        <div>
          <div className="text-xs text-gray-500 mb-1">Produziert von ({madeBy.length}):</div>
          <div className="flex flex-col gap-0.5 text-xs">
            {madeBy.map(r => {
              const cnt = machineCounts.get(r.name) ?? 0;
              const prodIo = r.products.find(p => p.name === itemName);
              const amt = prodIo?.amount ?? 1;
              const exact = cnt > 0 && r.energy > 0 ? cnt * (amt / r.energy) * avgSpeed * 60 : 0;
              return (
                <div key={r.name} className="flex justify-between">
                  <span className="truncate text-gray-300">{r.name}</span>
                  <span className="text-gray-500 ml-2 shrink-0 tabular-nums">
                    {cnt > 0 ? <>{cnt} · {fmtPerMin(exact)}/min</> : <>&mdash;</>}
                  </span>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {usedIn.length > 0 && (
        <div>
          <div className="text-xs text-gray-500 mb-1">Verwendet in ({usedIn.length}):</div>
          <div className="flex flex-col gap-0.5 text-xs">
            {usedIn.slice(0, 15).map(r => (
              <div key={r.name} className="flex justify-between text-gray-400">
                <span className="truncate">{r.name}</span>
                <span className="text-gray-500 ml-2 shrink-0">{machineCounts.get(r.name) ?? 0}</span>
              </div>
            ))}
            {usedIn.length > 15 && (
              <div className="text-gray-600">… +{usedIn.length - 15} weitere</div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

export function ProducingItemsGraph({ planet, production, assemblers }:
  { planet: string; production: any; assemblers: any }) {
  const meta = useGraphMeta();
  const [hover, setHover] = useState<string | null>(null);
  const [, forceRedraw] = useState(0); // erzwingt Neuzeichnen wenn Icons geladen sind
  const [size, setSize] = useState({ w: 0, h: 0 });
  const [selectedNode, setSelectedNode] = useState<string | null>(null);
  // Fluids sind abschaltbar: Wasser/Dampf haengen an sehr vielen Rezepten und
  // ziehen den Graphen sonst zu einem Knaeuel zusammen.
  const [showFluids, setShowFluids] = useState(true);
  // Optional: Bau-/Mall-Items ausblenden, die nur on the fly gecraftet werden.
  const [onlyIntermediates, setOnlyIntermediates] = useState(false);
  // Suche hebt nur hervor; Knoten/Kanten und die Kameraposition bleiben unberuehrt,
  // damit die Force-Simulation nicht bei jedem Tastendruck neu startet.
  const [search, setSearch] = useState("");
  const activeIngredients = useActiveIngredients(meta, assemblers);
  const missingMap = useMissingMap(assemblers);
  const rootSet = useRootCauseSet(assemblers);
  useEffect(() => { setSelectedNode(null); setSearch(""); }, [planet]);

  // Container-Groesse messen (ForceGraph2D braucht feste Pixel). Als Callback-Ref
  // statt useEffect: beim ersten Mount rendert die Komponente noch den
  // "Warte auf Produktionsdaten"-Platzhalter, der Container existiert also gar
  // nicht — ein Effekt mit [] haette nie einen Knoten zu beobachten bekommen und
  // der Graph waere auf seiner Startgroesse haengen geblieben.
  const observerRef = useRef<ResizeObserver | null>(null);
  const wrapRef = useCallback((el: HTMLDivElement | null) => {
    observerRef.current?.disconnect();
    observerRef.current = null;
    if(!el) return;
    setSize({ w: el.clientWidth, h: el.clientHeight });
    const ro = new ResizeObserver(() => setSize({ w: el.clientWidth, h: el.clientHeight }));
    ro.observe(el);
    observerRef.current = ro;
  }, []);

  // Graph-Daten nur neu berechnen, wenn sich die Menge der live-produzierten
  // Items tatsaechlich geaendert hat (nicht bei jedem Snapshot).
  const [data, setData] = useState<{ nodes: GNode[]; links: GLink[] }>({ nodes: [], links: [] });
  const prevFingerprintRef = useRef("");
  // Der Graph soll das Frame sofort ausfuellen. Frueher wurde erst in
  // onEngineStop gezoomt — bei cooldownTicks=120 stand der Graph also mehrere
  // Sekunden klein in der Mitte. Jetzt wird waehrend der Simulation
  // mitgezoomt (siehe onEngineTick), bis die Simulation ausgelaufen ist.
  // Danach nicht mehr, sonst wuerde jedes Nachwackeln die Kamera des Nutzers
  // zuruecksetzen; erst ein neuer Datensatz setzt fittedRef zurueck.
  const fgRef = useRef<any>(null);
  const fittedRef = useRef(false);

  useEffect(() => {
    if(!meta || !production?.items) {
      prevFingerprintRef.current = "";
      setData({ nodes: [], links: [] });
      return;
    }

    const oreSet = new Set(meta.oreItems);
    const fluidSet = new Set(meta.fluidNames);

    // Fingerprint = sortierte Liste aller Items mit produced_per_min > 0.
    // Fluids kommen seit der Erweiterung von production.lua als eigene Eintraege
    // (type = "fluid") mit und lassen sich per Schalter ausblenden.
    const live = new Set<string>(
      (production.items as any[])
        .filter((i: any) => (i.produced_per_min ?? 0) > 0)
        .map((i: any) => i.item as string)
        .filter((n: string) => showFluids || !fluidSet.has(n))
        .filter((n: string) => !onlyIntermediates || activeIngredients.has(n))
    );
    const fingerprint = `${showFluids ? "f" : "-"}${onlyIntermediates ? "i" : "-"}:`
      + [...live].sort().join(",");
    if(fingerprint === prevFingerprintRef.current) return;
    prevFingerprintRef.current = fingerprint;

    const nodes: GNode[] = [...live].map(n => ({
      id: n, isOre: oreSet.has(n), isFluid: fluidSet.has(n)
    }));

    // Kanten Zutat -> Produkt, nur wenn beide Endpunkte live-produziert sind
    // (Anti-Hairball). Dedup ueber source->target.
    const seen = new Set<string>();
    const links: GLink[] = [];
    for(const r of meta.recipes) {
      for(const ing of r.ingredients) {
        if(!live.has(ing.name)) continue;
        for(const prod of r.products) {
          if(ing.name === prod.name || !live.has(prod.name)) continue;
          const key = `${ing.name}->${prod.name}`;
          if(seen.has(key)) continue;
          seen.add(key);
          links.push({ source: ing.name, target: prod.name });
        }
      }
    }
    fittedRef.current = false;
    setData({ nodes, links });
  }, [meta, production, showFluids, onlyIntermediates, activeIngredients]);

  // Auswahl faellt weg, wenn der Knoten aus dem Graphen verschwindet (Fluid-
  // Schalter aus, Produktion gestoppt) — sonst haengt rechts ein Panel ohne Bezug.
  useEffect(() => {
    if(selectedNode && !data.nodes.some(n => n.id === selectedNode)) setSelectedNode(null);
  }, [data, selectedNode]);

  // Hover hat Vorrang, ohne Hover bleibt der angeklickte Knoten hervorgehoben —
  // sonst "verliert" man beim Mausbewegen den Knoten, dessen Details rechts stehen.
  const focus = hover ?? selectedNode;

  // Inzidente Knoten des fokussierten Knotens samt Beziehung (REL_IN/REL_OUT/REL_BOTH).
  const neighbors = useMemo(() => {
    const m = new Map<string, number>();
    if(!focus) return m;
    for(const l of data.links) {
      const src = typeof l.source === "object" ? (l.source as any).id : l.source;
      const tgt = typeof l.target === "object" ? (l.target as any).id : l.target;
      if(tgt === focus && src !== focus) m.set(src, (m.get(src) ?? 0) | REL_IN);
      if(src === focus && tgt !== focus) m.set(tgt, (m.get(tgt) ?? 0) | REL_OUT);
    }
    return m;
  }, [focus, data]);

  // Suchtreffer: Praefix-Treffer zuerst, dann alphabetisch — "iron" listet
  // iron-plate vor steel-plate-from-iron.
  const searching = search.trim() !== "";
  const matchList = useMemo(() => {
    if(!searching) return [];
    const q = search.trim().toLowerCase();
    return data.nodes
      .filter(n => itemMatches(n.id, search))
      .map(n => n.id)
      .sort((a, b) => {
        const pa = a.toLowerCase().startsWith(q) ? 0 : 1;
        const pb = b.toLowerCase().startsWith(q) ? 0 : 1;
        return pa - pb || a.localeCompare(b);
      });
  }, [data, search, searching]);
  const matches = useMemo(() => new Set(matchList), [matchList]);

  if(!production?.items) {
    return (
      <div className="bg-panel border border-panelborder rounded-lg p-6 text-gray-500 text-sm">
        Warte auf Produktionsdaten für <span className="capitalize">{planet}</span> …
      </div>
    );
  }

  const fluidCount = data.nodes.filter(n => n.isFluid).length;

  if(data.nodes.length === 0) {
    return (
      <div className="bg-panel border border-panelborder rounded-lg p-6 text-gray-500 text-sm">
        Keine aktuell produzierten Items auf <span className="capitalize">{planet}</span>.
      </div>
    );
  }

  return (
    // Hoehe kommt vom flex-Container in App (h-screen): flex-1 + min-h-0, damit
    // der Canvas den Rest des Viewports fuellt statt einer geschaetzten calc-Hoehe.
    <div className="flex gap-2 flex-1 min-h-0">
      <div className="flex-1 flex flex-col gap-2 min-h-0" style={{ minWidth: 0 }}>
        <div className="flex items-center gap-4 text-xs text-gray-400">
          <label className="flex items-center gap-1.5 cursor-pointer select-none">
            <input type="checkbox" checked={showFluids}
              onChange={e => setShowFluids(e.target.checked)} />
            <span style={{ color: FLUID_COLOR }}>Fluids</span>
            {showFluids && <span className="text-gray-600">({fluidCount})</span>}
          </label>
          <label className="flex items-center gap-1.5 cursor-pointer select-none"
            title="Nur Items, die Zutat eines aktiv eingestellten Rezepts sind — blendet Bau-/Mall-Items aus.">
            <input type="checkbox" checked={onlyIntermediates}
              onChange={e => setOnlyIntermediates(e.target.checked)} />
            <span>nur Zwischenprodukte</span>
          </label>
          <span><span style={{ color: ORE_COLOR }}>●</span> Erz</span>
          <span className="flex items-center gap-2"
            title="Farben der Nachbarknoten, sobald ein Item angeklickt oder überfahren wird.">
            <span style={{ color: REL_COLOR[REL_IN] }}>● Zutat</span>
            <span style={{ color: REL_COLOR[REL_OUT] }}>● Produkt</span>
            <span style={{ color: REL_COLOR[REL_BOTH] }}>● beides</span>
          </span>
          <span style={{ color: ROOT_COLOR }}
            title="Wurzel-Ursache: fehlt anderen Items, ihm selbst fehlt nichts">● Engpass</span>
          <span className="text-gray-600">
            {data.nodes.length} Knoten · {data.links.length} Kanten
          </span>
          <div className="ml-auto flex items-center gap-2">
            <SearchInput value={search} onChange={setSearch} placeholder="Item suchen…"
              className="w-44" onEnter={() => matchList[0] && setSelectedNode(matchList[0])} />
            {searching && (
              <span className="whitespace-nowrap" style={{ color: matchList.length ? MATCH_COLOR : "#6b7280" }}>
                {matchList.length} Treffer
              </span>
            )}
          </div>
        </div>
        {searching && matchList.length > 0 && (
          // Klick oeffnet nur das Detail-Panel rechts — die Kamera bleibt stehen.
          <div className="flex flex-wrap gap-1 text-xs">
            {matchList.slice(0, 12).map(name => (
              <button key={name} onClick={() => setSelectedNode(name)}
                className={`flex items-center rounded px-1.5 py-0.5 border transition-colors
                  ${name === selectedNode
                    ? "border-[#f97316] text-gray-100"
                    : "border-panelborder text-gray-300 hover:bg-white/10"}`}>
                <ItemIcon name={name} size={14} />{name}
              </button>
            ))}
            {matchList.length > 12 && (
              <span className="text-gray-600 self-center">… +{matchList.length - 12} weitere</span>
            )}
          </div>
        )}
        <div ref={wrapRef}
          className="flex-1 bg-panel border border-panelborder rounded-lg overflow-hidden"
          style={{ minWidth: 0 }}>
          {size.w > 0 && size.h > 0 && <ForceGraph2D
            ref={fgRef}
            width={size.w}
            height={size.h}
            graphData={data}
            backgroundColor="#0d1117"
            nodeRelSize={5}
            // warmupTicks laufen vor dem ersten Frame und ohne zu zeichnen: das
            // Layout ist damit schon weitgehend auseinandergezogen, wenn der Graph
            // zum ersten Mal sichtbar wird. Der Rest darf entsprechend kuerzer
            // nachlaufen (120 -> 60 sichtbare Ticks).
            warmupTicks={100}
            cooldownTicks={60}
            // 20px Rand: die Icons haengen ueber die reine Knotenposition hinaus.
            onEngineTick={() => {
              if(!fittedRef.current) fgRef.current?.zoomToFit(0, 20);
            }}
            onEngineStop={() => {
              if(fittedRef.current) return;
              fittedRef.current = true;
              fgRef.current?.zoomToFit(0, 20);
            }}
            onNodeClick={(n: any) => setSelectedNode(n ? n.id : null)}
            onBackgroundClick={() => setSelectedNode(null)}
            linkColor={(l: any) => {
              const src = typeof l.source === "object" ? l.source.id : l.source;
              const tgt = typeof l.target === "object" ? l.target.id : l.target;
              // Richtung mitfaerben: Kante zum Fokus hin = Zutat, vom Fokus weg = Produkt.
              if(focus && tgt === focus) return REL_LINK[REL_IN];
              if(focus && src === focus) return REL_LINK[REL_OUT];
              return "rgba(120,130,150,0.15)";
            }}
            linkWidth={(l: any) => {
              const src = typeof l.source === "object" ? l.source.id : l.source;
              const tgt = typeof l.target === "object" ? l.target.id : l.target;
              return focus && (src === focus || tgt === focus) ? 2 : 1;
            }}
            linkDirectionalArrowLength={3.5}
            linkDirectionalArrowRelPos={1}
            onNodeHover={(n: any) => setHover(n ? n.id : null)}
            nodeCanvasObject={(node: any, ctx, scale) => {
              const isSelected = node.id === selectedNode;
              const isMatch = matches.has(node.id);
              // Ohne Suche ist `matches` leer -> exakt das bisherige Hover-Verhalten.
              // Mit Suche bleiben Treffer auch dann hell, wenn woanders gehovert wird.
              const dimmed = focus != null
                ? (node.id !== focus && !neighbors.has(node.id) && !isSelected && !isMatch)
                : (searching && !isMatch);
              ctx.globalAlpha = dimmed ? 0.25 : 1;
              const r = node.isOre ? 7 : 5;
              // Beziehung zum fokussierten Knoten als gefuellter Halo hinter dem Icon.
              // Bewusst kein weiterer Ring: Treffer- und Auswahl-Ring liegen aussen,
              // ein dritter Ring waere davon kaum zu unterscheiden.
              const rel = neighbors.get(node.id);
              if(rel) {
                ctx.globalAlpha = 0.55;
                ctx.beginPath();
                ctx.arc(node.x, node.y, r + 3.5, 0, 2 * Math.PI);
                ctx.fillStyle = REL_COLOR[rel];
                ctx.fill();
                ctx.globalAlpha = dimmed ? 0.25 : 1;
              }
              const img = getIcon(node.id, () => forceRedraw(v => v + 1));
              if(img) {
                // Nur das linke Quadrat zeichnen: Factorio-Icons liefern die
                // Mipmap-Kette als Streifen (z.B. 120x64) in einer Datei mit.
                const src = img.naturalHeight || img.height;
                ctx.drawImage(img, 0, 0, src, src, node.x - r, node.y - r, r * 2, r * 2);
              } else {
                ctx.beginPath();
                ctx.arc(node.x, node.y, r, 0, 2 * Math.PI);
                ctx.fillStyle = node.isOre ? ORE_COLOR : node.isFluid ? FLUID_COLOR : ITEM_COLOR;
                ctx.fill();
              }
              if(node.isOre) {
                ctx.beginPath();
                ctx.arc(node.x, node.y, r + 2, 0, 2 * Math.PI);
                ctx.strokeStyle = ORE_COLOR;
                ctx.lineWidth = 1.5;
                ctx.stroke();
              } else if(node.isFluid) {
                ctx.beginPath();
                ctx.arc(node.x, node.y, r + 2, 0, 2 * Math.PI);
                ctx.strokeStyle = FLUID_COLOR;
                ctx.lineWidth = 1.2;
                ctx.stroke();
              }
              // Engpass-Badge statt eines weiteren Rings: r+2 (Erz/Fluid), r+5
              // (Treffer) und r+6.5 (Auswahl) sind schon belegt, ein vierter Ring
              // waere nicht mehr unterscheidbar. Rot = Wurzel-Ursache, gelb = wartet
              // seinerseits auf etwas. Nie gedimmt — sonst uebersieht man genau das.
              const isRootNode = rootSet.has(node.id);
              if(isRootNode || missingMap.has(node.id)) {
                ctx.globalAlpha = 1;
                ctx.beginPath();
                ctx.arc(node.x + r * 0.85, node.y - r * 0.85, 2.4, 0, 2 * Math.PI);
                ctx.fillStyle = isRootNode ? ROOT_COLOR : "#eab308";
                ctx.fill();
                ctx.strokeStyle = "#0d1117";
                ctx.lineWidth = 0.8;
                ctx.stroke();
                ctx.globalAlpha = dimmed ? 0.25 : 1;
              }
              // Suchtreffer bekommen einen eigenen Ring; der Auswahl-Ring liegt
              // darueber, damit Orange sichtbar bleibt.
              if(isMatch) {
                ctx.globalAlpha = 1;
                ctx.beginPath();
                ctx.arc(node.x, node.y, r + 5, 0, 2 * Math.PI);
                ctx.strokeStyle = MATCH_COLOR;
                ctx.lineWidth = 1.5;
                ctx.stroke();
              }
              // Auswahl bleibt sichtbar markiert, solange das Detail-Panel offen ist.
              if(isSelected) {
                ctx.globalAlpha = 1;
                ctx.beginPath();
                ctx.arc(node.x, node.y, r + 6.5, 0, 2 * Math.PI);
                ctx.strokeStyle = SELECT_COLOR;
                ctx.lineWidth = 2;
                ctx.stroke();
              }
              // Label erst beim Reinzoomen bzw. bei Hover/Auswahl.
              if(scale > 3 || node.id === focus || isSelected || isMatch || neighbors.has(node.id)) {
                ctx.globalAlpha = dimmed ? 0.4 : 1;
                ctx.font = `${Math.max(3, 10 / scale)}px sans-serif`;
                ctx.fillStyle = "#e5e7eb";
                ctx.textAlign = "center";
                ctx.textBaseline = "top";
                ctx.fillText(node.id, node.x, node.y + r + 1);
              }
              ctx.globalAlpha = 1;
            }}
          />}
        </div>
      </div>
      {selectedNode && meta && (
        <div className="bg-panel border border-panelborder rounded-lg p-3 w-80 shrink-0 overflow-y-auto">
          <ItemDetail
            itemName={selectedNode}
            meta={meta}
            production={production}
            assemblers={assemblers}
            missingMap={missingMap}
            isRoot={rootSet.has(selectedNode)}
            onClose={() => setSelectedNode(null)}
          />
        </div>
      )}
    </div>
  );
}
