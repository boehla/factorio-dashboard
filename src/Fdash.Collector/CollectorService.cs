using System.Text.Json;
using Fdash.Core;
using Fdash.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fdash.Collector;

/// <summary>
/// Holt Snapshots vom Mod und verteilt sie an Bus, Zeitreihe und die
/// abgeleiteten Auswertungen.
///
/// Der Unterschied zur RCON-Variante von frueher ist der Ort der Arbeit: es
/// wird hier nichts mehr im Spiel angestossen und abgewartet. Der Mod hat den
/// Snapshot laengst fertig, dieser Dienst liest ihn nur ab. Deshalb gibt es
/// auch keinen Job-Scheduler mehr — die Intervalle stehen im Mod.
/// </summary>
public sealed class CollectorService : BackgroundService {
    /// <summary>Jobs, deren Werte in die Zeitreihe gehen (Rest ist nur Live-Anzeige).</summary>
    private static readonly HashSet<string> Persisted = new(StringComparer.Ordinal) {
        "power", "assemblers", "logistics", "circuits", "production", "platforms", "drills", "resources"
    };

    private readonly GameLink link;
    private readonly DiscoveryService discovery;
    private readonly PrototypeExporter prototypes;
    private readonly ITimeSeriesStore store;
    private readonly ISnapshotBus bus;
    private readonly StallDetector stallDetector;
    private readonly AutoResearchService autoResearch;
    private readonly CollectorOptions options;
    private readonly ILogger<CollectorService> log;

    private Discovery? current;
    private DateTime nextAutoResearch = DateTime.MinValue;

    /// <summary>Letzter verarbeiteter Game-Tick je Job-Key — gegen Doppelschreiben.</summary>
    private readonly Dictionary<string, long> lastJobTick = new(StringComparer.Ordinal);

    public CollectorService(GameLink link, DiscoveryService discovery, PrototypeExporter prototypes,
        ITimeSeriesStore store, ISnapshotBus bus, StallDetector stallDetector,
        AutoResearchService autoResearch, IOptions<CollectorOptions> options, ILogger<CollectorService> log) {
        this.link = link;
        this.discovery = discovery;
        this.prototypes = prototypes;
        this.store = store;
        this.bus = bus;
        this.stallDetector = stallDetector;
        this.autoResearch = autoResearch;
        this.options = options.Value;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        await store.InitializeAsync(ct);

        long lastSeq = -1;
        int backoff = 1;
        bool announcedWaiting = false;

        while(!ct.IsCancellationRequested) {
            try {
                ModSnapshot? snapshot = await link.FetchAsync(lastSeq, ct);
                if(snapshot == null) {
                    if(!announcedWaiting && current == null) {
                        log.LogInformation("Waiting for the fdash-exporter mod to publish its first snapshot ({Source})…",
                            link.Description);
                        announcedWaiting = true;
                    }
                } else {
                    lastSeq = snapshot.Seq;
                    await processAsync(snapshot, ct);
                    announcedWaiting = false;
                }
                backoff = 1;
                await Task.Delay(options.PollIntervalMs, ct);
            } catch(OperationCanceledException) {
                break;
            } catch(Exception ex) {
                log.LogError(ex, "Snapshot fetch failed; retrying in {Backoff}s", backoff);
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); } catch { break; }
                backoff = Math.Min(60, backoff * 2);
            }
        }
    }

    private async Task processAsync(ModSnapshot snapshot, CancellationToken ct) {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // --- 1. Discovery aus meta (Save-Wechsel-Erkennung) ---
        if(!snapshot.Jobs.TryGetValue("meta", out ModJob? meta)) {
            log.LogWarning("Snapshot without 'meta' — cannot determine save id, skipping.");
            return;
        }
        Discovery d = discovery.Parse(meta.Data);
        if(current == null) {
            current = d;
            discovery.LogDiscovery(d);
        } else if(d.SaveId != current.SaveId) {
            log.LogWarning("Save change detected: {Old} -> {New}", current.SaveId, d.SaveId);
            current = d;
            lastJobTick.Clear();
        } else {
            current = d;
        }

        string saveId = current.SaveId;
        string primary = primarySurface(current);

        // --- 2. Prototypen (einmalig, sobald der Mod sie geschrieben hat) ---
        if(!prototypes.Loaded) await prototypes.TryLoadAsync(ct);

        // --- 3. Jobs verteilen ---
        foreach(KeyValuePair<string, ModJob> kv in snapshot.Jobs) {
            string key = kv.Key;
            ModJob job = kv.Value;

            // Unveraendert seit dem letzten Snapshot -> nichts zu tun. Ein Job
            // veroeffentlicht nur am Ende eines Durchlaufs neu, andere Jobs
            // laufen in der Zwischenzeit weiter.
            if(lastJobTick.TryGetValue(key, out long seen) && seen == job.Tick) continue;
            lastJobTick[key] = job.Tick;

            (string baseName, string? surface) = splitKey(key);

            await bus.PublishAsync(new Snapshot(key, saveId, ts, job.Data));
            // Zusaetzlich unter dem blanken Job-Namen veroeffentlichen, damit
            // das Frontend ohne Surface-Wissen auskommt. Bewusst nur fuer die
            // Haupt-Surface — frueher gewann hier zufaellig die zuletzt
            // abgefragte Oberflaeche.
            if(surface != null && surface == primary) {
                await bus.PublishAsync(new Snapshot(baseName, saveId, ts, job.Data));
            }

            if(Persisted.Contains(baseName)) {
                IReadOnlyList<Sample> samples = MetricExtractor.Extract(baseName, saveId, ts, job.Data);
                if(samples.Count > 0) await store.WriteAsync(samples, ct);
            }
        }

        // --- 4. Abgeleiteter Stall-Job ---
        JsonElement? production = payload(snapshot, "production", primary);
        JsonElement? assemblers = payload(snapshot, "assemblers", primary);
        if(production is JsonElement prod && assemblers is JsonElement asm) {
            IReadOnlyList<StallItem> stalls = stallDetector.Detect(prod, asm, 0.01, ts);
            JsonElement stallPayload = JsonSerializer.SerializeToElement(new { stalled = stalls });
            await bus.PublishAsync(new Snapshot("stall", saveId, ts, stallPayload));
        }

        // --- 5. Auto-Research ---
        DateTime now = DateTime.UtcNow;
        if(now >= nextAutoResearch && production is JsonElement prodForRes) {
            nextAutoResearch = now.AddSeconds(Math.Max(5, options.AutoResearchIntervalSeconds));
            JsonElement? researchState = payload(snapshot, "research_state", primary);
            if(researchState is JsonElement rs) {
                await evaluateAutoResearchAsync(rs, prodForRes, saveId, ts, ct);
            }
        }
    }

    private async Task evaluateAutoResearchAsync(JsonElement researchState, JsonElement production,
            string saveId, long ts, CancellationToken ct) {
        try {
            HashSet<string> produced = new(StringComparer.Ordinal);
            if(production.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array) {
                foreach(JsonElement it in items.EnumerateArray()) {
                    if(it.TryGetProperty("produced_per_min", out JsonElement p)
                        && p.ValueKind == JsonValueKind.Number && p.GetDouble() > 0) {
                        string? name = it.TryGetProperty("item", out JsonElement n) ? n.GetString() : null;
                        if(name != null) produced.Add(name);
                    }
                }
            }

            ResearchChoice? choice = autoResearch.Evaluate(researchState, produced);
            JsonElement payloadJson;
            if(choice != null) {
                string result = await autoResearch.ApplyAsync(choice, ct);
                payloadJson = JsonSerializer.SerializeToElement(new {
                    tech = choice.Tech, level = choice.Level,
                    estimated_seconds = choice.EstimatedSeconds,
                    enabled = autoResearch.Enabled, can_write = autoResearch.CanWrite, result
                });
            } else {
                payloadJson = JsonSerializer.SerializeToElement(new {
                    tech = (string?)null, enabled = autoResearch.Enabled,
                    can_write = autoResearch.CanWrite,
                    result = "queue_not_empty_or_no_candidate"
                });
            }
            await bus.PublishAsync(new Snapshot("research", saveId, ts, payloadJson));
        } catch(Exception ex) {
            log.LogWarning(ex, "Auto-research evaluation failed");
        }
    }

    /// <summary>"power@nauvis" -> ("power", "nauvis"); "trains" -> ("trains", null).</summary>
    private static (string, string?) splitKey(string key) {
        int at = key.IndexOf('@');
        return at < 0 ? (key, null) : (key[..at], key[(at + 1)..]);
    }

    private static JsonElement? payload(ModSnapshot snapshot, string job, string surface) {
        if(snapshot.Jobs.TryGetValue(job + "@" + surface, out ModJob? j)) return j.Data;
        if(snapshot.Jobs.TryGetValue(job, out ModJob? global)) return global.Data;
        return null;
    }

    /// <summary>Nauvis, sonst die erste gemeldete Oberflaeche.</summary>
    private static string primarySurface(Discovery d) {
        foreach(string s in d.Surfaces) {
            if(string.Equals(s, "nauvis", StringComparison.OrdinalIgnoreCase)) return s;
        }
        return d.Surfaces.Length > 0 ? d.Surfaces[0] : "nauvis";
    }
}
