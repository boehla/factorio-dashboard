import { Panel, Bar, statusColor } from "./Panel";
import { ItemIcon } from "./ItemIcon";

// Fluidtanks je Fluid. Auf Pyanodons ist die Gasbilanz die halbe Diagnose:
// ein voller Tank heisst "der Abnehmer fehlt", ein leerer "der Erzeuger fehlt".
// Beides sieht man in der Produktionsstatistik nicht.

interface FluidRow {
  name: string;
  amount: number;
  capacity: number;
  tanks: number;
  fill: number;
  temperature?: number;
  produced: number;
  consumed: number;
}

function rows(fluids: any, production: any): FluidRow[] {
  const flow = new Map<string, { p: number; c: number }>();
  for(const it of (production?.items ?? []) as any[]) {
    if(it.type === "fluid") flow.set(it.item, { p: it.produced_per_min ?? 0, c: it.consumed_per_min ?? 0 });
  }
  const out: FluidRow[] = [];
  for(const [name, f] of Object.entries((fluids?.fluids ?? {}) as Record<string, any>)) {
    const fl = flow.get(name);
    out.push({
      name, amount: f.amount ?? 0, capacity: f.capacity ?? 0, tanks: f.tanks ?? 0,
      fill: f.fill ?? 0, temperature: f.temperature,
      produced: fl?.p ?? 0, consumed: fl?.c ?? 0
    });
  }
  // Gemessen wird an der Menge, nicht am Namen: die grossen Puffer sind die,
  // an denen etwas haengt.
  return out.sort((a, b) => b.amount - a.amount);
}

function fmt(n: number): string {
  if(n >= 1e6) return (n / 1e6).toFixed(1) + "M";
  if(n >= 1e3) return (n / 1e3).toFixed(1) + "k";
  return n.toFixed(0);
}

export function FluidsPanel({ data, production }: { data: any; production: any }) {
  const list = rows(data, production);
  const shown = list.slice(0, 12);

  return (
    <Panel title="Fluide" right={
      <span className="text-xs text-gray-500">
        {data ? `${data.tanks_total ?? 0} Tanks, ${data.tanks_empty ?? 0} leer` : "—"}
      </span>
    }>
      {!data && <span className="text-gray-500 text-sm">
        kein Tank-Scan (Mod-Einstellung fdash-fluid-scan)
      </span>}

      {shown.map(f => (
        <div key={f.name} className="grid grid-cols-[minmax(7rem,1fr)_5rem_1fr_auto] gap-2 items-center text-sm">
          <span className="truncate"><ItemIcon name={f.name} size={16} />{f.name}</span>
          <span className="text-gray-400 tabular-nums text-right">{fmt(f.amount)}</span>
          {/* Voll ist hier das Warnsignal, nicht das Ziel — daher invertiert. */}
          <Bar pct={f.fill} color={statusColor(f.fill, 0.85, 0.95, true)} />
          <span className="text-xs text-gray-500 w-28 text-right tabular-nums"
            title={`${f.tanks} Tanks · ${f.produced.toFixed(0)}/min rein, ${f.consumed.toFixed(0)}/min raus`
              + (f.temperature != null ? ` · ${f.temperature.toFixed(0)}°` : "")}>
            {f.produced.toFixed(0)}/{f.consumed.toFixed(0)}
          </span>
        </div>
      ))}

      {list.length > shown.length && (
        <span className="text-xs text-gray-600">… +{list.length - shown.length} weitere</span>
      )}
      {data && list.length === 0 && <span className="text-gray-500 text-sm">keine gefüllten Tanks</span>}
    </Panel>
  );
}
