using System.Text.Json;

namespace Fdash.Collector;

/// <summary>
/// Liest die Icon-Pfade aus einem <c>data-raw-dump.json</c> (erzeugt von
/// <c>factorio --dump-data</c>). Die Runtime-API kennt keine Icon-Pfade
/// (LuaItemPrototype hat keinen Key "icon"/"icons", getestet mit 2.0.77),
/// deshalb ist der Data-Stage-Dump die einzige verlaessliche Quelle fuer die
/// Zuordnung Prototyp-Name -&gt; PNG-Datei.
///
/// Der Dump ist gross (dreistellige MB bei pY/Space-Age), daher wird er
/// streamend mit einem Utf8JsonReader ueber einen gleitenden Puffer gelesen und
/// nur "name -&gt; __mod__/pfad.png" behalten.
/// </summary>
public static class DataRawIcons {
    // Prototyp-Typen, die im Spiel Items sind. Deren Icons haben Vorrang vor
    // gleichnamigen Rezepten/Entities (Name-Kollisionen sind haeufig).
    private static readonly HashSet<string> PrimaryTypes = new HashSet<string>(StringComparer.Ordinal) {
        "item", "ammo", "armor", "blueprint", "blueprint-book", "capsule",
        "copy-paste-tool", "deconstruction-item", "gun", "item-with-entity-data",
        "item-with-inventory", "item-with-label", "item-with-tags", "mining-tool",
        "module", "rail-planner", "repair-tool", "selection-tool",
        "space-platform-starter-pack", "spidertron-remote", "tool", "upgrade-item",
        "fluid"
    };

    /// <summary>Name -&gt; Icon-Pfad in Factorio-Notation (<c>__mod__/pfad.png</c>).</summary>
    public static Dictionary<string, string> Parse(string path) {
        Dictionary<string, string> primary = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> secondary = new Dictionary<string, string>(StringComparer.Ordinal);

        // Verschachtelungstiefe selbst mitzaehlen statt reader.CurrentDepth zu
        // deuten: 1 = Prototyp-Typ, 2 = Prototyp-Name, >=3 = Felder.
        int depth = 0;
        string? category = null;
        string? proto = null;
        string? field = null;
        bool iconTaken = false; // erster "icon"-String pro Prototyp gewinnt (= Layer 1)

        byte[] buffer = new byte[128 * 1024];
        int used = 0;
        bool final = false;
        JsonReaderState state = default;
        using(FileStream fs = File.OpenRead(path)) {
            while(!final) {
                int read = fs.Read(buffer, used, buffer.Length - used);
                used += read;
                final = read == 0;
                Utf8JsonReader reader = new Utf8JsonReader(buffer.AsSpan(0, used), final, state);
                while(reader.Read()) {
                    switch(reader.TokenType) {
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            depth++;
                            break;
                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            depth--;
                            break;
                        case JsonTokenType.PropertyName:
                            if(depth == 1) {
                                category = reader.GetString();
                            } else if(depth == 2) {
                                proto = reader.GetString();
                                iconTaken = false;
                            } else {
                                field = reader.GetString();
                            }
                            break;
                        case JsonTokenType.String:
                            if(field != "icon" || iconTaken || category == null || proto == null) break;
                            string? icon = reader.GetString();
                            if(string.IsNullOrEmpty(icon)) break;
                            iconTaken = true;
                            Dictionary<string, string> target =
                                PrimaryTypes.Contains(category) ? primary : secondary;
                            if(!target.ContainsKey(proto)) target[proto] = icon!;
                            break;
                    }
                }
                state = reader.CurrentState;
                int consumed = (int)reader.BytesConsumed;
                used -= consumed;
                if(used > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, used);
                // Ein einzelnes Token passt nicht in den Puffer -> vergroessern.
                if(used == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            }
        }

        foreach(KeyValuePair<string, string> kv in secondary) {
            if(!primary.ContainsKey(kv.Key)) primary[kv.Key] = kv.Value;
        }
        return primary;
    }
}
