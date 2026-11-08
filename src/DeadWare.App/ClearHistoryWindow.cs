using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>The Clear history… button of Settings, the Connections page and Usage history. It is disabled while a clear is active, with
/// a tooltip that shows on the disabled button, and opens a modal <see cref="ClearHistoryWindow"/> owned by the window it sits in.</summary>
internal static class ClearHistoryEntry
{
    internal static Button Create(UiSession session, Func<Window?> owner)
    {
        var button = Ui.Button(HistoryClearCopy.EntryButton, () => Open(session, owner()));
        button.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTipService.SetShowOnDisabled(button, true);
        void Refresh()
        {
            // One at a time (S:2989): Queued, Clearing, or Failed with its marker saved. A failure without the marker does not hold back.
            bool waiting = session.ClearWaitingForRestart, enabled = !session.ClearActive && !waiting;
            if (button.IsEnabled != enabled) button.IsEnabled = enabled;
            string? tip = enabled ? null : HistoryClearCopy.DisabledTip(waiting);
            if (!Equals(button.ToolTip, tip)) button.ToolTip = tip;
        }
        WhileShown(button, session, Refresh);
        return button;
    }

    private static void Open(UiSession session, Window? owner)
    {
        if (session.ClearActive || session.Ending) return;
        var window = new ClearHistoryWindow(session);
        if (owner is { IsVisible: true }) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        window.ShowModal();
    }

    /// <summary>Runs <paramref name="refresh"/> now and on each <see cref="UiSession.Changed"/> while <paramref name="element"/> is shown
    /// (subscribed at once, dropped on Unloaded, taken again on Loaded). Returns the action that drops the subscription.</summary>
    internal static Action WhileShown(FrameworkElement element, UiSession session, Action refresh)
    {
        bool attached = false;
        void Attach() { if (!attached) { session.Changed += refresh; attached = true; } refresh(); }
        void Detach() { if (attached) { session.Changed -= refresh; attached = false; } }
        Attach();
        element.Loaded += (_, _) => Attach();
        element.Unloaded += (_, _) => Detach();
        return Detach;
    }
}

/// <summary>The progress block of a clear: a header, one line per store, the summary once every line has finished, Try again while a
/// store failed with its worker running, and, in the window, Close. Settings shows it whenever a store has a clear or a result.</summary>
internal sealed class HistoryClearProgressView : StackPanel
{
    private readonly UiSession session;
    private readonly bool inWindow;
    private readonly StackPanel list = new() { Margin = new(0, 6, 0, 0) };
    private readonly List<TextBlock> lines = [];
    private HistoryClearLine[]? shown;

    internal TextBlock Header { get; } = Ui.Heading("", 14);
    internal IReadOnlyList<TextBlock> Lines => lines;
    internal TextBlock Summary { get; } = Ui.Text("", 12, "Muted");
    internal Button TryAgain { get; }
    /// <summary>The window's only Close; null in Settings.</summary>
    internal Button? Close { get; }
    /// <summary>Drops the view's <see cref="UiSession.Changed"/> subscription.</summary>
    internal Action Detach { get; }

    /// <param name="closeButton">True in the window, which also decides when the view shows; false in Settings, where it shows whenever
    /// <see cref="UiSession.ClearLines"/> is non-empty.</param>
    internal HistoryClearProgressView(UiSession session, bool closeButton)
    {
        this.session = session; inWindow = closeButton;
        AutomationProperties.SetLiveSetting(Header, AutomationLiveSetting.Polite);
        Children.Add(Header); Children.Add(list);
        Summary.Margin = new(0, 6, 0, 0); Summary.Visibility = Visibility.Collapsed; Children.Add(Summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) };
        TryAgain = Ui.Button(HistoryClearCopy.TryAgainButton, Retry); TryAgain.Margin = new(0, 0, 8, 0); TryAgain.Visibility = Visibility.Collapsed;
        buttons.Children.Add(TryAgain);
        if (closeButton) { Close = Ui.Button(HistoryClearCopy.CloseButton, () => { }); buttons.Children.Add(Close); }
        Children.Add(buttons);
        if (!inWindow) Visibility = Visibility.Collapsed;
        Detach = ClearHistoryEntry.WhileShown(this, session, Refresh);
    }

    internal void Refresh() => Apply(session.ClearLines());

    /// <summary>Shows <paramref name="value"/>; an unchanged list changes nothing, so focus survives the once-a-second refresh.</summary>
    internal void Apply(IReadOnlyList<HistoryClearLine> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (shown is not null && shown.SequenceEqual(value)) return;
        shown = value.ToArray();
        if (lines.Count != shown.Length)
        {
            lines.Clear(); list.Children.Clear();
            foreach (var _ in shown) { var line = Ui.Text("", 12); lines.Add(line); list.Children.Add(line); }
        }
        for (int index = 0; index < shown.Length; index++) lines[index].Text = HistoryClearCopy.Line(shown[index]);
        Header.Text = HistoryClearCopy.Header(shown);
        string summary = HistoryClearCopy.Summary(shown);
        Summary.Text = summary; Summary.Visibility = summary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TryAgain.Visibility = shown.Any(line => line.CanRetry) ? Visibility.Visible : Visibility.Collapsed;
        if (!inWindow) Visibility = shown.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Retry()
    {
        foreach (var line in shown ?? []) if (line.CanRetry) session.RetryClear(line.Store);
        Refresh();
    }
}

/// <summary>Clear history: the apps and range to clear, the confirmation, then the progress view. Every brush is a theme token. It only
/// calls <see cref="UiSession"/>; the stores' work runs on their own workers.</summary>
internal sealed class ClearHistoryWindow : Window
{
    private const int MaximumLength = 260;
    private readonly UiSession session;
    private readonly AppSuggestions suggestions;
    private readonly StackPanel choices = new();
    private readonly DependencyPropertyDescriptor textProperty = DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox));
    private string[] offered = [];
    private bool typed, closed, modal;

    internal RadioButton OneApp { get; } = new() { Content = HistoryClearCopy.OneApp, GroupName = "Apps", IsChecked = true, Margin = new(0, 0, 16, 0) };
    internal RadioButton AllApps { get; } = new() { Content = HistoryClearCopy.AllApps, GroupName = "Apps" };
    internal ComboBox AppBox { get; } = new() { IsEditable = true, Margin = new(0, 8, 0, 0) };
    internal TextBlock Validation { get; } = Ui.Text(HistoryClearCopy.InvalidApp, 12, "Warning");
    internal RadioButton OlderThan7Days { get; } = new() { Content = HistoryClearCopy.OlderThan7Days, GroupName = "Range", IsChecked = true, Margin = new(0, 0, 16, 0) };
    internal RadioButton AllTime { get; } = new() { Content = HistoryClearCopy.AllTime, GroupName = "Range" };
    internal Button ClearButton { get; }
    internal Button CancelButton { get; }
    internal HistoryClearProgressView Progress { get; }
    /// <summary>Asks the question with this window as owner, so the confirmation closes with it; true for Yes.</summary>
    internal Func<Window, string, bool> Confirm { get; set; } = (owner, text)
        => MessageBox.Show(owner, text, "DeadWare", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    internal ClearHistoryWindow(UiSession session)
    {
        this.session = session;
        suggestions = new AppSuggestions(session, () => DateTimeOffset.UtcNow);
        Title = HistoryClearCopy.WindowTitle; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        Ui.Icon(this);
        var root = new StackPanel { Margin = new(20) };
        choices.Children.Add(Ui.Heading(HistoryClearCopy.WindowHeading, 20));
        foreach (string text in new[] { HistoryClearCopy.WindowHelp, HistoryClearCopy.WindowKept, HistoryClearCopy.WindowNotCleared, HistoryClearCopy.WindowFreedSpace, HistoryClearCopy.WindowKeepsRecording })
        { var block = Ui.Text(text, 12, "Muted"); block.Margin = new(0, 6, 0, 0); choices.Children.Add(block); }
        // Tab order: the Apps group, the app box, the Range group, Clear history, Cancel. Each group is one Tab stop; arrows move within it.
        choices.Children.Add(Group(HistoryClearCopy.AppsGroup, OneApp, AllApps));
        AutomationProperties.SetName(AppBox, HistoryClearCopy.AppBoxName);
        AppBox.Loaded += (_, _) => { if (AppBox.Template?.FindName("PART_EditableTextBox", AppBox) is TextBox box) box.MaxLength = MaximumLength; };
        AppBox.DropDownOpened += (_, _) => Offer();
        choices.Children.Add(AppBox);
        Validation.Margin = new(0, 4, 0, 0); Validation.Visibility = Visibility.Collapsed; choices.Children.Add(Validation);
        choices.Children.Add(Group(HistoryClearCopy.RangeGroup, OlderThan7Days, AllTime));
        // Clear history is not the default button, so Enter in the box never starts a clear; Esc cancels.
        ClearButton = Ui.Button(HistoryClearCopy.ClearButton, () => Submit());
        CancelButton = Ui.Button(HistoryClearCopy.CancelButton, Dismiss); CancelButton.IsCancel = true; CancelButton.Margin = new(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        buttons.Children.Add(ClearButton); buttons.Children.Add(CancelButton); choices.Children.Add(buttons);
        root.Children.Add(choices);
        Progress = new HistoryClearProgressView(session, closeButton: true) { Visibility = Visibility.Collapsed };
        Progress.Close!.Click += (_, _) => Dismiss();
        root.Children.Add(Progress);
        Content = root;

        foreach (var radio in new[] { OneApp, AllApps }) { radio.Checked += (_, _) => Validate(); }
        textProperty.AddValueChanged(AppBox, OnText);
        suggestions.Changed += Offer;
        Offer(); Validate();
        Loaded += (_, _) => _ = suggestions.RefreshAsync();
        Closed += (_, _) => { closed = true; textProperty.RemoveValueChanged(AppBox, OnText); suggestions.Changed -= Offer; Progress.Detach(); };
    }

    /// <summary>Shows the window modally (the entry points); its Esc and Close then end the dialog.</summary>
    internal bool? ShowModal() { modal = true; return ShowDialog(); }

    private static StackPanel Group(string title, RadioButton first, RadioButton second)
    {
        var group = new StackPanel { Margin = new(0, 14, 0, 0) };
        var label = Ui.Text(title, 13, "Heading", "HeadingFont"); label.Margin = new(0, 0, 0, 6); group.Children.Add(label);
        var radios = new StackPanel { Orientation = Orientation.Horizontal };
        radios.Children.Add(first); radios.Children.Add(second);
        AutomationProperties.SetName(radios, title);
        KeyboardNavigation.SetTabNavigation(radios, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(radios, KeyboardNavigationMode.Cycle);
        group.Children.Add(radios);
        return group;
    }

    private void OnText(object? sender, EventArgs e) { typed = true; Validate(); }

    private bool Valid(string text) => text.Length <= MaximumLength && AppExclusions.Accepts(text);

    /// <summary>Clear history is enabled for All apps, or for One app whose text passes <see cref="AppExclusions.Accepts"/>; once the
    /// user has typed, failing text shows the validation line.</summary>
    private void Validate()
    {
        bool one = OneApp.IsChecked == true;
        string text = AppBox.Text ?? "";
        bool valid = Valid(text);
        ClearButton.IsEnabled = !one || valid;
        Validation.Visibility = one && typed && text.Length > 0 && !valid ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Offers the suggestions with listed apps included, keeping whatever is typed in the box.</summary>
    private void Offer()
    {
        var names = suggestions.Names(includeListed: true).ToArray();
        if (names.SequenceEqual(offered, StringComparer.Ordinal)) return;
        offered = names;
        string text = AppBox.Text ?? "";
        AppBox.ItemsSource = names;
        if (AppBox.Text != text) AppBox.Text = text;
    }

    /// <summary>Asks for confirmation and, on Yes, sends the clear and shows the progress view. True when the clear was sent. Nothing is
    /// sent for No, when the window closed meanwhile or the session began ending (Quit, sign-out).</summary>
    internal bool Submit()
    {
        if (closed || !ClearButton.IsEnabled) return false;
        var now = DateTimeOffset.UtcNow;
        string? application = OneApp.IsChecked == true ? AppBox.Text ?? "" : null;
        var clear = HistoryClear.Create(application, AllTime.IsChecked == true, now);
        string? name = application is null ? null : ProcessHistory.SavedName(application);
        string question = HistoryClearCopy.Confirmation(clear, name, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        if (!Confirm(this, question)) return false;
        if (closed) return false;
        if (session.Ending) { Close(); return false; }
        bool sent = session.ClearHistory(clear);
        ShowProgress();
        return sent;
    }

    private void ShowProgress()
    {
        choices.Visibility = Visibility.Collapsed;
        CancelButton.IsCancel = false;
        var close = Progress.Close!;
        close.IsCancel = true;
        Progress.Visibility = Visibility.Visible;
        Progress.Refresh();
        if (!close.Focus()) FocusManager.SetFocusedElement(this, close);
    }

    private void Dismiss()
    {
        if (closed) return;
        // In a dialog, the IsCancel button's own dialog-cancel sets the same result again, which changes nothing.
        if (modal) DialogResult = false; else Close();
    }
}
