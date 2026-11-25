using System.Drawing;
using static ClassroomAgent.Native;

namespace ClassroomAgent;

/// <summary>Janela sem moldura, sempre por cima, a cobrir um monitor inteiro.</summary>
internal class CoverForm : Form
{
    public CoverForm(Screen screen)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        KeyPreview = true;
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: não aparece no Alt+Tab
            return cp;
        }
    }

    public void Reassert()
    {
        if (IsDisposed) return;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 e afins: só fecha quando o próprio agente manda
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    public bool AllowClose { get; set; }
}

internal sealed class LockForm : CoverForm
{
    private readonly Label _text;

    public LockForm(Screen screen, string? text) : base(screen)
    {
        BackColor = Color.FromArgb(18, 24, 38);
        _text = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 32f, FontStyle.Bold),
        };
        Controls.Add(_text);
        SetText(text);
    }

    public void SetText(string? text) =>
        _text.Text = "🔒\n" + (string.IsNullOrWhiteSpace(text) ? "Atenção ao professor" : text);
}

internal sealed class BroadcastForm : CoverForm
{
    private readonly PictureBox _pic;

    public BroadcastForm(Screen screen, string from) : base(screen)
    {
        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        var bar = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = $"  Ecrã partilhado por {from}",
            ForeColor = Color.White,
            BackColor = Color.FromArgb(31, 111, 235),
            Font = new Font("Segoe UI", 10f),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Controls.Add(_pic);
        Controls.Add(bar);
    }

    public void ShowFrame(Image img)
    {
        var old = _pic.Image;
        _pic.Image = img;
        old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pic.Image?.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class MessageForm : Form
{
    public MessageForm(string from, string title, string text, bool allowReply, Action<string> onReply)
    {
        Text = string.IsNullOrWhiteSpace(title) ? "Mensagem do professor" : title;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 11f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, allowReply ? 360 : 260);
        Padding = new Padding(14);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = $"De: {from}", AutoSize = true, ForeColor = SystemColors.GrayText });
        layout.Controls.Add(new TextBox
        {
            Text = text.Replace("\n", Environment.NewLine),
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = false,
        });

        TextBox? reply = null;
        if (allowReply)
        {
            reply = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Escreve aqui a tua resposta (opcional)", MaxLength = 2000 };
            layout.Controls.Add(reply);
        }
        else layout.Controls.Add(new Label { AutoSize = true });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var ok = new Button { Text = allowReply ? "Enviar / Fechar" : "OK", AutoSize = true };
        ok.Click += (_, _) =>
        {
            var r = reply?.Text.Trim();
            if (!string.IsNullOrEmpty(r)) onReply(r);
            Close();
        };
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        AcceptButton = ok;
        Controls.Add(layout);

        Shown += (_, _) =>
        {
            System.Media.SystemSounds.Asterisk.Play();
            Activate();
            reply?.Focus();
        };
    }
}

/// <summary>Pequeno aviso no topo do ecrã: "O professor está a ver o teu ecrã".</summary>
internal sealed class ViewIndicator : Form
{
    public ViewIndicator()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(198, 40, 40);
        Opacity = 0.9;
        var l = new Label
        {
            Text = "● O professor está a ver o teu ecrã",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
        };
        Controls.Add(l);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Load += (_, _) =>
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top);
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x20; // TOOLWINDOW | NOACTIVATE | TRANSPARENT (cliques passam)
            return cp;
        }
    }
}
