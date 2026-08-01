import { Panel } from "./Panel";
import { ItemIcon } from "./ItemIcon";
import { fmtAmount } from "../lib/shortages";

// Bestände im Logistiknetz und in Kisten. Zusammen mit dem Maschinenstatus
// ergibt das die Suchrichtung: volle Puffer heissen "Abnehmer fehlt", leere
// "Erzeuger fehlt".
//
// Kistendaten sind per Default aus (fdash-container-scan) — ein Inventar-Read
// je Kiste ist der teuerste Collector des Mods.

interface Row { item: string; net: number; chest: number; }

function rows(logistics: any, containers: any): Row[] {
  const net = new Map<string, number>();
  for(const n of (logistics?.networks ?? []) as any[]) {
    for(const [item, count] of Object.entries((n.contents ?? {}) as Record<string, number>)) {
      net.set(item, (net.get(item) ?? 0) + count);
    }
  }
  const chest = new Map<string, number>(
    Object.entries((containers?.items ?? {}) as Record<string, number>));

  const names = new Set([...net.keys(), ...chest.keys()]);
  return [...names]
    .map(item => ({ item, net: net.get(item) ?? 0, chest: chest.get(item) ?? 0 }))
    .sort((a, b) => (b.net + b.chest) - (a.net + a.chest));
}

export function StoragePanel({ logistics, containers }: { logistics: any; containers: any }) {
  const list = rows(logistics, containers);
  const shown = list.slice(0, 14);

  return (
    <Panel title="Bestände" right={
      <span className="text-xs text-gray-500">
        {containers ? `${containers.containers ?? 0} Kisten` : "Logistiknetz"}
      </span>
    }>
      {list.length === 0 && (
        <span className="text-gray-500 text-sm">
          keine Bestandsdaten — Kisten-Scan ist aus (fdash-container-scan)
        </span>
      )}

      {shown.map(r => (
        <div key={r.item} className="grid grid-cols-[1fr_auto_auto] gap-3 items-center text-sm">
          <span className="truncate"><ItemIcon name={r.item} size={16} />{r.item}</span>
          <span className="text-gray-400 tabular-nums w-16 text-right" title="im Logistiknetz">
            {r.net > 0 ? fmtAmount(r.net) : "—"}
          </span>
          <span className="text-gray-500 tabular-nums w-16 text-right" title="in Kisten">
            {r.chest > 0 ? fmtAmount(r.chest) : "—"}
          </span>
        </div>
      ))}

      {list.length > shown.length && (
        <span className="text-xs text-gray-600">… +{list.length - shown.length} weitere</span>
      )}
    </Panel>
  );
}
