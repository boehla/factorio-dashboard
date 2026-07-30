using Microsoft.AspNetCore.SignalR;

namespace Fdash.Api;

/// <summary>SignalR-Hub, ueber den Snapshots ins Frontend gepusht werden (Plan §7).</summary>
public sealed class DashboardHub : Hub {
    // Client-Methode: "snapshot" (job, payload). Kein Server-seitiger Input noetig.
}
