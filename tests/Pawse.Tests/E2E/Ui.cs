using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Threading;
using Pawse.Core;
using Xunit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using ContentControl = System.Windows.Controls.ContentControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using Slider = System.Windows.Controls.Slider;
using TextBox = System.Windows.Controls.TextBox;

namespace Pawse.Tests.E2E;

/// <summary>The UI-level suite (trait Category=E2E) runs one test at a time and after the
/// parallel unit tests: several tests force <see cref="Deployment"/>'s cached mode, which the
/// unit tests read too.</summary>
[CollectionDefinition("E2E", DisableParallelization = true)]
public sealed class E2ECollection { }

/// <summary>
/// Drives the app's real windows the way a user does, through UI Automation: buttons are
/// invoked, boxes toggled, radios selected and text set through each control's automation
/// peer, which refuses a disabled control just as a click would. Controls are found by what
/// they say on screen (their content), not by field name, wherever that is unique. Input the
/// peers cannot express - a typed character, a key pressed into a chord box, a mouse hold - is
/// raised as the routed input event the control itself listens to.
/// </summary>
internal static class Ui
{
    /// <summary>WPF needs an STA thread with its own dispatcher; xunit's are MTA.</summary>
    public static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Off every screen, so the real pointer cannot hover anything.</summary>
    public static void Show(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = w.Top = -10000;
        w.Show();
        Flush();
    }

    /// <summary>Run everything queued down to idle - layout, render, BeginInvoke'd handlers.</summary>
    public static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Keep the dispatcher (timers included) running until <paramref name="done"/>
    /// holds or the time is up; returns whether it held. The poll of this suite.</summary>
    public static bool PumpUntil(Func<bool> done, int timeoutMs = 5000)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var poll = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
            (_, _) => { if (done() || clock.ElapsedMilliseconds > timeoutMs) frame.Continue = false; },
            Dispatcher.CurrentDispatcher);
        poll.Start();
        Dispatcher.PushFrame(frame);
        poll.Stop();
        return done();
    }

    /// <summary>Keep the dispatcher running for a while - only for "and nothing happens"
    /// assertions, which have nothing to poll for.</summary>
    public static void PumpFor(int ms)
    {
        var clock = Stopwatch.StartNew();
        PumpUntil(() => clock.ElapsedMilliseconds >= ms, ms + 1000);
    }

    // ---- finding controls ----

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Descendants(child))
                yield return d;
    }

    /// <summary>The one control of type <typeparamref name="T"/> under <paramref name="root"/>
    /// whose content reads <paramref name="text"/> - getByRole(role, {name}).</summary>
    public static T ByText<T>(DependencyObject root, string text) where T : ContentControl
    {
        var hits = Descendants(root).OfType<T>().Where(c => c.Content as string == text).ToList();
        Assert.True(hits.Count == 1, $"expected one {typeof(T).Name} \"{text}\", found {hits.Count}");
        return hits[0];
    }

    public static CheckBox Box(DependencyObject root, string label) => ByText<CheckBox>(root, label);

    // ---- acting, through UI Automation ----

    private static object Pattern(UIElement e, PatternInterface p)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(e);
        Assert.NotNull(peer);
        var pattern = peer.GetPattern(p);
        Assert.True(pattern is not null, $"{e.GetType().Name} has no {p} pattern");
        return pattern;
    }

    /// <summary>Click: throws ElementNotEnabledException on a disabled button, like a dead click.</summary>
    public static void Click(ButtonBase b)
    {
        ((IInvokeProvider)Pattern(b, PatternInterface.Invoke)).Invoke();
        Flush();
    }

    public static void Toggle(CheckBox b)
    {
        ((IToggleProvider)Pattern(b, PatternInterface.Toggle)).Toggle();
        Flush();
    }

    public static void Check(CheckBox b, bool on)
    {
        if (b.IsChecked != on) Toggle(b);
        Assert.Equal(on, b.IsChecked);
    }

    public static void Select(RadioButton r)
    {
        ((ISelectionItemProvider)Pattern(r, PatternInterface.SelectionItem)).Select();
        Flush();
    }

    public static void Fill(TextBox t, string text)
    {
        ((IValueProvider)Pattern(t, PatternInterface.Value)).SetValue(text);
        Flush();
    }

    public static void Slide(Slider s, double value)
    {
        ((IRangeValueProvider)Pattern(s, PatternInterface.RangeValue)).SetValue(value);
        Flush();
    }

    // ---- raw input the peers cannot express ----

    /// <summary>One typed character offered to <paramref name="target"/>; true when the control
    /// refused it (marked the PreviewTextInput handled).</summary>
    public static bool TypeRefused(UIElement target, string text)
    {
        var e = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, target, text))
        { RoutedEvent = UIElement.PreviewTextInputEvent };
        target.RaiseEvent(e);
        return e.Handled;
    }

    /// <summary>A paste of <paramref name="text"/>; true when the control cancelled it.</summary>
    public static bool PasteRefused(UIElement target, string text)
    {
        var e = new DataObjectPastingEventArgs(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, text),
                                               false, System.Windows.DataFormats.UnicodeText);
        target.RaiseEvent(e);
        return e.CommandCancelled;
    }

    /// <summary>Keyboard focus arriving, as a click or Tab into the control delivers it.</summary>
    public static void FocusArrives(UIElement target)
    {
        target.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, target)
        { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
        Flush();
    }

    public static void Key(UIElement target, Key key, bool down)
    {
        var source = PresentationSource.FromVisual((System.Windows.Media.Visual)target);
        Assert.NotNull(source);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent });
    }

    /// <summary>Press every key in order, then release them in reverse.</summary>
    public static void Chord(UIElement target, params Key[] keys)
    {
        foreach (var k in keys) Key(target, k, down: true);
        foreach (var k in keys.Reverse()) Key(target, k, down: false);
        Flush();
    }

    public static void MouseDown(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });

    public static void MouseUp(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });

    public static void MouseLeave(UIElement target) =>
        target.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = UIElement.MouseLeaveEvent });

    // ---- app state the tests pin ----

    /// <summary>The test host counts as portable by Deployment's rules (no installer entry names
    /// its folder); most tests want an installed copy. Undo with <see cref="ResetDeployment"/>.</summary>
    public static void ForceDeployment(DeploymentMode mode) => ModeField().SetValue(null, (DeploymentMode?)mode);

    public static void ResetDeployment() => ModeField().SetValue(null, null);

    private static FieldInfo ModeField()
    {
        var f = typeof(Deployment).GetField("_mode", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        return f;
    }
}

/// <summary>
/// Message boxes shown on this thread while it is armed: each one is read (its text) and
/// answered with its default button, so a test can assert what the user would have been told
/// instead of hanging on a modal loop. WPF's dispatcher keeps running inside MessageBox's own
/// loop, which is where the poll fires.
/// </summary>
internal sealed class DialogWatch : IDisposable
{
    private readonly DispatcherTimer _poll;
    private readonly uint _thread = GetCurrentThreadId();
    public List<string> Texts { get; } = new();

    public DialogWatch()
    {
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal,
            (_, _) => AnswerOpenDialogs(), Dispatcher.CurrentDispatcher);
        _poll.Start();
    }

    private void AnswerOpenDialogs()
    {
        EnumThreadWindows(_thread, (hwnd, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "#32770" || !IsWindowVisible(hwnd)) return true;
            var text = new StringBuilder(2048);
            GetWindowText(GetDlgItem(hwnd, 0xFFFF), text, text.Capacity);   // the message's static
            Texts.Add(text.ToString());
            PostMessage(hwnd, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    public void Dispose() => _poll.Stop();

    private const uint WM_COMMAND = 0x0111;
    private const int IDOK = 1;
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
