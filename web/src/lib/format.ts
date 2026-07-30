export function siWatts(w: number): string {
  const units = ["W", "kW", "MW", "GW", "TW"];
  let v = w, i = 0;
  while(v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
  return `${v.toFixed(2)} ${units[i]}`;
}
// Item-Namen suchen: "iron plate" soll auch "iron-plate" finden, daher werden
// Bindestrich/Unterstrich/Leerzeichen auf beiden Seiten vereinheitlicht.
// Leerer Query passt auf alles, damit Aufrufer nicht extra pruefen muessen.
const normName = (s: string) => s.toLowerCase().replace(/[-_\s]+/g, " ").trim();
export function itemMatches(name: string, query: string): boolean {
  const q = normName(query);
  return q === "" || normName(name).includes(q);
}
export function duration(sec: number): string {
  if(!isFinite(sec) || sec <= 0) return "—";
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
  if(h > 0) return `${h}h ${m}m`;
  const s = Math.floor(sec % 60);
  return `${m}m ${s}s`;
}
