using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Services;

/// <summary>
/// Sends fan settings to NotOnlyFans.Service over the control pipe. The service applies them at once,
/// saves them, and publishes the new state on the sensor pipe (<see cref="SensorClient.Control"/>).
/// Requests are sent one at a time, newest wins (a slider can produce many in a second).
/// </summary>
public sealed class ControlClient
{
    private const string SafetySlot = "\u0001safety";   // can't clash with a sensor key
    private const string CalibrationSlot = "\u0001calibration";

    /// <summary>Starts calibrating a fan (the service reports progress in <see cref="ControlState.Calibration"/>).</summary>
    public Task<string?> CalibrateAsync(string controlKey) =>
        SendLatestAsync(CalibrationSlot, new ControlRequest { Type = ControlPipe.CalibrateType, Key = controlKey });

    public Task<string?> CancelCalibrationAsync(string controlKey) =>
        SendLatestAsync(CalibrationSlot, new ControlRequest { Type = ControlPipe.CancelCalibrationType, Key = controlKey });

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<string, ControlRequest> _pending = [];

    /// <summary>Sets a fan; returns null on success, else a message for the user.</summary>
    public Task<string?> SetFanAsync(string controlKey, FanSetting setting) =>
        SendLatestAsync(controlKey, new ControlRequest { Type = ControlPipe.SetFanType, Key = controlKey, Fan = setting });

    /// <summary>Sets the safety temperature (°C); returns null on success, else a message for the user.</summary>
    public Task<string?> SetSafetyAsync(float temperature) =>
        SendLatestAsync(SafetySlot, new ControlRequest { Type = ControlPipe.SetSafetyType, SafetyTemperature = temperature });

    private const string SyncSlot = "\u0001colorSync";
    private const string LightSlot = "\u0002";   // + light key: lights and fans never share a slot

    /// <summary>Sets one light (by light key); returns null on success, else a message for the user.</summary>
    public Task<string?> SetLightAsync(string lightKey, LightSetting setting) =>
        SendLatestAsync(LightSlot + lightKey, new ControlRequest { Type = ColorPipe.SetColorType, Key = lightKey, Light = setting });

    /// <summary>Sync all on / off; with a setting, it becomes the look of all lights.</summary>
    public Task<string?> SetColorSyncAsync(bool sync, LightSetting? all) =>
        SendLatestAsync(SyncSlot, new ControlRequest { Type = ColorPipe.SetColorSyncType, Sync = sync, Light = all });

    private const string AppLightsSlot = "\u0001appLights";

    /// <summary>The Windows Dynamic Lighting devices this app drives (see <see cref="DynamicLighting"/>).</summary>
    public Task<string?> SetAppLightsAsync(List<LightInfo> lights) =>
        SendLatestAsync(AppLightsSlot, new ControlRequest { Type = ColorPipe.SetAppLightsType, Lights = lights });

    private const string OcSlot = "\u0003";   // + card key: never shares a slot with fans or lights

    /// <summary>Applies an overclock to a card now (the service keeps it); null on success, else a message for the user.</summary>
    public Task<string?> ApplyOcAsync(string cardKey, OcSetting setting) =>
        SendLatestAsync(OcSlot + cardKey, new ControlRequest { Type = OcPipe.ApplyOcType, Key = cardKey, Oc = setting });

    /// <summary>Back to the card's defaults; the saved overclock is removed.</summary>
    public Task<string?> ResetOcAsync(string cardKey) =>
        SendLatestAsync(OcSlot + cardKey, new ControlRequest { Type = OcPipe.ResetOcType, Key = cardKey });

    public Task<string?> SetOcStartupAsync(string cardKey, bool on) =>
        SendLatestAsync(OcSlot + "startup" + cardKey, new ControlRequest { Type = OcPipe.SetOcStartupType, Key = cardKey, Startup = on });

    /// <summary>One request at a time; of several waiting for the same thing (slot) only the newest is sent.</summary>
    private async Task<string?> SendLatestAsync(string slot, ControlRequest request)
    {
        lock (_pending)
        {
            _pending[slot] = request;
        }

        await _one.WaitAsync();
        try
        {
            ControlRequest? latest;
            lock (_pending)
            {
                if (!_pending.Remove(slot, out latest))
                {
                    return null;   // a newer request already sent the latest value
                }
            }
            return await SendAsync(latest!);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The service answers within milliseconds; if it hasn't in this time, it is stuck or not ours.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

    private static async Task<string?> SendAsync(ControlRequest request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(AnswerTimeout);
            await using var pipe = new NamedPipeClientStream(".", ControlPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1000, timeout.Token).ConfigureAwait(false);
            PipeCheck.EnsureService(pipe);

            var line = JsonSerializer.Serialize(request, ContractsJson.Default.ControlRequest) + "\n";
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var reply = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } text
                ? JsonSerializer.Deserialize(text, ContractsJson.Default.ControlReply)
                : null;
            return reply is { Ok: true } ? null : reply?.Error ?? "The service did not answer.";
        }
        catch (OperationCanceledException)
        {
            return "The service did not answer.";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or JsonException)
        {
            return "Can't reach the NotOnlyFans service.";
        }
    }
}
