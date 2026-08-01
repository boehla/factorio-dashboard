using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Fdash.Collector;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Einstieg und Gesamtueberblick. Diese Tools beantworten "wie geht es der
/// Fabrik" und "kommen ueberhaupt Daten an" — alles Weitere geht danach in die
/// Tiefe.
/// </summary>
[McpServerToolType]
public static class FactoryTools {

    [McpServerTool(Name = "get_health")]
    [Description("Zustand der Datenerfassung: Transportweg zum Spiel, ob der Mod meldet, ob die "
        + "Prototypen geladen sind, und wie alt die Daten jedes Jobs sind. Erster Aufruf, wenn "
        + "andere Tools leere oder seltsame Werte liefern.")]
    public static async Task<string> GetHealth(SnapshotView view, GameLink link, PrototypeExporter proto,
            CancellationToken ct) {
        string? modStatus = null;
        try { modStatus = await link.ModStatusAsync(ct); } catch { /* RCON ist optional */ }

        List<object> jobs = new List<object>();
        foreach(JobPayload p in view.All()) {
            jobs.Add(new {
                job = p.Job, surface = p.Surface, age_seconds = p.AgeSeconds,
                fields = p.Data.ValueKind == JsonValueKind.Object ? p.Data.EnumerateObject().Count() : 0
            });
        }

        return ToolResponse.Ok(new {
            transport = link.Description,
            can_write = link.Available,
            prototypes_loaded = proto.Loaded,
            recipes_known = proto.Recipes.Count,
            save_id = view.SaveId,
            surfaces = view.Surfaces,
            primary_surface = view.PrimarySurface,
            mod_status_raw = modStatus,
            jobs
        });
    }

    [McpServerTool(Name = "get_base_snapshot")]
    [Description("Verdichteter Gesamtueberblick als Einstieg: Strom, Forschung, Maschinenzustand, "
        + "die groessten Engpaesse, Zuege, Roboter und die Items mit dem groessten Defizit "
        + "(Verbrauch > Produktion). Alle Raten sind pro Minute, Leistungen in MW.")]
    public static async Task<string> GetBaseSnapshot(
            SnapshotView view, TrendCalculator trend, Microsoft.Extensions.Options.IOptions<McpOptions> opts,
            [Description("Oberflaeche, z. B. nauvis. Leer = Hauptoberflaeche.")] string? surface = null,
            [Description("Trendfenster: five_seconds|one_minute|ten_minutes|one_hour|ten_hours")]
            string window = "ten_minutes",
            [Description("Detailgrad: brief|normal|full")] string detail = "normal",
            CancellationToken ct = default) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? meta = view.Get("meta");
        if(meta == null) return ToolResponse.NoData("meta");

        bool full = detail == "full";
        bool brief = detail == "brief";

        long tick = (long)Json.Num(meta.Data, "tick");
        object? powerBlock = power(view, surf);
        object? researchBlock = research(view, surf);
        object machineBlock = machines(view, surf, out List<object> bottlenecks);

        List<object> deficits = new List<object>();
        if(!brief) {
            deficits = await deficitList(view, trend, surf, window, full ? 10 : 5, ct);
        }

        JobPayload? problems = view.Get("problems");
        JobPayload? trains = view.Get("trains_derived") ?? view.Get("trains");

        return ToolResponse.Ok(new {
            save_id = view.SaveId,
            surface = surf,
            tick,
            playtime_hours = ToolResponse.R(tick / 60.0 / 3600.0, 1),
            window,
            data_age_seconds = meta.AgeSeconds,
            exporter_scanning = Json.Bool(Json.Sub(meta.Data, "exporter"), "scanning"),
            power = powerBlock,
            research = researchBlock,
            machines = machineBlock,
            top_bottlenecks = brief ? new List<object>() : bottlenecks.Take(full ? 10 : 5).ToList(),
            problem_counts = problems != null ? Json.Sub(problems.Data, "counts") : (object?)null,
            trains = trains == null ? null : new {
                total = Json.Int(trains.Data, "total"),
                problem = Json.Int(trains.Data, "problem")
            },
            robots = robots(view, surf),
            deficits
        });
    }

    [McpServerTool(Name = "get_problems")]
    [Description("Alle erkannten Probleme ueber alle Domaenen (Engpaesse, stehende Maschinen, Strom, "
        + "Erzfelder, Zuege, Roboter, Plattformen, Forschung), nach Schwere sortiert, jeweils mit "
        + "Begruendung und Vorschlag. Die direkte Antwort auf 'wo hakt es' und 'was als Naechstes'.")]
    public static string GetProblems(
            SnapshotView view, Microsoft.Extensions.Options.IOptions<McpOptions> opts,
            [Description("Nur eine Domaene: shortage|machines|power|resources|trains|logistics|platforms|research")]
            string? domain = null,
            [Description("Mindest-Schweregrad 0..1, Default 0 (alles)")] double minSeverity = 0,
            [Description("Maximale Anzahl, Default 15")] int limit = 15) {
        JobPayload? p = view.Get("problems");
        if(p == null) return ToolResponse.NoData("problems");

        List<object> all = new List<object>();
        foreach(JsonElement e in Json.Array(p.Data, "problems")) {
            string dom = Json.Str(e, "domain");
            double sev = Json.Num(e, "severity");
            if(domain != null && !string.Equals(dom, domain, StringComparison.OrdinalIgnoreCase)) continue;
            if(sev < minSeverity) continue;
            all.Add(new {
                domain = dom,
                severity = ToolResponse.R(sev),
                title = Json.Str(e, "title"),
                detail = Json.Str(e, "detail"),
                suggestion = Json.Str(e, "suggestion"),
                surface = Json.Str(e, "surface"),
                items = Json.StrList(e, "items").Take(8).ToList()
            });
        }

        (List<object> items, bool truncated, int total) = ToolResponse.Cap(all, limit, opts.Value.MaxItems);
        return ToolResponse.Ok(new {
            data_age_seconds = p.AgeSeconds,
            counts = Json.Sub(p.Data, "counts"),
            total_available = total,
            truncated,
            problems = items
        });
    }

    [McpServerTool(Name = "get_snapshot")]
    [Description("Rohes Payload eines Jobs, unveraendert wie der Mod es liefert. Fluchtweg fuer "
        + "alles, wofuer es kein eigenes Tool gibt. Bekannte Jobs liefert get_health.")]
    public static string GetSnapshot(
            SnapshotView view,
            [Description("Job-Name, z. B. power, production, assemblers, trains, resources, logistics, "
                + "circuits, platforms, research_state, stall, problems")] string job,
            [Description("Oberflaeche fuer Jobs, die pro Oberflaeche laufen")] string? surface = null) {
        JobPayload? p = view.Get(job, surface != null ? view.ResolveSurface(surface) : null);
        if(p == null) return ToolResponse.NoData(job, surface);
        return ToolResponse.Ok(new {
            job = p.Job, surface = p.Surface, data_age_seconds = p.AgeSeconds, payload = p.Data
        });
    }

    // ------------------------------------------------------------------ Bloecke

    private static object? power(SnapshotView view, string surface) {
        JobPayload? p = view.Get("power", surface);
        if(p == null) return null;

        double production = 0, consumption = 0, capacity = 0, energy = 0, accCapacity = 0;
        int nets = 0;
        foreach(JsonElement n in Json.Array(p.Data, "networks")) {
            nets++;
            production += Json.Num(n, "production");
            consumption += Json.Num(n, "consumption");
            capacity += Json.Num(n, "capacity");
            JsonElement acc = Json.Sub(n, "accumulators");
            if(acc.ValueKind == JsonValueKind.Object) {
                energy += Json.Num(acc, "energy");
                accCapacity += Json.Num(acc, "capacity");
            }
        }
        return new {
            networks = nets,
            satisfaction = consumption > 0 ? ToolResponse.R(Math.Min(1, production / consumption)) : 1,
            production_mw = ToolResponse.Mw(production),
            consumption_mw = ToolResponse.Mw(consumption),
            capacity_mw = ToolResponse.Mw(capacity),
            accumulator_charge = accCapacity > 0 ? ToolResponse.R(energy / accCapacity) : (double?)null,
            data_age_seconds = p.AgeSeconds
        };
    }

    private static object? research(SnapshotView view, string surface) {
        JobPayload? rs = view.Get("research_state", surface);
        if(rs == null) return null;
        JobPayload? choice = view.Get("research");
        int candidates = 0;
        foreach(JsonElement _ in Json.Array(rs.Data, "candidates")) candidates++;
        // Ohne Vorschlag steht dort ein leerer String — null ist ehrlicher und
        // spart dem Leser die Frage, ob eine Tech "" heisst.
        string suggested = choice == null ? "" : Json.Str(choice.Data, "tech");
        return new {
            queue_len = Json.Int(rs.Data, "queue_len"),
            active_labs = Json.Int(rs.Data, "active_labs"),
            speed_bonus = ToolResponse.R(Json.Num(rs.Data, "research_speed_bonus")),
            researchable_now = candidates,
            suggestion = suggested.Length > 0 ? suggested : null,
            suggestion_eta_minutes = suggested.Length > 0
                ? ToolResponse.R(Json.Num(choice!.Data, "estimated_seconds") / 60, 1) : (double?)null,
            data_age_seconds = rs.AgeSeconds
        };
    }

    /// <summary>
    /// Maschinen als Status-Summe plus die Gruppen mit den meisten stehenden
    /// Maschinen. Der Status heisst hier so wie in Factorio (no_ingredients,
    /// full_output, …) — jede Umbenennung wuerde die Zuordnung zur Spiel-GUI
    /// kaputtmachen.
    /// </summary>
    private static object machines(SnapshotView view, string surface, out List<object> bottlenecks) {
        bottlenecks = new List<object>();
        JobPayload? asm = view.Get("assemblers", surface);
        if(asm == null) return new { total = 0 };

        Dictionary<string, int> status = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        List<(string Item, int Idle, string Reason, List<string> Missing)> groups =
            new List<(string, int, string, List<string>)>();

        foreach(JsonProperty g in Json.Object(asm.Data, "by_item")) {
            int gt = Json.Int(g.Value, "total");
            total += gt;
            int working = 0, idle = 0;
            string worst = "";
            int worstCount = 0;
            foreach(JsonProperty s in Json.Object(g.Value, "status")) {
                int c = (int)s.Value.GetDouble();
                status[s.Name] = (status.TryGetValue(s.Name, out int prev) ? prev : 0) + c;
                if(s.Name == "working" || s.Name == "normal") { working += c; continue; }
                idle += c;
                if(c > worstCount) { worstCount = c; worst = s.Name; }
            }
            if(idle > 0) {
                List<string> missing = new List<string>();
                foreach(KeyValuePair<string, double> m in Json.NumMap(g.Value, "missing")) {
                    if(m.Value > 0) missing.Add(m.Key);
                }
                groups.Add((g.Name, idle, worst, missing));
            }
        }

        groups.Sort((a, b) => b.Idle.CompareTo(a.Idle));
        foreach((string item, int idle, string reason, List<string> missing) in groups) {
            bottlenecks.Add(new {
                item, idle_machines = idle, reason,
                missing = missing.Take(5).ToList()
            });
        }

        return new {
            total,
            by_status = status,
            no_recipe = Json.Int(asm.Data, "no_recipe"),
            data_age_seconds = asm.AgeSeconds
        };
    }

    private static object? robots(SnapshotView view, string surface) {
        JobPayload? log = view.Get("logistics", surface);
        if(log == null) return null;
        int total = 0, working = 0, waiting = 0, construction = 0;
        foreach(JsonElement n in Json.Array(log.Data, "networks")) {
            JsonElement l = Json.Sub(n, "logistic_robots");
            total += Json.Int(l, "total");
            working += Json.Int(l, "working");
            waiting += Json.Int(l, "waiting_for_charge");
            construction += Json.Int(Json.Sub(n, "construction_robots"), "total");
        }
        return new { logistic = total, working, waiting_for_charge = waiting, construction };
    }

    /// <summary>
    /// Items, bei denen mehr verbraucht als produziert wird — die kuerzeste
    /// Antwort auf "was fehlt der Fabrik". Mit Trend, weil ein Defizit direkt
    /// nach dem Einschalten einer neuen Linie normal ist.
    /// </summary>
    private static async Task<List<object>> deficitList(SnapshotView view, TrendCalculator trend,
            string surface, string window, int count, CancellationToken ct) {
        List<object> result = new List<object>();
        JobPayload? prod = view.Get("production", surface);
        if(prod == null) return result;

        List<(string Item, double P, double C)> items = new List<(string, double, double)>();
        foreach(JsonElement it in Json.Array(prod.Data, "items")) {
            if(!Json.Bool(it, "is_ingredient")) continue;   // Bau-Items interessieren hier nicht
            double p = Json.Num(it, "produced_per_min");
            double c = Json.Num(it, "consumed_per_min");
            if(c - p <= 0) continue;
            items.Add((Json.Str(it, "item"), p, c));
        }
        items.Sort((a, b) => (b.C - b.P).CompareTo(a.C - a.P));

        int windowSeconds = WindowSeconds(window);
        foreach((string item, double p, double c) in items.Take(count)) {
            TrendResult t = await trend.ComputeAsync(view.SaveId, "production.produced",
                $"surface={surface},item={item}", windowSeconds, ct);
            result.Add(new {
                item,
                produced_per_min = ToolResponse.R(p, 1),
                consumed_per_min = ToolResponse.R(c, 1),
                net_per_min = ToolResponse.R(p - c, 1),
                trend = t.Direction
            });
        }
        return result;
    }

    /// <summary>Fenstername der Spezifikation -&gt; Sekunden fuer die Zeitreihe.</summary>
    public static int WindowSeconds(string window) => window switch {
        "five_seconds" => 60,      // kuerzer als eine Minute traegt die Zeitreihe nicht
        "one_minute" => 300,
        "ten_minutes" => 1200,
        "one_hour" => 7200,
        "ten_hours" => 72000,
        _ => 1200
    };
}
