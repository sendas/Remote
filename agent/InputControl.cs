using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static ClassroomAgent.Native;

namespace ClassroomAgent;

/// <summary>Controlo remoto: injeta rato e teclado vindos da consola do professor.</summary>
internal static class InputInjector
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, RDOWN = 0x8, RUP = 0x10, MDOWN = 0x20, MUP = 0x40,
        WHEEL = 0x800, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
    private const uint KEYUP = 0x2, EXTENDED = 0x1;

    private static readonly HashSet<ushort> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, // PgUp..Down, Insert, Delete
        0xA3, 0xA5, 0x6F, 0x90, 0x5B, 0x5C, 0x5D, 0x2C,               // RCtrl, RAlt, Num/, NumLock, Win, Menu, PrtSc
    };

    public static void Handle(JsonNode n)
    {
        var ev = n["ev"]?.GetValue<string>();
        switch (ev)
        {
            case "move":
                Mouse(n, MOVE);
                break;
            case "down":
            case "up":
                var b = n["button"]?.GetValue<int>() ?? 0;
                var down = ev == "down";
                var flag = b switch
                {
                    1 => down ? MDOWN : MUP,
                    2 => down ? RDOWN : RUP,
                    _ => down ? LDOWN : LUP,
                };
                Mouse(n, MOVE | flag);
                break;
            case "wheel":
                var d = CompatMath.Clamp(n["delta"]?.GetValue<int>() ?? 0, -1200, 1200);
                Mouse(n, MOVE, 0);
                Send(new INPUT { type = INPUT_MOUSE, U = { mi = { dwFlags = WHEEL, mouseData = d } } });
                break;
            case "key":
                var vk = (ushort)CompatMath.Clamp(n["vk"]?.GetValue<int>() ?? 0, 0, 255);
                if (vk == 0) return;
                var flags = (n["down"]?.GetValue<bool>() ?? true) ? 0u : KEYUP;
                if (ExtendedKeys.Contains(vk)) flags |= EXTENDED;
                Send(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = { ki = { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags } },
                });
                break;
        }
    }

    private static void Mouse(JsonNode n, uint flags, int data = 0)
    {
        // coordenadas normalizadas (0..1) sobre o ecrã virtual -> 0..65535 do Windows
        var x = CompatMath.Clamp(n["x"]?.GetValue<double>() ?? 0, 0, 1);
        var y = CompatMath.Clamp(n["y"]?.GetValue<double>() ?? 0, 0, 1);
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U =
            {
                mi =
                {
                    dx = (int)(x * 65535), dy = (int)(y * 65535),
                    mouseData = data, dwFlags = flags | ABSOLUTE | VIRTUALDESK,
                },
            },
        });
    }

    private static void Send(INPUT i) => SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
}

/// <summary>
/// Hook de teclado de baixo nível: enquanto ativo, engole Win, Alt+Tab, Alt+F4, Ctrl+Esc, etc.
/// (Ctrl+Alt+Del nunca é bloqueável pelo Windows — por isso o bloqueio reativa-se sozinho.)
/// </summary>
internal sealed class KeyboardBlocker : IDisposable
{
    private IntPtr _hook;
    private readonly LowLevelProc _proc; // manter referência para o GC não a recolher

    public KeyboardBlocker()
    {
        _proc = Hook;
    }

    public bool Active => _hook != IntPtr.Zero;

    public void Enable()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(null), 0);
    }

    public void Disable()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Hook(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            // KBDLLHOOKSTRUCT.flags bit 4 = LLKHF_INJECTED: deixa passar o que o professor envia
            var flags = Marshal.ReadInt32(l, 8);
            if ((flags & 0x10) == 0) return (IntPtr)1;
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    public void Dispose() => Disable();
}
