using System.Text.Json;

namespace Fdash.Analysis;

/// <summary>Ein Knoten der Mangelkette unter einem Item.</summary>
public sealed record ShortageNode {
    public string Item { get; init; } = "";
    public double Amount { get; init; }
    /// <summary>Fehlt selbst nichts -> hier bricht die Kette ab, das ist die Ursache.</summary>
    public bool IsRoot { get; init; }
    public List<ShortageNode> Children { get; init; } = new List<ShortageNode>();
}

/// <summary>Eine Wurzel-Ursache mit ihrer Wirkung auf die Fabrik.</summary>
public sealed record RootCause {
    public string Item { get; init; } = "";
    /// <summary>Aufsummierte Fehlmenge ueber alle Abnehmer.</summary>
    public double Amount { get; init; }
    /// <summary>Items, die direkt oder transitiv daran haengen.</summary>
    public List<string> BlockedItems { get; init; } = new List<string>();
    /// <summary>Hungernde Maschinen in diesen Gruppen.</summary>
    public int Machines { get; init; }
    /// <summary>Status der Maschinen, die die Wurzel selbst fertigen.</summary>
    public Dictionary<string, int> OwnStatus { get; init; } = new Dictionary<string, int>();
    /// <summary>Wird die Wurzel auf dieser Oberflaeche ueberhaupt gefertigt?</summary>
    public bool Produced { get; init; }
}

/// <summary>
/// Engpass-Wurzelanalyse. Portiert aus <c>web/src/lib/shortages.ts</c> — dieselbe
/// Rechnung, nur serverseitig, damit MCP-Tools und Dashboard dasselbe Ergebnis
/// sehen und die Logik nicht in zwei Sprachen auseinanderlaeuft.
///
/// Das Backend liefert pro produziertem Item nur die direkt fehlenden Zutaten;
/// die Kette entsteht hier, weil jedes Zwischenprodukt selbst eine Gruppe mit
/// eigener missing-Map ist.
/// </summary>
public static class RootCauseAnalyzer {
    /// <summary>Item -&gt; { fehlende Zutat: Fehlmenge }.</summary>
    public sealed class MissingMap : Dictionary<string, Dictionary<string, double>> {
        public MissingMap() : base(StringComparer.Ordinal) { }
    }

    private const int DefaultDepth = 12;

    public static MissingMap BuildMissingMap(JsonElement assemblers) {
        MissingMap map = new MissingMap();
        foreach(JsonProperty g in Json.Object(assemblers, "by_item")) {
            Dictionary<string, double> missing = Json.NumMap(g.Value, "missing");
            if(missing.Count > 0) map[g.Name] = missing;
        }
        return map;
    }

    /// <summary>Fehlt dem Item selbst etwas? Wenn nein, ist es das Ende der Kette.</summary>
    public static bool IsRootCause(string item, MissingMap map) {
        if(!map.TryGetValue(item, out Dictionary<string, double>? own)) return true;
        foreach(double v in own.Values) {
            if(v > 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Kette unter <paramref name="item"/>. <paramref name="path"/> bricht Kreise
    /// (Recycling und Nebenprodukte erzeugen echte Zyklen), <paramref name="depth"/>
    /// deckelt die Baumgroesse.
    /// </summary>
    public static List<ShortageNode> BuildShortageTree(string item, MissingMap map,
            int depth = DefaultDepth, List<string>? path = null) {
        List<ShortageNode> result = new List<ShortageNode>();
        path ??= new List<string>();
        if(depth <= 0 || path.Contains(item)) return result;
        if(!map.TryGetValue(item, out Dictionary<string, double>? own)) return result;

        List<string> next = new List<string>(path) { item };
        foreach(KeyValuePair<string, double> kv in sorted(own)) {
            result.Add(new ShortageNode {
                Item = kv.Key,
                Amount = kv.Value,
                IsRoot = IsRootCause(kv.Key, map),
                Children = BuildShortageTree(kv.Key, map, depth - 1, next)
            });
        }
        return result;
    }

    /// <summary>
    /// Wurzel-Ursachen nach Wirkung sortiert: erst die, die die meisten Maschinen
    /// lahmlegen.
    /// </summary>
    public static List<RootCause> ComputeRootCauses(JsonElement assemblers) {
        MissingMap map = BuildMissingMap(assemblers);

        Dictionary<string, JsonElement> all = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach(JsonProperty g in Json.Object(assemblers, "by_item")) all[g.Name] = g.Value;

        Dictionary<string, Acc> acc = new Dictionary<string, Acc>(StringComparer.Ordinal);
        foreach(KeyValuePair<string, JsonElement> group in all) {
            Dictionary<string, double> roots = new Dictionary<string, double>(StringComparer.Ordinal);
            collectRoots(group.Key, map, roots, new HashSet<string>(StringComparer.Ordinal), DefaultDepth);
            int starving = Json.Int(group.Value, "starving");

            foreach(KeyValuePair<string, double> root in roots) {
                if(!acc.TryGetValue(root.Key, out Acc? rc)) {
                    bool produced = all.TryGetValue(root.Key, out JsonElement own);
                    rc = new Acc {
                        Item = root.Key,
                        OwnStatus = produced ? Json.IntMap(own, "status") : new Dictionary<string, int>(),
                        Produced = produced
                    };
                    acc[root.Key] = rc;
                }
                rc.Amount += root.Value;
                rc.BlockedItems.Add(group.Key);
                rc.Machines += starving;
            }
        }

        List<RootCause> result = new List<RootCause>();
        foreach(Acc a in acc.Values) {
            a.BlockedItems.Sort(StringComparer.Ordinal);
            result.Add(new RootCause {
                Item = a.Item, Amount = a.Amount, BlockedItems = a.BlockedItems,
                Machines = a.Machines, OwnStatus = a.OwnStatus, Produced = a.Produced
            });
        }

        // Wie im Frontend: Maschinen, dann Anzahl blockierter Items, dann Fehlmenge.
        // Der Name als letztes Kriterium ist neu — die Reihenfolge eines
        // Dictionary ist in C# nicht garantiert, und eine stabile Ausgabe ist
        // fuer Tests und fuer Diffs zwischen zwei Abrufen mehr wert.
        result.Sort((a, b) => {
            int c = b.Machines.CompareTo(a.Machines);
            if(c != 0) return c;
            c = b.BlockedItems.Count.CompareTo(a.BlockedItems.Count);
            if(c != 0) return c;
            c = b.Amount.CompareTo(a.Amount);
            if(c != 0) return c;
            return string.CompareOrdinal(a.Item, b.Item);
        });
        return result;
    }

    /// <summary>
    /// Alle Wurzeln, an denen <paramref name="item"/> transitiv haengt. Anders als
    /// <see cref="BuildShortageTree"/> wird hier ein seen-Set statt eines Pfads
    /// gefuehrt: das bricht nicht nur Kreise, sondern besucht jeden Knoten nur
    /// einmal. Ohne das waere eine rautenfoermige Rezeptkette exponentiell.
    /// </summary>
    private static void collectRoots(string item, MissingMap map, Dictionary<string, double> outp,
            HashSet<string> seen, int depth) {
        if(depth <= 0 || !seen.Add(item)) return;
        if(!map.TryGetValue(item, out Dictionary<string, double>? own)) return;
        foreach(KeyValuePair<string, double> kv in own) {
            if(kv.Value <= 0) continue;
            if(IsRootCause(kv.Key, map)) {
                outp[kv.Key] = (outp.TryGetValue(kv.Key, out double v) ? v : 0) + kv.Value;
            } else {
                collectRoots(kv.Key, map, outp, seen, depth - 1);
            }
        }
    }

    /// <summary>Fehlmengen absteigend, bei Gleichstand nach Name — deterministisch.</summary>
    private static IEnumerable<KeyValuePair<string, double>> sorted(Dictionary<string, double> map) {
        List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>();
        foreach(KeyValuePair<string, double> kv in map) {
            if(kv.Value > 0) list.Add(kv);
        }
        list.Sort((a, b) => {
            int c = b.Value.CompareTo(a.Value);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return list;
    }

    private sealed class Acc {
        public string Item = "";
        public double Amount;
        public List<string> BlockedItems = new List<string>();
        public int Machines;
        public Dictionary<string, int> OwnStatus = new Dictionary<string, int>();
        public bool Produced;
    }
}
