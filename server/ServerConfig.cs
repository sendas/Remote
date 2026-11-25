using System.Security.Cryptography;
using System.Text.Json;

namespace ClassroomServer;

public sealed class TeacherAccount
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>Salas que este professor pode controlar. Vazio = todas.</summary>
    public List<string> Rooms { get; set; } = new();
}

public sealed class ServerConfig
{
    public int Port { get; set; } = 8443;
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
    /// <summary>Intervalo entre miniaturas enviadas por cada aluno.</summary>
    public int ThumbnailIntervalMs { get; set; } = 2000;
    public int ThumbnailWidth { get; set; } = 360;
    /// <summary>Imagens por segundo quando o professor abre um ecrã em grande.</summary>
    public int FullFrameFps { get; set; } = 6;
    public int SessionHours { get; set; } = 12;
    /// <summary>Se o agente perder a ligação ao servidor durante este tempo, desbloqueia sozinho (failsafe).</summary>
    public int AgentFailsafeUnlockMinutes { get; set; } = 10;

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
            return cfg;
        }

        var pwd = RandomToken(9);
        var created = new ServerConfig
        {
            EnrollmentKey = RandomToken(24),
            Teachers = { new TeacherAccount { Username = "admin", PasswordHash = PasswordHasher.Hash(pwd) } },
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

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));

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
