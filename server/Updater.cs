using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomServer;

/// <summary>
/// Atualização automática do servidor a partir das Releases do GitHub.
/// De X em X minutos vê a release "auto" do repositório; se o ClassroomServer.exe publicado for diferente
/// do que está a correr, descarrega-o e agenda uma troca (parar serviço → copiar → arrancar) com o Agendador
/// de Tarefas do Windows, para que a troca sobreviva ao fim deste processo.
///
/// NOTA: em Windows Server 2008 R2 a ligação de saída ao GitHub precisa de TLS 1.2 no SChannel; se não estiver
/// ligado, a verificação falha (fica registado) e a atualização continua a poder ser feita à mão.
/// </summary>
public sealed class Updater : BackgroundService
{
    private readonly ServerConfig _cfg;
    private readonly ILogger<Updater> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public Updater(ServerConfig cfg, ILogger<Updater> log)
    {
        _cfg = cfg;
        _log = log;
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SalaViva-Updater");
    }

    private string StateFile => Path.Combine(ServerConfig.DataDir, "update", "estado.json");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.AutoUpdate) return;
        await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try { await CheckOnce(ct); }
            catch (Exception ex) { _log.LogWarning("Auto-update: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(_cfg.UpdateCheckMinutes, 10, 1440)), ct)
                .ContinueWith(_ => { });
        }
    }

    public async Task<string> CheckOnce(CancellationToken ct = default)
    {
        var api = _cfg.UpdateReleaseApi;
        using var resp = await Http.GetAsync(api, ct);
        resp.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        var assets = release["assets"]?.AsArray() ?? new JsonArray();
        var asset = assets.FirstOrDefault(a =>
            string.Equals((string?)a?["name"], _cfg.UpdateAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset == null) return $"A release não tem o ficheiro {_cfg.UpdateAssetName}.";

        var url = (string)asset!["browser_download_url"]!;
        var size = (long?)asset["size"] ?? 0;
        var updatedAt = (string?)asset["updated_at"] ?? "";

        var state = ReadState();
        if (state.Size == size && state.UpdatedAt == updatedAt)
            return "Já está na versão mais recente.";

        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        var newExe = Path.Combine(ServerConfig.DataDir, "update", "ClassroomServer.new.exe");
        await Download(url, newExe, ct);

        var running = Environment.ProcessPath!;
        if (Sha256(newExe) == Sha256(running))
        {
            WriteState(size, updatedAt);
            return "O ficheiro descarregado é igual ao atual. Nada a trocar.";
        }

        WriteState(size, updatedAt);
        if (OperatingSystem.IsWindows())
        {
            ScheduleSwap(newExe, running);
            _log.LogWarning("Auto-update: nova versão descarregada; troca agendada em ~1 minuto.");
            return "Nova versão descarregada. O serviço vai reiniciar dentro de ~1 minuto.";
        }
        return "Nova versão descarregada em " + newExe + " (troca automática só em Windows).";
    }

    private async Task Download(string url, string dest, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs, ct);
    }

    /// <summary>Agenda uma tarefa única (SYSTEM) que para o serviço, troca o exe e volta a arrancar.</summary>
    private void ScheduleSwap(string newExe, string target)
    {
        var dir = Path.GetDirectoryName(StateFile)!;
        var cmd = Path.Combine(dir, "trocar.cmd");
        File.WriteAllText(cmd, string.Join("\r\n", new[]
        {
            "@echo off",
            "net stop ClassroomServer",
            "ping -n 4 127.0.0.1 > nul",
            $"copy /y \"{newExe}\" \"{target}\"",
            "net start ClassroomServer",
            "schtasks /delete /tn SalaVivaUpdate /f",
            "exit /b 0",
        }));
        // corre daqui a 1 minuto, como SYSTEM, independente deste processo
        var when = DateTime.Now.AddMinutes(1).ToString("HH:mm");
        Run("schtasks.exe", $"/create /tn SalaVivaUpdate /ru SYSTEM /sc once /st {when} /tr \"cmd /c \\\"{cmd}\\\"\" /f");
    }

    private static void Run(string exe, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
        { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(15000);
    }

    private static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private sealed record UpdateState(long Size, string UpdatedAt);

    private UpdateState ReadState()
    {
        try { return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StateFile)) ?? new(0, ""); }
        catch { return new(0, ""); }
    }

    private void WriteState(long size, string updatedAt)
    {
        try { File.WriteAllText(StateFile, JsonSerializer.Serialize(new UpdateState(size, updatedAt))); }
        catch { }
    }
}
