using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Fdash.Collector;
using Fdash.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Forschung: was laeuft, was ginge, was fehlt dafuer.
///
/// Die interessanteste Zahl ist <c>limiting</c> beim Wissenschaftspaket mit dem
/// geringsten Durchsatz — das ist die direkte Antwort auf "warum ist meine SPM
/// so niedrig", und sie steht nirgends im Spiel.
/// </summary>
[McpServerToolType]
public static class ResearchTools {

    [McpServerTool(Name = "get_research_state")]
    [Description("Laufende Forschung mit Fortschritt und Restzeit, die Warteschlange, der Durchsatz "
        + "jedes benoetigten Wissenschaftspakets (inklusive dem limitierenden), sowie die aktuell "
        + "forschbaren Technologien mit Kosten, geschaetzter Dauer und den Rezepten, die sie freischalten.")]
    public static string GetResearchState(
            SnapshotView view, PrototypeExporter proto, IOptions<McpOptions> opts,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Forschbare Technologien mit ausgeben")] bool includeAvailable = true,
            [Description("Maximale Anzahl forschbarer Technologien, Default 15")] int availableLimit = 15,
            [Description("Nur Technologien, deren Pakete aktuell alle produziert werden")]
            bool filterAffordable = false) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? rs = view.Get("research_state", surf);
        if(rs == null) return ToolResponse.NoData("research_state", surf);

        Dictionary<string, (double Produced, double Consumed)> flows = productionFlows(view, surf);

        // ------------------------------------------------------- laufende Forschung
        JsonElement cur = Json.Sub(rs.Data, "current");
        object? current = null;
        List<object> throughput = new List<object>();
        if(cur.ValueKind == JsonValueKind.Object) {
            List<(string Name, double Amount)> packs = new List<(string, double)>();
            foreach(JsonElement p in Json.Array(cur, "packs")) {
                packs.Add((Json.Str(p, "name"), Json.Num(p, "amount", 1)));
            }
            throughput = packThroughput(packs, flows, out double spm);
            double energy = Json.Num(cur, "energy");
            current = new {
                name = Json.Str(cur, "name"),
                level = Json.Int(cur, "level"),
                progress = ToolResponse.R(Json.Num(cur, "progress"), 3),
                units_done = Json.Int(cur, "units_done"),
                units_total = Json.Int(cur, "units_total"),
                seconds_per_unit = ToolResponse.R(energy),
                eta_minutes = Json.NumOrNull(cur, "eta_seconds") is double e
                    ? ToolResponse.R(e / 60, 1) : (double?)null,
                spm_actual = ToolResponse.R(spm, 1),
                packs = packs.Select(p => new { name = p.Name, count = p.Amount }).ToList()
            };
        }

        // -------------------------------------------------------------- Kandidaten
        List<object> available = new List<object>();
        int candidateCount = 0;
        bool truncated = false;
        if(includeAvailable) {
            double rate = labRate(rs.Data);
            List<(JsonElement C, double Seconds, bool Affordable)> cands =
                new List<(JsonElement, double, bool)>();
            foreach(JsonElement c in Json.Array(rs.Data, "candidates")) {
                candidateCount++;
                bool affordable = true;
                foreach(JsonElement ing in Json.Array(c, "ingredients")) {
                    string pack = Json.Str(ing, "name");
                    if(!flows.TryGetValue(pack, out (double Produced, double Consumed) f) || f.Produced <= 0) {
                        affordable = false;
                        break;
                    }
                }
                if(filterAffordable && !affordable) continue;
                double seconds = rate > 0
                    ? Json.Num(c, "unit_count") * Json.Num(c, "energy") / rate : double.PositiveInfinity;
                cands.Add((c, seconds, affordable));
            }
            cands.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));

            (List<(JsonElement C, double Seconds, bool Affordable)> page, bool tr, int _) =
                ToolResponse.Cap(cands, availableLimit, opts.Value.MaxItems);
            truncated = tr;
            foreach((JsonElement c, double seconds, bool affordable) in page) {
                string name = Json.Str(c, "name");
                available.Add(new {
                    name,
                    level = Json.Int(c, "level"),
                    cost_units = Json.Num(c, "unit_count"),
                    est_minutes = double.IsFinite(seconds) ? ToolResponse.R(seconds / 60, 1) : (double?)null,
                    all_packs_available = affordable,
                    packs = Json.Array(c, "ingredients")
                        .Select(i => new { name = Json.Str(i, "name"), count = Json.Num(i, "amount") }).ToList(),
                    unlocks_recipes = unlocks(proto, name)
                });
            }
        }

        // ------------------------------------------------------------- blockiert
        List<object> blocked = new List<object>();
        foreach(JsonElement b in Json.Array(rs.Data, "blocked")) {
            blocked.Add(new {
                name = Json.Str(b, "name"),
                missing_prerequisites = Json.StrList(b, "missing_prerequisites")
            });
        }

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = rs.AgeSeconds,
            current,
            queue = Json.StrList(rs.Data, "queue"),
            queue_len = Json.Int(rs.Data, "queue_len"),
            active_labs = Json.Int(rs.Data, "active_labs"),
            total_labs = Json.Int(rs.Data, "total_labs"),
            lab_speed = ToolResponse.R(Json.Num(rs.Data, "lab_speed")),
            speed_bonus = ToolResponse.R(Json.Num(rs.Data, "research_speed_bonus")),
            researched_count = Json.Int(rs.Data, "researched_count"),
            total_count = Json.Int(rs.Data, "total_count"),
            science_throughput = throughput,
            researchable_now = candidateCount,
            // Ohne laufende Forschung stehen die Labore still; die Schaetzung
            // rechnet dann mit allen vorhandenen statt mit null.
            eta_assumes_all_labs = Json.Int(rs.Data, "active_labs") == 0 && Json.Int(rs.Data, "total_labs") > 0,
            truncated,
            available,
            // Nur die, denen genau eine Voraussetzung fehlt — alles andere waere
            // auf einem grossen Modpack eine Liste mit tausenden Eintraegen.
            blocked_count = Json.Int(rs.Data, "blocked_count"),
            blocked_one_step_away = blocked
        });
    }

    /// <summary>
    /// Durchsatz je Paket und wer bremst. Verglichen wird produziert gegen
    /// verbraucht pro benoetigter Menge: ein Rezept mit 2 Paketen der Sorte A
    /// braucht doppelt so viel davon.
    /// </summary>
    private static List<object> packThroughput(List<(string Name, double Amount)> packs,
            Dictionary<string, (double Produced, double Consumed)> flows, out double spm) {
        List<object> result = new List<object>();
        spm = double.MaxValue;
        string limiting = "";
        double worst = double.MaxValue;

        foreach((string name, double amount) in packs) {
            flows.TryGetValue(name, out (double Produced, double Consumed) f);
            double per = amount > 0 ? f.Produced / amount : f.Produced;
            if(per < worst) { worst = per; limiting = name; }
            if(f.Consumed > 0 && f.Consumed / Math.Max(1, amount) < spm) spm = f.Consumed / Math.Max(1, amount);
        }
        if(spm == double.MaxValue) spm = 0;

        foreach((string name, double amount) in packs) {
            flows.TryGetValue(name, out (double Produced, double Consumed) f);
            result.Add(new {
                pack = name,
                count_per_unit = amount,
                produced_per_min = ToolResponse.R(f.Produced, 1),
                consumed_per_min = ToolResponse.R(f.Consumed, 1),
                limiting = name == limiting && packs.Count > 1
            });
        }
        return result;
    }

    private static Dictionary<string, (double, double)> productionFlows(SnapshotView view, string surface) {
        Dictionary<string, (double, double)> flows = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        JobPayload? prod = view.Get("production", surface);
        if(prod == null) return flows;
        foreach(JsonElement it in Json.Array(prod.Data, "items")) {
            flows[Json.Str(it, "item")] = (Json.Num(it, "produced_per_min"), Json.Num(it, "consumed_per_min"));
        }
        return flows;
    }

    /// <summary>
    /// Units pro Sekunde. Steht gerade keine Forschung an, arbeitet auch kein
    /// Labor — dann waere die Rate 0 und jede Schaetzung unendlich. Genau in dem
    /// Moment ist die Frage aber "wie lange daeuerte das denn", also wird mit
    /// allen vorhandenen Laboren gerechnet und das im Ergebnis vermerkt.
    /// </summary>
    private static double labRate(JsonElement rs) {
        int labs = Json.Int(rs, "active_labs");
        if(labs == 0) labs = Json.Int(rs, "total_labs");
        return labs * Math.Max(0.0001, Json.Num(rs, "lab_speed", 1))
            * (1 + Json.Num(rs, "research_speed_bonus"));
    }

    /// <summary>Freigeschaltete Rezepte, gedeckelt — manche Techs schalten dutzende frei.</summary>
    private static List<string> unlocks(PrototypeExporter proto, string tech) {
        if(!proto.Technologies.TryGetValue(tech, out TechProto? t)) return new List<string>();
        return t.UnlockedRecipes.Take(8).ToList();
    }
}
