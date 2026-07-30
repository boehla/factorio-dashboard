using System.Text.Json;
using Fdash.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fdash.Collector;

/// <summary>
/// Laedt den Prototyp-Export des Mods (<c>script-output/fdash/prototypes.json</c>).
///
/// Der Mod schreibt die Datei beim ersten Start und nach jeder Mod-Aenderung,
/// gestueckelt ueber mehrere Ticks. Sie ist pro Save konstant, wird also genau
/// einmal eingelesen — und liefert Rezeptstruktur, Ressourcen-Metadaten und
/// Fluidnamen fuer den Abhaengigkeitsgraphen.
/// </summary>
public sealed class PrototypeExporter {
    private readonly CollectorOptions options;
    private readonly ILogger<PrototypeExporter> log;

    public bool Loaded { get; private set; }

    public IReadOnlyDictionary<string, ResourceProto> Resources { get; private set; }
        = new Dictionary<string, ResourceProto>();
    public IReadOnlyDictionary<string, RecipeProto> Recipes { get; private set; }
        = new Dictionary<string, RecipeProto>();
    public IReadOnlyList<string> OreItems { get; private set; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> IconStems { get; private set; }
        = new Dictionary<string, string>();
    public IReadOnlyList<string> FluidNames { get; private set; } = Array.Empty<string>();

    public PrototypeExporter(IOptions<CollectorOptions> options, ILogger<PrototypeExporter> log) {
        this.options = options.Value;
        this.log = log;
    }

    private string? path {
        get {
            if(string.IsNullOrWhiteSpace(options.ScriptOutputPath)) return null;
            return Path.Combine(
                Environment.ExpandEnvironmentVariables(options.ScriptOutputPath!),
                "fdash", "prototypes.json");
        }
    }

    /// <summary>
    /// Versucht zu laden. Liefert false, solange die Datei fehlt oder noch
    /// geschrieben wird — der Aufrufer probiert es dann spaeter erneut.
    /// </summary>
    public async Task<bool> TryLoadAsync(CancellationToken ct) {
        if(Loaded) return true;
        string? p = path;
        if(p == null) {
            log.LogWarning("Collector:ScriptOutputPath is not set — prototype data (recipe graph, ore metadata) stays empty.");
            return false;
        }
        if(!File.Exists(p)) return false;

        try {
            await using(FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete)) {
                using(JsonDocument doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct)) {
                    load(doc.RootElement);
                }
            }
            Loaded = true;
            return true;
        } catch(JsonException) {
            // Wird gerade geschrieben (der Mod haengt blockweise an) — spaeter erneut.
            return false;
        } catch(IOException) {
            return false;
        }
    }

    private void load(JsonElement root) {
        Dictionary<string, ResourceProto> res = new();
        List<string> ores = new();
        if(root.TryGetProperty("resources", out JsonElement r) && r.ValueKind == JsonValueKind.Object) {
            foreach(JsonProperty p in r.EnumerateObject()) {
                string? product = p.Value.TryGetProperty("product", out JsonElement pr) && pr.ValueKind == JsonValueKind.String
                    ? pr.GetString() : null;
                res[p.Name] = new ResourceProto {
                    Infinite = p.Value.TryGetProperty("infinite", out JsonElement i) && i.ValueKind == JsonValueKind.True,
                    MiningTime = p.Value.TryGetProperty("mining_time", out JsonElement mt) && mt.ValueKind == JsonValueKind.Number
                        ? mt.GetDouble() : 1,
                    Normal = p.Value.TryGetProperty("normal", out JsonElement n) && n.ValueKind == JsonValueKind.Number
                        ? n.GetDouble() : null,
                    Product = product
                };
                if(product != null && !ores.Contains(product)) ores.Add(product);
            }
        }
        Resources = res;
        OreItems = ores;

        Dictionary<string, RecipeProto> recipes = new();
        if(root.TryGetProperty("recipes", out JsonElement rc) && rc.ValueKind == JsonValueKind.Object) {
            foreach(JsonProperty p in rc.EnumerateObject()) {
                recipes[p.Name] = new RecipeProto {
                    Name = p.Name,
                    EnergyRequired = p.Value.TryGetProperty("e", out JsonElement er) && er.ValueKind == JsonValueKind.Number
                        ? er.GetDouble() : 0.5,
                    Ingredients = parseIo(p.Value, "ing"),
                    Products = parseIo(p.Value, "prod")
                };
            }
        }
        Recipes = recipes;

        Dictionary<string, string> stems = new();
        if(root.TryGetProperty("icon_stems", out JsonElement icst) && icst.ValueKind == JsonValueKind.Object) {
            foreach(JsonProperty p in icst.EnumerateObject()) {
                string? stem = p.Value.GetString();
                if(!string.IsNullOrEmpty(stem)) stems[p.Name] = stem;
            }
        }
        IconStems = stems;

        List<string> fluidNames = new();
        if(root.TryGetProperty("fluid_names", out JsonElement fn) && fn.ValueKind == JsonValueKind.Object) {
            foreach(JsonProperty p in fn.EnumerateObject()) fluidNames.Add(p.Name);
        }
        FluidNames = fluidNames;

        log.LogInformation("Loaded {Res} resources, {Rec} recipes, {Stem} icon stems, {Fluid} fluids.",
            res.Count, recipes.Count, stems.Count, fluidNames.Count);
    }

    /// <summary>Kompakte {n=name, t=type, a=amount}-Liste aus einem Rezept-Eintrag.</summary>
    private static List<RecipeIo> parseIo(JsonElement recipe, string prop) {
        List<RecipeIo> list = new();
        if(recipe.TryGetProperty(prop, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array) {
            foreach(JsonElement e in arr.EnumerateArray()) {
                string? name = e.TryGetProperty("n", out JsonElement n) ? n.GetString() : null;
                if(string.IsNullOrEmpty(name)) continue;
                string type = e.TryGetProperty("t", out JsonElement t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()! : "item";
                double amount = e.TryGetProperty("a", out JsonElement a) && a.ValueKind == JsonValueKind.Number
                    ? a.GetDouble() : 1;
                list.Add(new RecipeIo { Name = name, Type = type, Amount = amount });
            }
        }
        return list;
    }
}

public sealed record ResourceProto {
    public bool Infinite { get; init; }
    public double MiningTime { get; init; } = 1;
    public double? Normal { get; init; }
    public string? Product { get; init; }
}
