using Fdash.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdash.Collector;

/// <summary>Stuendlicher Roll-up + Prune der Zeitreihen (Plan §5.5).</summary>
public sealed class RollupService : BackgroundService {
    private readonly ITimeSeriesStore store;
    private readonly ILogger<RollupService> log;

    public RollupService(ITimeSeriesStore store, ILogger<RollupService> log) {
        this.store = store;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        while(!ct.IsCancellationRequested) {
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch { break; }
            try {
                await store.RollupAsync(ct);
                await store.PruneAsync(ct);
                log.LogInformation("Roll-up + prune completed.");
            } catch(Exception ex) {
                log.LogError(ex, "Roll-up failed");
            }
        }
    }
}
