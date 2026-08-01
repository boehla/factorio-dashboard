import { useEffect, useState } from "react";
import { useSnapshots } from "./lib/useSnapshots";
import {
  PowerPanel, AlertsPanel, RobotsPanel, ResearchPanel,
  MachinesPanel, ResourcesPanel, ProductionPanel
} from "./components/Panels";
import { ShortagesPanel } from "./components/ShortagesPanel";
import { ProblemsPanel } from "./components/ProblemsPanel";
import { PlatformsPanel } from "./components/PlatformsPanel";
import { FluidsPanel } from "./components/FluidsPanel";
import { TrainsPanel } from "./components/TrainsPanel";
import { StoragePanel } from "./components/StoragePanel";
import { ProducingItemsGraph } from "./components/ProducingItemsGraph";

const PLANETS = ["nauvis", "vulcanus", "fulgora", "gleba", "aquilo"];
// Plattformen haengen an keiner Oberflaeche, teilen sich aber die Auswahlleiste
// mit den Planeten — deshalb hier als Pseudo-Planet gefuehrt.
const PLATFORMS = "platforms";

export default function App() {
  const { snaps, connected } = useSnapshots();
  const [planet, setPlanet] = useState("nauvis");
  const [view, setView] = useState<"dashboard" | "graph">("dashboard");
  const [audit, setAudit] = useState<any[]>([]);
  const [autoResearch, setAutoResearch] = useState(false);

  useEffect(() => {
    const load = () => fetch("/api/research/audit").then(r => r.json()).then(setAudit).catch(() => {});
    load();
    const id = setInterval(load, 10000);
    return () => clearInterval(id);
  }, []);

  // Die blanken Job-Keys (snaps.production) werden im Collector pro Surface
  // ueberschrieben und zeigen den zuletzt gepollten Planeten — nicht den hier
  // gewaehlten. Deshalb ueberall die surface-qualifizierten Keys; sie werden im
  // selben Durchlauf publiziert, kommen also nie spaeter. Kein Fallback: fehlt
  // der Planet im Save, ist "keine Daten" richtiger als fremde Daten.
  // production und assemblers muessen zudem vom selben Planeten stammen —
  // ProductionPanel filtert mit den aktiv eingestellten Rezepten des Snapshots.
  const production = snaps[`production@${planet}`];
  const assemblers = snaps[`assemblers@${planet}`];

  const toggleResearch = (v: boolean) => {
    fetch(`/api/research/toggle?enabled=${v}`, { method: "POST" })
      .then(r => r.json()).then(d => setAutoResearch(d.enabled)).catch(() => {});
  };

  // Der Graph soll den Viewport fuellen: dort schmaleres Padding, kein Scroll
  // und die Hoehe kommt aus flex-1 statt aus einem geschaetzten calc().
  const isGraph = view === "graph";

  return (
    <div className={`flex flex-col ${isGraph
      ? "h-screen overflow-hidden p-2 gap-2" : "min-h-screen p-3 gap-3"}`}>
      <header className="flex items-center justify-between">
        <h1 className="text-lg font-bold">Factorio Dashboard</h1>
        <div className="flex items-center gap-2">
          <div className="flex gap-1">
            {(["dashboard", "graph"] as const).map(v => (
              <button key={v} onClick={() => setView(v)}
                className={`px-3 py-1 rounded text-sm ${view === v ? "bg-panelborder" : "bg-panel"}`}>
                {v === "dashboard" ? "Dashboard" : "Item-Graph"}
              </button>
            ))}
          </div>
          <span className={`text-xs px-2 py-0.5 rounded ${connected ? "bg-ok/20 text-ok" : "bg-crit/20 text-crit"}`}>
            {connected ? "live" : "getrennt"}
          </span>
        </div>
      </header>

      <nav className="flex gap-1">
        {PLANETS.map(p => (
          <button key={p} onClick={() => setPlanet(p)}
            className={`px-3 py-1 rounded text-sm capitalize ${planet === p ? "bg-panelborder" : "bg-panel"}`}>
            {p}
          </button>
        ))}
        {view === "dashboard" && (
          <button onClick={() => setPlanet(PLATFORMS)}
            className={`px-3 py-1 rounded text-sm ${planet === PLATFORMS ? "bg-panelborder" : "bg-panel"}`}>
            Platforms
          </button>
        )}
      </nav>

      {isGraph ? (
        <ProducingItemsGraph planet={planet} production={production} assemblers={assemblers} />
      ) : planet === PLATFORMS ? (
        <PlatformsPanel data={snaps.platforms} />
      ) : (
        <>
          <div className="grid grid-cols-1 md:grid-cols-4 gap-3">
            <PowerPanel data={snaps.power} />
            <ResearchPanel audit={audit} enabled={autoResearch} onToggle={toggleResearch}
              state={snaps[`research_state@${planet}`] ?? snaps.research_state} production={production} />
            <AlertsPanel trains={snaps.trains_derived ?? snaps.trains} stall={snaps.stall}
              power={snaps.power} alerts={snaps.alerts} />
            <RobotsPanel data={snaps.logistics} />
          </div>

          {/* Serverseitige Rangliste ueber alle Domaenen — steht bewusst ueber
              den Engpaessen: die sind nur eine ihrer Quellen. */}
          <ProblemsPanel data={snaps.problems} />

          <ShortagesPanel assemblers={assemblers} />

          <MachinesPanel data={assemblers} />

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <ResourcesPanel data={snaps[`resources@${planet}`]} />
            <ProductionPanel data={production} assemblers={assemblers} />
          </div>

          <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
            <FluidsPanel data={snaps[`fluids@${planet}`]} production={production} />
            <TrainsPanel data={snaps[`stations@${planet}`]}
              trains={snaps.trains_derived ?? snaps.trains} />
            <StoragePanel logistics={snaps[`logistics@${planet}`]}
              containers={snaps[`containers@${planet}`]} />
          </div>
        </>
      )}

      {!isGraph && (
        <footer className="text-xs text-gray-600 text-center mt-2">
          Datenerfassung über den Mod fdash-exporter
          {planet === PLATFORMS ? " · Plattformen" : ` · Planet: ${planet}`}
        </footer>
      )}
    </div>
  );
}
