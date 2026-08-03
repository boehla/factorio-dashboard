using System.Text.Json;
using Fdash.Core;

namespace Fdash.Analysis;

public enum TechStatus { Unknown, Researched, Available, Blocked }

/// <summary>
/// Haelt die zuletzt gemeldete Liste der erforschten Technologien fest.
///
/// Der Mod schickt sie nicht in jedem Snapshot mit — auf Pyanodons sind das
/// knapp tausend Namen, und sie aendert sich nur, wenn eine Forschung fertig
/// wird. Fuer die Frage "was fehlt bis Technologie X" braucht man sie aber bei
/// jedem Aufruf. Gleiches Muster wie <see cref="TrainWatcher"/>: Zustand, den
/// der Mod bewusst nicht fuehrt (alles Gespeicherte landet im Savegame), liegt
/// auf der Serverseite.
/// </summary>
public sealed class TechLedger {
    private readonly object gate = new object();
    private HashSet<string>? researched;
    private string saveId = "";

    /// <summary>
    /// Nimmt die Liste aus einem research_state-Payload an, wenn eine dabei ist.
    /// Payloads ohne <c>researched</c> lassen den gemerkten Stand unveraendert.
    /// </summary>
    public void Observe(JsonElement researchState, string saveId) {
        if(!researchState.TryGetProperty("researched", out JsonElement arr)
            || arr.ValueKind != JsonValueKind.Array) return;

        HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
        foreach(JsonElement e in arr.EnumerateArray()) {
            string? s = e.GetString();
            if(!string.IsNullOrEmpty(s)) set.Add(s);
        }
        lock(gate) {
            this.researched = set;
            this.saveId = saveId;
        }
    }

    /// <summary>
    /// Der gemerkte Stand, oder null wenn noch keine Liste kam. An die Save-Id
    /// gebunden: nach einem Savewechsel ist die alte Liste schlicht falsch.
    /// </summary>
    public IReadOnlySet<string>? Researched(string saveId) {
        lock(gate) {
            return researched != null && this.saveId == saveId ? researched : null;
        }
    }
}

/// <summary>
/// Der Technologiebaum aus dem Prototyp-Export, verbunden mit dem Laufzeitstand
/// aus <c>research_state</c>.
///
/// Der Laufzeit-Job meldet nur die forschbaren Technologien und — gedeckelt —
/// die, denen genau eine Voraussetzung fehlt. Auf Pyanodons haengen tausende an
/// fehlenden Voraussetzungen, eine vollstaendige Liste waere weder lesbar noch
/// bezahlbar. Die Frage "was fehlt mir bis X" ist aber genau die interessante,
/// und die laesst sich fuer ein einzelnes Ziel exakt beantworten, sobald man
/// den Prototypgraphen dazunimmt.
///
/// Wichtig fuer Overhaul-Mods: die exportierten Voraussetzungen sind die
/// Laufzeit-Prototypen, also inklusive der Abhaengigkeiten, die pypostprocessing
/// erst zur Data-Stage aus den Rezeptzutaten erzeugt. Was im Lua-Quelltext einer
/// Technologie steht, ist nicht dasselbe.
/// </summary>
public sealed class TechGraph {
    private readonly IReadOnlyDictionary<string, TechProto> techs;
    private readonly HashSet<string> candidates = new HashSet<string>(StringComparer.Ordinal);
    private readonly IReadOnlySet<string>? reported;
    private readonly Dictionary<string, bool> cache = new Dictionary<string, bool>(StringComparer.Ordinal);

    public TechGraph(IReadOnlyDictionary<string, TechProto> technologies, JsonElement researchState,
            IReadOnlySet<string>? reportedResearched = null) {
        techs = technologies;
        reported = reportedResearched;
        foreach(JsonElement c in Json.Array(researchState, "candidates")) {
            string name = Json.Str(c, "name");
            if(name.Length > 0) candidates.Add(name);
        }
    }

    /// <summary>Woher der Erforscht-Stand kommt — gemeldet oder hergeleitet.</summary>
    public string Source => reported != null ? "reported" : "derived";

    public bool Knows(string name) => techs.ContainsKey(name);

    public bool IsResearched(string name) {
        if(reported != null) return reported.Contains(name);
        return derived(name, new HashSet<string>(StringComparer.Ordinal));
    }

    public TechStatus StatusOf(string name) {
        if(!techs.ContainsKey(name)) return TechStatus.Unknown;
        if(IsResearched(name)) return TechStatus.Researched;
        return candidates.Contains(name) ? TechStatus.Available : TechStatus.Blocked;
    }

    /// <summary>Die direkten Voraussetzungen, die noch fehlen.</summary>
    public List<string> MissingPrerequisites(string name) {
        List<string> missing = new List<string>();
        if(!techs.TryGetValue(name, out TechProto? t)) return missing;
        foreach(string pre in t.Prerequisites) {
            if(!IsResearched(pre)) missing.Add(pre);
        }
        return missing;
    }

    /// <summary>
    /// Alle noch nicht erforschten Technologien bis einschliesslich
    /// <paramref name="target"/>, in einer Reihenfolge, in der jede
    /// Voraussetzung vor ihrem Nutzer steht. Leer, wenn das Ziel schon
    /// erforscht ist oder gar nicht existiert.
    /// </summary>
    public List<string> ResearchPath(string target) {
        List<string> order = new List<string>();
        HashSet<string> done = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> visiting = new HashSet<string>(StringComparer.Ordinal);
        walk(target, order, done, visiting);
        return order;
    }

    private void walk(string name, List<string> order, HashSet<string> done, HashSet<string> visiting) {
        if(done.Contains(name)) return;
        if(!techs.TryGetValue(name, out TechProto? t)) return;
        if(IsResearched(name)) return;
        // Ein Zyklus im Baum ist nach einem Mod-Update moeglich. Hier abbrechen
        // statt endlos abzusteigen — der Rest des Pfades bleibt brauchbar.
        if(!visiting.Add(name)) return;
        foreach(string pre in t.Prerequisites) walk(pre, order, done, visiting);
        visiting.Remove(name);
        if(done.Add(name)) order.Add(name);
    }

    /// <summary>
    /// Erforscht, ohne dass es jemand gemeldet hat.
    ///
    /// Der Mod schickt in <c>candidates</c> genau die Technologien, die nicht
    /// erforscht sind und deren Voraussetzungen alle stehen. Umgekehrt gilt
    /// damit: eine Technologie ist erforscht, wenn sie kein Kandidat ist und
    /// alle ihre Voraussetzungen erforscht sind. Rekursiv aufgeloest, Ergebnis
    /// gemerkt.
    ///
    /// Einzige Unschaerfe: per Skript abgeschaltete Technologien
    /// (<c>enabled = false</c>) tauchen weder als Kandidat noch als blockiert
    /// auf und gelten hier faelschlich als erforscht. Deshalb hat die gemeldete
    /// Liste aus <see cref="TechLedger"/> Vorrang, sobald sie da ist.
    /// </summary>
    private bool derived(string name, HashSet<string> visiting) {
        if(cache.TryGetValue(name, out bool known)) return known;
        if(!techs.TryGetValue(name, out TechProto? t)) return false;
        if(candidates.Contains(name)) {
            cache[name] = false;
            return false;
        }
        if(!visiting.Add(name)) return false;

        bool all = true;
        foreach(string pre in t.Prerequisites) {
            if(!derived(pre, visiting)) {
                all = false;
                break;
            }
        }
        visiting.Remove(name);
        cache[name] = all;
        return all;
    }
}
