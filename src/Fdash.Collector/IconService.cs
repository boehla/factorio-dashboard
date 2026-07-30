using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fdash.Collector;

/// <summary>
/// Extrahiert Item-Icons (PNG) aus dem Base-Game-Data-Verzeichnis und den
/// Mod-Zips (Plan §10.6, F12).
///
/// Primaerquelle ist <c>data-raw-dump.json</c> (`factorio --dump-data`): dort
/// steht pro Prototyp der echte Icon-Pfad (<c>__mod__/graphics/icons/x.png</c>).
/// Ohne den Dump bleibt der alte Heuristik-Weg: PNG-Dateiname == Prototyp-Name.
/// Der deckt alle Items ab, deren Icon-Datei zufaellig so heisst wie das Item,
/// aber z.B. nicht wood-seedling -> mip/seedling-1.png.
///
/// Ergebnis wird im RAM gecacht; unbekannte Namen -> null (Frontend zeigt Text).
/// </summary>
public sealed class IconService {
    private readonly IconOptions options;
    private readonly ILogger<IconService> log;
    // Fallback-Index nach Dateinamen-Stamm: stem -> Quelle
    private readonly Dictionary<string, IconSource> index = new(StringComparer.OrdinalIgnoreCase);
    // Prototyp-Name -> "__mod__/pfad.png" aus dem Data-Raw-Dump
    private Dictionary<string, string> protoIcons = new(StringComparer.Ordinal);
    // Mod-Name -> Zip-Datei bzw. entpacktes Verzeichnis
    private readonly Dictionary<string, string> modZips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> modDirs = new(StringComparer.OrdinalIgnoreCase);
    private string? dataPath;
    private readonly Dictionary<string, byte[]?> pngCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private bool built;

    public IconService(IOptions<IconOptions> options, ILogger<IconService> log) {
        this.options = options.Value;
        this.log = log;
    }

    public void EnsureIndex() {
        if(built || !options.Enabled) return;
        lock(gate) {
            if(built) return;
            try {
                dataPath = expand(options.FactorioDataPath);
                string? modsPath = expand(options.ModsPath);
                if(!string.IsNullOrWhiteSpace(dataPath) && Directory.Exists(dataPath)) {
                    indexDirectory(dataPath!);
                }
                if(!string.IsNullOrWhiteSpace(modsPath) && Directory.Exists(modsPath)) {
                    indexMods(modsPath!);
                }
                loadDataRawDump();
                log.LogInformation("Icon index built: {Stems} file stems, {Protos} prototype icons, {Mods} mods.",
                    index.Count, protoIcons.Count, modZips.Count + modDirs.Count);
            } catch(Exception ex) {
                log.LogWarning(ex, "Icon index build failed");
            }
            built = true;
        }
    }

    /// <summary>Liefert die PNG-Bytes fuer einen Item-Namen oder null.</summary>
    public byte[]? GetIcon(string name, IReadOnlyDictionary<string, string>? iconStems = null) {
        EnsureIndex();
        lock(gate) {
            if(pngCache.TryGetValue(name, out byte[]? cached)) return cached;
            byte[]? bytes = null;
            // 1. Exakter Icon-Pfad aus dem Data-Raw-Dump.
            if(protoIcons.TryGetValue(name, out string? iconPath)) {
                bytes = readIconPath(iconPath);
            }
            // 2. Prototyp-Mapping aus dem Lua-Export (Altpfad, seit 2.0 leer).
            if(bytes == null && iconStems != null && iconStems.TryGetValue(name, out string? stem)
                && index.TryGetValue(stem, out IconSource srcFromStem)) {
                bytes = read(srcFromStem, stem);
            }
            // 3. Heuristik: name == Dateiname (iron-plate -> iron-plate.png)
            if(bytes == null && index.TryGetValue(name, out IconSource src)) {
                bytes = read(src, name);
            }
            pngCache[name] = bytes;
            return bytes;
        }
    }

    private byte[]? read(IconSource src, string what) {
        try {
            return src.Read();
        } catch(Exception ex) {
            log.LogDebug(ex, "icon read {What}", what);
            return null;
        }
    }

    // "__pyalienlifegraphics__/graphics/icons/mip/seedling-1.png" -> Bytes.
    // Basisspiel-Mods (base/core/space-age/…) liegen als Ordner unter data/,
    // alle anderen als Zip (oder entpackt) im mods-Verzeichnis.
    private byte[]? readIconPath(string iconPath) {
        if(!iconPath.StartsWith("__", StringComparison.Ordinal)) return null;
        int end = iconPath.IndexOf("__", 2, StringComparison.Ordinal);
        if(end < 0) return null;
        string mod = iconPath.Substring(2, end - 2);
        string rest = iconPath.Substring(end + 2).TrimStart('/');
        // Manche Mods bauen ihre Pfade mit doppeltem Trenner ("icons//x.png").
        while(rest.Contains("//", StringComparison.Ordinal)) rest = rest.Replace("//", "/");
        if(rest.Length == 0) return null;

        if(dataPath != null) {
            string file = Path.Combine(dataPath, mod, rest.Replace('/', Path.DirectorySeparatorChar));
            if(File.Exists(file)) return read(new IconSource(file, null), iconPath);
        }
        if(modDirs.TryGetValue(mod, out string? dir)) {
            string file = Path.Combine(dir, rest.Replace('/', Path.DirectorySeparatorChar));
            if(File.Exists(file)) return read(new IconSource(file, null), iconPath);
        }
        if(modZips.TryGetValue(mod, out string? zip)) {
            string? entry = findZipEntry(zip, rest);
            if(entry != null) return read(new IconSource(null, (zip, entry)), iconPath);
        }
        return null;
    }

    // Im Zip liegt alles unter "<mod>_<version>/". Der Ordnername entspricht in
    // der Regel dem Dateinamen, sicherheitshalber wird sonst per Suffix gesucht.
    private static string? findZipEntry(string zip, string rest) {
        try {
            using(ZipArchive archive = ZipFile.OpenRead(zip)) {
                string direct = Path.GetFileNameWithoutExtension(zip) + "/" + rest;
                if(archive.GetEntry(direct) != null) return direct;
                string suffix = "/" + rest;
                foreach(ZipArchiveEntry e in archive.Entries) {
                    if(e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return e.FullName;
                }
            }
        } catch { }
        return null;
    }

    private void loadDataRawDump() {
        string? dump = expand(options.DataRawDumpPath);
        if(string.IsNullOrWhiteSpace(dump) || !File.Exists(dump)) {
            log.LogInformation("No data-raw-dump.json at {Path} — icons fall back to filename matching. "
                + "Run `factorio --dump-data` once for complete icons.", dump ?? "(unset)");
            return;
        }
        try {
            protoIcons = DataRawIcons.Parse(dump!);
        } catch(Exception ex) {
            log.LogWarning(ex, "Failed to parse {Path}", dump);
        }
    }

    private static string? expand(string? path) =>
        string.IsNullOrWhiteSpace(path) ? path : Environment.ExpandEnvironmentVariables(path);

    private void indexDirectory(string root) {
        // Nur icon-Verzeichnisse durchsuchen, um nicht das ganze data/ zu scannen.
        foreach(string dir in Directory.EnumerateDirectories(root, "icons", SearchOption.AllDirectories)) {
            foreach(string png in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories)) {
                string stem = Path.GetFileNameWithoutExtension(png);
                if(!index.ContainsKey(stem)) index[stem] = new IconSource(png, null);
            }
        }
    }

    private void indexMods(string modsPath) {
        // Entpackte Mods (Ordner "name" oder "name_version").
        foreach(string dir in Directory.EnumerateDirectories(modsPath)) {
            string name = Path.GetFileName(dir);
            registerMod(modDirs, stripVersion(name), dir);
        }
        foreach(string zip in Directory.EnumerateFiles(modsPath, "*.zip")) {
            string name = stripVersion(Path.GetFileNameWithoutExtension(zip));
            registerMod(modZips, name, zip);
            try {
                using(ZipArchive archive = ZipFile.OpenRead(zip)) {
                    foreach(ZipArchiveEntry entry in archive.Entries) {
                        if(!entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                        if(!entry.FullName.Contains("/icons/", StringComparison.OrdinalIgnoreCase)) continue;
                        string stem = Path.GetFileNameWithoutExtension(entry.Name);
                        // Mods duerfen Base ueberschreiben -> immer setzen
                        index[stem] = new IconSource(null, (zip, entry.FullName));
                    }
                }
            } catch(Exception ex) {
                log.LogDebug(ex, "skip mod zip {Zip}", zip);
            }
        }
    }

    // Bei mehreren Versionen desselben Mods (flib_0.15.0 + flib_0.16.5) gewinnt
    // die hoechste — Factorio laedt ebenfalls nur die neueste.
    private static void registerMod(Dictionary<string, string> map, string name, string path) {
        if(!map.TryGetValue(name, out string? existing)) {
            map[name] = path;
            return;
        }
        if(compareVersion(versionOf(path), versionOf(existing)) > 0) map[name] = path;
    }

    private static string stripVersion(string name) {
        int i = name.LastIndexOf('_');
        if(i <= 0) return name;
        string tail = name.Substring(i + 1);
        return tail.Length > 0 && char.IsDigit(tail[0]) ? name.Substring(0, i) : name;
    }

    private static string versionOf(string path) {
        string name = Path.GetFileNameWithoutExtension(path);
        int i = name.LastIndexOf('_');
        return i > 0 ? name.Substring(i + 1) : "";
    }

    private static int compareVersion(string a, string b) {
        string[] pa = a.Split('.');
        string[] pb = b.Split('.');
        for(int i = 0; i < Math.Max(pa.Length, pb.Length); i++) {
            int va = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
            if(va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}

/// <summary>Quelle eines Icons: entweder Datei oder Eintrag in einem Zip.</summary>
public readonly struct IconSource {
    private readonly string? filePath;
    private readonly (string zip, string entry)? zipEntry;

    public IconSource(string? filePath, (string zip, string entry)? zipEntry) {
        this.filePath = filePath;
        this.zipEntry = zipEntry;
    }

    public byte[] Read() {
        if(filePath != null) return File.ReadAllBytes(filePath);
        using(ZipArchive archive = ZipFile.OpenRead(zipEntry!.Value.zip)) {
            ZipArchiveEntry? e = archive.GetEntry(zipEntry.Value.entry);
            if(e == null) return Array.Empty<byte>();
            using(Stream s = e.Open())
            using(MemoryStream ms = new MemoryStream()) {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
