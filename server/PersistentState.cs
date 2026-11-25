using System.Text.Json;

namespace ClassroomServer;

/// <summary>
/// Estado que tem de sobreviver a um reinício do servidor (ex.: atualização automática):
/// PCs bloqueados (com texto, cor e fim), PCs sem Internet e sessões abertas na consola.
/// Gravado em data/estado.json, de forma atómica, sempre que muda.
/// </summary>
public sealed class PersistentState
{
    public sealed record LockInfo(string? Text, string? Color, DateTimeOffset? Until)
    {
        public bool Expired => Until is { } u && u <= DateTimeOffset.UtcNow;
        // nunca 0 enquanto houver um fim: 0 quer dizer "até o professor desbloquear"
        public int SecondsLeft => Until is { } u ? Math.Max(1, (int)Math.Ceiling((u - DateTimeOffset.UtcNow).TotalSeconds)) : 0;
    }

    public sealed record SessionInfo(string Token, string Username, DateTimeOffset Expires);

    private sealed class Data
    {
        public Dictionary<string, LockInfo> Locks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> NoInternet { get; set; } = new();
        public List<SessionInfo> Sessions { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly Data _d;
    private static string FilePath => Path.Combine(ServerConfig.DataDir, "estado.json");

    public PersistentState()
    {
        try
        {
            _d = File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data() : new Data();
            _d.Locks = new Dictionary<string, LockInfo>(_d.Locks, StringComparer.OrdinalIgnoreCase);
        }
        catch { _d = new Data(); }
    }

    // ---------------------------------------------------------------- bloqueios
    public void SetLock(string id, LockInfo? info)
    {
        lock (_gate)
        {
            if (info == null) _d.Locks.Remove(id); else _d.Locks[id] = info;
            Save();
        }
    }

    public void SetLocks(IEnumerable<string> ids, LockInfo? info)
    {
        lock (_gate)
        {
            foreach (var id in ids)
                if (info == null) _d.Locks.Remove(id); else _d.Locks[id] = info;
            Save();
        }
    }

    public LockInfo? GetLock(string id)
    {
        lock (_gate)
        {
            if (!_d.Locks.TryGetValue(id, out var l)) return null;
            if (!l.Expired) return l;
            _d.Locks.Remove(id);
            Save();
            return null;
        }
    }

    /// <summary>Remove e devolve os bloqueios temporizados que já acabaram.</summary>
    public List<string> TakeExpiredLocks()
    {
        lock (_gate)
        {
            var ids = _d.Locks.Where(kv => kv.Value.Expired).Select(kv => kv.Key).ToList();
            foreach (var id in ids) _d.Locks.Remove(id);
            if (ids.Count > 0) Save();
            return ids;
        }
    }

    // ---------------------------------------------------------------- Internet
    public bool NoInternet(string id) { lock (_gate) return _d.NoInternet.Contains(id, StringComparer.OrdinalIgnoreCase); }

    public void SetNoInternet(string id, bool off)
    {
        lock (_gate)
        {
            _d.NoInternet.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (off) _d.NoInternet.Add(id);
            Save();
        }
    }

    // ---------------------------------------------------------------- sessões
    public List<SessionInfo> Sessions() { lock (_gate) return _d.Sessions.Where(s => s.Expires > DateTimeOffset.UtcNow).ToList(); }

    public void AddSession(SessionInfo s) { lock (_gate) { _d.Sessions.RemoveAll(x => x.Expires <= DateTimeOffset.UtcNow); _d.Sessions.Add(s); Save(); } }

    public void RemoveSessions(Func<SessionInfo, bool> match) { lock (_gate) { if (_d.Sessions.RemoveAll(x => match(x)) > 0) Save(); } }

    private void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_d));
            File.Move(tmp, FilePath, true);
        }
        catch { /* o estado é uma ajuda; nunca deve deitar abaixo o servidor */ }
    }
}
