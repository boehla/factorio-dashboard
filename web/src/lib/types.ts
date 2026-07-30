// Spiegelt die JSON-Snapshots des Collectors (Plan §3).
export interface Snapshot { job: string; saveId: string; ts: number; payload: any; }

export interface Accumulators {
  count: number; energy: number; capacity: number; // J
  charge_rate: number; discharge_rate: number;     // W (1-Min-Mittel)
}
export interface PowerNetwork {
  id: number; production: number; consumption: number; satisfaction: number;
  capacity?: number; // max. Generator-Nennleistung (W); Solar/Akkus nicht enthalten
  accumulators?: Accumulators | null;
  by_producer: Record<string, number>; by_consumer_group: Record<string, number>;
}
export interface TrainCargo { name: string; count: number; }
export interface TrainProblem {
  id: number; surface?: string; state: string;
  schedule_station?: string; cargo?: TrainCargo[];
}
export interface StallItem { item: string; reason: string; machines_affected: number; since_seconds: number; }

// Eine Item-Gruppe aus assemblers.lua (by_item). `missing` fehlt, solange keine
// Maschine der Gruppe hungert — nur dann liest das Snippet die Zutatenpuffer.
export interface AssemblerGroup {
  total: number;
  status: Record<string, number>;
  avg_speed: number;
  recipes: Record<string, number>;
  starving?: number;
  missing?: Record<string, number>;
}
