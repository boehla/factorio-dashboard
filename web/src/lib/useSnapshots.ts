import { useEffect, useRef, useState } from "react";
import * as signalR from "@microsoft/signalr";
import type { Snapshot } from "./types";

// Abonniert den SignalR-Hub und haelt den jeweils letzten Snapshot pro Job.
// Kein Polling im Browser (Plan §7).
export function useSnapshots() {
  const [snaps, setSnaps] = useState<Record<string, any>>({});
  const [connected, setConnected] = useState(false);
  const connRef = useRef<signalR.HubConnection | null>(null);

  useEffect(() => {
    const conn = new signalR.HubConnectionBuilder()
      .withUrl("/hub")
      .withAutomaticReconnect()
      .build();
    connRef.current = conn;

    conn.on("snapshot", (s: Snapshot) => {
      setSnaps(prev => ({ ...prev, [s.job]: s.payload }));
    });
    conn.onreconnected(() => setConnected(true));
    conn.onclose(() => setConnected(false));

    conn.start()
      .then(() => setConnected(true))
      .catch(() => setConnected(false));

    // Initiale Snapshots per REST holen (fuer sofortige Anzeige)
    fetch("/api/snapshots").then(r => r.json()).then((all: Record<string, Snapshot>) => {
      const seed: Record<string, any> = {};
      Object.values(all).forEach(s => { seed[s.job] = s.payload; });
      setSnaps(prev => ({ ...seed, ...prev }));
    }).catch(() => {});

    return () => { conn.stop(); };
  }, []);

  return { snaps, connected };
}
