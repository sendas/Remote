using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClassroomServer;

public sealed class School
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Regra de uma pasta do AD: nome a mostrar na consola (grupo/sala) e se o agente é instalado lá.</summary>
public sealed class RoomRule
{
    public string School { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; }
}

/// <summary>
/// Escolas e salas. A sala "verdadeira" de cada PC é a pasta do AD onde está a conta com o MAC dele
/// (o agente envia escola + pasta); aqui decide-se o nome com que aparece na consola. Vários nomes de
/// pasta podem dar o mesmo nome -> ficam no mesmo grupo. Mudar nomes não obriga a reinstalar nada.
/// </summary>
public sealed class RoomCatalog
{
    private readonly ServerConfig _cfg;
    private readonly ILogger<RoomCatalog> _log;
    private List<AdFolder>? _adCache;
    private DateTime _adCacheAt;

    public event Action? Changed;

    public sealed record AdFolder(string School, string Folder, int Count);
    /// <summary>Uma conta da árvore de salas: normalmente o "nome de início de sessão" é o MAC do PC.</summary>
    public sealed record AdAccount(string School, string Folder, string Name, string Mac);
    private List<AdAccount> _adAccounts = new();

    /// <summary>Contas (PCs) de uma pasta do AD.</summary>
    public List<AdAccount> AccountsOf(string school, string folder)
    {
        AdFolders(); // atualiza a cache se for preciso
        return _adAccounts.Where(a => a.School.Equals(school, StringComparison.OrdinalIgnoreCase) &&
                                      a.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<AdAccount> AllAccounts() { AdFolders(); return _adAccounts; }

    public RoomCatalog(ServerConfig cfg, ILogger<RoomCatalog> log)
    {
        _cfg = cfg;
        _log = log;
        if (_cfg.Schools.Count == 0)
        {
            _cfg.Schools.AddRange(new[]
            {
                new School { Id = "EBVG", Name = "EB Vasco da Gama" },
                new School { Id = "ESEQ", Name = "ES Eça de Queirós" },
                new School { Id = "EBPN", Name = "EB Parque das Nações" },
            });
            ImportFromInstallJson();
            _cfg.Save();
        }
    }

    public static string Key(string school, string room) => $"{school}/{room}";

    public string SchoolName(string id) =>
        _cfg.Schools.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;

    /// <summary>Nome a mostrar para uma pasta do AD (sem regra = o próprio nome da pasta).</summary>
    public string Resolve(string school, string folder)
    {
        lock (_cfg)
        {
            var r = _cfg.Rooms.FirstOrDefault(x => x.School.Equals(school, StringComparison.OrdinalIgnoreCase) &&
                                                   x.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(r?.Name) ? folder : r!.Name.Trim();
        }
    }

    public object Snapshot(Func<string, string, bool> canSee, IEnumerable<(string School, string Room)> live)
    {
        lock (_cfg)
        {
            var rooms = _cfg.Rooms.Select(r => (r.School, Room: string.IsNullOrWhiteSpace(r.Name) ? r.Folder : r.Name.Trim()))
                .Concat(live)
                .Where(x => x.Room.Length > 0 && canSee(x.School, x.Room))
                .Distinct()
                .ToList();
            return new
            {
                schools = _cfg.Schools.Select(s => new { id = s.Id, name = s.Name }).ToArray(),
                rooms = rooms.Select(x => new { school = x.School, name = x.Room, key = Key(x.School, x.Room) })
                    .OrderBy(x => x.school).ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToArray(),
            };
        }
    }

    // ------------------------------------------------------------------ administração

    public object AdminView()
    {
        var ad = AdFolders();
        lock (_cfg)
        {
            var rows = new List<object>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in ad)
            {
                keys.Add(Key(f.School, f.Folder));
                var r = _cfg.Rooms.FirstOrDefault(x => x.School.Equals(f.School, StringComparison.OrdinalIgnoreCase) &&
                                                       x.Folder.Equals(f.Folder, StringComparison.OrdinalIgnoreCase));
                rows.Add(new { school = f.School, folder = f.Folder, count = f.Count, name = r?.Name ?? "", active = r?.Active ?? false, inAd = true });
            }
            foreach (var r in _cfg.Rooms.Where(r => !keys.Contains(Key(r.School, r.Folder))))
                rows.Add(new { school = r.School, folder = r.Folder, count = 0, name = r.Name, active = r.Active, inAd = false });
            return new
            {
                adBase = _cfg.AdBase,
                adOk = ad.Count > 0,
                schools = _cfg.Schools.Select(s => new { id = s.Id, name = s.Name }).ToArray(),
                folders = rows,
            };
        }
    }

    public void Save(List<School> schools, List<RoomRule> rules)
    {
        lock (_cfg)
        {
            if (schools.Count > 0)
                _cfg.Schools = schools.Where(s => !string.IsNullOrWhiteSpace(s.Id))
                    .Select(s => new School { Id = s.Id.Trim().ToUpperInvariant(), Name = (s.Name ?? "").Trim() }).ToList();
            _cfg.Rooms = rules.Where(r => !string.IsNullOrWhiteSpace(r.School) && !string.IsNullOrWhiteSpace(r.Folder))
                .Where(r => r.Active || !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new RoomRule { School = r.School.Trim(), Folder = r.Folder.Trim(), Name = (r.Name ?? "").Trim(), Active = r.Active })
                .ToList();
            _cfg.Save();
        }
        WriteInstallJson();
        Changed?.Invoke();
    }

    /// <summary>Atualiza instalar.json na partilha: o instalador dos PCs só instala nas pastas ativas.</summary>
    public void WriteInstallJson()
    {
        try
        {
            var path = Path.Combine(_cfg.DeployDir, "instalar.json");
            if (!File.Exists(path)) return;
            var n = JsonNode.Parse(File.ReadAllText(path).TrimStart('﻿'))!.AsObject();
            List<string> active;
            lock (_cfg) active = _cfg.Rooms.Where(r => r.Active).Select(r => Key(r.School, r.Folder)).ToList();
            // lista vazia no instalador quer dizer "todas" -> usa um marcador que não corresponde a nada
            n["Salas"] = new JsonArray(active.Count > 0 ? active.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray() : new JsonNode[] { JsonValue.Create("(nenhuma)")! });
            n["PastaSalas"] = _cfg.AdBase;
            n["SoAbaixoDe"] = "";
            n.Remove("NomesSalas");
            File.WriteAllText(path, n.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Não consegui atualizar instalar.json: {Msg}", ex.Message);
        }
    }

    /// <summary>Primeira vez: aproveita as salas ativas/nomes que já estavam no instalar.json (só EBVG na altura).</summary>
    private void ImportFromInstallJson()
    {
        try
        {
            var path = Path.Combine(_cfg.DeployDir, "instalar.json");
            if (!File.Exists(path)) return;
            var n = JsonNode.Parse(File.ReadAllText(path).TrimStart('﻿'))!.AsObject();
            var pasta = n["PastaSalas"]?.ToString() ?? "";
            var school = Regex.Match(pasta, @"^OU=([^,]+)").Groups[1].Value;
            if (school.Length == 0 || school.StartsWith("_")) school = "EBVG";
            var salas = n["Salas"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList()
                      : new List<string> { n["Salas"]?.ToString() ?? "" };
            foreach (var s in salas.Where(s => s.Length > 0 && s != "*" && !s.Contains('/')))
                _cfg.Rooms.Add(new RoomRule { School = school, Folder = s, Active = true });
            if (n["NomesSalas"] is JsonObject nomes)
                foreach (var kv in nomes)
                {
                    var r = _cfg.Rooms.FirstOrDefault(x => x.Folder.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (r == null) _cfg.Rooms.Add(r = new RoomRule { School = school, Folder = kv.Key });
                    r.Name = kv.Value?.ToString() ?? "";
                }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Importar instalar.json: {Msg}", ex.Message);
        }
    }

    /// <summary>Pastas do AD com contas (MAC/PC), por escola. Só funciona no servidor Windows do domínio.</summary>
    public List<AdFolder> AdFolders()
    {
        if (_adCache != null && DateTime.UtcNow - _adCacheAt < TimeSpan.FromMinutes(5)) return _adCache;
        var list = new List<AdFolder>();
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(_cfg.AdBase))
        {
            try
            {
                var baseLen = SplitDn(_cfg.AdBase).Length;
                using var root = new System.DirectoryServices.DirectoryEntry("LDAP://" + _cfg.AdBase);
                using var s = new System.DirectoryServices.DirectorySearcher(root,
                    "(|(objectClass=user)(objectClass=computer))",
                    new[] { "distinguishedName", "name", "sAMAccountName", "userPrincipalName" }) { PageSize = 1000 };
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var accounts = new List<AdAccount>();
                foreach (System.DirectoryServices.SearchResult r in s.FindAll())
                {
                    var (school, folder) = SchoolAndFolder((string)r.Properties["distinguishedname"][0], baseLen);
                    if (school.Length == 0) continue;
                    var k = Key(school, folder);
                    counts[k] = counts.GetValueOrDefault(k) + 1;
                    string P(string name) => r.Properties[name].Count > 0 ? r.Properties[name][0]?.ToString() ?? "" : "";
                    var upn = P("userprincipalname");
                    var mac = SeenStore.NormalizeMac(P("samaccountname"));
                    if (mac.Length == 0 && upn.Contains('@')) mac = SeenStore.NormalizeMac(upn[..upn.IndexOf('@')]);
                    accounts.Add(new AdAccount(school, folder, P("name"), mac));
                }
                _adAccounts = accounts;
                list = counts.Select(kv =>
                {
                    var i = kv.Key.IndexOf('/');
                    return new AdFolder(kv.Key[..i], kv.Key[(i + 1)..], kv.Value);
                }).OrderBy(f => f.School).ThenBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                _log.LogWarning("Leitura do AD falhou: {Msg}", ex.Message);
            }
        }
        _adCache = list;
        _adCacheAt = DateTime.UtcNow;
        return list;
    }

    public static string[] SplitDn(string dn) => Regex.Split(dn, @",(?=(?:OU|CN|DC)=)", RegexOptions.IgnoreCase);

    /// <summary>"CN=pc,OU=Sala TIC1,OU=Salas TIC,OU=Alunos,OU=EBVG,&lt;base&gt;" -> ("EBVG", "Sala TIC1").</summary>
    public static (string School, string Folder) SchoolAndFolder(string dn, int baseLen)
    {
        var c = SplitDn(dn);
        var schoolIdx = c.Length - baseLen - 1;
        if (schoolIdx < 1) return ("", "");
        string Val(string x) => x[(x.IndexOf('=') + 1)..];
        return (Val(c[schoolIdx]), Val(c[1]));
    }
}
