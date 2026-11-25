using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomServer;

/// <summary>Sessão de consola. Aponta para a conta viva: mudanças de salas/admin aplicam-se logo.</summary>
public sealed record TeacherSession(string Token, TeacherAccount Account, DateTimeOffset Expires)
{
    public string Username => Account.Username;
}

/// <summary>
/// Protocolo binário (1.º byte = tipo):
///   agente → servidor : 0x01 miniatura JPEG | 0x02 imagem em grande JPEG
///   servidor → professor: [tipo][len id (1 byte)][id UTF-8][JPEG]
///   professor → servidor: 0x10 imagem da partilha de ecrã do professor
///   servidor → agente   : 0x10 imagem da partilha
/// Tudo o resto é JSON com campo "type".
/// </summary>
public sealed partial class Hub
{
    public const byte Thumb = 0x01, Full = 0x02, Share = 0x10;

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly HashSet<string> BroadcastActions = new()
    {
        "lock", "unlock", "message", "openUrl", "logoff", "reboot", "shutdown",
        "broadcastStart", "broadcastStop", "watch", "unwatch", "input", "procs", "kill", "type",
        "chat", "notice", "noticeOff", "minimize", "mute",
        "internetOff", "internetOn", "closeAll", "sendFile", "collect", "poll", "pollClose", "demoStart", "demoStop",
    };

    private readonly ServerConfig _cfg;
    private readonly Audit _audit;
    private readonly ILogger<Hub> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentConnection> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TeacherConnection> _teachers = new();
    /// <summary>Bloqueios, Internet cortada e sessões sobrevivem a reconexões e a reinícios do servidor.</summary>
    private readonly PersistentState _state;
    private readonly ConcurrentDictionary<string, TeacherSession> _sessions = new();
    private readonly Timer _expiry;
    private readonly Timer _cleanup;

    private readonly RoomCatalog _catalog;

    private readonly SeenStore _seen;

    public Hub(ServerConfig cfg, Audit audit, ILogger<Hub> log, RoomCatalog catalog, PersistentState state, SeenStore seen)
    {
        _seen = seen;
        _catalog = catalog;
        _catalog.Changed += OnCatalogChanged;
        _cfg = cfg;
        _audit = audit;
        _log = log;
        _state = state;
        // repõe as sessões abertas antes de um reinício (ex.: atualização automática)
        foreach (var si in _state.Sessions())
        {
            var acc = _cfg.Teachers.FirstOrDefault(t => t.Username.Equals(si.Username, StringComparison.OrdinalIgnoreCase));
            if (acc != null) _sessions[si.Token] = new TeacherSession(si.Token, acc, si.Expires);
        }
        _expiry = new Timer(_ => ExpireTimedLocks(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        Updater.AgentShareChanged += () => { _shareSha = null; foreach (var a in SnapshotAgents()) OfferUpdate(a); };
        _cleanup = new Timer(_ => CleanupOldFiles(), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
    }

    // ---------------------------------------------------------------- atualização dos agentes
    private string? _shareSha;
    private DateTime _shareShaAt;

    /// <summary>SHA-256 do agente que está na partilha de instalação (C:\Deploy\SalaDeAula\leve).</summary>
    public string ShareAgentSha()
    {
        if (_shareSha != null && DateTime.UtcNow - _shareShaAt < TimeSpan.FromMinutes(5)) return _shareSha;
        try
        {
            var f = Path.Combine(_cfg.DeployDir, "leve", "ClassroomAgent.exe");
            using var s = File.OpenRead(f);
            _shareSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
        }
        catch { _shareSha = ""; }
        _shareShaAt = DateTime.UtcNow;
        return _shareSha;
    }

    /// <summary>Se o agente do PC é diferente do da partilha, pede-lhe que se atualize (o PC corre o instalador da partilha).</summary>
    private void OfferUpdate(AgentConnection a)
    {
        var share = ShareAgentSha();
        var outdated = share.Length > 0 && a.ExeSha.Length > 0 && a.ExeSha != share;
        if (outdated != a.Outdated)
        {
            a.Outdated = outdated;
            BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
        }
        if (!outdated) return;
        var path = $"\\\\{Environment.MachineName}\\{_cfg.DeployShare}\\leve\\ClassroomAgent.exe";
        a.SendJson(new { type = "selfUpdate", path });
    }

    public (int Total, int Outdated) AgentVersions()
    {
        var all = SnapshotAgents();
        return (all.Count, all.Count(a => a.Outdated));
    }

    /// <summary>Bloqueios temporizados que acabaram: o agente já desbloqueou sozinho; atualiza a consola.</summary>
    private void ExpireTimedLocks()
    {
        try
        {
            var ids = _state.TakeExpiredLocks();
            foreach (var a in SnapshotAgents().Where(a => ids.Contains(a.Id, StringComparer.OrdinalIgnoreCase)))
            {
                a.Locked = false;
                a.SendJson(new { type = "unlock", text = (string?)null });
                BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- sessões de professor

    public TeacherSession? Login(string username, string password)
    {
        TeacherAccount? acc;
        lock (_cfg) acc = _cfg.Teachers.FirstOrDefault(t =>
            string.Equals(t.Username, username, StringComparison.OrdinalIgnoreCase));
        // compara sempre algo, para não revelar por tempo de resposta se o utilizador existe
        var ok = PasswordHasher.Verify(password, acc?.PasswordHash ?? PasswordHasher.Hash("x")) && acc != null;
        if (!ok) return null;
        var s = new TeacherSession(ServerConfig.RandomToken(32), acc!, DateTimeOffset.UtcNow.AddHours(_cfg.SessionHours));
        _sessions[s.Token] = s;
        _state.AddSession(new PersistentState.SessionInfo(s.Token, s.Username, s.Expires));
        return s;
    }

    public TeacherSession? ValidateToken(string? token)
    {
        if (token == null || !_sessions.TryGetValue(token, out var s)) return null;
        bool exists;
        lock (_cfg) exists = _cfg.Teachers.Contains(s.Account);
        if (s.Expires < DateTimeOffset.UtcNow || !exists)
        {
            _sessions.TryRemove(token, out _);
            _state.RemoveSessions(x => x.Token == token);
            return null;
        }
        return s;
    }

    /// <summary>Quantas consolas de professor estão abertas agora (o auto-update espera que seja 0).</summary>
    public int TeacherCount { get { lock (_gate) return _teachers.Count; } }

    public void Logout(string token)
    {
        _sessions.TryRemove(token, out _);
        _state.RemoveSessions(x => x.Token == token);
    }

    // ---------------------------------------------------------------- utilizadores (gestão na consola)

    public object[] ListUsers()
    {
        lock (_cfg)
            return _cfg.Teachers.OrderBy(t => t.Username, StringComparer.OrdinalIgnoreCase).Select(t => (object)new
            {
                username = t.Username,
                displayName = t.DisplayName,
                isAdmin = t.IsAdmin,
                rooms = t.Rooms,
            }).ToArray();
    }

    /// <summary>Cria ou altera uma conta. Devolve uma mensagem de erro, ou null se correu bem.</summary>
    public string? SaveUser(string by, string username, string? displayName, string? password, bool? isAdmin, List<string>? rooms, bool create)
    {
        username = (username ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, @"^[\w.\-@]{2,64}$"))
            return "Nome de utilizador inválido (2-64 letras, números, . - _ @).";
        if (password != null && password.Length > 0 && password.Length < 8)
            return "A palavra-passe tem de ter pelo menos 8 caracteres.";
        lock (_cfg)
        {
            var acc = _cfg.Teachers.FirstOrDefault(t => t.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (create)
            {
                if (acc != null) return "Esse utilizador já existe.";
                if (string.IsNullOrEmpty(password)) return "Indica uma palavra-passe.";
                acc = new TeacherAccount { Username = username };
                _cfg.Teachers.Add(acc);
            }
            else if (acc == null) return "Utilizador não encontrado.";

            if (isAdmin == false && acc.IsAdmin && _cfg.Teachers.Count(t => t.IsAdmin) <= 1)
                return "Tem de existir pelo menos um administrador.";
            if (displayName != null) acc.DisplayName = displayName.Trim();
            if (!string.IsNullOrEmpty(password)) acc.PasswordHash = PasswordHasher.Hash(password);
            if (isAdmin != null) acc.IsAdmin = isAdmin.Value;
            if (rooms != null) acc.Rooms = rooms.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct().ToList();
            _cfg.Save();
        }
        _audit.Write(by, create ? "utilizador-criado" : "utilizador-alterado", username);
        Reconnect(username); // a consola dessa pessoa volta a carregar a lista de PCs com as salas novas
        return null;
    }

    public string? DeleteUser(string by, string username)
    {
        lock (_cfg)
        {
            var acc = _cfg.Teachers.FirstOrDefault(t => t.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (acc == null) return "Utilizador não encontrado.";
            if (acc.Username.Equals(by, StringComparison.OrdinalIgnoreCase)) return "Não podes apagar a tua própria conta.";
            if (acc.IsAdmin && _cfg.Teachers.Count(t => t.IsAdmin) <= 1) return "Tem de existir pelo menos um administrador.";
            _cfg.Teachers.Remove(acc);
            _cfg.Save();
        }
        foreach (var k in _sessions.Where(kv => kv.Value.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            _sessions.TryRemove(k, out _);
        _state.RemoveSessions(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        _audit.Write(by, "utilizador-apagado", username);
        Reconnect(username);
        return null;
    }

    public string? ChangeOwnPassword(TeacherSession s, string current, string next)
    {
        if (!PasswordHasher.Verify(current, s.Account.PasswordHash)) return "A palavra-passe atual está errada.";
        if (next.Length < 8) return "A palavra-passe nova tem de ter pelo menos 8 caracteres.";
        lock (_cfg)
        {
            s.Account.PasswordHash = PasswordHasher.Hash(next);
            _cfg.Save();
        }
        _audit.Write(s.Username, "palavra-passe-alterada", "");
        return null;
    }

    /// <summary>Nomes de salas mudaram: atualiza a sala de cada PC ligado e as consolas.</summary>
    private void OnCatalogChanged()
    {
        foreach (var a in SnapshotAgents())
        {
            var room = _catalog.Resolve(a.School, a.Folder);
            a.SchoolName = _catalog.SchoolName(a.School);
            if (room == a.Room) continue;
            a.Room = room;
            BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
        }
        List<TeacherConnection> list;
        lock (_gate) list = _teachers.Values.ToList();
        foreach (var t in list) t.SendJson(new { type = "catalogChanged" });
        RefreshThumbs();
    }

    /// <summary>
    /// Estado de uma pasta (sala) do AD: cada conta/PC com o que sabemos dele —
    /// ligado agora, já teve o agente (e quando), ou nunca apareceu.
    /// </summary>
    public object RoomStatus(string school, string folder)
    {
        var online = SnapshotAgents();
        var rows = _catalog.AccountsOf(school, folder).Select(acc =>
        {
            var on = acc.Mac.Length > 0 ? online.FirstOrDefault(a => a.Macs.Contains(acc.Mac)) : null;
            var seen = acc.Mac.Length > 0 ? _seen.Get(acc.Mac) : null;
            return new
            {
                name = acc.Name,
                mac = acc.Mac,
                state = on != null ? "online" : seen != null ? "seen" : "never",
                pc = on?.Hostname ?? seen?.Pc ?? "",
                user = on != null ? (on.FullUserName.Length > 0 ? on.FullUserName : on.User) : seen?.User ?? "",
                lastSeen = on != null ? DateTimeOffset.UtcNow : seen?.LastSeen,
                version = on?.Version ?? seen?.Version ?? "",
            };
        }).ToList();
        return new
        {
            school, folder,
            total = rows.Count,
            online = rows.Count(r => r.state == "online"),
            seen = rows.Count(r => r.state != "never"),
            rows,
        };
    }

    /// <summary>Contagens por pasta (para a tabela de "Escolas e salas").</summary>
    public Dictionary<string, (int Online, int Seen)> FolderCounts()
    {
        var online = SnapshotAgents();
        var res = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var acc in _catalog.AllAccounts())
        {
            var k = RoomCatalog.Key(acc.School, acc.Folder);
            var (on, seen) = res.GetValueOrDefault(k);
            var isOn = acc.Mac.Length > 0 && online.Any(a => a.Macs.Contains(acc.Mac));
            var wasSeen = isOn || (acc.Mac.Length > 0 && _seen.Get(acc.Mac) != null);
            res[k] = (on + (isOn ? 1 : 0), seen + (wasSeen ? 1 : 0));
        }
        return res;
    }

    public object CatalogFor(TeacherSession s) =>
        _catalog.Snapshot((school, room) => s.Account.CanSee(school, room),
            SnapshotAgents().Select(a => (a.School, a.Room)));

    private void Reconnect(string username)
    {
        List<TeacherConnection> list;
        lock (_gate) list = _teachers.Values.Where(t => t.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var t in list) t.Abort();
    }


    // ---------------------------------------------------------------- agentes (PCs dos alunos)

    public async Task RunAgent(WebSocket ws, string ip)
    {
        var agent = new AgentConnection(ws, ip);
        var writer = agent.WriterLoop();
        var registered = false;

        try
        {
        await agent.ReadLoop(4 * 1024 * 1024, async (type, data) =>
        {
          try
          {
            if (!registered)
            {
                if (type != WebSocketMessageType.Text || !TryRegisterAgent(agent, Connection.Utf8(data)))
                {
                    agent.Abort();
                    return;
                }
                registered = true;
                return;
            }

            if (type == WebSocketMessageType.Binary)
            {
                if (data.Count < 2) return;
                var kind = data[0];
                if (kind == CollectChunk) { HandleCollectChunk(agent, data); return; }
                if (kind is not (Thumb or Full)) return;
                if (kind == Full) ForwardDemo(agent, data.AsSpan(1));
                var packet = BuildTeacherFrame(kind, agent.IdBytes, data.AsSpan(1));
                var key = (kind == Thumb ? "t:" : "f:") + agent.Id;
                if (kind == Thumb) agent.LastThumbPacket = packet;
                foreach (var t in TeachersFor(agent, kind == Full))
                    if (kind == Full || (t.ViewsRoom(agent) && t.WantsThumb(agent.Id)))
                        t.SendFrame(key, packet);
                return;
            }

            HandleAgentJson(agent, Connection.Utf8(data));
            await Task.CompletedTask;
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
            // uma mensagem mal formada (ou disco cheio) não pode deitar abaixo a ligação nem saltar a limpeza
            _log.LogDebug("Agente {Id}: {Msg}", agent.Id, ex.Message);
          }
        });
        }
        finally
        {
            if (registered) UnregisterAgent(agent);
            agent.Abort();
        }
        await writer;
    }

    private bool TryRegisterAgent(AgentConnection a, string json)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(json); } catch { return false; }
        if (n?["type"]?.GetValue<string>() != "hello") return false;
        var key = n["key"]?.GetValue<string>() ?? "";
        if (!FixedEquals(key, _cfg.EnrollmentKey))
        {
            _log.LogWarning("Agente rejeitado (chave errada) de {Ip}", a.RemoteIp);
            return false;
        }

        a.Id = Clean(n["id"]?.GetValue<string>(), 64);
        if (a.Id.Length == 0) return false;
        a.Hostname = Clean(n["hostname"]?.GetValue<string>(), 64);
        a.Folder = Clean(n["room"]?.GetValue<string>(), 64);
        a.School = Clean(n["school"]?.GetValue<string>(), 32);
        a.ExeSha = Clean(n["exeSha"]?.GetValue<string>(), 64).ToUpperInvariant();
        a.Macs = (n["macs"] as JsonArray)?.Select(m => SeenStore.NormalizeMac(m?.ToString())).Where(m => m.Length > 0).Distinct().Take(8).ToList()
                 ?? new List<string>();
        a.Room = _catalog.Resolve(a.School, a.Folder);
        a.SchoolName = _catalog.SchoolName(a.School);
        a.User = Clean(n["user"]?.GetValue<string>(), 128);
        a.Os = Clean(n["os"]?.GetValue<string>(), 128);
        a.Version = Clean(n["version"]?.GetValue<string>(), 32);
        a.IdBytes = Encoding.UTF8.GetBytes(a.Id);

        AgentConnection? old;
        lock (_gate)
        {
            _agents.TryGetValue(a.Id, out old);
            _agents[a.Id] = a;
            a.Locked = _state.GetLock(a.Id) != null;
        }
        old?.Abort(); // PC com o mesmo nome ligou outra vez: a ligação antiga morreu

        a.SendJson(new
        {
            type = "welcome",
            thumbIntervalMs = _cfg.ThumbnailIntervalMs,
            thumbWidth = _cfg.ThumbnailWidth,
            fullFps = _cfg.FullFrameFps,
            failsafeUnlockMinutes = _cfg.AgentFailsafeUnlockMinutes,
        });
        // repõe o bloqueio se o aluno tinha sido bloqueado antes de perder a ligação
        // ligação nova = ninguém lhe está a partilhar nada: se ficou um ecrã partilhado/demonstração
        // pendurado (ex.: servidor reiniciou a meio), o PC fecha-o e o teclado volta
        a.SendJson(new { type = "broadcastStop" });
        // professores que estavam a ver este PC em grande continuam a ver (a ligação antiga caiu)
        lock (_gate)
            foreach (var tw in _teachers.Values.Where(tw => tw.Watching.Contains(a.Id)))
                a.Watchers.Add(tw.ConnId);
        if (a.Watchers.Count > 0) a.SendJson(new { type = "stream", on = true, fps = _cfg.FullFrameFps });
        var li = _state.GetLock(a.Id);
        if (li != null) a.SendJson(new { type = "lock", text = li.Text, color = li.Color, seconds = li.SecondsLeft });
        else a.SendJson(new { type = "unlock", text = (string?)null });

        _log.LogInformation("Agente ligado: {Id} ({User}) sala {Room} de {Ip}", a.Id, a.User, a.Room, a.RemoteIp);
        a.NoInternet = _state.NoInternet(a.Id);
        // envia sempre o estado, para o PC e a consola nunca ficarem em desacordo
        if (!a.NoInternet) a.SendJson(new { type = "internet", on = true });
        if (a.NoInternet) a.SendJson(new { type = "internet", on = false });
        BroadcastToTeachers(a, new { type = "agentJoined", agent = a.Describe() });
        _seen.Touch(a.Macs, new SeenStore.Seen(a.Hostname, a.User, a.School, a.Folder, DateTimeOffset.UtcNow, a.Version));
        OfferUpdate(a);
        ResolveName(a);
        RefreshThumbs(a);
        return true;
    }

    private void UnregisterAgent(AgentConnection a)
    {
        bool removed;
        lock (_gate)
        {
            removed = _agents.TryGetValue(a.Id, out var cur) && ReferenceEquals(cur, a);
            if (removed) _agents.Remove(a.Id);
        }
        StopDemo(a);
        try { a.Collect?.Dispose(); } catch { }
        a.Collect = null;
        if (!removed) return;
        _log.LogInformation("Agente desligado: {Id}", a.Id);
        BroadcastToTeachers(a, new { type = "agentLeft", id = a.Id });
    }

    private void HandleAgentJson(AgentConnection a, string json)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(json); } catch { return; }
        switch (n?["type"]?.GetValue<string>())
        {
            case "status":
                a.User = Clean(n["user"]?.GetValue<string>(), 128);
                a.FullUserName = "";
                BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
                ResolveName(a);
                break;
            case "collectStart":
            case "collectEnd":
                HandleCollectJson(a, n, n["type"]!.GetValue<string>());
                break;
            case "answer":
                BroadcastToTeachers(a, new
                {
                    type = "answer", id = a.Id,
                    user = a.FullUserName.Length > 0 ? a.FullUserName : a.User,
                    poll = Clean(n["poll"]?.GetValue<string>(), 16),
                    choice = Clean(n["choice"]?.GetValue<string>(), 60),
                });
                break;
            case "reply":
                var text = Clean(n["text"]?.GetValue<string>(), 2000, allowNewlines: true);
                if (text.Length == 0) return;
                _audit.Write(a.Id, "resposta-aluno", text);
                BroadcastToTeachers(a, new
                {
                    type = "reply",
                    id = a.Id,
                    user = a.User,
                    text,
                    at = DateTimeOffset.UtcNow,
                });
                break;
            case "pong":
                break;
            case "unlocked":
                _state.SetLock(a.Id, null);
                a.Locked = false;
                BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
                break;
            case "chat":
                var ctext = Clean(n["text"]?.GetValue<string>(), 2000, allowNewlines: true);
                if (ctext.Length == 0) return;
                _audit.Write(a.Id, "chat-aluno", ctext);
                BroadcastToTeachers(a, new { type = "chat", id = a.Id, user = a.User, text = ctext, at = DateTimeOffset.UtcNow });
                break;
            case "hand":
                _audit.Write(a.Id, "mao-no-ar", "");
                BroadcastToTeachers(a, new { type = "hand", id = a.Id, user = a.User, at = DateTimeOffset.UtcNow });
                break;
            case "procs":
                // lista de programas abertos pedida por uma consola
                BroadcastToTeachers(a, new { type = "procs", id = a.Id, list = n["list"]?.DeepClone() });
                break;
        }
    }

    // ---------------------------------------------------------------- professores

    public async Task RunTeacher(WebSocket ws, string ip, TeacherSession session)
    {
        var t = new TeacherConnection(ws, ip, session);
        var writer = t.WriterLoop();
        lock (_gate) _teachers[t.ConnId] = t;
        RefreshThumbs();
        _audit.Write(t.Username, "consola-ligada", ip);

        t.SendJson(new
        {
            type = "hello",
            username = t.Username,
            agents = SnapshotAgents().Where(t.MayControl).Select(a => a.Describe()).ToArray(),
        });
        foreach (var a in SnapshotAgents().Where(t.MayControl))
            if (a.LastThumbPacket is { } p) t.SendFrame("t:" + a.Id, p);

        try
        {
            await t.ReadLoop(8 * 1024 * 1024, (type, data) =>
            {
                try
                {
                    if (type == WebSocketMessageType.Binary)
                    {
                        if (data.Count > 1 && data[0] == Share) ForwardShare(t, data);
                        return Task.CompletedTask;
                    }
                    HandleTeacherJson(t, Connection.Utf8(data));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug("Consola {User}: {Msg}", t.Username, ex.Message);
                }
                return Task.CompletedTask;
            });
        }
        finally
        {
            // limpeza: parar partilha e streams que este professor tinha abertos
            lock (_gate) _teachers.Remove(t.ConnId);
            foreach (var a in SnapshotAgents())
            {
                if (a.BroadcastFrom == t.ConnId)
                {
                    a.BroadcastFrom = null;
                    a.SendJson(new { type = "broadcastStop" });
                    BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
                }
                StopWatching(t, a);
            }
            CleanupTeacherDemos(t);
            _audit.Write(t.Username, "consola-desligada", ip);
            RefreshThumbs();
            t.Abort();
        }
        await writer;
    }

    private void HandleTeacherJson(TeacherConnection t, string json)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(json); } catch { return; }
        if (n == null) return;
        var mtype = n["type"]?.GetValue<string>();
        if (mtype == "prefs")
        {
            // -1 = sem miniaturas, 0 = todas, >0 = no máximo uma por PC a cada N ms
            t.ThumbMinIntervalMs = Math.Clamp(n["thumbMinIntervalMs"]?.GetValue<int>() ?? 0, -1, 600_000);
            var room = n["room"]?.GetValue<string>();
            t.RoomFilter = string.IsNullOrWhiteSpace(room) ? null : room;
            // envia já as últimas miniaturas da sala escolhida, para a grelha não ficar vazia
            foreach (var a in SnapshotAgents().Where(t.ViewsRoom))
                if (a.LastThumbPacket is { } p) t.SendFrame("t:" + a.Id, p);
            RefreshThumbs();
            return;
        }
        if (mtype != "cmd") return;

        var action = n["action"]?.GetValue<string>() ?? "";
        if (!BroadcastActions.Contains(action)) return;

        var ids = n["targets"]?.AsArray().Select(x => x?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                  ?? new HashSet<string>();
        var targets = SnapshotAgents().Where(a => ids.Contains(a.Id) && t.MayControl(a)).ToList();
        if (targets.Count == 0) return;

        if (action != "input" && action != "demoStart" && HandleClassroomAction(t, action, n, targets))
        {
            _audit.Write(t.Username, action, string.Join(",", targets.Select(a => a.Id)) + Summary(n));
            return;
        }
        if (action == "demoStart") { _audit.Write(t.Username, action, n["source"]?.ToString() ?? ""); HandleClassroomAction(t, action, n, targets); return; }

        if (action != "input")
            _audit.Write(t.Username, action, string.Join(",", targets.Select(a => a.Id)) + Summary(n));

        switch (action)
        {
            case "lock":
            case "unlock":
                var lockIt = action == "lock";
                var lockSecs = Math.Clamp(n["seconds"]?.GetValue<int>() ?? 0, 0, 14400);
                var lockInfo = lockIt
                    ? new PersistentState.LockInfo(Clean(n["text"]?.GetValue<string>(), 300), Clean(n["color"]?.GetValue<string>(), 7),
                        lockSecs > 0 ? DateTimeOffset.UtcNow.AddSeconds(lockSecs) : null)
                    : null;
                _state.SetLocks(targets.Select(a => a.Id), lockInfo);
                foreach (var a in targets)
                {
                    a.Locked = lockIt;
                    a.SendJson(new { type = action, text = Clean(n["text"]?.GetValue<string>(), 300), color = Clean(n["color"]?.GetValue<string>(), 7), seconds = Math.Clamp(n["seconds"]?.GetValue<int>() ?? 0, 0, 14400) });
                    BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
                }
                break;

            case "message":
                var msg = new
                {
                    type = "message",
                    id = Guid.NewGuid().ToString("N")[..8],
                    from = t.Username,
                    title = Clean(n["title"]?.GetValue<string>(), 120),
                    text = Clean(n["text"]?.GetValue<string>(), 4000, allowNewlines: true),
                    allowReply = n["allowReply"]?.GetValue<bool>() ?? true,
                };
                foreach (var a in targets) a.SendJson(msg);
                break;

            case "openUrl":
                var url = n["url"]?.GetValue<string>() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                    return; // só links web — nunca executáveis ou caminhos locais
                foreach (var a in targets) a.SendJson(new { type = "openUrl", url = u.AbsoluteUri });
                break;

            case "logoff":
            case "reboot":
            case "shutdown":
                foreach (var a in targets) a.SendJson(new { type = action });
                break;

            case "broadcastStart":
                foreach (var a in targets)
                {
                    a.BroadcastFrom = t.ConnId;
                    a.SendJson(new { type = "broadcastStart", from = t.Username, fullscreen = n["fullscreen"]?.GetValue<bool>() ?? true });
                    BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
                }
                break;

            case "broadcastStop":
                foreach (var a in targets)
                {
                    a.BroadcastFrom = null;
                    a.SendJson(new { type = "broadcastStop" });
                    BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
                }
                break;

            case "watch":
                foreach (var a in targets) StartWatching(t, a);
                break;

            case "unwatch":
                foreach (var a in targets) StopWatching(t, a);
                break;

            case "procs":
                targets[0].SendJson(new { type = "procs" });
                break;

            case "kill":
                var pid = n["pid"]?.GetValue<int>() ?? 0;
                if (pid > 0) targets[0].SendJson(new { type = "kill", pid });
                break;

            case "type":
                var txt = Clean(n["text"]?.GetValue<string>(), 2000, allowNewlines: true);
                if (txt.Length > 0) foreach (var a in targets) a.SendJson(new { type = "type", text = txt });
                break;

            case "chat":
                var chatText = Clean(n["text"]?.GetValue<string>(), 2000, allowNewlines: true);
                if (chatText.Length == 0) return;
                foreach (var a in targets) a.SendJson(new { type = "chat", from = t.Username, text = chatText });
                break;

            case "notice":
                var notice = new
                {
                    type = "notice",
                    id = Guid.NewGuid().ToString("N").Substring(0, 6),
                    text = Clean(n["text"]?.GetValue<string>(), 300),
                    color = Clean(n["color"]?.GetValue<string>(), 7),
                    seconds = Math.Clamp(n["seconds"]?.GetValue<int>() ?? 0, 0, 7200),
                    closable = n["closable"]?.GetValue<bool>() ?? true,
                    style = Clean(n["style"]?.GetValue<string>(), 12),
                    corner = Clean(n["corner"]?.GetValue<string>(), 2),
                    endAction = Clean(n["endAction"]?.GetValue<string>(), 12),
                    endSeconds = Math.Clamp(n["endSeconds"]?.GetValue<int>() ?? 0, 0, 14400),
                };
                foreach (var a in targets) a.SendJson(notice);
                break;

            case "noticeOff":
                foreach (var a in targets) a.SendJson(new { type = "noticeOff" });
                break;

            case "minimize":
                foreach (var a in targets) a.SendJson(new { type = "minimize" });
                break;

            case "mute":
                var on = n["on"]?.GetValue<bool>() ?? true;
                foreach (var a in targets) a.SendJson(new { type = "mute", on });
                break;

            case "input":
                // controlo remoto: só para um PC que este professor esteja de facto a ver
                var a0 = targets[0];
                if (!t.Watching.Contains(a0.Id)) return;
                n.AsObject().Remove("targets");
                n["type"] = "input";
                a0.SendJson(n);
                break;
        }
    }

    /// <summary>
    /// Liga/desliga o envio de miniaturas em cada PC conforme haja (ou não) uma consola a ver a sala dele.
    /// Com centenas de PCs, evita que salas que ninguém está a ver gastem rede e CPU.
    /// </summary>
    private void RefreshThumbs(AgentConnection? only = null)
    {
        List<TeacherConnection> teachers;
        lock (_gate) teachers = _teachers.Values.ToList();
        foreach (var a in only != null ? new List<AgentConnection> { only } : SnapshotAgents())
        {
            var on = teachers.Any(t => t.ViewsRoom(a));
            if (a.ThumbsOn == on) continue;
            a.ThumbsOn = on;
            a.SendJson(new { type = "thumbs", on });
        }
    }

    private void StartWatching(TeacherConnection t, AgentConnection a)
    {
        bool first;
        lock (_gate)
        {
            t.Watching.Add(a.Id);
            first = a.Watchers.Count == 0;
            a.Watchers.Add(t.ConnId);
        }
        if (first) a.SendJson(new { type = "stream", on = true, fps = _cfg.FullFrameFps });
    }

    private void StopWatching(TeacherConnection t, AgentConnection a)
    {
        bool last;
        lock (_gate)
        {
            t.Watching.Remove(a.Id);
            if (!a.Watchers.Remove(t.ConnId)) return;
            last = a.Watchers.Count == 0;
        }
        if (last) a.SendJson(new { type = "stream", on = false, fps = 0 });
    }

    private void ForwardShare(TeacherConnection t, ArraySegment<byte> data)
    {
        // uma só cópia do frame é partilhada por todos os PCs; o envio é feito aqui no servidor,
        // por isso o professor (mesmo em hotspot) só faz upload de um fluxo.
        var payload = data.ToArray();
        foreach (var a in SnapshotAgents())
            if (a.BroadcastFrom == t.ConnId)
                a.SendFrame("share", payload);
    }

    // ---------------------------------------------------------------- utilitários

    private List<AgentConnection> SnapshotAgents()
    {
        lock (_gate) return _agents.Values.ToList();
    }

    private IEnumerable<TeacherConnection> TeachersFor(AgentConnection a, bool watchersOnly)
    {
        lock (_gate)
            return _teachers.Values
                .Where(t => t.MayControl(a) && (!watchersOnly || a.Watchers.Contains(t.ConnId)))
                .ToList();
    }

    private void BroadcastToTeachers(AgentConnection a, object msg)
    {
        foreach (var t in TeachersFor(a, false)) t.SendJson(msg);
    }

    private static byte[] BuildTeacherFrame(byte kind, byte[] id, ReadOnlySpan<byte> jpeg)
    {
        var p = new byte[2 + id.Length + jpeg.Length];
        p[0] = kind;
        p[1] = (byte)id.Length;
        id.CopyTo(p, 2);
        jpeg.CopyTo(p.AsSpan(2 + id.Length));
        return p;
    }

    private static string Summary(JsonNode n)
    {
        var parts = new List<string>();
        foreach (var k in new[] { "title", "text", "url" })
            if (n[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                parts.Add($"{k}={(s.Length > 200 ? s[..200] + "…" : s)}");
        return parts.Count == 0 ? "" : " | " + string.Join(" | ", parts);
    }

    private static string Clean(string? s, int max, bool allowNewlines = false)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            if (sb.Length >= max) break;
            if (ch == '\n' && allowNewlines) sb.Append(ch);
            else if (!char.IsControl(ch)) sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static bool FixedEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Registo de auditoria (quem fez o quê e quando) — útil para RGPD e para o Conselho Pedagógico.</summary>
public sealed class Audit
{
    private readonly object _gate = new();
    private readonly string _dir;

    public Audit()
    {
        _dir = Path.Combine(ServerConfig.DataDir, "auditoria");
        Directory.CreateDirectory(_dir);
    }

    public void Write(string who, string action, string details)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\t{who}\t{action}\t{details.Replace('\n', ' ')}";
        lock (_gate)
            File.AppendAllText(Path.Combine(_dir, $"{DateTime.Now:yyyy-MM}.log"), line + Environment.NewLine);
    }
}
