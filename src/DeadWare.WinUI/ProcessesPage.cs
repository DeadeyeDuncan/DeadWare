using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeadWare.WinUI;

/// <summary>
/// Applications by resource use: one row per application with its processes' totals, expanding into its processes,
/// in the columns chosen with the column picker, with search and click-to-sort headings. The selected row can be
/// ended or shown in File Explorer. While the pointer is over the table the rows keep their order.
/// </summary>
internal sealed class ProcessesPage : Page
{
    private const string WaitingForScan = "Waiting for the first process scan";
    private readonly MonitorSession session;
    private readonly Func<Task>? quit;
    private readonly IProcessControl control = new NativeProcessControl();
    private readonly TextBlock offText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock bannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock actionStatus = new() { TextWrapping = TextWrapping.Wrap }, status = Muted(), capNote = Muted(), count = Muted();
    private readonly TextBox search = new() { PlaceholderText = "Find app or PID", Width = 260 };
    private readonly Button endTask = new() { Content = "End task…", IsEnabled = false }, openLocation = new() { Content = "Open file location", IsEnabled = false };
    private readonly Button columnsButton = new() { Content = "Columns" }, restart = new() { Content = "Restart as administrator" };
    private readonly MenuFlyout picker = new();
    private readonly Border offCard, card = Pages.Card(new TableView([new("Name", 2)]));
    private readonly Grid live, banner = new() { ColumnSpacing = 12 };
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private IReadOnlyList<string> shown = [];
    private IReadOnlyList<ProcessTableRow> tree = [];
    private TableView? table;
    private string? sortKey;
    private bool sortDescending, acting, loaded;

    public ProcessesPage(MonitorSession session, Func<Task>? quit = null)
    {
        this.session = session;
        this.quit = quit;

        // Off state.
        var turnOn = new Button { Content = "Turn on", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        turnOn.Click += (_, _) => { session.SetProcessMonitoring(true); Refresh(resort: true); };
        offCard = Pages.Card(new StackPanel { Spacing = 12, Padding = new Thickness(8), Children = { offText, turnOn } });
        offCard.VerticalAlignment = VerticalAlignment.Top;

        // Top strip: search and the actions, one banner when administrator rights limit the table, then the action result, scan status and cap note.
        AutomationProperties.SetName(search, "Find app or PID");
        ToolTipService.SetToolTip(search, "Search app name or PID");
        endTask.Click += (_, _) => EndTask();
        openLocation.Click += (_, _) => OpenLocation();
        columnsButton.Flyout = picker;
        picker.Opening += (_, _) => FillPicker();
        var tools = new Grid { ColumnSpacing = 8 };
        tools.ColumnDefinitions.Add(new ColumnDefinition()); tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        search.HorizontalAlignment = HorizontalAlignment.Left;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { endTask, openLocation, columnsButton } };
        Grid.SetColumn(actions, 1);
        tools.Children.Add(search); tools.Children.Add(actions);
        banner.ColumnDefinitions.Add(new ColumnDefinition()); banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(restart, 1);
        banner.Children.Add(bannerText); banner.Children.Add(restart);
        restart.Visibility = quit is null ? Visibility.Collapsed : Visibility.Visible;
        restart.Click += async (_, _) =>
        {
            if (this.quit is null) return;
            try { Note(actionStatus, await session.RestartElevatedAsync(this.quit) ?? ""); }
            catch (Exception error) { Note(actionStatus, $"Could not restart as administrator ({error.Message})."); }
        };
        var strip = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { tools, banner, actionStatus, status, capNote } });
        Note(actionStatus, "");

        var footer = new StackPanel { Spacing = 3 };
        foreach (string line in ProcessCopy.Footer(ProcessFeatures.DiskColumns)) footer.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 });
        var about = new Expander { Header = "About these columns", Content = footer, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };

        live = new Grid { RowSpacing = 12 };
        live.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        live.RowDefinitions.Add(new RowDefinition());
        live.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        live.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(card, 1); Grid.SetRow(count, 2); Grid.SetRow(about, 3);
        live.Children.Add(strip); live.Children.Add(card); live.Children.Add(count); live.Children.Add(about);

        var root = new Grid { Padding = new Thickness(24) };
        root.Children.Add(offCard); root.Children.Add(live);
        Content = root;

        search.TextChanged += (_, _) => Refresh(resort: true);
        // Ctrl+F focuses the search box and F5 re-sorts at once, even while the order is held.
        var find = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += (_, e) => { search.Focus(FocusState.Keyboard); search.SelectAll(); e.Handled = true; };
        var resortNow = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F5 };
        resortNow.Invoked += (_, e) => { Refresh(resort: true); e.Handled = true; };
        KeyboardAccelerators.Add(find); KeyboardAccelerators.Add(resortNow);

        this.WhileLoaded(() => { loaded = true; session.Changed += OnChanged; Refresh(resort: true); }, () => { loaded = false; session.Changed -= OnChanged; });
    }

    private static TextBlock Muted() => new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };

    private static void Note(TextBlock note, string text) { note.Text = text; note.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }

    private void OnChanged() => Refresh(resort: false);

    private bool Elevated => session.Current?.Elevated ?? Collector.IsElevated;

    /// <summary>Every column except NAME, ticked when shown; a column that needs administrator rights is listed but disabled without them.</summary>
    private void FillPicker()
    {
        picker.Items.Clear();
        foreach (var choice in ProcessTableColumns.Picker(session.Settings.ProcessColumns, Elevated, ProcessFeatures.DiskColumns))
        {
            string key = choice.Key;
            var item = new ToggleMenuFlyoutItem { Text = choice.Label, IsChecked = choice.Checked, IsEnabled = choice.Enabled };
            // Only this key changes, so keys the menu disables or leaves out stay saved.
            item.Click += (_, _) => { session.SetProcessColumns(ProcessColumnKeys.Toggle(session.Settings.ProcessColumns, key, item.IsChecked)); Refresh(resort: true); };
            picker.Items.Add(item);
        }
    }

    /// <summary>Draws the page from the latest reading. Rows re-sort unless the pointer is over the table; <paramref name="resort"/> re-sorts regardless.</summary>
    private void Refresh(bool resort)
    {
        var reading = session.Current;
        bool elevated = Elevated, on = session.Settings.ProcessMonitoring;
        offText.Text = ProcessCopy.OffTitle + " " + ProcessCopy.ScanSentence(elevated, ProcessFeatures.DiskColumns, ProcessFeatures.ScanLoop, history: true);
        offCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        live.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { UpdateActions(); return; }

        string? limit = ProcessCopy.Banner(elevated, ProcessFeatures.DiskColumns);
        bannerText.Text = limit ?? "";
        banner.Visibility = limit is null ? Visibility.Collapsed : Visibility.Visible;

        var visible = ProcessTableColumns.Visible(session.Settings.ProcessColumns, elevated, ProcessFeatures.DiskColumns);
        if (table is null || !visible.SequenceEqual(shown)) { Rebuild(visible); resort = true; }
        var sort = ProcessTableColumns.EffectiveSort(sortKey, sortDescending, visible);
        table!.SetSort(ProcessTablePresenter.Keys(visible).ToList().IndexOf(sort.Key), sort.Descending);

        var activity = reading?.ProcessActivity ?? ProcessActivity.None;
        Note(status, activity.Kind switch
        {
            ProcessActivityKind.Unavailable => activity.Scan.Status,
            ProcessActivityKind.Off or ProcessActivityKind.None => WaitingForScan,
            _ => "",
        });
        Note(capNote, activity.TotalProcesses > activity.Processes.Count && activity.Kind == ProcessActivityKind.Available ? ProcessCopy.CapNote(activity.TotalProcesses) : "");
        // Only a published scan replaces the rows; a transient unavailable reading keeps the last ones on screen.
        if (activity.Kind != ProcessActivityKind.Available) { UpdateActions(); return; }

        var keep = !resort && table.PointerOver ? tree.Where(row => row.IsApp).Select(row => row.Row.Key).ToList() : null;
        tree = ProcessTablePresenter.Tree(activity.Processes, visible, sortKey, sortDescending, search.Text, expanded, session.IsExcluded, keep);
        // An application that is gone, or no longer has several processes, is no longer expanded.
        if (search.Text.Length == 0) expanded.IntersectWith(tree.Where(row => row.Expandable).Select(row => row.Row.Key));
        table.SetRows(tree.Select(row => row.Row).ToList());
        if (table.SelectedKey is { } selected && tree.All(row => row.Row.Key != selected)) table.SelectedKey = null;
        int apps = tree.Count(row => row.IsApp);
        count.Text = (apps == 0 ? (search.Text.Length > 0 ? "No applications match your search." : "No processes reported.") : $"{apps} applications")
            + (session.Stale ? " · " + ProcessCopy.Stale : "");
        UpdateActions();
    }

    /// <summary>The chosen columns changed: build a table with those columns and carry the sort and the selection over.</summary>
    private void Rebuild(IReadOnlyList<string> visible)
    {
        string? selected = table?.SelectedKey;
        shown = visible.ToList();
        var keys = ProcessTablePresenter.Keys(visible);
        var headers = ProcessTablePresenter.Headers(visible);
        var built = new TableView(keys.Select((key, i) => new TableColumn(headers[i], key == "name" ? 2.4 : key == "pid" ? 0.7 : 1, Numeric: key is not ("name" or "gpuengine"))).ToList()) { Selectable = true };
        AutomationProperties.SetName(built, "Processes");
        built.SelectedKey = selected;
        built.HeaderClicked += column =>
        {
            var current = ProcessTableColumns.EffectiveSort(sortKey, sortDescending, shown);
            (sortKey, sortDescending) = ProcessTablePresenter.Clicked(current.Key, current.Descending, keys[column]);
            Refresh(resort: true);
        };
        built.SelectionChanged += UpdateActions;
        // The marker at the start of the name opens and closes an application; so does a double-click anywhere on its row.
        built.RowClicked += (key, column, x) => { if (column == 0 && x < 26) Toggle(key); };
        built.RowDoubleClicked += Toggle;
        // Left closes an open application (or goes to a process's application), Right opens one, Delete ends the task, Esc clears the selection.
        built.KeyDown += (_, e) =>
        {
            if (e.Handled) return;
            var row = Selection;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left when row is { Expandable: true, Open: true }: Toggle(row.Row.Key); break;
                case Windows.System.VirtualKey.Left when row is { ParentKey: { } parent }: built.SelectedKey = parent; break;
                case Windows.System.VirtualKey.Right when row is { Expandable: true, Open: false }: Toggle(row.Row.Key); break;
                case Windows.System.VirtualKey.Delete when row is not null: EndTask(); break;
                case Windows.System.VirtualKey.Escape when row is not null: built.SelectedKey = null; break;
                default: return;
            }
            e.Handled = true;
        };
        built.RowContext += (key, element, point) =>
        {
            var menu = new MenuFlyout();
            var end = new MenuFlyoutItem { Text = "End task…", IsEnabled = !acting };
            end.Click += (_, _) => EndTask();
            var open = new MenuFlyoutItem { Text = "Open file location", IsEnabled = !acting };
            open.Click += (_, _) => OpenLocation();
            menu.Items.Add(end); menu.Items.Add(open);
            if (Find(key) is { Expandable: true } app)
            {
                var toggle = new MenuFlyoutItem { Text = app.Open ? "Collapse" : "Expand" };
                toggle.Click += (_, _) => Toggle(key);
                menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(toggle);
            }
            menu.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        };
        table = built;
        card.Child = built;
    }

    private ProcessTableRow? Find(string? key) => key is null ? null : tree.FirstOrDefault(row => row.Row.Key == key);

    private ProcessTableRow? Selection => Find(table?.SelectedKey);

    /// <summary>Opens or closes an application that has processes to expand into; the rows keep their order.</summary>
    private void Toggle(string key)
    {
        if (Find(key) is not { Expandable: true }) return;
        if (!expanded.Add(key)) expanded.Remove(key);
        // Keep the applications where they are, whether or not the pointer is still over the table.
        var keep = tree.Where(row => row.IsApp).Select(row => row.Row.Key).ToList();
        if (session.Current?.ProcessActivity is not { Kind: ProcessActivityKind.Available } activity) return;
        tree = ProcessTablePresenter.Tree(activity.Processes, shown, sortKey, sortDescending, search.Text, expanded, session.IsExcluded, keep);
        table?.SetRows(tree.Select(row => row.Row).ToList());
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool can = !acting && Selection is not null && session.Settings.ProcessMonitoring;
        endTask.IsEnabled = can; openLocation.IsEnabled = can;
    }

    /// <summary>An application row ends every process of the application; a process row ends that process.</summary>
    private void EndTask()
    {
        if (acting || Selection is not { } row) return;
        Act(Actions().EndAsync(row.Name, row.Members, row.IsApp));
    }

    private void OpenLocation()
    {
        if (acting || Selection is not { } row) return;
        Act(Actions().OpenLocationAsync(row.Members));
    }

    private ProcessActions Actions() => new(control, Ask, ProcessActions.Reveal, Environment.ProcessId, Elevated);

    /// <summary>
    /// End task's confirmation, asked from the action's own thread: a dialog whose default button is No, shown on the
    /// UI thread while that thread waits. Anything that stops the dialog from showing counts as No.
    /// </summary>
    private bool Ask(string question, string? summary)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!loaded || XamlRoot is null) { answer.TrySetResult(false); return; }
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "DeadWare",
                    Content = new TextBlock { Text = question + (summary is null ? "" : "\n\n" + summary), TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "Yes", CloseButtonText = "No", DefaultButton = ContentDialogButton.Close,
                };
                answer.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
            }
            catch (Exception) { answer.TrySetResult(false); }
        });
        return queued && answer.Task.GetAwaiter().GetResult();
    }

    /// <summary>Follows an action running off the UI thread: both actions stay disabled until it finishes, then its status text is shown ("" clears it).</summary>
    private async void Act(Task<string> action)
    {
        acting = true; UpdateActions();
        string result = "";
        try { result = await action; }
        catch (Exception) { }
        acting = false;
        Note(actionStatus, result);
        UpdateActions();
    }
}
