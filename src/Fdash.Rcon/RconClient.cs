using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Fdash.Rcon;

/// <summary>
/// Minimaler Source-RCON-Client (Valve-Protokoll), wie ihn Factorio spricht.
/// Behandelt fragmentierte Antworten (> ~4 kB) ueber die bekannte
/// "leeres Folgepaket"-Technik. Nicht thread-safe: Aufrufe serialisieren
/// (der Collector nutzt genau eine Verbindung, ein Call nach dem anderen).
/// (Plan §2.4, §5)
/// </summary>
public sealed class RconClient : IDisposable {
    private const int TypeAuth = 3;
    private const int TypeAuthResponse = 2;
    private const int TypeExecCommand = 2;
    private const int TypeResponseValue = 0;

    private readonly RconOptions options;
    private TcpClient? tcp;
    private NetworkStream? stream;
    private int nextId = 1;

    public bool Connected => tcp?.Connected == true;

    public RconClient(RconOptions options) {
        this.options = options;
    }

    public async Task ConnectAsync(CancellationToken ct) {
        dispose();
        tcp = new TcpClient { NoDelay = true };
        using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
            cts.CancelAfter(options.TimeoutMs);
            try {
                await tcp.ConnectAsync(options.Host, options.Port, cts.Token);
            } catch(Exception ex) {
                throw new RconException($"RCON connect to {options.Host}:{options.Port} failed", ex);
            }
        }
        stream = tcp.GetStream();
        int authId = nextId++;
        await sendPacketAsync(authId, TypeAuth, options.Password, ct);
        (int id, int type, string _) = await readPacketAsync(ct);
        // Factorio kann ein leeres ResponseValue vor der Auth-Antwort schicken.
        if(type == TypeResponseValue) {
            (id, type, _) = await readPacketAsync(ct);
        }
        if(id == -1 || type != TypeAuthResponse) {
            throw new RconException("RCON authentication failed (wrong password?)");
        }
    }

    /// <summary>Fuehrt ein Kommando aus und liefert die zusammengesetzte Antwort.</summary>
    /// <remarks>
    /// Factorio antwortet je Command mit genau einer RESPONSE_VALUE (auch bei
    /// grossen Payloads, kein 4-kB-Split in der getesteten 2.1.x) und spiegelt
    /// KEINEN Sentinel zurueck. Deshalb: erstes Paket blockierend lesen, dann
    /// nur weiterlesen, solange der Socket noch gepufferte Daten hat — so werden
    /// etwaige Fragmente eingesammelt, ohne auf ein Ende-Paket zu warten.
    /// </remarks>
    public async Task<string> ExecuteAsync(string command, CancellationToken ct) {
        if(stream == null) throw new RconException("RCON not connected");
        int id = nextId++;
        await sendPacketAsync(id, TypeExecCommand, command, ct);

        StringBuilder sb = new StringBuilder();
        (int _, int _, string body) = await readPacketAsync(ct);
        sb.Append(body);

        // evtl. Fragmente einsammeln (defensiv fuer den Fall, dass ein Save
        // doch splittet): nur lesen, wenn wirklich schon Bytes anliegen.
        while(true) {
            if(tcp!.Available == 0) {
                await Task.Delay(25, ct);
                if(tcp.Available == 0) break;
            }
            (int _, int _, string more) = await readPacketAsync(ct);
            sb.Append(more);
        }
        return sb.ToString();
    }

    private async Task sendPacketAsync(int id, int type, string body, CancellationToken ct) {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        int size = 4 + 4 + payload.Length + 2; // id + type + body + two nulls
        byte[] buffer = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), size);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), type);
        payload.CopyTo(buffer.AsSpan(12));
        // letzte zwei Bytes bleiben 0
        await stream!.WriteAsync(buffer.AsMemory(0, buffer.Length), ct);
    }

    private async Task<(int id, int type, string body)> readPacketAsync(CancellationToken ct) {
        byte[] sizeBuf = await readExactAsync(4, ct);
        int size = BinaryPrimitives.ReadInt32LittleEndian(sizeBuf);
        if(size < 10 || size > 4 * 1024 * 1024) throw new RconException($"RCON packet size out of range: {size}");
        byte[] buf = await readExactAsync(size, ct);
        int id = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0));
        int type = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4));
        int bodyLen = size - 4 - 4 - 2;
        string body = bodyLen > 0 ? Encoding.UTF8.GetString(buf, 8, bodyLen) : "";
        return (id, type, body);
    }

    private async Task<byte[]> readExactAsync(int count, CancellationToken ct) {
        byte[] buf = new byte[count];
        int read = 0;
        while(read < count) {
            int n = await stream!.ReadAsync(buf.AsMemory(read, count - read), ct);
            if(n <= 0) throw new RconException("RCON connection closed by server");
            read += n;
        }
        return buf;
    }

    private void dispose() {
        try { stream?.Dispose(); } catch { }
        try { tcp?.Dispose(); } catch { }
        stream = null;
        tcp = null;
    }

    public void Dispose() {
        dispose();
    }
}
