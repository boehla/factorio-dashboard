using System.Text.Json;
using Fdash.Collector;
using Microsoft.Extensions.Options;

namespace Fdash.Analysis;

/// <summary>
/// Ein erkanntes Problem. <paramref name="Severity"/> ist 0..1 und dient nur der
/// Rangfolge — die Begruendung steht im Detail, damit ein Leser (Mensch oder
/// Modell) die Einordnung nachvollziehen kann statt einer Zahl glauben zu muessen.
/// </summary>
public sealed record Problem {
    public string Domain { get; init; } = "";
    public double Severity { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? Suggestion { get; init; }
    public string? Surface { get; init; }
    public List<string> Items { get; init; } = new List<string>();
}

/// <summary>
/// Sammelt die Probleme aller Domaenen und sortiert sie nach Schwere.
///
/// Das ist die Antwort auf "wo hakt es gerade" in einem Aufruf. Bisher musste
/// man sich das aus vier Panels zusammenreimen; die Wurzelanalyse gab es
/// ueberhaupt nur im Frontend.
///
/// Laeuft im Collector nach jedem Snapshot und wird als abgeleiteter Job
/// <c>problems</c> veroeffentlicht — Dashboard und MCP sehen damit garantiert
/// dieselbe Bewertung.
/// </summary>
public sealed class ProblemAnalyzer {
    private readonly SnapshotView view;
    private readonly CollectorOptions options;

    /// <summary>Pro Domaene und Oberflaeche nicht mehr als das melden.</summary>
    private const int MaxPerDomain = 10;

    public ProblemAnalyzer(SnapshotView view, IOptions<CollectorOptions> options) {
        this.view = view;
        this.options = options.Value;
    }

    public List<Problem> Analyze() {
        List<Problem> problems = new List<Problem>();

        IReadOnlyList<string> surfaces = view.Surfaces;
        if(surfaces.Count == 0) surfaces = new[] { view.PrimarySurface };

        foreach(string surface in surfaces) {
            shortages(surface, problems);
            power(surface, problems);
            resources(surface, problems);
            robots(surface, problems);
        }

        stalls(problems);
        trains(problems);
        platforms(problems);
        research(problems);

        problems.Sort((a, b) => {
            int c = b.Severity.CompareTo(a.Severity);
            return c != 0 ? c : string.CompareOrdinal(a.Title, b.Title);
        });
        return problems;
    }

    // ------------------------------------------------------------- Engpaesse

    private void shortages(string surface, List<Problem> outp) {
        JobPayload? asm = view.Get("assemblers", surface);
        if(asm == null) return;

        int totalMachines = 0, totalStarving = 0;
        foreach(JsonProperty g in Json.Object(asm.Data, "by_item")) {
            totalMachines += Json.Int(g.Value, "total");
            totalStarving += Json.Int(g.Value, "starving");
        }
        if(totalMachines == 0) return;

        List<RootCause> roots = RootCauseAnalyzer.ComputeRootCauses(asm.Data);
        int emitted = 0;
        foreach(RootCause r in roots) {
            if(r.Machines <= 0) continue;
            // Auf einer Py-Basis stehen leicht 40 Wurzeln in der Liste. Die
            // Rangfolge steht schon, alles darunter ist Rauschen — und die
            // Liste geht per SignalR auch ins Dashboard.
            if(++emitted > MaxPerDomain) break;
            // Gemessen wird am Anteil der HUNGERNDEN Maschinen, nicht an allen:
            // auf einer grossen Basis sind 40 stehende Maschinen ein Prozent des
            // Bestands, aber womoeglich der halbe aktuelle Aerger. Am Gesamtbestand
            // gemessen landete jeder Engpass am Ende der Liste — genau unter dem,
            // was man eigentlich reparieren muesste.
            double share = r.Machines / (double)Math.Max(1, totalStarving);
            double sev = clamp(0.3 + 0.5 * share, 0.3, 0.9);
            string ownStatus = r.Produced ? topStatus(r.OwnStatus) : "wird hier nicht gefertigt";
            outp.Add(new Problem {
                Domain = "shortage",
                Severity = sev,
                Surface = surface,
                Title = $"{r.Item} fehlt",
                Detail = $"{r.Machines} hungernde Maschinen in {r.BlockedItems.Count} Gruppen haengen daran "
                    + $"(Fehlmenge {r.Amount:0.#}); Zustand der Wurzel: {ownStatus}",
                Suggestion = r.Produced
                    ? $"Produktion von {r.Item} erhoehen oder Zufuhr pruefen"
                    : $"{r.Item} wird auf {surface} nicht gefertigt — Import oder eigene Fertigung noetig",
                Items = new List<string>(r.BlockedItems)
            });
        }
    }

    private void stalls(List<Problem> outp) {
        JobPayload? stall = view.Get("stall");
        if(stall == null) return;
        foreach(JsonElement s in Json.Array(stall.Data, "stalled")) {
            int seconds = Json.Int(s, "since_seconds");
            string item = Json.Str(s, "item");
            outp.Add(new Problem {
                Domain = "machines",
                Severity = clamp(seconds / (double)Math.Max(1, options.StallSecondsWarn) * 0.6, 0.2, 1),
                Title = $"{item} steht still",
                Detail = $"keine Produktion seit {seconds}s, Grund {Json.Str(s, "reason")}, "
                    + $"{Json.Int(s, "machines_affected")} Maschinen betroffen",
                Suggestion = "Zutatenzufuhr oder Strom an dieser Stufe pruefen",
                Items = new List<string> { item }
            });
        }
    }

    // ----------------------------------------------------------------- Strom

    private void power(string surface, List<Problem> outp) {
        JobPayload? p = view.Get("power", surface);
        if(p == null) return;

        foreach(JsonElement net in Json.Array(p.Data, "networks")) {
            int id = Json.Int(net, "id");
            double satisfaction = Json.Num(net, "satisfaction", 1);
            double consumption = Json.Num(net, "consumption");
            // Ein Netz ohne Verbrauch ist nicht unterversorgt, sondern leer.
            if(consumption > 0 && satisfaction < options.PowerSatisfactionWarn) {
                outp.Add(new Problem {
                    Domain = "power",
                    Severity = clamp(1 - satisfaction, 0.3, 1),
                    Surface = surface,
                    Title = $"Netz {id}: Strom reicht nicht ({satisfaction * 100:0}%)",
                    Detail = $"Bedarf {mw(consumption)} MW, Erzeugung {mw(Json.Num(net, "production"))} MW, "
                        + $"Nennleistung {mw(Json.Num(net, "capacity"))} MW",
                    Suggestion = "Erzeugung ausbauen oder Verbraucher abschalten"
                });
            }

            JsonElement acc = Json.Sub(net, "accumulators");
            if(acc.ValueKind != JsonValueKind.Object) continue;
            double capacity = Json.Num(acc, "capacity");
            if(capacity <= 0) continue;
            double charge = Json.Num(acc, "energy") / capacity;
            double discharge = Json.Num(acc, "discharge_rate");
            double charging = Json.Num(acc, "charge_rate");
            // Fallende Ladung ist der Fruehindikator: die Satisfaction kann noch
            // 1.0 sein, waehrend die Akkus seit Stunden leerer werden.
            if(charge < 0.5 && discharge > charging) {
                outp.Add(new Problem {
                    Domain = "power",
                    Severity = clamp(0.9 - charge, 0.2, 0.9),
                    Surface = surface,
                    Title = $"Netz {id}: Akkus entladen sich ({charge * 100:0}%)",
                    Detail = $"Entladung {mw(discharge)} MW gegen Ladung {mw(charging)} MW bei "
                        + $"{Json.Int(acc, "count")} Akkus",
                    Suggestion = "Grundlast-Erzeugung ausbauen — die Akkus puffern das nur noch"
                });
            }
        }
    }

    // ------------------------------------------------------------ Ressourcen

    private void resources(string surface, List<Problem> outp) {
        JobPayload? res = view.Get("resources", surface);
        if(res == null) return;

        List<Problem> idle = new List<Problem>();
        List<(Problem P, double Lost)> ranked = new List<(Problem, double)>();

        foreach(JsonProperty r in Json.Object(res.Data, "resources")) {
            double? depletion = Json.NumOrNull(r.Value, "depletion_seconds");
            if(depletion is double d && d > 0 && d < 12 * 3600) {
                outp.Add(new Problem {
                    Domain = "resources",
                    Severity = clamp(1 - d / (12 * 3600), 0.2, 0.95),
                    Surface = surface,
                    Title = $"{r.Name} geht zur Neige",
                    Detail = $"noch ~{d / 3600:0.#} h bei {Json.Num(r.Value, "rate_current"):0}/min",
                    Suggestion = "neues Feld erschliessen, bevor die Fertigung darauf wartet",
                    Items = new List<string> { r.Name }
                });
            }

            JsonElement drills = Json.Sub(r.Value, "drills");
            int total = Json.Int(drills, "total");
            int working = Json.Int(drills, "working");
            double rateMax = Json.Num(r.Value, "rate_max");
            double rateNow = Json.Num(r.Value, "rate_current");
            if(total < 5 || rateMax <= 0 || working >= total * 0.5) continue;

            // Stehende Bohrer sind fuer sich genommen kein Fehler: ein voller
            // Erzpuffer haelt sie voellig zu Recht an. Deshalb nur ein Hinweis
            // mittlerer Schwere — ob das Erz downstream wirklich gebraucht wird,
            // laesst sich ohne Pufferstaende (Mod 0.2.0) nicht sagen.
            ranked.Add((new Problem {
                Domain = "resources",
                Severity = clamp(0.25 + 0.35 * (1 - working / (double)total), 0.25, 0.6),
                Surface = surface,
                Title = $"{r.Name}: {total - working} von {total} Bohrern stehen",
                Detail = $"Foerderung {rateNow:0}/min von moeglichen {rateMax:0}/min",
                Suggestion = "Abtransport oder Strom pruefen — oder der Abnehmer braucht das Erz gerade nicht",
                Items = new List<string> { r.Name }
            }, rateMax - rateNow));
        }

        // Nur die drei groessten Ausfaelle: sonst besteht die halbe Rangliste aus
        // Erzen, die gerade schlicht niemand abnimmt.
        ranked.Sort((a, b) => b.Lost.CompareTo(a.Lost));
        foreach((Problem p, double _) in ranked.Take(3)) idle.Add(p);
        outp.AddRange(idle);
    }

    // -------------------------------------------------------------- Roboter

    private void robots(string surface, List<Problem> outp) {
        JobPayload? log = view.Get("logistics", surface);
        if(log == null) return;

        foreach(JsonElement net in Json.Array(log.Data, "networks")) {
            JsonElement bots = Json.Sub(net, "logistic_robots");
            int total = Json.Int(bots, "total");
            int waiting = Json.Int(bots, "waiting_for_charge");
            if(total < 20 || waiting <= total * 0.1) continue;
            outp.Add(new Problem {
                Domain = "logistics",
                Severity = clamp(waiting / (double)total, 0.15, 0.7),
                Surface = surface,
                Title = $"Netz {Json.Int(net, "id")}: {waiting} Roboter warten auf Ladung",
                Detail = $"{waiting} von {total} Logistikrobotern stehen in der Warteschlange, "
                    + $"{Json.Int(net, "roboports")} Roboports im Netz",
                Suggestion = "mehr Roboports auf der Strecke — nicht mehr Roboter"
            });
        }
    }

    // ----------------------------------------------------------------- Zuege

    private void trains(List<Problem> outp) {
        JobPayload? t = view.Get("trains_derived");
        if(t == null) return;

        // Nach Zustand gruppieren: "12 Zuege ohne Weg" ist eine Meldung, nicht zwoelf.
        Dictionary<string, (int Count, int MaxStuck, List<string> Stations)> byState =
            new Dictionary<string, (int, int, List<string>)>(StringComparer.Ordinal);
        foreach(JsonElement p in Json.Array(t.Data, "problems")) {
            string state = Json.Str(p, "state");
            if(!byState.TryGetValue(state, out (int Count, int MaxStuck, List<string> Stations) g)) {
                g = (0, 0, new List<string>());
            }
            g.Count++;
            g.MaxStuck = Math.Max(g.MaxStuck, Json.Int(p, "stuck_seconds"));
            string station = Json.Str(p, "schedule_station");
            if(station.Length > 0 && !g.Stations.Contains(station)) g.Stations.Add(station);
            byState[state] = g;
        }

        foreach(KeyValuePair<string, (int Count, int MaxStuck, List<string> Stations)> kv in byState) {
            bool hard = kv.Key == "no_path" || kv.Key == "no_schedule" || kv.Key == "path_lost";
            double sev = hard ? 0.85 : clamp(kv.Value.MaxStuck / 600.0, 0.15, 0.7);
            outp.Add(new Problem {
                Domain = "trains",
                Severity = sev,
                Title = $"{kv.Value.Count} {(kv.Value.Count == 1 ? "Zug" : "Zuege")}: {kv.Key}",
                Detail = $"laengster Stillstand {kv.Value.MaxStuck}s"
                    + (kv.Value.Stations.Count > 0
                        ? $", Ziele: {string.Join(", ", kv.Value.Stations.Take(5))}" : ""),
                Suggestion = hard
                    ? "Schienenweg oder Fahrplan ist kaputt — das loest sich nicht von selbst"
                    : "Zielbahnhof ueberlastet: Ladekapazitaet oder Zuglimit pruefen"
            });
        }
    }

    // ----------------------------------------------------------- Plattformen

    private void platforms(List<Problem> outp) {
        JobPayload? pl = view.Get("platforms");
        if(pl == null) return;
        foreach(JsonElement p in Json.Array(pl.Data, "platforms")) {
            List<string> warnings = Json.StrList(p, "warnings");
            if(warnings.Count == 0) continue;
            outp.Add(new Problem {
                Domain = "platforms",
                Severity = 0.5,
                Title = $"Plattform {Json.Str(p, "name")}: {string.Join(", ", warnings)}",
                Detail = $"Zustand {Json.Str(p, "state")}, Position {Json.Str(p, "location")}",
                Suggestion = "Treibstoff-Nachschub der Plattform pruefen"
            });
        }
    }

    // ------------------------------------------------------------ Forschung

    private void research(List<Problem> outp) {
        JobPayload? rs = view.Get("research_state", view.PrimarySurface);
        if(rs == null) return;

        int queue = Json.Int(rs.Data, "queue_len");
        int candidates = 0;
        foreach(JsonElement _ in Json.Array(rs.Data, "candidates")) candidates++;
        if(queue == 0 && candidates > 0) {
            outp.Add(new Problem {
                Domain = "research",
                Severity = 0.4,
                Title = "Keine Forschung aktiv",
                Detail = $"{candidates} Technologien waeren forschbar, die Warteschlange ist leer",
                Suggestion = "Forschung setzen — die Labore stehen sonst nur herum"
            });
        }

        int labs = Json.Int(rs.Data, "active_labs");
        if(queue > 0 && labs == 0) {
            outp.Add(new Problem {
                Domain = "research",
                Severity = 0.5,
                Title = "Forschung laeuft, aber kein Labor arbeitet",
                Detail = "aktive Labore: 0",
                Suggestion = "Wissenschaftspakete oder Strom an den Laboren fehlen"
            });
        }
    }

    // ----------------------------------------------------------------- Hilfen

    private static double clamp(double v, double min, double max) => Math.Min(max, Math.Max(min, v));

    private static double mw(double watt) => Math.Round(watt / 1_000_000, 1);

    /// <summary>Haeufigster Status eines Histogramms — die kurze Begruendung.</summary>
    private static string topStatus(Dictionary<string, int> status) {
        string best = "unbekannt";
        int max = -1;
        foreach(KeyValuePair<string, int> kv in status) {
            if(kv.Value > max) { max = kv.Value; best = kv.Key; }
        }
        return max <= 0 ? "unbekannt" : $"{best} ({max})";
    }
}
