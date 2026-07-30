using System.Security.Cryptography;
using System.Text;
using Fdash.Core;

namespace Fdash.Collector;

/// <summary>
/// Bildet aus Discovery-Werten einen stabilen save_id-Hash (Plan §4.3, F6).
/// Basis: Map-Seed + sortierte Surface-Liste. Wechselt der Server auf ein
/// anderes Save (anderer Seed), entsteht automatisch eine neue Zeitreihe.
/// Fuer den seltenen Fall zweier Saves aus demselben Seed (Kopie) kann per
/// Config eine manuelle SaveIdOverride gesetzt werden.
/// </summary>
public static class SaveFingerprint {
    public static string Compute(Discovery d, string? overrideId) {
        if(!string.IsNullOrWhiteSpace(overrideId)) return overrideId!;
        string surfaces = string.Join(",", d.Surfaces.OrderBy(s => s, StringComparer.Ordinal));
        string basis = $"{d.Seed}|{surfaces}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
