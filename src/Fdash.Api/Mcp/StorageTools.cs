using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Bestaende: Logistiknetz und Kisten, mit Puffer-Einordnung.
///
/// Zusammen mit dem Maschinenstatus ergibt das die Suchrichtung. "Maschine
/// wartet auf Zutat" allein sagt nicht, wo es klemmt; volle Kisten davor sagen
/// downstream, leere upstream.
/// </summary>
[McpServerToolType]
public static class StorageTools {

    [McpServerTool(Name = "get_logistic_and_storage")]
    [Description("Roboternetze (arbeitend, im Leerlauf, wartend auf Ladung) und Bestaende — im "
        + "Logistiknetz und in Kisten, je Item mit Trend und Einordnung als leer/fallend/gesund/"
        + "steigend/voll. Kistendaten nur, wenn der Mod-Job 'containers' eingeschaltet ist.")]
    public static async Task<string> GetLogisticAndStorage(
            SnapshotView view, TrendCalculator trend, IOptions<McpOptions> opts,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Was: logistic_network | containers | both")] string scope = "both",
            [Description("Nur diese Items, kommagetrennt")] string? items = null,
            [Description("Maximale Anzahl Items, Default 25")] int limit = 25,
            CancellationToken ct = default) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? log = view.Get("logistics", surf);
        JobPayload? cont = view.Get("containers", surf);
        if(log == null && cont == null) return ToolResponse.NoData("logistics", surf);

        HashSet<string>? filter = null;
        if(!string.IsNullOrWhiteSpace(items)) {
            filter = new HashSet<string>(
                items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
        }

        // ---------------------------------------------------------- Roboter
        List<object> networks = new List<object>();
        Dictionary<string, double> netContents = new Dictionary<string, double>(StringComparer.Ordinal);
        if(log != null && scope != "containers") {
            foreach(JsonElement n in Json.Array(log.Data, "networks")) {
                JsonElement bots = Json.Sub(n, "logistic_robots");
                JsonElement build = Json.Sub(n, "construction_robots");
                networks.Add(new {
                    id = Json.Int(n, "id"),
                    roboports = Json.Int(n, "roboports"),
                    logistic = new {
                        total = Json.Int(bots, "total"),
                        working = Json.Int(bots, "working"),
                        idle = Json.Int(bots, "idle"),
                        charging = Json.Int(bots, "charging"),
                        waiting_for_charge = Json.Int(bots, "waiting_for_charge")
                    },
                    construction = new {
                        total = Json.Int(build, "total"),
                        working = Json.Int(build, "working")
                    }
                });
                foreach(KeyValuePair<string, double> kv in Json.NumMap(n, "contents")) {
                    netContents[kv.Key] = (netContents.TryGetValue(kv.Key, out double v) ? v : 0) + kv.Value;
                }
                if(networks.Count >= 10) break;   // mehr Netze sind selten interessant
            }
        }

        // ----------------------------------------------------------- Kisten
        Dictionary<string, double> chest = new Dictionary<string, double>(StringComparer.Ordinal);
        if(cont != null && scope != "logistic_network") {
            foreach(JsonProperty p in Json.Object(cont.Data, "items")) {
                if(p.Value.ValueKind == JsonValueKind.Number) chest[p.Name] = p.Value.GetDouble();
            }
        }

        // ------------------------------------------------- zusammenfuehren
        SortedSet<string> names = new SortedSet<string>(StringComparer.Ordinal);
        foreach(string k in netContents.Keys) names.Add(k);
        foreach(string k in chest.Keys) names.Add(k);

        List<(string Item, double InNet, double InChests)> rows = new List<(string, double, double)>();
        foreach(string name in names) {
            if(filter != null && !filter.Contains(name)) continue;
            netContents.TryGetValue(name, out double inNet);
            chest.TryGetValue(name, out double inChests);
            rows.Add((name, inNet, inChests));
        }
        rows.Sort((a, b) => (b.InNet + b.InChests).CompareTo(a.InNet + a.InChests));

        (List<(string Item, double InNet, double InChests)> page, bool truncated, int total) =
            ToolResponse.Cap(rows, limit, opts.Value.MaxItems);

        List<object> outp = new List<object>();
        foreach((string item, double inNet, double inChests) in page) {
            string dir = "unknown";
            if(cont != null && inChests > 0) {
                TrendResult t = await trend.ComputeAsync(view.SaveId, "storage.item_count",
                    $"surface={surf},item={item}", 1800, ct);
                dir = t.Direction;
            }
            outp.Add(new {
                item,
                in_logistic_network = inNet > 0 ? ToolResponse.R(inNet, 0) : (double?)null,
                in_containers = inChests > 0 ? ToolResponse.R(inChests, 0) : (double?)null,
                trend = dir == "unknown" ? null : dir
            });
        }

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = log?.AgeSeconds ?? cont?.AgeSeconds,
            networks,
            container_data = cont != null,
            containers_scanned = cont != null ? Json.Int(cont.Data, "containers") : (int?)null,
            note = cont == null && scope != "logistic_network"
                ? "Keine Kistendaten — der Job 'containers' ist per Default aus. Einschalten ueber "
                  + "die Mod-Einstellung fdash-container-scan (kostet Tick-Zeit: ein Inventar-Read je Kiste)."
                : null,
            total_available = total,
            truncated,
            items = outp
        });
    }
}
