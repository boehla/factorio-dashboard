using System.Text.Json;

namespace Fdash.Analysis;

public sealed record TrainCargo(string Name, double Count);

public sealed record StuckTrain {
    public long Id { get; init; }
    public string? Surface { get; init; }
    public string State { get; init; } = "";
    public string? ScheduleStation { get; init; }
    public List<TrainCargo> Cargo { get; init; } = new List<TrainCargo>();
    /// <summary>Wie lange der Zug schon in diesem Problemzustand steht.</summary>
    public int StuckSeconds { get; init; }
}

public sealed record TrainReport {
    public int Total { get; init; }
    public int Ok { get; init; }
    public int Problem { get; init; }
    public List<StuckTrain> Problems { get; init; } = new List<StuckTrain>();
}

/// <summary>
/// Fuehrt die Dauer, die ein Zug schon haengt. Der Mod kann das nicht liefern:
/// er haelt zwischen zwei Durchlaeufen keinen Zustand zu einzelnen Zuegen, und
/// genau das ist auch richtig so (jeder gespeicherte Zustand landet im
/// Savegame). Gleiches Muster wie <see cref="Fdash.Collector.StallDetector"/>.
///
/// "Haengt seit 14 Minuten" ist eine voellig andere Aussage als "haengt gerade":
/// ein Zug im Zustand destination_full ist Sekunden nach dem Abfahren normal
/// und nach einer Viertelstunde ein Ladeengpass.
/// </summary>
public sealed class TrainWatcher {
    /// <summary>Zug-Id -&gt; (Zustand, seit wann). Der Zustand gehoert dazu: wechselt
    /// er, faengt die Zeit neu an.</summary>
    private readonly Dictionary<long, (string State, long Since)> since = new Dictionary<long, (string, long)>();

    public TrainReport Detect(JsonElement trains, long now) {
        List<StuckTrain> problems = new List<StuckTrain>();
        HashSet<long> seen = new HashSet<long>();

        foreach(JsonElement p in Json.Array(trains, "problems")) {
            long id = (long)Json.Num(p, "id");
            string state = Json.Str(p, "state");
            seen.Add(id);

            if(!since.TryGetValue(id, out (string State, long Since) prev) || prev.State != state) {
                prev = (state, now);
                since[id] = prev;
            }

            List<TrainCargo> cargo = new List<TrainCargo>();
            foreach(JsonElement c in Json.Array(p, "cargo")) {
                cargo.Add(new TrainCargo(Json.Str(c, "name"), Json.Num(c, "count")));
            }

            problems.Add(new StuckTrain {
                Id = id,
                Surface = Json.Str(p, "surface") is string s && s.Length > 0 ? s : null,
                State = state,
                ScheduleStation = Json.Str(p, "schedule_station") is string st && st.Length > 0 ? st : null,
                Cargo = cargo,
                StuckSeconds = (int)(now - prev.Since)
            });
        }

        // Zuege, die wieder fahren, vergessen.
        foreach(long id in since.Keys.ToList()) {
            if(!seen.Contains(id)) since.Remove(id);
        }

        problems.Sort((a, b) => b.StuckSeconds.CompareTo(a.StuckSeconds));

        JsonElement totals = Json.Sub(trains, "totals");
        return new TrainReport {
            Total = Json.Int(totals, "total"),
            Ok = Json.Int(totals, "ok"),
            Problem = Json.Int(totals, "problem"),
            Problems = problems
        };
    }
}
