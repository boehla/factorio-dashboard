using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Karte und Umwelt: Erzfelder und die Lage mit den Beissern.
/// </summary>
[McpServerToolType]
public static class WorldTools {

    [McpServerTool(Name = "get_resource_patches")]
    [Description("Erzfelder einzeln statt als Summe: Zentrum, Restmenge, Erschoepfungsgrad, Bohrer "
        + "darauf und die Foerderrate. Beantwortet 'wann brauche ich eine neue Aussenbasis' — eine "
        + "Gesamtmenge sagt nichts, wenn 90 Prozent davon in einem Feld ohne Bohrer liegen.")]
    public static string GetResourcePatches(
            SnapshotView view, IOptions<McpOptions> opts,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Nur diese Ressourcen, kommagetrennt")] string? resourceFilter = null,
            [Description("Nur Felder ohne Bohrer (Ausbaukandidaten)")] bool onlyUntapped = false,
            [Description("Maximale Anzahl Felder, Default 20")] int limit = 20) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? res = view.Get("resources", surf);
        if(res == null) return ToolResponse.NoData("resources", surf);

        HashSet<string>? filter = null;
        if(!string.IsNullOrWhiteSpace(resourceFilter)) {
            filter = new HashSet<string>(
                resourceFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
        }

        // Sortiert wird nach Dringlichkeit, nicht nach Menge: die groessten
        // Vorkommen einer Py-Karte sind unendliche Felsbrocken ohne Bohrer, und
        // die beantworten die Frage "wo geht mir zuerst das Erz aus" nicht.
        // Erst die Felder, die abgebaut werden und bald leer sind, dann der Rest.
        List<(object Row, double Amount, double Eta)> rows = new List<(object, double, double)>();
        int patchesTotal = 0;
        bool anyPatchData = false;

        foreach(JsonProperty r in Json.Object(res.Data, "resources")) {
            if(filter != null && !filter.Contains(r.Name)) continue;
            bool infinite = Json.Bool(r.Value, "infinite");
            double rateNow = Json.Num(r.Value, "rate_current");
            double totalAmount = Json.Num(r.Value, "total_amount");
            int shown = 0;

            foreach(JsonElement p in Json.Array(r.Value, "patches")) {
                anyPatchData = true;
                patchesTotal++;
                shown++;
                int drills = Json.Int(p, "drills");
                if(onlyUntapped && drills > 0) continue;

                double amount = Json.Num(p, "amount");
                // Die Foerderrate kennt der Mod nur je Ressource, nicht je Feld.
                // Aufgeteilt wird deshalb nach Bohreranteil — eine Schaetzung,
                // aber eine, die mit der Wirklichkeit korreliert.
                int drillsAll = Json.Int(Json.Sub(r.Value, "drills"), "total");
                double share = drillsAll > 0 ? drills / (double)drillsAll : 0;
                double rate = rateNow * share;
                double eta = (!infinite && rate > 0) ? amount / rate / 60 : double.PositiveInfinity;

                rows.Add((new {
                    resource = r.Name,
                    x = Json.Int(p, "x"),
                    y = Json.Int(p, "y"),
                    amount = ToolResponse.R(amount, 0),
                    chunks = Json.Int(p, "chunks"),
                    infinite = infinite ? true : (bool?)null,
                    depletion_pct = Json.NumOrNull(p, "depletion_pct") is double dp
                        ? ToolResponse.R(dp, 3) : (double?)null,
                    drills,
                    drills_working = Json.Int(p, "drills_working"),
                    extraction_per_min = ToolResponse.R(rate, 0),
                    eta_depletion_hours = double.IsFinite(eta) ? ToolResponse.R(eta, 1) : (double?)null
                }, amount, eta));
            }

            // Aeltere Mod-Versionen liefern keine Felder — dann bleibt die Summe.
            if(shown == 0 && !onlyUntapped && totalAmount > 0) {
                rows.Add((new {
                    resource = r.Name,
                    x = (int?)null, y = (int?)null,
                    amount = ToolResponse.R(totalAmount, 0),
                    chunks = (int?)null,
                    infinite = infinite ? true : (bool?)null,
                    depletion_pct = (double?)null,
                    drills = Json.Int(Json.Sub(r.Value, "drills"), "total"),
                    drills_working = Json.Int(Json.Sub(r.Value, "drills"), "working"),
                    extraction_per_min = ToolResponse.R(rateNow, 0),
                    eta_depletion_hours = Json.NumOrNull(r.Value, "depletion_seconds") is double ds
                        ? ToolResponse.R(ds / 3600, 1) : (double?)null
                }, totalAmount,
                Json.NumOrNull(r.Value, "depletion_seconds") is double d2 ? d2 / 3600 : double.PositiveInfinity));
            }
        }

        rows.Sort((a, b) => {
            bool ae = double.IsFinite(a.Eta), be = double.IsFinite(b.Eta);
            if(ae != be) return ae ? -1 : 1;          // mit Erschoepfungsdatum zuerst
            if(ae) return a.Eta.CompareTo(b.Eta);     // das naechste zuerst
            return b.Amount.CompareTo(a.Amount);      // sonst die groessten
        });
        (List<(object Row, double Amount, double Eta)> page, bool truncated, int total) =
            ToolResponse.Cap(rows, limit, opts.Value.MaxItems);

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = res.AgeSeconds,
            scanned_chunks = Json.Int(res.Data, "scanned_chunks"),
            patch_data = anyPatchData,
            patches_total = patchesTotal,
            total_available = total,
            truncated,
            patches = page.Select(r => r.Row).ToList()
        });
    }

    [McpServerTool(Name = "get_pollution_and_threat")]
    [Description("Evolution der Beisser samt Aufschluesselung (Zeit, Verschmutzung, zerstoerte "
        + "Nester), die Pollution-Bilanz aus Erzeugung gegen Absorption, und die Zahl zerstoerter "
        + "Gebaeude. Nesterzaehlung nur, wenn die Mod-Einstellung fdash-threat-scan an ist.")]
    public static string GetPollutionAndThreat(
            SnapshotView view, IOptions<McpOptions> opts,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? th = view.Get("threat", surf);
        JobPayload? al = view.Get("alerts");
        if(th == null) return ToolResponse.NoData("threat", surf);

        JsonElement evo = Json.Sub(th.Data, "evolution");
        JsonElement pol = Json.Sub(th.Data, "pollution");

        Dictionary<string, double> bySource = Json.NumMap(pol, "by_source");
        List<KeyValuePair<string, double>> sources = bySource.ToList();
        sources.Sort((a, b) => b.Value.CompareTo(a.Value));

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = th.AgeSeconds,
            evolution = evo.ValueKind == JsonValueKind.Object ? new {
                factor = ToolResponse.R(Json.Num(evo, "factor"), 4),
                by_time = Json.NumOrNull(evo, "by_time") is double bt ? ToolResponse.R(bt, 4) : (double?)null,
                by_pollution = Json.NumOrNull(evo, "by_pollution") is double bp ? ToolResponse.R(bp, 4) : (double?)null,
                by_killing_spawners = Json.NumOrNull(evo, "by_killing_spawners") is double bk
                    ? ToolResponse.R(bk, 4) : (double?)null
            } : null,
            pollution = pol.ValueKind == JsonValueKind.Object ? new {
                // Ohne dieses Flag liest sich eine abgeschaltete Pollution wie
                // eine kaputte Messung: alles 0.
                enabled = Json.Bool(pol, "enabled"),
                produced_per_min = ToolResponse.R(Json.Num(pol, "produced_per_min"), 1),
                absorbed_per_min = ToolResponse.R(Json.Num(pol, "absorbed_per_min"), 1),
                net_per_min = ToolResponse.R(Json.Num(pol, "net_per_min"), 1),
                on_map = ToolResponse.R(Json.Num(pol, "on_map"), 0),
                top_sources = sources.Take(6).ToDictionary(s => s.Key, s => ToolResponse.R(s.Value, 1))
            } : null,
            nests = Json.Bool(th.Data, "nest_scan") ? Json.Int(th.Data, "nests") : (int?)null,
            worms = Json.Bool(th.Data, "nest_scan") ? Json.Int(th.Data, "worms") : (int?)null,
            nest_scan = Json.Bool(th.Data, "nest_scan"),
            note = Json.Bool(th.Data, "nest_scan") ? null
                : "Nester werden nicht gezaehlt — das hiesse die ganze Karte abgehen. "
                  + "Einschalten ueber die Mod-Einstellung fdash-threat-scan.",
            buildings_destroyed = al != null ? Json.Int(al.Data, "destroyed_total") : (int?)null,
            destroyed_by_name = al != null
                ? Json.Array(al.Data, "destroyed_by_name")
                    .Select(d => new { name = Json.Str(d, "name"), count = Json.Int(d, "count") }).ToList()
                : null
        });
    }
}
