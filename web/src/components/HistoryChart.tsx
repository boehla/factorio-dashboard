import { useEffect, useRef } from "react";
import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";

// Verlaufs-Graph (Plan §5, §7). Laedt Zeitreihen per REST und rendert mit uPlot.
export function HistoryChart({ saveId, metric, labels, label }:
  { saveId: string; metric: string; labels?: string; label: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const plotRef = useRef<uPlot | null>(null);

  useEffect(() => {
    if(!ref.current) return;
    const to = Math.floor(Date.now() / 1000);
    const from = to - 6 * 3600;
    const url = `/api/series?saveId=${saveId}&metric=${metric}` +
      (labels ? `&labels=${encodeURIComponent(labels)}` : "") +
      `&from=${from}&to=${to}&resolution=raw`;
    fetch(url).then(r => r.json()).then((rows: { ts: number; value: number }[]) => {
      const xs = rows.map(r => r.ts);
      const ys = rows.map(r => r.value);
      const opts: uPlot.Options = {
        width: ref.current!.clientWidth, height: 200, title: label,
        scales: { x: { time: true } },
        series: [{}, { label, stroke: "#22c55e", width: 2 }]
      };
      if(plotRef.current) plotRef.current.destroy();
      plotRef.current = new uPlot(opts, [xs, ys], ref.current!);
    }).catch(() => {});
    return () => { plotRef.current?.destroy(); };
  }, [saveId, metric, labels, label]);

  return <div ref={ref} className="bg-panel border border-panelborder rounded-lg p-2" />;
}
