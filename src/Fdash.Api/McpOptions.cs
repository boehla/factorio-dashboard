namespace Fdash.Api;

/// <summary>
/// Einstellungen des MCP-Servers.
///
/// Der lesende Pfad fasst das Spiel nicht an — er liest nur, was der Collector
/// ohnehin schon geholt hat. Deshalb gibt es hier kein Rate-Limit; begrenzt
/// wird stattdessen die Antwortgroesse, denn die kostet Tokens.
/// </summary>
public sealed class McpOptions {
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Schreibende Tools sind aus, bis das hier bewusst gesetzt wird. Zusaetzlich
    /// muss das Tool in <see cref="WriteToolWhitelist"/> stehen — eine Whitelist,
    /// keine Blacklist.
    /// </summary>
    public bool AllowWriteTools { get; set; } = false;

    public string[] WriteToolWhitelist { get; set; } = new[] { "set_research" };

    /// <summary>Oberflaeche, wenn ein Aufruf keine angibt (leer = Hauptoberflaeche aus meta).</summary>
    public string? DefaultSurface { get; set; }

    /// <summary>Obergrenze fuer jede Listenantwort, unabhaengig vom angefragten limit.</summary>
    public int MaxItems { get; set; } = 50;

    /// <summary>
    /// Richtwert fuer die Antwortgroesse in Zeichen (~4 Zeichen je Token). Wird
    /// von den Selbsttests geprueft und bei Ueberschreitung geloggt.
    /// </summary>
    public int MaxResponseChars { get; set; } = 16000;

    public bool IsWriteAllowed(string tool) =>
        AllowWriteTools && Array.IndexOf(WriteToolWhitelist, tool) >= 0;
}
