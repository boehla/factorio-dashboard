namespace Fdash.Analysis;

/// <summary>
/// Einordnung eines Puffers. Der Fuellstand allein sagt wenig — erst zusammen
/// mit der Richtung wird daraus eine Suchrichtung:
///
/// * voll und steigend  -> der Abnehmer fehlt, downstream weitersuchen
/// * leer und fallend   -> der Erzeuger fehlt, upstream weitersuchen
/// * dazwischen         -> laeuft
/// </summary>
public enum BufferState {
    Empty,
    Draining,
    Healthy,
    Filling,
    Full
}

public static class BufferClassifier {
    public const double FullAt = 0.9;
    public const double EmptyAt = 0.05;

    /// <summary>
    /// <paramref name="fill"/> ist 0..1, <paramref name="trend"/> die Richtung
    /// aus der Zeitreihe (rising/falling/stable/unknown).
    /// </summary>
    public static BufferState Classify(double fill, string trend) {
        if(fill >= FullAt) return BufferState.Full;
        if(fill <= EmptyAt) return BufferState.Empty;
        if(trend == "falling") return BufferState.Draining;
        if(trend == "rising") return BufferState.Filling;
        return BufferState.Healthy;
    }

    /// <summary>Wo weitergesucht werden sollte, oder null wenn alles in Ordnung ist.</summary>
    public static string? Direction(BufferState state) => state switch {
        BufferState.Full => "downstream",
        BufferState.Filling => "downstream",
        BufferState.Empty => "upstream",
        BufferState.Draining => "upstream",
        _ => null
    };

    public static string Name(BufferState state) => state switch {
        BufferState.Empty => "empty",
        BufferState.Draining => "draining",
        BufferState.Filling => "filling",
        BufferState.Full => "full",
        _ => "healthy"
    };
}
