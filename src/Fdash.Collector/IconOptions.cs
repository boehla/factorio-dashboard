namespace Fdash.Collector;

/// <summary>Pfade fuer die Icon-Extraktion (Plan §10.6, F12). Windows-Defaults.</summary>
public sealed class IconOptions {
    // z.B. C:\Program Files (x86)\Steam\steamapps\common\Factorio\data
    public string? FactorioDataPath { get; set; }
    // z.B. %APPDATA%\Factorio\mods
    public string? ModsPath { get; set; }
    // data-raw-dump.json aus `factorio --dump-data`. Liefert die exakte
    // Zuordnung Prototyp -> Icon-Datei; ohne den Dump wird nur ueber den
    // Dateinamen geraten (Items wie wood-seedling -> seedling-1.png fehlen dann).
    public string? DataRawDumpPath { get; set; }
    public bool Enabled { get; set; } = true;
}
