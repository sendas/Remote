using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;
using System.Windows.Forms;

namespace ClassroomAgent;

/// <summary>
/// Assistente de instalação manual: abre-se ao fazer duplo clique no ClassroomAgent.exe
/// (ex.: \\SERVIDOR\SalaDeAula$\leve\ClassroomAgent.exe). Instala neste PC na sala escolhida,
/// mesmo que o MAC não esteja no AD, e deixa escolher o nome que aparece na consola.
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly InstallConfig? _cfg;
    private readonly ComboBox _school = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 300 };
    private readonly ComboBox _room = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 300 };
    private readonly RadioButton _rUser = new() { Text = "Nome completo de quem inicia sessão (do domínio)", AutoSize = true, Checked = true };
    private readonly RadioButton _rLogin = new() { Text = "Nome de início de sessão (ex.: a12345)", AutoSize = true };
    private readonly RadioButton _rPc = new() { Text = "Nome do computador (" + Environment.MachineName + ")", AutoSize = true };
    private readonly RadioButton _rCustom = new() { Text = "Outro nome:", AutoSize = true };
    private readonly TextBox _custom = new() { Width = 220, Enabled = false };
    private readonly CheckBox _hide = new() { Text = "PC partilhado: só aparece na consola quando é um aluno a usá-lo\n(quando é um professor a usá-lo, fica escondido)", AutoSize = true };
    private readonly Button _go = new() { Text = "Instalar neste computador", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(460, 0) };

    public SetupForm()
    {
        Text = "SalaViva — instalar neste computador";
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        try { Icon = AppIcon.Get(); } catch { }

        _cfg = Installer.TryLoadConfig();

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        Controls.Add(flow);
        flow.Controls.Add(new Label { Text = "Computador: " + Environment.MachineName, AutoSize = true, Font = new Font(Font, FontStyle.Bold) });

        flow.Controls.Add(new Label { Text = "Escola", AutoSize = true, Margin = new Padding(0, 12, 0, 0) });
        flow.Controls.Add(_school);
        flow.Controls.Add(new Label { Text = "Sala (nome da pasta no AD ou outro nome)", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        flow.Controls.Add(_room);

        var names = new GroupBox { Text = "Nome que aparece na consola", AutoSize = true, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(8) };
        var nf = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var customRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        customRow.Controls.Add(_rCustom);
        customRow.Controls.Add(_custom);
        nf.Controls.AddRange(new Control[] { _rUser, _rLogin, _rPc, customRow });
        names.Controls.Add(nf);
        flow.Controls.Add(names);
        _rCustom.CheckedChanged += (_, _) => { _custom.Enabled = _rCustom.Checked; if (_rCustom.Checked) _custom.Focus(); };

        _hide.Margin = new Padding(0, 10, 0, 0);
        flow.Controls.Add(_hide);
        flow.Controls.Add(_go);
        _go.Margin = new Padding(0, 14, 0, 0);
        flow.Controls.Add(_status);
        AcceptButton = _go;
        _go.Click += (_, _) => Install();

        Fill();
    }

    private void Fill()
    {
        foreach (var s in new[] { "EBVG", "ESEQ", "EBPN" }) _school.Items.Add(s);
        if (_cfg == null)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "Não encontro o instalar.json. Abre este programa a partir da partilha do servidor:\n" +
                           @"\\SERVIDOR\SalaDeAula$\leve\ClassroomAgent.exe";
            _go.Enabled = false;
            return;
        }
        _school.TextChanged += (_, _) => FillRooms();

        // valores já instalados, ou a sala encontrada pelo MAC no AD
        AgentConfig? atual = null;
        try { if (File.Exists(AgentConfig.FilePath)) atual = AgentConfig.Load(); } catch { }
        if (atual != null && atual.Room.Length > 0)
        {
            _school.Text = atual.School;
            _room.Text = atual.Room;
            (atual.NameMode switch { "login" => _rLogin, "pc" => _rPc, "custom" => _rCustom, _ => _rUser }).Checked = true;
            _custom.Text = atual.CustomName;
            _hide.Checked = atual.HideForTeachers;
            _status.Text = "Este PC já tem o agente (sala " + atual.Room + "). Instalar outra vez atualiza-o.";
        }
        else if (Installer.SuggestRoom(_cfg) is { } sug)
        {
            _school.Text = sug.School;
            _room.Text = sug.Folder;
            _status.Text = "Sala encontrada pelo MAC deste PC no AD: " + sug.School + "/" + sug.Folder;
        }
        FillRooms();
    }

    private void FillRooms()
    {
        if (_cfg == null) return;
        var keep = _room.Text;
        _room.Items.Clear();
        var school = _school.Text.Trim();
        foreach (var s in _cfg.Salas.Where(x => !string.IsNullOrWhiteSpace(x) && x != "*"))
        {
            var i = s.IndexOf('/');
            if (i > 0 && !s.Substring(0, i).Equals(school, StringComparison.OrdinalIgnoreCase)) continue;
            _room.Items.Add(i > 0 ? s.Substring(i + 1) : s);
        }
        _room.Text = keep;
    }

    private void Install()
    {
        if (_room.Text.Trim().Length == 0) { MessageBox.Show(this, "Escolhe ou escreve a sala.", Text); return; }
        if (_rCustom.Checked && _custom.Text.Trim().Length == 0) { MessageBox.Show(this, "Escreve o nome a mostrar.", Text); return; }
        var opt = new ManualInstall
        {
            School = _school.Text.Trim(),
            Room = _room.Text.Trim(),
            NameMode = _rLogin.Checked ? "login" : _rPc.Checked ? "pc" : _rCustom.Checked ? "custom" : "user",
            CustomName = _rCustom.Checked ? _custom.Text.Trim() : "",
            HideForTeachers = _hide.Checked,
        };
        _go.Enabled = false;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "A instalar… (até 30 segundos)";
        var t = new Thread(() =>
        {
            var code = Installer.Run(manual: opt);
            BeginInvoke(new Action(() =>
            {
                _go.Enabled = true;
                _status.ForeColor = code == 0 ? Color.SeaGreen : Color.Firebrick;
                _status.Text = (code == 0 ? "✔ " : "✖ ") + Installer.LastMessage +
                               (code == 0 ? "\nO PC deve aparecer na consola dentro de 1 minuto." : "");
            }));
        }) { IsBackground = true };
        t.Start();
    }

    /// <summary>Abre o assistente, pedindo primeiro permissões de administrador se for preciso.</summary>
    public static int Run()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            try
            {
                Process.Start(new ProcessStartInfo(Compat.ExePath) { Verb = "runas", UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show("É preciso uma conta de administrador para instalar.", "SalaViva");
            }
            return 0;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
        return 0;
    }
}
