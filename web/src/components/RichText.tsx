import React from "react";
import { ItemIcon } from "./ItemIcon";

// Factorio-Rich-Text-Tags, die auf einen Prototyp mit Icon zeigen. Der Name
// wird ueber /api/icon/{name} (Dateinamen-Stamm) aufgeloest.
const ICON_TYPES = new Set([
  "item", "fluid", "virtual-signal", "entity", "recipe", "technology", "tech",
  "item-group", "tile", "quality", "planet", "space-location", "achievement",
  "ammo", "gun", "capsule", "module", "armor", "tool", "fuel"
]);

const TOKEN_RE = /\[[^\]]*\]/g;

// Rendert einen Factorio-String (z.B. Stationsname "[item=fish][virtual-signal=
// signal-input] Fisch") als Text + Icons. Nicht-Icon-Tags (color, font, gps,
// Schliess-Tags) werden entfernt. Kann ein Icon nicht geladen werden, zeigt es
// den Namen als Text (Fallback in ItemIcon).
export function RichText({ text, size = 16 }: { text?: string | null; size?: number }) {
  if(!text) return null;
  const parts: React.ReactNode[] = [];
  let last = 0, key = 0;
  let m: RegExpExecArray | null;
  TOKEN_RE.lastIndex = 0;
  while((m = TOKEN_RE.exec(text)) !== null) {
    if(m.index > last) parts.push(text.slice(last, m.index));
    const inner = m[0].slice(1, -1); // Klammern entfernen
    const eq = inner.indexOf("=");
    if(eq > 0) {
      const type = inner.slice(0, eq).toLowerCase();
      // Suffixe wie ",quality=uncommon" abschneiden
      const value = inner.slice(eq + 1).split(",")[0].trim();
      if(type === "img") {
        // [img=item/iron-plate] -> "iron-plate"
        const slash = value.lastIndexOf("/");
        const name = slash >= 0 ? value.slice(slash + 1) : value;
        parts.push(<ItemIcon key={key++} name={name} size={size} />);
      } else if(ICON_TYPES.has(type)) {
        parts.push(<ItemIcon key={key++} name={value} size={size} fallback={value} />);
      }
      // sonstige Tags (color/font/gps/train/...) werden verworfen
    }
    // Tags ohne '=' (z.B. Schliess-Tags [/color]) werden verworfen
    last = m.index + m[0].length;
  }
  if(last < text.length) parts.push(text.slice(last));
  return <>{parts}</>;
}
