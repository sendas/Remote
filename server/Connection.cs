using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClassroomServer;

/// <summary>
/// Ligação WebSocket com duas filas de saída:
///  - controlo: mensagens JSON, nunca descartadas, enviadas por ordem;
///  - imagens: "a mais recente ganha" por fluxo (ex.: miniatura de cada PC), para que um
///    cliente lento (ex.: professor no hotspot) nunca acumule atraso nem bloqueie os outros.
/// </summary>
public abstract class Connection
{
    public string ConnId { get; } = Guid.NewGuid().ToString("N");
    public WebSocket Socket { get; }
    public string RemoteIp { get; }

    private readonly ConcurrentQueue<(byte[] Data, bool Binary)> _control = new();
    private readonly ConcurrentDictionary<string, byte[]> _frames = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    protected Connection(WebSocket socket, string remoteIp)
    {
        Socket = socket;
        RemoteIp = remoteIp;
    }

    public CancellationToken Closing => _cts.Token;

    public void SendJson(object msg)
    {
        _control.Enqueue((JsonSerializer.SerializeToUtf8Bytes(msg, Hub.Json), false));
        _signal.Release();
    }

    /// <summary>Dados binários que têm de chegar todos e por ordem (ex.: pedaços de um ficheiro).</summary>
    public void SendOrdered(byte[] data)
    {
        _control.Enqueue((data, true));
        _signal.Release();
    }

    /// <summary>Envia uma imagem binária; se já houver uma pendente com a mesma chave, é substituída.</summary>
    public void SendFrame(string streamKey, byte[] payload)
    {
        _frames[streamKey] = payload;
        _signal.Release();
    }

    public async Task WriterLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested && Socket.State == WebSocketState.Open)
            {
                await _signal.WaitAsync(_cts.Token);
                while (_control.TryDequeue(out var m))
                    await Socket.SendAsync(m.Data, m.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, _cts.Token);
                foreach (var key in _frames.Keys)
                {
                    if (_frames.TryRemove(key, out var frame))
                        await Socket.SendAsync(frame, WebSocketMessageType.Binary, true, _cts.Token);
                    // dá prioridade ao controlo entre imagens
                    while (_control.TryDequeue(out var m))
                        await Socket.SendAsync(m.Data, m.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, _cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _cts.Cancel();
        }
    }

    public void Abort()
    {
        _cts.Cancel();
        try { Socket.Abort(); } catch { }
    }

    /// <summary>Lê mensagens completas (até maxBytes) e entrega-as ao handler.</summary>
    public async Task ReadLoop(int maxBytes, Func<WebSocketMessageType, ArraySegment<byte>, Task> onMessage)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && Socket.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await Socket.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > maxBytes) return; // mensagem demasiado grande -> corta a ligação
                } while (!r.EndOfMessage);

                await onMessage(r.MessageType, new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _cts.Cancel();
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { }
        }
    }

    public static string Utf8(ArraySegment<byte> seg) => Encoding.UTF8.GetString(seg);
}

public sealed class AgentConnection : Connection
{
    public AgentConnection(WebSocket s, string ip) : base(s, ip) { }

    public string Id { get; set; } = "";
    public string Hostname { get; set; } = "";
    /// <summary>Nome da sala na consola (resolvido a partir de escola + pasta do AD).</summary>
    public string Room { get; set; } = "";
    /// <summary>Escola (pasta do AD, ex.: EBVG) e pasta da sala no AD, tal como o agente as envia.</summary>
    public string School { get; set; } = "";
    public string SchoolName { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Key => School + "/" + Room;
    public string User { get; set; } = "";
    /// <summary>Nome completo de quem tem sessão (displayName do AD).</summary>
    public string FullUserName { get; set; } = "";
    public string Os { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Outdated { get; set; }
    /// <summary>Endereços MAC do PC (para o relacionar com as contas da pasta da sala no AD).</summary>
    public List<string> Macs { get; set; } = new();
    /// <summary>SHA-256 do executável do agente (para saber se está igual ao da partilha).</summary>
    public string ExeSha { get; set; } = "";
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public bool Locked { get; set; }
    /// <summary>ConnId do professor cuja partilha de ecrã este PC está a receber.</summary>
    public string? BroadcastFrom { get; set; }
    public HashSet<string> Watchers { get; } = new();

    /// <summary>Internet cortada neste PC (regra de firewall no agente).</summary>
    public bool NoInternet { get; set; }
    /// <summary>"Mostrar à turma": PCs que estão a ver o ecrã deste aluno, e a consola que o pediu.</summary>
    public HashSet<string> DemoTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DemoBy { get; set; }
    /// <summary>Recolha de trabalhos em curso (ficheiro zip a ser recebido).</summary>
    public FileStream? Collect { get; set; }
    public string? CollectPath { get; set; }
    public string? CollectBatch { get; set; }
    public string? CollectRequested { get; set; }

    /// <summary>Última miniatura recebida (enviada logo a professores que abram a consola depois).</summary>
    public byte[]? LastThumbPacket { get; set; }

    /// <summary>Se o agente deve enviar miniaturas (só quando algum professor está a ver a sala dele).</summary>
    public bool? ThumbsOn { get; set; }
    /// <summary>Largura das miniaturas pedida a este PC (0 = a do "welcome").</summary>
    public int ThumbWidthSent { get; set; }

    /// <summary>Cabeçalho binário pré-calculado para reencaminhar imagens deste agente aos professores.</summary>
    public byte[] IdBytes { get; set; } = Array.Empty<byte>();

    public object Describe() => new
    {
        id = Id,
        hostname = Hostname,
        room = Room,
        school = School,
        schoolName = SchoolName,
        key = Key,
        user = User,
        userName = FullUserName,
        os = Os,
        version = Version,
        outdated = Outdated,
        ip = RemoteIp,
        locked = Locked,
        broadcasting = BroadcastFrom != null,
        noInternet = NoInternet,
        demo = DemoTargets.Count > 0,
        connectedAt = ConnectedAt,
    };
}

public sealed class TeacherConnection : Connection
{
    public TeacherConnection(WebSocket s, string ip, TeacherSession session) : base(s, ip) => Session = session;

    public TeacherSession Session { get; }
    public string Username => Session.Username;
    public HashSet<string> Watching { get; } = new();

    /// <summary>Intervalo mínimo entre miniaturas do mesmo PC (modo poupança/hotspot). 0 = todas.</summary>
    public int ThumbMinIntervalMs { get; set; }
    /// <summary>Tamanho das miniaturas escolhido na consola: 1, 2 ou 3 vezes.</summary>
    public int ThumbScale { get; set; } = 1;

    /// <summary>Sala que a consola está a mostrar (null = todas). Só recebe miniaturas dessa sala.</summary>
    public string? RoomFilter { get; set; }

    public bool ViewsRoom(AgentConnection a) =>
        ThumbMinIntervalMs >= 0 && MayControl(a) &&
        (RoomFilter == null ||
         string.Equals(RoomFilter, a.Key, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(RoomFilter, a.School + "/*", StringComparison.OrdinalIgnoreCase));
    private readonly ConcurrentDictionary<string, long> _lastThumb = new();

    public bool WantsThumb(string agentId)
    {
        if (ThumbMinIntervalMs < 0) return false;
        if (ThumbMinIntervalMs == 0) return true;
        var now = Environment.TickCount64;
        var last = _lastThumb.GetValueOrDefault(agentId);
        if (now - last < ThumbMinIntervalMs) return false;
        _lastThumb[agentId] = now;
        return true;
    }

    public bool MayControl(AgentConnection a) => Session.Account.CanSee(a.School, a.Room);
}
