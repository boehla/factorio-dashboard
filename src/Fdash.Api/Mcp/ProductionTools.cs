using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Produktions- und Verbrauchsraten aus der Force-Statistik des Spiels.
///
/// Der Mehrwert gegenueber der Spiel-GUI ist der Trend: er unterscheidet "gerade
/// angelaufen" von "bricht ein". Er kommt aus der Zeitreihe des Servers, nicht
/// aus dem Mod — dort gaebe es dafuer keinen Zustand ausser im Savegame.
/// </summary>
[McpServerToolType]
public static class ProductionTools {

    [McpServerTool(Name = "get_production_stats")]
    [Description("Produktion und Verbrauch je Item/Fluid in Stueck pro Minute, mit Netto-Differenz, "
        + "Verhaeltnis und Trend (rising|stable|falling). Sortierbar nach Defizit, Produktion, "
        + "Verbrauch oder Netto.")]
    public static async Task<string> GetProductionStats(
            SnapshotView view, TrendCalculator trend, IOptions<McpOptions> opts,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Trendfenster: five_seconds|one_minute|ten_minutes|one_hour|ten_hours")]
            string window = "ten_minutes",
            [Description("Nur diese Items/Fluide, kommagetrennt. Leer = alle.")] string? items = null,
            [Description("Sortierung: deficit|production|consumption|net")] string sortBy = "deficit",
            [Description("Maximale Anzahl, Default 30")] int limit = 30,
            [Description("Fluide mit ausgeben")] bool includeFluids = true,
            [Description("Nur Items, die irgendwo als Zutat vorkommen (blendet Bau-Items aus)")]
            bool ingredientsOnly = true,
            CancellationToken ct = default) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? prod = view.Get("production", surf);
        if(prod == null) return ToolResponse.NoData("production", surf);

        HashSet<string>? filter = null;
        if(!string.IsNullOrWhiteSpace(items)) {
            filter = new HashSet<string>(
                items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
        }

        List<Row> rows = new List<Row>();
        foreach(JsonElement it in Json.Array(prod.Data, "items")) {
            string name = Json.Str(it, "item");
            string type = Json.Str(it, "type", "item");
            if(filter != null && !filter.Contains(name)) continue;
            if(!includeFluids && type == "fluid") continue;
            // Ohne Filter waere die Liste auf Pyanodons mehrere tausend Zeilen
            // lang; ein Item, das nirgends Zutat ist, wird nur fuer den Bau
            // gefertigt und hat keine sinnvolle Rate.
            if(filter == null && ingredientsOnly && !Json.Bool(it, "is_ingredient")) continue;

            double p = Json.Num(it, "produced_per_min");
            double c = Json.Num(it, "consumed_per_min");
            if(p == 0 && c == 0) continue;
            rows.Add(new Row { Item = name, Type = type, Produced = p, Consumed = c });
        }

        Comparison<Row> cmp = sortBy switch {
            "production" => (a, b) => b.Produced.CompareTo(a.Produced),
            "consumption" => (a, b) => b.Consumed.CompareTo(a.Consumed),
            "net" => (a, b) => (b.Produced - b.Consumed).CompareTo(a.Produced - a.Consumed),
            _ => (a, b) => (b.Consumed - b.Produced).CompareTo(a.Consumed - a.Produced)
        };
        rows.Sort((a, b) => {
            int r = cmp(a, b);
            return r != 0 ? r : string.CompareOrdinal(a.Item, b.Item);
        });

        (List<Row> page, bool truncated, int total) = ToolResponse.Cap(rows, limit, opts.Value.MaxItems);

        int windowSeconds = FactoryTools.WindowSeconds(window);
        List<object> outp = new List<object>();
        foreach(Row r in page) {
            TrendResult t = await trend.ComputeAsync(view.SaveId, "production.produced",
                $"surface={surf},item={r.Item}", windowSeconds, ct);
            outp.Add(new {
                item = r.Item,
                type = r.Type,
                produced_per_min = ToolResponse.R(r.Produced, 1),
                consumed_per_min = ToolResponse.R(r.Consumed, 1),
                net_per_min = ToolResponse.R(r.Produced - r.Consumed, 1),
                ratio = r.Consumed > 0 ? ToolResponse.R(r.Produced / r.Consumed) : (double?)null,
                trend = t.Direction,
                trend_samples = t.Samples
            });
        }

        return ToolResponse.Ok(new {
            surface = surf,
            window,
            data_age_seconds = prod.AgeSeconds,
            sort_by = sortBy,
            total_available = total,
            truncated,
            items = outp
        });
    }

    private sealed class Row {
        public string Item = "";
        public string Type = "item";
        public double Produced;
        public double Consumed;
    }
}
