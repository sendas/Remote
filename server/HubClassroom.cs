using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace ClassroomServer;

/// <summary>
/// Funções de aula: cortar Internet, enviar ficheiros, recolher trabalhos, pergunta rápida,
/// mostrar o ecrã de um aluno à turma, fechar programas, e nome completo de quem tem sessão.
/// </summary>
public sealed partial class Hub
{
    public const byte FileChunk = 0x20, CollectChunk = 0x21;
    private const int ChunkSize = 256 * 1024;
    public const long MaxFileBytes = 100L * 1024 * 1024;

    public static string FilesDir => Dir("ficheiros");
    public static string CollectDir => Dir("entregas");

    private static string Dir(string name)
    {
        var d = Path.Combine(ServerConfig.DataDir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string((s ?? "").Select(c => bad.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "ficheiro" : clean.Length > 120 ? clean[..120] : clean;
    }

    // ------------------------------------------------------------------ nome completo (AD)

    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"QUEIROZ\maria.silva" -> "Maria Silva" (displayName do AD; em cache).</summary>
    public string FullName(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) return "";
        if (_names.TryGetValue(account, out var cached)) return cached;
        var sam = account.Contains('\\') ? account[(account.LastIndexOf('\\') + 1)..] : account;
        var name = "";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var s = new System.DirectoryServices.DirectorySearcher(
                    $"(&(objectCategory=person)(sAMAccountName={sam.Replace("(", "").Replace(")", "").Replace("*", "")}))",
                    new[] { "displayName", "givenName", "sn" });
                var r = s.FindOne();
                if (r != null)
                {
                    string P(string k) => r.Properties[k].Count > 0 ? r.Properties[k][0]?.ToString() ?? "" : "";
                    name = P("displayname");
                    if (name.Length == 0) name = $"{P("givenname")} {P("sn")}".Trim();
                }
            }
            catch { }
        }
        _names[account] = name;
        return name;
    }

    /// <summary>Vai buscar o nome completo ao AD sem atrasar a ligação, e avisa as consolas.</summary>
    private void ResolveName(AgentConnection a)
    {
        var account = a.User;
        if (string.IsNullOrWhiteSpace(account)) return;
        _ = Task.Run(() =>
        {
            var name = FullName(account);
            if (name.Length == 0 || a.User != account) return;
            a.FullUserName = name;
            BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
        });
    }

    // ------------------------------------------------------------------ comandos novos (vindos da consola)

    /// <summary>Trata as ações de aula. Devolve true se a ação era uma destas.</summary>
    private bool HandleClassroomAction(TeacherConnection t, string action, JsonNode n, List<AgentConnection> targets)
    {
        switch (action)
        {
            case "internetOff":
            case "internetOn":
                var off = action == "internetOff";
                foreach (var a in targets) _state.SetNoInternet(a.Id, off);
                foreach (var a in targets)
                {
                    a.NoInternet = off;
                    a.SendJson(new { type = "internet", on = !off });
                    BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
                }
                return true;

            case "closeAll":
                foreach (var a in targets) a.SendJson(new { type = "closeAll" });
                return true;

            case "sendFile":
                var fileId = SafeName(n["fileId"]?.GetValue<string>() ?? "");
                _ = Task.Run(() => SendFile(fileId, targets));
                return true;

            case "collect":
                var batch = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
                t.SendJson(new { type = "collectBatch", batch });
                foreach (var a in targets) { a.CollectRequested = batch; a.SendJson(new { type = "collect", batch }); }
                return true;

            case "poll":
                var pollId = Guid.NewGuid().ToString("N")[..8];
                var question = Clean(n["question"]?.GetValue<string>(), 300);
                var options = (n["options"] as JsonArray)?.Select(o => Clean(o?.GetValue<string>(), 60)).Where(o => o.Length > 0).Take(8).ToArray()
                              ?? Array.Empty<string>();
                if (options.Length < 2) return true;
                foreach (var a in targets) a.SendJson(new { type = "poll", id = pollId, question, options });
                t.SendJson(new { type = "pollStarted", id = pollId, question, options, targets = targets.Select(a => a.Id).ToArray() });
                return true;

            case "pollClose":
                var pid = Clean(n["poll"]?.GetValue<string>(), 16);
                foreach (var a in targets) a.SendJson(new { type = "pollClose", id = pid });
                return true;

            case "demoStart":
                // targets = quem vê; source = o aluno cujo ecrã é mostrado
                var srcId = n["source"]?.GetValue<string>() ?? "";
                var src = SnapshotAgents().FirstOrDefault(a => a.Id.Equals(srcId, StringComparison.OrdinalIgnoreCase) && t.MayControl(a));
                if (src == null) return true;
                StopDemo(src);
                lock (_gate)
                {
                    foreach (var a in targets.Where(a => a != src)) src.DemoTargets.Add(a.Id);
                    src.DemoBy = t.ConnId;
                    var first = src.Watchers.Count == 0;
                    src.Watchers.Add("demo");
                    if (first) src.SendJson(new { type = "stream", on = true, fps = _cfg.FullFrameFps });
                }
                var label = string.IsNullOrEmpty(src.FullUserName) ? src.Hostname : src.FullUserName;
                foreach (var a in targets.Where(a => a != src))
                {
                    a.BroadcastFrom = "demo:" + src.Id;
                    a.SendJson(new { type = "broadcastStart", from = $"o ecrã de {label}", fullscreen = true });
                    BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
                }
                BroadcastToTeachers(src, new { type = "agentUpdate", agent = src.Describe() });
                return true;

            case "demoStop":
                foreach (var a in SnapshotAgents().Where(a => a.DemoBy != null && t.MayControl(a) &&
                                                             (a.DemoBy == t.ConnId || t.Session.Account.IsAdmin)))
                    StopDemo(a);
                return true;
        }
        return false;
    }

    private void StopDemo(AgentConnection src)
    {
        List<string> ids;
        bool last;
        lock (_gate)
        {
            ids = src.DemoTargets.ToList();
            src.DemoTargets.Clear();
            src.DemoBy = null;
            src.Watchers.Remove("demo");
            last = src.Watchers.Count == 0;
        }
        if (ids.Count == 0) return;
        if (last) src.SendJson(new { type = "stream", on = false, fps = 0 });
        foreach (var a in SnapshotAgents().Where(a => ids.Contains(a.Id, StringComparer.OrdinalIgnoreCase)))
        {
            if (a.BroadcastFrom != "demo:" + src.Id) continue;
            a.BroadcastFrom = null;
            a.SendJson(new { type = "broadcastStop" });
            BroadcastToTeachers(a, new { type = "agentUpdate", agent = a.Describe() });
        }
        BroadcastToTeachers(src, new { type = "agentUpdate", agent = src.Describe() });
    }

    /// <summary>Imagem em grande do aluno que está a ser mostrado à turma -> vai para os PCs da turma.</summary>
    private void ForwardDemo(AgentConnection src, ReadOnlySpan<byte> jpeg)
    {
        HashSet<string> targets;
        lock (_gate)
        {
            if (src.DemoTargets.Count == 0) return;
            targets = new HashSet<string>(src.DemoTargets, StringComparer.OrdinalIgnoreCase);
        }
        var pkt = new byte[jpeg.Length + 1];
        pkt[0] = Share;
        jpeg.CopyTo(pkt.AsSpan(1));
        foreach (var a in SnapshotAgents())
            if (targets.Contains(a.Id) && a.BroadcastFrom == "demo:" + src.Id)
                a.SendFrame("share", pkt);
    }

    private void CleanupTeacherDemos(TeacherConnection t)
    {
        foreach (var a in SnapshotAgents().Where(a => a.DemoBy == t.ConnId)) StopDemo(a);
    }

    // ------------------------------------------------------------------ enviar ficheiro

    private void SendFile(string fileId, List<AgentConnection> targets)
    {
        try
        {
            var dir = Path.Combine(FilesDir, fileId);
            var file = Directory.Exists(dir) ? Directory.GetFiles(dir).FirstOrDefault() : null;
            if (file == null) return;
            var name = Path.GetFileName(file);
            var size = new FileInfo(file).Length;
            var idBytes = Encoding.ASCII.GetBytes(fileId.PadRight(16)[..16]);
            foreach (var a in targets) a.SendJson(new { type = "fileStart", id = fileId[..Math.Min(16, fileId.Length)].PadRight(16), name, size });
            using var fs = File.OpenRead(file);
            var buf = new byte[ChunkSize];
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var pkt = new byte[1 + 16 + read];
                pkt[0] = FileChunk;
                idBytes.CopyTo(pkt, 1);
                Buffer.BlockCopy(buf, 0, pkt, 17, read);
                foreach (var a in targets) a.SendOrdered(pkt);
            }
            foreach (var a in targets) a.SendJson(new { type = "fileEnd", id = fileId[..Math.Min(16, fileId.Length)].PadRight(16) });
            _audit.Write("servidor", "ficheiro-enviado", $"{name} -> {string.Join(",", targets.Select(a => a.Id))}");
        }
        catch (Exception ex)
        {
            _log.LogWarning("Enviar ficheiro: {Msg}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ recolher trabalhos (agente -> servidor)

    private void HandleCollectJson(AgentConnection a, JsonNode n, string type)
    {
        if (type == "collectStart")
        {
            a.Collect?.Dispose();
            a.Collect = null;
            // só aceita entregas que o servidor pediu (um agente não pode encher o disco por iniciativa própria)
            var asked = a.CollectRequested;
            a.CollectRequested = null;
            if (asked == null || asked != (n["batch"]?.GetValue<string>() ?? "")) return;
            var batch = SafeName(asked);
            // entregas/<recolha>/<escola>/<sala>/<PC - aluno>.zip  (permite filtrar por permissões)
            var dir = Path.Combine(CollectDir, batch, SafeName(a.School.Length > 0 ? a.School : "_"), SafeName(a.Room.Length > 0 ? a.Room : "_"));
            Directory.CreateDirectory(dir);
            var who = string.IsNullOrEmpty(a.FullUserName) ? a.User.Split('\\').Last() : a.FullUserName;
            var path = Path.Combine(dir, SafeName($"{a.Hostname} - {who}") + ".zip");
            a.CollectPath = path;
            a.CollectBatch = batch;
            a.Collect = File.Create(path);
            return;
        }
        // collectEnd
        var ok = n["ok"]?.GetValue<bool>() ?? false;
        var err = Clean(n["error"]?.GetValue<string>(), 200);
        long size = 0;
        try { size = a.Collect?.Length ?? 0; a.Collect?.Dispose(); } catch { }
        a.Collect = null;
        if (!ok && a.CollectPath != null) { try { File.Delete(a.CollectPath); } catch { } }
        BroadcastToTeachers(a, new
        {
            type = "collected",
            id = a.Id,
            user = a.FullUserName.Length > 0 ? a.FullUserName : a.User,
            batch = a.CollectBatch,
            file = ok && a.CollectPath != null ? Path.GetRelativePath(CollectDir, a.CollectPath).Replace('\\', '/') : null,
            size,
            error = ok ? null : (err.Length > 0 ? err : "sem ficheiros"),
        });
        if (ok) _audit.Write(a.Id, "trabalho-recolhido", a.CollectPath ?? "");
    }

    private void HandleCollectChunk(AgentConnection a, ArraySegment<byte> data)
    {
        var fs = a.Collect;
        if (fs == null || data.Count <= 17) return;
        if (fs.Length + data.Count - 17 > MaxFileBytes) { fs.Dispose(); a.Collect = null; return; }
        fs.Write(data.Array!, data.Offset + 17, data.Count - 17);
    }

    /// <summary>
    /// Pode este professor ver esta entrega? O caminho relativo é "recolha/escola/sala/ficheiro.zip".
    /// (Recolhas antigas, "recolha/escola sala/ficheiro.zip", só os administradores veem.)
    /// </summary>
    public static bool CanSeeEntrega(TeacherAccount acc, string relPath)
    {
        if (acc.IsAdmin) return true;
        var p = relPath.Replace('\\', '/').Split('/');
        return p.Length == 4 && acc.CanSee(p[1] == "_" ? "" : p[1], p[2] == "_" ? "" : p[2]);
    }

    public static IEnumerable<string> EntregasOf(string batchDir) =>
        Directory.GetFiles(batchDir, "*.zip", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(CollectDir, f).Replace('\\', '/'));

    /// <summary>Entregas de uma recolha que este professor pode ver (para "Descarregar tudo").</summary>
    public static List<string> VisibleEntregas(string batch, TeacherAccount acc)
    {
        var dir = Path.Combine(CollectDir, SafeName(batch));
        if (!Directory.Exists(dir)) return new List<string>();
        return EntregasOf(dir).Where(r => CanSeeEntrega(acc, r)).ToList();
    }

    /// <summary>Apaga ficheiros enviados aos alunos com mais de 7 dias e entregas com mais de 180 dias.</summary>
    public static void CleanupOldFiles()
    {
        void Clean(string root, TimeSpan age)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(root))
                    if (DateTime.Now - Directory.GetLastWriteTime(d) > age) Directory.Delete(d, true);
            }
            catch { }
        }
        Clean(FilesDir, TimeSpan.FromDays(7));
        Clean(CollectDir, TimeSpan.FromDays(180));
    }
}
