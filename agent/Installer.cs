using System.Diagnostics;
using System.DirectoryServices;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace ClassroomAgent;

/// <summary>Configuração lida da partilha (instalar.json), ao lado da pasta "leve".</summary>
public sealed class InstallConfig
{
    public List<string> ServerUrls { get; set; } = new();
    public string EnrollmentKey { get; set; } = "";
    public string CertificatePin { get; set; } = "";
    /// <summary>Pasta do AD com a árvore de salas (contas MAC dos PCs dentro de pastas com o nome da sala).</summary>
    public string PastaSalas { get; set; } = "";
    /// <summary>Só PCs abaixo de uma pasta com este nome (ex.: "Alunos"). Vazio = qualquer.</summary>
    public string SoAbaixoDe { get; set; } = "Alunos";
    /// <summary>Salas ativas. Vazio ou "*" = todas. Permite instalar por fases.</summary>
    public List<string> Salas { get; set; } = new();
    public bool ShowViewIndicator { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    /// <summary>Nome a mostrar na consola para cada pasta do AD, ex.: { "Sala 13": "Sala TIC 2" }.</summary>
    public Dictionary<string, string> NomesSalas { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// "ClassroomAgent.exe --instalar": corre no PC do aluno (como SYSTEM: script de arranque da GPO,
/// ou lançado à distância). Descobre a sala do PC pelo endereço MAC no AD e instala/atualiza o agente.
/// Num PC que não pertença a nenhuma sala ativa, não faz nada.
/// </summary>
internal static class Installer
{
    private static readonly string Target = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClassroomAgent");

    private static string LogPath => Path.Combine(AgentConfig.Dir, "instalar.log");

    public static int Run()
    {
        try
        {
            Directory.CreateDirectory(AgentConfig.Dir);
            var src = Path.GetDirectoryName(Compat.ExePath)!;
            var cfgFile = new[] { Path.Combine(src, "..", "instalar.json"), Path.Combine(src, "instalar.json") }
                .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("instalar.json não encontrado junto de " + src);
            var cfg = LoadConfig(cfgFile);

            var macs = LocalMacs();
            var found = FindRoom(cfg, macs);
            if (found == null)
            {
                Write($"Sem sala: nenhum MAC deste PC ({string.Join(", ", macs)}) está nas salas de {cfg.PastaSalas}. Nada a fazer.");
                return 0;
            }
            var (escola, pasta) = found.Value;
            // o nome mostrado na consola é decidido no servidor (Escolas e salas); aqui guarda-se a pasta do AD
            var sala = pasta;
            var ativas = cfg.Salas.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (ativas.Count > 0 && !ativas.Contains("*") &&
                !ativas.Contains(escola + "/" + pasta, StringComparer.OrdinalIgnoreCase) &&
                !ativas.Contains(pasta, StringComparer.OrdinalIgnoreCase))
            {
                Write($"Sala '{escola}/{pasta}' não está ativa (ativa-a na consola: Escolas e salas). Nada a fazer.");
                return 0;
            }

            var agentJson = JsonSerializer.Serialize(new AgentConfig
            {
                ServerUrls = cfg.ServerUrls,
                EnrollmentKey = cfg.EnrollmentKey,
                CertificatePin = cfg.CertificatePin,
                Room = sala,
                School = escola,
                ShowViewIndicator = cfg.ShowViewIndicator,
                ShowTrayIcon = cfg.ShowTrayIcon,
            }, new JsonSerializerOptions { WriteIndented = true });

            var exeDst = Path.Combine(Target, "ClassroomAgent.exe");
            var upToDate = File.Exists(exeDst) && File.Exists(AgentConfig.FilePath) &&
                           SameFile(Path.Combine(src, "ClassroomAgent.exe"), exeDst) &&
                           File.ReadAllText(AgentConfig.FilePath).Trim() == agentJson.Trim() &&
                           Sc("query ClassroomAgent").Contains("RUNNING");
            if (upToDate)
            {
                Write($"Já instalado e atualizado (sala '{escola}/{sala}').");
                return 0;
            }

            // parar o agente antigo (se existir) e substituir os ficheiros
            Sc("stop ClassroomAgent");
            for (var i = 0; i < 15 && Sc("query ClassroomAgent").Contains("RUNNING"); i++) Thread.Sleep(1000);
            foreach (var p in Process.GetProcessesByName("ClassroomAgent"))
            {
                try
                {
                    if (p.Id != Compat.Pid && p.MainModule!.FileName!.StartsWith(Target, StringComparison.OrdinalIgnoreCase))
                        p.Kill();
                }
                catch { }
            }
            Thread.Sleep(1000);

            Directory.CreateDirectory(Target);
            foreach (var f in Directory.GetFiles(src))
            {
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(Target, Path.GetFileName(f)), true);
            }

            File.WriteAllText(AgentConfig.FilePath, agentJson);
            Run("icacls.exe", $"\"{AgentConfig.Dir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F");

            var bin = $"\"{exeDst}\"";
            if (Sc("query ClassroomAgent").Contains("1060"))
                Sc($"create ClassroomAgent binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto DisplayName= \"Sala de Aula - Agente\"");
            else
                Sc($"config ClassroomAgent binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto");
            Sc("failure ClassroomAgent reset= 0 actions= restart/2000/restart/2000/restart/2000");
            Sc("sdset ClassroomAgent D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)");
            Sc("start ClassroomAgent");
            for (var i = 0; i < 10 && !Sc("query ClassroomAgent").Contains("RUNNING"); i++) Thread.Sleep(1000);

            var ok = Sc("query ClassroomAgent").Contains("RUNNING");
            Write(ok ? $"Instalado (sala '{escola}/{sala}')." : $"ERRO: serviço não arrancou (sala '{escola}/{sala}').");
            return ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            Write("ERRO: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Lê instalar.json. "Salas" e "ServerUrls" podem vir como lista ou como texto simples
    /// (o ConvertTo-Json de PowerShell antigo às vezes escreve uma lista de 1 elemento como texto).
    /// </summary>
    private static InstallConfig LoadConfig(string path)
    {
        var n = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path).TrimStart('\uFEFF'),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();
        List<string> Lista(string k)
        {
            var v = n.FirstOrDefault(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value;
            if (v is System.Text.Json.Nodes.JsonArray arr) return arr.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
            if (v is System.Text.Json.Nodes.JsonValue val && val.TryGetValue<string>(out var str) && str.Length > 0) return new List<string> { str };
            return new List<string>();
        }
        string Texto(string k) => n.FirstOrDefault(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? "";
        var cfg = new InstallConfig
        {
            ServerUrls = Lista("ServerUrls"),
            EnrollmentKey = Texto("EnrollmentKey"),
            CertificatePin = Texto("CertificatePin"),
            PastaSalas = Texto("PastaSalas"),
            SoAbaixoDe = Texto("SoAbaixoDe"),
            Salas = Lista("Salas"),
        };
        var ind = n.FirstOrDefault(p => p.Key.Equals("ShowViewIndicator", StringComparison.OrdinalIgnoreCase)).Value;
        if (ind is System.Text.Json.Nodes.JsonValue b && b.TryGetValue<bool>(out var bv)) cfg.ShowViewIndicator = bv;
        var tr = n.FirstOrDefault(p => p.Key.Equals("ShowTrayIcon", StringComparison.OrdinalIgnoreCase)).Value;
        if (tr is System.Text.Json.Nodes.JsonValue tb && tb.TryGetValue<bool>(out var tbv)) cfg.ShowTrayIcon = tbv;
        if (n.FirstOrDefault(p => p.Key.Equals("NomesSalas", StringComparison.OrdinalIgnoreCase)).Value is System.Text.Json.Nodes.JsonObject nomes)
            foreach (var kv in nomes) cfg.NomesSalas[kv.Key] = kv.Value?.ToString() ?? "";
        return cfg;
    }

    /// <summary>MACs das placas de rede físicas, no formato das contas do AD (12 hex minúsculos).</summary>
    private static List<string> LocalMacs() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                        or NetworkInterfaceType.GigabitEthernet)
            .Select(n => n.GetPhysicalAddress().GetAddressBytes())
            .Where(b => b.Length == 6 && b.Any(x => x != 0))
            .Select(b => Compat.Hex(b).ToLowerInvariant())
            .Distinct()
            .ToList();

    /// <summary>
    /// Procura no AD (como conta de computador) uma conta com o MAC do PC — ou com o nome do PC —
    /// e devolve o nome da pasta onde está (a sala).
    /// </summary>
    private static (string School, string Folder)? FindRoom(InstallConfig cfg, List<string> macs)
    {
        var parts = macs.SelectMany(m => new[] { $"(sAMAccountName={m})", $"(userPrincipalName={m}@*)" }).ToList();
        parts.Add($"(cn={Environment.MachineName})");
        using var root = new DirectoryEntry("LDAP://" + cfg.PastaSalas);
        using var s = new DirectorySearcher(root, "(|" + string.Join("", parts) + ")", new[] { "distinguishedName" })
        {
            PageSize = 200,
        };
        foreach (SearchResult r in s.FindAll())
        {
            var dn = (string)r.Properties["distinguishedname"][0];
            var comps = System.Text.RegularExpressions.Regex.Split(dn, @",(?=(?:OU|CN|DC)=)");
            if (comps.Length < 2) continue;
            // escola = pasta logo abaixo da base (ex.: base "_Computadores MAC AEEQ" -> "EBVG");
            // se a base já for a pasta de uma escola (configuração antiga), é essa.
            var baseComps = System.Text.RegularExpressions.Regex.Split(cfg.PastaSalas, @",(?=(?:OU|CN|DC)=)");
            string Val(string c) => c.Substring(c.IndexOf('=') + 1);
            var baseFirst = Val(baseComps[0]);
            var idx = comps.Length - baseComps.Length - 1;
            var escola = !baseFirst.StartsWith("_") && baseComps[0].StartsWith("OU=", StringComparison.OrdinalIgnoreCase)
                ? baseFirst
                : idx >= 1 ? Val(comps[idx]) : "";
            var ous = comps.Where(c => c.StartsWith("OU=", StringComparison.OrdinalIgnoreCase)).Select(c => c.Substring(3)).ToList();
            if (!string.IsNullOrWhiteSpace(cfg.SoAbaixoDe) && !ous.Contains(cfg.SoAbaixoDe, StringComparer.OrdinalIgnoreCase))
                continue;
            return (escola, Val(comps[1]));
        }
        return null;
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && Compat.Hex(Compat.Sha256(File.ReadAllBytes(a))) == Compat.Hex(Compat.Sha256(File.ReadAllBytes(b)));
    }

    private static string Sc(string args) => Run("sc.exe", args);

    private static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return o;
    }

    private static void Write(string msg)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
        Log.Write("Instalador: " + msg);
    }
}
