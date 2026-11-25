using System.Collections.Concurrent;
using System.Net;
using ClassroomServer;
using Microsoft.Extensions.FileProviders;

// ------------------------------------------------------------------ comandos de linha
if (args.Length > 0 && !args[0].StartsWith("--"))
    return Cli.Run(args);

var cfg = ServerConfig.LoadOrCreate(out var firstRun);
var cert = Certificates.Load(cfg);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "ClassroomServer");
var managedTls = cfg.TlsEngine.Equals("managed", StringComparison.OrdinalIgnoreCase) ||
                 (cfg.TlsEngine.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                  OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10));
ManagedTls? tls = null;
if (managedTls)
{
    var (pfx, pfxPassword) = Certificates.PfxBytes(cfg);
    tls = new ManagedTls(pfx, pfxPassword,
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Tls"));
    // o TLS gerido bloqueia uma thread por ligação: garante threads suficientes desde o início
    ThreadPool.GetMinThreads(out var w, out var io);
    ThreadPool.SetMinThreads(Math.Max(w, 300), Math.Max(io, 300));
}

builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Any, cfg.Port, l =>
    {
        if (tls != null)
        {
            l.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
            l.Use(tls.Middleware);
        }
        else l.UseHttps(cert);
    });
    k.Limits.MaxRequestBodySize = 64 * 1024;
});
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<Audit>();
builder.Services.AddSingleton<RoomCatalog>();
builder.Services.AddSingleton<PersistentState>();
builder.Services.AddSingleton<Hub>();
// parar o serviço em poucos segundos, mesmo com ligações abertas (consola, agentes)
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
if (cfg.AgentPipe) builder.Services.AddHostedService<PipeListener>();
builder.Services.AddSingleton(sp => new Updater(sp.GetRequiredService<ServerConfig>(), sp.GetRequiredService<ILogger<Updater>>(), sp));
if (cfg.AutoUpdate) builder.Services.AddHostedService(sp => sp.GetRequiredService<Updater>());

var app = builder.Build();
Hub.CleanupOldFiles();
var log = app.Logger;
if (firstRun != null) log.LogWarning("{Msg}", firstRun);
var pin = Certificates.Pin(cert);
File.WriteAllText(Path.Combine(ServerConfig.DataDir, "certificado-pin.txt"), pin);
log.LogInformation("Consola do professor: https://{Host}:{Port}/", Dns.GetHostName(), cfg.Port);
log.LogInformation("CertificatePin para os agentes: {Pin}", pin);
log.LogInformation("Motor HTTPS: {Engine}", managedTls ? "interno (BouncyCastle)" : "Windows/sistema");

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' blob: data:; connect-src 'self' wss: ws:; style-src 'self' 'unsafe-inline'; script-src 'self'";
    await next();
});

var web = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = web });
app.UseStaticFiles(new StaticFileOptions { FileProvider = web });

// ------------------------------------------------------------------ login (com travão contra força bruta)
var failures = new ConcurrentDictionary<string, (int Count, DateTimeOffset Until)>();

app.MapPost("/api/login", async (HttpContext ctx, Hub hub, Audit audit) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (failures.TryGetValue(ip, out var f) && f.Until > DateTimeOffset.UtcNow)
        return Results.Json(new { error = "Demasiadas tentativas. Tenta daqui a pouco." }, statusCode: 429);

    LoginRequest? req;
    try { req = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); } catch { req = null; }
    if (req == null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
        return Results.BadRequest();

    var s = hub.Login(req.Username, req.Password);
    if (s == null)
    {
        var count = (failures.TryGetValue(ip, out var prev) ? prev.Count : 0) + 1;
        var lockout = count >= 5 ? TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, count - 5))) : TimeSpan.Zero;
        failures[ip] = (count, DateTimeOffset.UtcNow + lockout);
        audit.Write(req.Username, "login-falhado", ip);
        await Task.Delay(400);
        return Results.Json(new { error = "Utilizador ou palavra-passe errados." }, statusCode: 401);
    }
    failures.TryRemove(ip, out _);
    audit.Write(s.Username, "login", ip);
    return Results.Json(new { token = s.Token, username = s.Username });
});

TeacherSession? Auth(HttpContext ctx, Hub hub) =>
    hub.ValidateToken(ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", ""));

// nome e frase (públicos: aparecem na página de login)
app.MapGet("/api/brand", (ServerConfig c) => Results.Json(new { name = c.BrandName, tagline = c.BrandTagline }));

app.MapGet("/api/me", (HttpContext ctx, Hub hub) =>
{
    var s = Auth(ctx, hub);
    return s == null ? Results.Unauthorized() : Results.Json(new
    {
        username = s.Username,
        displayName = s.Account.DisplayName,
        isAdmin = s.Account.IsAdmin,
        rooms = s.Account.Rooms,
    });
});

app.MapPost("/api/me/password", async (HttpContext ctx, Hub hub) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    var b = await ctx.Request.ReadFromJsonAsync<PasswordChange>();
    var err = b == null ? "Pedido inválido." : hub.ChangeOwnPassword(s, b.Current ?? "", b.Next ?? "");
    return err == null ? Results.Ok() : Results.Json(new { error = err }, statusCode: 400);
});

// escolas e salas que este utilizador pode ver (para o menu "Sala")
app.MapGet("/api/catalog", (HttpContext ctx, Hub hub) =>
{
    var s = Auth(ctx, hub);
    return s == null ? Results.Unauthorized() : Results.Json(hub.CatalogFor(s));
});

// ---------- ficheiros para os alunos e entregas
app.MapPost("/api/files", async (HttpContext ctx, Hub hub, Audit audit) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    var feat = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (feat != null && !feat.IsReadOnly) feat.MaxRequestBodySize = Hub.MaxFileBytes;
    var name = Hub.SafeName(Uri.UnescapeDataString(ctx.Request.Headers["X-File-Name"].ToString()));
    var id = Guid.NewGuid().ToString("N")[..16];
    var dir = Path.Combine(Hub.FilesDir, id);
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, name);
    await using (var fs = File.Create(path))
        await ctx.Request.Body.CopyToAsync(fs);
    var size = new FileInfo(path).Length;
    audit.Write(s.Username, "ficheiro-carregado", $"{name} ({size} bytes)");
    return Results.Json(new { id, name, size });
});

app.MapGet("/api/entregas", (HttpContext ctx, Hub hub) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    var list = Directory.GetDirectories(Hub.CollectDir).OrderByDescending(d => d).Take(30).Select(d => new
    {
        batch = Path.GetFileName(d),
        files = Hub.EntregasOf(d).Where(r => Hub.CanSeeEntrega(s.Account, r))
            .Select(r => new { file = r, size = new FileInfo(Path.Combine(Hub.CollectDir, r)).Length })
            .ToArray(),
    }).Where(b => b.files.Length > 0);
    return Results.Json(list);
});

app.MapGet("/api/entregas/download", (HttpContext ctx, Hub hub, string? file, string? batch) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    if (!string.IsNullOrEmpty(batch))
    {
        var zip = Hub.ZipBatch(batch, s.Account);
        if (zip == null) return Results.NotFound();
        var bytes = File.ReadAllBytes(zip);
        File.Delete(zip);
        return Results.File(bytes, "application/zip", $"entregas-{Hub.SafeName(batch)}.zip");
    }
    var root = Path.GetFullPath(Hub.CollectDir) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(Path.Combine(Hub.CollectDir, file ?? ""));
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
    if (!Hub.CanSeeEntrega(s.Account, Path.GetRelativePath(Hub.CollectDir, full))) return Results.StatusCode(403);
    return Results.File(File.OpenRead(full), "application/zip", Path.GetFileName(full));
});

// ---------- administração: utilizadores
app.MapGet("/api/users", (HttpContext ctx, Hub hub) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    return s.Account.IsAdmin ? Results.Json(hub.ListUsers()) : Results.StatusCode(403);
});

app.MapPost("/api/users", async (HttpContext ctx, Hub hub) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    if (!s.Account.IsAdmin) return Results.StatusCode(403);
    var u = await ctx.Request.ReadFromJsonAsync<UserEdit>();
    if (u == null) return Results.BadRequest();
    var err = hub.SaveUser(s.Username, u.Username ?? "", u.DisplayName, u.Password, u.IsAdmin, u.Rooms, u.Create);
    return err == null ? Results.Ok() : Results.Json(new { error = err }, statusCode: 400);
});

app.MapDelete("/api/users/{name}", (string name, HttpContext ctx, Hub hub) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    if (!s.Account.IsAdmin) return Results.StatusCode(403);
    var err = hub.DeleteUser(s.Username, name);
    return err == null ? Results.Ok() : Results.Json(new { error = err }, statusCode: 400);
});

// ---------- administração: escolas e salas (pastas do AD)
app.MapGet("/api/admin/rooms", (HttpContext ctx, Hub hub, RoomCatalog cat) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    return s.Account.IsAdmin ? Results.Json(cat.AdminView()) : Results.StatusCode(403);
});

app.MapPost("/api/admin/rooms", async (HttpContext ctx, Hub hub, RoomCatalog cat, Audit audit) =>
{
    var s = Auth(ctx, hub);
    if (s == null) return Results.Unauthorized();
    if (!s.Account.IsAdmin) return Results.StatusCode(403);
    var b = await ctx.Request.ReadFromJsonAsync<RoomsEdit>(new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (b == null) return Results.BadRequest();
    cat.Save(b.Schools ?? new(), b.Folders ?? new());
    audit.Write(s.Username, "salas-alteradas", $"{b.Folders?.Count(f => f.Active)} pastas ativas");
    return Results.Ok();
});

app.MapPost("/api/logout", (HttpContext ctx, Hub hub) =>
{
    var token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
    hub.Logout(token);
    return Results.Ok();
});

// ------------------------------------------------------------------ WebSockets
app.Map("/ws/agent", async (HttpContext ctx, Hub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.RunAgent(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
});

app.Map("/ws/teacher", async (HttpContext ctx, Hub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

    // impede que outra página web abra a consola em nome do professor
    var origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && Uri.TryCreate(origin, UriKind.Absolute, out var o) &&
        !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(o.Host, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 403;
        return;
    }

    var session = hub.ValidateToken(ctx.Request.Query["token"]);
    if (session == null) { ctx.Response.StatusCode = 401; return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.RunTeacher(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "?", session);
});

app.Run();
return 0;

internal sealed record LoginRequest(string Username, string Password);
internal sealed record PasswordChange(string? Current, string? Next);
internal sealed record UserEdit(string? Username, string? DisplayName, string? Password, bool? IsAdmin, List<string>? Rooms, bool Create);
internal sealed record RoomsEdit(List<School>? Schools, List<RoomRule>? Folders);

internal static class Cli
{
    public static int Run(string[] args)
    {
        switch (args[0])
        {
            case "set-password" when args.Length == 3:
            case "add-teacher" when args.Length >= 3:
            {
                var cfg = ServerConfig.LoadOrCreate(out var first);
                if (first != null) Console.WriteLine(first);
                var t = cfg.Teachers.FirstOrDefault(x => x.Username.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                if (t == null)
                {
                    if (args[0] == "set-password") { Console.Error.WriteLine("Utilizador não existe."); return 1; }
                    t = new TeacherAccount { Username = args[1] };
                    cfg.Teachers.Add(t);
                }
                t.PasswordHash = PasswordHasher.Hash(args[2]);
                if (args.Length > 3) t.Rooms = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                else t.IsAdmin = true; // sem salas = administrador (vê tudo)
                cfg.Save();
                Console.WriteLine($"OK: {t.Username}" + (t.Rooms.Count > 0 ? $" (salas: {string.Join(", ", t.Rooms)})" : " (todas as salas)"));
                return 0;
            }
            case "remove-teacher" when args.Length == 2:
            {
                var cfg = ServerConfig.LoadOrCreate(out _);
                var n = cfg.Teachers.RemoveAll(x => x.Username.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                cfg.Save();
                Console.WriteLine(n > 0 ? "Removido." : "Não encontrado.");
                return 0;
            }
            case "list-teachers":
            {
                var cfg = ServerConfig.LoadOrCreate(out _);
                foreach (var t in cfg.Teachers)
                    Console.WriteLine($"{t.Username}\t{(t.IsAdmin ? "(administrador)" : string.Join(",", t.Rooms))}");
                return 0;
            }
            case "show-pin":
            {
                var cfg = ServerConfig.LoadOrCreate(out _);
                Console.WriteLine(Certificates.Pin(Certificates.Load(cfg)));
                return 0;
            }
            case "show-key":
            {
                Console.WriteLine(ServerConfig.LoadOrCreate(out _).EnrollmentKey);
                return 0;
            }
            case "update":
            {
                var cfg = ServerConfig.LoadOrCreate(out _);
                var up = new Updater(cfg, LoggerFactory.Create(b => b.AddConsole()).CreateLogger<Updater>());
                try { Console.WriteLine(up.CheckOnce().GetAwaiter().GetResult()); return 0; }
                catch (Exception ex) { Console.Error.WriteLine("Falha: " + ex.Message); return 1; }
            }
        }

        Console.WriteLine("""
            ClassroomServer — comandos:
              (sem argumentos)                        arranca o servidor
              add-teacher <user> <pass> [sala1,sala2] cria/atualiza professor (sem salas = todas)
              set-password <user> <pass>              muda palavra-passe
              remove-teacher <user>
              list-teachers
              show-pin                                mostra o CertificatePin para os agentes
              show-key                                mostra a EnrollmentKey para os agentes
              update                                  verifica e instala já a versão mais recente do GitHub
            """);
        return 1;
    }
}
