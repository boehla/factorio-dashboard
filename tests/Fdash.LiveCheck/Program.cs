using System.Text.Json;
using Fdash.Collector;
using Fdash.Core;
using Fdash.Rcon;
using Microsoft.Extensions.Logging.Abstractions;

// Diagnose-Werkzeug gegen einen laufenden Server mit installiertem
// fdash-exporter. Zeigt, ob Daten ankommen, wie alt jeder Job ist und wie gross
// die Payloads sind — die schnellste Art herauszufinden, ob Mod und Dashboard
// zusammenpassen, ohne das ganze Backend zu starten.
//
//   FDASH_SCRIPT_OUTPUT=%APPDATA%\Factorio\script-output
//   FDASH_HOST / FDASH_PORT / FDASH_PASS   (optional, fuer den RCON-Weg)

string? scriptOutput = Environment.GetEnvironmentVariable("FDASH_SCRIPT_OUTPUT");
string host = Environment.GetEnvironmentVariable("FDASH_HOST") ?? "127.0.0.1";
int port = int.TryParse(Environment.GetEnvironmentVariable("FDASH_PORT"), out int p) ? p : 27015;
string? pass = Environment.GetEnvironmentVariable("FDASH_PASS");

ISnapshotSource? source = null;
RconSnapshotSource? rcon = null;

if(!string.IsNullOrWhiteSpace(scriptOutput)) {
    FileSnapshotSource file = new FileSnapshotSource(scriptOutput!, NullLogger.Instance);
    Console.WriteLine($"file source: {file.Directory}  (ready={file.Ready})");
    if(file.Ready) source = file;
}

if(!string.IsNullOrWhiteSpace(pass)) {
    RconClient client = new RconClient(new RconOptions { Host = host, Port = port, Password = pass! });
    rcon = new RconSnapshotSource(client, NullLogger.Instance);
    Console.WriteLine($"rcon source: {host}:{port}");
    source ??= rcon;
}

if(source == null) {
    Console.Error.WriteLine("Nothing to do: set FDASH_SCRIPT_OUTPUT (and make sure the mod wrote a snapshot) or FDASH_PASS.");
    return 2;
}

Console.WriteLine($"\nusing: {source.Description}\n");

if(rcon != null) {
    try {
        Console.WriteLine("--- mod status ---");
        Console.WriteLine(Pretty(await rcon.StatusAsync(CancellationToken.None)));
        Console.WriteLine();
    } catch(Exception ex) {
        Console.Error.WriteLine($"status failed: {ex.Message}");
    }
}

ModSnapshot? snapshot = await source.FetchAsync(-1, CancellationToken.None);
if(snapshot == null) {
    Console.Error.WriteLine("No snapshot available yet. The mod publishes its first one a few seconds after load "
        + "— longer on big maps, because the initial entity scan has to finish first.");
    return 1;
}

Console.WriteLine($"--- snapshot: protocol={snapshot.Protocol} seq={snapshot.Seq} tick={snapshot.Tick} ---");
Console.WriteLine($"{"job",-28} {"tick",12} {"age (s)",9} {"bytes",9}");

int problems = 0;
foreach(KeyValuePair<string, ModJob> kv in snapshot.Jobs.OrderBy(k => k.Key, StringComparer.Ordinal)) {
    string json = kv.Value.Data.GetRawText();
    double ageSeconds = (snapshot.Tick - kv.Value.Tick) / 60.0;
    Console.WriteLine($"{kv.Key,-28} {kv.Value.Tick,12} {ageSeconds,9:F1} {json.Length,9}");
    if(kv.Value.Data.ValueKind != JsonValueKind.Object) {
        Console.WriteLine($"  ! unexpected payload kind: {kv.Value.Data.ValueKind}");
        problems++;
    }
}

// Plausibilitaetspruefungen auf die Jobs, die am haeufigsten klemmen.
Console.WriteLine();
foreach(string required in new[] { "meta", "trains" }) {
    if(!snapshot.Jobs.ContainsKey(required)) {
        Console.WriteLine($"! missing global job: {required}");
        problems++;
    }
}
if(!snapshot.Jobs.Keys.Any(k => k.StartsWith("power@", StringComparison.Ordinal))) {
    Console.WriteLine("! no per-surface power job — is the initial scan still running? (see 'scanning' above)");
    problems++;
}

Console.WriteLine(problems == 0 ? "\nlooks healthy." : $"\n{problems} problem(s) found.");
return problems == 0 ? 0 : 1;

static string Pretty(string json) {
    try {
        using JsonDocument doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    } catch(JsonException) {
        return json;
    }
}
