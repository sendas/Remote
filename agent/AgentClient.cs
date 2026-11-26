using System.IO.Pipes;
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
    private WebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<JsonNode>? Command;
    public event Action<byte[]>? ShareFrame;
    /// <summary>Pedaço de ficheiro enviado pelo professor: [16 bytes id][dados].</summary>
    public event Action<byte[]>? FileChunk;
    public event Action<bool>? ConnectionChanged;

    public bool Connected => _ws?.State == WebSocketState.Open;

    // definições recebidas do servidor
    public int ThumbIntervalMs { get; private set; } = 2000;
    public int ThumbWidth { get; private set; } = 360;
    public int FullFps { get; private set; }
    /// <summary>O servidor desliga as miniaturas quando nenhuma consola está a ver a sala deste PC.</summary>
    public bool ThumbsOn { get; private set; } = true;
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
            NamedPipeClientStream? pipe = null;
            try
            {
                WebSocket ws;
                if (url.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase))
                {
                    // \\SERVIDOR\pipe\SalaDeAula por SMB (porta 445): passa onde a firewall só deixa o tráfego do domínio.
                    var u = new Uri(url);
                    pipe = new NamedPipeClientStream(u.Host, u.AbsolutePath.Trim('/'), PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(10000);
#if NET
                    ws = WebSocket.CreateFromStream(new ChunkedStream(pipe), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(20));
#else
                    ws = new MiniWebSocket(new ChunkedStream(pipe));
#endif
                }
                else
                {
#if !NET
                    // Windows 7/8.1 (.NET Framework): só o canal pipe:// é suportado
                    throw new NotSupportedException("Esta versão do agente só usa pipe://");
#else
                    var cws = new ClientWebSocket();
                    cws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    cws.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    {
                        if (cert == null) return false;
                        if (string.IsNullOrWhiteSpace(_cfg.CertificatePin))
                            return errors == System.Net.Security.SslPolicyErrors.None;
                        var want = _cfg.CertificatePin.Replace(":", "").Trim();
                        // aceita o SHA-256 do certificado (antigo) ou da chave pública (não muda quando o certificado é renovado)
                        var pin = Compat.Hex(Compat.Sha256(cert.GetRawCertData()));
                        if (string.Equals(pin, want, StringComparison.OrdinalIgnoreCase)) return true;
                        using var c2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
                        var spki = Compat.Hex(Compat.Sha256(c2.PublicKey.ExportSubjectPublicKeyInfo()));
                        return string.Equals(spki, want, StringComparison.OrdinalIgnoreCase);
                    };
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        await cws.ConnectAsync(new Uri(url), cts.Token);
                    ws = cws;
#endif
                }
                using var wsDispose = ws;

                _ws = ws;
                await SendJson(new
                {
                    type = "hello",
                    key = _cfg.EnrollmentKey,
                    id = Environment.MachineName,
                    hostname = Environment.MachineName,
                    room = _cfg.Room,
                    school = _cfg.School,
                    macs = Installer.LocalMacs(), // o servidor usa-os para saber que PC é qual dentro da sala
                    exeSha = OwnSha,
                    nameMode = _cfg.NameMode,
                    hideForTeachers = _cfg.HideForTeachers,
                    label = _cfg.NameMode == "custom" ? _cfg.CustomName : "",
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
                try { pipe?.Dispose(); } catch { }
            }
            await Task.Delay(delay + Compat.RandomNext(1000)); // jitter: 30 PCs não reconectam todos ao mesmo tempo
            delay = Math.Min(delay * 2, 30000);
        }
    }

    private async Task ReceiveLoop(WebSocket ws)
    {
        var buf = new byte[256 * 1024];
        var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > 16 * 1024 * 1024) return;
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Binary)
            {
                if (ms.Length > 1 && ms.GetBuffer()[0] == Share)
                    { var all = ms.ToArray(); var f = new byte[all.Length - 1]; Buffer.BlockCopy(all, 1, f, 0, f.Length); ShareFrame?.Invoke(f); }
                else if (ms.Length > 17 && ms.GetBuffer()[0] == 0x20)
                    { var all = ms.ToArray(); var f = new byte[all.Length - 1]; Buffer.BlockCopy(all, 1, f, 0, f.Length); FileChunk?.Invoke(f); }
                continue;
            }

            JsonNode? n;
            try { n = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); }
            catch { continue; }
            if (n == null) continue;

            switch (n["type"]?.GetValue<string>())
            {
                case "welcome":
                    ThumbIntervalMs = CompatMath.Clamp(n["thumbIntervalMs"]?.GetValue<int>() ?? 2000, 500, 60000);
                    ThumbWidth = CompatMath.Clamp(n["thumbWidth"]?.GetValue<int>() ?? 360, 160, 800);
                    FailsafeUnlockMinutes = CompatMath.Clamp(n["failsafeUnlockMinutes"]?.GetValue<int>() ?? 10, 1, 1440);
                    _lastThumbHash = null; // envia logo uma miniatura
                    break;
                case "thumbSize":
                    ThumbWidth = CompatMath.Clamp(n["width"]?.GetValue<int>() ?? 360, 160, 1280);
                    _lastThumbHash = null; // envia logo uma miniatura no tamanho novo
                    break;
                case "thumbs":
                    ThumbsOn = n["on"]?.GetValue<bool>() ?? true;
                    if (ThumbsOn) _lastThumbHash = null; // envia logo uma miniatura atualizada
                    break;
                case "stream":
                    _lastFullHash = null; // envia logo uma imagem
                    FullFps = (n["on"]?.GetValue<bool>() ?? false) ? CompatMath.Clamp(n["fps"]?.GetValue<int>() ?? 5, 1, 15) : 0;
                    Command?.Invoke(n);
                    break;
                // ficheiros: início, pedaços e fim são tratados aqui, pela ordem em que chegam
                // (se o início fosse para a thread da interface, os primeiros pedaços perdiam-se)
                case "fileStart":
                    Classroom.FileStart(n["id"]?.GetValue<string>() ?? "", n["name"]?.GetValue<string>() ?? "ficheiro");
                    break;
                case "fileEnd":
                    var done = Classroom.FileEnd(n["id"]?.GetValue<string>() ?? "");
                    if (done != null) Command?.Invoke(new System.Text.Json.Nodes.JsonObject { ["type"] = "fileReceived", ["path"] = done });
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
        try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { _sendLock.Release(); }
    }

    public void Post(object msg) => _ = SendJson(msg);

    private static string? _ownSha;
    /// <summary>SHA-256 do próprio executável (o servidor compara com o da partilha para saber se há versão nova).</summary>
    public static string OwnSha
    {
        get
        {
            if (_ownSha != null) return _ownSha;
            try { _ownSha = Compat.Hex(Compat.Sha256(File.ReadAllBytes(Compat.ExePath))); } catch { _ownSha = ""; }
            return _ownSha;
        }
    }

    /// <summary>Envia dados binários e espera (para ficheiros: não se pode perder nenhum pedaço).</summary>
    public async Task SendBinary(byte[] pkt)
    {
        var ws = _ws;
        if (ws == null) throw new IOException("Sem ligação ao servidor.");
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(new ArraySegment<byte>(pkt), WebSocketMessageType.Binary, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Envia uma imagem; se ainda houver um envio em curso (rede lenta), descarta-a.</summary>
    private void TrySendFrame(byte kind, byte[] jpeg)
    {
        var ws = _ws;
        if (ws == null || !_sendLock.Wait(0)) return;
        var pkt = new byte[jpeg.Length + 1];
        pkt[0] = kind;
        jpeg.CopyTo(pkt, 1);
        try { ws.SendAsync(new ArraySegment<byte>(pkt), WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult(); }
        catch { }
        finally { _sendLock.Release(); }
    }

    private byte[]? _lastThumbHash;
    private byte[]? _lastFullHash;
    private long _lastFullSent;

    private void CaptureLoop()
    {
        var lastThumb = 0L;
        var lastForcedThumb = 0L;
        while (true)
        {
            try
            {
                if (!Connected) { Thread.Sleep(500); continue; }

                var now = CompatMath.Ticks;
                if (FullFps > 0)
                {
                    // ecrã em grande: só envia quando a imagem muda (ou a cada 3 s), para não entupir a rede
                    var jpeg = ScreenCapture.Capture(1366, 55, out var fhash);
                    var fsame = fhash != null && _lastFullHash != null && fhash.SequenceEqual(_lastFullHash);
                    if (jpeg != null && (!fsame || now - _lastFullSent > 3000))
                    {
                        TrySendFrame(Full, jpeg);
                        _lastFullHash = fhash;
                        _lastFullSent = now;
                    }
                    var wait = 1000 / FullFps - (int)(CompatMath.Ticks - now);
                    if (wait > 0) Thread.Sleep(wait);
                }
                else
                {
                    Thread.Sleep(100);
                }

                now = CompatMath.Ticks;
                if (ThumbsOn && now - lastThumb >= ThumbIntervalMs)
                {
                    lastThumb = now;
                    var jpeg = ScreenCapture.Capture(ThumbWidth, 50, out var hash);
                    // ecrã parado: não reenvia (exceto a cada 30 s, para o caso de o professor ter entrado agora)
                    var same = hash != null && _lastThumbHash != null && hash.SequenceEqual(_lastThumbHash);
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
