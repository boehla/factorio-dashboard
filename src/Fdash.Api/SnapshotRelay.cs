using System.Text.Json;
using Fdash.Collector;
using Fdash.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace Fdash.Api;

/// <summary>Bruecke Collector-Bus -> SignalR (Plan §7). Push statt Polling.</summary>
public sealed class SnapshotRelay : IHostedService {
    private readonly ISnapshotBus bus;
    private readonly IHubContext<DashboardHub> hub;

    public SnapshotRelay(ISnapshotBus bus, IHubContext<DashboardHub> hub) {
        this.bus = bus;
        this.hub = hub;
    }

    public Task StartAsync(CancellationToken ct) {
        bus.OnSnapshot += relay;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) {
        bus.OnSnapshot -= relay;
        return Task.CompletedTask;
    }

    private async Task relay(Snapshot s) {
        await hub.Clients.All.SendAsync("snapshot", new {
            job = s.Job, saveId = s.SaveId, ts = s.Ts, payload = s.Payload
        });
    }
}
