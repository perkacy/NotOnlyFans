using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace NotOnlyFans.Service;

/// <summary>
/// What the sensor pipe and the control pipe share: who may connect, how many at once, and the accept loop.
///
///  - Users get only read/write, never "create instance": nobody else can add a pipe with our name while we run.
///    Our first instance is created with FirstPipeInstance, so if someone took the name before us we notice
///    (and the window app checks who owns the pipe, see the app's PipeCheck).
///  - Connections over the network are refused; only this PC.
///  - At most <c>maxClients</c> connections at once, so a misbehaving program can't use up the service.
/// </summary>
internal static class ServicePipe
{
    private static readonly TimeSpan RetryTaken = TimeSpan.FromSeconds(5);

    public static async Task AcceptLoopAsync(
        string name, PipeDirection direction, WellKnownSidType users, int maxClients,
        Func<NamedPipeServerStream, CancellationToken, Task> serve, ILogger log, CancellationToken ct)
    {
        // Not disposed: client tasks may still release a slot while the service stops.
        var slots = new SemaphoreSlim(maxClients, maxClients);
        bool takenLogged = false;

        while (!ct.IsCancellationRequested)
        {
            await slots.WaitAsync(ct);
            // Only this slot taken = no other instance of ours exists: then the name must be free.
            bool first = slots.CurrentCount == maxClients - 1;

            NamedPipeServerStream pipe;
            try
            {
                pipe = Create(name, direction, users, maxClients, first);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                slots.Release();
                if (!takenLogged)
                {
                    log.LogError(ex,
                        "The pipe {Pipe} is taken by another program (or another NotOnlyFans.Service). " +
                        "Trying again every {Seconds} s. Stop the Windows service \"NotOnlyFans\" before starting the service by hand.",
                        name, RetryTaken.TotalSeconds);
                    takenLogged = true;
                }
                await Task.Delay(RetryTaken, ct);
                continue;
            }
            if (takenLogged)
            {
                log.LogInformation("The pipe {Pipe} is ours now.", name);
                takenLogged = false;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync();
                slots.Release();
                if (ex is OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            _ = ServeAsync(pipe);
        }

        async Task ServeAsync(NamedPipeServerStream pipe)
        {
            try
            {
                await serve(pipe, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Window app closed or service stopping.
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Serving a client of {Pipe} failed.", name);
            }
            finally
            {
                await pipe.DisposeAsync();
                slots.Release();
            }
        }
    }

    private static NamedPipeServerStream Create(string name, PipeDirection direction, WellKnownSidType users, int maxClients, bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(users, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous;
        if (first)
        {
            options |= PipeOptions.FirstPipeInstance;   // fail if someone else already created a pipe with our name
        }
        return NamedPipeServerStreamAcl.Create(
            name, direction, maxClients, PipeTransmissionMode.Byte, options, inBufferSize: 0, outBufferSize: 0, security);
    }
}
