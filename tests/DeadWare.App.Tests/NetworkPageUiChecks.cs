using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Network fills the shell's remaining space, wraps its controls and scrolls safely at the window minimum.</summary>
internal static class NetworkPageUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private const string Empty="No attributed traffic yet. Check the accounting status above.";
    internal static void Run(MainWindow dashboard)
    {
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,All)!.GetValue(dashboard)!;
        void Navigate(string route)=>typeof(MainWindow).GetMethod("Navigate",All)!.Invoke(dashboard,[route]);
        var shell=(FrameworkElement)dashboard.Content;var previousSize=shell.RenderSize;var pages=Field<Dictionary<string,FrameworkElement>>("pages");
        var status=Field<TextBlock>("networkStatus");var count=Field<TextBlock>("networkEmpty");var search=Field<TextBox>("appSearch");var apps=Field<DataGrid>("apps");
        string previousRoute=Field<string>("page"),previousStatus=status.Text,previousCount=count.Text;var previousRows=apps.ItemsSource;
        var frame=new Size(2*SystemParameters.ResizeFrameVerticalBorderWidth,SystemParameters.WindowCaptionHeight+2*SystemParameters.ResizeFrameHorizontalBorderHeight);
        Size Outer(double width,double height)=>new(width-frame.Width,height-frame.Height);
        void Layout(Size size){shell.Measure(size);shell.Arrange(new Rect(size));shell.UpdateLayout();Settle(shell);}
        try
        {
            Navigate("network");status.Text="Application accounting is active.";count.Text="12 applications";
            apps.ItemsSource=Enumerable.Range(0,40).Select(i=>new{Name=$"Fixture application {i:00}",Pid=1000+i,Download="12.3 KB/s",Upload="3.4 KB/s",Received="1.2 MB",Sent="3.4 MB"}).ToArray();
            Layout(Outer(1300,700));
            var page=Assert.IsAssignableFrom<ScrollViewer>(pages["network"]);Assert.Equal("FillingScroller",page.GetType().Name);
            var body=Assert.IsType<Grid>(page.Content);Assert.Equal(4,body.RowDefinitions.Count);
            var chartCard=Assert.Single(body.Children.OfType<ChartCard>());var chart=Assert.Single(Descendants(chartCard).OfType<HistoryChart>());
            var controls=Assert.Single(body.Children.OfType<Border>(),card=>Descendants(card).Contains(search));
            var about=Assert.Single(body.Children.OfType<Border>(),card=>Descendants(card).OfType<Expander>().Any());var expander=Assert.Single(Descendants(about).OfType<Expander>());
            Assert.Equal("About these totals",expander.Header);Assert.False(expander.IsExpanded);
            var explanation=Assert.IsType<TextBlock>(expander.Content);Assert.StartsWith("Per-app totals cover each current process lifetime",explanation.Text);
            Assert.Contains(status,Descendants(controls));Assert.Contains(count,Descendants(controls));Assert.DoesNotContain(count,Descendants(about));
            Assert.Single(Descendants(controls).OfType<WrapPanel>());Assert.Equal("Search applications",AutomationProperties.GetName(search));Assert.Equal(250,search.Width);
            Assert.Equal(new Thickness(14,10,14,10),controls.Padding);Assert.Equal(new Thickness(6,2,6,10),controls.Margin);
            Assert.Equal(new Thickness(14,6,14,6),about.Padding);Assert.Equal(new Thickness(6,10,6,6),about.Margin);
            Assert.True(double.IsPositiveInfinity(body.RowDefinitions[0].MaxHeight));Assert.Equal(new GridLength(1,GridUnitType.Star),body.RowDefinitions[0].Height);Assert.Equal(new GridLength(1,GridUnitType.Star),body.RowDefinitions[2].Height);
            Assert.Equal(0,chart.MinHeight);MinimumRows(body);
            double minimum=body.RowDefinitions[1].ActualHeight+body.RowDefinitions[3].ActualHeight+300+160;
            if(minimum>page.ViewportHeight+.5)Assert.True(page.ScrollableHeight>0);else Assert.InRange(page.ScrollableHeight,0,.5);
            // Tall windows: the body fills the viewport exactly (no unused space, no needless page scrolling).
            void Fills(){Assert.InRange(body.ActualHeight,page.ViewportHeight-.5,page.ViewportHeight+.5);Assert.InRange(page.ScrollableHeight,0,.5);}
            Layout(Outer(1300,1000));MinimumRows(body);Fills();
            Assert.True(body.RowDefinitions[0].ActualHeight>300);Assert.True(body.RowDefinitions[2].ActualHeight>160);
            double tallChart=body.RowDefinitions[0].ActualHeight,tallTable=body.RowDefinitions[2].ActualHeight,tallClient=shell.ActualHeight;
            Layout(Outer(1300,1300));double clientGrowth=shell.ActualHeight-tallClient;Fills();
            Assert.True(body.RowDefinitions[0].ActualHeight-tallChart>=clientGrowth*.4);Assert.True(body.RowDefinitions[2].ActualHeight>tallTable);
            // Widths below refer to the page, while the shell still owns every layout pass.
            void PageWidth(double width){var size=Outer(1300,700);Layout(size);Layout(new Size(size.Width+width-page.ActualWidth,size.Height));Assert.InRange(page.ActualWidth,width-.5,width+.5);}
            PageWidth(620);count.Text="12 applications";Settle(shell);Assert.True(Bounds(count,controls).Right<=controls.ActualWidth+.5);
            count.Text=Empty;Settle(shell);Assert.True(Bounds(count,controls).Top>=Bounds(search,controls).Bottom-.5,"The long empty-state message must wrap below the search at 620 page DIP.");
            PageWidth(1300);var searchBounds=Bounds(search,controls);var countBounds=Bounds(count,controls);
            Assert.InRange(Math.Abs((searchBounds.Top+searchBounds.Height/2)-(countBounds.Top+countBounds.Height/2)),0,4);
            Assert.True(countBounds.Left>=searchBounds.Right);
            // The grid retains its own finite, virtualized viewport despite the page's short-height fallback.
            count.Text="12 applications";Layout(Outer(1300,700));
            var tableScroll=Assert.Single(Descendants(apps).OfType<ScrollViewer>());
            Assert.True(double.IsFinite(tableScroll.ViewportHeight)&&tableScroll.ViewportHeight>0);Assert.True(tableScroll.ExtentHeight>tableScroll.ViewportHeight);
            Assert.Equal(Visibility.Visible,tableScroll.ComputedVerticalScrollBarVisibility);Assert.Same(page,Assert.Single(Ancestors(apps).OfType<ScrollViewer>()));
            var row=Assert.IsType<DataGridRow>(apps.ItemContainerGenerator.ContainerFromIndex(0));var scrollers=Ancestors(row).OfType<ScrollViewer>().ToArray();
            Assert.Equal(2,scrollers.Length);Assert.Contains(tableScroll,scrollers);Assert.Contains(page,scrollers);
            Assert.InRange(Descendants(apps).OfType<DataGridRow>().Count(),1,29);
            tableScroll.ScrollToEnd();Settle(shell);Assert.True(tableScroll.VerticalOffset>0);Assert.NotNull(apps.ItemContainerGenerator.ContainerFromIndex(39));
            // A wheel the table cannot use reaches the page: at its end, wheel-down scrolls the page; at its top, wheel-up scrolls the page back.
            {
                Layout(Outer(900,640));Settle(shell);
                if(page.ScrollableHeight>0)
                {
                    tableScroll.ScrollToEnd();page.ScrollToTop();Settle(shell);double before=page.VerticalOffset;
                    var lastRow=(UIElement)apps.ItemContainerGenerator.ContainerFromIndex(39)!;
                    lastRow.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,-120) {RoutedEvent=UIElement.PreviewMouseWheelEvent});Settle(shell);
                    Assert.True(page.VerticalOffset>before,"Wheel-down at the table's end scrolls the page.");
                    tableScroll.ScrollToTop();Settle(shell);double scrolled=page.VerticalOffset;
                    var first=(UIElement)apps.ItemContainerGenerator.ContainerFromIndex(0)!;
                    first.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,120) {RoutedEvent=UIElement.PreviewMouseWheelEvent});Settle(shell);
                    Assert.True(page.VerticalOffset<scrolled,"Wheel-up at the table's top scrolls the page back.");
                    // A table that can still scroll keeps the wheel.
                    tableScroll.ScrollToTop();page.ScrollToTop();Settle(shell);
                    first.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,-120) {RoutedEvent=UIElement.PreviewMouseWheelEvent});Settle(shell);
                    Assert.InRange(page.VerticalOffset,0,.5);
                }
            }
            Assert.InRange(Descendants(apps).OfType<DataGridRow>().Count(),1,29);tableScroll.ScrollToTop();Settle(shell);
            expander.IsExpanded=true;Settle(shell);MinimumRows(body);Assert.Contains(explanation,Descendants(expander));
            expander.IsExpanded=false;Settle(shell);
            Layout(Outer(900,640));MinimumRows(body);Assert.True(page.ScrollableHeight>0);
            double collapsedHeight=body.Height;expander.IsExpanded=true;Settle(shell);Assert.True(body.Height>collapsedHeight);MinimumRows(body);
            double expandedHeight=body.Height;
            status.Text=string.Join(" ",Enumerable.Repeat("Application accounting unavailable; restart as administrator to attribute application traffic.",5));Settle(shell);
            Assert.True(status.ActualHeight>=status.FontSize*3);Assert.True(body.Height>expandedHeight);Assert.True(page.ScrollableHeight>0);MinimumRows(body);
            AssertChartInsideCard(chart,chartCard);
            expander.IsExpanded=false;Settle(shell);
        }
        finally
        {
            foreach(var expander in Descendants(pages["network"]).OfType<Expander>())expander.IsExpanded=false;
            Navigate(previousRoute);status.Text=previousStatus;count.Text=previousCount;apps.ItemsSource=previousRows;Layout(previousSize);
        }
    }
    private static void MinimumRows(Grid body){Assert.True(body.RowDefinitions[0].ActualHeight>=299.5,$"Chart row: {body.RowDefinitions[0].ActualHeight}");Assert.True(body.RowDefinitions[2].ActualHeight>=159.5,$"Table row: {body.RowDefinitions[2].ActualHeight}");}
    private static void AssertChartInsideCard(HistoryChart chart,ChartCard card)
    {
        var drawing=new DrawingGroup();using(var context=drawing.Open())typeof(HistoryChart).GetMethod("OnRender",All)!.Invoke(chart,[context]);
        var bounds=chart.TransformToAncestor(card).TransformBounds(drawing.Bounds);Assert.True(new Rect(-.5,-.5,card.ActualWidth+1,card.ActualHeight+1).Contains(bounds),$"Network drawing {bounds} is outside card {card.RenderSize}.");
        var labels=Glyphs(drawing).ToArray();Assert.Equal(3,labels.Count(g=>new string(g.GlyphRun.Characters.ToArray()).EndsWith("/s",StringComparison.Ordinal)));
        Assert.Contains(labels,g=>new string(g.GlyphRun.Characters.ToArray())=="Now");
        Assert.All(labels,g=>Assert.True(new Rect(-.5,-.5,chart.ActualWidth+1,chart.ActualHeight+1).Contains(g.Bounds),$"Axis label {g.Bounds} is outside chart {chart.RenderSize}."));
    }
    private static Rect Bounds(FrameworkElement element,Visual ancestor)=>element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static void Settle(FrameworkElement shell){for(int i=0;i<2;i++){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);shell.UpdateLayout();}}
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject value){for(var parent=VisualTreeHelper.GetParent(value);parent is not null;parent=VisualTreeHelper.GetParent(parent))yield return parent;}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)=>drawing is GlyphRunDrawing glyph?[glyph]:drawing is DrawingGroup group?group.Children.SelectMany(Glyphs):[];
}
