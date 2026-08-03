using System.Text.Json;
using Fdash.Analysis;
using Fdash.Api;
using Fdash.Api.Mcp;
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

// --------------------------------------------------------------------------
// 8) RootCauseAnalyzer: der Port von web/src/lib/shortages.ts. Der Testfall ist
//    bewusst gemein — Kette, Raute UND Kreis in einem Payload:
//
//      circuits <- copper-cable <- copper-plate <- copper-ore   (Kette)
//      engine   <- gears        <- iron-plate                   (Raute ueber
//      circuits <- iron-plate                                    iron-plate)
//      scrap    <- recycled     <- scrap                        (Kreis)
//
//    Wurzeln sind copper-ore und iron-plate: ihnen selbst fehlt nichts.
// --------------------------------------------------------------------------
static object Grp(int total, int starving, Dictionary<string, int> status, Dictionary<string, double>? missing = null)
    => new { total, starving, status, missing, avg_speed = 1.25, recipes = new Dictionary<string, int>() };

JsonElement chain = JsonSerializer.SerializeToElement(new { by_item = new Dictionary<string, object> {
    ["circuits"] = Grp(10, 10, new() { ["no_ingredients"] = 10 },
        new() { ["copper-cable"] = 50, ["iron-plate"] = 20 }),
    ["copper-cable"] = Grp(4, 4, new() { ["no_ingredients"] = 4 }, new() { ["copper-plate"] = 30 }),
    ["copper-plate"] = Grp(6, 6, new() { ["no_ingredients"] = 6 }, new() { ["copper-ore"] = 100 }),
    ["copper-ore"] = Grp(3, 0, new() { ["working"] = 3 }),
    ["engine"] = Grp(5, 5, new() { ["no_ingredients"] = 5 }, new() { ["gears"] = 10 }),
    ["gears"] = Grp(8, 8, new() { ["item_ingredient_shortage"] = 8 }, new() { ["iron-plate"] = 40 }),
    ["iron-plate"] = Grp(20, 0, new() { ["working"] = 20 }),
    ["scrap"] = Grp(2, 2, new() { ["no_ingredients"] = 2 }, new() { ["recycled"] = 5 }),
    ["recycled"] = Grp(2, 2, new() { ["no_ingredients"] = 2 }, new() { ["scrap"] = 5 })
} });

List<RootCause> causes = RootCauseAnalyzer.ComputeRootCauses(chain);
RootCause? copperOre = causes.FirstOrDefault(c => c.Item == "copper-ore");
RootCause? ironPlate = causes.FirstOrDefault(c => c.Item == "iron-plate");
Check("rootcause: chain resolves to the ore", copperOre != null);
Check("rootcause: copper-ore blocks the whole chain",
    copperOre != null && copperOre.BlockedItems.Count == 3);   // circuits, copper-cable, copper-plate
Check("rootcause: diamond counted once per consumer",
    ironPlate != null && ironPlate.BlockedItems.Count == 3);   // circuits, engine, gears
// Jede Abnehmer-Gruppe zaehlt ihre eigene Fehlmenge bei: circuits 20 direkt,
// gears 40 direkt, engine noch einmal 40 ueber gears. Ein geteilter Engpass
// wiegt damit so schwer, wie viele Gruppen an ihm haengen — genau so ist es im
// Frontend auch gemeint.
Check("rootcause: amount is summed per consumer group",
    ironPlate != null && Math.Abs(ironPlate.Amount - 100) < 0.001);
Check("rootcause: intermediate is not a root", !causes.Any(c => c.Item == "copper-cable"));
Check("rootcause: cycle does not hang or produce a root",
    !causes.Any(c => c.Item == "scrap" || c.Item == "recycled"));
Check("rootcause: sorted by affected machines",
    causes.Count >= 2 && causes[0].Machines >= causes[1].Machines);
Check("rootcause: knows whether the root is produced here",
    ironPlate != null && ironPlate.Produced && ironPlate.OwnStatus.GetValueOrDefault("working") == 20);

List<ShortageNode> tree = RootCauseAnalyzer.BuildShortageTree("circuits",
    RootCauseAnalyzer.BuildMissingMap(chain));
Check("shortage tree: sorted by amount, largest first",
    tree.Count == 2 && tree[0].Item == "copper-cable");
Check("shortage tree: marks the leaf as root", tree.Any(n => n.Item == "iron-plate" && n.IsRoot));
Check("shortage tree: descends the chain",
    tree[0].Children.Count == 1 && tree[0].Children[0].Item == "copper-plate");

// --------------------------------------------------------------------------
// 9) TrainWatcher: Dauer laeuft mit, Zustandswechsel setzt zurueck, geloeste
//    Probleme werden vergessen.
// --------------------------------------------------------------------------
static JsonElement Trains(string state) => JsonSerializer.SerializeToElement(new {
    problems = new[] { new { id = 7, surface = "nauvis", state, schedule_station = "Eisen Abladen",
        cargo = new[] { new { name = "iron-ore", count = 4000 } } } },
    totals = new { total = 40, ok = 39, problem = 1 }
});

TrainWatcher watcher = new TrainWatcher();
watcher.Detect(Trains("no_path"), 1000);
TrainReport later = watcher.Detect(Trains("no_path"), 1180);
Check("train watcher: counts how long the train has been stuck",
    later.Problems.Count == 1 && later.Problems[0].StuckSeconds == 180);
Check("train watcher: keeps cargo and destination",
    later.Problems[0].ScheduleStation == "Eisen Abladen" && later.Problems[0].Cargo.Count == 1);
TrainReport changed = watcher.Detect(Trains("destination_full"), 1200);
Check("train watcher: a new state restarts the clock", changed.Problems[0].StuckSeconds == 0);
JsonElement noProblems = JsonSerializer.SerializeToElement(new {
    problems = Array.Empty<object>(), totals = new { total = 40, ok = 40, problem = 0 } });
Check("train watcher: forgets trains that run again",
    watcher.Detect(noProblems, 1300).Problems.Count == 0);

// --------------------------------------------------------------------------
// 10) TrendCalculator: Richtung aus zwei Fensterhaelften.
// --------------------------------------------------------------------------
static IReadOnlyList<Sample> Series(params double[] values) {
    List<Sample> list = new List<Sample>();
    for(int i = 0; i < values.Length; i++) list.Add(new Sample("s", "m", "l", 1000 + i * 10, values[i]));
    return list;
}
Check("trend: rising", TrendCalculator.FromSamples(Series(10, 10, 20, 20), 1000, 1040, 0.05).Direction == "rising");
Check("trend: falling", TrendCalculator.FromSamples(Series(20, 20, 10, 10), 1000, 1040, 0.05).Direction == "falling");
Check("trend: stable", TrendCalculator.FromSamples(Series(10, 10, 10, 10), 1000, 1040, 0.05).Direction == "stable");
Check("trend: too little data stays unknown",
    TrendCalculator.FromSamples(Series(10), 1000, 1040, 0.05).Direction == "unknown");
Check("trend: raw resolution for short windows", TrendCalculator.ResolutionFor(600) == Resolution.Raw);
Check("trend: hourly resolution for very long windows",
    TrendCalculator.ResolutionFor(120 * 86400) == Resolution.Hour);

// --------------------------------------------------------------------------
// 11) ProblemAnalyzer + MCP-Tools gegen einen Bus, wie ihn der Collector fuellt.
// --------------------------------------------------------------------------
SnapshotBus bus = new SnapshotBus();
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
async Task Publish(string job, object payload) =>
    await bus.PublishAsync(new Snapshot(job, "testsave", now, FdashJson.ToElement(payload)));

await Publish("meta", new { surfaces = new[] { "nauvis" }, tick = 3600 * 60 * 100, seed = 1 });
await bus.PublishAsync(new Snapshot("assemblers@nauvis", "testsave", now, chain));
await Publish("power@nauvis", new { surface = "nauvis", networks = new[] { new {
    id = 1, production = 40e6, consumption = 80e6, satisfaction = 0.5, capacity = 50e6,
    accumulators = new { count = 10, energy = 1e6, capacity = 100e6, charge_rate = 0.0, discharge_rate = 5e6 } } } });
await Publish("trains_derived", new { total = 40, ok = 39, problem = 1, problems = new[] {
    new { id = 7, state = "no_path", schedule_station = "Eisen", stuck_seconds = 900 } } });

SnapshotView view = new SnapshotView(bus);
ProblemAnalyzer analyzer = new ProblemAnalyzer(view, Options.Create(new CollectorOptions()));
List<Problem> found = analyzer.Analyze();
Check("problems: power shortfall detected", found.Any(p => p.Domain == "power" && p.Severity > 0.4));
Check("problems: draining accumulators detected",
    found.Any(p => p.Domain == "power" && p.Title.Contains("Akkus")));
Check("problems: shortage roots detected", found.Any(p => p.Domain == "shortage"));
Check("problems: stuck train detected", found.Any(p => p.Domain == "trains" && p.Severity > 0.8));
Check("problems: sorted by severity, worst first",
    found.Count >= 2 && found[0].Severity >= found[^1].Severity);
Check("problems: every entry explains itself",
    found.All(p => p.Title.Length > 0 && p.Detail.Length > 0));

// Ein Payload in Pyanodons-Groesse: 600 Gruppen, jede mit fehlenden Zutaten.
Dictionary<string, object> huge = new Dictionary<string, object>();
for(int i = 0; i < 600; i++) {
    huge["item-with-a-fairly-long-modded-name-" + i] = Grp(40, 40,
        new() { ["no_ingredients"] = 40 },
        new() { ["ingredient-" + i] = 12.5, ["another-ingredient-" + i] = 7.5 });
}
await bus.PublishAsync(new Snapshot("assemblers@nauvis", "testsave", now,
    JsonSerializer.SerializeToElement(new { by_item = huge, no_recipe = 12 })));

McpOptions mcpOptions = new McpOptions();
string machineAnswer = MachineTools.GetMachineStatusSummary(view, Options.Create(mcpOptions));
Check("token budget: machine summary stays within MaxResponseChars",
    machineAnswer.Length <= mcpOptions.MaxResponseChars);
Check("token budget: truncation is reported, not silent",
    machineAnswer.Contains("\"truncated\":true") && machineAnswer.Contains("\"total_available\":600"));

await new ProblemsDerivedJob(bus, new ProblemAnalyzer(view, Options.Create(new CollectorOptions())))
    .RunAsync(new ModSnapshot(1, 1, 1, new Dictionary<string, ModJob>()), "testsave", now, default);
string problemAnswer = FactoryTools.GetProblems(view, Options.Create(mcpOptions));
Check("token budget: problem list stays within MaxResponseChars",
    problemAnswer.Length <= mcpOptions.MaxResponseChars);
Check("token budget: every answer carries its age",
    problemAnswer.Contains("data_age_seconds") && machineAnswer.Contains("data_age_seconds"));

// --------------------------------------------------------------------------
// 12) Prototyp-Export und Rezeptbaum. Der Export wird aus einer echten Datei
//     gelesen, weil genau dort die Fallen liegen: unendliche Technologien
//     melden max_level = 4294967295, was in kein int passt und frueher den
//     ganzen Collector in seinen Backoff getrieben hat.
// --------------------------------------------------------------------------
string protoDir = Path.Combine(Path.GetTempPath(), "fdash-proto-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(protoDir, "fdash"));
try {
    File.WriteAllText(Path.Combine(protoDir, "fdash", "prototypes.json"), """
    {
      "recipes": {
        "circuit":      { "category": "crafting", "e": 0.5, "prodmod": true,
                          "ing": [{"n":"copper-cable","t":"item","a":3},{"n":"iron-plate","t":"item","a":1}],
                          "prod": [{"n":"circuit","t":"item","a":1}] },
        "copper-cable": { "e": 0.5, "ing": [{"n":"copper-plate","t":"item","a":1}],
                          "prod": [{"n":"copper-cable","t":"item","a":2}] },
        "copper-plate": { "e": 3.2, "ing": [{"n":"copper-ore","t":"item","a":1}],
                          "prod": [{"n":"copper-plate","t":"item","a":1}] },
        "scrap-back":   { "e": 1, "ing": [{"n":"circuit","t":"item","a":1}],
                          "prod": [{"n":"copper-cable","t":"item","a":1,"p":0.25}] }
      },
      "resources": { "copper-ore": { "infinite": false, "mining_time": 1, "product": "copper-ore" } },
      "technologies": {
        "electronics":     { "count": 100, "e": 15, "unlocks": ["circuit","copper-cable"],
                             "pre": ["automation"], "max_level": 1 },
        "mining-prod":     { "count": 500, "e": 60, "max_level": 4294967295, "upgrade": true }
      },
      "fluid_names": { "water": true },
      "entities": {}, "icon_stems": {}
    }
    """);

    PrototypeExporter protoExporter = new PrototypeExporter(
        Options.Create(new CollectorOptions { ScriptOutputPath = protoDir }),
        NullLogger<PrototypeExporter>.Instance);
    Check("prototypes: file loads", await protoExporter.TryLoadAsync(default));
    Check("prototypes: recipes parsed", protoExporter.Recipes.Count == 4);
    Check("prototypes: productivity flag survives",
        protoExporter.Recipes["circuit"].AllowProductivity && !protoExporter.Recipes["copper-cable"].AllowProductivity);
    Check("prototypes: technologies parsed", protoExporter.Technologies.Count == 2);
    Check("prototypes: infinite tech does not overflow max_level",
        protoExporter.Technologies["mining-prod"].MaxLevel == null && protoExporter.Technologies["electronics"].MaxLevel == 1);
    Check("prototypes: unlock index is inverted",
        protoExporter.UnlockedBy["circuit"].Contains("electronics"));

    RecipeQuery recipes = new RecipeQuery(protoExporter);
    Check("recipes: producer lookup", recipes.ProducedBy("circuit").Contains("circuit"));
    Check("recipes: consumer lookup", recipes.ConsumedBy("copper-cable").Contains("circuit"));

    JsonElement prodPayload = JsonSerializer.SerializeToElement(new { items = new[] {
        new { item = "circuit", produced_per_min = 30.0, consumed_per_min = 10.0 },
        new { item = "copper-cable", produced_per_min = 90.0, consumed_per_min = 90.0 } } });
    JsonElement asmPayload = JsonSerializer.SerializeToElement(new { by_item = new Dictionary<string, object> {
        ["circuit"] = new { total = 4, avg_speed = 1.25, recipes = new Dictionary<string, int> { ["circuit"] = 4 } } } });

    RecipeNode chainTree = recipes.Build("circuit", 3, false, true,
        RecipeQuery.StateFrom(prodPayload, asmPayload), RecipeQuery.MachinesFrom(asmPayload));
    Check("recipes: live rate is attached", Math.Abs(chainTree.State.ProducedPerMin - 30) < 0.001 && chainTree.State.Machines == 4);
    Check("recipes: the built recipe comes first", chainTree.Recipes[0].MachinesRunningIt == 4);
    Check("recipes: chain descends to the ore",
        chainTree.Children.Any(c => c.Item == "copper-cable" && c.Children.Any(g => g.Item == "copper-plate")));
    Check("recipes: recycling cycle terminates", chainTree.Children.Count > 0);

    RecipeNode shallow = recipes.Build("circuit", 1, false, true,
        RecipeQuery.StateFrom(prodPayload, asmPayload), RecipeQuery.MachinesFrom(asmPayload));
    Check("recipes: depth limit is distinguishable from a real leaf",
        shallow.Children.All(c => c.DepthLimited || c.IsLeaf)
        && shallow.Children.Any(c => c.Item == "copper-cable" && c.DepthLimited && !c.IsLeaf));
    Check("recipes: ore is a real leaf, not a depth limit",
        recipes.Build("copper-plate", 3, false, true,
            RecipeQuery.StateFrom(prodPayload, asmPayload), RecipeQuery.MachinesFrom(asmPayload))
        .Children.Any(c => c.Item == "copper-ore" && c.IsLeaf && !c.DepthLimited));

    // Eine Stufe statt eines Baums: das Werkzeug fuer "erst schauen, dann
    // gezielt tiefer". Es darf genau nicht rekursiv werden.
    SnapshotBus recipeBus = new SnapshotBus();
    await recipeBus.PublishAsync(new Snapshot("meta", "testsave", now,
        FdashJson.ToElement(new { surfaces = new[] { "nauvis" } })));
    await recipeBus.PublishAsync(new Snapshot("assemblers@nauvis", "testsave", now, asmPayload));
    SnapshotView recipeView = new SnapshotView(recipeBus);

    string oneLevel = RecipeTools.GetRecipeIngredients(recipeView, protoExporter,
        Options.Create(mcpOptions), "circuit,copper-ore,not-a-thing");
    Check("ingredients: direct ingredients are listed",
        oneLevel.Contains("copper-cable") && oneLevel.Contains("iron-plate"));
    // copper-plate haengt eine Ebene unter copper-cable — taucht es auf, ist
    // die Rekursion zurueck, die dieses Werkzeug gerade vermeiden soll.
    Check("ingredients: stops after one level",
        !oneLevel.Contains("copper-plate"));
    Check("ingredients: output count comes along",
        RecipeTools.GetRecipeIngredients(recipeView, protoExporter,
            Options.Create(mcpOptions), "copper-cable").Contains("\"produces\":2"));
    Check("ingredients: ore is raw, a typo is unknown",
        oneLevel.Contains("\"raw_items\":[\"copper-ore\"]")
        && oneLevel.Contains("\"unknown_items\":[\"not-a-thing\"]"));
    Check("ingredients: the built recipe is the one reported",
        oneLevel.Contains("\"machines_running_it\":4") && !oneLevel.Contains("arbitrary_pick"));
    // copper-cable entsteht auch aus dem Recyclingrezept scrap-back, und keines
    // von beiden steht in dieser Testfabrik.
    Check("ingredients: an unbuilt pick among several says so",
        RecipeTools.GetRecipeIngredients(recipeView, protoExporter, Options.Create(mcpOptions), "copper-cable")
            .Contains("\"arbitrary_pick\":true"));

    string wholeTree = RecipeTools.GetRecipeTree(recipeView, protoExporter, Options.Create(mcpOptions), "circuit");
    Check("ingredients: cheaper than the tree it replaces", oneLevel.Length < wholeTree.Length);

    // ----------------------------------------------------------------------
    // 13) ChainDiagnoser: die erste klemmende Stufe, nicht irgendeine. Der Fall
    //     ist der haeufigste echte: das Endprodukt hungert, die Stufe darunter
    //     staut am Ausgang — die Ursache liegt also NICHT bei der Zutat, die
    //     fehlt, sondern beim Nebenprodukt, das niemand abnimmt.
    // ----------------------------------------------------------------------
    JsonElement chainProd = JsonSerializer.SerializeToElement(new { items = new[] {
        new { item = "circuit", produced_per_min = 10.0, consumed_per_min = 60.0 },
        new { item = "copper-cable", produced_per_min = 5.0, consumed_per_min = 30.0 },
        new { item = "copper-plate", produced_per_min = 200.0, consumed_per_min = 5.0 } } });
    JsonElement chainAsm = JsonSerializer.SerializeToElement(new { by_item = new Dictionary<string, object> {
        ["circuit"] = new { total = 4, avg_speed = 1.0,
            status = new Dictionary<string, int> { ["no_ingredients"] = 4 },
            recipes = new Dictionary<string, int> { ["circuit"] = 4 } },
        ["copper-cable"] = new { total = 2, avg_speed = 1.0,
            status = new Dictionary<string, int> { ["full_output"] = 2 },
            recipes = new Dictionary<string, int> { ["copper-cable"] = 2 } },
        ["copper-plate"] = new { total = 8, avg_speed = 1.0,
            status = new Dictionary<string, int> { ["working"] = 8 },
            recipes = new Dictionary<string, int> { ["copper-plate"] = 8 } } } });

    ChainDiagnosis diag = new ChainDiagnoser(protoExporter)
        .Diagnose("circuit", 60, 5, chainProd, chainAsm);
    Check("chain: finds the blocked stage, not the hungry one",
        diag.RootCause != null && diag.RootCause.Item == "copper-cable"
        && diag.RootCause.Reason == "full_output");
    Check("chain: explains itself in words",
        diag.Detail.Contains("copper-cable") && diag.SuggestedAction.Length > 0);
    Check("chain: required rate propagates through the recipe",
        diag.Chain.Any(s => s.Item == "copper-cable" && Math.Abs(s.RequiredPerMin - 180) < 0.1));
    Check("chain: a healthy stage is not reported",
        diag.Chain.All(s => s.Item != "copper-plate" || s.Ok));

    ChainDiagnosis fine = new ChainDiagnoser(protoExporter)
        .Diagnose("copper-plate", 10, 3, chainProd, chainAsm);
    Check("chain: nothing wrong stays nothing wrong", fine.RootCause == null);

    // ----------------------------------------------------------------------
    // 14) ProductionPlanner: Maschinenzahl und der limitierende Schritt.
    //     circuit braucht 0.5 s je Craft -> 120 Crafts/min je Maschine.
    //     60/min Ziel = 0.5 Maschinen; copper-cable: 3 je circuit = 180/min,
    //     Rezept liefert 2 Stueck in 0.5 s -> 240/min je Maschine = 0.75.
    // ----------------------------------------------------------------------
    ProductionPlan plan = new ProductionPlanner(protoExporter)
        .Plan("circuit", 60, 3, chainProd, chainAsm);
    PlanStep? circuitStep = plan.Steps.FirstOrDefault(s => s.Item == "circuit");
    PlanStep? cableStep = plan.Steps.FirstOrDefault(s => s.Item == "copper-cable");
    Check("plan: machine count uses recipe time and measured speed",
        circuitStep != null && Math.Abs(circuitStep.MachinesNeeded - 0.5) < 0.05);
    Check("plan: ingredient rate accounts for output count",
        cableStep != null && Math.Abs(cableStep.RequiredPerMin - 180) < 0.1
        && Math.Abs(cableStep.MachinesNeeded - 0.75) < 0.05);
    Check("plan: limiting step is the worst covered one",
        plan.LimitingStep != null && plan.LimitingStep.Item == "copper-cable");
    Check("plan: productivity flag is carried through",
        circuitStep != null && circuitStep.AllowProductivity);
} finally {
    try { Directory.Delete(protoDir, true); } catch { }
}

// --------------------------------------------------------------------------
// 15) TechGraph: "was fehlt mir bis Technologie X". Der Laufzeit-Job meldet nur
//     die Kandidaten und — gedeckelt — die, denen genau eine Voraussetzung
//     fehlt. Der Erforscht-Stand muss also entweder gemeldet oder aus den
//     Kandidaten hergeleitet werden, und beides muss dasselbe ergeben.
//
//     Baum: base <- mid <- gate <- deep <- goal, goal haengt zusaetzlich an mid.
//     Kandidat ist nur gate, also sind base und mid erforscht.
// --------------------------------------------------------------------------
TechProto Tech(string name, params string[] pre) => new TechProto {
    Name = name, UnitCount = 100, UnitEnergy = 30,
    Prerequisites = pre.ToList(),
    Packs = new List<RecipeIo> { new RecipeIo { Name = "automation-science-pack", Amount = 1 } },
    UnlockedRecipes = new List<string> { name + "-recipe" }
};

Dictionary<string, TechProto> techTree = new Dictionary<string, TechProto> {
    ["base"] = Tech("base"),
    ["mid"] = Tech("mid", "base"),
    ["gate"] = Tech("gate", "mid"),
    ["deep"] = Tech("deep", "gate"),
    ["goal"] = Tech("goal", "deep", "mid")
};
JsonElement techState = FdashJson.ToElement(new {
    candidates = new[] { new { name = "gate" } },
    active_labs = 0, total_labs = 10, lab_speed = 1.0, research_speed_bonus = 0.0
});

TechGraph derivedGraph = new TechGraph(techTree, techState);
Check("tech: derives researched from the candidate list",
    derivedGraph.Source == "derived" && derivedGraph.IsResearched("base") && derivedGraph.IsResearched("mid"));
Check("tech: a candidate is not researched",
    !derivedGraph.IsResearched("gate") && derivedGraph.StatusOf("gate") == TechStatus.Available);
Check("tech: anything behind a candidate is blocked",
    derivedGraph.StatusOf("deep") == TechStatus.Blocked && derivedGraph.StatusOf("goal") == TechStatus.Blocked);
Check("tech: unknown name is distinguishable from blocked",
    derivedGraph.StatusOf("does-not-exist") == TechStatus.Unknown && !derivedGraph.Knows("does-not-exist"));
Check("tech: only the missing direct prerequisites are reported",
    derivedGraph.MissingPrerequisites("goal").SequenceEqual(new[] { "deep" }));

List<string> goalPath = derivedGraph.ResearchPath("goal");
Check("tech: path holds every unresearched step, target last",
    goalPath.SequenceEqual(new[] { "gate", "deep", "goal" }));
Check("tech: path is empty for something already researched",
    derivedGraph.ResearchPath("mid").Count == 0);

// Die gemeldete Liste hat Vorrang — sie kennt auch abgeschaltete Technologien,
// die die Herleitung faelschlich fuer erforscht haelt.
TechGraph reportedGraph = new TechGraph(techTree, techState,
    new HashSet<string>(StringComparer.Ordinal) { "base", "mid", "gate" });
Check("tech: reported list wins over the derivation",
    reportedGraph.Source == "reported" && reportedGraph.IsResearched("gate")
    && reportedGraph.ResearchPath("goal").SequenceEqual(new[] { "deep", "goal" }));

// Ein Zyklus entsteht durch ein Mod-Update schneller als man denkt; er darf
// nicht in einen Stack Overflow laufen.
Dictionary<string, TechProto> cyclic = new Dictionary<string, TechProto> {
    ["x"] = Tech("x", "y"),
    ["y"] = Tech("y", "x")
};
TechGraph cyclicGraph = new TechGraph(cyclic, FdashJson.ToElement(new { candidates = new[] { new { name = "x" } } }));
Check("tech: a prerequisite cycle terminates", cyclicGraph.ResearchPath("y").Count > 0);

TechLedger ledger = new TechLedger();
Check("tech ledger: nothing remembered yet", ledger.Researched("save-a") == null);
ledger.Observe(FdashJson.ToElement(new { researched = new[] { "base", "mid" } }), "save-a");
Check("tech ledger: keeps the reported list", ledger.Researched("save-a")?.Contains("mid") == true);
ledger.Observe(FdashJson.ToElement(new { candidates = new[] { new { name = "gate" } } }), "save-a");
Check("tech ledger: a payload without the list changes nothing",
    ledger.Researched("save-a")?.Count == 2);
Check("tech ledger: another save does not inherit it", ledger.Researched("save-b") == null);

// --------------------------------------------------------------------------
// 16) Stationsnamen als Warenliste. Factorio kennt kein "diese Station liefert
//     Eisen" — die Information steht nur im Namen, und die Namen kommen aus
//     einem Blueprint-Schema: [item=x][virtual-signal=signal-output] liefert.
//     Die Faelle, an denen eine naive Lesart scheitert, stehen alle im echten
//     Save: Beitext hinter dem Tag, ein virtuelles Signal als Ware und
//     Stationen ganz ohne Rolle.
// --------------------------------------------------------------------------
Check("station name: output signal marks a provider",
    StationNames.Parse("[item=iron-plate][virtual-signal=signal-output]") is { Item: "iron-plate", Type: "item", Provides: true, Requests: false });
Check("station name: input signal marks a consumer",
    StationNames.Parse("[item=iron-ore][virtual-signal=signal-input]") is { Item: "iron-ore", Requests: true, Provides: false });
Check("station name: text after the tag does not hide the fluid",
    StationNames.Parse("[fluid=steam]150°C[virtual-signal=signal-output]") is { Item: "steam", Type: "fluid", Provides: true });
Check("station name: a virtual signal can be the ware itself",
    StationNames.Parse("[virtual-signal=signal-fire][virtual-signal=signal-output]") is { Item: "signal-fire", Type: "virtual-signal", Provides: true });
Check("station name: quality rides on the item tag",
    StationNames.Parse("[item=iron-plate,quality=rare][virtual-signal=signal-output]").Item == "iron-plate");
Check("station name: a depot has no role",
    !StationNames.Parse("New[item=cargo-wagon]").HasRole);
Check("station name: a plain name yields nothing",
    StationNames.Parse("Refuel West") is { Item: null, Provides: false, Requests: false });

object Stop(int stops) => new { stops, trains = 0 };
await bus.PublishAsync(new Snapshot("stations@nauvis", "testsave", now, FdashJson.ToElement(new {
    surface = "nauvis",
    stations = new Dictionary<string, object> {
        ["[item=iron-plate][virtual-signal=signal-output]"] = Stop(6),
        ["[item=iron-plate][virtual-signal=signal-input]"] = Stop(3),
        ["[fluid=steam][virtual-signal=signal-output]"] = Stop(2),
        ["[item=ash][virtual-signal=signal-output]"] = Stop(1),
        ["[item=ash]O[virtual-signal=signal-output]"] = Stop(2),
        ["[item=iron-ore][virtual-signal=signal-input]"] = Stop(4),
        ["New[item=cargo-wagon]"] = Stop(12)
    } })));

string netProvide = TrainTools.GetTrainNetworkItems(view, Options.Create(mcpOptions));
Check("network items: only what a station actually provides",
    netProvide.Contains("iron-plate") && netProvide.Contains("steam") && !netProvide.Contains("iron-ore"));
Check("network items: stations without a role are counted, not listed",
    !netProvide.Contains("cargo-wagon") && netProvide.Contains("\"stations_total\":7"));
Check("network items: fluids are marked as such", netProvide.Contains("\"type\":\"fluid\""));
Check("network items: two names for one ware collapse into one entry",
    netProvide.Contains("\"name\":\"ash\",\"stops\":3,\"station_names\":2"));
// Eisen hat je eine Liefer- und eine Abnahmestation — das sind nicht zwei
// Lieferstationen.
Check("network items: the name count stays inside the asked role",
    netProvide.Contains("\"name\":\"iron-plate\",\"stops\":6}"));

string netBoth = TrainTools.GetTrainNetworkItems(view, Options.Create(mcpOptions), role: "both");
Check("network items: both roles show both sides",
    netBoth.Contains("iron-ore") && netBoth.Contains("\"provide_stops\":6,\"request_stops\":3"));

string netAsk = TrainTools.GetTrainNetworkItems(view, Options.Create(mcpOptions), "iron-plate,tungsten-plate,iron-ore");
Check("network items: the filter answers found against missing",
    netAsk.Contains("\"missing\":[\"tungsten-plate\",\"iron-ore\"]") && netAsk.Contains("iron-plate"));
Check("network items: the filtered answer stays small", netAsk.Length < 400);

Console.WriteLine($"\n{(failures == 0 ? "ALL PASSED" : failures + " FAILED")}");
return failures;

/// <summary>Kein RCON konfiguriert — Auto-Research bleibt Preview.</summary>
file sealed class NoControl : IGameControl {
    public bool Available => false;
    public Task<string> SetResearchAsync(string tech, CancellationToken ct) =>
        throw new InvalidOperationException("not available");
    public Task ExportPrototypesAsync(CancellationToken ct) => Task.CompletedTask;
}
