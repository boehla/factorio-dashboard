using Fdash.Core;

namespace Fdash.Collector;

/// <summary>
/// Entkoppelt Collector und Transport (SignalR). Der Collector published,
/// die API-Schicht abonniert und pusht ins Frontend (Plan §7).
/// </summary>
public interface ISnapshotBus {
    event Func<Snapshot, Task>? OnSnapshot;
    Task PublishAsync(Snapshot snapshot);
    Snapshot? Latest(string job);
    IReadOnlyDictionary<string, Snapshot> All();
}
