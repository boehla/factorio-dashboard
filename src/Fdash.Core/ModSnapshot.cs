using System.Text.Json;

namespace Fdash.Core;

/// <summary>
/// Ein Job-Ergebnis aus dem Mod. <paramref name="Tick"/> ist der Game-Tick, zu
/// dem der Durchlauf des Collectors fertig wurde — nicht der Zeitpunkt des
/// Abrufs. Daran laesst sich erkennen, wie alt ein Wert wirklich ist.
/// </summary>
public sealed record ModJob(long Tick, JsonElement Data);

/// <summary>
/// Vollstaendiger Snapshot des Mods (alle Jobs, ein Zeitpunkt).
/// <paramref name="Seq"/> steigt bei jedem veroeffentlichten Job-Ergebnis;
/// bleibt sie gleich, gibt es nichts Neues zu holen.
/// </summary>
public sealed record ModSnapshot(
    int Protocol,
    long Seq,
    long Tick,
    IReadOnlyDictionary<string, ModJob> Jobs);

/// <summary>
/// Liest ein Snapshot-Dokument des Mods (siehe mod/fdash-exporter/scripts/publish.lua).
/// </summary>
public static class ModSnapshotParser {
    /// <summary>Hoechste Protokollversion, die dieser Client versteht.</summary>
    public const int SupportedProtocol = 1;

    public static ModSnapshot Parse(string json) {
        using(JsonDocument doc = JsonDocument.Parse(json)) {
            JsonElement root = doc.RootElement;
            int protocol = root.TryGetProperty("protocol", out JsonElement p) ? p.GetInt32() : 0;
            long seq = root.TryGetProperty("seq", out JsonElement s) ? s.GetInt64() : 0;
            long tick = root.TryGetProperty("tick", out JsonElement t) ? t.GetInt64() : 0;

            Dictionary<string, ModJob> jobs = new(StringComparer.Ordinal);
            if(root.TryGetProperty("jobs", out JsonElement j) && j.ValueKind == JsonValueKind.Object) {
                foreach(JsonProperty prop in j.EnumerateObject()) {
                    long jobTick = prop.Value.TryGetProperty("tick", out JsonElement jt) ? jt.GetInt64() : tick;
                    if(!prop.Value.TryGetProperty("data", out JsonElement data)) continue;
                    // Clone, weil das JsonDocument gleich freigegeben wird.
                    jobs[prop.Name] = new ModJob(jobTick, data.Clone());
                }
            }
            return new ModSnapshot(protocol, seq, tick, jobs);
        }
    }
}

/// <summary>
/// Quelle fuer Mod-Snapshots. Zwei Implementierungen: Dateiausgabe des Mods
/// (<see cref="Fdash.Core"/> unabhaengig) und RCON. (Plan §5.2)
/// </summary>
public interface ISnapshotSource {
    /// <summary>Fuer Logs und /api/health — welcher Weg gerade genutzt wird.</summary>
    string Description { get; }

    /// <summary>
    /// Holt den Snapshot, wenn er neuer als <paramref name="knownSeq"/> ist.
    /// Liefert <c>null</c>, wenn es nichts Neues gibt oder die Quelle (noch)
    /// nicht bereit ist — beides ist kein Fehler.
    /// </summary>
    Task<ModSnapshot?> FetchAsync(long knownSeq, CancellationToken ct);
}

/// <summary>
/// Schreibender Pfad in die laufende Partie. Braucht zwingend RCON — ueber die
/// Dateiausgabe kann der Mod nur senden, nicht empfangen (Factorio-Mods haben
/// keine Datei-Lese-API).
/// </summary>
public interface IGameControl {
    bool Available { get; }

    /// <summary>Setzt die Forschung. Die Validierung passiert im Mod.</summary>
    Task<string> SetResearchAsync(string tech, CancellationToken ct);

    /// <summary>Stoesst den Prototyp-Export erneut an.</summary>
    Task ExportPrototypesAsync(CancellationToken ct);
}
