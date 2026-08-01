using System.Text.Json;
using Fdash.Core;

namespace Fdash.Collector;

/// <summary>
/// Wandelt Job-Snapshots in flache Zeitreihen-Samples (Plan §5.5). Bewusst
/// generisch ueber die JSON-Struktur, damit modded Metriken automatisch
/// mitlaufen, ohne feste Feldliste.
/// </summary>
public static class MetricExtractor {
    public static IReadOnlyList<Sample> Extract(string job, string saveId, long ts, JsonElement root) {
        List<Sample> samples = new List<Sample>();
        switch(job) {
            case "power":
                extractPower(saveId, ts, root, samples);
                break;
            case "production":
                extractProduction(saveId, ts, root, samples);
                break;
            case "logistics":
                extractLogistics(saveId, ts, root, samples);
                break;
            case "resources":
                extractResources(saveId, ts, root, samples);
                break;
            case "fluids":
                extractFluids(saveId, ts, root, samples);
                break;
            case "containers":
                extractContainers(saveId, ts, root, samples);
                break;
        }
        return samples;
    }

    private static void extractPower(string saveId, long ts, JsonElement root, List<Sample> outp) {
        string surface = str(root, "surface");
        if(!root.TryGetProperty("networks", out JsonElement nets)) return;
        foreach(JsonElement n in nets.EnumerateArray()) {
            string labels = $"surface={surface},network={num(n, "id")}";
            outp.Add(new Sample(saveId, "power.production", labels, ts, num(n, "production")));
            outp.Add(new Sample(saveId, "power.consumption", labels, ts, num(n, "consumption")));
            outp.Add(new Sample(saveId, "power.satisfaction", labels, ts, num(n, "satisfaction")));
            // Der Akkustand ist der Fruehindikator: die Satisfaction kann noch
            // 1.0 sein, waehrend die Puffer seit Stunden leerer werden.
            if(n.TryGetProperty("accumulators", out JsonElement acc) && acc.ValueKind == JsonValueKind.Object) {
                double capacity = num(acc, "capacity");
                if(capacity > 0) {
                    outp.Add(new Sample(saveId, "power.accumulator_charge", labels, ts,
                        num(acc, "energy") / capacity));
                }
            }
        }
    }

    private static void extractProduction(string saveId, long ts, JsonElement root, List<Sample> outp) {
        string surface = str(root, "surface");
        if(!root.TryGetProperty("items", out JsonElement items)) return;
        foreach(JsonElement it in items.EnumerateArray()) {
            string item = str(it, "item");
            string labels = $"surface={surface},item={item}";
            outp.Add(new Sample(saveId, "production.produced", labels, ts, num(it, "produced_per_min")));
            outp.Add(new Sample(saveId, "production.consumed", labels, ts, num(it, "consumed_per_min")));
        }
    }

    private static void extractLogistics(string saveId, long ts, JsonElement root, List<Sample> outp) {
        string surface = str(root, "surface");
        if(!root.TryGetProperty("networks", out JsonElement nets)) return;
        foreach(JsonElement n in nets.EnumerateArray()) {
            string labels = $"surface={surface},network={num(n, "id")}";
            if(n.TryGetProperty("logistic_robots", out JsonElement lr)) {
                outp.Add(new Sample(saveId, "robots.waiting_for_charge", labels, ts, num(lr, "waiting_for_charge")));
                outp.Add(new Sample(saveId, "robots.working", labels, ts, num(lr, "working")));
            }
        }
    }

    private static void extractResources(string saveId, long ts, JsonElement root, List<Sample> outp) {
        string surface = str(root, "surface");
        if(!root.TryGetProperty("resources", out JsonElement res)) return;
        foreach(JsonProperty p in res.EnumerateObject()) {
            string labels = $"surface={surface},resource={p.Name}";
            outp.Add(new Sample(saveId, "resource.rate_current", labels, ts, num(p.Value, "rate_current")));
            if(p.Value.TryGetProperty("depletion_seconds", out JsonElement d) && d.ValueKind == JsonValueKind.Number) {
                outp.Add(new Sample(saveId, "resource.depletion_seconds", labels, ts, d.GetDouble()));
            }
        }
    }

    /// <summary>
    /// Tankfuellstand je Fluid. Erst der Verlauf macht daraus eine Aussage:
    /// ein zu 80 % voller Tank ist gut, wenn er steigt, und ein Problem, wenn
    /// er seit einer Stunde faellt.
    /// </summary>
    private static void extractFluids(string saveId, long ts, JsonElement root, List<Sample> outp) {
        string surface = str(root, "surface");
        if(!root.TryGetProperty("fluids", out JsonElement fluids) || fluids.ValueKind != JsonValueKind.Object) return;
        foreach(JsonProperty p in fluids.EnumerateObject()) {
            string labels = $"surface={surface},fluid={p.Name}";
            outp.Add(new Sample(saveId, "fluid.amount", labels, ts, num(p.Value, "amount")));
            outp.Add(new Sample(saveId, "fluid.fill", labels, ts, num(p.Value, "fill")));
        }
    }

    /// <summary>
    /// Kistenbestand je Item — dieselbe Ueberlegung wie bei den Tanks: der
    /// Fuellstand allein sagt nichts, die Richtung schon.
    /// </summary>
    private static void extractContainers(string saveId, long ts, JsonElement root, List<Sample> outp) {
        string surface = str(root, "surface");
        if(!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Object) return;
        foreach(JsonProperty p in items.EnumerateObject()) {
            if(p.Value.ValueKind != JsonValueKind.Number) continue;
            outp.Add(new Sample(saveId, "storage.item_count",
                $"surface={surface},item={p.Name}", ts, p.Value.GetDouble()));
        }
    }

    private static string str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double num(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
