using System.Text.Json;
using Fdash.Collector;
using Fdash.Core;

namespace Fdash.Analysis;

/// <summary>Ist-Zustand eines Items auf einer Oberflaeche.</summary>
public sealed record ItemState(double ProducedPerMin, double ConsumedPerMin, int Machines, double AvgSpeed);

/// <summary>Ein Rezept mit dem, was davon gerade tatsaechlich laeuft.</summary>
public sealed record RecipeView {
    public string Name { get; init; } = "";
    public double Energy { get; init; }
    public string? Category { get; init; }
    public bool AllowProductivity { get; init; }
    public int MachinesRunningIt { get; init; }
    public List<RecipeIo> Ingredients { get; init; } = new();
    public List<RecipeIo> Products { get; init; } = new();
    public List<string> UnlockedBy { get; init; } = new();
}

public sealed record RecipeNode {
    public string Item { get; init; } = "";
    public ItemState State { get; init; } = new ItemState(0, 0, 0, 0);
    public List<RecipeView> Recipes { get; init; } = new();
    public List<RecipeNode> Children { get; init; } = new();
    /// <summary>Kein Rezept bekannt: Erz, Import oder Handcraft — hier endet die Kette wirklich.</summary>
    public bool IsLeaf { get; init; }
    /// <summary>
    /// Hier war nur die Tiefe zu Ende, nicht die Kette. Der Unterschied ist
    /// wichtig: "wird nicht gefertigt" und "nicht weiter verfolgt" sehen im
    /// Ergebnis sonst gleich aus und fuehren zu ganz verschiedenen Schluessen.
    /// </summary>
    public bool DepthLimited { get; init; }
}

/// <summary>
/// Rezeptketten aus dem Prototyp-Export, angereichert mit dem Ist-Zustand.
///
/// Der Abgleich ist der Punkt: der Prototyp sagt, was ein Schritt theoretisch
/// braucht; die Statistik sagt, was ankommt. Erst zusammen wird aus "du
/// brauchst Kupferdraht" ein "dir fehlen 400 Kupferdraht pro Minute".
/// </summary>
public sealed class RecipeQuery {
    private readonly PrototypeExporter proto;
    private Dictionary<string, List<string>>? producedBy;
    private Dictionary<string, List<string>>? consumedBy;

    public RecipeQuery(PrototypeExporter proto) {
        this.proto = proto;
    }

    /// <summary>Item -&gt; Rezepte, die es herstellen.</summary>
    public IReadOnlyList<string> ProducedBy(string item) =>
        index().TryGetValue(item, out List<string>? list) ? list : Array.Empty<string>();

    /// <summary>Item -&gt; Rezepte, die es verbrauchen.</summary>
    public IReadOnlyList<string> ConsumedBy(string item) =>
        reverseIndex().TryGetValue(item, out List<string>? list) ? list : Array.Empty<string>();

    /// <summary>
    /// Baum um ein Item. <paramref name="downstream"/> dreht die Richtung: nicht
    /// "woraus wird es gemacht", sondern "wer wartet darauf".
    /// </summary>
    public RecipeNode Build(string item, int depth, bool downstream, bool includeAlternatives,
            Func<string, ItemState> state, Func<string, int> machinesPerRecipe,
            HashSet<string>? path = null) {
        path ??= new HashSet<string>(StringComparer.Ordinal);
        ItemState s = state(item);

        // Kreise gibt es wirklich: Recycling und Nebenprodukte schliessen Ketten.
        if(depth <= 0 || !path.Add(item)) {
            IReadOnlyList<string> more = downstream ? ConsumedBy(item) : ProducedBy(item);
            return new RecipeNode {
                Item = item, State = s,
                IsLeaf = more.Count == 0,
                DepthLimited = more.Count > 0
            };
        }

        IReadOnlyList<string> names = downstream ? ConsumedBy(item) : ProducedBy(item);
        List<RecipeView> views = new List<RecipeView>();
        foreach(string name in names) {
            if(!proto.Recipes.TryGetValue(name, out RecipeProto? r)) continue;
            views.Add(new RecipeView {
                Name = r.Name,
                Energy = r.EnergyRequired,
                Category = r.Category,
                AllowProductivity = r.AllowProductivity,
                MachinesRunningIt = machinesPerRecipe(r.Name),
                Ingredients = r.Ingredients,
                Products = r.Products,
                UnlockedBy = proto.UnlockedBy.TryGetValue(r.Name, out List<string>? techs)
                    ? techs : new List<string>()
            });
        }

        // Die tatsaechlich gebaute Variante zuerst — auf Pyanodons hat fast jedes
        // Item mehrere Rezepte, und nur eines davon steht in der Fabrik.
        views.Sort((a, b) => {
            int c = b.MachinesRunningIt.CompareTo(a.MachinesRunningIt);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        if(!includeAlternatives && views.Count > 1) views = new List<RecipeView> { views[0] };

        List<RecipeNode> children = new List<RecipeNode>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach(RecipeView v in views) {
            IEnumerable<RecipeIo> next = downstream ? v.Products : v.Ingredients;
            foreach(RecipeIo io in next) {
                if(io.Name == item || !seen.Add(io.Name)) continue;
                children.Add(Build(io.Name, depth - 1, downstream, includeAlternatives,
                    state, machinesPerRecipe, path));
            }
        }

        path.Remove(item);
        return new RecipeNode {
            Item = item, State = s, Recipes = views, Children = children,
            IsLeaf = views.Count == 0
        };
    }

    /// <summary>Ist-Zustand aus production- und assemblers-Payload.</summary>
    public static Func<string, ItemState> StateFrom(JsonElement production, JsonElement assemblers) {
        Dictionary<string, ItemState> map = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        foreach(JsonElement it in Json.Array(production, "items")) {
            string name = Json.Str(it, "item");
            map[name] = new ItemState(Json.Num(it, "produced_per_min"), Json.Num(it, "consumed_per_min"), 0, 0);
        }
        foreach(JsonProperty g in Json.Object(assemblers, "by_item")) {
            ItemState prev = map.TryGetValue(g.Name, out ItemState? e) ? e : new ItemState(0, 0, 0, 0);
            map[g.Name] = prev with { Machines = Json.Int(g.Value, "total"), AvgSpeed = Json.Num(g.Value, "avg_speed") };
        }
        return item => map.TryGetValue(item, out ItemState? s) ? s : new ItemState(0, 0, 0, 0);
    }

    /// <summary>Rezeptname -&gt; wie viele Maschinen es gerade eingestellt haben.</summary>
    public static Func<string, int> MachinesFrom(JsonElement assemblers) {
        Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach(JsonProperty g in Json.Object(assemblers, "by_item")) {
            foreach(JsonProperty r in Json.Object(g.Value, "recipes")) {
                map[r.Name] = (map.TryGetValue(r.Name, out int n) ? n : 0) + (int)r.Value.GetDouble();
            }
        }
        return recipe => map.TryGetValue(recipe, out int c) ? c : 0;
    }

    private Dictionary<string, List<string>> index() {
        if(producedBy != null) return producedBy;
        producedBy = build(r => r.Products);
        return producedBy;
    }

    private Dictionary<string, List<string>> reverseIndex() {
        if(consumedBy != null) return consumedBy;
        consumedBy = build(r => r.Ingredients);
        return consumedBy;
    }

    private Dictionary<string, List<string>> build(Func<RecipeProto, List<RecipeIo>> side) {
        Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach(RecipeProto r in proto.Recipes.Values) {
            foreach(RecipeIo io in side(r)) {
                if(!map.TryGetValue(io.Name, out List<string>? list)) {
                    list = new List<string>();
                    map[io.Name] = list;
                }
                if(!list.Contains(r.Name)) list.Add(r.Name);
            }
        }
        foreach(List<string> list in map.Values) list.Sort(StringComparer.Ordinal);
        return map;
    }
}
