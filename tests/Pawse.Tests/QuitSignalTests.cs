using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

/// <summary>
/// The handshake the installer and uninstaller depend on (packaging/pawse.nsi opens the
/// same named event and sets it). These exercise the mechanism for real - a named event is
/// cheap and needs no WPF - but on throwaway event names: the event is session-global, so
/// driving the PRODUCTION name would signal (and quit!) a Pawse running in the developer's
/// session, and its listener could just as well swallow the auto-reset wakeup meant for a
/// test. The production name itself is pinned by
/// <see cref="The_production_event_name_matches_the_installer"/>, so renaming the event or
/// breaking the wiring still fails the build instead of silently turning every install
/// back into a force-kill.
/// </summary>
public class QuitSignalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static string TestName() => @"Local\Pawse-quit-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Signal_invokes_a_listener()
    {
        var name = TestName();
        using var fired = new ManualResetEventSlim(false);
        using var listener = QuitSignal.Listen(fired.Set, name);
        Assert.NotNull(listener);

        Assert.Equal(QuitRequest.Delivered, QuitSignal.Signal(name));
        Assert.True(fired.Wait(Patience), "the listener was never invoked");
    }

    /// <summary>Told apart from AccessDenied on purpose: a second Pawse offering to take over
    /// gives different advice for "that build is too old to ask" than for "that one is running
    /// as administrator". AccessDenied needs a real elevated process, so it stays untested.</summary>
    [Fact]
    public void Signal_reports_when_nothing_is_listening()
    {
        // A fresh name nobody has opened - unlike the production name, which a Pawse
        // running in this session WOULD be listening on.
        Assert.Equal(QuitRequest.NoListener, QuitSignal.Signal(TestName()));
    }

    [Fact]
    public void Disposing_the_listener_stops_delivery()
    {
        var name = TestName();
        using var fired = new ManualResetEventSlim(false);
        var listener = QuitSignal.Listen(fired.Set, name);
        Assert.NotNull(listener);
        listener?.Dispose();

        QuitSignal.Signal(name);
        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(500)), "a disposed listener still fired");
    }

    [Fact]
    public void A_signal_that_arrives_before_the_listener_is_not_lost()
    {
        // The installer can set the event in the gap between the app creating it and arming
        // the wait. That request still has to land, or the installer sits through its whole
        // ten-second wait for a quit that already happened.
        var name = TestName();
        using var held = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        Assert.Equal(QuitRequest.Delivered, QuitSignal.Signal(name));

        using var fired = new ManualResetEventSlim(false);
        using var listener = QuitSignal.Listen(fired.Set, name);
        Assert.NotNull(listener);
        Assert.True(fired.Wait(Patience), "a signal sent before the listener armed was dropped");
    }

    [Fact]
    public void The_production_event_name_matches_the_installer()
    {
        // Hard-coded as QUIT_EVENT in packaging/pawse.nsi - change both or neither, or the
        // installer silently falls back to asking the user to force the close.
        Assert.Equal(@"Local\Pawse-quit-2b8f9c", QuitSignal.EventName);
    }
}
