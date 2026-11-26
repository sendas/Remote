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

        // pacote para pen USB: tudo o que é preciso para instalar à mão num PC (agente, .NET, chave)
        app.MapGet("/api/admin/pen-package", (HttpContext ctx, Hub hub, ServerConfig cfg, Audit audit) =>
        {
            var s = Admin(ctx, hub);
            if (s == null) return Results.StatusCode(403);
            var dir = cfg.DeployDir;
            var leve = Path.Combine(dir, "leve");
            var json = Path.Combine(dir, "instalar.json");
            if (!Directory.Exists(leve) || !File.Exists(json))
                return Results.Json(new { error = "A partilha de instalação ainda não está preparada (preparar-partilha.ps1)." }, statusCode: 400);
            audit.Write(s.Username, "pacote-pen", "");
            var tmp = Path.Combine(Path.GetTempPath(), $"salaviva-pen-{Guid.NewGuid():N}.zip");
            try
            {
                using (var z = System.IO.Compression.ZipFile.Open(tmp, System.IO.Compression.ZipArchiveMode.Create))
                {
                    void Add(string file, string name, bool store = false) =>
                        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, file, "SalaViva/" + name,
                            store ? System.IO.Compression.CompressionLevel.NoCompression : System.IO.Compression.CompressionLevel.Fastest);
                    void AddText(string name, string text)
                    {
                        var e = z.CreateEntry("SalaViva/" + name);
                        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                        w.Write(text);
                    }
                    foreach (var f in Directory.GetFiles(leve)) Add(f, "leve/" + Path.GetFileName(f));
                    Add(json, "instalar.json");
                    AddText("instalar.cmd", DeployShareKeeper.InstallCmd(Environment.MachineName));
                    AddText("Instalar-SalaViva.cmd", PenLauncher);
                    AddText("LEIA-ME.txt", PenReadme);
                    var net = Path.Combine(dir, "dotnet");
                    if (Directory.Exists(net))
                        foreach (var f in Directory.GetFiles(net, "*.exe")) Add(f, "dotnet/" + Path.GetFileName(f), store: true);
                }
                var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Delete, 81920, FileOptions.DeleteOnClose);
                return Results.File(stream, "application/zip", "SalaViva-pen.zip");
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
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

    /// <summary>Duplo clique na pen: pede administrador, instala o .NET se faltar e abre o assistente.</summary>
    public const string PenLauncher =
        "@echo off\r\n" +
        "rem SalaViva - instalar neste PC (duplo clique)\r\n" +
        "net session >nul 2>&1\r\n" +
        "if errorlevel 1 (\r\n" +
        "  powershell -NoProfile -Command \"Start-Process -FilePath '%~f0' -Verb RunAs\"\r\n" +
        "  exit /b\r\n" +
        ")\r\n" +
        "set NETV=0\r\n" +
        "for /f \"tokens=3\" %%a in ('reg query \"HKLM\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\" /v Release 2^>nul ^| find \"Release\"') do set NETV=%%a\r\n" +
        "set /a NETD=%NETV% 2>nul\r\n" +
        "if %NETD% GEQ 394802 goto assistente\r\n" +
        "if not exist \"%~dp0dotnet\\ndp48-x86-x64-allos-enu.exe\" (\r\n" +
        "  echo Este PC precisa do .NET Framework 4.8 e a pasta dotnet esta vazia.\r\n" +
        "  pause\r\n" +
        "  exit /b\r\n" +
        ")\r\n" +
        "echo A instalar o .NET Framework 4.8 - pode demorar 10 minutos. Nao feches esta janela.\r\n" +
        "\"%~dp0dotnet\\ndp48-x86-x64-allos-enu.exe\" /passive /norestart\r\n" +
        "echo .NET terminou com o codigo %errorlevel% (0 ou 3010 = instalado).\r\n" +
        ":assistente\r\n" +
        "start \"\" \"%~dp0leve\\ClassroomAgent.exe\"\r\n" +
        "exit /b\r\n";

    private const string PenReadme =
        "SalaViva - pen de instalacao\r\n\r\n" +
        "Num PC: abre a pasta SalaViva e faz duplo clique em Instalar-SalaViva.cmd.\r\n" +
        "Pede as credenciais de administrador, instala o .NET se faltar (Windows 7) e abre o assistente:\r\n" +
        "escolhe a escola, a sala e o nome a mostrar, e carrega em Instalar.\r\n\r\n" +
        "ATENCAO: esta pasta tem a chave do servidor (instalar.json). Nao a partilhes; apaga-a no fim.\r\n";

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
