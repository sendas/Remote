using System.Diagnostics;
using System.Drawing;
using System.Text.Json.Nodes;

namespace ClassroomAgent;

/// <summary>Aplicação que corre na sessão do aluno: ícone na barra, bloqueio, partilha, mensagens.</summary>
internal sealed class AgentApp : ApplicationContext
{
    private readonly AgentConfig _cfg;
    private readonly AgentClient _client;
    private readonly Control _ui;          // para passar trabalho para a thread da interface
    private readonly NotifyIcon _tray;
    private readonly KeyboardBlocker _keys = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly uint _session = Native.CurrentSessionId();

    private List<LockForm>? _lock;
    private string? _lockText;
    private List<BroadcastForm>? _broadcast;
    private ViewIndicator? _indicator;
    private DateTime? _disconnectedSince;
    private string _user = "";
    private int _tick;

    private byte[]? _pendingFrame;
    private int _frameScheduled;

    public AgentApp(AgentConfig cfg)
    {
        _cfg = cfg;
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Sala de Aula — este computador pode ser observado pelo professor",
            Visible = true,
        };

        _user = CurrentUser();
        _client = new AgentClient(cfg, () => _user);
        _client.Command += n => _ui.BeginInvoke((Action)(() => OnCommand(n)));
        _client.ShareFrame += OnShareFrame;
        _client.ConnectionChanged += up => _ui.BeginInvoke((Action)(() =>
        {
            _disconnectedSince = up ? null : DateTime.UtcNow;
            _tray.Text = up ? "Sala de Aula — ligado ao professor" : "Sala de Aula — sem ligação ao servidor";
            if (!up) SetIndicator(false);
        }));
        _client.Start();

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Log.Write($"Agente de sessão iniciado (sessão {_session}, SYSTEM={Program.RunningAsSystem}, sala '{cfg.Room}')");
    }

    private string CurrentUser() =>
        Program.RunningAsSystem ? Native.SessionUser(_session) : Environment.UserDomainName + "\\" + Environment.UserName;

    private void OnTick()
    {
        _tick++;

        // mantém o bloqueio e a partilha por cima de tudo (e reativa após Ctrl+Alt+Del)
        if (_lock != null)
        {
            foreach (var f in _lock) f.Reassert();
            if (Program.RunningAsSystem) Native.BlockInput(true);
        }
        else if (_broadcast != null)
        {
            foreach (var f in _broadcast) f.Reassert();
        }

        // failsafe: se o servidor desaparecer, não deixa a turma bloqueada para sempre
        if (_disconnectedSince is { } since && (_lock != null || _broadcast != null) &&
            DateTime.UtcNow - since > TimeSpan.FromMinutes(_client.FailsafeUnlockMinutes))
        {
            Log.Write("Failsafe: sem servidor há demasiado tempo, a desbloquear");
            Unlock();
            StopBroadcast();
        }

        // muda o utilizador (login/logout) -> avisa o servidor
        if (_tick % 10 == 0)
        {
            var u = CurrentUser();
            if (u != _user)
            {
                _user = u;
                _client.Post(new { type = "status", user = u });
            }
        }
    }

    private void OnCommand(JsonNode n)
    {
        try
        {
            switch (n["type"]?.GetValue<string>())
            {
                case "lock": Lock(n["text"]?.GetValue<string>()); break;
                case "unlock": Unlock(); break;
                case "message": ShowMessage(n); break;
                case "openUrl": OpenUrl(n["url"]?.GetValue<string>() ?? ""); break;
                case "broadcastStart": StartBroadcast(n["from"]?.GetValue<string>() ?? "professor"); break;
                case "broadcastStop": StopBroadcast(); break;
                case "stream": SetIndicator(n["on"]?.GetValue<bool>() ?? false); break;
                case "input": if (_lock == null) InputInjector.Handle(n); break;
                case "logoff": Logoff(); break;
                case "reboot": Run("shutdown.exe", "/r /t 10 /c \"O professor vai reiniciar este computador.\""); break;
                case "shutdown": Run("shutdown.exe", "/s /t 10 /c \"O professor vai desligar este computador.\""); break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Comando {n["type"]}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ bloqueio
    private void Lock(string? text)
    {
        _lockText = text;
        if (_lock != null)
        {
            foreach (var f in _lock) f.SetText(text);
            return;
        }
        _lock = Screen.AllScreens.Select(s => new LockForm(s, text)).ToList();
        foreach (var f in _lock) f.Show();
        _keys.Enable();
        if (Program.RunningAsSystem) Native.BlockInput(true);
        Log.Write("Bloqueado");
    }

    private void Unlock()
    {
        if (_lock == null) return;
        if (Program.RunningAsSystem) Native.BlockInput(false);
        foreach (var f in _lock) { f.AllowClose = true; f.Close(); f.Dispose(); }
        _lock = null;
        if (_broadcast == null) _keys.Disable();
        Log.Write("Desbloqueado");
    }

    // ------------------------------------------------------------ partilha do ecrã do professor
    private void StartBroadcast(string from)
    {
        StopBroadcast();
        _broadcast = Screen.AllScreens.Select(s => new BroadcastForm(s, from)).ToList();
        foreach (var f in _broadcast) f.Show();
        _lock?.ForEach(f => f.Reassert()); // se estiver bloqueado, o bloqueio continua por cima
        _keys.Enable();
    }

    private void StopBroadcast()
    {
        if (_broadcast == null) return;
        foreach (var f in _broadcast) { f.AllowClose = true; f.Close(); f.Dispose(); }
        _broadcast = null;
        if (_lock == null) _keys.Disable();
    }

    private void OnShareFrame(byte[] jpeg)
    {
        // corre na thread de rede: guarda só a imagem mais recente e agenda um único desenho
        Volatile.Write(ref _pendingFrame, jpeg);
        if (Interlocked.Exchange(ref _frameScheduled, 1) == 1) return;
        _ui.BeginInvoke((Action)(() =>
        {
            Interlocked.Exchange(ref _frameScheduled, 0);
            var data = Interlocked.Exchange(ref _pendingFrame, null);
            if (data == null || _broadcast == null) return;
            try
            {
                using var ms = new MemoryStream(data);
                using var img = Image.FromStream(ms);
                foreach (var f in _broadcast) f.ShowFrame(new Bitmap(img));
            }
            catch { /* frame corrompido: ignora */ }
        }));
    }

    // ------------------------------------------------------------ mensagens
    private void ShowMessage(JsonNode n)
    {
        var id = n["id"]?.GetValue<string>() ?? "";
        var form = new MessageForm(
            n["from"]?.GetValue<string>() ?? "Professor",
            n["title"]?.GetValue<string>() ?? "",
            n["text"]?.GetValue<string>() ?? "",
            n["allowReply"]?.GetValue<bool>() ?? true,
            reply => _client.Post(new { type = "reply", msgId = id, text = reply }));
        form.FormClosed += (_, _) => form.Dispose();
        form.Show();
    }

    private void SetIndicator(bool on)
    {
        if (!_cfg.ShowViewIndicator) return;
        if (on && _indicator == null)
        {
            _indicator = new ViewIndicator();
            _indicator.Show();
        }
        else if (!on && _indicator != null)
        {
            _indicator.Close();
            _indicator.Dispose();
            _indicator = null;
        }
    }

    // ------------------------------------------------------------ ações de sistema
    private void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
        if (Program.RunningAsSystem)
        {
            // abre no browser do aluno (como o aluno, não como SYSTEM)
            var rundll = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
            Native.StartAsLoggedOnUser($"\"{rundll}\" url.dll,FileProtocolHandler {u.AbsoluteUri}", _session);
        }
        else
        {
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private void Logoff()
    {
        if (Program.RunningAsSystem) Native.WTSLogoffSession(IntPtr.Zero, _session, false);
        else Native.ExitWindowsEx(0, 0);
    }

    private static void Run(string exe, string args) =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Unlock();
            StopBroadcast();
            _tray.Dispose();
            _keys.Dispose();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
