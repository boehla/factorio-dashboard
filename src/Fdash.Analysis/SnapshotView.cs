using System.Text.Json;
using Fdash.Collector;
using Fdash.Core;

namespace Fdash.Analysis;

/// <summary>
/// Ein Job-Payload mit seinem Alter. <paramref name="AgeSeconds"/> ist der
/// wichtigste Teil: die Jobs im Mod laufen mit sehr unterschiedlichen
/// Intervallen (Strom alle 5 s, Erze alle 60 s, Bohrer alle 30 s), und ein
/// Verbraucher muss unterscheiden koennen zwischen "der Wert ist 0" und "der
/// Wert ist von vor zehn Minuten".
/// </summary>
public sealed record JobPayload(string Job, string? Surface, string SaveId, long Ts, int AgeSeconds, JsonElement Data);

/// <summary>
/// Lesesicht auf den <see cref="ISnapshotBus"/>: loest Job + Oberflaeche auf,
/// kennt die Surface-Liste aus dem meta-Job und haengt an jeden Payload sein
/// Alter.
///
/// Einziger Zugriffspunkt fuer Analysen und MCP-Tools — die Aufloesungsregel
/// (erst <c>job@surface</c>, dann der blanke Job-Key) steht hier genau einmal
/// statt in jedem Aufrufer.
/// </summary>
public sealed class SnapshotView {
    private readonly ISnapshotBus bus;

    public SnapshotView(ISnapshotBus bus) {
        this.bus = bus;
    }

    /// <summary>Aktuelle Zeit als Unix-Sekunden — in Tests ueberschreibbar.</summary>
    public Func<long> Now { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Oberflaechen aus dem meta-Job, sortiert wie vom Mod geliefert.</summary>
    public IReadOnlyList<string> Surfaces {
        get {
            JobPayload? meta = Get("meta");
            if(meta == null) return System.Array.Empty<string>();
            return Json.StrList(meta.Data, "surfaces");
        }
    }

    /// <summary>Nauvis, sonst die erste gemeldete Oberflaeche.</summary>
    public string PrimarySurface {
        get {
            IReadOnlyList<string> all = Surfaces;
            foreach(string s in all) {
                if(string.Equals(s, "nauvis", StringComparison.OrdinalIgnoreCase)) return s;
            }
            return all.Count > 0 ? all[0] : "nauvis";
        }
    }

    /// <summary>
    /// Vorhandene Oberflaeche zu einer Nutzereingabe. Leer/unbekannt faellt auf
    /// die Hauptoberflaeche zurueck, damit ein Tippfehler nicht in "keine
    /// Daten" endet.
    /// </summary>
    public string ResolveSurface(string? requested) {
        if(string.IsNullOrWhiteSpace(requested)) return PrimarySurface;
        foreach(string s in Surfaces) {
            if(string.Equals(s, requested, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return PrimarySurface;
    }

    /// <summary>
    /// Payload eines Jobs. Mit <paramref name="surface"/> zuerst
    /// <c>job@surface</c>, sonst der blanke Job-Key (globale Jobs wie trains
    /// und meta haben nur diesen).
    /// </summary>
    public JobPayload? Get(string job, string? surface = null) {
        if(!string.IsNullOrEmpty(surface)) {
            Snapshot? s = bus.Latest(job + "@" + surface);
            if(s != null) return wrap(s, job, surface);
        }
        Snapshot? bare = bus.Latest(job);
        return bare == null ? null : wrap(bare, job, null);
    }

    /// <summary>Alle bekannten Job-Keys mit ihrem Alter — fuer get_health.</summary>
    public IReadOnlyList<JobPayload> All() {
        List<JobPayload> list = new List<JobPayload>();
        foreach(KeyValuePair<string, Snapshot> kv in bus.All()) {
            int at = kv.Key.IndexOf('@');
            string job = at < 0 ? kv.Key : kv.Key[..at];
            string? surface = at < 0 ? null : kv.Key[(at + 1)..];
            list.Add(wrap(kv.Value, job, surface));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Job, b.Job));
        return list;
    }

    /// <summary>Save-Id des zuletzt gesehenen Snapshots (leer, wenn noch nichts ankam).</summary>
    public string SaveId => Get("meta")?.SaveId ?? "";

    private JobPayload wrap(Snapshot s, string job, string? surface) =>
        new JobPayload(job, surface, s.SaveId, s.Ts, (int)Math.Max(0, Now() - s.Ts), s.Payload);
}
