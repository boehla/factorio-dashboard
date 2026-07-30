namespace Fdash.Core;

/// <summary>Ein einzelner Zeitreihen-Datenpunkt. (Plan §5.5)</summary>
public sealed record Sample(
    string SaveId,
    string Metric,   // z.B. "power.production"
    string Labels,   // z.B. "surface=nauvis,network=1"
    long Ts,         // unix seconds
    double Value);

public enum Resolution {
    Raw,    // 5 s
    Minute, // 1 min
    Quarter,// 15 min
    Hour    // 1 h
}
