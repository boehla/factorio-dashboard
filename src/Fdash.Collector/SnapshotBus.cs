using System.Collections.Concurrent;
using Fdash.Core;

namespace Fdash.Collector;

public sealed class SnapshotBus : ISnapshotBus {
    private readonly ConcurrentDictionary<string, Snapshot> latest = new();

    public event Func<Snapshot, Task>? OnSnapshot;

    public async Task PublishAsync(Snapshot snapshot) {
        latest[snapshot.Job] = snapshot;
        Func<Snapshot, Task>? handler = OnSnapshot;
        if(handler != null) {
            foreach(Func<Snapshot, Task> d in handler.GetInvocationList().Cast<Func<Snapshot, Task>>()) {
                try { await d(snapshot); } catch { }
            }
        }
    }

    public Snapshot? Latest(string job) => latest.TryGetValue(job, out Snapshot? s) ? s : null;

    public IReadOnlyDictionary<string, Snapshot> All() => latest;
}
