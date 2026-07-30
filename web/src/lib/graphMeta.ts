import { useEffect, useMemo, useState } from "react";

// Statische Rezept-Struktur von /api/graph/meta (pro Save konstant).
export interface RecipeIo { name: string; type: string; amount: number }
export interface GraphMeta {
  recipes: {
    name: string; energy: number;
    ingredients: RecipeIo[]; products: RecipeIo[];
  }[];
  oreItems: string[];
  fluidNames: string[];
}

// Modul-Cache: die Meta-Daten aendern sich pro Save nicht, daher nur einmal laden.
let metaCache: GraphMeta | null = null;
let metaPromise: Promise<GraphMeta> | null = null;
export function loadMeta(): Promise<GraphMeta> {
  if(metaCache) return Promise.resolve(metaCache);
  if(!metaPromise) {
    metaPromise = fetch("/api/graph/meta").then(r => r.json()).then((m: GraphMeta) => {
      metaCache = m;
      return m;
    });
  }
  return metaPromise;
}

// Meta einmalig laden; dank Modul-Cache kostenlos fuer jeden weiteren Aufrufer.
export function useGraphMeta(): GraphMeta | null {
  const [meta, setMeta] = useState<GraphMeta | null>(metaCache);
  useEffect(() => { loadMeta().then(setMeta).catch(() => {}); }, []);
  return meta;
}

// Rezeptname -> Anzahl Maschinen, die genau dieses Rezept eingestellt haben.
// assemblers gruppiert nach Haupt-Produkt, das Rezept kann also unter einem
// anderen Item-Key haengen als dem gesuchten -> ueber alle Gruppen summieren.
export function useRecipeMachineCounts(assemblers: any): Map<string, number> {
  return useMemo(() => {
    const m = new Map<string, number>();
    for(const grp of Object.values(assemblers?.by_item ?? {}) as any[]) {
      for(const [recipe, cnt] of Object.entries(grp?.recipes ?? {}) as [string, number][]) {
        m.set(recipe, (m.get(recipe) ?? 0) + cnt);
      }
    }
    return m;
  }, [assemblers]);
}

// Items, die dauerhaft fuer andere Items gebraucht werden: Zutat eines Rezepts,
// das auf dieser Surface in mindestens einer Maschine eingestellt ist. Die reine
// Prototyp-Liste taugt dafuer nicht — dort ist z.B. transport-belt Zutat von
// Underground-Belt, obwohl das Rezept nirgends gebaut wird (Mall/on the fly).
export function useActiveIngredients(meta: GraphMeta | null, assemblers: any): Set<string> {
  const machineCounts = useRecipeMachineCounts(assemblers);
  return useMemo(() => {
    const s = new Set<string>();
    if(!meta) return s;
    for(const r of meta.recipes) {
      if((machineCounts.get(r.name) ?? 0) <= 0) continue;
      for(const ing of r.ingredients) s.add(ing.name);
    }
    return s;
  }, [meta, machineCounts]);
}
