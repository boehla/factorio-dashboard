import { useState } from "react";
import { Panel } from "./Panel";
import { ItemIcon } from "./ItemIcon";

// Die Rangliste aus dem abgeleiteten Job `problems` (ProblemAnalyzer im Server).
// Bewusst dieselbe Quelle wie die MCP-Tools: eine zweite Bewertung im Frontend
// waere dieselbe Rechnung mit anderem Ergebnis, sobald eine Seite sich aendert.
//
// Das Panel beantwortet die Frage, fuer die man sonst vier andere Panels
// nebeneinanderlegen musste: was ist gerade das Schlimmste?

interface Problem {
  domain: string;
  severity: number;
  title: string;
  detail: string;
  suggestion?: string;
  surface?: string;
  items?: string[];
}

const DOMAIN_DE: Record<string, string> = {
  shortage: "Engpass",
  machines: "Maschinen",
  power: "Strom",
  resources: "Erze",
  trains: "Züge",
  logistics: "Roboter",
  platforms: "Plattformen",
  research: "Forschung"
};

// Drei Stufen reichen: darunter faengt man an, Farben zu unterscheiden, die
// nebeneinander gar nicht auftreten.
function severityColor(s: number): string {
  if(s >= 0.7) return "#ef4444";
  if(s >= 0.4) return "#eab308";
  return "#60a5fa";
}

export function ProblemsPanel({ data }: { data: any }) {
  const [domain, setDomain] = useState<string | null>(null);
  const all = (data?.problems ?? []) as Problem[];
  const counts = (data?.counts ?? {}) as Record<string, number>;
  const shown = (domain ? all.filter(p => p.domain === domain) : all).slice(0, 12);

  return (
    <Panel title="Was als Nächstes?" right={
      <div className="flex items-center gap-1">
        {Object.entries(counts).sort(([, a], [, b]) => b - a).map(([d, n]) => (
          <button key={d} onClick={() => setDomain(domain === d ? null : d)}
            className={`text-xs px-1.5 py-0.5 rounded ${domain === d ? "bg-panelborder" : "bg-black/30"}`}
            title={`${n} in ${DOMAIN_DE[d] ?? d}`}>
            {DOMAIN_DE[d] ?? d} {n}
          </button>
        ))}
      </div>
    }>
      {all.length === 0 && <span className="text-ok text-sm">keine Probleme erkannt</span>}

      {shown.map((p, i) => (
        <div key={`${p.domain}-${p.title}-${i}`}
          className="flex items-start gap-2 text-sm rounded px-1 -mx-1 py-0.5 hover:bg-white/10 transition-colors">
          {/* Schmaler Farbstreifen statt Icon: die Schwere soll auffallen, ohne
              dass jede Zeile ein eigenes Symbol braucht. */}
          <span className="w-1 self-stretch rounded shrink-0"
            style={{ background: severityColor(p.severity) }}
            title={`Schwere ${(p.severity * 100).toFixed(0)}%`} />
          <div className="min-w-0 flex-1">
            <div className="flex items-baseline gap-2">
              <span className="font-medium truncate" style={{ color: severityColor(p.severity) }}>
                {p.title}
              </span>
              {p.surface && <span className="text-xs text-gray-600 shrink-0">{p.surface}</span>}
            </div>
            <div className="text-xs text-gray-400">{p.detail}</div>
            {p.suggestion && <div className="text-xs text-gray-500 italic">→ {p.suggestion}</div>}
          </div>
          <div className="flex items-center gap-0.5 shrink-0"
            title={(p.items ?? []).join(", ")}>
            {(p.items ?? []).slice(0, 5).map(it => <ItemIcon key={it} name={it} size={14} />)}
          </div>
        </div>
      ))}

      {(domain ? all.filter(p => p.domain === domain) : all).length > shown.length && (
        <span className="text-xs text-gray-600">… +{(domain ? all.filter(p => p.domain === domain) : all).length - shown.length} weitere</span>
      )}
    </Panel>
  );
}
