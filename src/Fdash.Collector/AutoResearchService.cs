using System.Text.Json;
using Fdash.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fdash.Collector;

/// <summary>
/// Auto-Research: waehlt die schnellste Tech, deren Packs aktuell produziert
/// werden, und nur bei leerer Queue. Laeuft als Preview, solange
/// <see cref="Enabled"/> aus ist. Fuehrt ein Audit-Log.
///
/// Die Kandidatenliste kommt jetzt aus dem <c>research_state</c>-Job des
/// Snapshots — kein eigener Abruf mehr. Geschrieben wird ueber
/// <see cref="IGameControl"/>, also ueber RCON; ohne RCON bleibt es zwangslaeufig
/// bei der Preview.
/// </summary>
public sealed class AutoResearchService {
    private readonly IGameControl control;
    private readonly CollectorOptions options;
    private readonly ILogger<AutoResearchService> log;
    private readonly List<AuditEntry> auditLog = new();

    public bool Enabled { get; set; }
    public IReadOnlyList<AuditEntry> Audit => auditLog;

    /// <summary>Ohne RCON kann nur eine Vorschau berechnet werden.</summary>
    public bool CanWrite => control.Available;

    public AutoResearchService(IGameControl control, IOptions<CollectorOptions> options, ILogger<AutoResearchService> log) {
        this.control = control;
        this.options = options.Value;
        this.log = log;
        Enabled = options.Value.AutoResearchEnabled;
    }

    /// <summary>
    /// Bester Kandidat aus dem research_state-Payload.
    /// <paramref name="producedPacks"/> = aktuell produzierte Items.
    /// </summary>
    public ResearchChoice? Evaluate(JsonElement researchState, ISet<string> producedPacks) {
        int queueLen = researchState.TryGetProperty("queue_len", out JsonElement q) ? q.GetInt32() : 1;
        if(queueLen > 0) return null; // Sicherheitsregel: nur bei leerer Queue

        double speedBonus = researchState.TryGetProperty("research_speed_bonus", out JsonElement sb) ? sb.GetDouble() : 0;
        int activeLabs = researchState.TryGetProperty("active_labs", out JsonElement al) ? al.GetInt32() : 0;
        double labSpeed = researchState.TryGetProperty("lab_speed", out JsonElement ls) ? ls.GetDouble() : 1;
        double effRate = Math.Max(0.0001, activeLabs * labSpeed * (1 + speedBonus));

        ResearchChoice? best = null;
        if(!researchState.TryGetProperty("candidates", out JsonElement cands) || cands.ValueKind != JsonValueKind.Array) {
            return null;
        }

        foreach(JsonElement c in cands.EnumerateArray()) {
            string name = c.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            if(name.Length == 0 || isBlacklisted(name)) continue;

            int level = c.TryGetProperty("level", out JsonElement lv) ? lv.GetInt32() : 0;
            if(options.MaxInfiniteLevel is int max && level > max) continue;

            double unitCount = c.TryGetProperty("unit_count", out JsonElement uc) ? uc.GetDouble() : 0;
            double energy = c.TryGetProperty("energy", out JsonElement en) ? en.GetDouble() : 0;

            // Alle Packs muessen aktiv produziert werden.
            bool allAvailable = true;
            if(c.TryGetProperty("ingredients", out JsonElement ings) && ings.ValueKind == JsonValueKind.Array) {
                foreach(JsonElement ing in ings.EnumerateArray()) {
                    string pack = ing.TryGetProperty("name", out JsonElement pn) ? pn.GetString() ?? "" : "";
                    if(!producedPacks.Contains(pack)) { allAvailable = false; break; }
                }
            }
            if(!allAvailable) continue;

            // zeit = (unit_count * energy) / effektive_lab_rate
            double seconds = unitCount * energy / effRate;
            if(best == null || seconds < best.EstimatedSeconds ||
               (Math.Abs(seconds - best.EstimatedSeconds) < 0.001 && level < best.Level)) {
                best = new ResearchChoice(name, level, seconds);
            }
        }
        return best;
    }

    /// <summary>Setzt die Forschung — nur wenn Enabled und RCON verfuegbar.</summary>
    public async Task<string> ApplyAsync(ResearchChoice choice, CancellationToken ct) {
        if(!Enabled) {
            record(choice, "preview");
            return "preview";
        }
        if(!control.Available) {
            record(choice, "no_rcon");
            return "no_rcon";
        }
        try {
            string raw = await control.SetResearchAsync(choice.Tech, ct);
            string result = "?";
            try {
                using(JsonDocument doc = JsonDocument.Parse(raw)) {
                    if(doc.RootElement.TryGetProperty("result", out JsonElement r)) result = r.GetString() ?? "?";
                }
            } catch(JsonException) {
                result = "unparsable";
            }
            record(choice, result);
            log.LogInformation("Auto-research set {Tech} -> {Result}", choice.Tech, result);
            return result;
        } catch(Exception ex) {
            log.LogWarning(ex, "Auto-research write failed for {Tech}", choice.Tech);
            record(choice, "error");
            return "error";
        }
    }

    private bool isBlacklisted(string name) {
        foreach(string prefix in options.ResearchBlacklistPrefixes) {
            if(name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void record(ResearchChoice c, string result) {
        auditLog.Add(new AuditEntry(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), c.Tech, c.EstimatedSeconds, result));
        if(auditLog.Count > 500) auditLog.RemoveAt(0);
    }
}

public sealed record ResearchChoice(string Tech, int Level, double EstimatedSeconds);
public sealed record AuditEntry(long Ts, string Tech, double EstimatedSeconds, string Result);
