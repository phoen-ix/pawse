using System.Diagnostics;
using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

/// <summary>
/// The handshake that decides whether a portable self-replace keeps the new exe or puts the
/// old one back. Before it existed, <see cref="SelfReplace"/> took <c>Process.Start</c>
/// returning as proof of a live successor - which a .NET apphost with no matching runtime
/// satisfies perfectly while never running a line of managed code.
///
/// <para>Split in two on purpose. The timing rules go through
/// <see cref="StartupSignal.Watching"/> on a handle the test owns: they are what the
/// correctness of the rollback rests on, and a named event is both session-global (so the
/// production name would let a Pawse running in the developer's session answer a test) and
/// Windows-only. The named channel itself is covered separately, on throwaway names, the
/// same way <see cref="QuitSignalTests"/> does it.</para>
/// </summary>
public class StartupSignalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>Long enough that a loaded CI box does not fail a test about timing out, short
    /// enough that the negative cases do not dominate the run.</summary>
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(750);

    private static string TestName() => @"Local\Pawse-started-test-" + Guid.NewGuid().ToString("N");

    private static EventWaitHandle Handle() => new(false, EventResetMode.ManualReset);

    // ---- the timing rules the rollback depends on ----

    [Fact]
    public void A_successor_that_signals_is_proof_of_life()
    {
        using var handle = Handle();
        using var proof = StartupSignal.Watching(handle);
        handle.Set();

        Assert.True(proof.Wait(Patience, null), "a signal was not accepted as proof");
    }

    /// <summary>The whole point. A successor that starts but never reaches managed code - an
    /// apphost sitting on hostfxr's "you must install .NET" dialog - signals nothing, and
    /// silence has to read as failure so <see cref="SelfReplace.Swap"/> rolls back.</summary>
    [Fact]
    public void Silence_is_not_proof()
    {
        using var handle = Handle();
        using var proof = StartupSignal.Watching(handle);

        var watch = Stopwatch.StartNew();
        Assert.False(proof.Wait(ShortWait, null), "a silent successor was treated as running");
        watch.Stop();
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(600),
            $"gave up after {watch.Elapsed.TotalMilliseconds:0}ms instead of waiting out the timeout");
    }

    /// <summary>Manual-reset, so a signal that lands before the wait begins is still there
    /// when it does - the successor can be quicker than we are. An auto-reset event would
    /// drop it and roll back a perfectly good update.</summary>
    [Fact]
    public void A_signal_sent_before_the_wait_still_counts()
    {
        using var handle = Handle();
        using var proof = StartupSignal.Watching(handle);
        handle.Set();

        Assert.True(proof.Wait(Patience, null), "an early signal was dropped");
        Assert.True(proof.Wait(Patience, null), "the proof did not survive being read");
    }

    /// <summary>A successor that exits outright is known-dead immediately, so a failed update
    /// does not make the user sit through the full timeout for a verdict already in.</summary>
    [Fact]
    public void A_successor_that_exits_without_signalling_is_dead()
    {
        using var handle = Handle();
        using var proof = StartupSignal.Watching(handle);

        using var doomed = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 1",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(doomed);
        Assert.True(doomed!.WaitForExit((int)Patience.TotalMilliseconds), "the helper process never exited");

        // A generous timeout on purpose: passing means the process check ended the wait, not
        // the clock. Judged by the clock alone this would take 30 s and fail the assertion.
        var watch = Stopwatch.StartNew();
        Assert.False(proof.Wait(TimeSpan.FromSeconds(30), doomed),
            "a successor that exited without signalling was treated as running");
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"took {watch.Elapsed.TotalSeconds:0.0}s to notice a dead successor - the process check is not working");
    }

    /// <summary>A live successor that simply has not signalled yet must not be called dead;
    /// only the timeout ends that wait. This process stands in for it - it is certainly
    /// running, and needs no helper to stay that way.</summary>
    [Fact]
    public void A_live_successor_that_has_not_signalled_yet_is_not_dead()
    {
        using var handle = Handle();
        using var proof = StartupSignal.Watching(handle);
        using var alive = Process.GetCurrentProcess();

        var watch = Stopwatch.StartNew();
        Assert.False(proof.Wait(ShortWait, alive));
        watch.Stop();
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(600),
            $"gave up after {watch.Elapsed.TotalMilliseconds:0}ms on a successor that was still alive");
    }

    /// <summary>The successor may take a moment to get through the CLR before it signals;
    /// arriving late is still arriving.</summary>
    [Fact]
    public void A_late_signal_from_a_live_successor_still_counts()
    {
        using var handle = Handle();
        using var proof = StartupSignal.Watching(handle);
        using var alive = Process.GetCurrentProcess();

        // Fully qualified: UseWPF + UseWindowsForms puts System.Windows.Forms.Timer and
        // System.Threading.Timer in scope together, and the bare name is CS0104.
        using var late = new System.Threading.Timer(
            _ => handle.Set(), null, TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        Assert.True(proof.Wait(Patience, alive), "a signal that arrived late was not accepted");
    }

    // ---- the named channel itself ----

    [Fact]
    public void Announcing_reaches_a_waiting_predecessor()
    {
        var name = TestName();
        using var proof = StartupSignal.Expect(name);
        Assert.NotNull(proof);

        StartupSignal.Announce(name);

        Assert.True(proof!.Wait(Patience, null), "the announcement never reached the channel");
    }

    /// <summary>Every normal start calls this with nothing listening. It must be a silent
    /// no-op, not a throw on the first line of StartupCore.</summary>
    [Fact]
    public void Announcing_with_nobody_listening_is_harmless()
        => Assert.Null(Record.Exception(() => StartupSignal.Announce(TestName())));

    [Fact]
    public void The_production_event_name_is_pinned()
    {
        // Every build from the one that introduced this must announce on exactly this name.
        // A successor that does not gets rolled back as failed, because silence is how a
        // missing runtime looks - so this is a compatibility contract across releases, not
        // merely a constant. Same class of thing as the quit event and the mutex name.
        Assert.Equal(@"Local\Pawse-started-2b8f9c", StartupSignal.EventName);
    }

    /// <summary>The timeout has to cover the first run of a compressed single-file build,
    /// which unpacks ~66 MB before managed code exists. Too short and a good update on a slow
    /// disk is rolled back for no reason.</summary>
    [Fact]
    public void The_proof_timeout_leaves_room_for_a_cold_start()
        => Assert.True(StartupSignal.ProofTimeout >= TimeSpan.FromSeconds(30),
                       $"{StartupSignal.ProofTimeout.TotalSeconds:0}s is not enough for a first-run self-extract");
}
