using System.Text.Json;

namespace ClassroomAgent;

public sealed class AgentConfig
{
    /// <summary>Um ou mais endereços do servidor, tentados por ordem (ex.: IP interno e nome público).</summary>
    public List<string> ServerUrls { get; set; } = new() { "wss://servidor-escola:8443/ws/agent" };
    public string EnrollmentKey { get; set; } = "";
    /// <summary>SHA-256 do certificado do servidor (ClassroomServer show-pin). Vazio = validação normal de TLS.</summary>
    public string CertificatePin { get; set; } = "";
    /// <summary>Nome da sala (ex.: "Sala 12"). Vazio = usa o prefixo do nome do PC antes do último "-".</summary>
    public string Room { get; set; } = "";
    /// <summary>Mostra um aviso no ecrã do aluno quando o professor abre o seu ecrã em grande (transparência/RGPD).</summary>
    public bool ShowViewIndicator { get; set; } = true;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassroomAgent");

    public static string FilePath => Path.Combine(Dir, "agent.json");

    public static AgentConfig Load()
    {
        var cfg = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            : null;
        cfg ??= new AgentConfig();
        if (string.IsNullOrWhiteSpace(cfg.Room))
        {
            // "SALA12-PC05" -> "SALA12"
            var n = Environment.MachineName;
            var i = n.LastIndexOf('-');
            cfg.Room = i > 0 ? n[..i] : "";
        }
        return cfg;
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(AgentConfig.Dir);
            var p = Path.Combine(AgentConfig.Dir, "agent.log");
            lock (Gate)
            {
                if (File.Exists(p) && new FileInfo(p).Length > 2_000_000)
                    File.Move(p, p + ".old", true);
                File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* nunca falhar por causa do log */ }
    }
}
