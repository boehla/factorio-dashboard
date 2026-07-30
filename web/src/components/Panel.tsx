import React from "react";

export function Panel({ title, right, children }:
  { title: string; right?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="bg-panel border border-panelborder rounded-lg p-3 flex flex-col gap-2">
      <div className="flex items-center justify-between">
        <h2 className="text-xs uppercase tracking-wider text-gray-400">{title}</h2>
        {right}
      </div>
      {children}
    </div>
  );
}

export function Bar({ pct, color }: { pct: number; color: string }) {
  return (
    <div className="w-full h-2 bg-black/40 rounded overflow-hidden">
      <div className="h-full rounded" style={{ width: `${Math.min(100, pct * 100)}%`, background: color }} />
    </div>
  );
}

export interface Seg { value: number; color: string; label: string }

// Gestapelter Farbbalken (wie DSP): mehrere Segmente nebeneinander, Breite
// proportional zum Anteil an `total`. `title` liefert einen nativen Tooltip.
// `detail` haengt nach einer Leerzeile Zusatzzeilen an — bei den Maschinen die
// rohen Factorio-Status, damit man hinter dem Sammel-Segment die echte Ursache
// sieht (no_power vs. no_ingredients vs. …).
export function StackedBar({ segs, total, detail }:
  { segs: Seg[]; total: number; detail?: string[] }) {
  const lines = segs.map(s => `${s.label}: ${s.value}`);
  if(detail && detail.length > 0) lines.push("", ...detail);
  return (
    <div className="flex w-full h-2.5 rounded overflow-hidden bg-black/40"
      title={lines.join("\n")}>
      {total > 0 && segs.map((s, i) =>
        s.value > 0 ? (
          <div key={i} style={{ width: `${(s.value / total) * 100}%`, background: s.color }} />
        ) : null
      )}
    </div>
  );
}

// Ampel-Farbe: gruen ok, gelb Warnung, rot kritisch (Plan §7)
export function statusColor(value: number, warn: number, crit: number, invert = false): string {
  const bad = invert ? value > warn : value < warn;
  const critical = invert ? value > crit : value < crit;
  if(critical) return "#ef4444";
  if(bad) return "#eab308";
  return "#22c55e";
}
