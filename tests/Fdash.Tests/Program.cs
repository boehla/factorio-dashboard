using System.Text.Json;
using Fdash.Collector;
using Fdash.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

int failures = 0;
void Check(string name, bool cond) {
    Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name);
    if(!cond) failures++;
}

// --------------------------------------------------------------------------
// 1) Snapshot-Parser: das Format, das mod/fdash-exporter/scripts/publish.lua
//    erzeugt. Bricht das hier, passen Mod und Server nicht mehr zusammen.
// --------------------------------------------------------------------------
const string snapshotJson = """
{"protocol":1,"seq":42,"tick":123456,"jobs":{
  "meta":{"tick":123400,"data":{"version":"2.0.77","save_name":"server","surfaces":["nauvis","vulcanus"],"tick":123400,"seed":987,"mods":["base 2.0.77"]}},
  "power@nauvis":{"tick":123450,"data":{"surface":"nauvis","networks":[{"id":1,"production":1200.0,"consumption":1100.0,"satisfaction":0.9}]}},
  "trains":{"tick":123440,"data":{"problems":[],"totals":{"total":84,"ok":81,"problem":3}}}
}}
""";

ModSnapshot parsed = ModSnapshotParser.Parse(snapshotJson);
Check("snapshot: protocol", parsed.Protocol == 1);
Check("snapshot: seq", parsed.Seq == 42);
Check("snapshot: job count", parsed.Jobs.Count == 3);
Check("snapshot: per-job tick", parsed.Jobs["power@nauvis"].Tick == 123450);
Check("snapshot: payload survives document disposal",
    parsed.Jobs["power@nauvis"].Data.GetProperty("surface").GetString() == "nauvis");
Check("snapshot: global job keeps bare key", parsed.Jobs.ContainsKey("trains"));

// --------------------------------------------------------------------------
// 2) Discovery + Save-Fingerprint aus dem meta-Job.
// --------------------------------------------------------------------------
DiscoveryService discovery = new DiscoveryService(
    Options.Create(new CollectorOptions()), NullLogger<DiscoveryService>.Instance);
Discovery disc = discovery.Parse(parsed.Jobs["meta"].Data);
Check("discovery: surfaces", disc.Surfaces.Length == 2 && disc.Surfaces[0] == "nauvis");
Check("discovery: seed", disc.Seed == 987);
Check("discovery: save id assigned", disc.SaveId.Length == 12);

Discovery d = new Discovery { Seed = 123456789, SaveName = "foo" };
string f1 = SaveFingerprint.Compute(d, null);
string f2 = SaveFingerprint.Compute(d, null);
Check("fingerprint deterministic", f1 == f2 && f1.Length == 12);
Check("fingerprint override wins", SaveFingerprint.Compute(d, "manual") == "manual");

// --------------------------------------------------------------------------
// 3) FileSnapshotSource gegen ein echtes Verzeichnis (so schreibt der Mod).
// --------------------------------------------------------------------------
string tmp = Path.Combine(Path.GetTempPath(), "fdash-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(tmp, "fdash"));
try {
    void WriteMod(string file, long seq, string body) {
        File.WriteAllText(Path.Combine(tmp, "fdash", file), body);
        File.WriteAllText(Path.Combine(tmp, "fdash", "index.json"),
            $"{{\"protocol\":1,\"seq\":{seq},\"tick\":1,\"file\":\"{file}\"}}");
    }

    FileSnapshotSource source = new FileSnapshotSource(tmp, NullLogger.Instance);
    Check("file source: not ready without index", !source.Ready);

    WriteMod("snapshot-0.json", 42, snapshotJson);
    Check("file source: ready", source.Ready);

    ModSnapshot? got = await source.FetchAsync(-1, CancellationToken.None);
    Check("file source: reads snapshot", got != null && got.Seq == 42);

    // Gleiche Sequenz -> nichts Neues, kein erneutes Lesen der grossen Datei.
    ModSnapshot? again = await source.FetchAsync(42, CancellationToken.None);
    Check("file source: seq gating", again == null);

    // Rotation: naechster Snapshot in einer anderen Datei.
    WriteMod("snapshot-1.json", 43, snapshotJson.Replace("\"seq\":42", "\"seq\":43"));
    ModSnapshot? rotated = await source.FetchAsync(42, CancellationToken.None);
    Check("file source: follows rotation", rotated != null && rotated.Seq == 43);

    // Halb geschriebene Datei darf nicht knallen, sondern muss null liefern.
    WriteMod("snapshot-2.json", 44, "{\"protocol\":1,\"seq\":44,\"jobs\":{\"meta\":{\"tick\"");
    ModSnapshot? torn = await source.FetchAsync(43, CancellationToken.None);
    Check("file source: survives torn write", torn == null);

    // Index selbst halb geschrieben.
    File.WriteAllText(Path.Combine(tmp, "fdash", "index.json"), "{\"seq\":");
    ModSnapshot? tornIndex = await source.FetchAsync(43, CancellationToken.None);
    Check("file source: survives torn index", tornIndex == null);
} finally {
    try { Directory.Delete(tmp, true); } catch { /* Aufraeumen ist best effort */ }
}

// --------------------------------------------------------------------------
// 4) RCON-Identifier: Injection wird abgelehnt, nicht escaped.
// --------------------------------------------------------------------------
bool threw = false;
try { RconSnapshotSource.RequireSafeIdentifier("logistics-3'); game.print('x"); } catch { threw = true; }
Check("rcon identifier rejects injection", threw);
Check("rcon identifier accepts valid",
    RconSnapshotSource.RequireSafeIdentifier("logistics-3") == "logistics-3");

// --------------------------------------------------------------------------
// 5) Auto-Research: waehlt die schnellste Tech mit produzierten Packs.
// --------------------------------------------------------------------------
JsonElement researchState = JsonSerializer.SerializeToElement(new {
    queue_len = 0,
    research_speed_bonus = 0.0,
    active_labs = 1,
    lab_speed = 1.0,
    candidates = new object[] {
        // teuer, Packs vorhanden
        new { name = "slow-tech", level = 1, unit_count = 1000.0, energy = 30.0,
              ingredients = new[] { new { name = "automation-science-pack", amount = 1 } } },
        // billig, Packs vorhanden -> soll gewinnen
        new { name = "fast-tech", level = 1, unit_count = 10.0, energy = 30.0,
              ingredients = new[] { new { name = "automation-science-pack", amount = 1 } } },
        // billigste, aber Pack wird nicht produziert -> raus
        new { name = "missing-pack-tech", level = 1, unit_count = 1.0, energy = 1.0,
              ingredients = new[] { new { name = "military-science-pack", amount = 1 } } },
        // billig, aber auf der Blacklist -> raus
        new { name = "blocked-tech", level = 1, unit_count = 2.0, energy = 1.0,
              ingredients = new[] { new { name = "automation-science-pack", amount = 1 } } }
    }
});

AutoResearchService research = new AutoResearchService(
    new NoControl(),
    Options.Create(new CollectorOptions { ResearchBlacklistPrefixes = new[] { "blocked-" } }),
    NullLogger<AutoResearchService>.Instance);

HashSet<string> produced = new() { "automation-science-pack" };
ResearchChoice? choice = research.Evaluate(researchState, produced);
Check("auto-research picks fastest available", choice != null && choice.Tech == "fast-tech");

JsonElement busyQueue = JsonSerializer.SerializeToElement(new { queue_len = 1, candidates = new object[0] });
Check("auto-research skips when queue busy", research.Evaluate(busyQueue, produced) == null);
Check("auto-research is preview without rcon", !research.CanWrite);

// --------------------------------------------------------------------------
// 6) StallDetector: erkennt echten Stall, ignoriert output_full.
// --------------------------------------------------------------------------
StallDetector sd = new StallDetector();
JsonElement prod = JsonSerializer.SerializeToElement(new { items = new[] {
    new { item = "lds", produced_per_min = 0.0 },
    new { item = "gears", produced_per_min = 0.0 } } });
JsonElement asm = JsonSerializer.SerializeToElement(new { by_item = new Dictionary<string, object> {
    ["lds"] = new { status = new Dictionary<string, int> { ["no_ingredients"] = 12 } },
    ["gears"] = new { status = new Dictionary<string, int> { ["output_full"] = 5 } } } });
var stalls = sd.Detect(prod, asm, 0.01, 1000);
Check("stall detects no_ingredients", stalls.Any(s => s.Item == "lds" && s.Reason == "no_ingredients"));
Check("stall ignores output_full", !stalls.Any(s => s.Item == "gears"));

// --------------------------------------------------------------------------
// 7) MetricExtractor gegen die Payloads, die der Mod liefert.
// --------------------------------------------------------------------------
var powerSamples = MetricExtractor.Extract("power", "abc", 5000, parsed.Jobs["power@nauvis"].Data);
Check("metric extractor emits power samples",
    powerSamples.Any(s => s.Metric == "power.production" && Math.Abs(s.Value - 1200.0) < 0.001));
Check("metric extractor labels by surface",
    powerSamples.All(s => s.Labels.Contains("surface=nauvis")));

JsonElement resources = JsonSerializer.SerializeToElement(new {
    surface = "nauvis",
    resources = new Dictionary<string, object> {
        ["iron-ore"] = new { rate_current = 240.0, depletion_seconds = 3600.0 }
    }
});
var resSamples = MetricExtractor.Extract("resources", "abc", 5000, resources);
Check("metric extractor emits depletion",
    resSamples.Any(s => s.Metric == "resource.depletion_seconds" && Math.Abs(s.Value - 3600.0) < 0.001));

Console.WriteLine($"\n{(failures == 0 ? "ALL PASSED" : failures + " FAILED")}");
return failures;

/// <summary>Kein RCON konfiguriert — Auto-Research bleibt Preview.</summary>
file sealed class NoControl : IGameControl {
    public bool Available => false;
    public Task<string> SetResearchAsync(string tech, CancellationToken ct) =>
        throw new InvalidOperationException("not available");
    public Task ExportPrototypesAsync(CancellationToken ct) => Task.CompletedTask;
}
