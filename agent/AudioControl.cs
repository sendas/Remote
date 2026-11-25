using System.Runtime.InteropServices;

namespace ClassroomAgent;

/// <summary>Silencia/repõe o som do PC do aluno. Usa a API de volume geral (funciona em Windows 7 a 11).</summary>
internal static class AudioControl
{
    [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr h, uint volume);
    [DllImport("winmm.dll")] private static extern int waveOutGetVolume(IntPtr h, out uint volume);

    private static uint _saved = 0xFFFFFFFF;

    public static void SetMute(bool on)
    {
        if (on)
        {
            waveOutGetVolume(IntPtr.Zero, out _saved);
            waveOutSetVolume(IntPtr.Zero, 0);
        }
        else
        {
            waveOutSetVolume(IntPtr.Zero, _saved == 0 ? 0xFFFFFFFF : _saved);
        }
    }
}
