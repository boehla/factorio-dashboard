using Fdash.Core;
using Fdash.Rcon;
using Microsoft.Extensions.Logging;

namespace Fdash.Collector;

/// <summary>
/// Holt den Snapshot ueber RCON aus dem Remote-Interface des Mods.
///
/// Wichtig fuer die Tick-Zeit: die Kommandos rechnen NICHTS aus. Sie geben nur
/// einen im Mod bereits fertig serialisierten String zurueck — die Arbeit ist
/// laengst ueber viele Ticks verteilt passiert. Genau das war beim alten
/// /silent-command-Ansatz anders, wo jeder Poll die volle Auswertung im Tick
/// erzwang.
///
/// Zusaetzlich wird pro Poll erst nur die Sequenznummer geholt (ein winziges
/// Kommando); der grosse Transfer passiert nur, wenn es wirklich etwas Neues
/// gibt.
/// </summary>
public sealed class RconSnapshotSource : ISnapshotSource, IGameControl {
    private const string SeqCommand = "/silent-command rcon.print(remote.call('fdash','seq'))";
    private const string SnapshotCommand = "/silent-command rcon.print(remote.call('fdash','snapshot'))";

    private readonly RconClient rcon;
    private readonly ILogger log;
    private readonly SemaphoreSlim gate = new(1, 1);

    public RconSnapshotSource(RconClient rcon, ILogger log) {
        this.rcon = rcon;
        this.log = log;
    }

    public string Description => "rcon";

    public bool Available => true;

    public async Task<ModSnapshot?> FetchAsync(long knownSeq, CancellationToken ct) {
        string seqRaw = await executeAsync(SeqCommand, ct);
        if(string.IsNullOrWhiteSpace(seqRaw)) {
            throw new RconException("Empty response from remote.call('fdash','seq') — is the fdash-exporter mod loaded?");
        }
        if(!long.TryParse(seqRaw.Trim(), out long seq)) {
            throw new RconException($"Unexpected response from fdash.seq: {truncate(seqRaw)}");
        }
        if(seq <= knownSeq) return null;

        string json = await executeAsync(SnapshotCommand, ct);
        if(string.IsNullOrWhiteSpace(json)) return null;

        try {
            ModSnapshot snapshot = ModSnapshotParser.Parse(json);
            if(snapshot.Protocol > ModSnapshotParser.SupportedProtocol) {
                log.LogWarning("Mod snapshot protocol {Actual} is newer than supported {Supported} — update the dashboard.",
                    snapshot.Protocol, ModSnapshotParser.SupportedProtocol);
            }
            return snapshot;
        } catch(System.Text.Json.JsonException ex) {
            throw new RconException($"Invalid snapshot JSON over RCON: {truncate(json)}", ex);
        }
    }

    public async Task<string> SetResearchAsync(string tech, CancellationToken ct) {
        string safe = RequireSafeIdentifier(tech);
        string cmd = $"/silent-command rcon.print(remote.call('fdash','set_research','{safe}'))";
        return await executeAsync(cmd, ct);
    }

    public async Task ExportPrototypesAsync(CancellationToken ct) {
        await executeAsync("/silent-command rcon.print(remote.call('fdash','export_prototypes'))", ct);
    }

    /// <summary>Kleiner Diagnose-Call fuer Startup und /api/health.</summary>
    public async Task<string> StatusAsync(CancellationToken ct) {
        return await executeAsync("/silent-command rcon.print(remote.call('fdash','status'))", ct);
    }

    private async Task<string> executeAsync(string command, CancellationToken ct) {
        await gate.WaitAsync(ct);
        try {
            if(!rcon.Connected) await rcon.ConnectAsync(ct);
            string raw = await rcon.ExecuteAsync(command, ct);
            return raw.Trim();
        } finally {
            gate.Release();
        }
    }

    /// <summary>
    /// Technologienamen gehen als Lua-String-Literal in ein RCON-Kommando.
    /// Alles ausserhalb von [A-Za-z0-9-_.] wird abgelehnt statt escaped —
    /// Factorio-Prototypnamen enthalten nichts anderes, und Ablehnen ist die
    /// Variante, bei der man sich nicht verrechnen kann.
    /// </summary>
    public static string RequireSafeIdentifier(string value) {
        if(string.IsNullOrEmpty(value)) throw new ArgumentException("Empty identifier");
        foreach(char c in value) {
            if(!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) {
                throw new ArgumentException($"Unsafe identifier: {value}");
            }
        }
        return value;
    }

    private static string truncate(string s) => s.Length <= 200 ? s : s[..200];
}
