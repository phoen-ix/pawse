using System.Diagnostics;

namespace Pawse.Core;

/// <summary>Hand a URL or a file to the shell's default handler (browser, editor). The shell
/// can refuse - no browser registered, a policy - and that is logged, never thrown: the one
/// place that does this, so the callers cannot drift in how they log or quote.</summary>
internal static class ShellOpen
{
    public static void Open(string target, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error($"open {what}", ex); }
    }
}
