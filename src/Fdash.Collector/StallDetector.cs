using System.Text.Json;
using Fdash.Core;

namespace Fdash.Collector;

/// <summary>
/// Abgeleiteter Job (Plan §3.8, §5): korreliert production + assemblers.
/// Ein Item gilt als "stalled", wenn Produktion == 0, mindestens eine Maschine
/// mit dem Rezept existiert und deren Status ein Mangel ist (nicht output_full).
/// since_seconds fuehrt der Collector, weil es ohne Mod-storage keinen
/// Lua-Zustand zwischen Polls gibt.
/// </summary>
public sealed class StallDetector {
    private readonly Dictionary<string, long> stalledSince = new();
    private static readonly string[] StallReasons = {
        "no_ingredients", "no_power", "low_power", "no_fluid", "no_input_fluid"
    };

    public IReadOnlyList<StallItem> Detect(JsonElement production, JsonElement assemblers, double threshold, long now) {
        // Item -> aggregierter Mangel-Status aus assemblers (jetzt nach Produkt
        // gruppiert, daher matcht der Key direkt auf die Produktions-Items).
        Dictionary<string, (int affected, string reason)> problem = new();
        if(assemblers.TryGetProperty("by_item", out JsonElement recipes)) {
            foreach(JsonProperty p in recipes.EnumerateObject()) {
                if(!p.Value.TryGetProperty("status", out JsonElement st)) continue;
                foreach(JsonProperty s in st.EnumerateObject()) {
                    if(Array.IndexOf(StallReasons, s.Name) >= 0) {
                        int cnt = s.Value.GetInt32();
                        if(cnt > 0) problem[p.Name] = (cnt, s.Name);
                    }
                }
            }
        }

        List<StallItem> result = new List<StallItem>();
        HashSet<string> currentlyStalled = new();
        if(production.TryGetProperty("items", out JsonElement items)) {
            foreach(JsonElement it in items.EnumerateArray()) {
                string item = it.GetProperty("item").GetString() ?? "";
                double produced = it.TryGetProperty("produced_per_min", out JsonElement pr) ? pr.GetDouble() : 0;
                if(produced > threshold) continue;
                if(!problem.TryGetValue(item, out (int affected, string reason) info)) continue;

                currentlyStalled.Add(item);
                if(!stalledSince.ContainsKey(item)) stalledSince[item] = now;
                result.Add(new StallItem {
                    Item = item, Reason = info.reason, MachinesAffected = info.affected,
                    SinceSeconds = (int)(now - stalledSince[item])
                });
            }
        }
        // aufgeloeste Stalls vergessen
        foreach(string key in stalledSince.Keys.ToList()) {
            if(!currentlyStalled.Contains(key)) stalledSince.Remove(key);
        }
        return result;
    }
}
