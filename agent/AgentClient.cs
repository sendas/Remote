using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomAgent;

/// <summary>
/// Ligação de saída ao servidor (o PC do aluno liga-se ao servidor, nunca o contrário):
/// funciona através de VLANs e NAT sem abrir portas nos PCs dos alunos.
/// </summary>
internal sealed class AgentClient
{
    public const byte Thumb = 0x01, Full = 0x02, Share = 0x10;

    private readonly AgentConfig _cfg;
    private readonly Func<string> _currentUser;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<JsonNode>? Command;
    public event Action<byte[]>? ShareFrame;
    public event Action<bool>? ConnectionChanged;

    public bool Connected => _ws?.State == WebSocketState.Open;

    // definições recebidas do servidor
    public int ThumbIntervalMs { get; private set; } = 2000;
    public int ThumbWidth { get; private set; } = 360;
    public int FullFps { get; private set; }
    public int FailsafeUnlockMinutes { get; private set; } = 10;

    public AgentClient(AgentConfig cfg, Func<string> currentUser)
    {
        _cfg = cfg;
        _currentUser = currentUser;
    }

    public void Start()
    {
        new Thread(() => ConnectLoop().GetAwaiter().GetResult()) { IsBackground = true, Name = "net" }.Start();
        new Thread(CaptureLoop) { IsBackground = true, Name = "capture" }.Start();
    }

    private async Task ConnectLoop()
    {
        var delay = 2000;
        var idx = 0;
        while (true)
        {
            var url = _cfg.ServerUrls[idx % _cfg.ServerUrls.Count];
            idx++;
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert == null) return false;
                    if (string.IsNullOrWhiteSpace(_cfg.CertificatePin))
                        return errors == System.Net.Security.SslPolicyErrors.None;
                    var pin = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
                    return string.Equals(pin, _cfg.CertificatePin.Replace(":", "").Trim(), StringComparison.OrdinalIgnoreCase);
                };
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await ws.ConnectAsync(new Uri(url), cts.Token);

                _ws = ws;
                await SendJson(new
                {
                    type = "hello",
                    key = _cfg.EnrollmentKey,
                    id = Environment.MachineName,
                    hostname = Environment.MachineName,
                    room = _cfg.Room,
                    user = _currentUser(),
                    os = Environment.OSVersion.VersionString,
                    version = typeof(AgentClient).Assembly.GetName().Version?.ToString(3) ?? "",
                });
                Log.Write("Ligado a " + url);
                delay = 2000;
                idx--; // na próxima falha, volta a tentar primeiro este endereço
                ConnectionChanged?.Invoke(true);
                await ReceiveLoop(ws);
            }
            catch (Exception ex)
            {
                Log.Write($"Ligação a {url} falhou: {ex.Message}");
            }
            finally
            {
                if (_ws != null) ConnectionChanged?.Invoke(false);
                _ws = null;
                FullFps = 0;
            }
            await Task.Delay(delay + Random.Shared.Next(1000)); // jitter: 30 PCs não reconectam todos ao mesmo tempo
            delay = Math.Min(delay * 2, 30000);
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws)
    {
        var buf = new byte[256 * 1024];
        var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > 16 * 1024 * 1024) return;
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Binary)
            {
                if (ms.Length > 1 && ms.GetBuffer()[0] == Share)
                    ShareFrame?.Invoke(ms.ToArray()[1..]);
                continue;
            }

            JsonNode? n;
            try { n = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); }
            catch { continue; }
            if (n == null) continue;

            switch (n["type"]?.GetValue<string>())
            {
                case "welcome":
                    ThumbIntervalMs = Math.Clamp(n["thumbIntervalMs"]?.GetValue<int>() ?? 2000, 500, 60000);
                    ThumbWidth = Math.Clamp(n["thumbWidth"]?.GetValue<int>() ?? 360, 160, 800);
                    FailsafeUnlockMinutes = Math.Clamp(n["failsafeUnlockMinutes"]?.GetValue<int>() ?? 10, 1, 1440);
                    _lastThumbHash = null; // envia logo uma miniatura
                    break;
                case "stream":
                    FullFps = (n["on"]?.GetValue<bool>() ?? false) ? Math.Clamp(n["fps"]?.GetValue<int>() ?? 5, 1, 15) : 0;
                    Command?.Invoke(n);
                    break;
                default:
                    Command?.Invoke(n);
                    break;
            }
        }
    }

    public async Task SendJson(object msg)
    {
        var ws = _ws;
        if (ws == null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { _sendLock.Release(); }
    }

    public void Post(object msg) => _ = SendJson(msg);

    /// <summary>Envia uma imagem; se ainda houver um envio em curso (rede lenta), descarta-a.</summary>
    private void TrySendFrame(byte kind, byte[] jpeg)
    {
        var ws = _ws;
        if (ws == null || !_sendLock.Wait(0)) return;
        var pkt = new byte[jpeg.Length + 1];
        pkt[0] = kind;
        jpeg.CopyTo(pkt, 1);
        try { ws.SendAsync(pkt, WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult(); }
        catch { }
        finally { _sendLock.Release(); }
    }

    private byte[]? _lastThumbHash;

    private void CaptureLoop()
    {
        var lastThumb = 0L;
        var lastForcedThumb = 0L;
        while (true)
        {
            try
            {
                if (!Connected) { Thread.Sleep(500); continue; }

                var now = Environment.TickCount64;
                if (FullFps > 0)
                {
                    var jpeg = ScreenCapture.Capture(1600, 60, out _);
                    if (jpeg != null) TrySendFrame(Full, jpeg);
                    var wait = 1000 / FullFps - (int)(Environment.TickCount64 - now);
                    if (wait > 0) Thread.Sleep(wait);
                }
                else
                {
                    Thread.Sleep(100);
                }

                now = Environment.TickCount64;
                if (now - lastThumb >= ThumbIntervalMs)
                {
                    lastThumb = now;
                    var jpeg = ScreenCapture.Capture(ThumbWidth, 50, out var hash);
                    // ecrã parado: não reenvia (exceto a cada 30 s, para o caso de o professor ter entrado agora)
                    var same = hash != null && _lastThumbHash != null && hash.AsSpan().SequenceEqual(_lastThumbHash);
                    if (jpeg != null && (!same || now - lastForcedThumb > 30000))
                    {
                        TrySendFrame(Thumb, jpeg);
                        _lastThumbHash = hash;
                        lastForcedThumb = now;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Captura: " + ex.Message);
                Thread.Sleep(1000);
            }
        }
    }
}
