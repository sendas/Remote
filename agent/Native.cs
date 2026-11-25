using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClassroomAgent;

internal static class Native
{
    // ------------------------------------------------------------ sessões / tokens
    public const uint INVALID_SESSION = 0xFFFFFFFF;

    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int len);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr server, uint sessionId, bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass,
        out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr env);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUser(IntPtr token, string? app, string cmdLine, IntPtr pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2, TokenPrimary = 1, TokenSessionId = 12;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;

    /// <summary>
    /// Lança um processo com o token do SYSTEM, mas dentro da sessão interativa do aluno
    /// (assim consegue capturar o ecrã e mostrar janelas, e o aluno não o consegue terminar).
    /// </summary>
    public static int StartAsSystemInSession(string cmdLine, uint sessionId)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out var own)) throw new Win32Exception();
        try
        {
            if (!DuplicateTokenEx(own, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var dup))
                throw new Win32Exception();
            try
            {
                if (!SetTokenInformation(dup, TokenSessionId, ref sessionId, sizeof(uint))) throw new Win32Exception();
                return Launch(dup, cmdLine, IntPtr.Zero, CREATE_NO_WINDOW);
            }
            finally { CloseHandle(dup); }
        }
        finally { CloseHandle(own); }
    }

    /// <summary>Lança um processo como o aluno que tem sessão iniciada (ex.: abrir o browser dele).</summary>
    public static void StartAsLoggedOnUser(string cmdLine, uint sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken)) throw new Win32Exception();
        try
        {
            CreateEnvironmentBlock(out var env, userToken, false);
            try { Launch(userToken, cmdLine, env, CREATE_UNICODE_ENVIRONMENT); }
            finally { if (env != IntPtr.Zero) DestroyEnvironmentBlock(env); }
        }
        finally { CloseHandle(userToken); }
    }

    private static int Launch(IntPtr token, string cmdLine, IntPtr env, uint flags)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
        if (!CreateProcessAsUser(token, null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, flags, env, null,
                ref si, out var pi))
            throw new Win32Exception();
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }

    public static uint CurrentSessionId()
    {
        ProcessIdToSessionId(GetCurrentProcessId(), out var s);
        return s;
    }

    /// <summary>Utilizador com sessão iniciada nesta sessão ("DOMINIO\aluno"), ou "" no ecrã de login.</summary>
    public static string SessionUser(uint sessionId)
    {
        string Q(int cls)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, cls, out var p, out _)) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; } finally { WTSFreeMemory(p); }
        }
        var user = Q(5);   // WTSUserName
        if (user.Length == 0) return "";
        var dom = Q(7);    // WTSDomainName
        return dom.Length > 0 ? dom + "\\" + user : user;
    }

    // ------------------------------------------------------------ input
    [DllImport("user32.dll", SetLastError = true)] public static extern bool BlockInput(bool block);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public int mouseData;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);

    // ------------------------------------------------------------ hook de teclado (bloqueia Win, Alt+Tab…)
    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    // ------------------------------------------------------------ janelas
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_SHOWWINDOW = 0x40, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] public static extern bool ExitWindowsEx(uint flags, uint reason);
}
