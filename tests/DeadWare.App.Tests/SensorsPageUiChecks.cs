using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>0.17.12: the Sensors count and statistics line shares the search card, beside the search on a wide page and under it on a narrow one, and the table takes the rest.</summary>
internal static class SensorsPageUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    internal static void Run(MainWindow dashboard)
    {
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,All)!.GetValue(dashboard)!;
        void Navigate(string route)=>typeof(MainWindow).GetMethod("Navigate",All)!.Invoke(dashboard,[route]);
        var shell=(FrameworkElement)dashboard.Content;var previousSize=shell.RenderSize;var pages=Field<Dictionary<string,FrameworkElement>>("pages");
        var count=Field<TextBlock>("sensorCount");var search=Field<TextBox>("sensorSearch");var sensors=Field<DataGrid>("sensors");
        string previousRoute=Field<string>("page"),previousCount=count.Text;var previousRows=sensors.ItemsSource;
        void Layout(Size size){shell.Measure(size);shell.Arrange(new Rect(size));shell.UpdateLayout();Settle(shell);}
        try
        {
            Navigate("sensors");Layout(new Size(1300,700));
            // A refresh may rewrite the line; it keeps this shape and length, which is all the geometry below depends on.
            count.Text="128 sensors · statistics over 5 minutes · — means unavailable";
            sensors.ItemsSource=Enumerable.Range(0,40).Select(i=>new{Name=$"Fixture sensor {i:00}",Current="60.0 °C",Minimum="45.0 °C",Average="55.0 °C",Maximum="65.0 °C"}).ToArray();
            Layout(new Size(1300,700));
            var page=Assert.IsType<Grid>(pages["sensors"]);Assert.Equal(2,page.RowDefinitions.Count);Assert.Equal(2,page.Children.Count);
            Assert.Equal(new GridLength(1,GridUnitType.Star),page.RowDefinitions[1].Height);
            var strip=Assert.Single(page.Children.OfType<Border>(),card=>search.IsDescendantOf(card));
            var table=Assert.Single(page.Children.OfType<Border>(),card=>sensors.IsDescendantOf(card));
            Assert.True(count.IsDescendantOf(strip));Assert.IsType<WrapPanel>(strip.Child);
            Assert.Equal("Search sensors",System.Windows.Automation.AutomationProperties.GetName(search));Assert.Equal(280,search.Width);
            Rect Bounds(FrameworkElement element)=>element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
            void Stacked()
            {
                Rect stripBox=Bounds(strip),tableBox=Bounds(table);
                Assert.True(stripBox.Bottom<=tableBox.Top&&tableBox.Bottom<=page.ActualHeight+.5,$"strip {stripBox}, table {tableBox}, page {page.ActualHeight}");
                Assert.True(Bounds(count).Right<=stripBox.Right+.5&&Bounds(count).Bottom<=stripBox.Bottom+.5,$"count {Bounds(count)} inside strip {stripBox}");
                Assert.True(tableBox.Height>200,$"table is {tableBox.Height} high");
            }
            // Wide: the line sits beside the search, centred on it.
            Assert.True(page.ActualWidth>=1000,$"page is {page.ActualWidth} wide");
            Rect searchBox=Bounds(search),countBox=Bounds(count);
            Assert.True(countBox.Left>=searchBox.Right,$"count {countBox} beside search {searchBox}");
            Assert.InRange(Math.Abs((searchBox.Top+searchBox.Height/2)-(countBox.Top+countBox.Height/2)),0,4);
            Stacked();double wideStrip=Bounds(strip).Height;
            // Narrow: the page is 620 DIP and the line wraps under the search, so the card grows and the table gives up that height.
            var wide=new Size(1300,700);Layout(new Size(wide.Width+620-page.ActualWidth,wide.Height));Assert.InRange(page.ActualWidth,619.5,620.5);
            Assert.True(Bounds(count).Top>=Bounds(search).Bottom-.5,$"count {Bounds(count)} under search {Bounds(search)}");
            Stacked();Assert.True(Bounds(strip).Height>wideStrip);
        }
        finally
        {
            Navigate(previousRoute);count.Text=previousCount;sensors.ItemsSource=previousRows;Layout(previousSize);
        }
    }
    private static void Settle(FrameworkElement shell){for(int i=0;i<2;i++){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);shell.UpdateLayout();}}
}
