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
///
/// Dazu die dritte Frage, die dieselben Daten beantworten: was das Netz
/// ueberhaupt fuehrt (<c>get_train_network_items</c>) — die Warenliste steckt
/// in den Stationsnamen, nicht in den Zuegen.
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

    [McpServerTool(Name = "get_train_network_items")]
    [Description("Welche Waren das Zugnetz fuehrt: deduplizierte Liste aus den Stationsnamen, getrennt "
        + "nach Lieferstation ([virtual-signal=signal-input]) und Abnehmer (signal-output), mit Zahl der "
        + "Stops je Ware. Mit items= wird daraus die direkte Antwort auf \"hat das Netz X?\" — gefunden "
        + "gegen fehlend, ohne die ganze Liste zu lesen.")]
    public static string GetTrainNetworkItems(
            SnapshotView view, IOptions<McpOptions> opts,
            [Description("Nur diese Namen pruefen, kommagetrennt — leer = ganze Liste")] string? items = null,
            [Description("Rolle: provide (liefert) | request (nimmt ab) | both")] string role = "provide",
            [Description("Oberflaeche, leer = Hauptoberflaeche")] string? surface = null,
            [Description("Maximale Anzahl Namen, Default 300")] int limit = 300) {
        string surf = view.ResolveSurface(surface ?? opts.Value.DefaultSurface);
        JobPayload? st = view.Get("stations", surf);
        if(st == null) return ToolResponse.NoData("stations", surf);

        bool wantProvide = role != "request";
        bool wantRequest = role != "provide";
        bool both = wantProvide && wantRequest;

        // Zusammenfassen ueber die Namen: dieselbe Ware haengt an mehreren
        // Stationsnamen ([item=ash]O neben [item=ash]), und genau die Doppelung
        // soll hier verschwinden.
        Dictionary<string, Ware> byItem = new Dictionary<string, Ware>(StringComparer.Ordinal);
        int stationsTotal = 0, withRole = 0, withoutItem = 0;

        foreach(JsonProperty s in Json.Object(st.Data, "stations")) {
            stationsTotal++;
            StationLabel label = StationNames.Parse(s.Name);
            if(!label.HasRole) continue;
            withRole++;
            if(label.Item == null) {
                // Rollensignal ohne Ware — ein Name, aus dem sich nichts ablesen laesst.
                withoutItem++;
                continue;
            }

            int stops = Json.Int(s.Value, "stops");
            string key = label.Type + "|" + label.Item;
            Ware prev = byItem.TryGetValue(key, out Ware? e) ? e : new Ware(label.Item, label.Type);
            byItem[key] = prev with {
                ProvideStops = prev.ProvideStops + (label.Provides ? stops : 0),
                RequestStops = prev.RequestStops + (label.Requests ? stops : 0),
                ProvideNames = prev.ProvideNames + (label.Provides ? 1 : 0),
                RequestNames = prev.RequestNames + (label.Requests ? 1 : 0)
            };
        }

        // Nur Waren, die in der gefragten Rolle wirklich vorkommen. Ohne das
        // stuende ein reiner Abnehmer in der Provide-Liste und das Netz haette
        // scheinbar etwas, das niemand liefert.
        List<Ware> all = byItem.Values
            .Where(v => (wantProvide && v.ProvideStops > 0) || (wantRequest && v.RequestStops > 0))
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ThenBy(v => v.Type, StringComparer.Ordinal)
            .ToList();

        // Gefragte Namen zuerst: die Antwort ist dann klein und vollstaendig,
        // egal wie gross das Netz ist.
        List<string>? missing = null;
        if(!string.IsNullOrWhiteSpace(items)) {
            List<string> wanted = items
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToList();
            HashSet<string> have = new HashSet<string>(all.Select(v => v.Name), StringComparer.Ordinal);
            missing = wanted.Where(w => !have.Contains(w)).ToList();
            all = all.Where(v => wanted.Contains(v.Name, StringComparer.Ordinal)).ToList();
        }

        // MaxItems deckelt Objektlisten mit vielen Feldern; ein Eintrag ist hier
        // ein Name und zwei Zahlen. Bei 50 gekappt wuerde die Liste die Frage
        // sogar falsch beantworten ("fuehrt das Netz nicht"), deshalb ein
        // eigener, hoeherer Deckel.
        int cap = Math.Clamp(limit <= 0 ? 300 : limit, 1, 500);
        List<object> list = new List<object>();
        foreach(Ware w in all.Take(cap)) {
            // Nur die Namen der gefragten Rolle: sonst zaehlt eine Ware mit je
            // einer Liefer- und einer Abnahmestation als "zwei Lieferstationen".
            int names = both ? w.ProvideNames + w.RequestNames : (wantProvide ? w.ProvideNames : w.RequestNames);
            list.Add(new {
                name = w.Name,
                // "item" ist der Normalfall und kostet sonst nur Tokens.
                type = w.Type == "item" ? null : w.Type,
                stops = both ? (int?)null : (wantProvide ? w.ProvideStops : w.RequestStops),
                provide_stops = both ? w.ProvideStops : (int?)null,
                request_stops = both ? w.RequestStops : (int?)null,
                // Mehrere Stationsnamen fuer dieselbe Ware — Hinweis auf
                // Varianten (Temperatur, Qualitaet, zwei Aussenposten).
                station_names = names > 1 ? names : (int?)null
            });
        }

        return ToolResponse.Ok(new {
            surface = surf,
            data_age_seconds = st.AgeSeconds,
            role = both ? "both" : (wantProvide ? "provide" : "request"),
            stations_total = stationsTotal,
            stations_with_role = withRole,
            // Namen mit Rollensignal, aber ohne erkennbare Ware — wenn das gross
            // ist, folgt das Netz einem anderen Namensschema als angenommen.
            stations_without_item = withoutItem > 0 ? withoutItem : (int?)null,
            total_available = all.Count,
            truncated = all.Count > list.Count,
            // Nur mit items=: die gefragten Namen, die in dieser Rolle fehlen.
            missing,
            items = list
        });
    }

    /// <summary>Eine Ware im Netz, zusammengefasst ueber alle Stationsnamen.</summary>
    private sealed record Ware(string Name, string Type) {
        public int ProvideStops { get; init; }
        public int RequestStops { get; init; }
        public int ProvideNames { get; init; }
        public int RequestNames { get; init; }
    }
}
