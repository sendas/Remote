using System.Net.WebSockets;
using System.Text;

namespace ClassroomAgent;

/// <summary>
/// Cliente WebSocket mínimo (RFC 6455, só a parte de "frames") sobre um stream já ligado — aqui, o named pipe.
/// O .NET Framework não tem WebSocket.CreateFromStream e o ClientWebSocket não funciona em Windows 7,
/// por isso a versão para Windows 7/8.1 usa esta classe. O protocolo é o mesmo que o servidor fala.
/// </summary>
internal sealed class MiniWebSocket : WebSocket
{
    private const byte OpCont = 0, OpText = 1, OpBinary = 2, OpClose = 8, OpPing = 9, OpPong = 10;

    private readonly Stream _s;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Random _rng = new();
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDesc;

    // frame de dados em curso
    private long _remaining;
    private bool _fin = true;
    private WebSocketMessageType _msgType = WebSocketMessageType.Binary;

    public MiniWebSocket(Stream s) => _s = s;

    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => _closeDesc;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken ct)
    {
        var op = messageType switch
        {
            WebSocketMessageType.Text => OpText,
            WebSocketMessageType.Binary => OpBinary,
            _ => OpClose,
        };
        await SendFrame(op, buffer, ct);
    }

    private async Task SendFrame(byte opcode, ArraySegment<byte> payload, CancellationToken ct)
    {
        var len = payload.Count;
        var header = new byte[14];
        var h = 0;
        header[h++] = (byte)(0x80 | opcode); // FIN
        if (len < 126) header[h++] = (byte)(0x80 | len);
        else if (len <= 0xFFFF)
        {
            header[h++] = 0x80 | 126;
            header[h++] = (byte)(len >> 8);
            header[h++] = (byte)len;
        }
        else
        {
            header[h++] = 0x80 | 127;
            for (var i = 7; i >= 0; i--) header[h++] = (byte)((long)len >> (8 * i));
        }
        var mask = new byte[4];
        lock (_rng) _rng.NextBytes(mask);
        Buffer.BlockCopy(mask, 0, header, h, 4);
        h += 4;

        // o cliente tem de "mascarar" os dados (exigência do protocolo)
        var data = new byte[h + len];
        Buffer.BlockCopy(header, 0, data, 0, h);
        for (var i = 0; i < len; i++) data[h + i] = (byte)(payload.Array![payload.Offset + i] ^ mask[i & 3]);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _s.WriteAsync(data, 0, data.Length, ct);
            await _s.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            if (_remaining == 0 && _fin && _state != WebSocketState.Open)
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _closeStatus, _closeDesc);

            if (_remaining == 0)
            {
                // novo frame
                var b = await ReadExact(2, ct);
                var fin = (b[0] & 0x80) != 0;
                var op = (byte)(b[0] & 0x0F);
                var masked = (b[1] & 0x80) != 0;
                long len = b[1] & 0x7F;
                if (len == 126) { var e = await ReadExact(2, ct); len = (e[0] << 8) | e[1]; }
                else if (len == 127) { var e = await ReadExact(8, ct); len = 0; for (var i = 0; i < 8; i++) len = (len << 8) | e[i]; }
                byte[]? key = masked ? await ReadExact(4, ct) : null;

                if (op >= 8)
                {
                    // frames de controlo: lidos por inteiro e tratados aqui
                    var p = len > 0 ? await ReadExact((int)len, ct) : new byte[0];
                    if (key != null) for (var i = 0; i < p.Length; i++) p[i] ^= key[i & 3];
                    if (op == OpPing) await SendFrame(OpPong, new ArraySegment<byte>(p), ct);
                    else if (op == OpClose)
                    {
                        _closeStatus = p.Length >= 2 ? (WebSocketCloseStatus)((p[0] << 8) | p[1]) : WebSocketCloseStatus.NormalClosure;
                        _closeDesc = p.Length > 2 ? Encoding.UTF8.GetString(p, 2, p.Length - 2) : "";
                        _state = WebSocketState.CloseReceived;
                        try { await SendFrame(OpClose, new ArraySegment<byte>(p.Length >= 2 ? new[] { p[0], p[1] } : new byte[0]), ct); } catch { }
                        _state = WebSocketState.Closed;
                        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _closeStatus, _closeDesc);
                    }
                    continue; // pong: ignora
                }

                if (op == OpText) _msgType = WebSocketMessageType.Text;
                else if (op == OpBinary) _msgType = WebSocketMessageType.Binary;
                // OpCont mantém o tipo anterior
                _fin = fin;
                _remaining = len;
                if (key != null) throw new WebSocketException("O servidor enviou dados mascarados.");
                if (len == 0) return new WebSocketReceiveResult(0, _msgType, _fin);
            }

            var want = (int)Math.Min(buffer.Count, _remaining);
            var n = await _s.ReadAsync(buffer.Array!, buffer.Offset, want, ct);
            if (n <= 0) { _state = WebSocketState.Aborted; throw new WebSocketException("Ligação fechada."); }
            _remaining -= n;
            return new WebSocketReceiveResult(n, _msgType, _remaining == 0 && _fin);
        }
    }

    private async Task<byte[]> ReadExact(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var off = 0;
        while (off < n)
        {
            var r = await _s.ReadAsync(buf, off, n - off, ct);
            if (r <= 0) { _state = WebSocketState.Aborted; throw new WebSocketException("Ligação fechada."); }
            off += r;
        }
        return buf;
    }

    public override async Task CloseAsync(WebSocketCloseStatus status, string? desc, CancellationToken ct) =>
        await CloseOutputAsync(status, desc, ct);

    public override async Task CloseOutputAsync(WebSocketCloseStatus status, string? desc, CancellationToken ct)
    {
        if (_state != WebSocketState.Open) return;
        _state = WebSocketState.CloseSent;
        var code = (int)status;
        await SendFrame(OpClose, new ArraySegment<byte>(new[] { (byte)(code >> 8), (byte)code }), ct);
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        try { _s.Dispose(); } catch { }
    }

    public override void Dispose()
    {
        if (_state == WebSocketState.Open) _state = WebSocketState.Closed;
        try { _s.Dispose(); } catch { }
    }
}
