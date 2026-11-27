using System.Diagnostics;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// Settings → Lighting's three hub headings: the Corsair iCUE LINK hub switch with its state lines, red lines and take-over dialog, the iCUE lines
/// and the DeadLight lines with Retire DeadLight. It only renders: every decision about the switch comes from <see cref="HubSwitchFlow"/> (each handler
/// calls one of its methods and sends exactly what its answer says), every sentence from <see cref="HubTexts"/>, <see cref="ICueLines"/> and
/// <see cref="DeadLightLines"/>. The hub's mode never changes without the owner's say-so: "driven" is sent only after the dialog's Take over the hub.
/// </summary>
internal sealed class HubSection
{
    private static readonly TimeSpan FactsEvery = TimeSpan.FromSeconds(5);
    private const string MarkFont = "Segoe Fluent Icons";

    private readonly MonitorSession session;
    private readonly Func<XamlRoot?> root;
    private readonly IHubFactReader facts;
    private readonly HubSwitchFlow flow = new(TimeProvider.System);
    private readonly DispatcherQueue queue;

    // The hub.
    private readonly ToggleSwitch toggle = new() { OnContent = HubTexts.SwitchLabel, OffContent = HubTexts.SwitchLabel, MinWidth = 0 };
    private readonly TextBlock stateLine = new() { TextWrapping = TextWrapping.Wrap }, takenLine = Muted(""), caution = Caution(), red = Red();
    private readonly TextBlock whatHappened = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button backButton = new() { Content = HubTexts.BackButton }, retryButton = new() { Content = HubTexts.RetryButton };
    private readonly Border gaveBackBox = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12), Visibility = Visibility.Collapsed };
    // iCUE and DeadLight.
    private readonly StackPanel icueLines = new() { Spacing = 6 }, deadLightPanel = new() { Spacing = 8, Visibility = Visibility.Collapsed }, deadLightLines = new() { Spacing = 6 };
    private readonly Button retire = new() { Content = DeadLightLines.Retire, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock needsAdmin = Muted(DeadLightLines.NeedsAdmin), retireFailed = Red(), retireNote = new() { TextWrapping = TextWrapping.Wrap };

    private bool syncing, dialogOpen, retireDialogOpen, retiring, retiredHere, onScreen, factsReading, factsWanted;
    private long? lastFactsRead;
    private ICueFacts? icue;
    private DeadLightFacts? deadLight;
    private IReadOnlyList<LightingConflict>? latestConflicts;
    private DeviceHostClient? followed;
    private int refreshQueued;
    // The reading the take-over dialog showed (null: its no-reading form); the take-over request names exactly this.
    private HubReading? dialogShown;

    public HubSection(MonitorSession session, Func<XamlRoot?> root, IHubFactReader facts)
    {
        this.session = session;
        this.root = root;
        this.facts = facts;
        var hub = new StackPanel { Spacing = 8 };
        hub.Children.Add(Heading(HubTexts.Heading));
        AutomationProperties.SetName(toggle, HubTexts.SwitchLabel);
        toggle.Toggled += (_, _) => { if (!syncing) Apply(flow.Click(State, toggle.IsOn)); };
        hub.Children.Add(toggle);
        hub.Children.Add(stateLine);
        hub.Children.Add(takenLine);
        hub.Children.Add(Muted(HubTexts.About));
        hub.Children.Add(caution);
        hub.Children.Add(red);
        backButton.Click += (_, _) => Apply(flow.Back(State));
        retryButton.Click += (_, _) => Apply(flow.Retry(State));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { backButton, retryButton } };
        gaveBackBox.BorderBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        gaveBackBox.Child = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = HubTexts.GaveBackTitle, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap },
                whatHappened, new TextBlock { Text = HubTexts.GaveBackTail, TextWrapping = TextWrapping.Wrap }, buttons },
        };
        hub.Children.Add(gaveBackBox);
        // Polite live regions, so a screen reader hears a new red or caution line without focus moving.
        foreach (var message in new[] { caution, red, retireFailed })
        {
            AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            message.Visibility = Visibility.Collapsed;
        }
        takenLine.Visibility = Visibility.Collapsed;

        var icuePanel = new StackPanel { Spacing = 8 };
        icuePanel.Children.Add(Heading(ICueLines.Heading));
        icuePanel.Children.Add(Muted(ICueLines.Lead));
        icuePanel.Children.Add(icueLines);

        deadLightPanel.Children.Add(Heading(DeadLightLines.Heading));
        deadLightPanel.Children.Add(deadLightLines);
        retire.Click += (_, _) => RetireAsync();
        deadLightPanel.Children.Add(retire);
        deadLightPanel.Children.Add(needsAdmin);
        deadLightPanel.Children.Add(retireFailed);
        deadLightPanel.Children.Add(retireNote);
        retireNote.Visibility = Visibility.Collapsed;
        retire.Visibility = needsAdmin.Visibility = Visibility.Collapsed;

        Root = new StackPanel { Spacing = 8, Children = { hub, icuePanel, deadLightPanel } };
        queue = Root.DispatcherQueue;
    }

    /// <summary>The three headings, in the order Settings → Lighting shows them.</summary>
    public UIElement Root { get; }

    private HostState? State => session.Lighting?.State;

    /// <summary>
    /// Called on the session's tick and when the Lighting section comes on or goes off screen. The switch's sequence is stepped on every call; the helper's
    /// changes are followed, and the iCUE and DeadLight facts read, only while the section is on screen. <paramref name="conflicts"/> are the ones the
    /// page already has (the helper's own list, or its local scan while lighting is off), or null when it has none yet.
    /// </summary>
    public void Update(bool onScreen, IReadOnlyList<LightingConflict>? conflicts)
    {
        this.onScreen = onScreen;
        latestConflicts = conflicts;
        Follow(onScreen ? session.Lighting : null);
        Apply(flow.Look(State));
        if (onScreen) ReadFacts(force: false);
    }

    // ---- The switch ----

    /// <summary>Shows what the flow returned, then does what it asks: sends one request, or opens the dialog. Nothing else decides anything.</summary>
    private void Apply(HubSwitchShow show)
    {
        bool connected = session.Settings.LightingEnabled && State is not null;
        syncing = true;
        try
        {
            if (toggle.IsOn != show.On) toggle.IsOn = show.On;
            bool enabled = show.Enabled && connected;
            if (toggle.IsEnabled != enabled) toggle.IsEnabled = enabled;
        }
        finally { syncing = false; }
        backButton.IsEnabled = retryButton.IsEnabled = show.Enabled && connected;
        Say(red, show.Red ?? "");
        RenderState();
        if (show.Send != HubSend.None) SendAsync(show.Send);
        if (show.Ask is { } ask) AskAsync(ask);
    }

    /// <summary>The state line and what goes with it, as the spec's state table: off, switching, driving, let go and gave back.</summary>
    private void RenderState()
    {
        var hub = State?.Hub;
        bool picture = session.PictureApplies;
        string line = HubTexts.OffLine;
        string? taken = null, cautionText = null;
        bool gaveBack = false;
        switch (hub?.State)
        {
            case HubStates.Switching:
                line = HubTexts.SwitchingLine;
                break;
            case HubStates.Driving:
                line = HubTexts.Driving(session.HubReadings?.Readings, session.HubReadings?.Age);
                taken = HubTexts.TakenLine(hub.Before, hub.TakenAt, DateTimeOffset.Now);
                break;
            case HubStates.LetGo:
                // Starting and waking are a moment's wait, not a fault: no caution line.
                if (HubTexts.TakesInAMoment(hub.Reason)) line = HubTexts.TakesLine;
                else { line = HubTexts.LetGoLine; cautionText = HubTexts.LetGoCaution(hub.Reason, hub.Detail); }
                break;
            case HubStates.GaveBack:
                line = HubTexts.GaveBackLine;
                gaveBack = true;
                break;
        }
        stateLine.Text = line;
        Say(takenLine, taken ?? "");
        Say(caution, cautionText ?? "");
        Show(gaveBackBox, gaveBack);
        if (gaveBack) whatHappened.Text = hub?.Stop is { } stop ? HubTexts.WhatHappened(stop, picture) : "";
        Show(whatHappened, gaveBack && whatHappened.Text.Length > 0);
    }

    /// <summary>Sends the request the flow asked for, and gives the flow its answer. Driven is only ever asked for by the flow's answer to the dialog.</summary>
    private async void SendAsync(HubSend what)
    {
        HostMessage request = what switch
        {
            HubSend.Read => new ReadHubRequest(),
            HubSend.Driven => new SetHubModeRequest(HubModes.Driven, dialogShown),
            HubSend.OpenRgb => new SetHubModeRequest(HubModes.OpenRgb),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };
        var client = session.Lighting;
        bool ok = false;
        string? error = null;
        if (client is not null)
        {
            try { var reply = await client.SendAsync(request, CancellationToken.None); ok = reply.Ok; error = reply.Error; }
            catch (Exception failure) { error = failure.Message; }
            // A reply from a client the window no longer follows says nothing about the new one.
            if (!ReferenceEquals(client, session.Lighting)) { ok = false; error = null; }
        }
        Apply(flow.Reply(State, ok, error));
    }

    /// <summary>The take-over dialog. Closing it, any way but Take over the hub, is Cancel; a dialog that cannot open is Cancel too.</summary>
    private async void AskAsync(HubAsk ask)
    {
        var xamlRoot = root();
        if (xamlRoot is null || dialogOpen) { Apply(flow.Answer(State, false)); return; }
        dialogOpen = true;
        dialogShown = ask.Shown;
        bool takeOver = false;
        try { takeOver = await TakeOverDialog(xamlRoot, ask.Read).ShowAsync() == ContentDialogResult.Primary; }
        catch (Exception error) { App.Note($"Take-over dialog failed: {error.Message}"); }
        finally { dialogOpen = false; }
        Apply(flow.Answer(State, takeOver));
    }

    private ContentDialog TakeOverDialog(XamlRoot xamlRoot, HubReading? read)
    {
        bool reading = new HubAsk(read).Shown is not null;
        var box = new StackPanel { Spacing = 4 };
        box.Children.Add(new TextBlock { Text = HubTexts.DialogBoxHead, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (reading)
        {
            // The rows of a reading that has just been taken: the same Coolant, Pump and fans the Readings section shows.
            foreach (var (label, value) in HubTexts.ReadingRows(HubRoutes.Driven, read!.Readings, TimeSpan.Zero, session.PictureApplies))
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var name = new TextBlock { Text = label };
                var number = new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(number, 1);
                row.Children.Add(name); row.Children.Add(number);
                box.Children.Add(row);
            }
        }
        else box.Children.Add(new TextBlock { Text = HubTexts.DialogNoReading, TextWrapping = TextWrapping.Wrap });
        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = HubTexts.DialogAsks, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = HubTexts.DialogBlink, TextWrapping = TextWrapping.Wrap },
                new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Child = box },
            },
        };
        if (reading) body.Children.Add(new TextBlock { Text = HubTexts.DialogCheck, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Muted(HubTexts.Never));
        // Cancel is the default button: Enter never takes the hub.
        return new ContentDialog
        {
            XamlRoot = xamlRoot, Title = HubTexts.DialogTitle, Content = body,
            PrimaryButtonText = HubTexts.TakeOver, CloseButtonText = HubTexts.Cancel, DefaultButton = ContentDialogButton.Close,
        };
    }

    // ---- Following the helper while the section is on screen ----

    private void Follow(DeviceHostClient? wanted)
    {
        if (ReferenceEquals(wanted, followed)) return;
        if (followed is not null) followed.Changed -= OnClientChanged;
        followed = wanted;
        if (followed is not null) followed.Changed += OnClientChanged;
    }

    /// <summary>Raised on the client's read loop: one refresh waits on the UI thread, and it reads the state as it is when it runs.</summary>
    private void OnClientChanged()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) == 1) return;
        if (!queue.TryEnqueue(Refresh)) Volatile.Write(ref refreshQueued, 0);
    }

    private void Refresh()
    {
        Volatile.Write(ref refreshQueued, 0);
        if (followed is not null) Apply(flow.Look(State));
    }

    // ---- iCUE and DeadLight ----

    /// <summary>
    /// Reads the facts off the UI thread: one read at a time, at most one every 5 s, and one straight after a retire (<paramref name="force"/>). The running
    /// programs are listed on that same background read; the conflicts are the page's own, or, with none yet, found in that list.
    /// </summary>
    private void ReadFacts(bool force)
    {
        if (force) factsWanted = true;
        if (factsReading) return;
        if (!factsWanted && lastFactsRead is { } at && Stopwatch.GetElapsedTime(at) < FactsEvery) return;
        factsWanted = false;
        factsReading = true;
        lastFactsRead = Stopwatch.GetTimestamp();
        _ = ReadFactsAsync(latestConflicts);
    }

    private async Task ReadFactsAsync(IReadOnlyList<LightingConflict>? conflicts)
    {
        try
        {
            var (iCueFacts, deadLightFacts) = await Task.Run(() =>
            {
                var processes = LightingConflicts.RunningProcesses();
                var found = conflicts ?? LightingConflicts.Find(processes, null);
                return (facts.ICue(found, processes), facts.DeadLight(found));
            });
            icue = iCueFacts; deadLight = deadLightFacts;
            RenderFacts();
        }
        catch (Exception error) { App.Note($"Reading the iCUE and DeadLight facts failed: {error.Message}"); }
        finally { factsReading = false; }
        if (factsWanted && onScreen) ReadFacts(force: false);
    }

    private void RenderFacts()
    {
        icueLines.Children.Clear();
        if (icue is not null) foreach (var line in ICueLines.Lines(icue)) icueLines.Children.Add(FactRow(line));
        bool found = deadLight is not null && DeadLightLines.Found(deadLight);
        Show(deadLightPanel, found);
        deadLightLines.Children.Clear();
        if (!found) return;
        var lines = DeadLightLines.Lines(deadLight!);
        foreach (var line in lines) deadLightLines.Children.Add(FactRow(line));
        // Both lines good: retired, and "just now" when this window did it.
        if (lines.All(line => line.Mark == FactMark.Good))
        {
            // One muted line, as the mockup: "Retired just now." goes before the retired line.
            deadLightLines.Children.Add(Muted(retiredHere ? DeadLightLines.JustNow + " " + DeadLightLines.Retired : DeadLightLines.Retired));
        }
        RenderRetire();
    }

    private void RenderRetire()
    {
        bool can = deadLight is not null && DeadLightLines.CanRetire(deadLight);
        bool elevated = Collector.IsElevated;
        Show(retire, can);
        retire.IsEnabled = elevated && !retiring;
        Show(needsAdmin, can && !elevated);
    }

    /// <summary>Retire DeadLight: the dialog says what will happen, then the retire runs off the UI thread and its line shows when it completes.</summary>
    private async void RetireAsync()
    {
        if (retiring || retireDialogOpen || root() is not { } xamlRoot) return;
        retireDialogOpen = true;
        bool confirmed = false;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot, Title = DeadLightLines.DialogTitle,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = DeadLightLines.DialogLead, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = DeadLightLines.DialogTask, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = DeadLightLines.DialogStop, TextWrapping = TextWrapping.Wrap },
                        Muted(DeadLightLines.DialogNothing),
                    },
                },
                PrimaryButtonText = DeadLightLines.Retire, CloseButtonText = HubTexts.Cancel, DefaultButton = ContentDialogButton.Close,
            };
            confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error) { App.Note($"Retire DeadLight dialog failed: {error.Message}"); }
        finally { retireDialogOpen = false; }
        if (!confirmed) return;

        retiring = true;
        Say(retireFailed, ""); Say(retireNote, "");
        RenderRetire();
        bool elevated = Collector.IsElevated;
        string? openRgbPath = session.Settings.OpenRgbPath;
        RetireResult result;
        // It waits up to 5 s for processes to exit and calls schtasks, so it stays off the UI thread; an exception from the Windows calls is a failure line.
        try { result = await Task.Run(() => DeadLightRetire.Run(new WindowsDeadLight(), elevated, openRgbPath)); }
        catch (Exception error) { result = DeadLightRetire.Failed(error); }
        retiring = false;
        if (result.Ok) retiredHere = true;
        if (result.Ok) Say(retireNote, result.Line ?? ""); else Say(retireFailed, result.Line ?? "");
        // The lines are read again straight away, whatever the result.
        ReadFacts(force: true);
        RenderRetire();
    }

    private static Grid FactRow(FactLine line)
    {
        var (glyph, brush) = line.Mark switch
        {
            FactMark.Good => ("", "SystemFillColorSuccessBrush"),
            FactMark.Blocking => ("", "SystemFillColorCriticalBrush"),
            _ => ("", "SystemFillColorCautionBrush"),
        };
        // The mark is colour and shape only: the line's own words carry the meaning, so a screen reader skips the glyph.
        var mark = new TextBlock { Text = glyph, FontFamily = new FontFamily(MarkFont), Foreground = (Brush)Application.Current.Resources[brush], VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        AutomationProperties.SetAccessibilityView(mark, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap });
        foreach (string instruction in line.Instructions ?? []) text.Children.Add(Muted(instruction));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        row.Children.Add(mark); row.Children.Add(text);
        return row;
    }

    // ---- Small helpers, in the style of the page ----

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
    private static TextBlock Caution() => new() { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] };
    private static TextBlock Red() => new() { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
    private static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };
    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows a message, hidden while it is empty; a screen reader hears it when it appears.</summary>
    private static void Say(TextBlock block, string text)
    {
        if (block.Text == text) return;
        block.Text = text;
        Show(block, text.Length > 0);
        if (text.Length > 0)
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(block)?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
}
