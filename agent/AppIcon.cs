using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClassroomAgent;

/// <summary>Desenha o ícone do SalaViva (quatro ecrãs coloridos + "olho" central) em código, sem ficheiro .ico.</summary>
internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Get() => _cached ??= Render(64);

    public static Bitmap Bitmap(int size) => Draw(size);

    private static Icon Render(int size)
    {
        using var bmp = Draw(size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = s / 96f;
        RectangleF R(float x, float y, float w, float h) => new(x * u, y * u, w * u, h * u);

        void Tile(RectangleF r, Color a, Color b)
        {
            using var br = new LinearGradientBrush(r, a, b, 45f);
            using var path = Rounded(r, 8 * u);
            g.FillPath(br, path);
        }
        Tile(R(6, 8, 39, 30), Color.FromArgb(0xFF, 0x6B, 0x6B), Color.FromArgb(0xF2, 0x41, 0x6B));
        Tile(R(51, 8, 39, 30), Color.FromArgb(0xFF, 0xC9, 0x3C), Color.FromArgb(0xFF, 0x9F, 0x1C));
        Tile(R(6, 44, 39, 30), Color.FromArgb(0x2E, 0xE6, 0xC5), Color.FromArgb(0x00, 0xA6, 0xA6));
        Tile(R(51, 44, 39, 30), Color.FromArgb(0x5B, 0x8C, 0xFF), Color.FromArgb(0x6A, 0x3D, 0xFF));

        using var baseBr = new SolidBrush(Color.FromArgb(0x2B, 0x2F, 0x44));
        using var basePath = Rounded(R(30, 80, 36, 7), 3.5f * u);
        g.FillPath(baseBr, basePath);

        // olho central
        g.FillEllipse(Brushes.White, R(33, 26, 30, 30));
        using var pen = new Pen(Color.FromArgb(0x2B, 0x2F, 0x44), 3 * u);
        g.DrawEllipse(pen, R(33, 26, 30, 30));
        g.FillEllipse(baseBr, R(42, 35, 12, 12));
        g.FillEllipse(Brushes.White, R(46.5f, 38, 3.2f * 2, 3.2f * 2));
        return bmp;
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
