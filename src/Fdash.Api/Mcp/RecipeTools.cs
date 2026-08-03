using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Fdash.Collector;
using Fdash.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Rezeptketten. Ohne die raet ein Modell Ketten zusammen, die es in diesem
/// Modpack gar nicht gibt — auf Pyanodons ist das der Normalfall.
/// </summary>
[McpServerToolType]
public static class RecipeTools {

    [McpServerTool(Name = "get_recipe_tree")]
    [Description("Rezeptkette um ein oder mehrere Items, mit Craftzeit, Zutaten, Nebenprodukten und "
        + "Wahrscheinlichkeiten — und je Stufe der Ist-Zustand: aktuelle Rate, Anzahl Maschinen und "
        + "welche Technologie das Rezept freigeschaltet hat. Richtung waehlbar: woraus es entsteht "
        + "(upstream) oder wer darauf wartet (downstream).")]
    public static string GetRecipeTree(
            SnapshotView view, PrototypeExporter proto, IOptions<McpOptions> opts,
            [Description("Item-Namen, kommagetrennt — mehrere auf einmal sind erlaubt")] string items,
            [Description("Tiefe der Kette, Default 3, Maximum 6")] int depth = 3,
            [Description("Richtung: upstream (Zutaten) | downstream (Abnehmer)")] string direction = "upstream",
            [Description("Alle Rezepte je Item statt nur des gebauten")] bool includeAlternatives = false,
            [Description("Oberflaeche fuer die Ist-Werte, leer = Hauptoberflaeche")] string? surface = null) {
        if(!proto.Loaded) {
            return ToolResponse.Error("Die Prototypen sind noch nicht geladen.",
                "Der Mod schreibt script-output/fdash/prototypes.json beim ersten Start und nach jedem "
                + "Mod-Wechsel. Zustand: get_health (prototypes_loaded).");
        }

        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? prod = view.Get("production", surf);
        JobPayload? asm = view.Get("assemblers", surf);
        JsonElement prodData = prod?.Data ?? default;
        JsonElement asmData = asm?.Data ?? default;

        RecipeQuery query = new RecipeQuery(proto);
        Func<string, ItemState> state = RecipeQuery.StateFrom(prodData, asmData);
        Func<string, int> machines = RecipeQuery.MachinesFrom(asmData);

        bool downstream = direction == "downstream";
        int d = Math.Clamp(depth, 1, 6);

        List<object> trees = new List<object>();
        List<string> unknown = new List<string>();
        foreach(string raw in items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if(query.ProducedBy(raw).Count == 0 && query.ConsumedBy(raw).Count == 0) {
                unknown.Add(raw);
                continue;
            }
            trees.Add(node(query.Build(raw, d, downstream, includeAlternatives, state, machines)));
            if(trees.Count >= 8) break;   // mehr passt nicht in eine sinnvolle Antwort
        }

        return ToolResponse.Ok(new {
            surface = surf,
            direction = downstream ? "downstream" : "upstream",
            depth = d,
            data_age_seconds = prod?.AgeSeconds,
            unknown_items = unknown,
            trees
        });
    }

    [McpServerTool(Name = "get_recipe_ingredients")]
    [Description("Nur die direkten Zutaten — eine Ebene, mit Menge je Craft, Fluid-Kennzeichnung, "
        + "Craftzeit und Ausbeute, ohne Rekursion. Das kleine Gegenstueck zu get_recipe_tree: eine Stufe "
        + "holen, gegen den Ist-Zustand pruefen und nur dort weitergraben, wo es noetig ist, statt einen "
        + "ganzen Baum zu laden.")]
    public static string GetRecipeIngredients(
            SnapshotView view, PrototypeExporter proto, IOptions<McpOptions> opts,
            [Description("Item-Namen, kommagetrennt — mehrere auf einmal sind erlaubt")] string items,
            [Description("Alle Rezepte je Item statt nur des gebauten")] bool includeAlternatives = false,
            [Description("Oberflaeche fuer die Maschinenzahlen, leer = Hauptoberflaeche")] string? surface = null) {
        if(!proto.Loaded) {
            return ToolResponse.Error("Die Prototypen sind noch nicht geladen.",
                "Der Mod schreibt script-output/fdash/prototypes.json beim ersten Start und nach jedem "
                + "Mod-Wechsel. Zustand: get_health (prototypes_loaded).");
        }

        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? asm = view.Get("assemblers", surf);
        RecipeQuery query = new RecipeQuery(proto);
        Func<string, int> machines = RecipeQuery.MachinesFrom(asm?.Data ?? default);

        List<string> names = items
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();
        (List<string> wanted, bool truncated, int total) = ToolResponse.Cap(names, 12, opts.Value.MaxItems);

        List<object> results = new List<object>();
        List<string> unknown = new List<string>();
        List<string> raw = new List<string>();
        foreach(string item in wanted) {
            IReadOnlyList<string> producers = query.ProducedBy(item);
            if(producers.Count == 0) {
                // Kein Rezept heisst zweierlei: Erz oder Tippfehler. Der
                // Unterschied entscheidet, ob man weitersucht oder aufhoert.
                if(query.ConsumedBy(item).Count > 0) raw.Add(item);
                else unknown.Add(item);
                continue;
            }

            List<RecipeProto> views = new List<RecipeProto>();
            foreach(string name in producers) {
                if(proto.Recipes.TryGetValue(name, out RecipeProto? r)) views.Add(r);
            }
            // Die tatsaechlich gebaute Variante zuerst — wie im Baum.
            views.Sort((a, b) => {
                int c = machines(b.Name).CompareTo(machines(a.Name));
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            if(!includeAlternatives && views.Count > 1) views = new List<RecipeProto> { views[0] };

            results.Add(new {
                item,
                recipes_total = producers.Count > views.Count ? producers.Count : (int?)null,
                // Steht keine davon in der Fabrik, ist die Wahl unter mehreren
                // Rezepten alphabetisch und damit willkuerlich. Ohne den Hinweis
                // liest sich "Eisenerz kommt aus Blut" wie eine Tatsache.
                arbitrary_pick = views.Count < producers.Count && machines(views[0].Name) == 0 ? true : (bool?)null,
                recipes = views.Select(r => new {
                    name = r.Name,
                    seconds = ToolResponse.R(r.EnergyRequired),
                    category = r.Category,
                    machines_running_it = machines(r.Name),
                    // Ohne die Ausbeute ist die Zutatenmenge nicht zu deuten:
                    // 3 Kupferdraht je Craft heisst bei 2 Produkten etwas anderes.
                    produces = ToolResponse.R(r.Products
                        .Where(p => p.Name == item).Sum(p => p.Amount), 3),
                    ingredients = io(r.Ingredients),
                    byproducts = r.Products.Count > 1
                        ? io(r.Products.Where(p => p.Name != item).ToList()) : null
                }).ToList()
            });
        }

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = asm?.AgeSeconds,
            truncated,
            total_available = total,
            // Existiert, wird aber von keinem Rezept hergestellt: Erz, Import,
            // Handcraft — hier endet die Kette wirklich.
            raw_items = raw.Count > 0 ? raw : null,
            unknown_items = unknown.Count > 0 ? unknown : null,
            items = results
        });
    }

    private static object node(RecipeNode n) => new {
        item = n.Item,
        produced_per_min = ToolResponse.R(n.State.ProducedPerMin, 1),
        consumed_per_min = ToolResponse.R(n.State.ConsumedPerMin, 1),
        machines = n.State.Machines,
        avg_crafting_speed = n.State.AvgSpeed > 0 ? ToolResponse.R(n.State.AvgSpeed) : (double?)null,
        // Ohne Rezept endet die Kette: Erz, Import oder Handcraft.
        leaf = n.IsLeaf ? true : (bool?)null,
        // Hier war nur die Tiefe zu Ende — mit groesserem depth geht es weiter.
        depth_limited = n.DepthLimited ? true : (bool?)null,
        recipes = n.Recipes.Select(r => new {
            name = r.Name,
            seconds = ToolResponse.R(r.Energy),
            category = r.Category,
            allow_productivity = r.AllowProductivity ? true : (bool?)null,
            machines_running_it = r.MachinesRunningIt,
            ingredients = io(r.Ingredients),
            products = io(r.Products),
            unlocked_by = r.UnlockedBy.Count > 0 ? r.UnlockedBy : null
        }).ToList(),
        children = n.Children.Select(node).ToList()
    };

    private static List<object> io(List<RecipeIo> list) =>
        list.Select(i => (object)new {
            name = i.Name,
            type = i.Type == "item" ? null : i.Type,
            amount = ToolResponse.R(i.Amount, 3)
        }).ToList();
}
