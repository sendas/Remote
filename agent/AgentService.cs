using System.Diagnostics;
using System.ServiceProcess;

namespace ClassroomAgent;

/// <summary>
/// Serviço Windows (corre como LocalSystem na sessão 0). Não tem acesso ao ecrã;
/// a sua única função é manter vivo um processo "--session" dentro da sessão de quem está ao PC.
/// Se o processo morrer ou o utilizador mudar, volta a lançá-lo em ~2 segundos.
/// </summary>
public sealed class AgentService : ServiceBase
{
    private Thread? _thread;
    private volatile bool _stopping;
    private Process? _child;
    private uint _childSession = Native.INVALID_SESSION;

    public AgentService()
    {
        ServiceName = "ClassroomAgent";
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        Log.Write("Serviço a arrancar");
        _thread = new Thread(Loop) { IsBackground = true, Name = "watchdog" };
        _thread.Start();
    }

    protected override void OnStop() => Shutdown();
    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _stopping = true;
        KillChild();
        // nunca deixar um PC sem Internet por o agente ter parado (desinstalação, atualização)
        try { Classroom.SetInternet(true); } catch { }
        Log.Write("Serviço parado");
    }

    private void Loop()
    {
        var exe = Compat.ExePath;
        while (!_stopping)
        {
            try
            {
                var session = Native.WTSGetActiveConsoleSessionId();
                if (session != Native.INVALID_SESSION && session != 0)
                {
                    var alive = _child is { HasExited: false };
                    if (!alive || _childSession != session)
                    {
                        KillChild();
                        var pid = Native.StartAsSystemInSession($"\"{exe}\" --session", session);
                        _child = Process.GetProcessById(pid);
                        _childSession = session;
                        Log.Write($"Processo de sessão lançado (pid {pid}, sessão {session})");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Watchdog: " + ex.Message);
            }
            for (var i = 0; i < 20 && !_stopping; i++) Thread.Sleep(100);
        }
    }

    private void KillChild()
    {
        try
        {
            if (_child is { HasExited: false }) _child.Kill();
        }
        catch { }
        _child = null;
        _childSession = Native.INVALID_SESSION;
    }
}
