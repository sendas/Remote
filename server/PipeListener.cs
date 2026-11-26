using System.IO.Pipes;
using System.Net.WebSockets;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClassroomServer;

/// <summary>
/// Canal alternativo para os agentes: named pipe "\\SERVIDOR\pipe\SalaDeAula", que viaja por SMB (porta 445).
/// Num domínio, os PCs já chegam ao controlador de domínio pela 445 (é por aí que lêem as GPO/SYSVOL),
/// por isso funciona mesmo quando a firewall entre VLANs só deixa passar o tráfego do Active Directory.
/// O agente autentica-se no SMB com a conta de computador (corre como SYSTEM) e depois apresenta a
/// EnrollmentKey, tal como na ligação HTTPS. Por cima do pipe corre exatamente o mesmo protocolo WebSocket.
/// </summary>
public sealed class PipeListener : BackgroundService
{
    public const string PipeName = "SalaDeAula";

    private readonly Hub _hub;
    private readonly ILogger<PipeListener> _log;

    public PipeListener(Hub hub, ILogger<PipeListener> log)
    {
        _hub = hub;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Canal SMB para agentes: \\\\{Host}\\pipe\\{Pipe}", Environment.MachineName, PipeName);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = Create();
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogWarning("Pipe: {Msg}", ex.Message);
                await Task.Delay(2000, ct).ContinueWith(_ => { });
                continue;
            }
            _ = Task.Run(() => Serve(server), ct);
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        var who = "smb";
        try
        {
            if (OperatingSystem.IsWindows()) who = "smb:" + pipe.GetImpersonationUserName();
        }
        catch { }
        try
        {
            using var ws = WebSocket.CreateFromStream(new ChunkedStream(pipe), isServer: true, subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(20));
            await _hub.RunAgent(ws, who);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Pipe {Who}: {Msg}", who, ex.Message);
        }
        finally
        {
            try { pipe.Dispose(); } catch { }
        }
    }

    private static NamedPipeServerStream Create()
    {
        const int buf = 256 * 1024;
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, buf, buf);

        var sec = new PipeSecurity();
        // Utilizadores autenticados inclui as contas de computador do domínio (o agente corre como SYSTEM
        // e aparece na rede como DOMINIO\PC$). Sem a EnrollmentKey ninguém passa do "hello".
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, buf, buf, sec);
    }
}
