using System.Diagnostics;
using System.Security.Cryptography;

namespace ClassroomAgent;

/// <summary>Pequenas funções que existem no .NET 8 mas não no .NET Framework (versão para Windows 7/8.1).</summary>
internal static class Compat
{
    private static readonly Random Rng = new();

    public static int RandomNext(int max)
    {
        lock (Rng) return Rng.Next(max);
    }

    public static string Hex(byte[] data) => BitConverter.ToString(data).Replace("-", "");

    public static byte[] Sha256(byte[] data)
    {
        using var h = SHA256.Create();
        return h.ComputeHash(data);
    }

    public static byte[] Sha1(byte[] data)
    {
        using var h = SHA1.Create();
        return h.ComputeHash(data);
    }

    public static string ExePath => Process.GetCurrentProcess().MainModule!.FileName!;

    public static int Pid => Process.GetCurrentProcess().Id;
}

internal static class CompatMath
{
    public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    /// <summary>Milissegundos monótonos (substitui Environment.TickCount64).</summary>
    public static long Ticks => Clock.ElapsedMilliseconds;
}
