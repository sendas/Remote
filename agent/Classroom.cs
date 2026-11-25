using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace ClassroomAgent;

/// <summary>Funções de aula do lado do aluno: Internet, ficheiros recebidos, entregas, fechar programas.</summary>
internal static class Classroom
{
    private const string RuleName = "SalaViva - Sem Internet";

    /// <summary>C:\Users\Public\Documents\SalaViva — partilhado por todos os utilizadores do PC (os alunos podem escrever).</summary>
    public static string BaseDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "SalaViva");
    public static string ReceivedDir => Path.Combine(BaseDir, "Recebidos do professor");
    public static string HandInDir => Path.Combine(BaseDir, "Entregar ao professor");

    public static void EnsureFolders()
    {
        try
        {
            Directory.CreateDirectory(ReceivedDir);
            Directory.CreateDirectory(HandInDir);
            // garante que qualquer utilizador do PC consegue gravar na pasta de entregas
            Run("icacls.exe", $"\"{BaseDir}\" /grant *S-1-5-32-545:(OI)(CI)M");
        }
        catch (Exception ex) { Log.Write("Pastas SalaViva: " + ex.Message); }
    }

    // ------------------------------------------------------------------ Internet

    /// <summary>
    /// Corta/repõe a Internet com uma regra da firewall do Windows: bloqueia HTTP/HTTPS (e QUIC) para endereços
    /// públicos. Rede da escola (10.x, 172.16-31.x, 192.168.x) e o servidor SalaViva continuam a funcionar.
    /// </summary>
    public static void SetInternet(bool on)
    {
        Run("netsh.exe", $"advfirewall firewall delete rule name=\"{RuleName}\"");
        if (on) { Log.Write("Internet reposta"); return; }
        const string publicIps = "1.0.0.0-9.255.255.255,11.0.0.0-126.255.255.255,128.0.0.0-172.15.255.255," +
                                 "172.32.0.0-192.167.255.255,192.169.0.0-223.255.255.255";
        Run("netsh.exe", $"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block protocol=TCP remoteport=80,443,8080 remoteip={publicIps}");
        Run("netsh.exe", $"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block protocol=UDP remoteport=443 remoteip={publicIps}");
        Log.Write("Internet cortada");
    }

    // ------------------------------------------------------------------ fechar programas

    private static readonly HashSet<string> Keep = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ClassroomAgent", "dwm", "taskmgr", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchUI", "SearchApp", "TextInputHost", "LockApp", "ApplicationFrameHost", "SystemSettings",
    };

    /// <summary>Fecha todos os programas com janela na sessão do aluno (pede para fechar; força ao fim de 3 s).</summary>
    public static int CloseAll(uint session)
    {
        var n = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session || p.Id == Compat.Pid || Keep.Contains(p.ProcessName)) continue;
                if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill();
                n++;
            }
            catch { }
            finally { p.Dispose(); }
        }
        Log.Write($"Fechados {n} programas a pedido do professor");
        return n;
    }

    // ------------------------------------------------------------------ ficheiros do professor

    private sealed class Incoming
    {
        public string Path = "";
        public FileStream? Stream;
        public string Name = "";
    }

    private static readonly Dictionary<string, Incoming> Files = new();

    public static void FileStart(string id, string name)
    {
        EnsureFolders();
        var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = System.IO.Path.Combine(ReceivedDir, safe);
        // não apaga um ficheiro que o aluno já tenha: acrescenta (1), (2)...
        for (var i = 1; File.Exists(path); i++)
            path = System.IO.Path.Combine(ReceivedDir, $"{System.IO.Path.GetFileNameWithoutExtension(safe)} ({i}){System.IO.Path.GetExtension(safe)}");
        lock (Files) Files[id] = new Incoming { Path = path, Stream = File.Create(path), Name = safe };
    }

    public static void FileChunk(byte[] data)
    {
        var id = Encoding.ASCII.GetString(data, 0, 16);
        Incoming? f;
        lock (Files) Files.TryGetValue(id, out f);
        f?.Stream?.Write(data, 16, data.Length - 16);
    }

    /// <summary>Ligação caiu a meio de uma receção: fecha e apaga os ficheiros incompletos.</summary>
    public static void AbortIncoming()
    {
        List<Incoming> list;
        lock (Files) { list = Files.Values.ToList(); Files.Clear(); }
        foreach (var f in list)
        {
            try { f.Stream?.Dispose(); File.Delete(f.Path); } catch { }
        }
    }

    /// <summary>Fecha o ficheiro recebido e devolve o caminho (ou null).</summary>
    public static string? FileEnd(string id)
    {
        Incoming? f;
        lock (Files) { Files.TryGetValue(id, out f); Files.Remove(id); }
        if (f == null) return null;
        f.Stream?.Dispose();
        Log.Write("Ficheiro recebido: " + f.Path);
        return f.Path;
    }

    // ------------------------------------------------------------------ entregas

    /// <summary>Faz um zip da pasta "Entregar ao professor" e envia-o ao servidor, em pedaços.</summary>
    public static async Task Collect(AgentClient client, string batch)
    {
        string? zip = null;
        try
        {
            EnsureFolders();
            var files = Directory.GetFiles(HandInDir, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                await client.SendJson(new { type = "collectEnd", ok = false, error = "pasta de entregas vazia" });
                return;
            }
            zip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"salaviva-{Guid.NewGuid():N}.zip");
            ZipFile.CreateFromDirectory(HandInDir, zip);
            if (new FileInfo(zip).Length > 100L * 1024 * 1024)
            {
                await client.SendJson(new { type = "collectEnd", ok = false, error = "trabalho com mais de 100 MB" });
                return;
            }
            await client.SendJson(new { type = "collectStart", batch });
            var batchBytes = Encoding.ASCII.GetBytes(batch.PadRight(16).Substring(0, 16));
            using (var fs = File.OpenRead(zip))
            {
                var buf = new byte[256 * 1024];
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    var pkt = new byte[1 + 16 + read];
                    pkt[0] = 0x21;
                    Buffer.BlockCopy(batchBytes, 0, pkt, 1, 16);
                    Buffer.BlockCopy(buf, 0, pkt, 17, read);
                    await client.SendBinary(pkt);
                }
            }
            await client.SendJson(new { type = "collectEnd", ok = true });
            Log.Write($"Entrega enviada ({files.Length} ficheiros)");
        }
        catch (Exception ex)
        {
            Log.Write("Entrega: " + ex.Message);
            try { await client.SendJson(new { type = "collectEnd", ok = false, error = ex.Message }); } catch { }
        }
        finally
        {
            if (zip != null) try { File.Delete(zip); } catch { }
        }
    }

    // ------------------------------------------------------------------ utilitários

    public static void OpenFolder(string path, uint session)
    {
        try
        {
            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (Program.RunningAsSystem) Native.StartAsLoggedOnUser($"\"{explorer}\" \"{path}\"", session);
            else Process.Start(explorer, $"\"{path}\"");
        }
        catch (Exception ex) { Log.Write("Abrir pasta: " + ex.Message); }
    }

    private static void Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, exe), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(15000);
        }
        catch (Exception ex) { Log.Write($"{exe}: {ex.Message}"); }
    }
}

/// <summary>Pergunta rápida: janela ao centro com um botão por resposta.</summary>
internal sealed class PollForm : Form
{
    public string PollId { get; }

    private static readonly Color[] Colors =
    {
        Color.FromArgb(79, 107, 255), Color.FromArgb(18, 163, 114), Color.FromArgb(255, 173, 31), Color.FromArgb(229, 57, 79),
        Color.FromArgb(106, 61, 255), Color.FromArgb(0, 184, 163), Color.FromArgb(255, 90, 106), Color.FromArgb(43, 47, 68),
    };

    public PollForm(string id, string question, string[] options, Action<string> answer)
    {
        PollId = id;
        Text = "SalaViva — pergunta do professor";
        Icon = AppIcon.Get();
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ControlBox = false;
        Font = new Font("Segoe UI", 12f);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        ClientSize = new Size(560, 140 + 64 * ((options.Length + 1) / 2));

        var q = new Label
        {
            Text = question,
            Dock = DockStyle.Top,
            Height = 100,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            Padding = new Padding(16, 10, 16, 0),
        };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < options.Length; i++)
        {
            var opt = options[i];
            var b = new Button
            {
                Text = opt,
                Dock = DockStyle.Fill,
                Height = 52,
                Margin = new Padding(6),
                FlatStyle = FlatStyle.Flat,
                BackColor = Colors[i % Colors.Length],
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => { answer(opt); Close(); };
            grid.Controls.Add(b);
        }
        Controls.Add(grid);
        Controls.Add(q);
        Shown += (_, _) => { Activate(); System.Media.SystemSounds.Asterisk.Play(); };
    }
}
