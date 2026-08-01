using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Maschinen-Sicht: der eigentliche Flaschenhals-Detektor. Grundlage ist
/// <c>LuaEntity.status</c>, den der Mod je Gruppe als Histogramm liefert.
/// </summary>
[McpServerToolType]
public static class MachineTools {

    /// <summary>
    /// Statuswerte, bei denen eine Maschine auf Zutaten wartet. Absichtlich
    /// dieselbe Liste wie im Mod (util.starving_statuses) — wer hier etwas
    /// aendert, muss dort mitziehen.
    /// </summary>
    private static readonly HashSet<string> InputProblems = new HashSet<string>(StringComparer.Ordinal) {
        "no_ingredients", "item_ingredient_shortage", "fluid_ingredient_shortage",
        "no_input_fluid", "low_input_fluid", "waiting_for_source_items"
    };

    private static readonly HashSet<string> OutputProblems = new HashSet<string>(StringComparer.Ordinal) {
        "full_output", "output_full", "full_burnt_result_output", "full_burned_result_output",
        "waiting_for_space_in_destination"
    };

    /// <summary>Vom Spieler gewollt — keine Fehlermeldung wert.</summary>
    private static readonly HashSet<string> Ignorable = new HashSet<string>(StringComparer.Ordinal) {
        "disabled_by_control_behavior", "disabled_by_script", "marked_for_deconstruction"
    };

    [McpServerTool(Name = "get_machine_status_summary")]
    [Description("Maschinen nach Zustand gruppiert: wie viele laufen, wie viele warten auf Zutaten "
        + "(Problem liegt upstream), wie viele stauen am Ausgang (Problem liegt downstream). Je Gruppe "
        + "die fehlenden Zutaten mit Fehlmenge. Der wichtigste Aufruf zur Engpass-Suche.")]
    public static string GetMachineStatusSummary(
            SnapshotView view, IOptions<McpOptions> opts,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Gruppierung: item (Default, nach hergestelltem Item) | status (nur Summen) "
                + "| recipe (Rezeptnamen je Item-Gruppe)")] string groupBy = "item",
            [Description("Nur Gruppen mit diesem Factorio-Status, z. B. no_ingredients oder full_output")]
            string? statusFilter = null,
            [Description("Gruppen mit weniger Maschinen ueberspringen, Default 3")] int minCount = 3,
            [Description("Maximale Anzahl Gruppen, Default 25")] int limit = 25,
            [Description("Nur Gruppen, in denen ueberhaupt etwas klemmt")] bool onlyProblems = true) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? asm = view.Get("assemblers", surf);
        if(asm == null) return ToolResponse.NoData("assemblers", surf);

        Dictionary<string, int> totals = new Dictionary<string, int>(StringComparer.Ordinal);
        List<GroupInfo> groups = new List<GroupInfo>();

        foreach(JsonProperty g in Json.Object(asm.Data, "by_item")) {
            GroupInfo info = new GroupInfo {
                Item = g.Name,
                Total = Json.Int(g.Value, "total"),
                AvgSpeed = Json.Num(g.Value, "avg_speed"),
                Starving = Json.Int(g.Value, "starving")
            };
            foreach(JsonProperty s in Json.Object(g.Value, "status")) {
                int c = (int)s.Value.GetDouble();
                if(c <= 0) continue;
                info.Status[s.Name] = c;
                totals[s.Name] = (totals.TryGetValue(s.Name, out int prev) ? prev : 0) + c;
                if(s.Name == "working" || s.Name == "normal") info.Working += c;
                else if(InputProblems.Contains(s.Name)) info.WaitingInput += c;
                else if(OutputProblems.Contains(s.Name)) info.Blocked += c;
                else if(!Ignorable.Contains(s.Name)) info.Other += c;
            }
            foreach(KeyValuePair<string, double> m in Json.NumMap(g.Value, "missing")) {
                if(m.Value > 0) info.Missing[m.Key] = m.Value;
            }
            foreach(JsonProperty r in Json.Object(g.Value, "recipes")) {
                info.Recipes[r.Name] = (int)r.Value.GetDouble();
            }
            groups.Add(info);
        }

        if(groupBy == "status") {
            return ToolResponse.Ok(new {
                surface = surf, data_age_seconds = asm.AgeSeconds, group_by = "status",
                machines_total = groups.Sum(g => g.Total),
                by_status = totals,
                no_recipe = Json.Int(asm.Data, "no_recipe")
            });
        }

        IEnumerable<GroupInfo> filtered = groups.Where(g => g.Total >= Math.Max(1, minCount));
        if(onlyProblems) filtered = filtered.Where(g => g.WaitingInput + g.Blocked + g.Other > 0);
        if(statusFilter != null) filtered = filtered.Where(g => g.Status.ContainsKey(statusFilter));

        List<GroupInfo> sorted = filtered
            .OrderByDescending(g => g.WaitingInput + g.Blocked + g.Other)
            .ThenByDescending(g => g.Total)
            .ThenBy(g => g.Item, StringComparer.Ordinal)
            .ToList();

        (List<GroupInfo> page, bool truncated, int total) = ToolResponse.Cap(sorted, limit, opts.Value.MaxItems);
        List<object> items = new List<object>();
        foreach(GroupInfo g in page) {
            items.Add(new {
                item = g.Item,
                total = g.Total,
                working = g.Working,
                waiting_for_input = g.WaitingInput,
                output_blocked = g.Blocked,
                other_problem = g.Other,
                avg_crafting_speed = ToolResponse.R(g.AvgSpeed),
                status = g.Status,
                missing = g.Missing.OrderByDescending(m => m.Value).Take(6)
                    .ToDictionary(m => m.Key, m => ToolResponse.R(m.Value, 1)),
                recipes = groupBy == "recipe" ? g.Recipes : null,
                direction = g.WaitingInput > g.Blocked ? "upstream" : g.Blocked > 0 ? "downstream" : null
            });
        }

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = asm.AgeSeconds,
            group_by = groupBy,
            machines_total = groups.Sum(g => g.Total),
            by_status = totals,
            total_available = total,
            truncated,
            // Bis Mod 0.2.0 gruppiert der Exporter nur nach hergestelltem Item;
            // Entity-Namen und Beispielkoordinaten gibt es dort noch nicht.
            note = groupBy == "entity_name"
                ? "group_by=entity_name braucht fdash-exporter 0.2.0 — es wird nach Item gruppiert."
                : null,
            groups = items
        });
    }

    private sealed class GroupInfo {
        public string Item = "";
        public int Total;
        public int Working;
        public int WaitingInput;
        public int Blocked;
        public int Other;
        public int Starving;
        public double AvgSpeed;
        public Dictionary<string, int> Status = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, double> Missing = new Dictionary<string, double>(StringComparer.Ordinal);
        public Dictionary<string, int> Recipes = new Dictionary<string, int>(StringComparer.Ordinal);
    }
}
