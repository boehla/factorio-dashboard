using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Zuege und Bahnhoefe.
///
/// Zwei Seiten derselben Frage: haengende Zuege sagen, dass etwas klemmt;
/// die Bahnhoefe sagen, wo. Eine Station ohne Zug ist eine tote Route, eine
/// mit Dauerwarteschlange ein Ladeengpass.
/// </summary>
[McpServerToolType]
public static class TrainTools {

    [McpServerTool(Name = "get_train_report")]
    [Description("Zuege nach Zustand (no_path und no_schedule sind immer Fehler, destination_full "
        + "erst mit Dauer) inklusive Stillstandsdauer, Ladung und Ziel — und Bahnhoefe mit "
        + "zugewiesenen Zuegen gegen Zuglimit, inklusive der Stationen, die nie angefahren werden.")]
    public static string GetTrainReport(
            SnapshotView view, IOptions<McpOptions> opts,
            [Description("Oberflaeche fuer die Bahnhoefe, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Was: trains | stations | both")] string scope = "both",
            [Description("Nur auffaellige")] bool problemOnly = true,
            [Description("Maximale Anzahl je Abschnitt, Default 20")] int limit = 20) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? t = view.Get("trains_derived") ?? view.Get("trains");
        JobPayload? st = view.Get("stations", surf);
        if(t == null && st == null) return ToolResponse.NoData("trains");

        // ------------------------------------------------------------ Zuege
        List<object> trains = new List<object>();
        Dictionary<string, int> byState = new Dictionary<string, int>(StringComparer.Ordinal);
        int trainsTotal = 0, trainsProblem = 0, trainsAvailable = 0;
        if(t != null && scope != "stations") {
            trainsTotal = Json.Int(t.Data, "total");
            trainsProblem = Json.Int(t.Data, "problem");
            List<JsonElement> problems = Json.Array(t.Data, "problems").ToList();
            trainsAvailable = problems.Count;
            foreach(JsonElement p in problems) {
                string state = Json.Str(p, "state");
                byState[state] = (byState.TryGetValue(state, out int n) ? n : 0) + 1;
            }
            // Am laengsten stehende zuerst — die Reihenfolge kommt schon aus
            // dem abgeleiteten Job, hier nur noch kappen.
            foreach(JsonElement p in problems.Take(Math.Min(limit, opts.Value.MaxItems))) {
                trains.Add(new {
                    id = Json.Int(p, "id"),
                    state = Json.Str(p, "state"),
                    stuck_seconds = Json.Int(p, "stuck_seconds"),
                    surface = Json.Str(p, "surface"),
                    destination = Json.Str(p, "schedule_station"),
                    cargo = Json.Array(p, "cargo")
                        .Select(c => new { name = Json.Str(c, "name"), count = Json.Num(c, "count") }).ToList()
                });
            }
        }

        // -------------------------------------------------------- Bahnhoefe
        List<object> stations = new List<object>();
        int stationsTotal = 0;
        if(st != null && scope != "trains") {
            List<(string Name, int Stops, int Trains, int? Limit, bool Odd)> rows =
                new List<(string, int, int, int?, bool)>();
            foreach(JsonProperty s in Json.Object(st.Data, "stations")) {
                stationsTotal++;
                int stops = Json.Int(s.Value, "stops");
                int cnt = Json.Int(s.Value, "trains");
                int? lim = Json.NumOrNull(s.Value, "limit") is double l ? (int)l : null;
                // Nie angefahren oder am Limit — dazwischen laeuft es.
                bool odd = cnt == 0 || (lim is int lv && lv > 0 && cnt >= lv);
                rows.Add((s.Name, stops, cnt, lim, odd));
            }
            IEnumerable<(string Name, int Stops, int Trains, int? Limit, bool Odd)> filtered =
                problemOnly ? rows.Where(r => r.Odd) : rows;
            List<(string Name, int Stops, int Trains, int? Limit, bool Odd)> sorted = filtered
                .OrderByDescending(r => r.Trains)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();

            foreach((string name, int stops, int cnt, int? lim, bool odd) in
                    sorted.Take(Math.Min(limit, opts.Value.MaxItems))) {
                stations.Add(new {
                    name,
                    stops,
                    trains_count = cnt,
                    trains_limit = lim,
                    // "Nie angefahren" heisst nicht zwingend kaputt: eine
                    // Reservestation ist genauso still. Deshalb als Hinweis,
                    // nicht als Fehler.
                    hint = cnt == 0 ? "keine Zuege zugewiesen"
                        : (lim is int lv && cnt >= lv ? "am Zuglimit" : null)
                });
            }
        }

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = t?.AgeSeconds ?? st?.AgeSeconds,
            trains_total = trainsTotal,
            trains_with_problems = trainsProblem,
            by_state = byState,
            trains_truncated = trains.Count < trainsAvailable,
            trains,
            stations_total = stationsTotal,
            station_data = st != null,
            stations_truncated = stationsTotal > stations.Count,
            stations
        });
    }
}
