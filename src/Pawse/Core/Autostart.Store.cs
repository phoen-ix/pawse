using Windows.ApplicationModel;

namespace Pawse.Core;

/// <summary>
/// Store build only (see Pawse.csproj): start-at-sign-in is the package's StartupTask, declared
/// in packaging/msix/AppxManifest.xml. The Run key would not work here - a packaged app's HKCU
/// writes land in its private, virtualized hive, which sign-in never reads. Same surface as
/// Autostart.cs, which this build does not compile.
/// <para>Windows keeps the task's state, and the user can flip it outside Pawse too (Settings →
/// Apps → Startup, or Task Manager). One they switched off there can only be switched back on
/// there - <see cref="StartupTaskState.DisabledByUser"/> - so SetEnabled says so instead of
/// failing quietly.</para>
/// <para>The WinRT calls are async; Settings needs an answer synchronously on the UI thread.
/// They run on the thread pool and are waited for there, so no continuation needs the (STA) UI
/// thread that is blocked on them.</para>
/// </summary>
public static class Autostart
{
    /// <summary>Must match the StartupTask's TaskId in AppxManifest.xml.</summary>
    public const string TaskId = "PawseStartup";

    public static bool IsEnabled() =>
        State() is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    /// <summary>Nothing to repair: the task names the package, not a path that can move.</summary>
    public static string? Repair() => null;

    /// <summary>The task's state, or null when Windows would not say (no package identity -
    /// the exe started outside its package).</summary>
    public static StartupTaskState? State()
    {
        try { return Get().State; }
        catch (Exception ex)
        {
            Log.Error("autostart: reading the startup task", ex);
            return null;
        }
    }

    public static string? SetEnabled(bool on)
    {
        try
        {
            var task = Get();
            if (!on)
            {
                task.Disable();
                Log.Info($"autostart: startup task disabled (now {task.State})");
                return task.State == StartupTaskState.EnabledByPolicy
                    ? "Your organization has Pawse set to start when you sign in on this PC, so it can't be turned off here."
                    : null;
            }
            var state = Task.Run(async () => await task.RequestEnableAsync()).GetAwaiter().GetResult();
            Log.Info($"autostart: asked to enable the startup task - now {state}");
            return state switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
                StartupTaskState.DisabledByUser =>
                    "Windows has Pawse switched off as a startup app. Turn it on in Settings → Apps → Startup.",
                StartupTaskState.DisabledByPolicy =>
                    "Startup apps are managed by your organization on this PC, so Pawse can't turn this on.",
                _ => "Windows didn't turn on starting Pawse when you sign in.",
            };
        }
        catch (Exception ex)
        {
            Log.Error("autostart: changing the startup task", ex);
            return "Pawse couldn't change whether it starts when you sign in.";
        }
    }

    private static StartupTask Get() =>
        Task.Run(async () => await StartupTask.GetAsync(TaskId)).GetAwaiter().GetResult();
}
