import { useMemo } from "react";
import type { AssemblerGroup } from "./types";

// Engpass-Auswertung, portiert aus DspHttpStats (Webroot/index.html: missingMap,
// renderMissingTree, computeRootProblems). Das Backend liefert pro produziertem
// Item nur die direkt fehlenden Zutaten; die Kette entsteht hier, weil jedes
// Zwischenprodukt selbst eine Gruppe mit eigener missing-Map ist. Ein
// Backend-Graph-Walk waere doppelte Arbeit.

// Item -> { fehlende Zutat: Fehlmenge }
export type MissingMap = Map<string, Record<string, number>>;

export interface ShortageNode {
  item: string;
  amount: number;
  isRoot: boolean;      // fehlt selbst nichts -> hier bricht die Kette ab
  children: ShortageNode[];
}

export interface RootCause {
  item: string;
  amount: number;                     // aufsummierte Fehlmenge ueber alle Abnehmer
  blockedItems: string[];             // Items, die direkt oder transitiv daran haengen
  machines: number;                   // hungernde Maschinen in diesen Gruppen
  ownStatus: Record<string, number>;  // Status der Maschinen, die die Wurzel fertigen
  produced: boolean;                  // wird die Wurzel auf dieser Surface ueberhaupt gefertigt?
}

function groups(assemblers: any): [string, AssemblerGroup][] {
  return Object.entries((assemblers?.by_item ?? {}) as Record<string, AssemblerGroup>);
}

export function buildMissingMap(assemblers: any): MissingMap {
  const m: MissingMap = new Map();
  for(const [name, g] of groups(assemblers)) {
    if(g?.missing && Object.keys(g.missing).length > 0) m.set(name, g.missing);
  }
  return m;
}

export function useMissingMap(assemblers: any): MissingMap {
  return useMemo(() => buildMissingMap(assemblers), [assemblers]);
}

// Fehlt dem Item selbst etwas? Wenn nein, ist es das Ende der Kette — die Ursache.
export function isRootCause(item: string, map: MissingMap): boolean {
  const own = map.get(item);
  if(!own) return true;
  return !Object.values(own).some(v => v > 0);
}

// Kette unter `item` aufbauen. `path` bricht Kreise (Recycling und Nebenprodukte
// erzeugen echte Zyklen), `depth` deckelt die Baumgroesse — beides wie in DSP.
export function buildShortageTree(
  item: string, map: MissingMap, depth = 12, path: string[] = []
): ShortageNode[] {
  if(depth <= 0 || path.includes(item)) return [];
  const own = map.get(item);
  if(!own) return [];
  const next = [...path, item];
  return Object.entries(own)
    .filter(([, amount]) => amount > 0)
    .sort(([, a], [, b]) => b - a)
    .map(([child, amount]) => ({
      item: child,
      amount,
      isRoot: isRootCause(child, map),
      children: buildShortageTree(child, map, depth - 1, next)
    }));
}

// Alle Wurzeln, an denen `item` transitiv haengt — flach, fuer die Zuordnung
// Wurzel -> Abnehmer. Anders als buildShortageTree wird hier ein `seen`-Set statt
// eines Pfads gefuehrt: das bricht nicht nur Kreise, sondern besucht auch jeden
// Knoten nur einmal. Ohne das waere eine rautenfoermige Rezeptkette exponentiell,
// und diese Funktion laeuft fuer jede Gruppe bei jedem Snapshot.
function collectRoots(
  item: string, map: MissingMap, out: Map<string, number>, seen: Set<string>, depth: number
): void {
  if(depth <= 0 || seen.has(item)) return;
  seen.add(item);
  const own = map.get(item);
  if(!own) return;
  for(const [child, amount] of Object.entries(own)) {
    if(amount <= 0) continue;
    if(isRootCause(child, map)) out.set(child, (out.get(child) ?? 0) + amount);
    else collectRoots(child, map, out, seen, depth - 1);
  }
}

// Wurzel-Ursachen nach Wirkung sortiert: erst die, die die meisten Maschinen
// lahmlegen. Pendant zu DSPs computeRootProblems, nur zusaetzlich gewichtet.
export function computeRootCauses(assemblers: any): RootCause[] {
  const map = buildMissingMap(assemblers);
  const all = new Map(groups(assemblers));
  const acc = new Map<string, RootCause>();

  for(const [item, g] of all) {
    const roots = new Map<string, number>();
    collectRoots(item, map, roots, new Set(), 12);
    for(const [root, amount] of roots) {
      let rc = acc.get(root);
      if(!rc) {
        const own = all.get(root);
        rc = {
          item: root, amount: 0, blockedItems: [], machines: 0,
          ownStatus: own?.status ?? {}, produced: own != null
        };
        acc.set(root, rc);
      }
      rc.amount += amount;
      rc.blockedItems.push(item);
      rc.machines += g.starving ?? 0;
    }
  }

  return [...acc.values()].sort((a, b) =>
    b.machines - a.machines || b.blockedItems.length - a.blockedItems.length || b.amount - a.amount);
}

export function useRootCauses(assemblers: any): RootCause[] {
  return useMemo(() => computeRootCauses(assemblers), [assemblers]);
}

// Menge der Wurzel-Items — fuer schnelle Treffer in Filter und Graph.
export function useRootCauseSet(assemblers: any): Set<string> {
  const roots = useRootCauses(assemblers);
  return useMemo(() => new Set(roots.map(r => r.item)), [roots]);
}

// Kompakte Mengenangabe: Fehlmengen sind Summen ueber viele Maschinen und
// werden schnell vierstellig.
export function fmtAmount(n: number): string {
  if(n >= 1e6) return (n / 1e6).toFixed(1) + "M";
  if(n >= 1e3) return (n / 1e3).toFixed(1) + "k";
  if(n >= 10) return n.toFixed(0);
  return n.toFixed(n % 1 === 0 ? 0 : 1);
}
