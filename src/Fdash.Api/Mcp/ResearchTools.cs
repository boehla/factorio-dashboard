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

    [McpServerTool(Name = "get_technology")]
    [Description("Eine einzelne Technologie im Detail: erforscht/forschbar/blockiert, Kosten, "
        + "Wissenschaftspakete mit ihrem aktuellen Durchsatz und die freigeschalteten Rezepte — "
        + "vor allem aber der vollstaendige Forschungspfad dorthin: alle noch fehlenden "
        + "Voraussetzungen in der Reihenfolge, in der sie erforscht werden muessen, mit "
        + "Gesamtkosten und Gesamtdauer. Die Antwort auf 'was fehlt mir bis X'.")]
    public static string GetTechnology(
            SnapshotView view, PrototypeExporter proto, TechLedger ledger, IOptions<McpOptions> opts,
            [Description("Name der Technologie, z. B. py-science-pack-2")] string name,
            [Description("Forschungspfad mit ausgeben")] bool includePath = true,
            [Description("Maximale Anzahl Schritte im Pfad, Default 40")] int pathLimit = 40,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null) {
        if(string.IsNullOrWhiteSpace(name)) {
            return ToolResponse.Error("Kein Technologiename angegeben.",
                "Beispiel: name = \"py-science-pack-2\". Forschbare Namen liefert get_research_state.");
        }
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? rs = view.Get("research_state", surf);
        if(rs == null) return ToolResponse.NoData("research_state", surf);
        if(proto.Technologies.Count == 0) {
            return ToolResponse.Error("Es sind keine Technologie-Prototypen geladen.",
                "prototypes.json fehlt oder stammt aus einer Mod-Version vor 0.2.0. Neu exportieren "
                + "mit remote.call('fdash','export_prototypes'). Zustand: get_health.");
        }

        // Die vollstaendige Erforscht-Liste kommt nur gelegentlich mit; hier
        // wird sie mitgenommen, wenn sie da ist, und sonst der gemerkte Stand
        // (oder als letzte Stufe die Herleitung aus den Kandidaten) benutzt.
        ledger.Observe(rs.Data, rs.SaveId);
        TechGraph graph = new TechGraph(proto.Technologies, rs.Data, ledger.Researched(rs.SaveId));

        if(!graph.Knows(name)) {
            List<string> guesses = similar(proto, name);
            return ToolResponse.Error($"Technologie '{name}' ist unbekannt.",
                guesses.Count > 0
                    ? "Gemeint war vielleicht: " + string.Join(", ", guesses)
                    : "Namen sind die internen Prototypnamen, z. B. py-science-pack-2 statt 'pY Science 2'.");
        }

        TechProto target = proto.Technologies[name];
        Dictionary<string, (double Produced, double Consumed)> flows = productionFlows(view, surf);
        double rate = labRate(rs.Data);

        double producedOf(string item) =>
            flows.TryGetValue(item, out (double Produced, double Consumed) f) ? ToolResponse.R(f.Produced, 1) : 0;
        double secondsFor(TechProto t) => rate > 0 ? t.UnitCount * t.UnitEnergy / rate : double.PositiveInfinity;

        // ------------------------------------------------------------- Pfad
        // Der Pfad wird immer gerechnet — die Summen und die fehlenden Pakete
        // sind die eigentliche Antwort. includePath entscheidet nur, ob auch
        // die einzelnen Schritte mit ausgegeben werden.
        List<string> path = graph.ResearchPath(name);

        // Summen ueber den ganzen Pfad, nicht nur ueber die ausgegebene Seite —
        // sonst stimmt die Gesamtdauer nicht mehr, sobald gekuerzt wird.
        double totalUnits = 0;
        double totalSeconds = 0;
        HashSet<string> packsMissing = new HashSet<string>(StringComparer.Ordinal);
        foreach(string step in path) {
            if(!proto.Technologies.TryGetValue(step, out TechProto? t)) continue;
            totalUnits += t.UnitCount;
            double s = secondsFor(t);
            if(double.IsFinite(s)) totalSeconds += s;
            foreach(RecipeIo p in t.Packs) {
                if(producedOf(p.Name) <= 0) packsMissing.Add(p.Name);
            }
        }

        List<string> page = new List<string>();
        bool truncated = false;
        if(includePath) {
            (page, truncated, _) = ToolResponse.Cap(path, pathLimit, opts.Value.MaxItems);
        }

        List<object> steps = new List<object>();
        double cumulative = 0;
        int index = 0;
        foreach(string step in page) {
            index++;
            if(!proto.Technologies.TryGetValue(step, out TechProto? t)) continue;
            double s = secondsFor(t);
            if(double.IsFinite(s)) cumulative += s;
            steps.Add(new {
                step = index,
                name = step,
                status = statusName(graph.StatusOf(step)),
                cost_units = t.UnitCount,
                est_minutes = double.IsFinite(s) ? ToolResponse.R(s / 60, 1) : (double?)null,
                cumulative_minutes = ToolResponse.R(cumulative / 60, 1),
                packs = t.Packs.Select(p => new { name = p.Name, count = p.Amount }).ToList(),
                unlocks_recipes = t.UnlockedRecipes.Take(6).ToList()
            });
        }

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = rs.AgeSeconds,
            name,
            status = statusName(graph.StatusOf(name)),
            // Woher der Erforscht-Stand stammt: "reported" ist die Liste des
            // Mods, "derived" die Herleitung aus den Kandidaten. Letztere haelt
            // per Skript abgeschaltete Technologien faelschlich fuer erforscht.
            researched_source = graph.Source,
            cost_units = target.UnitCount,
            seconds_per_unit = ToolResponse.R(target.UnitEnergy),
            max_level = target.MaxLevel,
            upgrade = target.Upgrade,
            packs = target.Packs.Select(p => new {
                name = p.Name,
                count = p.Amount,
                produced_per_min = producedOf(p.Name)
            }).ToList(),
            unlocks_recipes = target.UnlockedRecipes.Take(12).ToList(),
            direct_prerequisites = target.Prerequisites.Select(p => new {
                name = p,
                status = statusName(graph.StatusOf(p))
            }).ToList(),
            missing_prerequisites = graph.MissingPrerequisites(name),
            eta_assumes_all_labs = Json.Int(rs.Data, "active_labs") == 0 && Json.Int(rs.Data, "total_labs") > 0,
            path_steps = path.Count,
            path_total_units = ToolResponse.R(totalUnits, 0),
            path_total_minutes = rate > 0 ? ToolResponse.R(totalSeconds / 60, 1) : (double?)null,
            // Pakete, die auf dem Weg gebraucht werden und gerade gar nicht
            // laufen — ohne die faengt der Pfad nicht einmal an.
            packs_not_produced = packsMissing.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            truncated,
            research_path = steps
        });
    }

    private static string statusName(TechStatus s) => s switch {
        TechStatus.Researched => "researched",
        TechStatus.Available => "available",
        TechStatus.Blocked => "blocked",
        _ => "unknown"
    };

    /// <summary>Namensvorschlaege bei einem Tippfehler — Prototypnamen sind lang und kryptisch.</summary>
    private static List<string> similar(PrototypeExporter proto, string query) {
        List<string> hits = new List<string>();
        foreach(string tech in proto.Technologies.Keys) {
            if(tech.Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add(tech);
        }
        hits.Sort(StringComparer.Ordinal);
        return hits.Take(8).ToList();
    }

    [McpServerTool(Name = "suggest_next_research")]
    [Description("Bewertet die forschbaren Technologien und schlaegt welche vor. optimize_for: "
        + "throughput (schnellste), cheapest (guenstigste), unlock_bottleneck (loest einen aktuell "
        + "erkannten Engpass), progression (schaltet die meisten weiteren Technologien frei).")]
    public static string SuggestNextResearch(
            SnapshotView view, PrototypeExporter proto, IOptions<McpOptions> opts,
            [Description("Anzahl Vorschlaege, Default 5")] int horizon = 5,
            [Description("throughput | cheapest | unlock_bottleneck | progression")]
            string optimizeFor = "throughput",
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? rs = view.Get("research_state", surf);
        if(rs == null) return ToolResponse.NoData("research_state", surf);

        Dictionary<string, (double Produced, double Consumed)> flows = productionFlows(view, surf);
        double rate = labRate(rs.Data);

        // Wie viele weitere Technologien direkt an einer haengen. Einmal ueber
        // alle Prototypen, nicht pro Kandidat.
        Dictionary<string, int> dependents = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach(TechProto t in proto.Technologies.Values) {
            foreach(string pre in t.Prerequisites) {
                dependents[pre] = (dependents.TryGetValue(pre, out int n) ? n : 0) + 1;
            }
        }

        // Items, die gerade fehlen — aus der Problemliste, damit "loest einen
        // Engpass" sich auf denselben Befund stuetzt wie get_problems.
        HashSet<string> wanted = new HashSet<string>(StringComparer.Ordinal);
        JobPayload? problems = view.Get("problems");
        if(problems != null) {
            foreach(JsonElement p in Json.Array(problems.Data, "problems")) {
                string domain = Json.Str(p, "domain");
                if(domain != "shortage" && domain != "machines" && domain != "fluids") continue;
                foreach(string i in Json.StrList(p, "items")) wanted.Add(i);
            }
        }

        List<(string Name, double Seconds, double Units, bool Affordable, int Unblocks, int Fixes,
              List<string> Unlocks)> cands = new List<(string, double, double, bool, int, int, List<string>)>();

        foreach(JsonElement c in Json.Array(rs.Data, "candidates")) {
            string name = Json.Str(c, "name");
            bool affordable = true;
            foreach(JsonElement ing in Json.Array(c, "ingredients")) {
                if(!flows.TryGetValue(Json.Str(ing, "name"), out (double P, double C) f) || f.P <= 0) {
                    affordable = false;
                    break;
                }
            }

            double units = Json.Num(c, "unit_count");
            double seconds = rate > 0 ? units * Json.Num(c, "energy") / rate : double.PositiveInfinity;

            List<string> unlocks = proto.Technologies.TryGetValue(name, out TechProto? t)
                ? t.UnlockedRecipes : new List<string>();

            // Zaehlt, wie viele der freigeschalteten Rezepte etwas herstellen,
            // das aktuell fehlt.
            int fixes = 0;
            foreach(string recipe in unlocks) {
                if(!proto.Recipes.TryGetValue(recipe, out RecipeProto? r)) continue;
                foreach(RecipeIo p in r.Products) {
                    if(wanted.Contains(p.Name)) { fixes++; break; }
                }
            }

            cands.Add((name, seconds, units, affordable, dependents.TryGetValue(name, out int dep) ? dep : 0,
                fixes, unlocks));
        }

        // Nicht bezahlbare Technologien immer nach hinten: eine Empfehlung, fuer
        // die die Pakete fehlen, ist keine.
        Comparison<(string Name, double Seconds, double Units, bool Affordable, int Unblocks, int Fixes,
                    List<string> Unlocks)> cmp = optimizeFor switch {
            "cheapest" => (a, b) => a.Units.CompareTo(b.Units),
            "unlock_bottleneck" => (a, b) => b.Fixes.CompareTo(a.Fixes) != 0
                ? b.Fixes.CompareTo(a.Fixes) : a.Seconds.CompareTo(b.Seconds),
            "progression" => (a, b) => b.Unblocks.CompareTo(a.Unblocks) != 0
                ? b.Unblocks.CompareTo(a.Unblocks) : a.Seconds.CompareTo(b.Seconds),
            _ => (a, b) => a.Seconds.CompareTo(b.Seconds)
        };
        cands.Sort((a, b) => {
            int c = b.Affordable.CompareTo(a.Affordable);
            if(c != 0) return c;
            c = cmp(a, b);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        (List<(string Name, double Seconds, double Units, bool Affordable, int Unblocks, int Fixes,
               List<string> Unlocks)> page, bool truncated, int total) =
            ToolResponse.Cap(cands, horizon, opts.Value.MaxItems);

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = rs.AgeSeconds,
            optimize_for = optimizeFor,
            queue_len = Json.Int(rs.Data, "queue_len"),
            eta_assumes_all_labs = Json.Int(rs.Data, "active_labs") == 0 && Json.Int(rs.Data, "total_labs") > 0,
            total_available = total,
            truncated,
            suggestions = page.Select(c => new {
                name = c.Name,
                cost_units = c.Units,
                est_minutes = double.IsFinite(c.Seconds) ? ToolResponse.R(c.Seconds / 60, 1) : (double?)null,
                all_packs_available = c.Affordable,
                unlocks_recipes = c.Unlocks.Take(6).ToList(),
                unblocks_technologies = c.Unblocks,
                fixes_current_shortage = c.Fixes > 0 ? c.Fixes : (int?)null,
                why = why(optimizeFor, c.Affordable, c.Fixes, c.Unblocks)
            }).ToList()
        });
    }

    private static string why(string mode, bool affordable, int fixes, int unblocks) {
        if(!affordable) return "Pakete werden gerade nicht produziert — erst die Wissenschaft aufbauen";
        return mode switch {
            "cheapest" => "guenstigste verfuegbare Technologie",
            "unlock_bottleneck" => fixes > 0
                ? $"schaltet {fixes} Rezept(e) frei, die einen aktuell erkannten Engpass betreffen"
                : "loest keinen der erkannten Engpaesse — nur nach Dauer sortiert",
            "progression" => $"schaltet {unblocks} weitere Technologie(n) frei",
            _ => "schnellste verfuegbare Technologie"
        };
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
