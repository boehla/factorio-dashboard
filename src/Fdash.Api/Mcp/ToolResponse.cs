using Fdash.Analysis;
using Fdash.Core;

namespace Fdash.Api.Mcp;

/// <summary>
/// Gemeinsame Form aller MCP-Antworten.
///
/// Zwei Regeln stecken hier drin, und beide sind wichtiger als sie aussehen:
///
/// 1. **Jede Liste ist gedeckelt.** Eine Pyanodons-Basis hat sechsstellige
///    Entity-Zahlen; eine ungebremste Liste sprengt jedes Kontextfenster. Wird
///    gekuerzt, steht das mit <c>truncated</c> und <c>total_available</c> in der
///    Antwort — stillschweigend abschneiden waere schlimmer als gar nichts
///    liefern, weil das Modell die Luecke sonst nicht bemerkt.
///
/// 2. **Jede Antwort traegt ihr Alter.** Die Jobs im Mod laufen mit sehr
///    unterschiedlichen Intervallen. Ohne <c>data_age_seconds</c> kann ein
///    Verbraucher nicht unterscheiden zwischen "der Wert ist 0" und "der Wert
///    ist zehn Minuten alt".
/// </summary>
public static class ToolResponse {
    public static string Ok(object payload) => FdashJson.Serialize(payload);

    /// <summary>
    /// Fehler nie werfen, sondern zurueckgeben: ein geworfener Fehler kommt beim
    /// Aufrufer als Protokollfehler ohne Hinweis an, was zu tun waere.
    /// </summary>
    public static string Error(string error, string? hint = null) =>
        FdashJson.Serialize(new { error, hint });

    /// <summary>Der haeufigste Fehlerfall: der Job hat noch nichts veroeffentlicht.</summary>
    public static string NoData(string job, string? surface = null) => Error(
        surface == null ? $"Job '{job}' hat noch keine Daten geliefert."
                        : $"Job '{job}' hat fuer '{surface}' noch keine Daten geliefert.",
        "Laeuft der Factorio-Server mit dem Mod fdash-exporter? Der erste Durchlauf eines Jobs "
        + "dauert je nach Fabrikgroesse einige Sekunden bis Minuten. Zustand: get_health.");

    /// <summary>Kuerzt auf das kleinere aus angefragtem Limit und Serverdeckel.</summary>
    public static (List<T> Items, bool Truncated, int Total) Cap<T>(IReadOnlyList<T> source, int limit, int maxItems) {
        int effective = Math.Max(1, Math.Min(limit <= 0 ? maxItems : limit, maxItems));
        List<T> items = new List<T>();
        for(int i = 0; i < source.Count && i < effective; i++) items.Add(source[i]);
        return (items, source.Count > items.Count, source.Count);
    }

    /// <summary>Runden — Nachkommastellen jenseits der Anzeigegenauigkeit sind nur Tokens.</summary>
    public static double R(double value, int digits = 2) =>
        double.IsFinite(value) ? Math.Round(value, digits) : 0;

    /// <summary>Watt -&gt; MW, wie in der Strom-GUI des Spiels.</summary>
    public static double Mw(double watt) => R(watt / 1_000_000, 2);

    /// <summary>Alter eines Payloads, oder null wenn es ihn nicht gibt.</summary>
    public static int? Age(JobPayload? payload) => payload?.AgeSeconds;
}
