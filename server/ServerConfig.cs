using System.Security.Cryptography;
using System.Text.Json;

namespace ClassroomServer;

public sealed class TeacherAccount
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>Administrador: vê todas as salas e gere os utilizadores.</summary>
    public bool IsAdmin { get; set; }
    /// <summary>Salas que este professor pode controlar ("*" = todas). Ignorado para administradores.</summary>
    public List<string> Rooms { get; set; } = new();

    /// <summary>Acesso: "*" = tudo; "EBVG/*" = escola inteira; "EBVG/Sala TIC1" = uma sala.</summary>
    public bool CanSee(string school, string room) =>
        IsAdmin || Rooms.Contains("*") ||
        Rooms.Contains(school + "/*", StringComparer.OrdinalIgnoreCase) ||
        Rooms.Contains(school + "/" + room, StringComparer.OrdinalIgnoreCase) ||
        (school.Length == 0 && Rooms.Contains(room, StringComparer.OrdinalIgnoreCase));
}

public sealed class ServerConfig
{
    public int ConfigVersion { get; set; }
    public int Port { get; set; } = 8443;
    /// <summary>Nome e frase mostrados na página de login e no topo da consola.</summary>
    public string BrandName { get; set; } = "SalaViva";
    public string BrandTagline { get; set; } = "A sala inteira, num só ecrã.";
    /// <summary>Escolas do agrupamento (Id = nome da pasta no AD).</summary>
    public List<School> Schools { get; set; } = new();
    /// <summary>Pasta do AD com uma subpasta por escola e, dentro, as pastas das salas com as contas MAC dos PCs.</summary>
    public string AdBase { get; set; } = "OU=_Computadores MAC AEEQ,DC=queiroz,DC=local";
    /// <summary>Nome na consola e instalação ativa, por pasta do AD.</summary>
    public List<RoomRule> Rooms { get; set; } = new();
    /// <summary>Pasta partilhada de instalação (instalar.json é atualizado quando se mudam as salas ativas).</summary>
    public string DeployDir { get; set; } = @"C:\Deploy\SalaDeAula";
    /// <summary>Nome da partilha de DeployDir (os PCs atualizam-se a partir de \\SERVIDOR\&lt;partilha&gt;\leve).</summary>
    public string DeployShare { get; set; } = "SalaDeAula$";
    /// <summary>Nome de início de sessão dos alunos (sem domínio), ex.: a9999. Usado nos PCs partilhados
    /// ("só aparece quando é um aluno"): quem não corresponde conta como professor.</summary>
    public string StudentLoginPattern { get; set; } = @"^a\d+$";
    /// <summary>Chave partilhada que os agentes dos alunos têm de apresentar.</summary>
    public string EnrollmentKey { get; set; } = "";
    public List<TeacherAccount> Teachers { get; set; } = new();
    /// <summary>Certificado PFX próprio (ex.: emitido pela CA do domínio). Vazio = autoassinado gerado.</summary>
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    /// <summary>
    /// Motor de HTTPS: "auto" (por omissão), "windows" (SChannel) ou "managed" (BouncyCastle, dentro do programa).
    /// Em "auto" usa "managed" em Windows anteriores ao 10 / Server 2016 (ex.: Server 2008 R2), onde o SChannel
    /// não tem as cifras que os browsers atuais exigem.
    /// </summary>
    public string TlsEngine { get; set; } = "auto";
    /// <summary>
    /// Aceita agentes também pelo named pipe \\SERVIDOR\pipe\SalaDeAula (SMB, porta 445).
    /// Útil quando a firewall entre VLANs não deixa os PCs chegar à porta HTTPS do servidor.
    /// </summary>
    public bool AgentPipe { get; set; } = true;
    /// <summary>Intervalo entre miniaturas enviadas por cada aluno.</summary>
    public int ThumbnailIntervalMs { get; set; } = 2000;
    public int ThumbnailWidth { get; set; } = 360;
    /// <summary>Imagens por segundo quando o professor abre um ecrã em grande.</summary>
    public int FullFrameFps { get; set; } = 6;
    public int SessionHours { get; set; } = 12;
    /// <summary>Se o agente perder a ligação ao servidor durante este tempo, desbloqueia sozinho (failsafe).</summary>
    public int AgentFailsafeUnlockMinutes { get; set; } = 10;

    /// <summary>Atualização automática do servidor a partir das Releases do GitHub.</summary>
    public bool AutoUpdate { get; set; } = true;
    public int UpdateCheckMinutes { get; set; } = 60;
    /// <summary>Só se reinicia para atualizar dentro desta janela (hora local), e sem consolas abertas.</summary>
    public string UpdateWindow { get; set; } = "20:00-07:00";
    public string UpdateReleaseApi { get; set; } = "https://api.github.com/repos/sendas/Remote/releases/tags/auto";
    public string UpdateAssetName { get; set; } = "SalaViva-Servidor.zip";

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string DataDir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("CLASSROOM_DATA")
                    ?? Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string ConfigPath => Path.Combine(DataDir, "classroom-server.json");

    public static ServerConfig LoadOrCreate(out string? firstRunMessage)
    {
        firstRunMessage = null;
        if (File.Exists(ConfigPath))
        {
            var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(ConfigPath), JsonOpts)
                      ?? throw new InvalidOperationException("Configuração inválida.");
            if (string.IsNullOrWhiteSpace(cfg.EnrollmentKey))
                throw new InvalidOperationException("EnrollmentKey vazia em " + ConfigPath);
            // versões antigas descarregavam só o .exe (sem a consola web)
            if (cfg.UpdateAssetName.Equals("SalaViva-Servidor.exe", StringComparison.OrdinalIgnoreCase))
                cfg.UpdateAssetName = "SalaViva-Servidor.zip";
            if (cfg.ConfigVersion < 2)
            {
                // versão 1: "sem salas" queria dizer "todas" -> essas contas passam a administradores
                foreach (var t in cfg.Teachers.Where(t => t.Rooms.Count == 0)) t.IsAdmin = true;
                cfg.ConfigVersion = 2;
                cfg.Save();
            }
            return cfg;
        }

        var pwd = RandomToken(9);
        var created = new ServerConfig
        {
            EnrollmentKey = RandomToken(24),
            ConfigVersion = 2,
            Teachers = { new TeacherAccount { Username = "admin", DisplayName = "Administrador", IsAdmin = true, PasswordHash = PasswordHasher.Hash(pwd) } },
        };
        created.Save();
        firstRunMessage =
            "Primeira execução: configuração criada em " + ConfigPath + Environment.NewLine +
            "  Utilizador: admin" + Environment.NewLine +
            "  Palavra-passe: " + pwd + Environment.NewLine +
            "  Chave dos agentes (EnrollmentKey): " + created.EnrollmentKey + Environment.NewLine +
            "Muda a palavra-passe com: ClassroomServer set-password admin <nova>";
        File.WriteAllText(Path.Combine(DataDir, "LEIA-ME-credenciais-iniciais.txt"), firstRunMessage);
        return created;
    }

    private static readonly object SaveGate = new();

    public void Save()
    {
        lock (SaveGate)
        {
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Copy(tmp, ConfigPath, true);
            File.Delete(tmp);
        }
    }

    public static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace('+', 'A').Replace('/', 'B').TrimEnd('=');
}

public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2-sha256") return false;
        var iters = int.Parse(p[1]);
        var salt = Convert.FromBase64String(p[2]);
        var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
