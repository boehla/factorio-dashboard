import { useEffect, useState, useCallback, useMemo } from "react";
import type { SupplyChain, SupplyNode, TrainAvailability, RecipeOption } from "../lib/types";
import { ItemIcon } from "./ItemIcon";

const SOURCE_OPTIONS: { value: SupplyNode['source']; label: string; cls: string }[] = [
  { value: "local", label: "Bauen", cls: "bg-blue-800 text-blue-200 border-blue-600" },
  { value: "train", label: "Train", cls: "bg-amber-800 text-amber-200 border-amber-600" },
  { value: "ignore", label: "Ignor.", cls: "bg-gray-700 text-gray-300 border-gray-600" },
];

function parseStations(payload: any): TrainAvailability {
  const provides: TrainAvailability['provides'] = {};
  const requests: TrainAvailability['requests'] = {};
  const stations = payload?.stations ?? {};
  const decorative = new Set(["color", "font", "img", "gps", "tooltip", "quality", "planet", "space-location", "train", "train-stop", "space-platform"]);
  for (const [name, data] of Object.entries(stations) as [string, any][]) {
    let item: string | null = null;
    let type = "item";
    let providesRole = false, requestsRole = false;
    const re = /\[([^=\]]+)=([^\]]*)\]/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(name)) !== null) {
      const tagType = m[1];
      let tagName = m[2];
      const comma = tagName.indexOf(",");
      if (comma >= 0) tagName = tagName.slice(0, comma);
      // Namenskonvention: signal-input = Einspeisung ins Netz (Provider),
      // signal-output = Entnahme aus dem Netz (Request).
      if (tagType === "virtual-signal" && tagName === "signal-input") { providesRole = true; continue; }
      if (tagType === "virtual-signal" && tagName === "signal-output") { requestsRole = true; continue; }
      if (item === null && !decorative.has(tagType)) { item = tagName; type = tagType; }
    }
    if (!item || (!providesRole && !requestsRole)) continue;
    const info = { itemType: type, stationCount: data?.stops ?? 1 };
    if (providesRole) provides[item] = info;
    if (requestsRole) requests[item] = info;
  }
  return { provides, requests };
}

export function SupplyChainPlanner({ planet, stationsSnap }: { planet: string; stationsSnap: any }) {
  const [chains, setChains] = useState<SupplyChain[]>([]);
  const [trainAvail, setTrainAvail] = useState<TrainAvailability>({ provides: {}, requests: {} });
  const [expanded, setExpanded] = useState<Record<number, boolean>>(() => {
    try { return JSON.parse(localStorage.getItem("fdash-expanded-chains") ?? "{}"); } catch { return {}; }
  });
  const [graphMeta, setGraphMeta] = useState<any>(null);
  const [itemNames, setItemNames] = useState<string[]>([]);

  // Load graph meta for item list
  useEffect(() => {
    fetch("/api/graph/meta").then(r => r.json()).then(m => {
      setGraphMeta(m);
      const items: string[] = [];
      const seen = new Set<string>();
      for (const r of m.recipes ?? []) {
        for (const p of r.products as any[]) { if (!seen.has(p.name)) { seen.add(p.name); items.push(p.name); } }
        for (const i of r.ingredients as any[]) { if (!seen.has(i.name)) { seen.add(i.name); items.push(i.name); } }
      }
      items.sort();
      setItemNames(items);
    }).catch(() => {});
  }, []);

  // Live train availability from SignalR
  useEffect(() => {
    if (stationsSnap) setTrainAvail(parseStations(stationsSnap));
  }, [stationsSnap]);

  // Initial train availability + chains load
  useEffect(() => {
    fetch(`/api/supply-chain/train-availability?surface=${planet}`).then(r => r.json())
      .then(setTrainAvail).catch(() => {});
    loadChains();
  }, [planet]);

  const loadChains = useCallback(() => {
    fetch("/api/supply-chain").then(r => r.json()).then(setChains).catch(() => {});
  }, []);

  const saveExpanded = (id: number, v: boolean) => {
    setExpanded(prev => { const n = { ...prev, [id]: v }; localStorage.setItem("fdash-expanded-chains", JSON.stringify(n)); return n; });
  };

  const createChain = async () => {
    const r = await fetch("/api/supply-chain", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ targetItem: "iron-plate", recipeName: "", surface: planet, targetPerMin: null })
    });
    if (r.ok) { const c = await r.json(); setChains(prev => [c, ...prev]); saveExpanded(c.id, true); }
  };

  const deleteChain = async (id: number) => {
    await fetch(`/api/supply-chain/${id}`, { method: "DELETE" });
    setChains(prev => prev.filter(c => c.id !== id));
  };

  const updateChainConfig = async (id: number, data: { targetItem?: string; recipeName?: string; targetPerMin?: number | null }) => {
    const r = await fetch(`/api/supply-chain/${id}`, {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data)
    });
    if (r.ok) setChains(prev => prev.map(c => c.id === id ? { ...c, ...data } : c));
  };

  const saveNodes = async (id: number, nodes: SupplyNode[]) => {
    await fetch(`/api/supply-chain/${id}/nodes`, {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ nodes })
    });
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center gap-3">
        <h2 className="text-lg font-bold">Supply Chain Planner</h2>
        <button onClick={createChain}
          className="px-3 py-1 rounded text-sm bg-panelborder hover:bg-gray-600">
          + Neue Chain
        </button>
      </div>
      {chains.length === 0 ? (
        <div className="text-gray-500 text-sm p-4 bg-panel border border-panelborder rounded-lg text-center">
          Noch keine Chains. Klick auf "+ Neue Chain" um loszulegen.
        </div>
      ) : (
        chains.map(chain => (
          <ChainCard key={chain.id}
            chain={chain}
            expanded={expanded[chain.id] ?? true}
            onToggle={v => saveExpanded(chain.id, v)}
            onDelete={() => deleteChain(chain.id)}
            onUpdate={d => updateChainConfig(chain.id, d)}
            onSaveNodes={ns => saveNodes(chain.id, ns)}
            trainAvail={trainAvail}
            itemNames={itemNames}
            graphMeta={graphMeta}
            planet={planet}
          />
        ))
      )}
    </div>
  );
}

function ChainCard({ chain, expanded, onToggle, onDelete, onUpdate, onSaveNodes, trainAvail, itemNames, graphMeta, planet }:
  { chain: SupplyChain; expanded: boolean; onToggle: (v: boolean) => void; onDelete: () => void;
    onUpdate: (d: any) => void; onSaveNodes: (ns: SupplyNode[]) => void;
    trainAvail: TrainAvailability; itemNames: string[]; graphMeta: any; planet: string }) {
  const [localNodes, setLocalNodes] = useState<SupplyNode[]>(chain.nodes ?? []);
  const [targetRateStr, setTargetRateStr] = useState(chain.targetPerMin?.toString() ?? "");
  const [itemSearch, setItemSearch] = useState(chain.targetItem);
  const [showItemSearch, setShowItemSearch] = useState(false);
  const [recipes, setRecipes] = useState<RecipeOption[]>([]);
  const [showRecipes, setShowRecipes] = useState(false);

  useEffect(() => {
    // Load full chain with nodes from the API
    fetch(`/api/supply-chain/${chain.id}`).then(r => r.json()).then(c => {
      setLocalNodes(c.nodes ?? []);
    }).catch(() => {});
  }, [chain.id]);

  const loadRecipes = async (item: string) => {
    const r = await fetch(`/api/supply-chain/recipes/${item}?surface=${planet}`);
    if (r.ok) setRecipes(await r.json());
  };

  useEffect(() => {
    if (chain.targetItem) loadRecipes(chain.targetItem);
  }, [chain.targetItem]);

  const selectedRecipe = recipes.find(r => r.name === chain.recipeName) ?? null;

  const handleSave = () => {
    onSaveNodes(localNodes);
  };

  const handleTargetRate = () => {
    const v = targetRateStr ? parseFloat(targetRateStr) : null;
    onUpdate({ targetPerMin: isNaN(v as number) ? null : v });
  };

  const addIngredient = (node: SupplyNode) => {
    setLocalNodes(prev => [...prev, node]);
  };

  const updateNode = (idx: number, update: Partial<SupplyNode>) => {
    setLocalNodes(prev => prev.map((n, i) => i === idx ? { ...n, ...update } : n));
  };

  const updateNodeDeep = (path: number[], update: Partial<SupplyNode>) => {
    setLocalNodes(prev => {
      const next = [...prev];
      let arr = next;
      for (let i = 0; i < path.length - 1; i++) arr = arr[path[i]].children as SupplyNode[];
      arr[path[path.length - 1]] = { ...arr[path[path.length - 1]], ...update };
      return next;
    });
  };

  const removeNode = (idx: number) => {
    setLocalNodes(prev => prev.filter((_, i) => i !== idx));
  };

  const removeNodeDeep = (path: number[]) => {
    setLocalNodes(prev => {
      const next = [...prev];
      let arr = next;
      for (let i = 0; i < path.length - 1; i++) arr = arr[path[i]].children as SupplyNode[];
      (arr as any[]).splice(path[path.length - 1], 1);
      return next;
    });
  };

  const filteredItems = itemNames.filter(n => n.toLowerCase().includes(itemSearch.toLowerCase()));

  return (
    <div className="bg-panel border border-panelborder rounded-lg">
      {/* Header */}
      <div className="flex items-center gap-2 p-3 cursor-pointer" onClick={() => onToggle(!expanded)}>
        <span className="text-xs">{expanded ? "▼" : "▶"}</span>
        <ItemIcon name={chain.targetItem} size={18} />
        <span className="text-sm font-semibold">{chain.targetItem}</span>
        <span className="text-xs text-gray-500">{chain.recipeName}</span>
        {chain.targetPerMin != null && <span className="text-xs bg-blue-900/50 px-1.5 py-0.5 rounded">{chain.targetPerMin}/min</span>}
        <div className="flex-1" />
        <div className="flex items-center gap-2" onClick={e => e.stopPropagation()}>
          <input
            type="text" value={targetRateStr} placeholder="items/min"
            onChange={e => setTargetRateStr(e.target.value)}
            onBlur={handleTargetRate}
            onKeyDown={e => { if (e.key === "Enter") handleTargetRate(); }}
            className="w-20 text-xs bg-black/30 border border-panelborder rounded px-1.5 py-0.5 outline-none focus:border-gray-500"
          />
          <button onClick={handleSave}
            className="px-2 py-0.5 rounded text-xs bg-green-800 text-green-200 hover:bg-green-700">
            Save
          </button>
          <button onClick={onDelete}
            className="px-2 py-0.5 rounded text-xs bg-red-800 text-red-200 hover:bg-red-700">
            X
          </button>
        </div>
      </div>

      {expanded && (
        <div className="px-3 pb-3 border-t border-panelborder">
          {/* Item selector */}
          <div className="flex items-center gap-2 mt-2 mb-1">
            <span className="text-xs text-gray-400">Item:</span>
            <div className="relative">
              <input
                type="text" value={itemSearch} placeholder="Item suchen..."
                onChange={e => { setItemSearch(e.target.value); setShowItemSearch(true); }}
                onFocus={() => setShowItemSearch(true)}
                onBlur={() => setTimeout(() => setShowItemSearch(false), 150)}
                className="w-48 text-xs bg-black/30 border border-panelborder rounded px-2 py-0.5 outline-none focus:border-gray-500"
              />
              {showItemSearch && filteredItems.length > 0 && (
                <div className="absolute z-10 top-full left-0 mt-0.5 w-48 max-h-40 overflow-y-auto bg-gray-800 border border-panelborder rounded shadow-lg">
                  {filteredItems.slice(0, 30).map(n => (
                    <div key={n} className="px-2 py-0.5 text-xs hover:bg-gray-700 cursor-pointer"
                      onMouseDown={() => {
                        onUpdate({ targetItem: n });
                        setItemSearch(n);
                        setShowItemSearch(false);
                      }}>
                      {n}
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* Recipe selector */}
            <span className="text-xs text-gray-400 ml-2">Rezept:</span>
            <div className="relative">
              <button onClick={() => setShowRecipes(!showRecipes)}
                className="text-xs bg-black/30 border border-panelborder rounded px-2 py-0.5 hover:bg-gray-700 min-w-[100px] text-left">
                {selectedRecipe ? selectedRecipe.name : chain.recipeName}
              </button>
              {showRecipes && (
                <div className="absolute z-10 top-full left-0 mt-0.5 bg-gray-800 border border-panelborder rounded shadow-lg max-h-60 overflow-y-auto">
                  {recipes.map(r => (
                    <div key={r.name} className="px-2 py-1 text-xs hover:bg-gray-700 cursor-pointer whitespace-nowrap"
                      onMouseDown={() => {
                        onUpdate({ recipeName: r.name });
                        setShowRecipes(false);
                        // Also auto-add ingredients if we have recipe data
                        if (localNodes.length === 0 && r.ingredients.length > 0) {
                          const newNodes: SupplyNode[] = r.ingredients.map(ing => ({
                            itemName: ing.name,
                            itemType: ing.type as 'item' | 'fluid',
                            amountPerCraft: ing.amount,
                            source: 'local' as const,
                            childRecipe: null,
                            children: []
                          }));
                          setLocalNodes(newNodes);
                        }
                      }}>
                      <span className="font-medium">{r.name}</span>
                      <span className="text-gray-500 ml-2">
                        {(r.energy ?? 0).toFixed(1)}s
                        {r.machineCount > 0 ? ` · ${r.machineCount} Masch.` : ""}
                      </span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Ingredient tree */}
          <div className="mt-2">
            {localNodes.length === 0 && (
              <div className="text-xs text-gray-600 italic p-2">
                Keine Zutaten definiert. Wähle ein Rezept oder füge manuell Items hinzu.
              </div>
            )}
            {localNodes.map((node, i) => (
              <IngredientNode key={i}
                node={node}
                path={[i]}
                trainAvail={trainAvail}
                itemNames={itemNames}
                graphMeta={graphMeta}
                planet={planet}
                onUpdate={u => updateNodeDeep([i], u)}
                onRemove={() => removeNode(i)}
                onDeepUpdate={(p, u) => updateNodeDeep([i, ...p], u)}
                onDeepRemove={p => removeNodeDeep([i, ...p])}
              />
            ))}
          </div>

          {/* Manual add button */}
          <div className="mt-2">
            <div className="relative inline-block">
              <button onClick={() => { /* toggle manual add */ }}
                className="text-xs text-gray-500 hover:text-gray-300 px-2 py-0.5 border border-dashed border-gray-600 rounded">
                + Zutat hinzufügen
              </button>
              <ManualAddDrop
                onSelect={item => {
                  addIngredient({
                    itemName: item,
                    itemType: "item",
                    amountPerCraft: 1,
                    source: "local",
                    childRecipe: null,
                    children: []
                  });
                }}
                itemNames={itemNames}
              />
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function ManualAddDrop({ onSelect, itemNames }: { onSelect: (item: string) => void; itemNames: string[] }) {
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState("");
  const filtered = itemNames.filter(n => n.toLowerCase().includes(q.toLowerCase())).slice(0, 20);

  return (
    <>
      {open && (
        <div className="absolute z-10 top-full left-0 mt-0.5 bg-gray-800 border border-panelborder rounded shadow-lg p-1"
          onMouseLeave={() => setOpen(false)}>
          <input type="text" value={q} placeholder="suchen..." autoFocus
            onChange={e => setQ(e.target.value)}
            className="w-full text-xs bg-black/30 border border-panelborder rounded px-1.5 py-0.5 mb-1 outline-none" />
          <div className="max-h-32 overflow-y-auto">
            {filtered.map(n => (
              <div key={n} className="px-1.5 py-0.5 text-xs hover:bg-gray-700 cursor-pointer rounded"
                onMouseDown={() => { onSelect(n); setOpen(false); }}>
                {n}
              </div>
            ))}
          </div>
        </div>
      )}
      <button onClick={() => setOpen(!open)}
        className="text-xs text-gray-500 hover:text-gray-300 px-2 py-0.5 border border-dashed border-gray-600 rounded">
        + Zutat
      </button>
    </>
  );
}

function IngredientNode({ node, path, trainAvail, itemNames, graphMeta, planet, onUpdate, onRemove, onDeepUpdate, onDeepRemove }:
  { node: SupplyNode; path: number[]; trainAvail: TrainAvailability; itemNames: string[];
    graphMeta: any; planet: string;
    onUpdate: (u: Partial<SupplyNode>) => void;
    onRemove: () => void;
    onDeepUpdate: (path: number[], u: Partial<SupplyNode>) => void;
    onDeepRemove: (path: number[]) => void;
  }) {
  const [showRecipePicker, setShowRecipePicker] = useState(false);
  const [recipes, setRecipes] = useState<RecipeOption[]>([]);
  const [expanded, setExpanded] = useState(true);
  const [ingredients, setIngredients] = useState<SupplyNode[]>(node.children ?? []);

  const trainProvides = !!trainAvail.provides[node.itemName];
  const trainRequests = !trainProvides && !!trainAvail.requests[node.itemName];

  const isTrainAvail = node.source === "train" && trainProvides;
  const isTrainRequest = node.source === "train" && !trainProvides && trainRequests;
  const isTrainMissing = node.source === "train" && !trainProvides && !trainRequests;
  const isLocalSet = node.source === "local" && node.childRecipe !== null;
  const isLocalUnset = node.source === "local" && node.childRecipe === null;

  // Auto-switch to train if item is available in train network and node is still unconfigured
  useEffect(() => {
    if (node.source === "local" && node.childRecipe === null && trainProvides) {
      onUpdate({ source: "train", childRecipe: null, children: [] });
    }
  }, [node.itemName, trainProvides]);

  const statusColor = node.source === "train"
    ? (isTrainAvail ? "text-green-400" : isTrainRequest ? "text-yellow-400" : "text-red-400")
    : node.source === "ignore"
      ? "text-gray-500"
      : (isLocalSet ? "text-green-400" : "text-yellow-400");

  const statusDot = node.source === "train"
    ? (isTrainAvail ? "🟢" : isTrainRequest ? "🟡" : "🔴")
    : node.source === "ignore"
      ? "⚪"
      : (isLocalSet ? "🟢" : "🟡");

  const loadRecipes = async (item: string) => {
    const r = await fetch(`/api/supply-chain/recipes/${item}?surface=${planet}`);
    if (r.ok) setRecipes(await r.json());
  };

  const handleSourceChange = (source: SupplyNode['source']) => {
    onUpdate({
      source,
      childRecipe: source === "ignore" ? null : node.childRecipe,
      children: source === "ignore" ? [] : node.children
    });
  };

  const handleRecipeSelect = (recipe: RecipeOption) => {
    const newChildren = recipe.ingredients.map(ing => ({
      itemName: ing.name,
      itemType: ing.type as 'item' | 'fluid',
      amountPerCraft: ing.amount,
      source: 'local' as const,
      childRecipe: null,
      children: []
    }));
    onUpdate({
      childRecipe: recipe.name,
      children: newChildren
    });
    setIngredients(newChildren);
    setShowRecipePicker(false);
  };

  const hasKids = (node.source === "local" || node.source === "train") && node.children.length > 0;

  return (
    <div className="ml-4 mt-1 border-l-2 border-panelborder pl-3">
      <div className="flex items-center gap-1.5 py-0.5">
        {/* Expand/collapse if has children */}
        {hasKids && (
          <button onClick={() => setExpanded(!expanded)} className="text-xs w-4 text-gray-500">
            {expanded ? "▼" : "▶"}
          </button>
        )}
        {!hasKids && <span className="w-4 inline-block" />}

        {/* Status dot */}
        <span className="text-xs" title={
          node.source === "train"
            ? (isTrainAvail ? "Im Train-Netzwerk verfügbar" : isTrainRequest ? "Nur als Request im Netzwerk" : "Nicht im Train-Netzwerk")
            : node.source === "ignore"
              ? "Ignoriert"
              : (isLocalSet ? "Lokales Rezept konfiguriert" : "Kein Rezept gewählt")
        }>{statusDot}</span>

        {/* Item name */}
        <ItemIcon name={node.itemName} size={16} />
        <span className={`text-xs font-medium ${statusColor}`}>{node.itemName}</span>
        {/* Train network indicator (always visible if in network, regardless of source) */}
        {node.source !== "train" && trainProvides && (
          <span className="text-xs text-amber-500" title="Im Train-Netzwerk verfügbar">🚂</span>
        )}
        {node.source !== "train" && trainRequests && (
          <span className="text-xs text-yellow-600/60" title="Nur als Request im Train-Netzwerk">🚂</span>
        )}
        <span className="text-xs text-gray-600">{node.itemType === "fluid" ? "(Fluid)" : ""}</span>
        <span className="text-xs text-gray-600">x{node.amountPerCraft.toFixed(1)}</span>

        {/* Source toggle */}
        <div className="flex gap-0.5 ml-1">
          {SOURCE_OPTIONS.map(opt => (
            <button key={opt.value}
              onClick={() => handleSourceChange(opt.value)}
              className={`text-xs px-1.5 py-0 rounded border ${node.source === opt.value ? opt.cls : "border-transparent text-gray-500 hover:text-gray-300"}`}>
              {opt.label}
            </button>
          ))}
        </div>

        {/* Recipe picker for local and train */}
        {(node.source === "local" || node.source === "train") && (
          <div className="relative">
            <button onClick={() => { setShowRecipePicker(!showRecipePicker); loadRecipes(node.itemName); }}
              className={`text-xs px-1.5 py-0 rounded border ${node.childRecipe
                ? "bg-green-900/50 border-green-700 text-green-300"
                : "bg-yellow-900/50 border-yellow-700 text-yellow-300"}`}>
              {node.childRecipe ? `Rezept: ${node.childRecipe}` : "Rezept wählen..."}
            </button>
            {showRecipePicker && (
              <div className="absolute z-20 top-full left-0 mt-0.5 bg-gray-800 border border-panelborder rounded shadow-lg max-h-48 overflow-y-auto min-w-[200px]"
                onMouseLeave={() => setShowRecipePicker(false)}>
                {recipes.map(r => (
                  <div key={r.name} className="px-2 py-1 text-xs hover:bg-gray-700 cursor-pointer"
                    onMouseDown={() => handleRecipeSelect(r)}>
                    <span className="font-medium">{r.name}</span>
                    <span className="text-gray-500 ml-2">{(r.energy ?? 0).toFixed(1)}s</span>
                  </div>
                ))}
                {recipes.length === 0 && <div className="px-2 py-1 text-xs text-gray-500">Keine Rezepte gefunden</div>}
              </div>
            )}
          </div>
        )}

        {/* Train availability detail */}
        {node.source === "train" && isTrainAvail && (
          <span className="text-xs text-green-600">
            {trainAvail.provides[node.itemName].stationCount} Stations
          </span>
        )}
      </div>

      {/* Render children if local/train and has recipe */}
      {((node.source === "local" || node.source === "train") && node.children.length > 0 && expanded) && (
        (node.children as SupplyNode[]).map((child, i) => (
          <IngredientNode key={i}
            node={child}
            path={[...path, i]}
            trainAvail={trainAvail}
            itemNames={itemNames}
            graphMeta={graphMeta}
            planet={planet}
            onUpdate={u => onDeepUpdate([i], u)}
            onRemove={() => onDeepRemove([i])}
            onDeepUpdate={(p, u) => onDeepUpdate([i, ...p], u)}
            onDeepRemove={p => onDeepRemove([i, ...p])}
          />
        ))
      )}
    </div>
  );
}
