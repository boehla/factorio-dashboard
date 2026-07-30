namespace Fdash.Core;

// --- Plattformen (§3.6) ---
public sealed record FuelStat {
    public double Current { get; init; }
    public double Max { get; init; }
    public double Pct { get; init; }
}

public sealed record PlatformStat {
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string Location { get; init; } = "";
    public double Speed { get; init; }
    public int Thrusters { get; init; }
    public Dictionary<string, FuelStat> Fuel { get; init; } = new();
    public Dictionary<string, long> HubContents { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public sealed record PlatformsSnapshot {
    public List<PlatformStat> Platforms { get; init; } = new();
}

// --- Orbital Requests (§3.7) ---
public sealed record OrbitalRequest {
    public string Item { get; init; } = "";
    public long Requested { get; init; }
    public long Delivered { get; init; }
    public int WaitingPlatforms { get; init; }
}

public sealed record OrbitalSnapshot {
    public string Surface { get; init; } = "";
    public List<OrbitalRequest> Requests { get; init; } = new();
}

// --- Getaggte Circuits (§3.9) ---
public sealed record CircuitTag {
    public string Label { get; init; } = "";
    public string Surface { get; init; } = "";
    public Dictionary<string, long> Signals { get; init; } = new();
}

public sealed record CircuitsSnapshot {
    public List<CircuitTag> Tags { get; init; } = new();
}
