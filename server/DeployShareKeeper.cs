using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace ClassroomServer;

/// <summary>
/// Mantém a partilha de instalação em ordem, sem scripts à mão:
///  - instalar.cmd (o script da GPO) sempre na versão certa: verifica o .NET e, se faltar (Windows 7),
///    instala o .NET Framework 4.8 sozinho, e regista o resultado de cada PC no servidor;
///  - partilha SalaVivaRelatorios$ onde os PCs escrevem o que aconteceu (vê-se na consola);
///  - descarrega uma vez o instalador offline do .NET Framework 4.8 para a partilha.
/// </summary>
public sealed class DeployShareKeeper : BackgroundService
{
    private const string DotNetFile = "ndp48-x86-x64-allos-enu.exe";
    // ligação oficial da Microsoft para o instalador offline do .NET Framework 4.8
    private const string DotNetUrl = "https://go.microsoft.com/fwlink/?linkid=2088631";

    private readonly ServerConfig _cfg;
    private readonly ILogger<DeployShareKeeper> _log;

    public DeployShareKeeper(ServerConfig cfg, ILogger<DeployShareKeeper> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public static string Status { get; private set; } = "";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try { await EnsureOnce(ct); }
            catch (Exception ex) { _log.LogWarning("Partilha de instalação: {Msg}", ex.Message); Status = "Erro: " + ex.Message; }
            await Task.Delay(TimeSpan.FromHours(6), ct).ContinueWith(_ => { });
        }
    }

    private async Task EnsureOnce(CancellationToken ct)
    {
        var dir = _cfg.DeployDir;
        if (!Directory.Exists(dir)) { Status = "Pasta " + dir + " não existe (corre preparar-partilha.ps1)."; return; }

        // 1) instalar.cmd
        var cmd = Path.Combine(dir, "instalar.cmd");
        var text = InstallCmd(Environment.MachineName);
        if (!File.Exists(cmd) || File.ReadAllText(cmd) != text) File.WriteAllText(cmd, text, Encoding.ASCII);

        // 2) relatórios: pasta + permissões + partilha
        var rel = Path.Combine(dir, "relatorios");
        Directory.CreateDirectory(rel);
        var domComputers = DomainComputersSid();
        if (domComputers != null)
        {
            Run("icacls.exe", $"\"{rel}\" /grant *{domComputers}:(OI)(CI)M");
            var (_, shares) = Run("net.exe", "share");
            if (!shares.Contains("SalaVivaRelatorios$", StringComparison.OrdinalIgnoreCase))
            {
                var dc = new SecurityIdentifier(domComputers).Translate(typeof(NTAccount)).Value;
                var adm = new SecurityIdentifier("S-1-5-32-544").Translate(typeof(NTAccount)).Value;
                Run("net.exe", $"share \"SalaVivaRelatorios$={rel}\" \"/GRANT:{dc},CHANGE\" \"/GRANT:{adm},FULL\"");
            }
        }

        // 3) .NET Framework 4.8 offline (para os Windows 7 que não o têm)
        var netDir = Path.Combine(dir, "dotnet");
        var netFile = Path.Combine(netDir, DotNetFile);
        if (!File.Exists(netFile))
        {
            Directory.CreateDirectory(netDir);
            var tmp = netFile + ".part";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SalaViva");
            using (var resp = await http.GetAsync(DotNetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }
            if (new FileInfo(tmp).Length < 20_000_000) { File.Delete(tmp); throw new IOException("o download do .NET 4.8 veio incompleto"); }
            File.Move(tmp, netFile, true);
            _log.LogInformation(".NET Framework 4.8 descarregado para {File}", netFile);
        }
        Status = "OK";
    }

    private static string? DomainComputersSid()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var root = new System.DirectoryServices.DirectoryEntry("LDAP://RootDSE");
            var dn = root.Properties["defaultNamingContext"].Value?.ToString();
            using var dom = new System.DirectoryServices.DirectoryEntry("LDAP://" + dn);
            var sid = new SecurityIdentifier((byte[])dom.Properties["objectSid"].Value!, 0);
            return sid.Value + "-515";
        }
        catch { return null; }
    }

    private static (int, string) Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>Script de arranque da GPO (e da tarefa imediata). Corre como SYSTEM no PC.</summary>
    public static string InstallCmd(string server) =>
        string.Join("\r\n", new[]
        {
            "@echo off",
            "rem SalaViva - gerado pelo servidor (nao editar: e reescrito automaticamente)",
            $@"set REL=\\{server}\SalaVivaRelatorios$\%COMPUTERNAME%.log",
            "call :net",
            "if %NETD% LSS 394802 call :instalanet",
            "if %NETD% LSS 394802 (",
            "  echo %date% %time:~0,8% Falta o .NET Framework 4.6.2 ou mais recente neste PC ^(versao %NETV%^)>>\"%REL%\" 2>nul",
            "  exit /b 0",
            ")",
            "\"%~dp0leve\\ClassroomAgent.exe\" --instalar",
            "if errorlevel 1 echo %date% %time:~0,8% ERRO: o instalador terminou com o codigo %errorlevel%>>\"%REL%\" 2>nul",
            "exit /b 0",
            "",
            ":net",
            "set NETV=0",
            "for /f \"tokens=3\" %%a in ('reg query \"HKLM\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\" /v Release 2^>nul ^| find \"Release\"') do set NETV=%%a",
            "set /a NETD=%NETV% 2>nul",
            "goto :eof",
            "",
            ":instalanet",
            $"if not exist \"%~dp0dotnet\\{DotNetFile}\" goto :eof",
            "rem no maximo uma tentativa por hora",
            "set MARCA=%windir%\\Temp\\salaviva-dotnet.txt",
            "set AGORA=%date% %time:~0,2%",
            "if exist \"%MARCA%\" findstr /c:\"%AGORA%\" \"%MARCA%\" >nul && goto :eof",
            "echo %AGORA%>\"%MARCA%\"",
            "echo %date% %time:~0,8% A instalar o .NET Framework 4.8 (pode demorar 10 minutos)>>\"%REL%\" 2>nul",
            $"\"%~dp0dotnet\\{DotNetFile}\" /q /norestart",
            "set RC=%errorlevel%",
            "echo %date% %time:~0,8% .NET Framework 4.8 terminou com o codigo %RC% (0 = instalado, 3010 = instalado e falta reiniciar)>>\"%REL%\" 2>nul",
            "call :net",
            "goto :eof",
            "",
        });
}
