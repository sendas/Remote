using System.Diagnostics;
using System.Text;

namespace ClassroomServer;

/// <summary>Painel "Sistema e instalação" da consola: versão, atualizações e instalação nos PCs.</summary>
public static class SystemApi
{
    public const string InstallTask = "SalaVivaInstalar";

    public static void Map(WebApplication app)
    {
        TeacherSession? Admin(HttpContext ctx, Hub hub)
        {
            var s = hub.ValidateToken(ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", ""));
            return s?.Account.IsAdmin == true ? s : null;
        }

        app.MapGet("/api/admin/system", (HttpContext ctx, Hub hub, ServerConfig cfg) =>
        {
            if (Admin(ctx, hub) == null) return Results.StatusCode(403);
            var (exists, running, lastRun) = TaskState();
            return Results.Json(new
            {
                version = AppVersion.Version,
                commit = AppVersion.Commit,
                buildDate = AppVersion.BuildDate,
                startedAt = AppVersion.StartedAt,
                autoUpdate = cfg.AutoUpdate,
                window = cfg.UpdateWindow,
                lastCheck = Updater.LastCheck,
                lastResult = Updater.LastResult,
                lastOk = Updater.LastOk,
                installTask = new { exists, running, lastRun },
                agents = new { total = hub.AgentVersions().Total, outdated = hub.AgentVersions().Outdated },
                report = LatestReport(),
                pcReport = PcReport(cfg),
            });
        });

        app.MapPost("/api/admin/update", async (HttpContext ctx, Hub hub, Updater up, Audit audit) =>
        {
            var s = Admin(ctx, hub);
            if (s == null) return Results.StatusCode(403);
            var now = ctx.Request.Query["now"] == "1";
            audit.Write(s.Username, now ? "atualizar-agora" : "verificar-atualizacoes", "");
            var msg = await up.CheckAndRecord(force: now);
            return Results.Json(new { message = msg, ok = Updater.LastOk });
        });

        app.MapPost("/api/admin/install-now", (HttpContext ctx, Hub hub, Audit audit) =>
        {
            var s = Admin(ctx, hub);
            if (s == null) return Results.StatusCode(403);
            var (exists, running, _) = TaskState();
            if (!exists) return Results.Json(new { error = "A tarefa de instalação ainda não foi criada no servidor (ver o guia: criar-tarefa-instalar.ps1)." }, statusCode: 400);
            if (running) return Results.Json(new { error = "A instalação já está a correr." }, statusCode: 400);
            var (code, output) = Run("schtasks.exe", $"/run /tn {InstallTask}");
            audit.Write(s.Username, "instalar-agora", output.Trim());
            return code == 0 ? Results.Ok() : Results.Json(new { error = output.Trim() }, statusCode: 400);
        });

        // definições de atualização, alteráveis na consola
        app.MapPost("/api/admin/settings", async (HttpContext ctx, Hub hub, ServerConfig cfg, Audit audit) =>
        {
            var s = Admin(ctx, hub);
            if (s == null) return Results.StatusCode(403);
            var b = await ctx.Request.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
            if (b == null) return Results.BadRequest();
            var win = b["window"]?.ToString() ?? cfg.UpdateWindow;
            var parts = win.Split('-');
            if (parts.Length != 2 || !TimeSpan.TryParse(parts[0], out _) || !TimeSpan.TryParse(parts[1], out _))
                return Results.Json(new { error = "Horário inválido (ex.: 20:00-07:00)." }, statusCode: 400);
            lock (cfg)
            {
                cfg.AutoUpdate = b["autoUpdate"]?.GetValue<bool>() ?? cfg.AutoUpdate;
                cfg.UpdateWindow = win;
                cfg.Save();
            }
            audit.Write(s.Username, "definicoes-atualizacao", $"auto={cfg.AutoUpdate} janela={cfg.UpdateWindow}");
            return Results.Ok();
        });

        app.MapGet("/api/admin/room-status", (HttpContext ctx, Hub hub, string school, string folder) =>
            Admin(ctx, hub) == null ? Results.StatusCode(403) : Results.Json(hub.RoomStatus(school, folder)));
    }

    /// <summary>Estado da tarefa agendada que corre o instalar-agora.ps1 com credenciais de administrador.</summary>
    private static (bool Exists, bool Running, string LastRun) TaskState()
    {
        if (!OperatingSystem.IsWindows()) return (false, false, "");
        var (code, output) = Run("schtasks.exe", $"/query /tn {InstallTask} /fo list /v");
        if (code != 0) return (false, false, "");
        // o texto vem no idioma do Windows: procura as palavras em português e em inglês
        var running = output.Contains("Em execução", StringComparison.OrdinalIgnoreCase) ||
                      output.Contains("A ser executad", StringComparison.OrdinalIgnoreCase) ||
                      output.Contains(": Running", StringComparison.OrdinalIgnoreCase);
        var last = output.Split('\n').FirstOrDefault(l => l.Contains("Última execução", StringComparison.OrdinalIgnoreCase) ||
                                                          l.Contains("Last Run Time", StringComparison.OrdinalIgnoreCase));
        return (true, running, last == null ? "" : last[(last.IndexOf(':') + 1)..].Trim());
    }

    /// <summary>Último relatório do instalar-agora.ps1 (CSV em data\).</summary>
    private static object? LatestReport()
    {
        try
        {
            var f = new DirectoryInfo(ServerConfig.DataDir).GetFiles("instalar-agora-*.csv")
                .OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
            if (f == null) return null;
            var rows = File.ReadAllLines(f.FullName, Encoding.UTF8).Skip(1)
                .Select(l => l.Split(';').Select(c => c.Trim('"')).ToArray())
                .Where(c => c.Length >= 2)
                .Select(c => new { pc = c[0], result = c[1] })
                .ToList();
            var summary = rows.GroupBy(r => Category(r.result))
                .Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count).ToList();
            return new { file = f.Name, time = f.LastWriteTime, total = rows.Count, summary, rows };
        }
        catch { return null; }
    }

    /// <summary>O que cada PC disse da última vez que correu o instalador (GPO no arranque ou tarefa imediata).</summary>
    private static object? PcReport(ServerConfig cfg)
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(cfg.DeployDir, "relatorios"));
            if (!dir.Exists) return new { missing = true };
            var rows = dir.GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).Take(2000).Select(f =>
            {
                var last = "";
                try { last = File.ReadLines(f.FullName, Encoding.UTF8).LastOrDefault(l => l.Trim().Length > 0) ?? ""; } catch { }
                var msg = last.Length > 20 ? last[20..] : last; // tira "aaaa-mm-dd hh:mm:ss "
                return new { pc = Path.GetFileNameWithoutExtension(f.Name), result = msg, time = f.LastWriteTime };
            }).ToList();
            var summary = rows.GroupBy(r => Category(r.result))
                .Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count).ToList();
            return new { missing = false, total = rows.Count, summary, rows };
        }
        catch { return null; }
    }

    private static string Category(string r) =>
        r.StartsWith("Instalado", StringComparison.OrdinalIgnoreCase) ? "Instalado agora" :
        r.StartsWith("Já instalado", StringComparison.OrdinalIgnoreCase) ? "Já tinha o agente" :
        r.StartsWith("Sem sala", StringComparison.OrdinalIgnoreCase) ? "Não pertence a uma sala ativa" :
        r.Contains("desligado", StringComparison.OrdinalIgnoreCase) ? "Desligado ou inacessível" :
        r.Contains(".NET", StringComparison.OrdinalIgnoreCase) ? "Falta o .NET Framework" :
        r.Contains("não está ativa", StringComparison.OrdinalIgnoreCase) ? "Sala não ativa" :
        "Erro / outro";

    private static (int Code, string Output) Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(850),
            })!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
