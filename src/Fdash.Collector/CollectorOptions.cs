namespace Fdash.Collector;

public sealed class CollectorOptions {
    /// <summary>"Auto" (Default), "File" oder "Rcon" — siehe GameLink.</summary>
    public string? Transport { get; set; } = "Auto";

    /// <summary>
    /// Verzeichnis von Factorios script-output. Der Mod legt darunter
    /// <c>fdash/</c> an. Umgebungsvariablen werden aufgeloest.
    /// </summary>
    public string? ScriptOutputPath { get; set; }

    /// <summary>
    /// Wie oft nach einem neuen Snapshot gesehen wird. Der Mod schreibt per
    /// Default alle 5 s (<c>fdash-write-interval</c>); haeufiger abzufragen
    /// kostet im Dateimodus fast nichts, im RCON-Modus je einen winzigen
    /// seq-Call.
    /// </summary>
    public int PollIntervalMs { get; set; } = 1000;

    // --- RCON (optional: nur fuer den RCON-Transport und fuer Auto-Research) ---
    public string Host { get; set; } = "127.0.0.1";
    public int RconPort { get; set; } = 27015;
    public string RconPassword { get; set; } = "";

    public bool RconConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(RconPassword);

    public string? SaveIdOverride { get; set; }

    // --- Auto-Research ---
    public bool AutoResearchEnabled { get; set; } = false;
    public string[] ResearchBlacklistPrefixes { get; set; } = Array.Empty<string>();
    public int? MaxInfiniteLevel { get; set; }
    public int AutoResearchIntervalSeconds { get; set; } = 15;

    // --- Alert-Schwellwerte — Defaults, pro Save ueberschreibbar ---
    public double PowerSatisfactionWarn { get; set; } = 0.95;
    public double FuelLowPct { get; set; } = 0.25;
    public int StallSecondsWarn { get; set; } = 60;
}
