using Fdash.Core;
using Microsoft.Extensions.Logging;

namespace Fdash.Collector;

/// <summary>
/// Liest die Dateiausgabe des Mods aus <c>script-output/fdash/</c>.
///
/// Der Mod schreibt den Snapshot abwechselnd in eine von drei rotierenden
/// Dateien und danach erst <c>index.json</c>. Wer erst den Index liest und dann
/// die dort genannte Datei, kann deshalb keine halb geschriebene Datei
/// erwischen — bis dieselbe Datei wieder an der Reihe ist, vergehen zwei
/// weitere Schreibintervalle.
///
/// Das ist der bevorzugte Weg: kein RCON-Passwort, kein Tick-Slot pro Abruf,
/// beliebig hohe Abfragefrequenz.
/// </summary>
public sealed class FileSnapshotSource : ISnapshotSource {
    private readonly string dir;
    private readonly ILogger log;

    public FileSnapshotSource(string scriptOutputPath, ILogger log) {
        dir = Path.Combine(Environment.ExpandEnvironmentVariables(scriptOutputPath), "fdash");
        this.log = log;
    }

    public string Description => "file:" + dir;

    public string Directory => dir;

    /// <summary>
    /// Der Mod schreibt spaetestens alle paar Sekunden (schon `meta` allein
    /// sorgt dafuer). Ein deutlich aelterer Index heisst also: die Dateiausgabe
    /// ist abgeschaltet oder der Server laeuft nicht mehr — dann soll der
    /// Auto-Modus auf RCON umschalten statt ewig eine alte Datei zu lesen.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>Hat der Mod aktuell geschrieben?</summary>
    public bool Ready {
        get {
            string indexPath = Path.Combine(dir, "index.json");
            if(!File.Exists(indexPath)) return false;
            try {
                return DateTime.UtcNow - File.GetLastWriteTimeUtc(indexPath) < StaleAfter;
            } catch(IOException) {
                return false;
            }
        }
    }

    public async Task<ModSnapshot?> FetchAsync(long knownSeq, CancellationToken ct) {
        string indexPath = Path.Combine(dir, "index.json");
        if(!File.Exists(indexPath)) return null;

        string? indexJson = await readSharedAsync(indexPath, ct);
        if(indexJson == null) return null;

        long seq;
        string file;
        try {
            using(System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(indexJson)) {
                seq = doc.RootElement.TryGetProperty("seq", out System.Text.Json.JsonElement s) ? s.GetInt64() : 0;
                file = doc.RootElement.TryGetProperty("file", out System.Text.Json.JsonElement f)
                    ? f.GetString() ?? "" : "";
            }
        } catch(System.Text.Json.JsonException) {
            // Index gerade im Schreiben — beim naechsten Poll erneut versuchen.
            return null;
        }

        if(seq <= knownSeq || string.IsNullOrEmpty(file)) return null;

        // Dateiname kommt aus dem Index, nicht von aussen — trotzdem auf den
        // Ordner festnageln, damit ein manipulierter Index nicht ausbricht.
        string name = Path.GetFileName(file);
        string snapshotPath = Path.Combine(dir, name);
        if(!File.Exists(snapshotPath)) return null;

        string? json = await readSharedAsync(snapshotPath, ct);
        if(json == null) return null;

        try {
            ModSnapshot snapshot = ModSnapshotParser.Parse(json);
            if(snapshot.Protocol > ModSnapshotParser.SupportedProtocol) {
                log.LogWarning("Mod snapshot protocol {Actual} is newer than supported {Supported} — update the dashboard.",
                    snapshot.Protocol, ModSnapshotParser.SupportedProtocol);
            }
            return snapshot.Seq > knownSeq ? snapshot : null;
        } catch(System.Text.Json.JsonException ex) {
            log.LogDebug(ex, "Snapshot file {Path} not parsable yet", snapshotPath);
            return null;
        }
    }

    /// <summary>
    /// Liest eine Datei, die Factorio parallel schreiben koennte:
    /// FileShare.ReadWrite, plus ein kurzer Retry gegen den Moment, in dem
    /// Windows die Datei exklusiv haelt.
    /// </summary>
    private static async Task<string?> readSharedAsync(string path, CancellationToken ct) {
        for(int attempt = 0; attempt < 3; attempt++) {
            try {
                await using(FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete)) {
                    using(StreamReader reader = new StreamReader(fs)) {
                        return await reader.ReadToEndAsync(ct);
                    }
                }
            } catch(IOException) {
                if(attempt == 2) return null;
                await Task.Delay(40, ct);
            } catch(UnauthorizedAccessException) {
                return null;
            }
        }
        return null;
    }
}
