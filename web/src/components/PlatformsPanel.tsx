import { Panel, Bar, statusColor } from "./Panel";

// Space-Age Plattformen (Plan §3.6).
export function PlatformsPanel({ data }: { data: any }) {
  const platforms = (data?.platforms ?? []) as any[];
  return (
    <Panel title="Plattformen">
      {platforms.length === 0 && <span className="text-gray-500 text-sm">keine Plattformen</span>}
      {platforms.map((p) => (
        <div key={p.name} className="flex flex-col gap-1 border-b border-panelborder/50 pb-2">
          <div className="flex justify-between text-sm">
            <span className="font-medium">{p.name}</span>
            <span className="text-gray-400">{p.location}</span>
          </div>
          {Object.entries(p.fuel ?? {}).map(([f, fs]: [string, any]) => (
            <div key={f} className="flex items-center gap-2 text-xs">
              <span className="w-32 text-gray-400">{f}</span>
              <div className="flex-1"><Bar pct={fs.pct} color={statusColor(fs.pct, 0.25, 0.1)} /></div>
              <span className="w-10 text-right">{(fs.pct * 100).toFixed(0)}%</span>
            </div>
          ))}
          {(p.warnings ?? []).map((w: string) => (
            <span key={w} className="text-xs text-warn">⚠ {w}</span>
          ))}
        </div>
      ))}
    </Panel>
  );
}
