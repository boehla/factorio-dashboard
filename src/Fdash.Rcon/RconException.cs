namespace Fdash.Rcon;

public sealed class RconException : Exception {
    public RconException(string message) : base(message) { }
    public RconException(string message, Exception inner) : base(message, inner) { }
}
