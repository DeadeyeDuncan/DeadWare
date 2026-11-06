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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Offscreen checks for the Processes page; run from the shared STA UI test with the real styles loaded, each page arranged at 900 × 640.
/// Pages here are never Loaded, so they never hear <see cref="UiSession.Changed"/>: helpers call <see cref="ProcessesPage.Refresh"/> for a new reading,
/// and the page re-renders itself after a picker toggle or Turn on.</summary>
internal static class ProcessesPageUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly DateTimeOffset Epoch=new(2026,11,5,12,0,0,TimeSpan.Zero);
    private static readonly Size PageSize=new(900,640);
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
            Changed_without_a_new_reading_does_not_resort();
            New_reading_outside_a_hold_resorts();
            Held_order_appends_new_apps_and_keeps_selection();
            Navigation_keys_the_picker_and_the_confirm_box_hold_the_order();
            F5_header_click_and_search_resort_during_hold();
            Updates_keep_selection_expansion_and_scroll();
            Waiting_and_unavailable_readings_keep_expanded_rows();
            Keyboard_shortcuts();
            Selected_app_exiting_clears_selection_and_disables_actions();
            End_task_asks_through_the_confirm_hook_and_shows_the_result();
            Open_file_location_shows_the_result();
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
        // With nothing selected both actions stay disabled.
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

    private static void Changed_without_a_new_reading_does_not_resort()
    {
        var (p,_)=Page();
        var before=AppNames(p);
        MutateLatest(p,"Code.exe",cpu:99);
        RaiseChanged(p);
        Assert.Equal(before,AppNames(p));
        // The value itself updates in place.
        Assert.Equal("99 %",Cell(p,"Code.exe","cpu"));
    }

    private static void New_reading_outside_a_hold_resorts()
    {
        var (p,_)=Page();
        Publish(p,2,cpu:("Code.exe",99));
        Assert.Equal("Code.exe",AppNames(p)[0]);
        Assert.Equal(Sorted(p),AppNames(p));
    }

    private static void Held_order_appends_new_apps_and_keeps_selection()
    {
        var (p,_)=Page();
        var before=AppNames(p);
        Hold(p,pointer:true);
        Select(p,"chrome.exe");
        Publish(p,2,withNew:"zoom.exe",cpu:("chrome.exe",0));
        Assert.Equal([..before,"zoom.exe"],AppNames(p));
        Assert.Equal("chrome.exe",Selected(p));
        // Values still update during the hold.
        Assert.Equal("0 %",Cell(p,"chrome.exe","cpu"));
        Hold(p,pointer:false);
        Publish(p,3);
        Assert.Equal(Sorted(p),AppNames(p));
        Assert.Equal(["Explorer.EXE","Code.exe","zoom.exe","chrome.exe"],AppNames(p));
        // Rows move in place, so the selection follows chrome.exe to its sorted place.
        Assert.Equal("chrome.exe",Selected(p));
    }

    private static void Navigation_keys_the_picker_and_the_confirm_box_hold_the_order()
    {
        string[] held=["chrome.exe","Explorer.EXE","Code.exe"];
        // For 3 s after a navigation key in the table.
        var (keyed,_)=Page();
        Press(keyed,Key.Down);
        Publish(keyed,2,cpu:("Code.exe",99));
        Assert.Equal(held,AppNames(keyed));
        // While the column picker is open; once it has closed, the next new reading re-sorts.
        var (picking,_)=Page();
        Click(Button(picking,"Columns"));
        Assert.True(PickerMenu(picking).IsOpen);
        Publish(picking,2,cpu:("Code.exe",99));
        Assert.Equal(held,AppNames(picking));
        PickerMenu(picking).IsOpen=false;
        Publish(picking,3);
        Assert.Equal("Code.exe",AppNames(picking)[0]);
        // While End task's confirmation is open; it is asked on the action's own thread.
        var (asking,_)=Page();
        using var open=new ManualResetEventSlim();
        using var answer=new ManualResetEventSlim();
        asking.Confirm=(_,_)=>{open.Set();answer.Wait(TimeSpan.FromSeconds(10));return false;};
        try
        {
            Select(asking,"chrome.exe");
            Click(Button(asking,"End task…"));
            WaitFor(()=>open.IsSet);
            Publish(asking,2,cpu:("Code.exe",99));
            Assert.Equal(held,AppNames(asking));
        }
        finally{answer.Set();}
        WaitFor(()=>Button(asking,"End task…")!.IsEnabled);
        Publish(asking,3);
        Assert.Equal("Code.exe",AppNames(asking)[0]);
    }

    private static void F5_header_click_and_search_resort_during_hold()
    {
        var (p,_)=Page();
        Hold(p,pointer:true);
        Publish(p,2,cpu:("Code.exe",99));
        Assert.NotEqual("Code.exe",AppNames(p)[0]);
        Press(p,Key.F5);
        Assert.Equal("Code.exe",AppNames(p)[0]);
        Publish(p,3,cpu:("Explorer.EXE",200));
        Assert.NotEqual(Sorted(p),AppNames(p));
        ClickHeader(p,"CPU");
        Assert.Equal(Sorted(p),AppNames(p));
        Publish(p,4,cpu:("Code.exe",300));
        Assert.NotEqual(Sorted(p),AppNames(p));
        SetSearch(p,"e");
        Assert.Equal(Sorted(p),AppNames(p));
    }

    private static void Updates_keep_selection_expansion_and_scroll()
    {
        var (p,_)=Page(activity:SixtyApps());
        Expand(p,"chrome.exe");
        Select(p,"app40.exe");
        ScrollTo(p,200);
        Assert.Equal(200,VerticalOffset(p),tolerance:1);
        Publish(p,2);
        Assert.Equal("app40.exe",Selected(p));
        Assert.True(IsExpanded(p,"chrome.exe"));
        Assert.Equal(200,VerticalOffset(p),tolerance:1);
    }

    private static void Waiting_and_unavailable_readings_keep_expanded_rows()
    {
        var (p,_)=Page();
        Hold(p,pointer:true);
        Expand(p,"chrome.exe");
        // Monitoring is on, but the reading was taken before the first process scan: a reason instead of a bare empty table.
        SetActivity(p,ProcessActivity.Off);
        Assert.Empty(Rows(p));
        Assert.Contains("Waiting for the first process scan",Texts(p));
        SetActivity(p,ProcessActivity.Unavailable("Process scan unavailable"));
        Assert.Empty(Rows(p));
        Assert.Contains("Process scan unavailable",Texts(p));
        Assert.DoesNotContain("Waiting for the first process scan",Texts(p));
        // The next published scan shows the app still expanded, its processes under it even during a hold.
        SetActivity(p,Fixture() with {Sequence=2});
        Assert.True(IsExpanded(p,"chrome.exe"));
        Assert.Equal([31,32,30],ChildPids(p,"chrome.exe"));
        Assert.Equal(6,Rows(p).Count);
        Assert.DoesNotContain("Process scan unavailable",Texts(p));
        // No reading at all gives the same reason.
        var (empty,session)=Page();
        typeof(Collector).GetField("latest",All)!.SetValue(session.Collector,null);
        Refresh(empty);
        Assert.Empty(Rows(empty));
        Assert.Contains("Waiting for the first process scan",Texts(empty));
    }

    private static void Keyboard_shortcuts()
    {
        var (p,_)=Page();
        int asked=0;p.Confirm=(_,_)=>{Interlocked.Increment(ref asked);return false;};
        Select(p,"chrome.exe");
        Press(p,Key.Right);Assert.True(IsExpanded(p,"chrome.exe"));
        Press(p,Key.Left);Assert.False(IsExpanded(p,"chrome.exe"));
        Press(p,Key.Enter);Assert.True(IsExpanded(p,"chrome.exe"));
        // Left on a process row goes back to its app and collapses it, so the selection stays on a shown row.
        Select(p,31);Press(p,Key.Left);
        Assert.False(IsExpanded(p,"chrome.exe"));Assert.Same(Row(p,"chrome.exe"),p.Grid.SelectedItem);
        Press(p,Key.F,ModifierKeys.Control);
        Assert.Same(SearchBox(p),FocusManager.GetFocusedElement(FocusManager.GetFocusScope(SearchBox(p))));
        SetSearch(p,"code");
        Assert.Equal(["Code.exe"],AppNames(p));
        Press(SearchBox(p),Key.Escape);
        Assert.Equal("",SearchBox(p).Text);
        Assert.Equal(3,AppNames(p).Length);
        Select(p,"chrome.exe");
        Press(p,Key.Delete);
        WaitFor(()=>Volatile.Read(ref asked)==1&&Button(p,"End task…")!.IsEnabled);
    }

    private static void Selected_app_exiting_clears_selection_and_disables_actions()
    {
        var (p,_)=Page();
        Select(p,"chrome.exe");
        Assert.True(Button(p,"End task…")!.IsEnabled);Assert.True(Button(p,"Open file location")!.IsEnabled);
        Publish(p,2,without:"chrome.exe");
        Assert.Null(Selected(p));
        Assert.False(Button(p,"End task…")!.IsEnabled);Assert.False(Button(p,"Open file location")!.IsEnabled);
        // A selected process that drops out of the 4,096 published processes while its app stays: the same.
        Expand(p,"Explorer.EXE");Select(p,11);
        Assert.True(Button(p,"End task…")!.IsEnabled);
        Publish(p,3,withoutPid:11,total:5000);
        Assert.Contains("Showing 4,096 of 5,000 processes",Texts(p));
        Assert.Null(p.Grid.SelectedItem);
        Assert.False(Button(p,"End task…")!.IsEnabled);Assert.False(Button(p,"Open file location")!.IsEnabled);
        // Nothing is left to act on: Delete and both buttons start nothing.
        p.Confirm=(_,_)=>throw new InvalidOperationException("Nothing is selected.");
        Press(p,Key.Delete);Click(Button(p,"End task…"));Click(Button(p,"Open file location"));Pump();
        Assert.Empty(Fake(p).Opened);Assert.Equal("",StatusText(p));
    }

    private static void End_task_asks_through_the_confirm_hook_and_shows_the_result()
    {
        var (p,_)=Page();
        string? asked=null;p.Confirm=(q,_)=>{asked=q;return true;};
        Select(p,"chrome.exe");
        Click(Button(p,"End task…"));
        WaitFor(()=>StatusText(p)!="");
        Assert.Equal("End chrome.exe (3 processes)? Unsaved work in it will be lost.",asked);
        Assert.Equal("Ended 3 · not ended 0 · already exited 0 · still running 0",StatusText(p));
        Assert.Contains(StatusText(p),Texts(p));
        Assert.Equal([30,31,32],Fake(p).Terminated);
        // A process row ends that process alone.
        Expand(p,"chrome.exe");Select(p,31);
        Click(Button(p,"End task…"));
        WaitFor(()=>StatusText(p)=="Ended 1 · not ended 0 · already exited 0 · still running 0");
        Assert.Equal("End chrome.exe (PID 31)? Unsaved work in it will be lost.",asked);
        Assert.Equal([30,31,32,31],Fake(p).Terminated);
        // No ends nothing and returns "", which clears the line.
        p.Confirm=(q,_)=>{asked=q;return false;};
        Select(p,"Code.exe");
        Click(Button(p,"End task…"));
        WaitFor(()=>StatusText(p)==""&&Button(p,"End task…")!.IsEnabled);
        Assert.Equal("End Code.exe (1 process)? Unsaved work in it will be lost.",asked);
        Assert.DoesNotContain(Texts(p),t=>t.StartsWith("Ended ",StringComparison.Ordinal));
        Assert.Equal([30,31,32,31],Fake(p).Terminated);
    }

    private static void Open_file_location_shows_the_result()
    {
        var (p,_)=Page();
        // Image paths in a folder that does not exist, so nothing is ever revealed: the line says why.
        string missing=Path.Combine(Path.GetTempPath(),"deadware-missing-"+Guid.NewGuid().ToString("N"));
        foreach(int pid in new[] {30,31,32,40})Fake(p)[pid].ImagePath=Path.Combine(missing,"app.exe");
        Select(p,"Code.exe");
        Click(Button(p,"Open file location"));
        WaitFor(()=>StatusText(p)!="");
        Assert.Equal("The program file is no longer there.",StatusText(p));
        // An app whose processes run from two places asks for a process row.
        Fake(p)[31].ImagePath=Path.Combine(missing,"other","app.exe");
        Select(p,"chrome.exe");
        Click(Button(p,"Open file location"));
        WaitFor(()=>StatusText(p)=="runs from 2 locations; expand to choose one"&&Button(p,"Open file location")!.IsEnabled);
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

    /// <summary>chrome.exe's three processes and 59 single-process apps, app1.exe to app59.exe, whose CPU falls as the number rises.</summary>
    private static ProcessActivity SixtyApps()=>Activity([
        Sample(30,"chrome.exe",30),Sample(31,"chrome.exe",30),Sample(32,"chrome.exe",30),
        ..Enumerable.Range(1,59).Select(i=>Sample(100+i,"app"+i.ToString(CultureInfo.InvariantCulture)+".exe",60-i*.5))]);

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

    private static UiSession SessionOf(ProcessesPage page)=>(UiSession)typeof(ProcessesPage).GetField("session",All)!.GetValue(page)!;
    /// <summary>The process control Page() gave the page; its scripts decide what End task and Open file location read.</summary>
    private static FakeProcessControl Fake(ProcessesPage page)=>(FakeProcessControl)typeof(ProcessesPage).GetField("control",All)!.GetValue(page)!;
    private static ProcessActivity Latest(ProcessesPage page)=>SessionOf(page).Current!.ProcessActivity;
    /// <summary>Replaces the latest reading's process activity without telling the page.</summary>
    private static void Replace(ProcessesPage page,ProcessActivity activity)
    {
        var collector=SessionOf(page).Collector;var latest=typeof(Collector).GetField("latest",All)!;
        latest.SetValue(collector,(Reading)latest.GetValue(collector)! with {ProcessActivity=activity});
    }
    /// <summary>What the page does on <see cref="UiSession.Changed"/>; pages here are never Loaded, so they never hear it.</summary>
    private static void RaiseChanged(ProcessesPage page)=>Refresh(page);
    private static void SetActivity(ProcessesPage page,ProcessActivity activity){Replace(page,activity);RaiseChanged(page);}
    /// <summary><paramref name="activity"/> with an app's CPU spread evenly over its processes, a new single-process app (PID 70, 0.2 %),
    /// an app or one PID gone, and the scan's total before the 4,096 cap.</summary>
    private static ProcessActivity With(ProcessActivity activity,(string App,double Cpu)? cpu=null,string? withNew=null,string? without=null,int? withoutPid=null,int? total=null)
    {
        static bool Of(ProcessSample process,string app)=>ProcessGrouping.Key(process.Name)==ProcessGrouping.Key(app);
        var processes=activity.Processes.ToList();
        if(cpu is {} change)
        {
            int members=processes.Count(m=>Of(m,change.App));
            processes=[..processes.Select(m=>Of(m,change.App)?m with {CpuPercent=change.Cpu/members}:m)];
        }
        if(withNew is not null)processes.Add(Sample(70,withNew,.2));
        if(without is not null)processes.RemoveAll(m=>Of(m,without));
        if(withoutPid is int pid)processes.RemoveAll(m=>m.Pid==pid);
        return activity with {Processes=processes,TotalProcesses=total??processes.Count};
    }
    /// <summary>Changes the latest reading's values under the same Sequence: not a new reading.</summary>
    private static void MutateLatest(ProcessesPage page,string app,double cpu)=>Replace(page,With(Latest(page),cpu:(app,cpu)));
    /// <summary>A new reading: the latest activity, changed as <see cref="With"/> says, under a new Sequence; then the page's Changed handler runs.</summary>
    private static void Publish(ProcessesPage page,long sequence,(string App,double Cpu)? cpu=null,string? withNew=null,string? without=null,int? withoutPid=null,int? total=null)=>
        SetActivity(page,With(Latest(page),cpu,withNew,without,withoutPid,total) with {Sequence=sequence});
    /// <summary>The app order <see cref="ProcessSort"/> gives the latest reading under the page's current sort and search.</summary>
    private static string[] Sorted(ProcessesPage page)
    {
        var (key,descending)=SortState(page);
        var apps=ProcessGrouping.Group(Latest(page).Processes).Where(a=>ProcessGrouping.Matches(a,SearchBox(page).Text)).ToList();
        apps.Sort((a,b)=>ProcessSort.Compare(a,b,key,descending));
        return apps.Select(a=>a.Name).ToArray();
    }

    /// <summary>The pointer entering or leaving the table, as the mouse raises it.</summary>
    private static void Hold(ProcessesPage page,bool pointer)=>
        page.Grid.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0) {RoutedEvent=pointer?Mouse.MouseEnterEvent:Mouse.MouseLeaveEvent});
    private static void Select(ProcessesPage page,string app){page.Grid.SelectedItem=Row(page,app);Layout(page);}
    private static void Select(ProcessesPage page,int pid){page.Grid.SelectedItem=Row(page,pid);Layout(page);}
    private static string? Selected(ProcessesPage page)=>(page.Grid.SelectedItem as ProcessRowView)?.Name;
    private static bool IsExpanded(ProcessesPage page,string app)=>Row(page,app).Expanded;
    private static void SetSearch(ProcessesPage page,string text){SearchBox(page).Text=text;Layout(page);}
    /// <summary>The line under the top strip that shows End task's and Open file location's result.</summary>
    private static string StatusText(ProcessesPage page)=>((TextBlock)typeof(ProcessesPage).GetField("actionStatus",All)!.GetValue(page)!).Text;

    private static ScrollViewer Scroller(ProcessesPage page){Layout(page);return Shown(page.Grid).OfType<ScrollViewer>().First();}
    private static void ScrollTo(ProcessesPage page,double offset){Scroller(page).ScrollToVerticalOffset(offset);Pump();Layout(page);}
    private static double VerticalOffset(ProcessesPage page)=>Scroller(page).VerticalOffset;

    /// <summary>A key pressed with the table focused: PreviewKeyDown, then KeyDown unless handled, as the keyboard sends it.
    /// Offscreen the keyboard's modifier state cannot be set, so a key with modifiers runs the page's key binding for it, as CommandManager would.</summary>
    private static void Press(ProcessesPage page,Key key,ModifierKeys modifiers=ModifierKeys.None)
    {
        if(modifiers==ModifierKeys.None)Press(page.Grid,key);
        else
        {
            var binding=Assert.Single(page.InputBindings.OfType<KeyBinding>(),b=>b.Key==key&&b.Modifiers==modifiers);
            Assert.IsAssignableFrom<RoutedCommand>(binding.Command).Execute(binding.CommandParameter,binding.CommandTarget??page);
        }
        Layout(page);
    }
    private static void Press(UIElement target,Key key)
    {
        var preview=new KeyEventArgs(Keyboard.PrimaryDevice,new KeySource(),0,key) {RoutedEvent=Keyboard.PreviewKeyDownEvent};
        target.RaiseEvent(preview);
        if(!preview.Handled)target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,new KeySource(),0,key) {RoutedEvent=Keyboard.KeyDownEvent});
    }
    /// <summary>The input source a key event must name; it is never added to the sources, and offscreen nothing reads it.</summary>
    private sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual {get=>null!;set{}}
        protected override CompositionTarget GetCompositionTargetCore()=>null!;
        public override bool IsDisposed=>false;
    }

    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    /// <summary>Pumps the dispatcher until <paramref name="done"/> holds, for up to 5 s, so a result an action posts from its own thread arrives.</summary>
    private static void WaitFor(Func<bool> done)
    {
        if(!done())
        {
            var frame=new DispatcherFrame();var deadline=Stopwatch.StartNew();
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(5)};
            timer.Tick+=(_,_)=>{if(done()||deadline.Elapsed>=TimeSpan.FromSeconds(5))frame.Continue=false;};
            timer.Start();
            try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
        }
        Assert.True(done(),"The Processes page did not get there within 5 s.");
    }
}
