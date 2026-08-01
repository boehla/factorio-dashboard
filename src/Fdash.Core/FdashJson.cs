using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fdash.Core;

/// <summary>
/// Serialisierung fuer alles, was der Server selbst erzeugt (abgeleitete Jobs,
/// MCP-Antworten).
///
/// snake_case ist hier kein Geschmack, sondern Pflicht: die Payloads aus dem Mod
/// sind durchgehend snake_case, und ein Consumer soll nicht raten muessen, ob
/// ein Feld aus Lua oder aus C# stammt. Ohne das hiess derselbe Wert einmal
/// <c>since_seconds</c> und einmal <c>SinceSeconds</c>.
/// </summary>
public static class FdashJson {
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
