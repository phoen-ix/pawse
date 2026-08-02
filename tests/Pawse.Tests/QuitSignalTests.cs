using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

/// <summary>
/// The handshake the installer and uninstaller depend on (packaging/pawse.nsi opens the
/// same named event and sets it). These exercise it for real - a named event is cheap and
/// needs no WPF - so renaming the event or breaking the wiring fails the build instead of
/// silently turning every install back into a force-kill.
/// </summary>
public class QuitSignalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public void Signal_invokes_a_listener()
    {
        using var fired = new ManualResetEventSlim(false);
        using var listener = QuitSignal.Listen(fired.Set);
        Assert.NotNull(listener);

        Assert.True(QuitSignal.Signal());
        Assert.True(fired.Wait(Patience), "the listener was never invoked");
    }

    [Fact]
    public void Signal_is_false_when_nothing_is_listening()
    {
        // Nothing in this process has the channel open, and the test host is not Pawse.
        Assert.False(QuitSignal.Signal());
    }

    [Fact]
    public void Disposing_the_listener_stops_delivery()
    {
        using var fired = new ManualResetEventSlim(false);
        var listener = QuitSignal.Listen(fired.Set);
        Assert.NotNull(listener);
        listener?.Dispose();

        QuitSignal.Signal();
        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(500)), "a disposed listener still fired");
    }

    [Fact]
    public void A_signal_that_arrives_before_the_listener_is_not_lost()
    {
        // The installer can set the event in the gap between the app creating it and arming
        // the wait. That request still has to land, or the installer sits through its whole
        // ten-second wait for a quit that already happened.
        using var held = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignal.EventName);
        Assert.True(QuitSignal.Signal());

        using var fired = new ManualResetEventSlim(false);
        using var listener = QuitSignal.Listen(fired.Set);
        Assert.NotNull(listener);
        Assert.True(fired.Wait(Patience), "a signal sent before the listener armed was dropped");
    }
}
