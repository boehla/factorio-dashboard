namespace Fdash.Core;

// Datenmodelle pro Modul (Plan §3). Bewusst schlank gehalten; die Aggregation
// passiert in Lua, C# deserialisiert nur. Fuer generische/modded Items werden
// Dictionaries statt fester Felder verwendet.

// --- Assembler / Maschinen (§3.1) ---
public sealed record MachineRecipeStat {
    public int Total { get; init; }
    public Dictionary<string, int> Status { get; init; } = new();
    public double AvgSpeed { get; init; }
    public bool Modded { get; init; }
}

public sealed record AssemblersSnapshot {
    public string Surface { get; init; } = "";
    public Dictionary<string, MachineRecipeStat> ByRecipe { get; init; } = new();
    public int NoRecipe { get; init; }
}

// --- Zuege (§3.2) ---
public sealed record TrainProblem {
    public int Id { get; init; }
    public string? Surface { get; init; }
    public string State { get; init; } = "";
    public string? ScheduleStation { get; init; }
}

public sealed record TrainsSnapshot {
    public List<TrainProblem> Problems { get; init; } = new();
    public TrainTotals Totals { get; init; } = new();
}

public sealed record TrainTotals {
    public int Total { get; init; }
    public int Ok { get; init; }
    public int Problem { get; init; }
}

// --- Strom (§3.3) ---
public sealed record PowerNetwork {
    public int Id { get; init; }
    public double Production { get; init; }
    public double Consumption { get; init; }
    public double Satisfaction { get; init; }
    public Dictionary<string, double> ByProducer { get; init; } = new();
    public Dictionary<string, double> ByConsumerGroup { get; init; } = new();
}

public sealed record PowerSnapshot {
    public string Surface { get; init; } = "";
    public List<PowerNetwork> Networks { get; init; } = new();
}

// --- Logistik (§3.5) ---
public sealed record RobotStat {
    public int Total { get; init; }
    public int Idle { get; init; }
    public int Working { get; init; }
    public int Charging { get; init; }
    public int WaitingForCharge { get; init; }
}

public sealed record LogisticNetwork {
    public int Id { get; init; }
    public int Roboports { get; init; }
    public RobotStat LogisticRobots { get; init; } = new();
    public RobotStat ConstructionRobots { get; init; } = new();
}

public sealed record LogisticsSnapshot {
    public string Surface { get; init; } = "";
    public List<LogisticNetwork> Networks { get; init; } = new();
}

// --- Ressourcen (§3.4) ---
public sealed record ResourceStat {
    public bool Infinite { get; init; }
    public double? TotalAmount { get; init; }
    public double? CoveredAmount { get; init; }
    public int DrillsTotal { get; init; }
    public int DrillsWorking { get; init; }
    public double RateCurrent { get; init; }
    public double RateMax { get; init; }
    public double? DepletionSeconds { get; init; }
    public double? YieldPct { get; init; }
}

public sealed record ResourcesSnapshot {
    public string Surface { get; init; } = "";
    public Dictionary<string, ResourceStat> Resources { get; init; } = new();
    // Fortschritt des chunked Scans fuer die UI (§7)
    public int ScanStep { get; init; }
    public int ScanTotal { get; init; }
    public long LastCompleteTs { get; init; }
}

// --- Rezept-Prototypen (Item-Abhaengigkeitsgraph) ---
public sealed record RecipeIo {
    public string Name { get; init; } = "";
    public string Type { get; init; } = "item";   // "item" | "fluid"
    public double Amount { get; init; } = 1;      // expected items/fluid per craft (amount * probability)
}

public sealed record RecipeProto {
    public string Name { get; init; } = "";
    public double EnergyRequired { get; init; } = 0.5;  // crafting time in seconds @ speed 1
    public List<RecipeIo> Ingredients { get; init; } = new();
    public List<RecipeIo> Products { get; init; } = new();
}

// --- Produktion / Stall (§3.8) ---
public sealed record ProductionItem {
    public string Item { get; init; } = "";
    public double ProducedPerMin { get; init; }
    public double ConsumedPerMin { get; init; }
    public double Ratio { get; init; }
    public string Trend { get; init; } = "flat";
}

public sealed record StallItem {
    public string Item { get; init; } = "";
    public string Reason { get; init; } = "";
    public int MachinesAffected { get; init; }
    public int SinceSeconds { get; init; }
}

public sealed record ProductionSnapshot {
    public string Surface { get; init; } = "";
    public List<ProductionItem> Items { get; init; } = new();
    public List<StallItem> Stalled { get; init; } = new();
}
