using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Fdash.Collector;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Die Tools, die aus Daten eine Antwort machen: wo klemmt eine Kette, und was
/// muesste gebaut werden.
/// </summary>
[McpServerToolType]
public static class DiagnosisTools {

    [McpServerTool(Name = "diagnose_item_chain")]
    [Description("Laeuft die Rezeptkette eines Items nach oben und liefert die ERSTE Stufe, an der "
        + "es klemmt — mit Grund (kein Abnehmer, fehlende Zutat, zu wenig Maschinen, wird gar nicht "
        + "gefertigt), Zahlen und einem konkreten Vorschlag. Der Aufruf fuer 'warum kommt zu wenig X'.")]
    public static string DiagnoseItemChain(
            SnapshotView view, PrototypeExporter proto, IOptions<McpOptions> opts,
            [Description("Ziel-Item")] string item,
            [Description("Gewuenschte Rate pro Minute. Leer = die aktuelle Abnahme als Ziel.")]
            double? targetPerMin = null,
            [Description("Maximale Tiefe der Kette, Default 5")] int maxDepth = 5,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null) {
        if(!proto.Loaded) return notLoaded();

        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? prod = view.Get("production", surf);
        JobPayload? asm = view.Get("assemblers", surf);
        if(prod == null || asm == null) return ToolResponse.NoData("production/assemblers", surf);

        ChainDiagnosis d = new ChainDiagnoser(proto)
            .Diagnose(item, targetPerMin, Math.Clamp(maxDepth, 1, 8), prod.Data, asm.Data);

        return ToolResponse.Ok(new {
            item = d.Item,
            surface = surf,
            data_age_seconds = prod.AgeSeconds,
            current_per_min = ToolResponse.R(d.CurrentPerMin, 1),
            target_per_min = d.TargetPerMin,
            root_cause = d.RootCause == null ? null : new {
                stage = d.RootCause.Item,
                depth = d.RootCause.Depth,
                reason = d.RootCause.Reason,
                produced_per_min = d.RootCause.ProducedPerMin,
                required_per_min = d.RootCause.RequiredPerMin,
                machines = d.RootCause.Machines,
                waiting_for_input = d.RootCause.WaitingForInput,
                output_blocked = d.RootCause.OutputBlocked,
                detail = d.Detail,
                suggested_action = d.SuggestedAction
            },
            chain = d.Chain.Take(opts.Value.MaxItems).Select(s => new {
                stage = s.Item,
                depth = s.Depth,
                ok = s.Ok,
                reason = s.Reason == "ok" ? null : s.Reason,
                produced_per_min = s.ProducedPerMin,
                required_per_min = s.RequiredPerMin,
                machines = s.Machines
            }).ToList(),
            chain_truncated = d.Chain.Count > opts.Value.MaxItems
        });
    }

    [McpServerTool(Name = "plan_production")]
    [Description("Wie viele Maschinen je Stufe fuer eine Zielrate noetig sind, gerechnet mit der "
        + "GEMESSENEN Craftgeschwindigkeit der vorhandenen Maschinen — plus der limitierende Schritt "
        + "der Kette und ob Produktivitaetsmodule dort erlaubt sind.")]
    public static string PlanProduction(
            SnapshotView view, PrototypeExporter proto, IOptions<McpOptions> opts,
            [Description("Ziel-Item")] string item,
            [Description("Zielrate pro Minute")] double targetPerMin,
            [Description("Maximale Tiefe der Kette, Default 4")] int maxDepth = 4,
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null) {
        if(!proto.Loaded) return notLoaded();
        if(targetPerMin <= 0) return ToolResponse.Error("target_per_min muss groesser als 0 sein.");

        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? prod = view.Get("production", surf);
        JobPayload? asm = view.Get("assemblers", surf);
        if(prod == null || asm == null) return ToolResponse.NoData("production/assemblers", surf);

        ProductionPlan plan = new ProductionPlanner(proto)
            .Plan(item, targetPerMin, Math.Clamp(maxDepth, 1, 6), prod.Data, asm.Data);

        (List<PlanStep> steps, bool truncated, int total) =
            ToolResponse.Cap(plan.Steps, opts.Value.MaxItems, opts.Value.MaxItems);

        return ToolResponse.Ok(new {
            item = plan.Item,
            surface = surf,
            data_age_seconds = prod.AgeSeconds,
            target_per_min = plan.TargetPerMin,
            current_per_min = ToolResponse.R(plan.CurrentPerMin, 1),
            limiting_step = plan.LimitingStep == null ? null : new {
                item = plan.LimitingStep.Item,
                coverage = plan.LimitingStep.Coverage,
                produced_per_min = plan.LimitingStep.CurrentPerMin,
                required_per_min = plan.LimitingStep.RequiredPerMin,
                machines_present = plan.LimitingStep.MachinesPresent,
                machines_needed = plan.LimitingStep.MachinesNeeded
            },
            total_available = total,
            truncated,
            steps = steps.Select(s => new {
                item = s.Item,
                recipe = s.Recipe,
                depth = s.Depth,
                required_per_min = s.RequiredPerMin,
                produced_per_min = s.CurrentPerMin,
                machines_present = s.MachinesPresent,
                machines_needed = s.MachinesNeeded,
                machines_missing = ToolResponse.R(Math.Max(0, s.MachinesNeeded - s.MachinesPresent), 1),
                crafting_speed = s.CraftingSpeed,
                allow_productivity = s.AllowProductivity ? true : (bool?)null
            }).ToList()
        });
    }

    private static string notLoaded() => ToolResponse.Error(
        "Die Prototypen sind noch nicht geladen.",
        "Ohne Rezeptdaten gibt es keine Kette. Der Mod schreibt script-output/fdash/prototypes.json "
        + "beim ersten Start und nach jedem Mod-Wechsel. Zustand: get_health.");
}
