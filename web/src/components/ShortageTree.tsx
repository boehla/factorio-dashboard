import { useState } from "react";
import { createPortal } from "react-dom";
import { ItemIcon } from "./ItemIcon";
import { buildShortageTree, fmtAmount, type MissingMap, type ShortageNode } from "../lib/shortages";

// Rot = Wurzel-Ursache (fehlt selbst nichts mehr), gelb = fehlt, weil weiter
// unten etwas fehlt. Gleiche Logik wie DSPs Root-Issue-Markierung.
export const ROOT_COLOR = "#ef4444";
export const CHAIN_COLOR = "#eab308";

// Verschachtelte Liste der Engpass-Kette (Pendant zu DSPs renderMissingTree).
export function ShortageTreeView({ nodes }: { nodes: ShortageNode[] }) {
  if(nodes.length === 0) return null;
  return (
    <ul className="list-none pl-3 border-l border-panelborder ml-1">
      {nodes.map(n => (
        <li key={n.item} className="py-0.5">
          <span className="whitespace-nowrap">
            <ItemIcon name={n.item} size={14} />
            <span style={{ color: n.isRoot ? ROOT_COLOR : CHAIN_COLOR }}>{n.item}</span>
            <span className="text-gray-400 ml-1 tabular-nums">{fmtAmount(n.amount)}</span>
          </span>
          <ShortageTreeView nodes={n.children} />
        </li>
      ))}
    </ul>
  );
}

// Baut die Kette selbst auf; fuer Aufrufer, die nur Item + Map haben.
export function ShortageChain({ item, map, depth = 12 }:
  { item: string; map: MissingMap; depth?: number }) {
  return <ShortageTreeView nodes={buildShortageTree(item, map, depth)} />;
}

// Icon-Reihe der direkt fehlenden Zutaten (Pendant zu DSPs formatMissingTree).
// Hover blendet die komplette Kette ein — bewusst als Portal mit position:fixed
// an der Mausposition: die Maschinenliste scrollt (overflow-y-auto) und wuerde
// ein absolut positioniertes Popover abschneiden.
export function ShortageChips({ missing, map, size = 16, max = 6 }:
  { missing: Record<string, number> | undefined; map: MissingMap; size?: number; max?: number }) {
  const [pop, setPop] = useState<{ item: string; x: number; y: number } | null>(null);
  if(!missing) return null;
  const entries = Object.entries(missing).filter(([, v]) => v > 0).sort(([, a], [, b]) => b - a);
  if(entries.length === 0) return null;
  const shown = entries.slice(0, max);
  return (
    <span className="flex items-center gap-1 flex-wrap">
      {shown.map(([name, amount]) => (
        <span key={name}
          className="flex items-center rounded px-0.5 hover:bg-white/10 cursor-help"
          onMouseEnter={e => setPop({ item: name, x: e.clientX, y: e.clientY })}
          onMouseMove={e => setPop(p => (p?.item === name ? { item: name, x: e.clientX, y: e.clientY } : p))}
          onMouseLeave={() => setPop(p => (p?.item === name ? null : p))}>
          <ItemIcon name={name} size={size} fallback={name} />
          <span className="text-xs tabular-nums"
            style={{ color: map.has(name) ? CHAIN_COLOR : ROOT_COLOR }}>
            {fmtAmount(amount)}
          </span>
        </span>
      ))}
      {entries.length > max && (
        <span className="text-xs text-gray-500">+{entries.length - max}</span>
      )}
      {pop && <ShortagePopover item={pop.item} map={map} x={pop.x} y={pop.y} />}
    </span>
  );
}

function ShortagePopover({ item, map, x, y }:
  { item: string; map: MissingMap; x: number; y: number }) {
  const nodes = buildShortageTree(item, map);
  // Nach links kippen, wenn rechts kein Platz mehr ist.
  const flip = x > window.innerWidth - 320;
  return createPortal(
    <div className="fixed z-50 pointer-events-none bg-panel border border-panelborder
      rounded-lg p-2 text-xs shadow-xl max-w-[20rem] max-h-[60vh] overflow-hidden"
      style={{ left: flip ? undefined : x + 14, right: flip ? window.innerWidth - x + 14 : undefined, top: y + 14 }}>
      <div className="whitespace-nowrap mb-1">
        <ItemIcon name={item} size={16} fallback={item} />
        <span style={{ color: map.has(item) ? CHAIN_COLOR : ROOT_COLOR }}>{item}</span>
        <span className="text-gray-500 ml-1">fehlt</span>
      </div>
      {nodes.length > 0
        ? <><div className="text-gray-500 mb-0.5">…weil fehlt:</div><ShortageTreeView nodes={nodes} /></>
        : <div style={{ color: ROOT_COLOR }}>Ursache — hier bricht die Kette ab</div>}
    </div>,
    document.body
  );
}
