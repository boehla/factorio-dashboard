namespace Fdash.Analysis;

/// <summary>Was der Name einer Station ueber sie verraet.</summary>
public sealed record StationLabel {
    /// <summary>Das gehandelte Gut, oder null wenn der Name keines nennt.</summary>
    public string? Item { get; init; }

    /// <summary>"item" | "fluid" | "recipe" | "virtual-signal" | ... — aus dem Rich-Text-Tag.</summary>
    public string Type { get; init; } = "item";

    /// <summary>Liefernde Station: der Name traegt [virtual-signal=signal-output].</summary>
    public bool Provides { get; init; }

    /// <summary>Abnehmende Station: [virtual-signal=signal-input].</summary>
    public bool Requests { get; init; }

    /// <summary>Weder Liefer- noch Abnehmestation — Depot, Reparaturposten, Fahrplanpunkt.</summary>
    public bool HasRole => Provides || Requests;
}

/// <summary>
/// Liest Stationsnamen als Rich Text.
///
/// Der Mod kann das nicht liefern: Factorio kennt kein "diese Station liefert
/// Eisen". Was ein Zugnetz fuehrt, steht ausschliesslich in den Namen — und weil
/// die Namen von einem Blueprint-Schema kommen, sind sie maschinenlesbar:
/// <c>[item=iron-plate][virtual-signal=signal-output]</c> liefert,
/// <c>[fluid=steam][virtual-signal=signal-input]</c> nimmt ab.
///
/// Alles ausser den beiden Rollensignalen ist Beiwerk: <c>[item=ash]O</c>
/// unterscheidet zwei Aschesorten, <c>[fluid=steam]150°C</c> die Temperatur.
/// Fuer die Frage "fuehrt das Netz Dampf?" zaehlt nur das erste echte Tag.
/// </summary>
public static class StationNames {
    private const string outputSignal = "signal-output";
    private const string inputSignal = "signal-input";

    /// <summary>Tags, die nie das gehandelte Gut bezeichnen, sondern nur Darstellung.</summary>
    private static readonly HashSet<string> decorative = new HashSet<string>(StringComparer.Ordinal) {
        "color", "font", "img", "gps", "tooltip", "quality", "planet", "space-location",
        "train", "train-stop", "space-platform"
    };

    public static StationLabel Parse(string name) {
        string? item = null;
        string type = "item";
        bool provides = false, requests = false;

        int i = 0;
        while(i < name.Length) {
            int open = name.IndexOf('[', i);
            if(open < 0) break;
            int close = name.IndexOf(']', open + 1);
            if(close < 0) break;
            i = close + 1;

            int eq = name.IndexOf('=', open + 1);
            if(eq < 0 || eq > close) continue;

            string tagType = name.Substring(open + 1, eq - open - 1);
            string tagName = name.Substring(eq + 1, close - eq - 1);
            // [item=iron-plate,quality=rare] — die Qualitaet haengt am Namen.
            int comma = tagName.IndexOf(',');
            if(comma >= 0) tagName = tagName.Substring(0, comma);
            if(tagType.Length == 0 || tagName.Length == 0) continue;

            if(tagType == "virtual-signal" && tagName == outputSignal) { provides = true; continue; }
            if(tagType == "virtual-signal" && tagName == inputSignal) { requests = true; continue; }

            // Das erste nicht-dekorative Tag ist das Gut. Auch ein virtuelles
            // Signal zaehlt: [virtual-signal=signal-fire] ist in diesem Netz eine
            // eigene Ware, kein Rollenmarker.
            if(item == null && !decorative.Contains(tagType)) {
                item = tagName;
                type = tagType;
            }
        }

        return new StationLabel { Item = item, Type = type, Provides = provides, Requests = requests };
    }
}
