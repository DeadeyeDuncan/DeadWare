using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeadWare.WinUI;

/// <summary>
/// One labelled row of a device card: its label, a slider and the value as text. It behaves as the page's Brightness slider does: a drag
/// sends at most once per 100 ms through its own <see cref="Throttle"/> and its last value when it is released, and the arrow keys' last
/// step goes when the key comes up. While it is dragged, or a value of the user's waits or is on its way, a state arriving doesn't move
/// it; a refused change puts it back to the last state's value once nothing of the user's is left to hold it.
/// </summary>
internal sealed class TuningSlider
{
    private readonly Throttle sends;
    private readonly Action<HostMessage, Action<Reply>?> send;
    private readonly Func<int, string> format;
    private readonly TextBlock value = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    // sending: the slider's changes sent whose reply hasn't come. posted: the value of the latest change posted to the throttle, the
    // only one of those waiting that goes. shown: the value from the last state, which the slider goes back to.
    private int sending, posted;
    private bool syncing, dragging;
    private double? shown;

    /// <param name="queue">The page's queue, for the throttle's timer.</param>
    /// <param name="send">The page's send: the reply comes back on the UI thread, and not at all from a client the page no longer follows.</param>
    /// <param name="label">The row's label, which is also the slider's name for a screen reader.</param>
    /// <param name="maximum">The slider runs from 0 to this, a step of 1 at a time.</param>
    /// <param name="largeChange">How far Page Up and Page Down move it.</param>
    /// <param name="request">The request that sets a value.</param>
    /// <param name="format">The value as the row shows it; the number alone when null.</param>
    /// <param name="changed">Told of every value the slider shows, the user's or a state's.</param>
    public TuningSlider(DispatcherQueue queue, Action<HostMessage, Action<Reply>?> send, string label, double maximum, double largeChange, Func<int, HostMessage> request,
        Func<int, string>? format = null, Action<int>? changed = null)
    {
        this.send = send;
        this.format = format ?? (number => number.ToString(System.Globalization.CultureInfo.CurrentCulture));
        sends = new Throttle(queue, Send);
        Slider = new Slider { Minimum = 0, Maximum = maximum, StepFrequency = 1, SmallChange = 1, LargeChange = largeChange, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(Slider, label);
        ShowValue();
        Slider.ValueChanged += (_, args) =>
        {
            ShowValue();
            changed?.Invoke(Value);
            if (syncing) return;
            posted = (int)Math.Round(args.NewValue);
            sends.Post(request(posted));
        };
        // The slider handles its own pointer events, so these listen to handled ones too. A drag ends when the pointer is released or
        // capture is lost, and its last value goes then rather than at the end of the interval.
        Slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => dragging = Slider.IsEnabled), true);
        var released = new PointerEventHandler((_, _) => EndDrag());
        Slider.AddHandler(UIElement.PointerReleasedEvent, released, true);
        Slider.AddHandler(UIElement.PointerCaptureLostEvent, released, true);
        Slider.AddHandler(UIElement.PointerCanceledEvent, released, true);
        Slider.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, _) => sends.Flush()), true);

        var name = new TextBlock { Text = label, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // The label column holds the longest label ("This device"), the value column the widest value ("100%", "200").
        Root = new Grid { ColumnSpacing = 10 };
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        Root.ColumnDefinitions.Add(new ColumnDefinition());
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        Grid.SetColumn(Slider, 1);
        Grid.SetColumn(value, 2);
        AutomationProperties.SetAccessibilityView(name, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(value, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Root.Children.Add(name);
        Root.Children.Add(Slider);
        Root.Children.Add(value);
    }

    public Grid Root { get; }
    public Slider Slider { get; }

    /// <summary>The value the slider shows now, the user's or a state's.</summary>
    public int Value => (int)Math.Round(Slider.Value);

    /// <summary>
    /// Takes a state's value, unless the user is dragging the slider or a value of theirs waits or is on its way, and enables or disables
    /// it. Disabling it ends a drag, which may never get its release, and drops a waiting value (<see cref="Cancel"/>).
    /// </summary>
    public void Show(double next, bool enabled)
    {
        shown = next;
        if (!enabled) Cancel();
        Slider.IsEnabled = enabled;
        Apply();
    }

    /// <summary>Ends a drag and drops a value waiting for its interval, as when the host stops running or Reset replaces it.</summary>
    public void Cancel()
    {
        dragging = false;
        sends.Cancel();
    }

    /// <summary>Ends a drag and sends a waiting value now, as when the page is hidden: a drag that never gets its release would hold the slider against every later state.</summary>
    public void Leave()
    {
        dragging = false;
        sends.Flush();
    }

    private void Send(HostMessage message)
    {
        int sent = posted;
        sending++;
        send(message, reply =>
        {
            sending--;
            // The state carrying an accepted change follows its reply. A refused one goes back to the last state's value, unless the user
            // has moved the slider on since; a later state puts it right otherwise.
            if (!reply.Ok || shown == sent) Apply();
        });
    }

    private void Apply()
    {
        if (dragging || sending > 0 || sends.Pending || shown is not { } next) return;
        syncing = true;
        try { Slider.Value = Math.Clamp(next, Slider.Minimum, Slider.Maximum); }
        finally { syncing = false; }
    }

    private void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        sends.Flush();
        // A press that changed nothing leaves nothing on its way, so the slider takes any state that arrived meanwhile.
        Apply();
    }

    private void ShowValue() => value.Text = format(Value);
}
