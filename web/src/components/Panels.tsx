import React, { useState } from "react";
import { Panel, Bar, StackedBar, statusColor } from "./Panel";
import { ItemIcon } from "./ItemIcon";
import { RichText } from "./RichText";
import { SearchInput } from "./SearchInput";
import { siWatts, duration, itemMatches } from "../lib/format";
import { useGraphMeta, useActiveIngredients } from "../lib/graphMeta";
import { ShortageChips, ROOT_COLOR } from "./ShortageTree";
import { fmtAmount, useMissingMap, useRootCauseSet } from "../lib/shortages";

// Waehlt das groesste Stromnetz: das mit der meisten verfuegbaren Leistung
// (hoechste production). Bei mehreren unabhaengigen Netzen zeigt das Dashboard
// nur dieses eine an; die Gesamtzahl wird klein daneben eingeblendet.
function largestNetwork(networks: any[]): any | undefined {
  if(!networks || networks.length === 0) return undefined;
  return networks.reduce((a, b) => (b.production > a.production ? b : a));
}

// Ein Netz gilt als "ok", wenn seine Versorgung ueber der Warnschwelle liegt.
const POWER_OK = 0.95;

// Akku-Zeile: Ladung in % + Rest-/Ladezeit. net = discharge - charge (W):
// >0 entlaedt (reicht noch), <0 laedt (voll in). Schwelle gegen Rauschen.
function AccuLine({ acc }: { acc: any }) {
  const pct = acc.capacity > 0 ? acc.energy / acc.capacity : 0;
  const net = (acc.discharge_rate ?? 0) - (acc.charge_rate ?? 0);
  const color = statusColor(pct, 0.3, 0.15);
  const EPS = 1000; // 1 kW
  let status: React.ReactNode;
  if(pct >= 0.999 && net <= EPS) status = <span className="text-gray-400">voll</span>;
  else if(net > EPS) status = <>entlädt · reicht noch {duration(acc.energy / net)}</>;
  else if(net < -EPS) status = <>lädt · voll in {duration((acc.capacity - acc.energy) / -net)}</>;
  else status = <span className="text-gray-400">stabil</span>;
  return (
    <div className="text-xs flex items-center gap-2"
      title={`${acc.count} Akku(s) · ${(acc.energy / 1e6).toFixed(0)} / ${(acc.capacity / 1e6).toFixed(0)} MJ`}>
      <span>🔋</span>
      <div className="flex-1"><Bar pct={pct} color={color} /></div>
      <span className="whitespace-nowrap">{(pct * 100).toFixed(0)}% · {status}</span>
    </div>
  );
}

export function PowerPanel({ data }: { data: any }) {
  const networks = (data?.networks ?? []) as any[];
  const net = largestNetwork(networks);
  if(!net) return <Panel title="Power"><span className="text-gray-500">—</span></Panel>;
  const okCount = networks.filter(n => n.satisfaction >= POWER_OK).length;
  const allOk = okCount === networks.length;
  // Auslastung = aktuelle Last / max. Generator-Nennleistung. Das ist die echte
  // "Luft nach oben"; prod == cons ist normal (Generatoren drosseln auf Bedarf).
  const cap = net.capacity ?? 0;
  const hasCap = cap > 0;
  const util = hasCap ? Math.min(1, net.consumption / cap) : 0;
  const utilColor = statusColor(util, 0.7, 0.9, true);
  const satColor = statusColor(net.satisfaction, 0.95, 0.85);
  return (
    <Panel title="Power" right={
      <span className="text-xs" style={{ color: allOk ? "#22c55e" : "#eab308" }}>
        {okCount} von {networks.length} {networks.length === 1 ? "Netz" : "Netze"} ok
      </span>
    }>
      <div className="text-lg font-semibold">{siWatts(net.consumption)} / {siWatts(net.production)}</div>
      {hasCap ? (
        <>
          <Bar pct={util} color={utilColor} />
          <div className="text-xs text-gray-400">
            {(util * 100).toFixed(0)}% Auslastung · Reserve {siWatts(Math.max(0, cap - net.consumption))} von {siWatts(cap)} · größtes Netz
          </div>
        </>
      ) : (
        <>
          <Bar pct={net.satisfaction} color={satColor} />
          <div className="text-xs text-gray-400">{(net.satisfaction * 100).toFixed(0)}% satisfaction · größtes Netz</div>
        </>
      )}
      {net.satisfaction < POWER_OK && (
        <div className="text-xs" style={{ color: satColor }}>
          ⚠ nur {(net.satisfaction * 100).toFixed(0)}% versorgt (Unterdeckung)
        </div>
      )}
      {net.accumulators && net.accumulators.count > 0 && <AccuLine acc={net.accumulators} />}
    </Panel>
  );
}

// Lesbare Kurzbezeichnungen der Factorio-Zugzustaende.
const TRAIN_STATE_DE: Record<string, string> = {
  no_path: "kein Weg", path_lost: "Weg verloren", no_schedule: "kein Fahrplan",
  destination_full: "Ziel voll", manual_control: "manuell",
  manual_control_stop: "manuell (Stop)"
};

// destination_full ist "nur" eine Warnung, alle anderen Problemzustaende sind
// kritisch. Warnungen ans Ende sortieren.
const trainLevel = (state: string) => (state === "destination_full" ? "warn" : "crit");

export function AlertsPanel({ trains, stall, power }: { trains: any; stall: any; power: any }) {
  // Problemzuege nach Zustand gruppieren (alle "Ziel voll" zusammen usw.),
  // damit man auf einen Blick sieht welcher Fehler wie viele Zuege betrifft.
  const problems = (trains?.problems ?? []) as any[];
  const groups = new Map<string, any[]>();
  for(const p of problems) {
    const arr = groups.get(p.state) ?? [];
    arr.push(p);
    groups.set(p.state, arr);
  }
  const states = [...groups.keys()].sort((a, b) =>
    (trainLevel(a) === "crit" ? 0 : 1) - (trainLevel(b) === "crit" ? 0 : 1));

  // Sonstige Alerts (Stall, Strom) unterhalb der Zuege.
  const extra: { text: React.ReactNode; level: string }[] = [];
  (stall?.stalled ?? []).forEach((s: any) =>
    extra.push({ text: `${s.item} stalled (${s.reason}, ${s.since_seconds}s)`, level: "crit" }));
  const net = largestNetwork(power?.networks ?? []);
  if(net && net.satisfaction < 0.9) extra.push({ text: `Strom ${(net.satisfaction * 100).toFixed(0)}%`, level: "warn" });

  const nothing = states.length === 0 && extra.length === 0;
  return (
    <Panel title="Alerts">
      {nothing && <span className="text-ok text-sm">Alles ok</span>}

      {states.map(state => {
        const list = groups.get(state)!;
        const color = trainLevel(state) === "crit" ? "#ef4444" : "#eab308";
        return (
          <div key={state} className="text-sm mb-1">
            <div style={{ color }}>
              ⚠ {TRAIN_STATE_DE[state] ?? state} · {list.length} {list.length === 1 ? "Zug" : "Züge"}
            </div>
            <div className="ml-4 flex flex-col gap-0.5">
              {list.slice(0, 5).map((p: any) => {
                const cargo = (p.cargo ?? []) as any[];
                return (
                  <div key={p.id} className="text-xs text-gray-300 truncate">
                    <span className="opacity-50">#{p.id}</span>
                    {/* Dauer kommt aus dem abgeleiteten Job trains_derived; der
                        rohe trains-Job kennt sie nicht. "haengt seit 14 min" ist
                        etwas anderes als "haengt gerade". */}
                    {p.stuck_seconds > 0 && (
                      <span className="opacity-60" title="Dauer im Problemzustand">
                        {" "}{duration(p.stuck_seconds)}
                      </span>
                    )}
                    {p.schedule_station
                      ? <> → <RichText text={p.schedule_station} size={14} /></>
                      : null}
                    {cargo.length > 0 && (
                      <span className="opacity-70">
                        {" · "}
                        {cargo.map((c: any, i: number) => (
                          <span key={i} className="whitespace-nowrap">
                            <ItemIcon name={c.name} size={12} />{c.count}{" "}
                          </span>
                        ))}
                      </span>
                    )}
                  </div>
                );
              })}
              {list.length > 5 && (
                <div className="text-xs text-gray-500">… +{list.length - 5} weitere</div>
              )}
            </div>
          </div>
        );
      })}

      {extra.map((it, i) => (
        <div key={i} className="text-sm" style={{ color: it.level === "crit" ? "#ef4444" : "#eab308" }}>
          ⚠ {it.text}
        </div>
      ))}
    </Panel>
  );
}

// Waehlt das groesste Roboternetz: das mit den meisten Robotern insgesamt
// (Logistik + Konstruktion). Analog zum Power-Panel wird nur dieses eine
// angezeigt; bei mehreren Netzen (haeufig unter pY/pYanodons) verhindert das,
// dass ein leeres erstes Netz "0 von 0" anzeigt.
function largestBotNetwork(networks: any[]): any | undefined {
  if(!networks || networks.length === 0) return undefined;
  const total = (n: any) =>
    (n.logistic_robots?.total ?? 0) + (n.construction_robots?.total ?? 0);
  return networks.reduce((a, b) => (total(b) > total(a) ? b : a));
}

// Eine Roboter-Zeile: aktiv/gesamt plus gestapelter Balken laeuft/idle/laedt.
function BotRow({ label, r }: { label: string; r: any }) {
  if(!r) return null;
  const working = r.working ?? 0;
  const idle = r.idle ?? 0;
  const charging = (r.charging ?? 0) + (r.waiting_for_charge ?? 0);
  const total = r.total ?? 0;
  const segs = [
    { value: working, color: "#22c55e", label: "aktiv" },
    { value: idle, color: "#3b82f6", label: "idle" },
    { value: charging, color: "#eab308", label: "lädt" }
  ];
  return (
    <div className="grid grid-cols-[6rem_1fr] gap-2 items-center text-sm">
      <span className="text-gray-300">{label}</span>
      <div className="flex items-center gap-2">
        <span className="w-14 text-right text-gray-400">{working}/{total}</span>
        <div className="flex-1"><StackedBar segs={segs} total={total} /></div>
      </div>
    </div>
  );
}

export function RobotsPanel({ data }: { data: any }) {
  const networks = (data?.networks ?? []) as any[];
  const net = largestBotNetwork(networks);
  if(!net) return <Panel title="Robots"><span className="text-gray-500">—</span></Panel>;
  const lr = net.logistic_robots;
  const cr = net.construction_robots;
  const waiting = (lr?.waiting_for_charge ?? 0) + (cr?.waiting_for_charge ?? 0);
  const waitColor = statusColor(waiting, 20, 50, true);
  return (
    <Panel title="Robots" right={
      <span className="text-xs text-gray-500">{networks.length} {networks.length === 1 ? "Netz" : "Netze"} · größtes</span>
    }>
      <BotRow label="Logistik" r={lr} />
      <BotRow label="Konstruktion" r={cr} />
      {waiting > 0 && (
        <div className="text-xs" style={{ color: waitColor }}>{waiting} warten auf Ladung</div>
      )}
    </Panel>
  );
}

export function ResearchPanel({ audit, enabled, onToggle }:
  { audit: any[]; enabled: boolean; onToggle: (v: boolean) => void }) {
  const last = audit?.[audit.length - 1];
  return (
    <Panel title="Research" right={
      <button onClick={() => onToggle(!enabled)}
        className={`text-xs px-2 py-0.5 rounded ${enabled ? "bg-ok/20 text-ok" : "bg-gray-700 text-gray-300"}`}>
        auto: {enabled ? "ON" : "OFF"}
      </button>
    }>
      {last ? (
        <>
          <div className="text-sm font-medium">{last.tech}</div>
          <div className="text-xs text-gray-400">~{duration(last.estimatedSeconds)} · {last.result}</div>
        </>
      ) : <span className="text-gray-500 text-sm">keine Auswahl</span>}
    </Panel>
  );
}

// Zerlegt den status-Zaehler einer Item-Gruppe in vier Balken-Segmente:
// gruen = laeuft, blau = Ausgang voll / per Schaltung aus, gelb = Zutat fehlt,
// rot = Rest (Strom o.ae.).
function machineSegments(r: any) {
  const status = r.status ?? {};
  const working = status.working ?? 0;
  const full = (status.full_output ?? 0) + (status.output_full ?? 0)
    + (status.waiting_for_space_in_destination ?? 0) + (status.full_burned_result_output ?? 0)
    + (status.disabled_by_control_behavior ?? 0);
  const missing = (status.no_ingredients ?? 0) + (status.item_ingredient_shortage ?? 0)
    + (status.no_input_fluid ?? 0) + (status.low_input_fluid ?? 0)
    + (status.fluid_ingredient_shortage ?? 0) + (status.waiting_for_source_items ?? 0);
  const rest = Math.max(0, (r.total ?? 0) - working - full - missing);
  return { working, full, missing, rest };
}

// Die vier Segmente fassen sehr unterschiedliche Ursachen zusammen ("Problem: 3"
// kann Strom, Treibstoff oder ein leeres Erzfeld sein). Fuer den Tooltip daher
// die unveraenderten Factorio-Status, absteigend nach Anzahl.
function rawStatusLines(r: any): string[] {
  const lines = (Object.entries(r.status ?? {}) as [string, number][])
    .filter(([, cnt]) => cnt > 0)
    .sort(([, a], [, b]) => b - a)
    .map(([name, cnt]) => `${name}: ${cnt}`);
  const missing = (Object.entries(r.missing ?? {}) as [string, number][])
    .filter(([, v]) => v > 0)
    .sort(([, a], [, b]) => b - a);
  if(missing.length > 0) {
    lines.push("", "fehlt:", ...missing.map(([name, v]) => `  ${name}: ${fmtAmount(v)}`));
  }
  return lines;
}

// Die vier Status-Filter des Maschinen-Panels, gleiche Reihenfolge und Farben
// wie die Segmente im Balken.
const MACHINE_FILTERS = [
  { key: "working", label: "laeuft", color: "#22c55e" },
  { key: "full", label: "Ausgang voll", color: "#3b82f6" },
  { key: "missing", label: "Zutat fehlt", color: "#eab308" },
  { key: "rest", label: "Problem", color: "#ef4444" }
] as const;

type MachineFilter = typeof MACHINE_FILTERS[number]["key"];

export function MachinesPanel({ data }: { data: any }) {
  const [q, setQ] = useState("");
  const [active, setActive] = useState<MachineFilter[]>([]);
  const [sortPct, setSortPct] = useState(false);
  // Wurzel-Filter (DSPs "Root issues"): orthogonal zu den Status-Chips, deshalb
  // ein eigener Schalter — er filtert nach Position in der Engpass-Kette, nicht
  // nach Maschinen-Status.
  const [rootOnly, setRootOnly] = useState(false);
  const missingMap = useMissingMap(data);
  const rootSet = useRootCauseSet(data);
  const toggle = (key: MachineFilter) =>
    setActive(a => a.includes(key) ? a.filter(k => k !== key) : [...a, key]);

  // Ohne Auswahl gilt der bisherige Blickwinkel: Probleme (gelb+rot) zaehlen.
  const keys: MachineFilter[] = active.length > 0 ? active : ["missing", "rest"];
  // Sortier-/Filterwert eines Items: Summe der ausgewaehlten Segmente, je nach
  // Modus absolut (Maschinen) oder als Anteil an allen Maschinen des Items.
  const score = (r: any) => {
    const s = machineSegments(r) as Record<MachineFilter, number>;
    const sum = keys.reduce((n, k) => n + s[k], 0);
    return sortPct ? (r.total > 0 ? sum / r.total : 0) : sum;
  };

  const all = Object.entries(data?.by_item ?? {}) as [string, any][];
  const items = all.filter(([name, r]) =>
    itemMatches(name, q) && (active.length === 0 || score(r) > 0)
    && (!rootOnly || rootSet.has(name)));
  items.sort((a, b) => score(b[1]) - score(a[1]));
  // Ohne Suche/Filter nur die zwoelf auffaelligsten; sonst alle Treffer, dann
  // wird die Liste bei Bedarf scrollbar statt das Dashboard zu sprengen.
  const searching = q.trim() !== "";
  const limited = !searching && active.length === 0 && !rootOnly;
  const shown = limited ? items.slice(0, 12) : items;
  return (
    <Panel title="Maschinen (nach Item)" right={
      <div className="flex items-center gap-2">
        <div className="flex items-center gap-1">
          {MACHINE_FILTERS.map(f => {
            const on = active.includes(f.key);
            return (
              <button key={f.key} onClick={() => toggle(f.key)}
                title={`nur Items mit „${f.label}“`}
                className="text-xs px-1.5 py-0.5 rounded border transition-colors"
                style={{
                  borderColor: on ? f.color : "transparent",
                  background: on ? `${f.color}33` : "rgba(0,0,0,0.3)",
                  color: on ? f.color : "#9ca3af"
                }}>
                {f.label}
              </button>
            );
          })}
          <button onClick={() => setRootOnly(v => !v)}
            title="nur Wurzel-Ursachen: Items, die fehlen, ohne dass ihnen selbst etwas fehlt"
            className="text-xs px-1.5 py-0.5 rounded border transition-colors"
            style={{
              borderColor: rootOnly ? ROOT_COLOR : "transparent",
              background: rootOnly ? `${ROOT_COLOR}33` : "rgba(0,0,0,0.3)",
              color: rootOnly ? ROOT_COLOR : "#9ca3af"
            }}>
            nur Ursachen
          </button>
        </div>
        <div className="flex items-center rounded overflow-hidden border border-panelborder">
          {[{ pct: false, label: "#", title: "sortieren nach Anzahl Maschinen" },
            { pct: true, label: "%", title: "sortieren nach Anteil an allen Maschinen des Items" }
          ].map(m => (
            <button key={m.label} onClick={() => setSortPct(m.pct)} title={m.title}
              className={`text-xs px-2 py-0.5 ${sortPct === m.pct
                ? "bg-panelborder text-gray-100" : "text-gray-400 hover:bg-white/10"}`}>
              {m.label}
            </button>
          ))}
        </div>
        {!limited && (
          <span className="text-xs text-gray-500 whitespace-nowrap">{items.length} von {all.length}</span>
        )}
        <SearchInput value={q} onChange={setQ} placeholder="Item suchen…" className="w-40" />
      </div>
    }>
      <div className={`flex flex-col ${limited ? "" : "overflow-y-auto"}`}
        style={limited ? undefined : { maxHeight: "50vh" }}>
        {shown.map(([name, r]) => {
          const { working, full, missing, rest } = machineSegments(r);
          const segs = [
            { value: working, color: "#22c55e", label: "laeuft" },
            { value: full, color: "#3b82f6", label: "Ausgang voll" },
            { value: missing, color: "#eab308", label: "Zutat fehlt" },
            { value: rest, color: "#ef4444", label: "Problem" }
          ];
          return (
            <div key={name}
              className="grid grid-cols-[1fr_minmax(0,14rem)_auto] gap-2 items-center text-sm rounded px-1 -mx-1 py-0.5 hover:bg-white/10 transition-colors">
              <div className="truncate">
                <ItemIcon name={name} />
                <span style={rootSet.has(name) ? { color: ROOT_COLOR } : undefined}>{name}</span>
              </div>
              {/* Was dieser Gruppe gerade fehlt; Hover klappt die ganze Kette auf. */}
              <div className="overflow-hidden">
                <ShortageChips missing={r.missing} map={missingMap} size={14} max={5} />
              </div>
              <div className="flex items-center gap-2 w-64">
                <span className="text-gray-400 w-10 text-right">{r.total}</span>
                <div className="flex-1">
                  <StackedBar segs={segs} total={r.total ?? 0} detail={rawStatusLines(r)} />
                </div>
                <span className="text-xs w-24 truncate flex justify-end gap-2">
                  <span style={{ color: "#22c55e" }}>{working}</span>
                  {full > 0 && <span style={{ color: "#3b82f6" }}>{full}</span>}
                  {missing > 0 && <span style={{ color: "#eab308" }}>{missing}</span>}
                  {rest > 0 && <span style={{ color: "#ef4444" }}>{rest}</span>}
                </span>
              </div>
            </div>
          );
        })}
        {items.length === 0 && (
          <span className="text-gray-500 text-sm">
            {searching ? `keine Treffer für „${q.trim()}“`
              : rootOnly ? "keine Wurzel-Ursachen"
              : active.length > 0 ? "keine Treffer" : "—"}
          </span>
        )}
      </div>
    </Panel>
  );
}

export function ResourcesPanel({ data }: { data: any }) {
  const res = Object.entries(data?.resources ?? {}) as [string, any][];
  return (
    <Panel title="Erze" right={
      <span className="text-xs text-gray-500">Scan {data?.scan_step ?? 0}/{data?.scan_total ?? 0}</span>
    }>
      {res.slice(0, 10).map(([name, r]) => (
        <div key={name} className="grid grid-cols-[1fr_auto_auto] gap-3 text-sm">
          <span className="truncate"><ItemIcon name={name} />{name}</span>
          <span className="text-gray-400">{Math.round(r.rate_current)}/{Math.round(r.rate_max)}</span>
          <span className="text-gray-500 w-16 text-right">{duration(r.depletion_seconds ?? 0)}</span>
        </div>
      ))}
      {res.length === 0 && <span className="text-gray-500 text-sm">Scan läuft…</span>}
    </Panel>
  );
}

export function ProductionPanel({ data, assemblers }: { data: any; assemblers: any }) {
  // On-the-fly-Bauitems (transport-belt, Kisten, Assembler …) ausblenden: gezeigt
  // wird nur, was Zutat eines Rezepts ist, das auf dieser Surface auch wirklich in
  // einer Maschine eingestellt ist. Die Prototyp-Liste allein reicht nicht — dort
  // ist fast jedes Mall-Item irgendwo Zutat (Belt -> Underground-Belt usw.).
  const meta = useGraphMeta();
  const active = useActiveIngredients(meta, assemblers);
  // Solange Meta/Assembler-Snapshot fehlen (erste Sekunden): grober Fallback auf
  // das is_ingredient-Flag aus production.lua, damit das Panel nicht leer bleibt.
  const isNeeded = active.size > 0
    ? (it: any) => active.has(it.item)
    : (it: any) => it.is_ingredient !== false;
  // Zusaetzlich muss gerade wirklich verbraucht werden: input_counts/output_counts
  // sind All-Time-Werte, laengst stillgelegte Items stehen also weiter in der Liste
  // und hatten mit ratio = 0.00 die echten Engpaesse aus den Top 10 verdraengt.
  const items = ((data?.items ?? []) as any[])
    .filter(it => (it.consumed_per_min ?? 0) > 0 && isNeeded(it));
  items.sort((a, b) => a.ratio - b.ratio);
  return (
    <Panel title="Produktions-Ratio">
      {items.slice(0, 10).map((it) => {
        const arrow = it.ratio < 0.95 ? "↓" : it.ratio > 1.05 ? "↑" : "→";
        return (
          <div key={it.item} className="grid grid-cols-[1fr_auto_auto] gap-3 text-sm">
            <span className="truncate"><ItemIcon name={it.item} />{it.item}</span>
            <span style={{ color: statusColor(it.ratio, 0.95, 0.8) }}>{it.ratio.toFixed(2)}</span>
            <span className="text-gray-500 w-5">{arrow}</span>
          </div>
        );
      })}
      {items.length === 0 && <span className="text-gray-500 text-sm">—</span>}
    </Panel>
  );
}
