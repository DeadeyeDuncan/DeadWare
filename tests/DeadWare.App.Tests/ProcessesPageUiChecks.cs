using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Offscreen checks for the Processes page; run from the shared STA UI test with the real styles loaded.
/// Pages here are never Loaded, so they never hear <see cref="UiSession.Changed"/>: helpers call <see cref="ProcessesPage.Refresh"/> for a new reading,
/// and the page re-renders itself after a picker toggle or Turn on.</summary>
internal static class ProcessesPageUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly DateTimeOffset Epoch=new(2026,11,5,12,0,0,TimeSpan.Zero);
    private static readonly Size PageSize=new(1000,900);
    private static string settingsFolder="";

    internal static void Run()
    {
        var culture=CultureInfo.CurrentCulture;
        settingsFolder=Path.Combine(Path.GetTempPath(),"deadware-processes-"+Guid.NewGuid().ToString("N"));
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
            Off_state_shows_turn_on();
            Rows_group_case_variants_are_collapsed_and_expand();
            Single_process_app_shows_pid_without_chevron();
            Expanded_rows_share_columns_and_sort_within_their_app();
            Search_matches_name_or_pid();
            Column_picker_has_every_column_in_fixed_order();
            Header_right_click_opens_picker();
            Picker_toggle_keeps_hidden_admin_and_disk_keys();
            Standard_user_hides_admin_columns_disables_picker_entries_and_shows_banner();
            Elevated_shows_the_full_set_with_dash_and_status();
            Disk_build_off_omits_disk_everywhere();
            Gap_tooltips_name_their_reason();
            Stale_reading_shows_dash_with_stale_tooltip();
            Hidden_sort_column_falls_back();
            Cap_note_is_shown();
            Footer_lists_the_definitions();
            Automation_names_and_spoken_summary();
        }
        finally
        {
            CultureInfo.CurrentCulture=culture;
            try{if(Directory.Exists(settingsFolder))Directory.Delete(settingsFolder,true);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
        }
    }

    private static void Off_state_shows_turn_on()
    {
        var (p,session)=Page(monitoring:false);
        // The fixture reading is a standard user's (Elevated=false), so the sentence has no disk trace.
        string off=ProcessCopy.OffTitle+" "+ProcessCopy.ScanSentence(false,ProcessFeatures.DiskColumns,ProcessFeatures.ScanLoop);
        Assert.Contains(off,Texts(p));
        Assert.DoesNotContain(p.Grid,Shown(p));
        Click(Button(p,"Turn on"));
        Assert.True(session.Settings.ProcessMonitoring);Assert.True(session.Collector.ProcessMonitoring);
        // The page re-renders itself: the table replaces the off card.
        Assert.DoesNotContain(off,Texts(p));
        Assert.Contains(p.Grid,Shown(p));
        Assert.Equal(["chrome.exe","Explorer.EXE","Code.exe"],AppNames(p));
        // Before the first reading: the off sentence falls back to the process's own elevation, and an on page shows an empty table.
        var (empty,emptySession)=Page(monitoring:false);
        typeof(Collector).GetField("latest",All)!.SetValue(emptySession.Collector,null);
        Refresh(empty);
        Assert.Contains(ProcessCopy.OffTitle+" "+ProcessCopy.ScanSentence(Collector.IsElevated,ProcessFeatures.DiskColumns,ProcessFeatures.ScanLoop),Texts(empty));
        emptySession.Settings.ProcessMonitoring=true;Refresh(empty);
        Assert.Empty(Rows(empty));Assert.Contains(empty.Grid,Shown(empty));
    }

    private static void Rows_group_case_variants_are_collapsed_and_expand()
    {
        var (p,_)=Page();
        Assert.Equal(["chrome.exe","Explorer.EXE","Code.exe"],AppNames(p));
        Assert.Equal(3,Rows(p).Count);
        Assert.All(Rows(p),r=>Assert.False(r.Expanded));
        Expand(p,"chrome.exe");
        Assert.Equal(6,Rows(p).Count);
        Assert.True(Row(p,"chrome.exe").Expanded);
        Expand(p,"chrome.exe");
        Assert.Equal(3,Rows(p).Count);
    }

    private static void Single_process_app_shows_pid_without_chevron()
    {
        var (p,_)=Page(columns:["pid","cpu"]);
        Assert.Null(Chevron(p,"Code.exe"));
        Assert.Equal("40",Cell(p,"Code.exe","pid"));
        Assert.NotNull(Chevron(p,"chrome.exe"));
        Assert.Equal("",Cell(p,"chrome.exe","pid"));
    }

    private static void Expanded_rows_share_columns_and_sort_within_their_app()
    {
        var (p,_)=Page(elevated:true);
        Expand(p,"chrome.exe");
        Assert.Equal([31,32,30],ChildPids(p,"chrome.exe"));
        Assert.All(["cpu","memory","io","disk","network"],k=>Assert.NotEqual("",Cell(p,31,k)));
        Assert.Equal("5 %",Cell(p,31,"cpu"));
        Assert.Equal("app:EXPLORER.EXE",Rows(p)[4].Id);
        // Process rows have no chevron of their own.
        Assert.Empty(Shown(Container(p,Row(p,31))).OfType<ToggleButton>());
    }

    private static void Search_matches_name_or_pid()
    {
        var (p,_)=Page();
        SearchBox(p).Text=" 3";
        Assert.Equal(["chrome.exe"],AppNames(p));
        SearchBox(p).Text="EXPLORER";
        Assert.Equal(["Explorer.EXE"],AppNames(p));
        SearchBox(p).Text="";
        Assert.Equal(3,AppNames(p).Length);
    }

    private static void Column_picker_has_every_column_in_fixed_order()
    {
        var p=Page(elevated:true,disk:true).Page;
        Assert.Equal(["CPU","MEMORY","I/O","DISK","NETWORK","PID","I/O READ","I/O WRITE","DISK READ","DISK WRITE","↓ DOWNLOAD","↑ UPLOAD"],PickerHeaders(p));
        Assert.Equal([true,true,true,true,true,false,false,false,false,false,false,false],PickerItems(p).Select(i=>i.IsChecked).ToArray());
        Assert.All(PickerItems(p),i=>Assert.True(i.IsEnabled));
    }

    private static void Header_right_click_opens_picker()
    {
        var (p,_)=Page(elevated:true);
        RightClick(Header(p,"CPU"));
        Assert.True(PickerMenu(p).IsOpen);
        Assert.Equal(12,PickerItems(p).Count);
        PickerMenu(p).IsOpen=false;
        // The Columns button opens the same menu under itself.
        var columns=Button(p,"Columns");
        Click(columns);
        Assert.True(PickerMenu(p).IsOpen);Assert.Same(columns,PickerMenu(p).PlacementTarget);
        Assert.Same(PickerMenu(p),Header(p,"NAME").ContextMenu);
        PickerMenu(p).IsOpen=false;
    }

    private static void Picker_toggle_keeps_hidden_admin_and_disk_keys()
    {
        var (p,session)=Page(elevated:false,disk:false);
        Toggle(PickerItem(p,"PID"));
        Assert.Equal(["pid","cpu","memory","io","disk","network"],session.Settings.ProcessColumns);
        Assert.Equal(["NAME","PID","CPU","MEMORY","I/O"],Headers(p));
        Toggle(PickerItem(p,"CPU"));
        Assert.Equal(["pid","memory","io","disk","network"],session.Settings.ProcessColumns);
        Assert.Equal(["NAME","PID","MEMORY","I/O"],Headers(p));
        Assert.False(PickerItem(p,"CPU").IsChecked);Assert.True(PickerItem(p,"PID").IsChecked);
    }

    private static void Standard_user_hides_admin_columns_disables_picker_entries_and_shows_banner()
    {
        var (p,_)=Page(elevated:false,disk:true);
        Assert.Equal(["NAME","CPU","MEMORY","I/O"],Headers(p));
        var admin=PickerItems(p).Where(i=>Label(i).EndsWith(" (administrator)",StringComparison.Ordinal)).ToArray();
        Assert.Equal(["DISK (administrator)","NETWORK (administrator)","DISK READ (administrator)","DISK WRITE (administrator)","↓ DOWNLOAD (administrator)","↑ UPLOAD (administrator)"],admin.Select(Label).ToArray());
        Assert.All(admin,i=>Assert.False(i.IsEnabled));
        // Disabled entries keep their saved state.
        Assert.True(PickerItem(p,"DISK (administrator)").IsChecked);Assert.True(PickerItem(p,"NETWORK (administrator)").IsChecked);
        Assert.Contains("Disk and network need administrator",Texts(p));
        Assert.NotNull(Button(p,"Restart as administrator"));
    }

    private static void Elevated_shows_the_full_set_with_dash_and_status()
    {
        var (p,_)=Page(elevated:true,network:new(false,"Per-app ETW unavailable (X)"));
        Assert.Equal(["NAME","CPU","MEMORY","I/O","DISK","NETWORK"],Headers(p));
        Assert.Equal("—",Cell(p,"chrome.exe","network"));
        Assert.Equal("Per-app ETW unavailable (X)",Tip(p,"chrome.exe","network"));
        Assert.Null(Tip(p,"chrome.exe","cpu"));
        Assert.DoesNotContain("Disk and network need administrator",Texts(p));
        Assert.Null(Button(p,"Restart as administrator"));
    }

    private static void Disk_build_off_omits_disk_everywhere()
    {
        var (p,_)=Page(elevated:false,disk:false);
        Assert.DoesNotContain(PickerHeaders(p),h=>h.StartsWith("DISK",StringComparison.Ordinal));
        Assert.Contains("Network needs administrator",Texts(p));
        var (e,_)=Page(elevated:true,disk:false);
        Assert.DoesNotContain("DISK",Headers(e));
        Assert.Equal(ProcessCopy.Footer(false).Count,FooterTexts(e).Length);
    }

    private static void Gap_tooltips_name_their_reason()
    {
        var (p,_)=Page(elevated:true,activity:WithGaps());
        Assert.Equal("—",Cell(p,"new.exe","cpu"));
        Assert.Equal("first sample",Tip(p,"new.exe","cpu"));
        Assert.Equal("first sample",Tip(p,"new.exe","io"));
        Assert.Equal("—",Cell(p,"moved.exe","network"));
        Assert.Equal("traffic not matched to this process",Tip(p,"moved.exe","network"));
        Assert.Equal("disk activity not matched to this process",Tip(p,"moved.exe","disk"));
        // A known value carries no gap tooltip.
        Assert.Equal("2 %",Cell(p,"moved.exe","cpu"));Assert.Null(Tip(p,"moved.exe","cpu"));
    }

    private static void Stale_reading_shows_dash_with_stale_tooltip()
    {
        var (p,session)=Page(stale:true);
        Assert.True(session.Stale);
        Assert.Equal("—",Cell(p,"chrome.exe","cpu"));
        Assert.Equal("stale reading",Tip(p,"chrome.exe","cpu"));
        Assert.Equal("—",Cell(p,"chrome.exe","memory"));
        Assert.Equal("stale reading",Tip(p,"chrome.exe","memory"));
    }

    private static void Hidden_sort_column_falls_back()
    {
        var (p,_)=Page(elevated:true);
        Assert.Equal(("cpu",true),SortState(p));
        ClickHeader(p,"NETWORK");
        Assert.Equal(("network",true),SortState(p));
        Toggle(PickerItem(p,"NETWORK"));
        Assert.Equal(("cpu",true),SortState(p));
        Assert.Equal(["chrome.exe","Explorer.EXE","Code.exe"],AppNames(p));
        Toggle(PickerItem(p,"CPU"));
        Assert.Equal(("name",false),SortState(p));
        Assert.Equal(["chrome.exe","Code.exe","Explorer.EXE"],AppNames(p));
        // NAME sorts ascending first; a second click flips it.
        ClickHeader(p,"NAME");
        Assert.Equal(("name",true),SortState(p));
        Assert.Equal(["Explorer.EXE","Code.exe","chrome.exe"],AppNames(p));
    }

    private static void Cap_note_is_shown()
    {
        Assert.Contains("Showing 4,096 of 5,000 processes",Texts(Page(activity:Fixture() with {TotalProcesses=5000}).Page));
        Assert.DoesNotContain(Texts(Page().Page),t=>t.StartsWith("Showing ",StringComparison.Ordinal));
    }

    private static void Footer_lists_the_definitions()
    {
        Assert.Equal(ProcessCopy.Footer(true),FooterTexts(Page(elevated:true,disk:true).Page));
        Assert.Equal(ProcessCopy.Footer(false),FooterTexts(Page(elevated:true,disk:false).Page));
    }

    private static void Automation_names_and_spoken_summary()
    {
        var (p,_)=Page(elevated:true);
        Assert.Equal("Processes",AutomationProperties.GetName(p.Grid));
        Assert.Equal("Find app or PID",AutomationProperties.GetName(SearchBox(p)));
        Assert.All(["End task…","Open file location","Columns"],b=>Assert.Equal(b,AutomationProperties.GetName(Button(p,b)!)));
        // Task 17 wires the actions; until then both stay disabled.
        Assert.False(Button(p,"End task…")!.IsEnabled);Assert.False(Button(p,"Open file location")!.IsEnabled);
        Assert.Equal("Expand chrome.exe",AutomationProperties.GetName(Chevron(p,"chrome.exe")!));
        Expand(p,"chrome.exe");
        Assert.Equal("Collapse chrome.exe",AutomationProperties.GetName(Chevron(p,"chrome.exe")!));
        var chrome=Row(p,"chrome.exe");
        Assert.StartsWith("chrome.exe, 3 processes, CPU ",chrome.Spoken);
        Assert.Equal("chrome.exe, 3 processes, CPU 9 %, memory 192.0 MB, I/O 9.0 KB/s, disk 22.5 KB/s, network 12.0 KB/s",chrome.Spoken);
        Assert.Equal(chrome.Spoken,AutomationProperties.GetName(Container(p,chrome)));
        Assert.StartsWith("chrome.exe, PID 31, CPU 5 %, ",Row(p,31).Spoken);
        Assert.StartsWith("Code.exe, PID 40, CPU 0.5 %, ",Row(p,"Code.exe").Spoken);
    }

    // Fixtures: Generation 1 for every PID, the creation ticks FakeProcessControl reads.
    private static ProcessSample Sample(int pid,string name,double? cpu)=>
        new(pid,1,name,1,cpu,64_000_000+pid,2_000,1_000,ProcessGap.None,null)
        {DownloadPerSecond=3_000,UploadPerSecond=1_000,DiskReadPerSecond=5_000,DiskWritePerSecond=2_500};

    private static ProcessActivity Activity(params ProcessSample[] processes)=>
        new(ProcessActivityKind.Available,1,Epoch,0,1,8,processes,ProcessOmitted.None,processes.Length,1);

    private static ProcessActivity Fixture()=>Activity(
        Sample(30,"chrome.exe",1),Sample(31,"chrome.exe",5),Sample(32,"chrome.exe",3),
        Sample(10,"Explorer.EXE",1),Sample(11,"explorer.exe",1),Sample(40,"Code.exe",.5));

    /// <summary>new.exe has no rates yet (first sample); moved.exe's traffic and disk activity carry another generation (not matched).</summary>
    private static ProcessActivity WithGaps()=>Activity(
        Sample(50,"new.exe",null) with {IoReadPerSecond=null,IoWritePerSecond=null,RateGap=ProcessGap.FirstSample},
        Sample(60,"moved.exe",2) with {DownloadPerSecond=null,UploadPerSecond=null,NetworkGap=ProcessGap.NotMatched,DiskReadPerSecond=null,DiskWritePerSecond=null,DiskGap=ProcessGap.NotMatched});

    private static (ProcessesPage Page,UiSession Session) Page(bool monitoring=true,bool elevated=false,bool disk=true,bool stale=false,
        IReadOnlyList<string>? columns=null,ProcessActivity? activity=null,ProcessColumnState? network=null)
    {
        var processes=activity??Fixture();
        if(network is not null)processes=processes with {Network=network};
        var session=Session(monitoring,elevated,stale,columns??ProcessColumnKeys.Default,processes);
        var page=new ProcessesPage(session,disk,new FakeProcessControl());
        Layout(page);
        return (page,session);
    }

    /// <summary>A reading with the given elevation and activity; <paramref name="stale"/> moves it 100 s into the past of a frozen clock.</summary>
    private static UiSession Session(bool monitoring,bool elevated,bool stale,IReadOnlyList<string> columns,ProcessActivity activity)
    {
        void Set(object target,string field,object? value)=>target.GetType().GetField(field,All)!.SetValue(target,value);
        var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector,"clock",new Stopwatch());
        Set(collector,"latest",new Reading(new(Epoch,stale?-100:0,new Dictionary<string,double?>()),[],[],[],"Fixture CPU","","","",elevated){ProcessActivity=activity});
        var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session,"<Collector>k__BackingField",collector);
        Set(session,"<Settings>k__BackingField",new UserSettings {ProcessMonitoring=monitoring,ProcessColumns=[..columns]});
        Set(session,"layout",new DetachedWindowState([]));
        Set(session,"writer",new SettingsSnapshotWriter(Path.Combine(settingsFolder,Guid.NewGuid().ToString("N")+".json")));
        return session;
    }

    private static void Layout(ProcessesPage page){page.Measure(PageSize);page.Arrange(new Rect(PageSize));page.UpdateLayout();}
    private static void Refresh(ProcessesPage page){page.Refresh();Layout(page);}

    /// <summary>Descendants that are shown: a subtree whose root is Hidden or Collapsed is skipped.</summary>
    private static List<DependencyObject> Shown(DependencyObject root)
    {
        var shown=new List<DependencyObject>();
        void Walk(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                if(child is UIElement {Visibility:not Visibility.Visible})continue;
                shown.Add(child);Walk(child);
            }
        }
        Walk(root);
        return shown;
    }
    private static List<string> Texts(ProcessesPage page){Layout(page);return Shown(page).OfType<TextBlock>().Select(t=>t.Text).ToList();}
    private static Button? Button(ProcessesPage page,string content){Layout(page);return Shown(page).OfType<Button>().SingleOrDefault(b=>b.Content as string==content);}
    private static TextBox SearchBox(ProcessesPage page){Layout(page);return Shown(page).OfType<TextBox>().Single();}
    private static void Click(ButtonBase? button)=>Assert.IsAssignableFrom<ButtonBase>(button).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static List<ProcessRowView> Rows(ProcessesPage page)=>page.Grid.Items.Cast<ProcessRowView>().ToList();
    private static string[] AppNames(ProcessesPage page)=>Rows(page).Where(r=>r.IsApp).Select(r=>r.Name).ToArray();
    private static ProcessRowView Row(ProcessesPage page,string app)=>Rows(page).Single(r=>r.IsApp&&r.Name==app);
    private static ProcessRowView Row(ProcessesPage page,int pid)=>Rows(page).Single(r=>!r.IsApp&&r.Cells["pid"].Text==pid.ToString(CultureInfo.InvariantCulture));
    private static int[] ChildPids(ProcessesPage page,string app)
    {
        var rows=Rows(page);
        return rows.Skip(rows.IndexOf(Row(page,app))+1).TakeWhile(r=>!r.IsApp).Select(r=>int.Parse(r.Cells["pid"].Text,CultureInfo.InvariantCulture)).ToArray();
    }
    private static DataGridRow Container(ProcessesPage page,ProcessRowView row){Layout(page);return Assert.IsType<DataGridRow>(page.Grid.ItemContainerGenerator.ContainerFromItem(row));}
    /// <summary>The rendered cell of a shown column, so the text and tooltip come through the page's real bindings.</summary>
    private static DataGridCell RenderedCell(ProcessesPage page,ProcessRowView row,string key)
    {
        var container=Container(page,row);
        var content=Assert.IsAssignableFrom<FrameworkElement>(page.Grid.Columns.Single(c=>c.SortMemberPath==key).GetCellContent(container));
        return Assert.IsType<DataGridCell>(content.Parent);
    }
    private static string Cell(ProcessesPage page,string app,string key)=>Assert.IsType<TextBlock>(RenderedCell(page,Row(page,app),key).Content).Text;
    private static string Cell(ProcessesPage page,int pid,string key)=>Assert.IsType<TextBlock>(RenderedCell(page,Row(page,pid),key).Content).Text;
    private static string? Tip(ProcessesPage page,string app,string key)=>(string?)RenderedCell(page,Row(page,app),key).ToolTip;
    private static ToggleButton? Chevron(ProcessesPage page,string app)=>Shown(Container(page,Row(page,app))).OfType<ToggleButton>().SingleOrDefault();
    private static void Expand(ProcessesPage page,string app){Click(Chevron(page,app));Layout(page);}

    private static string[] Headers(ProcessesPage page)=>page.Grid.Columns.Select(c=>(string)c.Header).ToArray();
    private static DataGridColumnHeader Header(ProcessesPage page,string header){Layout(page);return Shown(page.Grid).OfType<DataGridColumnHeader>().Single(h=>h.Column?.Header as string==header);}
    /// <summary>What ContextMenuService does on a right-button release over a header: open that header's context menu there.</summary>
    private static void RightClick(DataGridColumnHeader header)
    {
        var menu=Assert.IsType<ContextMenu>(header.ContextMenu);
        menu.PlacementTarget=header;menu.IsOpen=true;
    }
    /// <summary>The header's own click path: DataGridColumnHeader.OnClick → DataGrid.PerformSort → Sorting.</summary>
    private static void ClickHeader(ProcessesPage page,string header){typeof(DataGridColumnHeader).GetMethod("OnClick",All)!.Invoke(Header(page,header),null);Layout(page);}
    private static (string Key,bool Descending) SortState(ProcessesPage page)
    {
        var sorted=Assert.Single(page.Grid.Columns,c=>c.SortDirection is not null);
        return (sorted.SortMemberPath,sorted.SortDirection==ListSortDirection.Descending);
    }

    private static ContextMenu PickerMenu(ProcessesPage page)=>(ContextMenu)typeof(ProcessesPage).GetField("picker",All)!.GetValue(page)!;
    private static List<MenuItem> PickerItems(ProcessesPage page)=>PickerMenu(page).Items.OfType<MenuItem>().ToList();
    private static string Label(MenuItem item)=>(string)item.Header;
    private static string[] PickerHeaders(ProcessesPage page)=>PickerItems(page).Select(Label).ToArray();
    private static MenuItem PickerItem(ProcessesPage page,string header)=>PickerItems(page).Single(i=>Label(i)==header);
    /// <summary>A user's click on a checkable item: WPF flips IsChecked, then raises Click.</summary>
    private static void Toggle(MenuItem item){item.IsChecked=!item.IsChecked;item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,item));}

    private static string[] FooterTexts(ProcessesPage page)
    {
        var footer=(Panel)typeof(ProcessesPage).GetField("footer",All)!.GetValue(page)!;
        Layout(page);Assert.Contains(footer,Shown(page));
        return footer.Children.OfType<TextBlock>().Select(t=>t.Text).ToArray();
    }
}
