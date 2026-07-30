import { Panel } from "./Panel";
import { ItemIcon } from "./ItemIcon";
import { ROOT_COLOR } from "./ShortageTree";
import { fmtAmount, useRootCauses, type RootCause } from "../lib/shortages";

// Warum die Wurzel fehlt: der dominante Status ihrer eigenen Maschinen. Steht das
// Item in keiner Gruppe, wird es hier gar nicht gefertigt (Erz, Import, Handcraft).
function reason(rc: RootCause): { text: string; color: string } {
  if(!rc.produced) return { text: "wird hier nicht gefertigt", color: "#9ca3af" };
  const top = (Object.entries(rc.ownStatus) as [string, number][])
    .filter(([name, cnt]) => cnt > 0 && name !== "working")
    .sort(([, a], [, b]) => b - a)[0];
  if(!top) return { text: "Maschinen laufen — zu wenig Durchsatz", color: "#eab308" };
  return { text: `${top[0]} · ${top[1]}`, color: "#ef4444" };
}

// Die Wurzel-Ursachen: Items, die fehlen, ohne dass ihnen selbst etwas fehlt.
// Genau die muss man reparieren — alles darueber loest sich von allein.
export function ShortagesPanel({ assemblers }: { assemblers: any }) {
  const roots = useRootCauses(assemblers);
  const shown = roots.slice(0, 10);
  return (
    <Panel title="Engpässe (Ursachen)" right={
      <span className="text-xs text-gray-500">
        {roots.length === 0 ? "—" : `${roots.length} ${roots.length === 1 ? "Ursache" : "Ursachen"}`}
      </span>
    }>
      {shown.map(rc => {
        const r = reason(rc);
        return (
          <div key={rc.item}
            className="grid grid-cols-[minmax(9rem,1fr)_auto_auto_minmax(6rem,14rem)] gap-3
              items-center text-sm rounded px-1 -mx-1 py-0.5 hover:bg-white/10 transition-colors">
            <span className="truncate">
              <ItemIcon name={rc.item} size={18} fallback={rc.item} />
              <span style={{ color: ROOT_COLOR }}>{rc.item}</span>
            </span>
            <span className="text-gray-400 tabular-nums w-16 text-right"
              title="aufsummierte Fehlmenge über alle wartenden Maschinen">
              {fmtAmount(rc.amount)}
            </span>
            <span className="text-xs text-gray-400 w-32 truncate" title={r.text} style={{ color: r.color }}>
              {r.text}
            </span>
            <span className="flex items-center gap-1 text-xs text-gray-500 justify-end"
              title={`blockiert: ${rc.blockedItems.join(", ")}`}>
              {rc.machines > 0 && <span className="tabular-nums mr-1">{rc.machines} Masch.</span>}
              {rc.blockedItems.slice(0, 5).map(b => <ItemIcon key={b} name={b} size={14} />)}
              {rc.blockedItems.length > 5 && <span>+{rc.blockedItems.length - 5}</span>}
            </span>
          </div>
        );
      })}
      {roots.length > shown.length && (
        <span className="text-xs text-gray-600">… +{roots.length - shown.length} weitere</span>
      )}
      {roots.length === 0 && (
        <span className="text-ok text-sm">keine Engpässe — allen Maschinen fehlt nichts</span>
      )}
    </Panel>
  );
}
