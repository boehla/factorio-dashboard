using System.Text.Json;

namespace Fdash.Core;

/// <summary>
/// Ein per SignalR an das Frontend gepushter Snapshot eines Jobs.
/// Payload bleibt bewusst als JsonElement generisch, weil modded Items
/// die Metrik-Namen nicht im Voraus festlegen. (Plan §5.5, §7)
/// </summary>
public sealed record Snapshot(
    string Job,
    string SaveId,
    long Ts,
    JsonElement Payload);
