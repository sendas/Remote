using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClassroomAgent;

/// <summary>
/// Aviso no ecrã do aluno, em quatro estilos:
///   "banner"     — faixa no topo (ligeira, não tapa nada quando o aluno pode fechar);
///   "corner"     — popup pequeno num canto (notificação com cor de fundo);
///   "center"     — janela ao centro do ecrã;
///   "fullscreen" — cobre o ecrã inteiro (impossível de ignorar).
/// Pode ter contagem decrescente e, no fim, disparar uma ação (ver AgentApp).
/// </summary>
internal sealed class NoticeForm : Form
{
    private readonly Label _label;
    private readonly Label _count;
    private readonly string _text;
    private DateTime? _until;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly bool _closable;
    private readonly string _style;
    private readonly string _corner;

    public string Id { get; }
    public event Action<NoticeForm>? Ended;

    public NoticeForm(string id, string text, Color color, int seconds, bool closable = true,
        string style = "banner", string corner = "br")
    {
        Id = id;
        _text = text;
        _closable = closable;
        _style = style;
        _corner = corner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = color;
        var fg = LockForm.Contrast(color);

        var big = style is "fullscreen" or "center";
        var pic = new PictureBox
        {
            Image = AppIcon.Bitmap(big ? 72 : 28),
            SizeMode = PictureBoxSizeMode.AutoSize,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.None,
            Dock = style == "banner" ? DockStyle.Left : DockStyle.Top,
            Padding = new Padding(10, big ? 24 : 6, 6, 6),
        };
        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = fg,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", big ? 30f : 12f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Padding = new Padding(12),
        };
        _count = new Label
        {
            Dock = DockStyle.Bottom,
            Height = big ? 150 : 0,
            ForeColor = fg,
            TextAlign = ContentAlignment.TopCenter,
            Font = new Font("Segoe UI", big ? 72f : 12f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Visible = false,
        };

        Controls.Add(_label);
        if (big) Controls.Add(_count);
        if (closable)
        {
            var close = new Button
            {
                Text = big ? "✕ Fechar" : "✕",
                Dock = big ? DockStyle.None : DockStyle.Right,
                Width = big ? 120 : 40,
                Height = big ? 40 : 0,
                FlatStyle = FlatStyle.Flat,
                ForeColor = fg,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            close.FlatAppearance.BorderSize = big ? 1 : 0;
            close.Click += (_, _) => Close();
            if (big) { close.Location = new Point(0, 12); Controls.Add(close); close.BringToFront(); }
            else Controls.Add(close);
        }
        Controls.Add(pic);

        if (seconds > 0) _until = DateTime.Now.AddSeconds(seconds);
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        Place();
        Load += (_, _) => { Place(); Update(); };
        if (style == "corner") RoundCorners();
        Tick();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000;                 // TOOLWINDOW | NOACTIVATE
            if (_closable && _style == "banner") cp.ExStyle |= 0x20; // TRANSPARENT só na faixa fechável
            return cp;
        }
    }

    public void Reassert()
    {
        if (IsDisposed) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
    }

    private void Place()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        switch (_style)
        {
            case "fullscreen":
                Bounds = Screen.PrimaryScreen!.Bounds;
                break;
            case "center":
                Width = Math.Min(620, wa.Width - 80);
                Height = Math.Min(_until != null ? 420 : 300, wa.Height - 80);
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
                break;
            case "corner":
                Width = 360;
                Height = 92;
                var x = _corner.Contains('l') ? wa.Left + 20 : wa.Right - Width - 20;
                var y = _corner.Contains('t') ? wa.Top + 20 : wa.Bottom - Height - 20;
                Location = new Point(x, y);
                break;
            default: // banner
                Width = Math.Min(760, wa.Width - 40);
                Height = 48;
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 12);
                break;
        }
    }

    private void RoundCorners()
    {
        using var path = new GraphicsPath();
        int r = 18;
        path.AddArc(0, 0, r, r, 180, 90);
        path.AddArc(Width - r, 0, r, r, 270, 90);
        path.AddArc(Width - r, Height - r, r, r, 0, 90);
        path.AddArc(0, Height - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    private void Tick()
    {
        if (_until is { } u)
        {
            var left = u - DateTime.Now;
            if (left <= TimeSpan.Zero) { Ended?.Invoke(this); Close(); return; }
            var clock = left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{left.Minutes}:{left.Seconds:00}";
            if (_count.Height > 0) { _label.Text = _text; _count.Visible = true; _count.Text = clock; }
            else _label.Text = $"{_text}   ⏳ {clock}";
        }
        else { _label.Text = _text; _count.Visible = false; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { _timer.Dispose(); base.OnFormClosed(e); }
}
