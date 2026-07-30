namespace Fdash.Core;

/// <summary>Ergebnis des Discovery-Calls beim Start. (Plan §4.2)</summary>
public sealed record Discovery {
    public string Version { get; init; } = "";
    public string SaveName { get; init; } = "";
    public string[] Surfaces { get; init; } = Array.Empty<string>();
    public long Tick { get; init; }
    public long Seed { get; init; }
    public string[] Mods { get; init; } = Array.Empty<string>();
    /// <summary>Aus Seed (+ optional SaveName) abgeleiteter stabiler Fingerprint.</summary>
    public string SaveId { get; init; } = "";
}
