using System.Text.Json;
using Fdash.Collector;
using Fdash.Core;

namespace Fdash.Analysis;

public sealed record PlanStep {
    public string Item { get; init; } = "";
    public string Recipe { get; init; } = "";
    public int Depth { get; init; }
    public double RequiredPerMin { get; init; }
    public double CurrentPerMin { get; init; }
    /// <summary>Maschinen bei der gemessenen Craftgeschwindigkeit.</summary>
    public double MachinesNeeded { get; init; }
    public int MachinesPresent { get; init; }
    public double CraftingSpeed { get; init; }
    public bool AllowProductivity { get; init; }
    /// <summary>Ist/Soll — der kleinste Wert der Kette ist der limitierende Schritt.</summary>
    public double Coverage { get; init; }
}

public sealed record ProductionPlan {
    public string Item { get; init; } = "";
    public double TargetPerMin { get; init; }
    public double CurrentPerMin { get; init; }
    public PlanStep? LimitingStep { get; init; }
    public List<PlanStep> Steps { get; init; } = new List<PlanStep>();
}

/// <summary>
/// Wie viele Maschinen je Stufe fuer eine Zielrate noetig sind — und wo die
/// Kette heute schon nicht mitkommt.
///
/// Gerechnet wird mit der GEMESSENEN Craftgeschwindigkeit der vorhandenen
/// Maschinen, nicht mit einer angenommenen. Ein Rechner, der Assembler-2 mit
/// Modulen als Assembler-1 zaehlt, liefert Zahlen, die im Spiel nicht stimmen.
/// </summary>
public sealed class ProductionPlanner {
    private readonly PrototypeExporter proto;

    public ProductionPlanner(PrototypeExporter proto) {
        this.proto = proto;
    }

    public ProductionPlan Plan(string item, double targetPerMin, int maxDepth,
            JsonElement production, JsonElement assemblers) {
        RecipeQuery query = new RecipeQuery(proto);
        Func<string, ItemState> state = RecipeQuery.StateFrom(production, assemblers);
        Func<string, int> machinesOf = RecipeQuery.MachinesFrom(assemblers);

        List<PlanStep> steps = new List<PlanStep>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        Queue<(string Item, double Required, int Depth)> queue = new Queue<(string, double, int)>();
        queue.Enqueue((item, targetPerMin, 0));

        while(queue.Count > 0) {
            (string current, double required, int depth) = queue.Dequeue();
            if(depth > maxDepth || !seen.Add(current)) continue;

            IReadOnlyList<string> recipes = query.ProducedBy(current);
            if(recipes.Count == 0) continue;   // Erz, Import, Handcraft

            string best = recipes[0];
            int bestMachines = -1;
            foreach(string r in recipes) {
                int m = machinesOf(r);
                if(m > bestMachines) { bestMachines = m; best = r; }
            }
            if(!proto.Recipes.TryGetValue(best, out RecipeProto? recipe)) continue;

            double perCraft = 0;
            foreach(RecipeIo p in recipe.Products) {
                if(p.Name == current) perCraft += p.Amount;
            }
            if(perCraft <= 0) continue;

            ItemState s = state(current);
            // Ohne gemessene Geschwindigkeit bleibt nur 1.0 — beim ersten Bau
            // einer Stufe steht dort noch keine Maschine.
            double speed = s.AvgSpeed > 0 ? s.AvgSpeed : 1;
            double craftsPerMin = 60.0 / Math.Max(0.001, recipe.EnergyRequired) * speed;
            double needed = required / (craftsPerMin * perCraft);

            steps.Add(new PlanStep {
                Item = current,
                Recipe = recipe.Name,
                Depth = depth,
                RequiredPerMin = Math.Round(required, 1),
                CurrentPerMin = Math.Round(s.ProducedPerMin, 1),
                // Zwei Nachkommastellen: bei kleinen Zielraten liegt die Antwort
                // oft unter einer Maschine, und "0,8" statt "0,75" ist dort ein
                // Fehler von sieben Prozent.
                MachinesNeeded = Math.Round(needed, 2),
                MachinesPresent = s.Machines,
                CraftingSpeed = Math.Round(speed, 2),
                AllowProductivity = recipe.AllowProductivity,
                Coverage = required > 0 ? Math.Round(s.ProducedPerMin / required, 3) : 1
            });

            foreach(RecipeIo ing in recipe.Ingredients) {
                queue.Enqueue((ing.Name, required * ing.Amount / perCraft, depth + 1));
            }
        }

        PlanStep? limiting = null;
        foreach(PlanStep s in steps) {
            if(limiting == null || s.Coverage < limiting.Coverage) limiting = s;
        }

        return new ProductionPlan {
            Item = item,
            TargetPerMin = targetPerMin,
            CurrentPerMin = state(item).ProducedPerMin,
            LimitingStep = limiting,
            Steps = steps
        };
    }
}
