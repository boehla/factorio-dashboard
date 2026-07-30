using Fdash.Core;
using Fdash.Rcon;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fdash.Collector;

/// <summary>
/// Waehlt den Transportweg zum Mod und buendelt lesenden wie schreibenden
/// Zugriff.
///
/// Lesen geht ueber Datei ODER RCON — im Auto-Modus gewinnt die Datei, sobald
/// der Mod dort schreibt, weil das ohne RCON-Passwort auskommt und keinen
/// Tick-Slot belegt. Schreiben (Auto-Research) geht nur ueber RCON: ein
/// Factorio-Mod kann Dateien schreiben, aber nicht lesen.
/// </summary>
public sealed class GameLink : ISnapshotSource, IGameControl {
    private readonly FileSnapshotSource? file;
    private readonly RconSnapshotSource? rcon;
    private readonly CollectorOptions options;
    private readonly ILogger<GameLink> log;

    private string lastDescription = "";

    public GameLink(RconClient rconClient, IOptions<CollectorOptions> options, ILogger<GameLink> log) {
        this.options = options.Value;
        this.log = log;

        if(!string.IsNullOrWhiteSpace(this.options.ScriptOutputPath)) {
            file = new FileSnapshotSource(this.options.ScriptOutputPath!, log);
        }
        if(this.options.RconConfigured) {
            rcon = new RconSnapshotSource(rconClient, log);
        }
    }

    public string Description => lastDescription.Length > 0 ? lastDescription : "not connected";

    /// <summary>Schreibender Zugriff braucht RCON.</summary>
    public bool Available => rcon != null;

    private ISnapshotSource? select() {
        switch(options.Transport?.ToLowerInvariant()) {
            case "file":
                return file;
            case "rcon":
                return rcon;
            default:
                // Auto: Datei bevorzugen, solange der Mod dorthin schreibt.
                if(file != null && file.Ready) return file;
                if(rcon != null) return rcon;
                return file;
        }
    }

    public async Task<ModSnapshot?> FetchAsync(long knownSeq, CancellationToken ct) {
        ISnapshotSource? source = select();
        if(source == null) {
            throw new InvalidOperationException(
                "No transport available: set Collector:ScriptOutputPath (file mode) or Collector:RconPassword (RCON mode).");
        }
        if(!string.Equals(source.Description, lastDescription, StringComparison.Ordinal)) {
            lastDescription = source.Description;
            log.LogInformation("Reading mod snapshots via {Source}", lastDescription);
        }
        return await source.FetchAsync(knownSeq, ct);
    }

    public async Task<string> SetResearchAsync(string tech, CancellationToken ct) {
        if(rcon == null) {
            throw new InvalidOperationException("Auto-research needs RCON — set Collector:RconPassword.");
        }
        return await rcon.SetResearchAsync(tech, ct);
    }

    public async Task ExportPrototypesAsync(CancellationToken ct) {
        if(rcon == null) return;
        await rcon.ExportPrototypesAsync(ct);
    }

    /// <summary>Rohstatus des Mods (nur ueber RCON verfuegbar).</summary>
    public async Task<string?> ModStatusAsync(CancellationToken ct) {
        if(rcon == null) return null;
        return await rcon.StatusAsync(ct);
    }
}
