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
    private ChatForm? _chat;
    private readonly List<NoticeForm> _notices = new();
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

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mensagens ao professor", null, (_, _) => Chat().Pop());
        _tray = new NotifyIcon
        {
            Icon = AppIcon.Get(),
            Text = "SalaViva — podes falar com o professor",
            Visible = cfg.ShowTrayIcon,
            ContextMenuStrip = trayMenu,
        };
        _tray.DoubleClick += (_, _) => Chat().Pop();

        _user = CurrentUser();
        _client = new AgentClient(cfg, () => _user);
        _client.Command += n => _ui.BeginInvoke((Action)(() => OnCommand(n)));
        _client.ShareFrame += OnShareFrame;
        _client.ConnectionChanged += up => _ui.BeginInvoke((Action)(() =>
        {
            _disconnectedSince = up ? null : DateTime.UtcNow;
            _tray.Text = up ? "SalaViva — ligado ao professor" : "SalaViva — sem ligação ao servidor";
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

        // mantém avisos não-fecháveis por cima de tudo
        foreach (var nf in _notices) nf.Reassert();

        // mantém o bloqueio e a partilha por cima de tudo (e reativa após Ctrl+Alt+Del)
        if (_lock != null)
        {
            if (_lockUntil is { } until)
            {
                var left = until - DateTime.Now;
                if (left <= TimeSpan.Zero) { Unlock(); }
                else foreach (var f in _lock) f.SetCountdown(left);
            }
            if (_lock != null)
            {
                foreach (var f in _lock) f.Reassert();
                if (Program.RunningAsSystem) Native.BlockInput(true);
            }
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
                case "lock": Lock(n["text"]?.GetValue<string>(), ParseColor(n["color"]?.GetValue<string>()), n["seconds"]?.GetValue<int>() ?? 0); break;
                case "unlock": Unlock(); break;
                case "message": ShowMessage(n); break;
                case "chat": ReceiveChat(n); break;
                case "notice": ShowNotice(n); break;
                case "noticeOff": ClearNotices(); break;
                case "minimize": MinimizeAll(); break;
                case "mute": SetMute(n["on"]?.GetValue<bool>() ?? true); break;
                case "openUrl": OpenUrl(n["url"]?.GetValue<string>() ?? ""); break;
                case "broadcastStart": StartBroadcast(n["from"]?.GetValue<string>() ?? "professor"); break;
                case "broadcastStop": StopBroadcast(); break;
                case "stream": SetIndicator(n["on"]?.GetValue<bool>() ?? false); break;
                case "input": if (_lock == null) InputInjector.Handle(n); break;
                case "logoff": Logoff(); break;
                case "procs": SendProcesses(); break;
                case "kill": KillProcess(n["pid"]?.GetValue<int>() ?? 0); break;
                case "type": if (_lock == null) InputInjector.TypeText(n["text"]?.GetValue<string>() ?? ""); break;
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
    private Color? _lockColor;
    private DateTime? _lockUntil;
    private void Lock(string? text, Color? color, int seconds = 0)
    {
        _lockText = text;
        _lockColor = color;
        _lockUntil = seconds > 0 ? DateTime.Now.AddSeconds(seconds) : null;
        if (_lock != null)
        {
            foreach (var f in _lock) f.SetText(text);
            return;
        }
        _lock = Screen.AllScreens.Select(s => new LockForm(s, text, color)).ToList();
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
        _lockUntil = null;
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

    // ------------------------------------------------------------ programas abertos
    private void SendProcesses()
    {
        var list = new List<object>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != _session || p.Id == Compat.Pid) continue;
                var title = p.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new { pid = p.Id, name = p.ProcessName, title });
            }
            catch { }
            finally { p.Dispose(); }
        }
        _client.Post(new { type = "procs", list });
    }

    private void KillProcess(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            // só programas da sessão do aluno, nunca o próprio agente nem processos do sistema
            if (p.SessionId != _session || p.Id == Compat.Pid || p.ProcessName.Equals("ClassroomAgent", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) return;
            Log.Write($"A fechar {p.ProcessName} ({pid}) a pedido do professor");
            if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill();
        }
        catch (Exception ex) { Log.Write("Fechar programa: " + ex.Message); }
        SendProcesses();
    }

    // ------------------------------------------------------------ chat, avisos, extras
    private ChatForm Chat()
    {
        if (_chat == null || _chat.IsDisposed)
            _chat = new ChatForm("Professor", text => _client.Post(new { type = "chat", text }), () => _client.Post(new { type = "hand" }));
        return _chat;
    }

    private void ReceiveChat(JsonNode n)
    {
        var text = n["text"]?.GetValue<string>() ?? "";
        if (text.Length == 0) return;
        var c = Chat();
        c.Add(text, false);
        c.Pop();
    }

    private void ShowNotice(JsonNode n)
    {
        var id = n["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N").Substring(0, 6);
        var text = n["text"]?.GetValue<string>() ?? "";
        var color = ParseColor(n["color"]?.GetValue<string>()) ?? Color.FromArgb(79, 107, 255);
        var secs = n["seconds"]?.GetValue<int>() ?? 0;
        var closable = n["closable"]?.GetValue<bool>() ?? true;
        var endAction = n["endAction"]?.GetValue<string>() ?? (n["lockAtEnd"]?.GetValue<bool>() == true ? "lock" : "");
        var endSeconds = n["endSeconds"]?.GetValue<int>() ?? 0;
        _notices.RemoveAll(f => { if (f.Id == id) { f.Close(); return true; } return false; });
        var form = new NoticeForm(id, text, color, secs, closable);
        if (secs > 0 && endAction.Length > 0)
            form.Ended += _ => RunEndAction(endAction, text, color, endSeconds);
        form.FormClosed += (_, _) => _notices.Remove(form);
        _notices.Add(form);
        form.Show();
    }

    private void RunEndAction(string action, string? text, Color? color, int seconds)
    {
        switch (action)
        {
            case "lock": Lock(text, color, seconds); break;   // seconds>0 => bloqueio com contagem e desbloqueio automático
            case "logoff": Logoff(); break;
            case "message": ShowMessage(JsonNode.Parse($"{{\"from\":\"Professor\",\"title\":\"Aviso\",\"text\":{System.Text.Json.JsonSerializer.Serialize(text ?? "")},\"allowReply\":false}}")!); break;
        }
    }

    private void ClearNotices()
    {
        foreach (var f in _notices.ToList()) f.Close();
        _notices.Clear();
    }

    private void MinimizeAll()
    {
        try { InputInjector.MinimizeAll(); } catch (Exception ex) { Log.Write("Minimizar: " + ex.Message); }
    }

    private void SetMute(bool on)
    {
        try { AudioControl.SetMute(on); Log.Write(on ? "Som silenciado" : "Som reposto"); }
        catch (Exception ex) { Log.Write("Som: " + ex.Message); }
    }

    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        return null;
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
            ClearNotices();
            _chat?.Dispose();
            _tray.Dispose();
            _keys.Dispose();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
