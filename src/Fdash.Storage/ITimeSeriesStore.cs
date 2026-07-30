using Fdash.Core;

namespace Fdash.Storage;

/// <summary>
/// Zeitreihen-Repository hinter einem Interface (Plan §5.5), damit ein
/// spaeterer Wechsel SQLite -> DuckDB billig bleibt.
/// </summary>
public interface ITimeSeriesStore {
    Task InitializeAsync(CancellationToken ct);
    Task WriteAsync(IReadOnlyCollection<Sample> samples, CancellationToken ct);
    Task<IReadOnlyList<Sample>> QueryAsync(string saveId, string metric, string? labels,
        long fromTs, long toTs, Resolution resolution, CancellationToken ct);
    /// <summary>Stuendlicher Roll-up der Raw-Daten in die groeberen Tiers.</summary>
    Task RollupAsync(CancellationToken ct);
    /// <summary>Loescht abgelaufene Daten gemaess Retention.</summary>
    Task PruneAsync(CancellationToken ct);
}
