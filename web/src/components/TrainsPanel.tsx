import { useState } from "react";
import { Panel } from "./Panel";
import { RichText } from "./RichText";
import { duration } from "../lib/format";

// Bahnhöfe. Die Züge selbst stehen im Alerts-Panel; hier steht die andere
// Hälfte der Frage: welche Station wird nie angefahren (tote Route) und welche
// hat Dauerwarteschlange (Ladeengpass).

interface Station {
  name: string;
  stops: number;
  trains: number;
  limit?: number;
}

function stations(data: any): Station[] {
  return Object.entries((data?.stations ?? {}) as Record<string, any>)
    .map(([name, s]) => ({ name, stops: s.stops ?? 0, trains: s.trains ?? 0, limit: s.limit }))
    .sort((a, b) => b.trains - a.trains || a.name.localeCompare(b.name));
}

export function TrainsPanel({ data, trains }: { data: any; trains: any }) {
  const [onlyProblems, setOnlyProblems] = useState(true);
  const all = stations(data);

  // Wo wirklich jemand wartet: Ziele der Züge im Zustand destination_full.
  // "Am Zuglimit" allein ist auf einer eingestellten Basis der Regelfall und
  // trifft fast jede Station — als Filter wertlos.
  const waiting = new Map<string, number>();
  for(const p of (trains?.problems ?? []) as any[]) {
    if(p.state !== "destination_full" || !p.schedule_station) continue;
    waiting.set(p.schedule_station, (waiting.get(p.schedule_station) ?? 0) + 1);
  }

  const odd = all.filter(s => waiting.has(s.name));
  const list = onlyProblems ? odd : all;
  const shown = list.slice(0, 14);

  return (
    <Panel title="Bahnhöfe" right={
      <div className="flex items-center gap-2">
        <span className="text-xs text-gray-500">
          {trains ? `${trains.total ?? 0} Züge, ${trains.problem ?? 0} mit Problem` : ""}
        </span>
        <button onClick={() => setOnlyProblems(!onlyProblems)}
          className={`text-xs px-2 py-0.5 rounded ${onlyProblems ? "bg-panelborder" : "bg-black/30"}`}>
          {onlyProblems ? `mit Warteschlange (${odd.length})` : `alle (${all.length})`}
        </button>
      </div>
    }>
      {!data && <span className="text-gray-500 text-sm">noch keine Bahnhofsdaten</span>}

      {shown.map(s => {
        const atLimit = s.limit != null && s.limit > 0 && s.trains >= s.limit;
        return (
          <div key={s.name} className="grid grid-cols-[1fr_auto_auto] gap-3 items-center text-sm">
            <span className="truncate"><RichText text={s.name} size={14} /></span>
            <span className="text-xs text-gray-500 w-14 text-right">
              {s.stops > 1 ? `${s.stops}×` : ""}
            </span>
            <span className={`tabular-nums w-16 text-right ${
              s.trains === 0 ? "text-gray-500" : atLimit ? "text-warn" : "text-gray-300"}`}
              title={s.trains === 0 ? "keine Züge zugewiesen"
                : atLimit ? "am Zuglimit — hier staut es" : "Züge / Limit"}>
              {s.trains}{s.limit != null ? `/${s.limit}` : ""}
            </span>
          </div>
        );
      })}

      {list.length > shown.length && (
        <span className="text-xs text-gray-600">… +{list.length - shown.length} weitere</span>
      )}
      {data && list.length === 0 && (
        <span className="text-ok text-sm">alle Bahnhöfe werden bedient</span>
      )}

      {(trains?.problems ?? []).length > 0 && (
        <div className="text-xs text-gray-500 border-t border-panelborder/50 pt-1 mt-1">
          längster Stillstand: {duration(trains.problems[0].stuck_seconds ?? 0)}
          {trains.problems[0].state ? ` (${trains.problems[0].state})` : ""}
        </div>
      )}
    </Panel>
  );
}
