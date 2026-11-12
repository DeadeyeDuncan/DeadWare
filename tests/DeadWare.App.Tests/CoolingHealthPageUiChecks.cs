using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Real offscreen Cooling Health layout, using an unstarted service and no hardware collector.</summary>
internal static class CoolingHealthPageUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"deadware-cooling-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var settings=new UserSettings();var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
            var cooling=new CoolingHistoryService(Path.Combine(root,"cooling-history.db"),"fixture",settings.CoolingConfigurationId);
            Set(session,"<Settings>k__BackingField",settings);Set(session,"<Cooling>k__BackingField",cooling);
            var empty=cooling.ReadViewAsync(null,null,1).GetAwaiter().GetResult();
            WideAndNarrow(session,empty);ControlsAndTabs(session,empty);FailureShowsInControls(session,empty);ScrollAndRemeasure(session,empty);Warnings(session,cooling,empty);RenderAtBothHeights();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            for(int attempt=0;Directory.Exists(root);attempt++)
            {
                try {foreach(string file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);Directory.Delete(root,true);}
                catch(Exception error) when(attempt<4&&error is IOException or UnauthorizedAccessException){Thread.Sleep(50*(attempt+1));}
            }
        }
    }

    private static void WideAndNarrow(UiSession session,CoolingView empty)
    {
        var page=Page(session,empty,1300,700);var body=Assert.IsType<Grid>(page.Content);
        var card=Field<Border>(page,"historyCard");var history=Assert.IsType<Grid>(card.Child);var panel=Field<Grid>(page,"assessmentPanel");
        var chart=Field<ThermalHistoryChart>(page,"chart");var heading=Field<TextBlock>(page,"assessmentHeading");var tabs=Field<TabControl>(page,"tabs");
        var tabItems=tabs.Items.Cast<TabItem>().ToArray();var narrow=Field<Border>(page,"narrowAssessmentCard");
        Assert.Same(history,panel.Parent);Assert.Equal(1,Grid.GetColumn(panel));Assert.Single(history.RowDefinitions);Assert.True(history.RowDefinitions[0].Height.IsStar);
        Assert.Equal(280,history.ColumnDefinitions[1].Width.Value);Assert.InRange(panel.ActualWidth,271.9,272.1);
        Assert.Equal(new Thickness(0,10,0,0),card.Margin);Assert.Equal(360,card.MinHeight);Assert.True(card.ActualHeight>=360);Assert.Equal(370,body.RowDefinitions[2].MinHeight);
        Assert.True(double.IsFinite(body.Height));Assert.True(double.IsNaN(chart.Height));Assert.Equal(180,chart.MinHeight);Assert.True(chart.ActualHeight>220);
        Assert.Equal(VerticalAlignment.Stretch,chart.VerticalAlignment);var column=Assert.IsType<Grid>(chart.Parent);Assert.True(column.RowDefinitions[Grid.GetRow(chart)].Height.IsStar);
        double before=chart.ActualHeight;Layout(page,1300,1000);Assert.True(chart.ActualHeight>before+250,$"Chart grew from {before} to {chart.ActualHeight}");
        foreach(var (width,beside) in new[]{(900d,false),(1300d,true),(1111d,false),(1112d,true),(900d,false),(1300d,true)})
        {
            Layout(page,width,700);Assert.Same(chart,Field<ThermalHistoryChart>(page,"chart"));Assert.Same(heading,Field<TextBlock>(page,"assessmentHeading"));
            Assert.Same(tabs,Field<TabControl>(page,"tabs"));Assert.Equal(tabItems,tabs.Items.Cast<TabItem>());Assert.Same(narrow,Field<Border>(page,"narrowAssessmentCard"));
            Assert.Equal(beside?280:0,history.ColumnDefinitions[1].Width.Value);Assert.Equal(beside?new Thickness(8,0,0,0):new Thickness(0),panel.Margin);
            Assert.Equal(beside,panel.RowDefinitions[1].Height.IsStar);
            if(beside){Assert.Same(history,panel.Parent);Assert.Null(narrow.Child);Assert.DoesNotContain(narrow,body.Children.Cast<UIElement>());}
            else
            {
                Assert.Same(panel,narrow.Child);Assert.Equal(1,Grid.GetRow(narrow));Assert.True(body.Children.IndexOf(narrow)<body.Children.IndexOf(card));
                Assert.True(narrow.TranslatePoint(new Point(),body).Y+narrow.ActualHeight<=card.TranslatePoint(new Point(),body).Y);
            }
        }
        Assert.False(page.IsLoaded);
    }

    private static void FailureShowsInControls(UiSession session,CoolingView empty)
    {
        // A recording failure lives in the Recording & setup tab's status; with Reference selected it must still show in the controls card.
        var page=Page(session,empty,1300,700);var controls=Field<Border>(page,"controlsCard");var warning=Field<TextBlock>(page,"recordingWarning");
        Assert.Equal(Visibility.Collapsed,warning.Visibility);
        page.ApplyView(empty with {Status="History recording unavailable (IOException). Live monitoring continues. Resolve the storage problem, then restart DeadWare to retry."});Layout(page,1300,700);
        Assert.Same(page.ReferenceTab,page.Tabs.SelectedItem);Assert.Equal(Visibility.Visible,warning.Visibility);Assert.Contains(warning,Descendants(controls));Assert.StartsWith("History recording unavailable",warning.Text);
        Assert.StartsWith("History recording unavailable",Field<TextBlock>(page,"recordingStatus").Text);
        page.ApplyView(empty with {Status="Recording local history · minute windows"});Layout(page,1300,700);
        Assert.Equal(Visibility.Collapsed,warning.Visibility);
    }

    private static void ControlsAndTabs(UiSession session,CoolingView empty)
    {
        var page=Page(session,empty,1300,700);var controls=Field<Border>(page,"controlsCard");var chart=Field<ThermalHistoryChart>(page,"chart");
        var header=Assert.Single(Assert.IsType<Grid>(chart.Parent).Children.OfType<DockPanel>());
        var range=Field<ComboBox>(page,"range");var metric=Field<ComboBox>(page,"metric");var device=Field<ComboBox>(page,"device");var status=Field<TextBlock>(page,"actionStatus");
        Assert.Contains(range,Descendants(header));Assert.Contains(metric,Descendants(header));Assert.Equal(Dock.Right,DockPanel.GetDock(header.Children[0]));
        Assert.Contains(device,Descendants(controls));Assert.Contains(status,Descendants(controls));Assert.Equal(new Thickness(12,6,12,6),controls.Padding);Assert.Equal(new Thickness(0,0,0,10),controls.Margin);
        var refresh=Assert.Single(Descendants(controls).OfType<Button>(),b=>Equals(b.Content,"Refresh"));var export=Field<Button>(page,"export");Assert.Contains(export,Descendants(controls));
        Assert.Equal(Visibility.Collapsed,status.Visibility);
        var tabs=Field<TabControl>(page,"tabs");var reference=Tab(tabs,"Reference");var warnings=Tab(tabs,"Warnings");var setup=Tab(tabs,"Recording & setup");
        Assert.Same(tabs,typeof(CoolingHealthPage).GetProperty("Tabs",All)!.GetValue(page));
        Assert.Same(reference,typeof(CoolingHealthPage).GetProperty("ReferenceTab",All)!.GetValue(page));
        Assert.Same(warnings,typeof(CoolingHealthPage).GetProperty("WarningsTab",All)!.GetValue(page));Assert.Same(setup,typeof(CoolingHealthPage).GetProperty("SetupTab",All)!.GetValue(page));
        Assert.Same(reference,tabs.SelectedItem);var editor=Assert.Single(Descendants(reference.Content as DependencyObject??reference).OfType<Expander>());Assert.False(editor.IsExpanded);
        Assert.DoesNotContain(Field<StackPanel>(page,"warningRows"),Descendants(page));Assert.DoesNotContain(Field<CheckBox>(page,"recording"),Descendants(page));
        tabs.SelectedItem=warnings;Layout(page,1300,700);Assert.Contains(Descendants(page).OfType<TextBlock>(),t=>t.Text.StartsWith("No warnings recorded",StringComparison.Ordinal));Assert.Empty(Descendants(warnings).OfType<Expander>());
        tabs.SelectedItem=setup;Layout(page,1300,700);Assert.Contains(Field<CheckBox>(page,"recording"),Descendants(page));
        tabs.SelectedItem=reference;Layout(page,1300,700);
        var operation=(Task)typeof(CoolingHealthPage).GetMethod("RunOperationAsync",All)!.Invoke(page,[new Func<Task<string>>(()=>Task.FromException<string>(new InvalidOperationException("Fixture operation failed")))])!;
        operation.GetAwaiter().GetResult();Layout(page,1300,700);Assert.Equal(Visibility.Visible,status.Visibility);Assert.Equal("Fixture operation failed",status.Text);Assert.Same(Application.Current.Resources["Warning"],status.Foreground);
        Layout(page,620,1400);Assert.True(double.IsFinite(Assert.IsType<Grid>(page.Content).Height));
        foreach(var element in new FrameworkElement[]{device,refresh,export,range,metric}.Concat(tabs.Items.Cast<TabItem>()))WithinPage(page,element);
        // Compact height still keeps every control reachable by scrolling, with no horizontal overflow.
        Layout(page,620,700);
        foreach(var element in new FrameworkElement[]{device,refresh,export,range,metric}.Concat(tabs.Items.Cast<TabItem>()))
        {element.BringIntoView();Pump();page.UpdateLayout();WithinPage(page,element);}
    }

    private static void ScrollAndRemeasure(UiSession session,CoolingView empty)
    {
        var longView=empty with {Assessment=empty.Assessment with {Message=string.Join(" ",Enumerable.Repeat("Long assessment evidence needs room to wrap and scroll.",100))}};
        var page=Page(session,longView,1300,700);var body=Assert.IsType<Grid>(page.Content);var panel=Field<Grid>(page,"assessmentPanel");var scroll=Assert.Single(Descendants(panel).OfType<ScrollViewer>());
        Assert.True(scroll.ScrollableHeight>0);Assert.InRange(body.Height,370,700);Assert.InRange(page.ScrollableHeight,0,1);
        Assert.All(new[]{"assessmentHeading","assessmentText","comparison","coverage"},name=>Assert.Equal(TextWrapping.Wrap,Field<TextBlock>(page,name).TextWrapping));
        page.ApplyView(empty);Layout(page,900,500);Assert.True(page.ScrollableHeight>0);Assert.InRange(scroll.ScrollableHeight,0,.5);
        Field<TextBlock>(page,"assessmentText").RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,-120){RoutedEvent=UIElement.PreviewMouseWheelEvent});
        Pump();page.UpdateLayout();Assert.True(page.VerticalOffset>0,"The assessment's unused wheel reaches the page itself.");
        page=Page(session,empty,1300,700);body=Assert.IsType<Grid>(page.Content);var tabs=Field<TabControl>(page,"tabs");var editor=Assert.Single(Descendants(page).OfType<Expander>());
        double folded=body.Height;editor.IsExpanded=true;Pump();page.UpdateLayout();Assert.True(body.Height>folded,$"Expanded editor must increase body height {folded} -> {body.Height}");
        editor.IsExpanded=false;Pump();page.UpdateLayout();Assert.InRange(body.Height,370,700);
        var warnings=Enumerable.Range(0,12).Select(i=>Warning(i)).ToArray();page.ApplyView(empty with {Warnings=warnings});Pump();page.UpdateLayout();
        double hidden=body.Height;tabs.SelectedItem=tabs.Items[1];Pump();page.UpdateLayout();Assert.True(body.Height>hidden,$"Warnings selection must increase body height {hidden} -> {body.Height}");
        tabs.SelectedItem=tabs.Items[0];Pump();page.UpdateLayout();Assert.InRange(body.Height,370,700);
    }

    private static void Warnings(UiSession session,CoolingHistoryService cooling,CoolingView empty)
    {
        var page=Page(session,empty with {Warnings=[Warning(0),Warning(1) with {Acknowledged=true}]},1300,700);var tabs=Field<TabControl>(page,"tabs");var tab=(TabItem)tabs.Items[1];
        Assert.Equal("Warnings · 1 need review",tab.Header);Assert.Same(tabs.Items[0],tabs.SelectedItem);Assert.Equal(2,Field<StackPanel>(page,"warningRows").Children.Count);
        page.ApplyView(empty with {Warnings=[Warning(0) with {Acknowledged=true},Warning(1) with {Acknowledged=true}]});
        Assert.Equal("Warnings",tab.Header);tabs.SelectedItem=tab;Layout(page,1300,700);Assert.DoesNotContain(Descendants(page).OfType<Button>(),b=>Equals(b.Content,"Acknowledge"));
        // Initialize only the fixture store, never Start() the service or a real session.
        var store=Field<ThermalStore>(cooling,"store");store.Initialize();var now=DateTimeOffset.UtcNow;var device=new ThermalDevice("cpu","Fixture CPU","CPU",session.Settings.CoolingConfigurationId,"map");
        store.Append([new(device,"fixture",now.AddMinutes(-2),now.AddMinutes(-1),0,60,60,new(60,40,60,50),new(60,50,50,50),new(60,100,100,100),new(0,null,null,null),new(0,null,null,null),true,"Fixture")],now);
        store.AddWarning(Warning(2) with {ConfigurationId=device.ConfigurationId,Created=now});Set(cooling,"initialized",true);
        page.ApplyView(cooling.ReadViewAsync(device.Id,device.ConfigurationId,1).GetAwaiter().GetResult());tabs.SelectedItem=tabs.Items[0];Layout(page,1300,700);Assert.Equal("Warnings · 1 need review",tab.Header);
        tabs.SelectedItem=tab;Layout(page,1300,700);var acknowledge=Assert.Single(Descendants(page).OfType<Button>(),b=>Equals(b.Content,"Acknowledge"));
        // Supply the synchronization context a real dispatched WPF click has, so async UI continuations return to this STA.
        var context=SynchronizationContext.Current;
        try {SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());acknowledge.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));}
        finally {SynchronizationContext.SetSynchronizationContext(context);}
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(Field<bool>(page,"operationBusy")&&DateTime.UtcNow<deadline){Pump();Thread.Sleep(1);}Assert.False(Field<bool>(page,"operationBusy"));
        Assert.Equal("Warning acknowledged.",Field<TextBlock>(page,"actionStatus").Text);
        // Offscreen pages deliberately skip automatic refresh; apply the persisted result explicitly.
        var acknowledged=cooling.ReadViewAsync(device.Id,device.ConfigurationId,1).GetAwaiter().GetResult();Assert.True(Assert.Single(acknowledged.Warnings).Acknowledged);
        page.ApplyView(acknowledged);Layout(page,1300,700);Assert.Equal("Warnings",tab.Header);Assert.DoesNotContain(Descendants(page).OfType<Button>(),b=>Equals(b.Content,"Acknowledge"));
    }

    private static void RenderAtBothHeights()
    {
        var chart=new ThermalHistoryChart();var from=DateTimeOffset.UnixEpoch;
        var windows=Enumerable.Range(0,6).Select(i=>new ThermalWindow(new("cpu","Fixture CPU","CPU","setup","map"),"fixture",from.AddMinutes(i),from.AddMinutes(i+1),i*60,60,60,
            new(60,40+i,60+i,50+i),new(60,50,50,50),new(60,100,100,100),new(0,null,null,null),new(0,null,null,null),true,"Fixture")).ToArray();
        chart.SetData(windows,"temperature",from,from.AddMinutes(6));Assert.Equal(180,chart.MinHeight);
        foreach(double height in new[]{180d,400d})
        {
            chart.Measure(new Size(620,height));chart.Arrange(new Rect(0,0,620,height));chart.UpdateLayout();var bounds=new Rect(chart.RenderSize);var drawing=VisualTreeHelper.GetDrawing(chart)!;
            Assert.True(bounds.Contains(chart.PlotRect));var glyphs=Drawings(drawing).OfType<GlyphRunDrawing>().ToArray();Assert.NotEmpty(glyphs);
            Assert.All(glyphs,g=>Assert.True(bounds.Contains(g.Bounds),$"Axis label {g.Bounds} outside {bounds}"));
            var shapes=Drawings(drawing).OfType<GeometryDrawing>().ToArray();Assert.Equal(6,shapes.Count(s=>s.Geometry is EllipseGeometry));
            Assert.All(shapes.Where(s=>s.Geometry is EllipseGeometry or StreamGeometry),s=>Assert.True(bounds.Contains(s.Bounds),$"Plot drawing {s.Bounds} outside {bounds}"));
        }
    }

    private static CoolingWarning Warning(int index)=>new("warning-"+index,"cpu","setup","reference",DateTimeOffset.UnixEpoch.AddMinutes(index),"A sustained cooling change needs review; compare workload and room temperature before changing the setup.",7);
    private static CoolingHealthPage Page(UiSession session,CoolingView view,double width,double height){var page=new CoolingHealthPage(session);page.ApplyView(view);Layout(page,width,height);return page;}
    private static TabItem Tab(TabControl tabs,string header)=>Assert.Single(tabs.Items.Cast<TabItem>(),t=>Equals(t.Header,header));
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,All)!.GetValue(target)!;
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,All)!.SetValue(target,value);
    private static void Layout(FrameworkElement page,double width,double height)
    {page.Measure(new Size(width,height));page.Arrange(new Rect(0,0,width,height));page.UpdateLayout();Pump();page.Measure(new Size(width,height));page.Arrange(new Rect(0,0,width,height));page.UpdateLayout();}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    private static void WithinPage(FrameworkElement page,FrameworkElement element)
    {Assert.True(element.ActualWidth>0&&element.ActualHeight>0,$"{element} has no size");var bounds=new Rect(element.TranslatePoint(new Point(),page),element.RenderSize);Assert.True(new Rect(-1,-1,page.ActualWidth+2,page.ActualHeight+2).Contains(bounds),$"{element} at {bounds} outside page {page.RenderSize}");}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static IEnumerable<Drawing> Drawings(Drawing drawing)=>drawing is DrawingGroup group?group.Children.SelectMany(Drawings):[drawing];
}
