using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Strom je elektrischem Netz. Der Momentanwert allein taeuscht: eine
/// Satisfaction von 1.0 sagt nichts, solange die Akkus sie stuetzen. Deshalb
/// kommen Akkustand-Trend und die Zahl der Einbrueche im Fenster aus der
/// Zeitreihe dazu.
/// </summary>
[McpServerToolType]
public static class PowerTools {

    [McpServerTool(Name = "get_power_report")]
    [Description("Strom je Netz: Erzeugung, Bedarf und Nennleistung in MW, Deckungsgrad, Akkustand "
        + "mit Trend, Anzahl der Einbrueche im Zeitfenster und die groessten Erzeuger und Verbraucher.")]
    public static async Task<string> GetPowerReport(
            SnapshotView view, TrendCalculator trend, IOptions<McpOptions> opts,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Nur dieses Netz (electric_network_id), leer = alle")] int? networkId = null,
            [Description("Zeitfenster: five_seconds|one_minute|ten_minutes|one_hour|ten_hours")]
            string window = "one_hour",
            [Description("Wie viele Erzeuger-/Verbrauchertypen je Netz, Default 8")] int topConsumers = 8,
            CancellationToken ct = default) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? p = view.Get("power", surf);
        if(p == null) return ToolResponse.NoData("power", surf);

        int windowSeconds = FactoryTools.WindowSeconds(window);
        double warn = 0.95;

        List<object> nets = new List<object>();
        List<JsonElement> all = Json.Array(p.Data, "networks").ToList();
        // Die groessten Netze zuerst: auf einer gewachsenen Basis sind die
        // meisten der 23 Netze eine einzelne Lampe an einem Solarpanel.
        all.Sort((a, b) => Json.Num(b, "consumption").CompareTo(Json.Num(a, "consumption")));

        foreach(JsonElement n in all) {
            int id = Json.Int(n, "id");
            if(networkId is int want && id != want) continue;
            if(nets.Count >= opts.Value.MaxItems) break;

            string labels = $"surface={surf},network={id}";
            (int below, int total) = await trend.BelowThresholdAsync(view.SaveId, "power.satisfaction",
                labels, warn, windowSeconds, ct);

            JsonElement acc = Json.Sub(n, "accumulators");
            object? accumulators = null;
            if(acc.ValueKind == JsonValueKind.Object) {
                double capacity = Json.Num(acc, "capacity");
                double charge = capacity > 0 ? Json.Num(acc, "energy") / capacity : 0;
                TrendResult chargeTrend = await trend.ComputeAsync(view.SaveId,
                    "power.accumulator_charge", labels, windowSeconds, ct);
                accumulators = new {
                    count = Json.Int(acc, "count"),
                    charge = ToolResponse.R(charge, 3),
                    charge_trend = chargeTrend.Direction,
                    charging_mw = ToolResponse.Mw(Json.Num(acc, "charge_rate")),
                    discharging_mw = ToolResponse.Mw(Json.Num(acc, "discharge_rate"))
                };
            }

            double consumption = Json.Num(n, "consumption");
            double production = Json.Num(n, "production");
            double capacityW = Json.Num(n, "capacity");

            nets.Add(new {
                id,
                satisfaction = ToolResponse.R(Json.Num(n, "satisfaction", 1), 3),
                production_mw = ToolResponse.Mw(production),
                consumption_mw = ToolResponse.Mw(consumption),
                capacity_mw = ToolResponse.Mw(capacityW),
                // Was noch an steuerbarer Erzeugung bereitsteht. Solar und Akkus
                // zaehlen bewusst nicht mit — sie helfen nachts nicht.
                headroom_mw = ToolResponse.Mw(Math.Max(0, capacityW - consumption)),
                brownout_samples = below,
                samples_in_window = total,
                accumulators,
                by_producer = topN(n, "by_producer", topConsumers),
                by_consumer = topN(n, "by_consumer_group", topConsumers)
            });
        }

        return ToolResponse.Ok(new {
            surface = surf,
            window,
            data_age_seconds = p.AgeSeconds,
            brownout_threshold = warn,
            networks_total = all.Count,
            truncated = nets.Count < all.Count && networkId == null,
            networks = nets
        });
    }

    /// <summary>Groesste Posten in MW; der lange Rest wird zu "other" summiert.</summary>
    private static Dictionary<string, double> topN(JsonElement net, string prop, int count) {
        List<KeyValuePair<string, double>> list = Json.NumMap(net, prop).ToList();
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.Ordinal);
        double rest = 0;
        for(int i = 0; i < list.Count; i++) {
            if(i < count) result[list[i].Key] = ToolResponse.Mw(list[i].Value);
            else rest += list[i].Value;
        }
        if(rest > 0) result["other"] = ToolResponse.Mw(rest);
        return result;
    }
}
