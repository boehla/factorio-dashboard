using System.Text.Json;
using Fdash.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fdash.Collector;

/// <summary>
/// Baut aus dem <c>meta</c>-Job des Mods die Discovery samt Save-Fingerprint.
///
/// Frueher war das ein eigener RCON-Call; jetzt kommt <c>meta</c> in jedem
/// Snapshot mit, es gibt also nichts mehr abzufragen — nur noch zu lesen.
/// </summary>
public sealed class DiscoveryService {
    private readonly CollectorOptions options;
    private readonly ILogger<DiscoveryService> log;

    public DiscoveryService(IOptions<CollectorOptions> options, ILogger<DiscoveryService> log) {
        this.options = options.Value;
        this.log = log;
    }

    public Discovery Parse(JsonElement meta) {
        List<string> surfaces = new();
        if(meta.TryGetProperty("surfaces", out JsonElement surf) && surf.ValueKind == JsonValueKind.Array) {
            foreach(JsonElement e in surf.EnumerateArray()) surfaces.Add(e.GetString() ?? "");
        }
        List<string> mods = new();
        if(meta.TryGetProperty("mods", out JsonElement m) && m.ValueKind == JsonValueKind.Array) {
            foreach(JsonElement e in m.EnumerateArray()) mods.Add(e.GetString() ?? "");
        }

        Discovery d = new Discovery {
            Version = meta.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "" : "",
            SaveName = meta.TryGetProperty("save_name", out JsonElement sn) ? sn.GetString() ?? "" : "",
            Surfaces = surfaces.ToArray(),
            Tick = meta.TryGetProperty("tick", out JsonElement t) ? t.GetInt64() : 0,
            Seed = meta.TryGetProperty("seed", out JsonElement sd) ? sd.GetInt64() : 0,
            Mods = mods.ToArray()
        };
        return d with { SaveId = SaveFingerprint.Compute(d, options.SaveIdOverride) };
    }

    public void LogDiscovery(Discovery d) {
        log.LogInformation("Discovery: version={Version}, surfaces=[{Surfaces}], saveId={SaveId}",
            d.Version, string.Join(",", d.Surfaces), d.SaveId);
    }

    /// <summary>
    /// Laeuft im Mod noch der Erstscan? Solange sind Zaehler unvollstaendig —
    /// das Frontend soll das anzeigen koennen statt falsche Zahlen zu glauben.
    /// </summary>
    public static bool IsScanning(JsonElement meta) {
        return meta.TryGetProperty("exporter", out JsonElement ex)
            && ex.TryGetProperty("scanning", out JsonElement s)
            && s.ValueKind == JsonValueKind.True;
    }
}
