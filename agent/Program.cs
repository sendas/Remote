using System.ServiceProcess;

namespace ClassroomAgent;

internal static class Program
{
    /// <summary>True quando corre como SYSTEM lançado pelo serviço (modo normal nos PCs dos alunos).</summary>
    public static bool RunningAsSystem { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "";

        if (mode == "--instalar")
            return Installer.Run();
        if (mode == "--atualizar")
            return Installer.Run(update: true);

        if (mode == "--session" || mode == "--console")
        {
            RunningAsSystem = mode == "--session";
            using var single = new Mutex(true, @"Local\ClassroomAgentSession", out var first);
            if (!first) return 0;

            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Erro fatal: " + e.ExceptionObject);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Log.Write("Erro na interface: " + e.Exception);
#if NET
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AgentApp(AgentConfig.Load()));
            return 0;
        }

        if (!Environment.UserInteractive)
        {
            ServiceBase.Run(new AgentService());
            return 0;
        }

        // duplo clique no programa: assistente de instalação neste PC
        return SetupForm.Run();
    }
}
