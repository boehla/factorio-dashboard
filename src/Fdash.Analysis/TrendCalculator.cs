using Fdash.Core;
using Fdash.Storage;

namespace Fdash.Analysis;

/// <summary>
/// Richtung einer Kennzahl. "37 Items im Puffer" sagt nichts, "12/min bei
/// Verbrauch 20/min, Trend fallend" sagt alles — deshalb haengt an jeder
/// Durchsatz- und Fuellstandsangabe ein Trend.
/// </summary>
public sealed record TrendResult {
    public string Direction { get; init; } = "unknown";  // rising | stable | falling | unknown
    public double Recent { get; init; }
    public double Previous { get; init; }
    /// <summary>Relative Aenderung, 0.15 = +15 %. 0, wenn Previous 0 ist.</summary>
    public double ChangePct { get; init; }
    public int Samples { get; init; }
}

public sealed record HistoryBucket(long Ts, double Avg, double Min, double Max);

public sealed record HistorySummary {
    public string Metric { get; init; } = "";
    public string? Labels { get; init; }
    public long From { get; init; }
    public long To { get; init; }
    public string Resolution { get; init; } = "";
    public List<HistoryBucket> Buckets { get; init; } = new List<HistoryBucket>();
    public TrendResult Trend { get; init; } = new TrendResult();
}

/// <summary>
/// Trend, Verlauf und Schwellwert-Auswertungen aus der Zeitreihe.
///
/// Die Spezifikation sieht dafuer einen Ringpuffer im Mod vor. Den braucht es
/// hier nicht: der Server schreibt die Kennzahlen ohnehin nach SQLite (siehe
/// <see cref="Fdash.Collector.MetricExtractor"/>), inklusive Roll-up ueber vier
/// Stufen. Das haelt das Savegame klein und erlaubt Fenster ueber Tage statt
/// ueber eine Stunde.
/// </summary>
public sealed class TrendCalculator {
    private readonly ITimeSeriesStore store;

    /// <summary>Ab welcher relativen Aenderung ein Trend nicht mehr "stable" heisst.</summary>
    public double Threshold { get; set; } = 0.05;

    public TrendCalculator(ITimeSeriesStore store) {
        this.store = store;
    }

    public Func<long> Now { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Vergleicht die juengere mit der aelteren Haelfte des Fensters. Bewusst
    /// keine Regression: bei Produktionsraten interessiert "laeuft es gerade an
    /// oder bricht es ein", nicht die exakte Steigung.
    /// </summary>
    public async Task<TrendResult> ComputeAsync(string saveId, string metric, string? labels,
            int windowSeconds, CancellationToken ct) {
        long now = Now();
        long from = now - windowSeconds;
        IReadOnlyList<Sample> data = await store.QueryAsync(saveId, metric, labels, from, now,
            ResolutionFor(windowSeconds), ct);
        return FromSamples(data, from, now, Threshold);
    }

    /// <summary>Trennung von der Abfrage, damit es ohne Datenbank testbar bleibt.</summary>
    public static TrendResult FromSamples(IReadOnlyList<Sample> data, long from, long to, double threshold) {
        if(data.Count < 2) return new TrendResult { Samples = data.Count };

        long mid = from + (to - from) / 2;
        double recentSum = 0, prevSum = 0;
        int recentN = 0, prevN = 0;
        foreach(Sample s in data) {
            if(s.Ts >= mid) { recentSum += s.Value; recentN++; } else { prevSum += s.Value; prevN++; }
        }
        // Alles in einer Haelfte -> keine Aussage moeglich.
        if(recentN == 0 || prevN == 0) return new TrendResult { Samples = data.Count };

        double recent = recentSum / recentN;
        double previous = prevSum / prevN;
        double change = previous != 0 ? (recent - previous) / Math.Abs(previous) : 0;

        string direction = "stable";
        if(change > threshold) direction = "rising";
        else if(change < -threshold) direction = "falling";

        return new TrendResult {
            Direction = direction, Recent = recent, Previous = previous,
            ChangePct = change, Samples = data.Count
        };
    }

    /// <summary>
    /// Verlauf als Buckets statt als Rohpunktwolke — eine Stunde Rohdaten sind
    /// 720 Punkte, und die will niemand durch ein Sprachmodell schieben.
    /// </summary>
    public async Task<HistorySummary> SummarizeAsync(string saveId, string metric, string? labels,
            int windowSeconds, int buckets, CancellationToken ct) {
        long now = Now();
        long from = now - windowSeconds;
        Resolution res = ResolutionFor(windowSeconds);
        IReadOnlyList<Sample> data = await store.QueryAsync(saveId, metric, labels, from, now, res, ct);

        List<HistoryBucket> result = new List<HistoryBucket>();
        if(buckets < 1) buckets = 1;
        long width = Math.Max(1, (now - from) / buckets);

        double sum = 0, min = double.MaxValue, max = double.MinValue;
        int n = 0;
        long bucketStart = from;
        foreach(Sample s in data) {
            while(s.Ts >= bucketStart + width) {
                if(n > 0) result.Add(new HistoryBucket(bucketStart, sum / n, min, max));
                bucketStart += width;
                sum = 0; min = double.MaxValue; max = double.MinValue; n = 0;
            }
            sum += s.Value;
            if(s.Value < min) min = s.Value;
            if(s.Value > max) max = s.Value;
            n++;
        }
        if(n > 0) result.Add(new HistoryBucket(bucketStart, sum / n, min, max));

        return new HistorySummary {
            Metric = metric, Labels = labels, From = from, To = now,
            Resolution = res.ToString().ToLowerInvariant(),
            Buckets = result,
            Trend = FromSamples(data, from, now, Threshold)
        };
    }

    /// <summary>
    /// Anteil der Messpunkte unterhalb einer Schwelle — fuer brownout_events:
    /// wie oft war die Stromversorgung im Fenster nicht ausreichend.
    /// </summary>
    public async Task<(int Below, int Total)> BelowThresholdAsync(string saveId, string metric, string? labels,
            double threshold, int windowSeconds, CancellationToken ct) {
        long now = Now();
        IReadOnlyList<Sample> data = await store.QueryAsync(saveId, metric, labels,
            now - windowSeconds, now, ResolutionFor(windowSeconds), ct);
        int below = 0;
        foreach(Sample s in data) {
            if(s.Value < threshold) below++;
        }
        return (below, data.Count);
    }

    /// <summary>
    /// Grobheit passend zum Fenster. Die Retention der Tiers gibt die Grenzen
    /// vor (roh 6 h, Minute 7 d, Viertelstunde 90 d) — eine feinere Stufe als
    /// die Retention erlaubt liefert schlicht keine Daten mehr.
    /// </summary>
    public static Resolution ResolutionFor(int windowSeconds) {
        if(windowSeconds <= 3 * 3600) return Resolution.Raw;
        if(windowSeconds <= 3 * 86400) return Resolution.Minute;
        if(windowSeconds <= 45 * 86400) return Resolution.Quarter;
        return Resolution.Hour;
    }
}
