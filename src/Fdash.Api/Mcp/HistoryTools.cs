using System.ComponentModel;
using Fdash.Analysis;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Verlauf aus der Zeitreihe des Servers.
///
/// Bewusst als Buckets und nicht als Rohpunkte: eine Stunde Rohdaten sind 720
/// Messwerte, und die will niemand durch ein Sprachmodell schieben. Min/Max je
/// Bucket bleiben erhalten, damit ein kurzer Einbruch nicht im Mittelwert
/// verschwindet.
/// </summary>
[McpServerToolType]
public static class HistoryTools {

    [McpServerTool(Name = "get_history")]
    [Description("Verlauf einer Kennzahl ueber die Zeit, zusammengefasst in Buckets (Mittel, Minimum, "
        + "Maximum) plus Trend. Verfuegbare Metriken: production.produced, production.consumed, "
        + "power.production, power.consumption, power.satisfaction, power.accumulator_charge, "
        + "resource.rate_current, resource.depletion_seconds, robots.working, robots.waiting_for_charge.")]
    public static async Task<string> GetHistory(
            SnapshotView view, TrendCalculator trend,
            [Description("Metrikname, z. B. production.produced")] string metric,
            [Description("Filter, z. B. 'surface=nauvis,item=iron-plate' oder 'surface=nauvis,network=1'. "
                + "Muss exakt passen; leer aggregiert nichts, sondern liefert alle Reihen gemischt.")]
            string? labels = null,
            [Description("Zeitfenster in Stunden, Default 1")] double hours = 1,
            [Description("Anzahl Buckets, Default 12, Maximum 60")] int buckets = 12,
            CancellationToken ct = default) {
        int windowSeconds = (int)Math.Clamp(hours * 3600, 60, 90L * 86400);
        int n = Math.Clamp(buckets, 2, 60);

        HistorySummary summary = await trend.SummarizeAsync(view.SaveId, metric, labels, windowSeconds, n, ct);
        if(summary.Buckets.Count == 0) {
            return ToolResponse.Error(
                $"Keine Daten fuer '{metric}'" + (labels != null ? $" mit labels '{labels}'" : "") + ".",
                "Labels muessen exakt passen (z. B. 'surface=nauvis,item=iron-plate'). Die Rohstufe der "
                + "Zeitreihe haelt 6 h, die Minutenstufe 7 Tage — bei laengeren Fenstern wird automatisch "
                + "grober aufgeloest. Frisch gestartete Server haben noch keine Historie.");
        }

        return ToolResponse.Ok(new {
            metric,
            labels,
            unit = unitOf(metric),
            resolution = summary.Resolution,
            from = summary.From,
            to = summary.To,
            trend = summary.Trend.Direction,
            change_pct = ToolResponse.R(summary.Trend.ChangePct * 100, 1),
            buckets = summary.Buckets.Select(b => new {
                ts = b.Ts,
                avg = ToolResponse.R(b.Avg, 2),
                min = ToolResponse.R(b.Min, 2),
                max = ToolResponse.R(b.Max, 2)
            }).ToList()
        });
    }

    /// <summary>
    /// Die Zeitreihe speichert Rohwerte — Strom in Watt, nicht in MW. Ohne diese
    /// Angabe liest sich ein Bucket wie 575000000 als Unsinn statt als 575 MW.
    /// </summary>
    private static string unitOf(string metric) {
        if(metric is "power.production" or "power.consumption") return "W";
        if(metric == "power.satisfaction" || metric == "power.accumulator_charge") return "ratio 0..1";
        if(metric.StartsWith("production.", StringComparison.Ordinal)) return "items or fluid per minute";
        if(metric == "resource.rate_current") return "items per minute";
        if(metric == "resource.depletion_seconds") return "seconds";
        return "count";
    }
}
