using System.Diagnostics;

namespace Pawse.Core;

/// <summary>
/// A cross-process "I am alive" channel, so a portable copy replacing its own exe can find
/// out whether the replacement actually runs before it throws away the only working one.
///
/// <para>Why it has to exist: <see cref="SelfReplace"/> used to treat
/// <c>Process.Start</c> returning as proof of a successful handover. It is not. A .NET
/// apphost whose framework is missing <em>starts perfectly well</em> - CreateProcess
/// succeeds - and only then fails inside hostfxr, where it puts up Windows' own "You must
/// install or update .NET to run this application" dialog and sits there. The predecessor
/// meanwhile reported Handover, shut down, and left the machine with a Pawse that cannot
/// start and a Run key still pointing at it. That is precisely what a runtime-major move
/// (net8 -> net10 for the framework-dependent build) does to a portable minimal copy.</para>
///
/// <para>Mechanism: a named manual-reset event in the same per-session <c>Local\</c>
/// namespace as the single-instance mutex and the quit channel, carrying a default DACL -
/// so only processes running as the same user in the same session can signal it. The
/// outgoing instance creates it before starting the successor (so a signal that arrives
/// immediately is not lost), and the successor sets it as soon as managed code runs.
/// Reaching managed code is the whole assertion: it means the apphost found a runtime,
/// hostfxr resolved the framework and the CLR came up.</para>
///
/// <para><b>Compatibility contract.</b> Every Pawse build from the one that introduced this
/// must call <see cref="Announce()"/> on the <c>--replace</c> path. A successor that does not
/// will be rolled back as though it had failed, because silence is indistinguishable from
/// the hostfxr dialog. Unlike the quit event and the mutex - which are shared with
/// packaging/pawse.nsi - this contract is between Pawse and its own future versions, so
/// nothing outside this repo can break it and nothing in the installer needs to know. The
/// name is pinned by a test.</para>
///
/// <para><b>What it does not do.</b> It cannot help a copy that is already out of date: the
/// decision to swap is made by the OLD build, so a version that predates this simply hands
/// over blind, as before. This protects the step from any build carrying it onwards - which
/// for the framework-dependent build means the next runtime major, not the net8 -> net10 one
/// that prompted it.</para>
/// </summary>
public static class StartupSignal
{
    /// <summary>Same suffix as the mutex and the quit event, for the same reason: one
    /// per-user, per-session namespace that a second Pawse can find.</summary>
    internal const string EventName = @"Local\Pawse-started-2b8f9c";

    /// <summary>How long the predecessor waits for that proof. Generous on purpose: the
    /// successor has to be created, and the self-contained build is a compressed single-file
    /// exe that unpacks ~66 MB to %TEMP% on the first run of a new version - through whatever
    /// the machine's antivirus does to it. A slow disk here must not be mistaken for a broken
    /// runtime, because the cost of getting that wrong is rolling back a perfectly good
    /// update. The success path does not pay this: the signal lands as soon as the CLR is
    /// up, usually in well under a second.</summary>
    internal static readonly TimeSpan ProofTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Poll granularity for the successor-died check. The event itself is waited on,
    /// not polled; this only bounds how long a process that exits outright goes unnoticed.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    /// <summary>Announce that this process reached managed code. A no-op when nobody is
    /// waiting, which is every start that is not a self-replace handover.</summary>
    public static void Announce() => Announce(EventName);

    /// <summary>Test seam - see <see cref="QuitSignal.Listen(Action, string)"/> for why the
    /// production name is never used from tests.</summary>
    internal static void Announce(string eventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var handle))
                return;                     // nobody replaced us; nothing to tell
            using (handle)
            {
                handle.Set();
                Log.Info("startup: announced to the instance that replaced itself with us");
            }
        }
        catch (Exception ex)
        {
            // Never fatal. Worst case the predecessor times out and rolls back a working
            // update - annoying, recoverable, and far better than the alternative.
            Log.Error("startup announce", ex);
        }
    }

    /// <summary>
    /// Start listening for that announcement. Call this <em>before</em> starting the
    /// successor: creating the event first is what stops a fast successor from signalling
    /// into the void. Returns null if the channel could not be opened at all, which the
    /// caller must treat as "cannot prove liveness" rather than as success.
    /// </summary>
    public static IStartupProof? Expect() => Expect(EventName);

    /// <summary>Test seam - see <see cref="Announce(string)"/>.</summary>
    internal static IStartupProof? Expect(string eventName)
    {
        try
        {
            // Manual-reset: the signal has to stay set. An auto-reset event would be consumed
            // by whichever wait got there first, and this one is read once but may be set
            // before the wait even begins.
            return new Proof(new EventWaitHandle(false, EventResetMode.ManualReset, eventName));
        }
        catch (Exception ex)
        {
            Log.Error("startup proof channel", ex);
            return null;
        }
    }

    /// <summary>Test seam: the same wait, over a handle the caller owns. The timing rules -
    /// an early signal surviving, a dead successor cutting the wait short, a live but silent
    /// one not - are independent of the name, and a named event is session-global (and
    /// Windows-only), so they are exercised through here instead.</summary>
    internal static IStartupProof Watching(EventWaitHandle handle) => new Proof(handle);

    private sealed class Proof : IStartupProof
    {
        private readonly EventWaitHandle _handle;

        internal Proof(EventWaitHandle handle) => _handle = handle;

        public bool Wait(TimeSpan timeout, Process? successor)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                if (_handle.WaitOne(left < Tick ? left : Tick)) return true;

                // Died outright - a corrupt exe, a missing dependency it could not report.
                // No point waiting out the rest of the timeout for a process that is gone.
                // The hostfxr case does NOT come through here: that one stays alive holding a
                // modal dialog, and only the timeout catches it.
                try
                {
                    if (successor is { HasExited: true })
                    {
                        Log.Error("update: the replacement exited without reaching managed code");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // Can't read the process any more - fall back to waiting out the clock
                    // rather than calling a possibly-fine update dead.
                    Log.Warn("update: could not check on the replacement - " + ex.Message);
                    successor = null;
                }
            }
            Log.Error($"update: the replacement did not report started within {timeout.TotalSeconds:0}s");
            return false;
        }

        public void Dispose()
        {
            try { _handle.Dispose(); } catch { /* ignore */ }
        }
    }
}

/// <summary>An armed <see cref="StartupSignal"/> wait. Dispose to stop listening.</summary>
public interface IStartupProof : IDisposable
{
    /// <summary>Block until the successor announces itself, it exits without doing so, or
    /// <paramref name="timeout"/> runs out. Only true means "the replacement is running".
    /// <paramref name="successor"/> may be null, in which case only the clock bounds it.</summary>
    bool Wait(TimeSpan timeout, Process? successor);
}
