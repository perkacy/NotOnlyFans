using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace NotOnlyFans;

/// <summary>
/// Own entry point (DISABLE_XAML_GENERATED_MAIN) for a single instance: NotOnlyFans lives in the tray,
/// so starting it again (Start menu, a shortcut) must open the window that is already running,
/// not a second copy.
/// </summary>
public static class Program
{
    private const string InstanceKey = "NotOnlyFans.Main";

    /// <summary>Raised (on a background thread) when another start was redirected to this running copy.</summary>
    public static event Action<AppActivationArguments>? Reactivated;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectedToRunningCopy())
        {
            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    private static bool RedirectedToRunningCopy()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => Reactivated?.Invoke(e);
            return false;
        }

        // Let the running copy bring its window to the front (we were started by the user, so we may pass that on).
        AllowSetForegroundWindow((int)main.ProcessId);
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Wait for the redirect without blocking COM on this STA thread.
        using var done = new ManualResetEvent(false);
        Task.Run(() =>
        {
            try
            {
                main.RedirectActivationToAsync(args).AsTask().Wait();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            done.Set();
        });
        CoWaitForMultipleObjects(0, 5000, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
}
