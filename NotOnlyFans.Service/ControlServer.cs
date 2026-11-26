using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NotOnlyFans.Contracts;
using NotOnlyFans.Service.Lights;
using NotOnlyFans.Service.Overclock;

namespace NotOnlyFans.Service;

/// <summary>
/// Control pipe: the window app sends one JSON request per line and gets one JSON reply per line.
/// Only users signed in at this PC (interactive) may connect - not services or network logons.
/// </summary>
internal sealed class ControlServer(SensorPoller poller, FanController controller, LightController lights, OcController oc, ILogger log)
{
    /// <summary>The window app sends a few hundred bytes; anything this long is not from it.</summary>
    private const int MaxRequestBytes = 64 * 1024;

    /// <summary>The window app connects per request; a connection silent this long is dropped.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    public Task AcceptLoopAsync(CancellationToken ct) =>
        ServicePipe.AcceptLoopAsync(ControlPipe.Name, PipeDirection.InOut, WellKnownSidType.InteractiveSid,
            maxClients: 8, ServeAsync, log, ct);

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var line = new MemoryStream();
        while (true)
        {
            int read;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(IdleTimeout);
                read = await pipe.ReadAsync(buffer, idle.Token);
            }
            if (read == 0)
            {
                return;   // window app closed the connection
            }

            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }
                line.Write(buffer, start, i - start);
                start = i + 1;
                if (line.Length > MaxRequestBytes)
                {
                    log.LogWarning("Control pipe: a request longer than {Max} bytes - connection dropped.", MaxRequestBytes);
                    return;
                }
                var reply = Handle(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r'));
                line.SetLength(0);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, ContractsJson.Default.ControlReply);
                await pipe.WriteAsync(bytes, ct);
                await pipe.WriteAsync("\n"u8.ToArray(), ct);
                await pipe.FlushAsync(ct);
            }
            line.Write(buffer, start, read - start);
            if (line.Length > MaxRequestBytes)
            {
                log.LogWarning("Control pipe: a request longer than {Max} bytes - connection dropped.", MaxRequestBytes);
                return;
            }
        }
    }

    private ControlReply Handle(string line)
    {
        ControlRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, ContractsJson.Default.ControlRequest);
        }
        catch (JsonException)
        {
            return new ControlReply { Error = "Not a valid request." };
        }
        if (request is null || string.IsNullOrEmpty(request.Type) || request.Key is null)
        {
            return new ControlReply { Error = "Not a valid request." };
        }

        string? error = request.Type switch
        {
            ControlPipe.SetFanType when request.Fan is { } fan =>
                Locked(() => controller.SetFan(request.Key, fan), "The service could not change this fan."),
            ControlPipe.SetSafetyType when request.SafetyTemperature is { } limit =>
                Locked(() => controller.SetSafety(limit), "The service could not change the safety temperature."),
            ControlPipe.CalibrateType =>
                Locked(() => controller.StartCalibration(request.Key), "The service could not calibrate this fan."),
            ControlPipe.CancelCalibrationType =>
                Locked(() => { controller.CancelCalibration(request.Key); return null; }, "The service could not calibrate this fan."),
            ColorPipe.SetColorType when request.Light is { } light =>
                Locked(() => lights.SetLight(request.Key, light), "The service could not change this light."),
            ColorPipe.SetColorSyncType when request.Sync is { } sync =>
                Locked(() => lights.SetSync(sync, request.Light), "The service could not change the lights."),
            OcPipe.ApplyOcType when request.Oc is { } setting =>
                Locked(() => oc.Apply(request.Key, setting), "The service could not change the graphics card."),
            OcPipe.ResetOcType =>
                Locked(() => oc.Reset(request.Key), "The service could not reset the graphics card."),
            OcPipe.SetOcStartupType when request.Startup is { } startup =>
                Locked(() => oc.SetStartup(request.Key, startup), "The service could not change this setting."),
            _ => "Unknown request.",
        };
        return new ControlReply { Ok = error is null, Error = error };
    }

    /// <summary>Runs a request under the hardware lock; an error in it becomes a message, never a dead service.</summary>
    private string? Locked(Func<string?> action, string failure)
    {
        try
        {
            lock (poller.Sync)
            {
                return action();
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Failure}", failure);
            return failure;
        }
    }
}
