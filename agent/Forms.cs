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
    private readonly PictureBox _logo;

    public LockForm(Screen screen, string? text, Color? color) : base(screen)
    {
        var bg = color ?? Color.FromArgb(18, 24, 38);
        BackColor = bg;
        _logo = new PictureBox
        {
            Image = AppIcon.Bitmap(96),
            SizeMode = PictureBoxSizeMode.AutoSize,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.None,
        };
        _text = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Contrast(bg),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 32f, FontStyle.Bold),
            BackColor = Color.Transparent,
        };
        Controls.Add(_text);
        Controls.Add(_logo);
        Shown += (_, _) => _logo.Location = new Point((ClientSize.Width - _logo.Width) / 2, ClientSize.Height / 2 - 150);
        SetText(text);
    }

    public void SetText(string? text) =>
        _text.Text = "\n\n" + (string.IsNullOrWhiteSpace(text) ? "Atenção ao professor" : text);

    internal static Color Contrast(Color c) =>
        (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 150 ? Color.FromArgb(20, 24, 38) : Color.White;
}

/// <summary>Faixa de aviso no topo do ecrã, com cor à escolha e contagem decrescente opcional.</summary>
internal sealed class NoticeForm : Form
{
    private readonly Label _label;
    private readonly string _text;
    private DateTime? _until;
    private readonly System.Windows.Forms.Timer _timer;

    public string Id { get; }

    public NoticeForm(string id, string text, Color color, int seconds)
    {
        Id = id;
        _text = text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = color;
        var fg = LockForm.Contrast(color);
        var pic = new PictureBox { Image = AppIcon.Bitmap(28), SizeMode = PictureBoxSizeMode.AutoSize, Dock = DockStyle.Left, BackColor = Color.Transparent, Padding = new Padding(10, 6, 6, 6) };
        _label = new Label { Dock = DockStyle.Fill, ForeColor = fg, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12f, FontStyle.Bold), BackColor = Color.Transparent };
        var close = new Button { Text = "✕", Dock = DockStyle.Right, Width = 40, FlatStyle = FlatStyle.Flat, ForeColor = fg, BackColor = Color.Transparent };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        Controls.Add(_label); Controls.Add(close); Controls.Add(pic);
        if (seconds > 0) _until = DateTime.Now.AddSeconds(seconds);
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        Place();
        Load += (_, _) => { Place(); Update(); };
        Tick();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; } }

    private void Place()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Width = Math.Min(760, wa.Width - 40);
        Height = 48;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 12);
    }

    private void Tick()
    {
        if (_until is { } u)
        {
            var left = u - DateTime.Now;
            if (left <= TimeSpan.Zero) { Close(); return; }
            _label.Text = $"{_text}   ⏳ {left.Minutes:0}:{left.Seconds:00}";
        }
        else _label.Text = _text;
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { _timer.Dispose(); base.OnFormClosed(e); }
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
            reply = new TextBox { Dock = DockStyle.Fill, MaxLength = 2000 };
#if NET
            reply.PlaceholderText = "Escreve aqui a tua resposta (opcional)";
#endif
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

/// <summary>Janela de chat com o professor: histórico, escrever, e botão "Levantar a mão".</summary>
internal sealed class ChatForm : Form
{
    private readonly FlowLayoutPanel _list;
    private readonly TextBox _input;
    private readonly Action<string> _onSend;
    private readonly Action _onHand;

    public ChatForm(string from, Action<string> onSend, Action onHand)
    {
        _onSend = onSend;
        _onHand = onHand;
        Text = "SalaViva — mensagens";
        Icon = AppIcon.Get();
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        ClientSize = new Size(360, 460);
        Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20);
        Font = new Font("Segoe UI", 10f);
        MinimizeBox = true;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(79, 107, 255) };
        top.Controls.Add(new Label { Text = "  " + from, Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 11f, FontStyle.Bold) });

        _list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 80, Padding = new Padding(8) };
        _input = new TextBox { Dock = DockStyle.Fill, Multiline = true };
        _input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); } };
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        var hand = new Button { Text = "✋ Levantar a mão", Dock = DockStyle.Left, Width = 150, FlatStyle = FlatStyle.Flat };
        hand.Click += (_, _) => { _onHand(); Add("✋ Levantaste a mão.", true, Color.FromArgb(255, 173, 31)); };
        var send = new Button { Text = "Enviar ▸", Dock = DockStyle.Right, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(79, 107, 255), ForeColor = Color.White };
        send.Click += (_, _) => Send();
        bar.Controls.Add(_input); bar.Controls.Add(send); bar.Controls.Add(hand);
        bottom.Controls.Add(_input); bottom.Controls.Add(bar);

        Controls.Add(_list); Controls.Add(bottom); Controls.Add(top);
    }

    private void Send()
    {
        var t = _input.Text.Trim();
        if (t.Length == 0) return;
        _input.Clear();
        _onSend(t);
        Add(t, true, Color.FromArgb(79, 107, 255));
    }

    public void Add(string text, bool mine, Color? color = null)
    {
        var bubble = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(280, 0),
            Padding = new Padding(10, 7, 10, 7),
            Margin = new Padding(mine ? 40 : 4, 3, mine ? 4 : 40, 3),
            BackColor = color ?? (mine ? Color.FromArgb(79, 107, 255) : Color.FromArgb(235, 238, 248)),
            ForeColor = (color ?? (mine ? Color.FromArgb(79, 107, 255) : Color.White)).GetBrightness() < .6f ? Color.White : Color.FromArgb(20, 24, 38),
        };
        _list.Controls.Add(bubble);
        _list.ScrollControlIntoView(bubble);
    }

    public void Pop()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        System.Media.SystemSounds.Asterisk.Play();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}
