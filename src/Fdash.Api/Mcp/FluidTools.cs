using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Fluide: Tankfuellstand, Richtung und Bilanz.
///
/// Auf Pyanodons ist das die halbe Diagnose. Die Produktionsstatistik allein
/// reicht nicht: sie zeigt, dass ein Gas entsteht und verschwindet, aber nicht,
/// ob es in vollen Tanks steht (Abnehmer fehlt) oder ob die Tanks leer sind
/// (Erzeuger fehlt).
/// </summary>
[McpServerToolType]
public static class FluidTools {

    [McpServerTool(Name = "get_fluid_report")]
    [Description("Fluide mit Tankfuellstand, Trend, Puffer-Einordnung (empty/draining/healthy/"
        + "filling/full) und der Suchrichtung, dazu Produktion und Verbrauch pro Minute. "
        + "no_consumer markiert Fluide, die in Menge entstehen, aber kaum verbraucht werden — "
        + "sie fuellen den Puffer oder werden abgefackelt.")]
    public static async Task<string> GetFluidReport(
            SnapshotView view, TrendCalculator trend, IOptions<McpOptions> opts,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Nur diese Fluide, kommagetrennt")] string? fluids = null,
            [Description("Nur auffaellige (volle, leere, verworfene)")] bool onlyProblems = false,
            [Description("Maximale Anzahl, Default 25")] int limit = 25,
            CancellationToken ct = default) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? fl = view.Get("fluids", surf);
        JobPayload? prod = view.Get("production", surf);

        // Ohne Tankdaten bleibt die Bilanz aus der Produktionsstatistik — besser
        // als eine Fehlermeldung, aber der Nutzer soll wissen, was fehlt.
        Dictionary<string, (double P, double C)> flows = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        if(prod != null) {
            foreach(JsonElement it in Json.Array(prod.Data, "items")) {
                if(Json.Str(it, "type") != "fluid") continue;
                flows[Json.Str(it, "item")] = (Json.Num(it, "produced_per_min"), Json.Num(it, "consumed_per_min"));
            }
        }
        if(fl == null && flows.Count == 0) return ToolResponse.NoData("fluids", surf);

        HashSet<string>? filter = null;
        if(!string.IsNullOrWhiteSpace(fluids)) {
            filter = new HashSet<string>(
                fluids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
        }

        // Alle Namen aus beiden Quellen — ein Fluid ohne Tank ist genauso
        // interessant wie ein Tank ohne Durchsatz.
        SortedSet<string> names = new SortedSet<string>(StringComparer.Ordinal);
        if(fl != null) foreach(JsonProperty p in Json.Object(fl.Data, "fluids")) names.Add(p.Name);
        foreach(string n in flows.Keys) names.Add(n);

        List<(object Row, double Weight, bool Odd)> rows = new List<(object, double, bool)>();
        foreach(string name in names) {
            if(filter != null && !filter.Contains(name)) continue;

            JsonElement tank = fl != null ? Json.Sub(Json.Sub(fl.Data, "fluids"), name) : default;
            bool hasTank = tank.ValueKind == JsonValueKind.Object;
            double fill = hasTank ? Json.Num(tank, "fill") : 0;
            double amount = hasTank ? Json.Num(tank, "amount") : 0;

            string dir = "unknown";
            if(hasTank) {
                TrendResult t = await trend.ComputeAsync(view.SaveId, "fluid.amount",
                    $"surface={surf},fluid={name}", 1200, ct);
                dir = t.Direction;
            }

            flows.TryGetValue(name, out (double P, double C) f);
            BufferState state = hasTank ? BufferClassifier.Classify(fill, dir) : BufferState.Healthy;

            // Entsteht in Menge, wird aber kaum verbraucht. Bewusst NICHT
            // "voided" genannt: ob das Gas abgefackelt wird oder nur den Puffer
            // fuellt, laesst sich von hier aus nicht unterscheiden — und der
            // Unterschied zwischen "wird entsorgt" und "hat keinen Abnehmer"
            // ist fuer den Leser wesentlich.
            bool noConsumer = f.P > 60 && f.C < f.P * 0.2;
            bool odd = noConsumer || (hasTank && (state == BufferState.Full || state == BufferState.Empty));

            if(onlyProblems && !odd) continue;

            rows.Add((new {
                fluid = name,
                stored = hasTank ? ToolResponse.R(amount, 0) : (double?)null,
                fill = hasTank ? ToolResponse.R(fill, 3) : (double?)null,
                tanks = hasTank ? Json.Int(tank, "tanks") : (int?)null,
                temperature = hasTank ? Json.NumOrNull(tank, "temperature") is double tp
                    ? ToolResponse.R(tp, 1) : (double?)null : null,
                trend = hasTank ? dir : null,
                buffer = hasTank ? BufferClassifier.Name(state) : null,
                look = hasTank ? BufferClassifier.Direction(state) : null,
                produced_per_min = ToolResponse.R(f.P, 1),
                consumed_per_min = ToolResponse.R(f.C, 1),
                net_per_min = ToolResponse.R(f.P - f.C, 1),
                no_consumer = noConsumer ? true : (bool?)null
            }, Math.Max(f.P, amount), odd));
        }

        // Auffaellige zuerst, danach nach Menge — sonst steht ganz oben das
        // Fluid mit dem alphabetisch ersten Namen.
        rows.Sort((a, b) => {
            int c = b.Odd.CompareTo(a.Odd);
            return c != 0 ? c : b.Weight.CompareTo(a.Weight);
        });

        (List<(object Row, double Weight, bool Odd)> page, bool truncated, int total) =
            ToolResponse.Cap(rows, limit, opts.Value.MaxItems);

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = fl?.AgeSeconds,
            tanks_total = fl != null ? Json.Int(fl.Data, "tanks_total") : (int?)null,
            tanks_empty = fl != null ? Json.Int(fl.Data, "tanks_empty") : (int?)null,
            tank_data = fl != null,
            note = fl == null
                ? "Keine Tankdaten — der Job 'fluids' ist aus (Mod-Einstellung fdash-fluid-scan) "
                  + "oder hat noch keinen Durchlauf beendet. Gezeigt wird nur die Bilanz."
                : null,
            total_available = total,
            truncated,
            fluids = page.Select(r => r.Row).ToList()
        });
    }
}
