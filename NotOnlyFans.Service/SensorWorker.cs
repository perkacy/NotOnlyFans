using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotOnlyFans.Contracts;
using NotOnlyFans.Service.Lights;
using NotOnlyFans.Service.Overclock;

namespace NotOnlyFans.Service;

/// <summary>
/// Live mode: read all sensors once per second, drive the fans the user put under control,
/// and stream everything to the window app.
/// Runs as the Windows service "NotOnlyFans" (or in a console during development).
/// </summary>
internal sealed class SensorWorker(ILogger<SensorWorker> log) : BackgroundService
{
    public const string ServiceName = "NotOnlyFans";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reading fails this many times in a row: the service restarts itself (Windows starts it again,
    /// see tools\Install-Service.ps1), which opens the hardware afresh. Fans are with the BIOS meanwhile.
    /// </summary>
    private const int FailuresBeforeRestart = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.UserInteractive)
        {
            Console.Title = "NotOnlyFans.Service";
        }
        if (!Hardware.IsElevated())
        {
            log.LogWarning("Not running as administrator - CPU and motherboard sensors will be missing.");
        }
        DataDirectory.TrySecure(log);

        SensorPoller? poller = null;
        FanController? controller = null;
        LightController? lights = null;
        OcController? oc = null;

        // Whatever happens, fans we drove go back to the BIOS / graphics card when this process ends.
        void ReleaseFans()
        {
            if (poller is null || controller is null)
            {
                return;
            }
            try
            {
                lock (poller.Sync)
                {
                    controller.ReleaseAll();
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Handing fans back to the BIOS failed.");
            }
        }
        UnhandledExceptionEventHandler onCrash = (_, _) => ReleaseFans();
        EventHandler onExit = (_, _) => ReleaseFans();
        AppDomain.CurrentDomain.UnhandledException += onCrash;
        AppDomain.CurrentDomain.ProcessExit += onExit;

        try
        {
            poller = new SensorPoller(log);
            controller = new FanController(poller, log);
            lights = new LightController(poller, log);
            oc = new OcController(log);
            var broadcaster = new PipeBroadcaster();
            var control = new ControlServer(poller, controller, lights, oc, log);
            broadcaster.ClientCountChanged += n => log.LogInformation("Window apps connected: {Count}", n);
            controller.StateChanged += state => broadcaster.PublishControl(ControlLine(state));
            lights.StateChanged += state => broadcaster.PublishColors(ColorsLine(state));
            oc.StateChanged += state => broadcaster.PublishOc(OcLine(state));

            lock (poller.Sync)
            {
                lights.Start();
                try
                {
                    oc.Start(DateTime.UtcNow);   // overclocking is optional: it must never stop fan control
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Starting overclocking failed.");
                }
            }

            // First read before accepting anyone, so the first client gets data immediately.
            var (catalog, values) = poller.Poll(() => SafeApply(controller, lights, oc));
            broadcaster.Publish(catalog, values);
            broadcaster.PublishControl(ControlLine(controller.State));
            broadcaster.PublishColors(ColorsLine(lights.State));
            broadcaster.PublishOc(OcLine(oc.State));
            log.LogInformation(
                "Reading {Sensors} sensors every {Interval} ms (one read takes {Read} ms). Fans under control: {Fans}. Safety limit {Limit:0} °C.",
                poller.SensorCount, Interval.TotalMilliseconds, (int)poller.LastPollDuration.TotalMilliseconds,
                controller.State.Fans.Count, controller.State.SafetyTemperature);

            var acceptTask = broadcaster.AcceptLoopAsync(log, stoppingToken);
            var controlTask = control.AcceptLoopAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            int failures = 0;
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        (catalog, values) = poller.Poll(() => SafeApply(controller, lights, oc));
                        broadcaster.Publish(catalog, values);
                        if (catalog is not null)
                        {
                            log.LogInformation("Hardware changed - now {Sensors} sensors.", poller.SensorCount);
                        }
                        if (failures > 0)
                        {
                            log.LogInformation("Reading the sensors works again.");
                            failures = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Without readings the safety rule can't work: the BIOS takes the fans until reading works again.
                        if (failures++ == 0)
                        {
                            log.LogError(ex, "Reading the sensors failed - fans are back with the BIOS until it works again.");
                        }
                        ReleaseFans();
                        if (failures >= FailuresBeforeRestart && !Environment.UserInteractive)
                        {
                            Restart($"Reading the sensors failed {failures} times in a row.");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            await IgnoreCancel(acceptTask);
            await IgnoreCancel(controlTask);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogCritical(ex, "The service failed.");
            ReleaseFans();
            if (!Environment.UserInteractive)
            {
                Restart("The service failed.");
            }
            throw;
        }
        finally
        {
            ReleaseFans();
            AppDomain.CurrentDomain.UnhandledException -= onCrash;
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            lights?.Dispose();
            poller?.Dispose();
            log.LogInformation("Stopped. All fans are back with the BIOS.");
        }
    }

    /// <summary>
    /// Ends the process abnormally so Windows restarts the service (its recovery settings);
    /// a normal stop would not be restarted. Fans are already back with the BIOS.
    /// </summary>
    private void Restart(string reason)
    {
        log.LogCritical("{Reason} Restarting the service.", reason);
        Environment.FailFast($"NotOnlyFans.Service: {reason} Windows restarts the service.");
    }

    // Parts whose last pass failed: an error that repeats every second is logged once, not 3600 times an hour.
    private readonly HashSet<string> _failing = [];

    /// <summary>A failing fan write must not stop sensor reading (or the safety rule for other fans).</summary>
    private void SafeApply(FanController controller, LightController lights, OcController oc)
    {
        Run("Applying fan settings", controller.Apply);
        Run("Updating lights", lights.Tick);
        Run("Checking the graphics card overclock", () => oc.Tick(DateTime.UtcNow));

        void Run(string what, Action action)
        {
            try
            {
                action();
                if (_failing.Remove(what))
                {
                    log.LogInformation("{What} works again.", what);
                }
            }
            catch (Exception ex)
            {
                if (_failing.Add(what))
                {
                    log.LogError(ex, "{What} failed (logged once until it works again).", what);
                }
            }
        }
    }

    private static byte[] ControlLine(ControlState state) =>
        SensorPoller.Line(new PipeMessage { Type = ControlPipe.ControlType, Control = state });

    private static byte[] ColorsLine(ColorState state) =>
        SensorPoller.Line(new PipeMessage { Type = ColorPipe.ColorsType, Colors = state });

    private static byte[] OcLine(OcState state) =>
        SensorPoller.Line(new PipeMessage { Type = OcPipe.OcType, Oc = state });

    private static async Task IgnoreCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
