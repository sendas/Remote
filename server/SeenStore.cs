using System.Text.Json;

namespace ClassroomServer;

/// <summary>
/// Lembra-se de cada PC que já se ligou, pelo endereço MAC: nome do PC, último utilizador e quando.
/// É isto que permite à consola mostrar, para cada sala do AD, que PCs já têm o agente e quais nunca apareceram.
/// Gravado em data/vistos.json.
/// </summary>
public sealed class SeenStore
{
    public sealed record Seen(string Pc, string User, string School, string Folder, DateTimeOffset LastSeen, string Version);

    private readonly object _gate = new();
    private readonly Dictionary<string, Seen> _byMac;
    private DateTime _lastSave = DateTime.MinValue;
    private static string FilePath => Path.Combine(ServerConfig.DataDir, "vistos.json");

    public SeenStore()
    {
        try
        {
            var d = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Seen>>(File.ReadAllText(FilePath)) : null;
            _byMac = new Dictionary<string, Seen>(d ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch { _byMac = new Dictionary<string, Seen>(StringComparer.OrdinalIgnoreCase); }
    }

    public static string NormalizeMac(string? mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 12 ? hex : "";
    }

    public void Touch(IEnumerable<string> macs, Seen info)
    {
        lock (_gate)
        {
            foreach (var m in macs.Select(NormalizeMac).Where(m => m.Length > 0)) _byMac[m] = info;
            // grava no máximo de minuto a minuto (há centenas de PCs a ligar de manhã)
            if (DateTime.UtcNow - _lastSave < TimeSpan.FromMinutes(1)) return;
            Save();
        }
    }

    public Seen? Get(string mac)
    {
        lock (_gate) return _byMac.TryGetValue(NormalizeMac(mac), out var s) ? s : null;
    }

    public void Flush() { lock (_gate) Save(); }

    private void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byMac));
            File.Move(tmp, FilePath, true);
            _lastSave = DateTime.UtcNow;
        }
        catch { }
    }
}
