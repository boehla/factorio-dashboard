using System.Text.Json;
using Fdash.Collector;
using Fdash.Core;

namespace Fdash.Analysis;

/// <summary>
/// Zuege mit Stillstandsdauer. Veroeffentlicht <c>trains_derived</c> — der rohe
/// <c>trains</c>-Job bleibt unangetastet daneben stehen.
/// </summary>
public sealed class TrainDerivedJob : IDerivedJob {
    private readonly ISnapshotBus bus;
    private readonly TrainWatcher watcher;

    public TrainDerivedJob(ISnapshotBus bus, TrainWatcher watcher) {
        this.bus = bus;
        this.watcher = watcher;
    }

    public async Task RunAsync(ModSnapshot snapshot, string saveId, long ts, CancellationToken ct) {
        if(!snapshot.Jobs.TryGetValue("trains", out ModJob? trains)) return;
        TrainReport report = watcher.Detect(trains.Data, ts);
        await bus.PublishAsync(new Snapshot("trains_derived", saveId, ts, FdashJson.ToElement(report)));
    }
}

/// <summary>
/// Problem-Rangliste ueber alle Domaenen. Veroeffentlicht <c>problems</c>.
///
/// Muss nach <see cref="TrainDerivedJob"/> laufen — die Zug-Probleme kommen aus
/// dessen Ergebnis, nicht aus dem rohen Job (nur dort steht die Dauer).
/// </summary>
public sealed class ProblemsDerivedJob : IDerivedJob {
    private readonly ISnapshotBus bus;
    private readonly ProblemAnalyzer analyzer;

    public ProblemsDerivedJob(ISnapshotBus bus, ProblemAnalyzer analyzer) {
        this.bus = bus;
        this.analyzer = analyzer;
    }

    public async Task RunAsync(ModSnapshot snapshot, string saveId, long ts, CancellationToken ct) {
        List<Problem> problems = analyzer.Analyze();
        JsonElement payload = FdashJson.ToElement(new {
            problems,
            counts = countByDomain(problems)
        });
        await bus.PublishAsync(new Snapshot("problems", saveId, ts, payload));
    }

    private static Dictionary<string, int> countByDomain(List<Problem> problems) {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach(Problem p in problems) {
            counts[p.Domain] = (counts.TryGetValue(p.Domain, out int n) ? n : 0) + 1;
        }
        return counts;
    }
}
