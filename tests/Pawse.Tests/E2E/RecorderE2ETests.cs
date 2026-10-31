using System.Windows;
using System.Windows.Threading;
using Pawse.Core;
using Pawse.UI;
using Xunit;
using static Pawse.Tests.E2E.Ui;
using Button = System.Windows.Controls.Button;

namespace Pawse.Tests.E2E;

/// <summary>The shortcut recorder (Settings → Set…), fed through the hook's capture sink - the
/// path real keys take - and shown modally, the way App.RecordChord shows it.</summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class RecorderE2ETests
{
    private const int Ctrl = 0xA2, Shift = 0xA0, Left = 0x25, Esc = 0x1B;

    /// <summary>Open the recorder modally, run <paramref name="user"/> once it is on screen and
    /// return what ShowDialog gave back. A watchdog closes it after 10 s, so a recorder that
    /// never finishes fails the test instead of hanging the run.</summary>
    private static (List<string>? chord, LockController controller) Record(Action<ShortcutRecorderWindow, Action<int, bool>> user)
    {
        var controller = new LockController(new Config());
        var w = new ShortcutRecorderWindow(controller)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
        };
        bool timedOut = false;
        var watchdog = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Normal,
            (s, _) => { ((DispatcherTimer)s!).Stop(); timedOut = true; w.Close(); }, w.Dispatcher);
        w.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var sink = controller.CaptureSink;
            Assert.NotNull(sink);                               // armed once the window is up
            user(w, sink);
        });
        bool? result = w.ShowDialog();
        watchdog.Stop();
        Assert.False(timedOut, "the recorder was still open after 10 s");
        return (result == true ? w.Chord : null, controller);
    }

    private static void Press(Action<int, bool> sink, params int[] vks)
    {
        foreach (int vk in vks) sink(vk, true);
    }

    private static void Release(Action<int, bool> sink, params int[] vks)
    {
        foreach (int vk in vks) sink(vk, false);
    }

    [Fact]
    public void Holding_keys_then_letting_go_records_them() => OnSta(() =>
    {
        string? shown = null;
        var (chord, controller) = Record((w, sink) =>
        {
            Press(sink, Ctrl, Shift, 'U');
            w.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                shown = w.LblChord.Text;                        // what it has so far, keys still held
                Release(sink, 'U', Ctrl, Shift);               // any order
            });
        });
        Assert.Equal("Ctrl+Shift+U", shown);
        Assert.Equal(new[] { "Ctrl", "Shift", "U" }, chord);
        Assert.Null(controller.CaptureSink);                    // the keyboard is given back
    });

    [Fact]
    public void Cancel_records_nothing() => OnSta(() =>
    {
        var (chord, controller) = Record((w, _) => Click(ByText<Button>(w, "Cancel")));
        Assert.Null(chord);
        Assert.Null(controller.CaptureSink);
    });
}
