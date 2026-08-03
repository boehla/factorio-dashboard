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

// --- Supply Chain Planner ---
export interface SupplyChain {
  id: number;
  targetItem: string;
  recipeName: string;
  name: string | null;
  surface: string;
  targetPerMin: number | null;
  nodes: SupplyNode[];
  createdAt: string;
  updatedAt: string;
}

export interface SupplyNode {
  id?: number;
  itemName: string;
  itemType: 'item' | 'fluid';
  amountPerCraft: number;
  source: 'local' | 'train' | 'ignore';
  childRecipe: string | null;
  children: SupplyNode[];
}

export interface TrainAvailability {
  provides: Record<string, { itemType: string; stationCount: number }>;
  requests: Record<string, { itemType: string; stationCount: number }>;
}

export interface RecipeOption {
  name: string;
  energy: number;
  category: string | null;
  allowProductivity: boolean;
  isResearched: boolean;
  isProducing: boolean;
  machineCount: number;
  ingredients: { name: string; type: string; amount: number }[];
  products: { name: string; type: string; amount: number }[];
}
