using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using DeadWare.Core;
using LayoutGrid=System.Windows.Controls.Grid;

namespace DeadWare.App;

/// <summary>One cell of a Processes row: its text, "—" when the value is unknown, and the tooltip naming why (null for a known value).</summary>
internal sealed record ProcessCell(string Text,string? Tip);

/// <summary>One Processes table row: an app summed over its processes, or one process of an expanded app.</summary>
internal sealed class ProcessRowView(string id,string? parentId) : INotifyPropertyChanged
{
    private bool expandable,expanded;
    private string name="",spoken="";
    private IReadOnlyDictionary<string,ProcessCell> cells=new Dictionary<string,ProcessCell>();
    /// <summary>"app:" and the app key for an app row; "process:" with the PID and generation for a process row.</summary>
    public string Id {get;}=id;
    /// <summary>The app row a process row belongs to; null for an app row.</summary>
    public string? ParentId {get;}=parentId;
    public bool IsApp=>ParentId is null;
    /// <summary>An app with more than one process; only these show a chevron.</summary>
    public bool Expandable=>expandable;
    public bool Expanded=>expanded;
    public string Name=>name;
    /// <summary>The chevron's automation name.</summary>
    public string ChevronName=>(expanded?"Collapse ":"Expand ")+name;
    /// <summary>One display string and one tooltip per column key, shown or not.</summary>
    public IReadOnlyDictionary<string,ProcessCell> Cells=>cells;
    /// <summary>The row's automation name: its name, process count or PID, then the shown columns in order.</summary>
    public string Spoken=>spoken;
    /// <summary>What End task… and Open file location act on: every process of an app, or this one process, as (PID, generation).</summary>
    internal IReadOnlyList<(int Pid,long Generation)> Members {get;set;}=[];
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(string name,bool expandable,bool expanded,IReadOnlyDictionary<string,ProcessCell> cells,string spoken)
    {
        bool chevron=false;
        if(this.name!=name){this.name=name;Raise(nameof(Name));chevron=true;}
        if(this.expandable!=expandable){this.expandable=expandable;Raise(nameof(Expandable));}
        if(this.expanded!=expanded){this.expanded=expanded;Raise(nameof(Expanded));chevron=true;}
        if(chevron)Raise(nameof(ChevronName));
        if(cells.Count!=this.cells.Count||cells.Any(c=>!this.cells.TryGetValue(c.Key,out var old)||old!=c.Value)){this.cells=cells;Raise(nameof(Cells));}
        if(this.spoken!=spoken){this.spoken=spoken;Raise(nameof(Spoken));}
    }
    private void Raise(string property)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(property));
}

/// <summary>Live per-app CPU, memory, I/O, disk, network and GPU (spec: Processes page). Apps sum their processes and expand into them;
/// the shown columns, the picker and the banner follow the reading's Elevated flag, and every "—" carries its reason as a tooltip.
/// The order stays calm: rows re-sort for a new reading only outside a hold, and End task… and Open file location run off the UI thread.</summary>
internal sealed class ProcessesPage : UserControl
{
    private const string Dash="—",WaitingForScan="Waiting for the first process scan";
    private readonly UiSession session;
    private readonly bool diskColumns;
    /// <summary>Process access for End task… and Open file location.</summary>
    private readonly IProcessControl control;
    private readonly ObservableCollection<ProcessRowView> rows=[];
    private readonly Dictionary<string,ProcessRowView> views=new(StringComparer.Ordinal);
    private readonly HashSet<string> expanded=new(StringComparer.Ordinal);
    private readonly Dictionary<string,DataGridColumn> columns=new(StringComparer.Ordinal);
    private readonly Dictionary<string,MenuItem> pickerItems=new(StringComparer.Ordinal);
    private readonly ContextMenu picker=new() {Template=PickerTemplate};
    private readonly TextBox search=new() {Width=240};
    private readonly TextBlock offText=Ui.Text("",13),bannerText=Ui.Text("",12,"Warning"),status=Ui.Text("",11,"Muted"),capNote=Ui.Text("",11,"Muted");
    /// <summary>End task's and Open file location's result, under the top strip; collapsed while empty.</summary>
    private readonly TextBlock actionStatus=Ui.Text("",12);
    private readonly Border offCard;
    private readonly DockPanel live=new(),banner=new() {Margin=new Thickness(0,8,0,0)};
    private readonly StackPanel footer=new() {Margin=new Thickness(8,10,8,0)};
    private readonly Button endTask,openLocation;
    private string sortKey="cpu";
    private bool sortDescending=true;
    /// <summary>The sort and the published scan the rows were last ordered for; a new Sequence is a new reading.</summary>
    private (string Key,bool Descending)? shownSort;
    private long? shownSequence;
    private readonly OrderHold hold=new();
    /// <summary>The key hold's clock: monotonic, so a wall-clock change can neither extend nor end it.</summary>
    private readonly Stopwatch clock=Stopwatch.StartNew();
    /// <summary>An action is running; both actions stay disabled until its result is shown.</summary>
    private bool acting;
    /// <summary>Confirmations open now. End task asks on its own thread, so this is written there and read on the UI thread.</summary>
    private int dialogs;
    /// <summary>The Processes table; its items are <see cref="ProcessRowView"/>s.</summary>
    internal DataGrid Grid {get;}=new();
    /// <summary>Asks before ending (question, optional summary); the only confirmation hook. Defaults to a Yes/No box whose default is No.</summary>
    internal Func<string,string?,bool> Confirm {get;set;}

    public ProcessesPage(UiSession session):this(session,ProcessFeatures.DiskColumns,null){}

    internal ProcessesPage(UiSession session,bool diskColumns,IProcessControl? control)
    {
        this.session=session;this.diskColumns=diskColumns;this.control=control??new NativeProcessControl();
        Confirm=ProcessActions.MessageBoxConfirm(Dispatcher);
        // Off state.
        var turnOn=Labelled(Ui.Button("Turn on",TurnOn));turnOn.HorizontalAlignment=HorizontalAlignment.Left;turnOn.Margin=new Thickness(0,12,0,0);
        var offBody=new StackPanel();offBody.Children.Add(offText);offBody.Children.Add(turnOn);
        offCard=Ui.Panel(offBody,new Thickness(18,16,18,16));DockPanel.SetDock(offCard,Dock.Top);
        // Top strip: search, actions and the column picker, then one banner when administrator rights limit the table.
        AutomationProperties.SetName(search,"Find app or PID");search.ToolTip="Search app name or PID";
        var placeholder=Ui.Text("Find app or PID",12,"Muted");placeholder.TextWrapping=TextWrapping.NoWrap;placeholder.IsHitTestVisible=false;placeholder.Margin=new Thickness(11,0,0,0);
        search.TextChanged+=(_,_)=>{placeholder.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;Render(resort:true);};
        var searchHost=new LayoutGrid {HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};searchHost.Children.Add(search);searchHost.Children.Add(placeholder);
        // Both actions act on the selected row; with nothing selected they stay disabled.
        endTask=Labelled(new Button {Content="End task…",IsEnabled=false});openLocation=Labelled(new Button {Content="Open file location",IsEnabled=false});
        endTask.Click+=(_,_)=>EndTask();openLocation.Click+=(_,_)=>OpenLocation();
        Button columnsButton=null!;columnsButton=Labelled(Ui.Button("Columns",()=>OpenPicker(columnsButton)));
        var actions=Ui.Row(endTask,openLocation,columnsButton);DockPanel.SetDock(actions,Dock.Right);
        var tools=new DockPanel();tools.Children.Add(actions);tools.Children.Add(searchHost);
        var restart=Labelled(Ui.Button("Restart as administrator",session.RestartElevated));DockPanel.SetDock(restart,Dock.Right);
        banner.Children.Add(restart);banner.Children.Add(bannerText);
        var stripBody=new StackPanel();stripBody.Children.Add(tools);stripBody.Children.Add(banner);
        var strip=Ui.Panel(stripBody,new Thickness(14,10,14,10));strip.Margin=new Thickness(0,2,0,10);DockPanel.SetDock(strip,Dock.Top);
        actionStatus.Margin=new Thickness(4,0,4,8);DockPanel.SetDock(actionStatus,Dock.Top);Note(actionStatus,"");
        status.Margin=new Thickness(4,0,4,8);DockPanel.SetDock(status,Dock.Top);
        capNote.Margin=new Thickness(4,0,4,8);DockPanel.SetDock(capNote,Dock.Top);
        foreach(string line in ProcessCopy.Footer(diskColumns)){var text=Ui.Text(line,11,"Muted");text.Margin=new Thickness(0,0,0,3);footer.Children.Add(text);}
        DockPanel.SetDock(footer,Dock.Bottom);
        // Picker: every column except NAME, in the picker's order; Columns and a header right-click open it.
        AutomationProperties.SetName(picker,"Columns");
        foreach(var choice in ProcessTableColumns.Picker(session.Settings.ProcessColumns,false,diskColumns))
        {
            string key=choice.Key;
            var item=new MenuItem {IsCheckable=true,StaysOpenOnClick=true};item.SetResourceReference(StyleProperty,"ThemeMenuItem");
            // WPF flips IsChecked before Click; only this key changes, so keys the menu disables or leaves out stay saved.
            item.Click+=(_,_)=>{session.SetProcessColumns(ProcessColumnKeys.Toggle(session.Settings.ProcessColumns,key,item.IsChecked));Refresh();};
            pickerItems[key]=item;picker.Items.Add(item);
        }
        // Table.
        AutomationProperties.SetName(Grid,"Processes");
        Grid.ItemsSource=rows;Grid.SelectionMode=DataGridSelectionMode.Single;Grid.CanUserReorderColumns=false;Grid.CanUserResizeRows=false;
        var header=new Style(typeof(DataGridColumnHeader),TryFindResource(typeof(DataGridColumnHeader)) as Style);header.Setters.Add(new Setter(ContextMenuProperty,picker));Grid.ColumnHeaderStyle=header;
        var row=new Style(typeof(DataGridRow),TryFindResource(typeof(DataGridRow)) as Style);row.Setters.Add(new Setter(AutomationProperties.NameProperty,new Binding(nameof(ProcessRowView.Spoken))));Grid.RowStyle=row;
        // Pixel scrolling keeps virtualization and makes the kept offset a distance, not a row count.
        VirtualizingPanel.SetScrollUnit(Grid,ScrollUnit.Pixel);
        Grid.Sorting+=OnSorting;
        Grid.AddHandler(ButtonBase.ClickEvent,new RoutedEventHandler(OnChevron),true);
        Grid.SelectionChanged+=(_,_)=>UpdateActions();
        Grid.MouseEnter+=(_,_)=>hold.PointerOver=true;
        Grid.MouseLeave+=(_,_)=>hold.PointerOver=false;
        var table=Ui.Panel(Grid,new Thickness(0));table.MinHeight=72;
        live.Children.Add(strip);live.Children.Add(actionStatus);live.Children.Add(status);live.Children.Add(capNote);live.Children.Add(footer);live.Children.Add(table);
        var root=new DockPanel {Margin=new Thickness(6,0,6,6)};root.Children.Add(offCard);root.Children.Add(live);
        Content=root;
        // Keys: the table's own (Left, Right, Enter, Delete and the navigation hold) come first, before the DataGrid acts on them; Ctrl+F, F5 and Esc work anywhere on the page, and MainWindow sends them from its rail and header.
        PreviewKeyDown+=OnPreviewKey;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find,(_,_)=>FocusSearch()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find,Key.F,ModifierKeys.Control));
        Loaded+=(_,_)=>{session.Changed+=OnChanged;Refresh();};
        Unloaded+=(_,_)=>{session.Changed-=OnChanged;hold.PointerOver=false;};
        Refresh();
    }

    private void OnChanged()
    {
        if(Dispatcher.HasShutdownStarted)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.InvokeAsync(OnChanged);return;}
        if(IsVisible)Refresh();
    }

    private void TurnOn(){session.SetProcessMonitoring(true);Refresh();}

    private void OpenPicker(UIElement target){picker.PlacementTarget=target;picker.Placement=PlacementMode.Bottom;picker.IsOpen=true;}

    /// <summary>Renders the page from <see cref="UiSession.Current"/> and the settings; the <see cref="UiSession.Changed"/> handler.
    /// Rows re-sort only for a new reading outside a hold, or when the sort changes; otherwise they keep their order and update in place.</summary>
    internal void Refresh()=>Render(resort:false);

    /// <summary>Renders the page; <paramref name="resort"/> re-sorts at once, even during a hold (F5, a header click, a search change).</summary>
    private void Render(bool resort)
    {
        var reading=session.Current;
        // Hiding, the picker and the banner follow the published reading; before the first one, this process's own elevation.
        bool elevated=reading?.Elevated??Collector.IsElevated;
        bool on=session.Settings.ProcessMonitoring;
        offText.Text=ProcessCopy.OffTitle+" "+ProcessCopy.ScanSentence(elevated,diskColumns,ProcessFeatures.ScanLoop,history:true);
        offCard.Visibility=on?Visibility.Collapsed:Visibility.Visible;
        live.Visibility=on?Visibility.Visible:Visibility.Collapsed;
        foreach(var choice in ProcessTableColumns.Picker(session.Settings.ProcessColumns,elevated,diskColumns))
        {
            var item=pickerItems[choice.Key];
            item.Header=choice.Label;item.IsChecked=choice.Checked;item.IsEnabled=choice.Enabled;
        }
        if(!on){UpdateActions();return;}
        string? limit=ProcessCopy.Banner(elevated,diskColumns);
        bannerText.Text=limit??"";banner.Visibility=limit is null?Visibility.Collapsed:Visibility.Visible;
        var visible=ProcessTableColumns.Visible(session.Settings.ProcessColumns,elevated,diskColumns);
        (sortKey,sortDescending)=ProcessTableColumns.EffectiveSort(sortKey,sortDescending,visible);
        // A sort the rows are not in yet (a hidden sort column falling back) re-sorts at once, like a header click.
        resort|=shownSort!=(sortKey,sortDescending);
        shownSort=(sortKey,sortDescending);
        ShowColumns(visible);
        var activity=reading?.ProcessActivity??ProcessActivity.None;
        // A reading is new only when its Sequence changed, so a re-render of the same one (Loaded after Changed, the stale timer) never re-sorts.
        if(activity.Kind==ProcessActivityKind.Available&&activity.Sequence!=shownSequence)
        {
            shownSequence=activity.Sequence;
            hold.MenuOpen=picker.IsOpen||Volatile.Read(ref dialogs)>0;
            resort|=!hold.IsHeld(clock.Elapsed.TotalSeconds);
        }
        // Monitoring is on but no scan has been published yet (no reading, or one taken while it was off): say so rather than show a bare table.
        Note(status,activity.Kind switch
        {
            ProcessActivityKind.Unavailable=>activity.Scan.Status,
            ProcessActivityKind.Off or ProcessActivityKind.None=>WaitingForScan,
            _=>"",
        });
        Note(capNote,activity.TotalProcesses>ProcessActivityTracker.MaximumPublished?ProcessCopy.CapNote(activity.TotalProcesses):"");
        ShowRows(activity,visible,session.Stale,resort);
        UpdateActions();
    }

    private static void Note(TextBlock note,string text){note.Text=text;note.Visibility=text.Length==0?Visibility.Collapsed:Visibility.Visible;}

    /// <summary>NAME, then the shown keys in their fixed order; columns are kept per key, so unchanged headers survive a picker toggle.</summary>
    private void ShowColumns(IReadOnlyList<string> visible)
    {
        var wanted=visible.Prepend("name").Select(Column).ToList();
        for(int i=Grid.Columns.Count-1;i>=0;i--)if(!wanted.Contains(Grid.Columns[i]))Grid.Columns.RemoveAt(i);
        for(int i=0;i<wanted.Count;i++)
        {
            int at=Grid.Columns.IndexOf(wanted[i]);
            if(at==i)continue;
            if(at>=0)Grid.Columns.Move(at,i);else Grid.Columns.Insert(i,wanted[i]);
        }
        foreach(var column in Grid.Columns)
            column.SortDirection=column.SortMemberPath!=sortKey?null:sortDescending?ListSortDirection.Descending:ListSortDirection.Ascending;
    }

    private DataGridColumn Column(string key)
    {
        if(columns.TryGetValue(key,out var column))return column;
        if(key=="name")column=new DataGridTemplateColumn {Header=ProcessTableColumns.Header(key),CellTemplate=NameTemplate,SortMemberPath=key,Width=new DataGridLength(2.4,DataGridLengthUnitType.Star),MinWidth=150};
        else
        {
            var cell=new Style(typeof(DataGridCell),TryFindResource(typeof(DataGridCell)) as Style);
            cell.Setters.Add(new Setter(ToolTipProperty,new Binding($"Cells[{key}].Tip")));
            column=new DataGridTextColumn {Header=ProcessTableColumns.Header(key),Binding=new Binding($"Cells[{key}].Text"),SortMemberPath=key,
                Width=new DataGridLength(key=="pid"?.7:1,DataGridLengthUnitType.Star),MinWidth=64,CellStyle=cell,ElementStyle=Ui.ValueText};
        }
        columns[key]=column;
        return column;
    }

    /// <summary>A header click sorts by its column: numeric columns descending first, the text columns NAME and GPU ENGINE ascending first; clicking the sorted column flips it.</summary>
    private void OnSorting(object? sender,DataGridSortingEventArgs e)
    {
        e.Handled=true;
        string key=e.Column.SortMemberPath;
        if(key==sortKey)sortDescending=!sortDescending;
        else {sortKey=key;sortDescending=!ProcessTableColumns.AscendingFirst(key);}
        Render(resort:true);
    }

    private void OnChevron(object sender,RoutedEventArgs e)
    {
        if(e.OriginalSource is not ToggleButton {DataContext:ProcessRowView {IsApp:true,Expandable:true} app})return;
        e.Handled=true;
        Expand(app,null);
    }

    /// <summary>Expands (true), collapses (false) or toggles (null) an app that has processes to expand into; only that app's process rows come or go.</summary>
    private void Expand(ProcessRowView? row,bool? open)
    {
        if(row is not {IsApp:true,Expandable:true})return;
        bool changed=open??!expanded.Contains(row.Id)?expanded.Add(row.Id):expanded.Remove(row.Id);
        if(changed)Refresh();
    }

    private ProcessRowView? Selection=>Grid.SelectedItem as ProcessRowView;

    /// <summary>Left and Right collapse and expand, Enter toggles, Delete opens End task…, a navigation key holds the order for 3 s; Esc clears the search and F5 re-sorts.
    /// The table's keys are taken in the preview, before the DataGrid moves its cell or swallows Delete; the search box and the buttons keep theirs,
    /// and keys in the column picker (its own popup) stay the menu's, so Esc still closes it.</summary>
    private void OnPreviewKey(object sender,KeyEventArgs e)
    {
        if(e.OriginalSource is not Visual source||!source.IsDescendantOf(this))return;
        if((e.Key is Key.F5 or Key.Escape)&&PageKey(e.Key,Keyboard.Modifiers)){e.Handled=true;return;}
        if(source!=Grid&&!source.IsDescendantOf(Grid))return;
        switch(e.Key)
        {
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End:
                hold.KeyNavigated(clock.Elapsed.TotalSeconds);
                return;
            case Key.Right:Expand(Selection,true);break;
            // On a process row, Left goes back to its app and collapses it, so the selection stays on a shown row.
            case Key.Left when Selection is {ParentId:{} parent}&&views.TryGetValue(parent,out var app):Grid.SelectedItem=app;Expand(app,false);break;
            case Key.Left:Expand(Selection,false);break;
            case Key.Enter:Expand(Selection,null);break;
            case Key.Delete:EndTask();break;
            default:return;
        }
        e.Handled=true;
    }

    /// <summary>The page's keys that work from anywhere it is shown: Ctrl+F (no other modifier) focuses the search box, F5 re-sorts, Esc clears a search that is not empty.
    /// True when the key acted; false for every other key, so it goes on to whoever else handles it.</summary>
    internal bool PageKey(Key key,ModifierKeys modifiers)
    {
        switch(key)
        {
            case Key.F when modifiers==ModifierKeys.Control:FocusSearch();return true;
            case Key.F5:Render(resort:true);return true;
            case Key.Escape when search.Text.Length>0:search.Text="";return true;
            default:return false;
        }
    }

    /// <summary>Ctrl+F. When keyboard focus cannot move (the window is not active), the search box still becomes its scope's focused element, so it has focus once the window does.</summary>
    private void FocusSearch()
    {
        if(!search.Focus())FocusManager.SetFocusedElement(FocusManager.GetFocusScope(search),search);
        search.SelectAll();
    }

    private void UpdateActions(){bool can=!acting&&Selection is not null;endTask.IsEnabled=can;openLocation.IsEnabled=can;}

    /// <summary>An app row ends every process of the app; a process row ends that process.</summary>
    private void EndTask()
    {
        if(acting||Selection is not {} row)return;
        Act(Actions().EndAsync(row.Name,row.Members,row.IsApp));
    }

    private void OpenLocation()
    {
        if(acting||Selection is not {} row)return;
        Act(Actions().OpenLocationAsync(row.Members));
    }

    /// <summary>Elevation follows the reading, as the columns do; outside UI checks it is this process's own.</summary>
    private ProcessActions Actions()=>new(control,Ask,ProcessActions.Reveal,Environment.ProcessId,session.Current?.Elevated??Collector.IsElevated);

    /// <summary>The confirmation End task asks on its own thread; the order holds while it is open.</summary>
    private bool Ask(string question,string? summary)
    {
        Interlocked.Increment(ref dialogs);
        try{return Confirm(question,summary);}
        finally{Interlocked.Decrement(ref dialogs);}
    }

    /// <summary>Follows an action running off the UI thread: both actions stay disabled until it finishes, then its status text replaces the line under the top strip ("" clears it).</summary>
    private void Act(Task<string> action)
    {
        acting=true;UpdateActions();
        _=action.ContinueWith(done=>Dispatcher.InvokeAsync(()=>
        {
            acting=false;
            Note(actionStatus,done.IsCompletedSuccessfully?done.Result:"");
            UpdateActions();
        }),TaskScheduler.Default);
    }

    private void ShowRows(ProcessActivity activity,IReadOnlyList<string> visible,bool stale,bool resort)
    {
        var groups=ProcessGrouping.Group(activity.Processes);
        // Only a published scan says which apps are gone; a transient Off or Unavailable reading keeps the expanded rows.
        if(activity.Kind==ProcessActivityKind.Available)expanded.IntersectWith(groups.Select(g=>AppId(g.Key)));
        var apps=groups.Where(g=>ProcessGrouping.Matches(g,search.Text)).ToList();
        apps.Sort((a,b)=>ProcessSort.Compare(a,b,sortKey,sortDescending));
        var byProcess=Comparer<ProcessSample>.Create((a,b)=>ProcessSort.Compare(a,b,sortKey,sortDescending));
        var shown=new List<ProcessRowView>();
        foreach(var app in apps)
        {
            string id=AppId(app.Key);
            bool expandable=app.Members.Count>1,open=expandable&&expanded.Contains(id);
            // A single-process app is its process: it shows the PID inline and has no chevron.
            string pid=expandable?"":Pid(app.Members[0]);
            var cells=Cells(activity,key=>ProcessGrouping.Value(app,key),key=>Gap(app,key),pid,ProcessGrouping.EngineText(app),app.GpuBusiest,stale);
            var view=View(id,null);
            view.Update(app.Name,expandable,open,cells,Spoken(app.Name,expandable?$"{app.Members.Count} processes":"PID "+pid,visible,cells));
            view.Members=[..app.Members.Select(m=>(m.Pid,m.Generation))];
            shown.Add(view);
            if(!open)continue;
            foreach(var process in app.Members.Order(byProcess))
            {
                string name=UsageMetrics.NormalizeName(process.Name),processPid=Pid(process);
                var processCells=Cells(activity,key=>ProcessGrouping.Value(process,key),key=>Gap(process,key),processPid,ProcessGrouping.EngineText(process),process.GpuEngines?.FirstOrDefault(),stale);
                var child=View(string.Create(CultureInfo.InvariantCulture,$"process:{process.Pid}:{process.Generation}"),id);
                child.Update(name,false,false,processCells,Spoken(name,"PID "+processPid,visible,processCells));
                child.Members=[(process.Pid,process.Generation)];
                shown.Add(child);
            }
        }
        foreach(string gone in views.Keys.Except(shown.Select(v=>v.Id)).ToArray())views.Remove(gone);
        // Outside a re-sort the rows keep their order: exited rows leave, new apps go to the bottom, new processes after their app's rows.
        var sorted=shown.Select(v=>v.Id).ToList();
        Show(resort?sorted:Grouped(CalmOrderPlanner.Held([..rows.Select(r=>r.Id)],sorted,ParentOf),ParentOf));
    }

    private string? ParentOf(string id)=>views[id].ParentId;

    /// <summary>Process rows directly under their app, keeping the order of the apps and of each app's processes. The held order puts the new processes of an app
    /// it did not show at the bottom, where they would read as another app's; that happens when rows come back after an Off or Unavailable reading during a hold.</summary>
    private static List<string> Grouped(IReadOnlyList<string> order,Func<string,string?> parentOf)
    {
        var children=order.Where(id=>parentOf(id) is not null).ToLookup(id=>parentOf(id)!);
        return [..order.Where(id=>parentOf(id) is null).SelectMany(app=>children[app].Prepend(app))];
    }

    /// <summary>Shows the rows in <paramref name="order"/>: gone rows leave, kept rows move as few as the planner allows, and new rows go in at their place.
    /// Row instances are kept per id and moved in place, so the selection, the expanded rows and the scroll position survive each update.</summary>
    private void Show(IReadOnlyList<string> order)
    {
        var target=order.Select(id=>views[id]).ToList();
        var keep=target.ToHashSet();
        // The selected row is gone (exited, beyond the 4,096 published, filtered out or collapsed): the selection clears and both actions disable.
        if(Selection is {} selected&&!keep.Contains(selected))Grid.SelectedItem=null;
        for(int i=rows.Count-1;i>=0;i--)if(!keep.Contains(rows[i]))rows.RemoveAt(i);
        foreach(var move in CalmOrderPlanner.Plan([..rows.Select(r=>r.Id)],order))rows.Move(move.From,move.To);
        for(int i=0;i<target.Count;i++)if(i>=rows.Count||!ReferenceEquals(rows[i],target[i]))rows.Insert(i,target[i]);
    }

    private ProcessRowView View(string id,string? parentId)
    {
        if(!views.TryGetValue(id,out var view))views[id]=view=new ProcessRowView(id,parentId);
        return view;
    }

    private static string AppId(string key)=>"app:"+key;
    private static string Pid(ProcessSample process)=>process.Pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>Every key's cell. A value is shown only when the reading is fresh (for the GPU keys, the GPU read too) and its column available; otherwise "—" with
    /// the first matching reason: stale reading, first sample, not matched to this process, else the column's status. GPU ENGINE shows the row's
    /// <paramref name="engine"/> text, null when unknown, with <paramref name="busiest"/> (the engine it names) as its tooltip.</summary>
    private static Dictionary<string,ProcessCell> Cells(ProcessActivity activity,Func<string,double?> value,Func<string,ProcessGap> gap,string pid,string? engine,GpuEngineUse? busiest,bool stale)
    {
        var cells=new Dictionary<string,ProcessCell>(StringComparer.Ordinal);
        foreach(string key in ProcessColumnKeys.All)
        {
            if(key=="pid"){cells[key]=new(pid,null);continue;}
            var state=State(activity,key);
            bool old=stale||(IsGpu(key)&&activity.GpuStale);
            if(!old&&state.Available&&Known(key,value,engine,busiest) is {} known){cells[key]=known;continue;}
            string reason=old?ProcessCopy.Stale:gap(key) switch
            {
                ProcessGap.FirstSample=>ProcessCopy.FirstSample,
                ProcessGap.NotMatched=>ProcessColumnKeys.DiskKeys.Contains(key)?ProcessCopy.DiskNotMatched:ProcessCopy.NetworkNotMatched,
                _=>state.Status,
            };
            cells[key]=new(Dash,reason.Length==0?null:reason);
        }
        return cells;
    }

    /// <summary>A known value's cell; null when the value is unknown.</summary>
    private static ProcessCell? Known(string key,Func<string,double?> value,string? engine,GpuEngineUse? busiest)
    {
        if(key!="gpuengine")return value(key) is {} known?new(Format(key,known),null):null;
        // GPU ENGINE: a label with its engine's adapter and type as the tooltip, or "" with none for a known zero.
        return engine is null?null:new(engine,engine.Length==0||busiest is null?null:ProcessCopy.EngineTip(busiest));
    }

    private static bool IsNetwork(string key)=>key is "network" or "download" or "upload";
    private static bool IsGpu(string key)=>key is "gpu" or "gpuengine" or "gpumemory" or "gpushared";
    private static ProcessColumnState State(ProcessActivity activity,string key)=>
        ProcessColumnKeys.DiskKeys.Contains(key)?activity.Disk:IsNetwork(key)?activity.Network:IsGpu(key)?activity.Gpu:activity.Scan;
    private static ProcessGap Gap(ProcessSample process,string key)=>
        ProcessColumnKeys.DiskKeys.Contains(key)?process.DiskGap:IsNetwork(key)?process.NetworkGap:IsGpu(key)?process.GpuGap:
        key is "cpu" or "io" or "ioread" or "iowrite"?process.RateGap:ProcessGap.None;
    /// <summary>An app's value is null only when no member knows it; its reason is the first that any member gives.</summary>
    private static ProcessGap Gap(ProcessAppRow app,string key)
    {
        var gaps=app.Members.Select(m=>Gap(m,key)).ToList();
        return gaps.Contains(ProcessGap.FirstSample)?ProcessGap.FirstSample:gaps.Contains(ProcessGap.NotMatched)?ProcessGap.NotMatched:ProcessGap.None;
    }
    private static string Format(string key,double value)=>key switch
    {
        "cpu" or "gpu"=>value.ToString("0.#",CultureInfo.CurrentCulture)+" %",
        "memory"=>Ui.Bytes(value),
        // A known zero reads "0 B", not Ui.Bytes's "0.0 B".
        "gpumemory" or "gpushared"=>value==0?"0 B":Ui.Bytes(value),
        _=>Ui.Bytes(value,true),
    };

    private static readonly Dictionary<string,string> SpokenLabels=new(StringComparer.Ordinal)
    {
        ["cpu"]="CPU",["memory"]="memory",["io"]="I/O",["ioread"]="I/O read",["iowrite"]="I/O write",["disk"]="disk",["diskread"]="disk read",
        ["diskwrite"]="disk write",["network"]="network",["download"]="download",["upload"]="upload",
        ["gpu"]="GPU",["gpuengine"]="GPU engine",["gpumemory"]="GPU memory",["gpushared"]="shared GPU memory",
    };
    /// <summary>Like "chrome.exe, 3 processes, CPU 9 %, memory 512.0 MB": the shown columns in order, an unknown value with its reason; an empty cell (a known zero's GPU engine) is left out.</summary>
    private static string Spoken(string name,string lead,IReadOnlyList<string> visible,IReadOnlyDictionary<string,ProcessCell> cells)
    {
        var parts=new List<string> {name,lead};
        foreach(string key in visible.Where(k=>k!="pid"))
        {
            var cell=cells[key];
            if(cell.Text.Length==0)continue;
            parts.Add(cell.Text!=Dash?$"{SpokenLabels[key]} {cell.Text}":cell.Tip is {} reason?$"{SpokenLabels[key]} unknown ({reason})":$"{SpokenLabels[key]} unknown");
        }
        return string.Join(", ",parts);
    }

    private static Button Labelled(Button button){AutomationProperties.SetName(button,(string)button.Content);return button;}

    private static T Sealed<T>(string xaml) where T:FrameworkTemplate {var template=(T)XamlReader.Parse(xaml);template.Seal();return template;}

    /// <summary>NAME: the chevron (hidden without processes to expand into, so names line up), then the name; process rows are indented under their app.</summary>
    private static readonly DataTemplate NameTemplate=Sealed<DataTemplate>("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <DockPanel x:Name="Line">
            <ToggleButton x:Name="Chevron" Width="20" Height="20" Margin="0,0,4,0" VerticalAlignment="Center" Cursor="Hand"
                          IsChecked="{Binding Expanded, Mode=OneWay}" AutomationProperties.Name="{Binding ChevronName}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="Frame" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="5" SnapsToDevicePixels="True">
                    <Path x:Name="Glyph" Data="M 0,0 L 4,4 L 0,8" Width="5" Height="8" Stroke="{DynamicResource Muted}" StrokeThickness="1.4"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin="0.5,0.5"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="Glyph" Property="RenderTransform"><Setter.Value><RotateTransform Angle="90"/></Setter.Value></Setter></Trigger>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource GlassHover}"/><Setter TargetName="Glyph" Property="Stroke" Value="{DynamicResource Ink}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
          </DockPanel>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding Expandable}" Value="False"><Setter TargetName="Chevron" Property="Visibility" Value="Hidden"/></DataTrigger>
            <DataTrigger Binding="{Binding IsApp}" Value="False"><Setter TargetName="Line" Property="Margin" Value="22,0,0,0"/></DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """);

    /// <summary>The column picker's popup, in theme colours.</summary>
    private static readonly ControlTemplate PickerTemplate=Sealed<ControlTemplate>("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ContextMenu">
          <Border Background="{DynamicResource GlassPopup}" BorderBrush="{DynamicResource GlassBorder}" BorderThickness="1" CornerRadius="9" Padding="4" MinWidth="190" SnapsToDevicePixels="True">
            <ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" MaxHeight="480">
              <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
            </ScrollViewer>
          </Border>
        </ControlTemplate>
        """);
}
