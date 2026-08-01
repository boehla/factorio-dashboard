using System.Text.Json;

namespace Fdash.Analysis;

/// <summary>
/// Defensive Lesehilfen fuer die Mod-Payloads. Alles hier liefert einen
/// Standardwert statt zu werfen: die Payloads kommen aus Lua, ein fehlendes
/// Feld ist dort der Normalfall (ein Job veroeffentlicht erst am Ende eines
/// Durchlaufs, optionale Felder werden bewusst weggelassen).
/// </summary>
public static class Json {
    public static string Str(JsonElement e, string prop, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public static double Num(JsonElement e, string prop, double fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v)
            && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    public static double? NumOrNull(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v)
            && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static int Int(JsonElement e, string prop, int fallback = 0) =>
        (int)Num(e, prop, fallback);

    public static bool Bool(JsonElement e, string prop, bool fallback = false) {
        if(e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out JsonElement v)) return fallback;
        return v.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    /// <summary>Array-Elemente; leere Folge, wenn das Feld fehlt oder kein Array ist.</summary>
    public static IEnumerable<JsonElement> Array(JsonElement e, string prop) {
        if(e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out JsonElement v)) yield break;
        if(v.ValueKind != JsonValueKind.Array) yield break;
        foreach(JsonElement item in v.EnumerateArray()) yield return item;
    }

    /// <summary>Objekt-Eigenschaften; leere Folge, wenn das Feld fehlt oder kein Objekt ist.</summary>
    public static IEnumerable<JsonProperty> Object(JsonElement e, string prop) {
        if(e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out JsonElement v)) yield break;
        if(v.ValueKind != JsonValueKind.Object) yield break;
        foreach(JsonProperty p in v.EnumerateObject()) yield return p;
    }

    /// <summary>Ein Unterobjekt oder <c>default</c> (ValueKind == Undefined).</summary>
    public static JsonElement Sub(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) ? v : default;

    /// <summary>name -&gt; Zahl, z. B. Status-Histogramme und Fehlmengen.</summary>
    public static Dictionary<string, double> NumMap(JsonElement e, string prop) {
        Dictionary<string, double> map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach(JsonProperty p in Object(e, prop)) {
            if(p.Value.ValueKind == JsonValueKind.Number) map[p.Name] = p.Value.GetDouble();
        }
        return map;
    }

    public static Dictionary<string, int> IntMap(JsonElement e, string prop) {
        Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach(JsonProperty p in Object(e, prop)) {
            if(p.Value.ValueKind == JsonValueKind.Number) map[p.Name] = (int)p.Value.GetDouble();
        }
        return map;
    }

    public static List<string> StrList(JsonElement e, string prop) {
        List<string> list = new List<string>();
        foreach(JsonElement item in Array(e, prop)) {
            if(item.ValueKind == JsonValueKind.String) {
                string? s = item.GetString();
                if(s != null) list.Add(s);
            }
        }
        return list;
    }
}
