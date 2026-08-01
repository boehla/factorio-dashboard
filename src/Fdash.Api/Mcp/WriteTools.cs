using System.ComponentModel;
using System.Text.Json;
using Fdash.Analysis;
using Fdash.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Fdash.Api.Mcp;

/// <summary>
/// Der einzige schreibende Pfad ins Spiel.
///
/// Dreifach abgesichert, und zwar bewusst an drei verschiedenen Stellen:
/// <list type="number">
/// <item>Der Server muss Schreiben erlauben (<c>Mcp:AllowWriteTools</c>) UND das
///       Tool muss auf der Whitelist stehen. Keine Blacklist — was nicht
///       ausdruecklich erlaubt ist, geht nicht.</item>
/// <item>Es braucht RCON. Ueber die Dateiausgabe kann der Mod nur senden.</item>
/// <item>Der Mod selbst validiert: nur eine freigeschaltete, unerforschte
///       Technologie bei leerer Warteschlange (control.lua). Das ist die
///       Absicherung, die zaehlt, denn sie gilt auch dann, wenn jemand anders
///       den Aufruf schickt.</item>
/// </list>
/// Es gibt bewusst kein Tool, das beliebiges Lua ausfuehrt — alles laeuft ueber
/// das feste remote-Interface des Mods.
/// </summary>
[McpServerToolType]
public static class WriteTools {

    [McpServerTool(Name = "set_research")]
    [Description("SCHREIBEND: setzt die laufende Forschung. Funktioniert nur, wenn der Server "
        + "Schreiben erlaubt (Mcp:AllowWriteTools), RCON verfuegbar ist und die Warteschlange leer "
        + "ist. Der Mod lehnt unbekannte, bereits erforschte oder gesperrte Technologien ab.")]
    public static async Task<string> SetResearch(
            SnapshotView view, IGameControl control, IOptions<McpOptions> opts,
            [Description("Name der Technologie, z. B. aus suggest_next_research")] string tech,
            CancellationToken ct = default) {
        if(string.IsNullOrWhiteSpace(tech)) return ToolResponse.Error("tech fehlt.");

        if(!opts.Value.IsWriteAllowed("set_research")) {
            return ToolResponse.Error(
                "Schreibende Tools sind abgeschaltet.",
                "In appsettings.json unter Mcp: AllowWriteTools auf true setzen und 'set_research' "
                + "in der WriteToolWhitelist lassen. Bis dahin liefert suggest_next_research den "
                + "Vorschlag, gesetzt wird er von Hand.");
        }

        if(!control.Available) {
            return ToolResponse.Error(
                "Kein RCON — ins Spiel schreiben geht nur darueber.",
                "Collector:RconPassword setzen (appsettings.Local.json oder Umgebungsvariable "
                + "Collector__RconPassword) und den Factorio-Server mit aktivem RCON starten.");
        }

        try {
            string raw = await control.SetResearchAsync(tech, ct);
            // Der Mod antwortet als JSON und begruendet eine Ablehnung selbst;
            // das durchzureichen ist ehrlicher als es hier neu zu formulieren.
            string result = "?", reason = "";
            try {
                using(JsonDocument doc = JsonDocument.Parse(raw)) {
                    result = Json.Str(doc.RootElement, "result", "?");
                    reason = Json.Str(doc.RootElement, "reason");
                }
            } catch(JsonException) {
                return ToolResponse.Error("Unerwartete Antwort des Mods: " + raw);
            }

            return ToolResponse.Ok(new {
                tech,
                result,
                reason = reason.Length > 0 ? reason : null,
                accepted = result == "ok",
                hint = result == "ok" ? null : hintFor(reason)
            });
        } catch(Exception ex) {
            return ToolResponse.Error("Der Aufruf ist fehlgeschlagen: " + ex.Message,
                "Laeuft der Factorio-Server noch? Zustand: get_health.");
        }
    }

    private static string? hintFor(string reason) => reason switch {
        "queue_not_empty" => "Es laeuft bereits eine Forschung. Erst im Spiel abbrechen.",
        "done" => "Die Technologie ist schon erforscht.",
        "missing" => "Unbekannter Name — Schreibweise aus suggest_next_research uebernehmen.",
        "disabled" => "Die Technologie ist in diesem Save gesperrt.",
        _ => null
    };
}
