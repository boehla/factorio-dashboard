import { useEffect, useState } from "react";

// Rendert das extrahierte Item-Icon (/api/icon/{name}); faellt bei 404 auf
// `fallback` zurueck (Standard: nichts). Bei Icons ohne danebenstehenden Text
// (z.B. in Stationsnamen) sollte fallback der Name sein, damit die Info nicht
// komplett verschwindet (F12).
//
// Factorio-Icon-PNGs enthalten ihre Mipmap-Kette als horizontalen Streifen
// (iron-ore.png ist 120x64 = 64+32+16+8). Wuerde man die Datei als Ganzes
// skalieren, saehe man das Icon mehrfach immer kleiner nebeneinander. Deshalb
// haengt das Bild an der Hoehe (width:auto) und der Container schneidet auf das
// linke Quadrat = die volle Aufloesung zu.
export function ItemIcon({ name, size = 16, fallback = null, title }:
  { name: string; size?: number; fallback?: React.ReactNode; title?: string }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => { setFailed(false); }, [name]);
  if(failed) return fallback == null ? null : <span title={title ?? name}>{fallback}</span>;
  return (
    <span
      title={title ?? name}
      className="inline-block align-middle mr-1 overflow-hidden"
      style={{ width: size, height: size }}>
      <img
        src={`/api/icon/${encodeURIComponent(name)}`}
        alt=""
        style={{ height: size, width: "auto", maxWidth: "none", display: "block" }}
        onError={() => setFailed(true)}
      />
    </span>
  );
}
