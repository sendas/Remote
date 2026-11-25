using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace ClassroomAgent;

internal static class ScreenCapture
{
    private static readonly ImageCodecInfo Jpeg =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Captura todos os monitores (ecrã virtual), reduz para maxWidth e devolve JPEG.
    /// Devolve null se não for possível (ex.: ecrã seguro do UAC / Ctrl+Alt+Del).
    /// </summary>
    public static byte[]? Capture(int maxWidth, long quality, out byte[]? fingerprint)
    {
        fingerprint = null;
        try
        {
            var vs = SystemInformation.VirtualScreen;
            if (vs.Width <= 0 || vs.Height <= 0) return null;

            using var full = new Bitmap(vs.Width, vs.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);

            DrawCursor(full, vs);

            var scale = Math.Min(1.0, (double)maxWidth / vs.Width);
            var w = Math.Max(1, (int)(vs.Width * scale));
            var h = Math.Max(1, (int)(vs.Height * scale));

            using var small = scale < 1.0 ? new Bitmap(w, h, PixelFormat.Format24bppRgb) : null;
            if (small != null)
            {
                using var g = Graphics.FromImage(small);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                g.DrawImage(full, 0, 0, w, h);
            }
            var img = small ?? full;

            fingerprint = Fingerprint(img);

            using var ms = new MemoryStream();
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            img.Save(ms, Jpeg, p);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Hash rápido dos píxeis, para não reenviar miniaturas de ecrãs que não mudaram.</summary>
    private static byte[] Fingerprint(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            unsafe
            {
                var span = new ReadOnlySpan<byte>((void*)data.Scan0, data.Stride * data.Height);
                return SHA1.HashData(span);
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void DrawCursor(Bitmap bmp, Rectangle vs)
    {
        try
        {
            var p = Cursor.Position;
            using var g = Graphics.FromImage(bmp);
            Cursors.Default.Draw(g, new Rectangle(p.X - vs.Left, p.Y - vs.Top, 32, 32));
        }
        catch { }
    }
}
