using System.Text.Json;
using Fdash.Collector;

namespace Fdash.Analysis;

/// <summary>Eine Stufe der Kette mit ihrem Befund.</summary>
public sealed record ChainStage {
    public string Item { get; init; } = "";
    public int Depth { get; init; }
    public double ProducedPerMin { get; init; }
    public double ConsumedPerMin { get; init; }
    /// <summary>Was diese Stufe liefern muesste, damit das Ziel erreicht wird.</summary>
    public double RequiredPerMin { get; init; }
    public int Machines { get; init; }
    public int WaitingForInput { get; init; }
    public int OutputBlocked { get; init; }
    public bool Ok { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record ChainDiagnosis {
    public string Item { get; init; } = "";
    public double CurrentPerMin { get; init; }
    public double? TargetPerMin { get; init; }
    public ChainStage? RootCause { get; init; }
    public string Detail { get; init; } = "";
    public string SuggestedAction { get; init; } = "";
    public List<ChainStage> Chain { get; init; } = new List<ChainStage>();
}

/// <summary>
/// Laeuft die Rezeptkette eines Items nach oben und sucht die erste Stufe, an
/// der es klemmt.
///
/// Das ist der Unterschied zwischen einem Datenzugriff und einer Antwort: die
/// Einzelwerte stehen alle in anderen Tools, aber die Frage "warum kommt zu
/// wenig X" beantwortet erst der Abgleich von Soll-Rate, Ist-Rate und
/// Maschinenzustand ueber die ganze Kette. Und der gehoert dorthin, wo die
/// Daten schon liegen.
/// </summary>
public sealed class ChainDiagnoser {
    private readonly PrototypeExporter proto;

    public ChainDiagnoser(PrototypeExporter proto) {
        this.proto = proto;
    }

    /// <summary>
    /// <paramref name="target"/> ist optional. Ohne Ziel wird die aktuelle
    /// Verbrauchsrate als Soll genommen — "so viel, wie gerade abgenommen wird".
    /// </summary>
    public ChainDiagnosis Diagnose(string item, double? target, int maxDepth,
            JsonElement production, JsonElement assemblers) {
        RecipeQuery query = new RecipeQuery(proto);
        Func<string, ItemState> state = RecipeQuery.StateFrom(production, assemblers);
        Dictionary<string, MachineStatus> status = machineStatus(assemblers);

        ItemState top = state(item);
        double goal = target ?? Math.Max(top.ConsumedPerMin, top.ProducedPerMin);

        List<ChainStage> chain = new List<ChainStage>();
        ChainStage? root = null;
        // Wenn die ganze Kette nur hungert, ist die tiefste hungernde Stufe die
        // beste Auskunft, die es gibt.
        ChainStage? deepestStarving = null;
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        // Breitensuche: die Kette wird Stufe fuer Stufe abgearbeitet, damit die
        // erste gefundene Ursache auch die naechstliegende ist.
        Queue<(string Item, double Required, int Depth)> queue = new Queue<(string, double, int)>();
        queue.Enqueue((item, goal, 0));

        while(queue.Count > 0) {
            (string current, double required, int depth) = queue.Dequeue();
            if(depth > maxDepth || !seen.Add(current)) continue;

            ItemState s = state(current);
            status.TryGetValue(current, out MachineStatus? ms);
            ChainStage stage = evaluate(current, depth, required, s, ms);
            chain.Add(stage);

            // "Wartet auf Zutaten" ist ein Symptom, keine Ursache: es heisst
            // wortwoertlich, dass die Ursache weiter oben liegt. Deshalb wird
            // weitergelaufen, bis eine Stufe etwas anderes meldet — ein
            // blockierter Ausgang, zu wenige Maschinen oder gar keine
            // Fertigung. Genau die ist die Antwort auf "warum kommt zu wenig X".
            if(!stage.Ok) {
                if(stage.Reason != "no_ingredients") root ??= stage;
                else if(deepestStarving == null || stage.Depth > deepestStarving.Depth) {
                    deepestStarving = stage;
                }
            }

            // Nur weiterlaufen, solange die Stufe wirklich zu wenig liefert:
            // eine ausreichend versorgte Zutat muss man nicht aufdroeseln.
            if(stage.Ok) continue;

            IReadOnlyList<string> recipes = query.ProducedBy(current);
            if(recipes.Count == 0) continue;

            // Das gebaute Rezept nimmt die Kette, nicht irgendeines: die
            // Alternativen sagen nichts ueber die Fabrik, die dasteht.
            string best = recipes[0];
            int bestMachines = -1;
            foreach(string r in recipes) {
                int m = machinesRunning(assemblers, r);
                if(m > bestMachines) { bestMachines = m; best = r; }
            }
            if(!proto.Recipes.TryGetValue(best, out Fdash.Core.RecipeProto? recipe)) continue;

            double perCraft = 0;
            foreach(Fdash.Core.RecipeIo p in recipe.Products) {
                if(p.Name == current) perCraft += p.Amount;
            }
            if(perCraft <= 0) continue;

            foreach(Fdash.Core.RecipeIo ing in recipe.Ingredients) {
                queue.Enqueue((ing.Name, required * ing.Amount / perCraft, depth + 1));
            }
        }

        root ??= deepestStarving;
        (string detail, string action) = explain(root, item);
        return new ChainDiagnosis {
            Item = item,
            CurrentPerMin = top.ProducedPerMin,
            TargetPerMin = target,
            RootCause = root,
            Detail = detail,
            SuggestedAction = action,
            Chain = chain
        };
    }

    /// <summary>
    /// Beurteilt eine Stufe. Die Reihenfolge der Pruefungen ist die Reihenfolge
    /// der Aussagekraft: ein blockierter Ausgang erklaert mehr als eine zu
    /// niedrige Rate, denn er sagt auch, wo man suchen muss.
    /// </summary>
    private static ChainStage evaluate(string item, int depth, double required,
            ItemState s, MachineStatus? ms) {
        int waiting = ms?.WaitingForInput ?? 0;
        int blocked = ms?.OutputBlocked ?? 0;
        int machines = ms?.Total ?? s.Machines;

        bool enough = required <= 0 || s.ProducedPerMin >= required * 0.95;
        string reason = "ok";
        if(machines == 0 && s.ProducedPerMin <= 0) reason = "not_produced";
        else if(blocked > 0 && blocked >= waiting) reason = "full_output";
        else if(waiting > 0) reason = "no_ingredients";
        else if(!enough) reason = "too_slow";

        return new ChainStage {
            Item = item,
            Depth = depth,
            ProducedPerMin = Math.Round(s.ProducedPerMin, 1),
            ConsumedPerMin = Math.Round(s.ConsumedPerMin, 1),
            RequiredPerMin = Math.Round(required, 1),
            Machines = machines,
            WaitingForInput = waiting,
            OutputBlocked = blocked,
            Ok = reason == "ok",
            Reason = reason
        };
    }

    private static (string Detail, string Action) explain(ChainStage? root, string item) {
        if(root == null) return ($"{item} wird ausreichend produziert.", "nichts zu tun");

        // Die Wurzel kann das angefragte Item selbst sein. "Das Zwischenprodukt
        // X" zu schreiben, wenn X genau das ist, wonach gefragt wurde, liest
        // sich wie ein Fehler im Werkzeug.
        string what = root.Depth == 0 ? root.Item : $"das Zwischenprodukt {root.Item}";

        return root.Reason switch {
            "full_output" =>
                ($"{root.OutputBlocked} Maschinen stauen am Ausgang — {what} wird gefertigt, "
                    + "aber nicht abgenommen.",
                 $"Abnahme fuer {root.Item} schaffen. Was niemand abnimmt, haelt die Stufe an, "
                    + "die es herstellt"),
            "no_ingredients" =>
                ($"{root.WaitingForInput} Maschinen warten auf Zutaten fuer {root.Item} "
                    + $"({root.ProducedPerMin}/min statt {root.RequiredPerMin}/min).",
                 $"Zufuhr zu {root.Item} pruefen — die Ursache liegt weiter oben in der Kette"),
            "not_produced" =>
                ($"{root.Item} wird auf dieser Oberflaeche gar nicht gefertigt.",
                 $"{root.Item} herstellen oder importieren"),
            "too_slow" =>
                ($"{root.Item} liefert {root.ProducedPerMin}/min, gebraucht werden "
                    + $"{root.RequiredPerMin}/min bei {root.Machines} Maschinen.",
                 $"mehr Maschinen fuer {root.Item} — oder schnellere. Siehe plan_production"),
            _ => ($"{root.Item} ist die erste auffaellige Stufe.", "Stufe pruefen")
        };
    }

    private static int machinesRunning(JsonElement assemblers, string recipe) {
        int total = 0;
        foreach(JsonProperty g in Json.Object(assemblers, "by_item")) {
            foreach(JsonProperty r in Json.Object(g.Value, "recipes")) {
                if(r.Name == recipe) total += (int)r.Value.GetDouble();
            }
        }
        return total;
    }

    private static Dictionary<string, MachineStatus> machineStatus(JsonElement assemblers) {
        Dictionary<string, MachineStatus> map = new Dictionary<string, MachineStatus>(StringComparer.Ordinal);
        foreach(JsonProperty g in Json.Object(assemblers, "by_item")) {
            MachineStatus ms = new MachineStatus { Total = Json.Int(g.Value, "total") };
            foreach(JsonProperty s in Json.Object(g.Value, "status")) {
                int c = (int)s.Value.GetDouble();
                switch(s.Name) {
                    case "no_ingredients":
                    case "item_ingredient_shortage":
                    case "fluid_ingredient_shortage":
                    case "no_input_fluid":
                    case "low_input_fluid":
                    case "waiting_for_source_items":
                        ms.WaitingForInput += c;
                        break;
                    case "full_output":
                    case "output_full":
                    case "full_burnt_result_output":
                    case "full_burned_result_output":
                    case "waiting_for_space_in_destination":
                        ms.OutputBlocked += c;
                        break;
                }
            }
            map[g.Name] = ms;
        }
        return map;
    }

    private sealed class MachineStatus {
        public int Total;
        public int WaitingForInput;
        public int OutputBlocked;
    }
}
