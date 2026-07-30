namespace Fdash.Core;

/// <summary>Beschreibt einen periodischen Collector-Job. (Plan §5.3)</summary>
public sealed record JobSpec(
    string Name,
    TimeSpan Interval,
    bool Persist,
    bool Batchable);
