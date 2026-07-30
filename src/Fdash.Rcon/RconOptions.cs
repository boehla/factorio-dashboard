namespace Fdash.Rcon;

public sealed class RconOptions {
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 27015;
    public string Password { get; set; } = "";
    public int TimeoutMs { get; set; } = 8000;
}
