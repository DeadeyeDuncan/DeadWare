using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;
using DeadWare.App;
using DeadWare.Core;

namespace DeadWare.App.Tests;

internal static class LiveRenderingUiChecks
{
    internal static void Run()
    {
        string? output=Environment.GetEnvironmentVariable("DEADWARE_PERF_OUTPUT");
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        void Set(object value,string field,object data)=>value.GetType().GetField(field,flags)!.SetValue(value,data);
        var history=new SampleHistory();var epoch=new DateTimeOffset(2026,10,29,12,0,0,TimeSpan.Zero);
        int samples=output is null?8192:43200;
        for(int i=0;i<=samples;i++)
        {
            double time=(i-samples)*.5;
            history.Add(new(epoch.AddSeconds(time),time,new Dictionary<string,double?>
            {
                ["cpu/usage"]=30+i%31,["gpu/usage"]=i%101,["cpu/temperature"]=60+i%20,
                ["gpu/temperature"]=55+i%30,["gpu/fan"]=1000+i%1500,["memory/usage"]=48+i%3,
                ["network/download"]=i%181==0?20_000_000:150_000,["network/upload"]=80_000
            }){PollingIntervalSeconds=.5});
        }
        var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector,"clock",new Stopwatch());Set(collector,"<History>k__BackingField",history);
        Set(collector,"latest",new Reading(history.Read()[^1],[new("gpu","Fixture GPU")],[],[],"Fixture CPU","","","",false));
        var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session,"<Collector>k__BackingField",collector);Set(session,"<Settings>k__BackingField",new UserSettings {HistorySeconds=21600,SelectedGpuId="gpu",ShowGpuFanSpeed=true});
        var charts=new[]{"cpu","cputemp","memory","network"}.Select(k=>new HistoryChart(session,k,true){CompareDevices=k is "cpu" or "cputemp"}).ToArray();
        var map=new ConnectionMap {Flat=true,MapOnly=true,AnimationEnabled=false};
        var points=Enumerable.Range(0,256).Select(i=>new ConnectionMapPoint("perf"+i,new(i%100-50,i-128),100,100,false)).ToArray();
        map.SetData(points,new(41,-87),true,"Synthetic performance fixture");map.SetView(new(43,-103),2.5);
        void Draw(FrameworkElement visual)
        {
            var drawing=new DrawingGroup();using var context=drawing.Open();
            visual.GetType().GetMethod("OnRender",flags)!.Invoke(visual,[context]);
        }
        foreach(var chart in charts){chart.Measure(new(300,120));chart.Arrange(new Rect(0,0,300,120));chart.UpdateLayout();}
        map.Measure(new(300,180));map.Arrange(new Rect(0,0,300,180));map.UpdateLayout();Draw(map);
        var root=new Grid();foreach(var chart in charts)root.Children.Add(chart);
        using var source=new OffscreenSource(root);
        void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
        Pump();Assert.All(charts,chart=>Assert.True(chart.IsLoaded));
        bool Preparing()=>charts.Any(c=>(bool)typeof(HistoryChart).GetField("preparing",flags)!.GetValue(c)!);
        void WaitPrepared(){var wait=Stopwatch.StartNew();while(Preparing()&&wait.Elapsed<TimeSpan.FromSeconds(10)){Pump();Thread.Sleep(1);}Assert.False(Preparing());}
        // Repeated polls coalesce, and an old worker cannot overwrite a changed range.
        foreach(var chart in charts)Draw(chart);
        for(int i=0;i<20;i++)foreach(var chart in charts)chart.Refresh();
        WaitPrepared();
        var preparedField=typeof(HistoryChart).GetField("prepared",flags)!;
        object Prepared(HistoryChart chart)=>preparedField.GetValue(chart)!;
        var expected=history.Summarize("cpu/usage",0,21600);
        var statistics=(Summary)Prepared(charts[0]).GetType().GetProperty("Statistics")!.GetValue(Prepared(charts[0]))!;
        Assert.Equal(expected.Minimum,statistics.Minimum);Assert.Equal(expected.Maximum,statistics.Maximum);
        Assert.Equal(expected.Average!.Value,statistics.Average!.Value,8);
        charts[0].Refresh();session.Settings.HistorySeconds=60;charts[0].Refresh();Draw(charts[0]);
        var shortRange=Prepared(charts[0]);WaitPrepared();Assert.Same(shortRange,Prepared(charts[0]));
        session.Settings.HistorySeconds=21600;charts[0].Refresh();Draw(charts[0]);
        charts[0].Refresh();charts[0].Visibility=Visibility.Hidden;var beforeHide=Prepared(charts[0]);
        WaitPrepared();Assert.Same(beforeHide,Prepared(charts[0]));charts[0].Visibility=Visibility.Visible;
        // A polling-only map update keeps the projected paths; camera/origin changes replace them.
        object Route()=>((System.Collections.IEnumerable)typeof(ConnectionMap).GetField("routes",flags)!.GetValue(map)!).Cast<object>().First();
        object Segments()=>Route().GetType().GetProperty("Segments")!.GetValue(Route())!;
        var segments=Segments();map.SetData(points.Select(p=>p with {ReceivedBytes=200}).ToArray(),new(41,-87),true,"fixture");Draw(map);
        Assert.Same(segments,Segments());map.SetView(new(43,-102),2.5);Draw(map);Assert.NotSame(segments,Segments());
        segments=Segments();map.SetData(points,new(40,-87),true,"fixture");Draw(map);Assert.NotSame(segments,Segments());
        map.SetData(points,new(41,-87),true,"fixture");map.SetView(new(43,-103),2.5);Draw(map);
        // Styling changes the trace pen, and glow is a second inexpensive drawing pass.
        var geometry=Geometry.Parse("M 0,10 L 50,10 L 100,20");
        session.Settings.GraphStyles["cpu"]=new("dotted",true);
        var styled=new DrawingGroup();using(var context=styled.Open())GraphStroke.Draw(context,geometry,session.Settings,"Graph/cpu",2,false,false);
        IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
        var strokes=Shapes(styled).ToArray();Assert.Equal(SystemParameters.HighContrast?1:2,strokes.Length);
        Assert.Equal(new double[]{1,3},strokes[^1].Pen.DashStyle.Dashes);Assert.Equal(2,strokes[^1].Pen.Thickness);
        if(!SystemParameters.HighContrast)Assert.True(strokes[0].Pen.Thickness>strokes[1].Pen.Thickness);
        session.Settings.GraphStyles["cpu"]=new("dotted",true,2,2.5);
        var strong=new DrawingGroup();using(var context=strong.Open())GraphStroke.Draw(context,geometry,session.Settings,"Graph/cpu",2,false,false);
        var strongStrokes=Shapes(strong).ToArray();Assert.Equal(4,strongStrokes[^1].Pen.Thickness);
        if(!SystemParameters.HighContrast)
        {
            Assert.Equal(14,strongStrokes[0].Pen.Thickness);
            Assert.Contains(strong.Children.OfType<DrawingGroup>(),group=>Math.Abs(group.Opacity-.4)<.0001);
            Assert.Equal(4,strongStrokes[0].Pen.DashStyle.Dashes[0]*strongStrokes[0].Pen.Thickness,8);
        }
        session.Settings.GraphStyles["cpu"]=new("dotted",true,2,0);
        var zero=new DrawingGroup();using(var context=zero.Open())GraphStroke.Draw(context,geometry,session.Settings,"Graph/cpu",2,false,false);
        Assert.Equal(4,Assert.Single(Shapes(zero)).Pen.Thickness);
        session.Settings.GraphStyles.Clear();
        // At one hour, completed averages must translate left without changing their vertical shape.
        session.Settings.HistorySeconds=3600;
        Point[] RollingTrace(double now)
        {
            var rolling=history.ChartRanges("cpu/usage",now,3600,200)
                .Where(p=>p.StartSeconds>=-3000&&p.EndSeconds<=-1000)
                .Select(p=>new SamplePoint(DateTimeOffset.UnixEpoch,p.EndSeconds,p.Average)).ToArray();
            var drawing=new DrawingGroup();using(var context=drawing.Open())
                typeof(HistoryChart).GetMethod("DrawSeries",flags)!.Invoke(charts[0],[context,rolling,now,100d,0d,0d,600d,120d,"Graph/cpu",false,false,true]);
            var path=PathGeometry.CreateFromGeometry(Shapes(drawing).Single(shape=>shape.Geometry is StreamGeometry).Geometry);
            return path.Figures.SelectMany(figure=>new[]{figure.StartPoint}.Concat(figure.Segments.SelectMany(segment=>segment switch
                {PolyLineSegment poly=>poly.Points.AsEnumerable(),LineSegment line=>new[]{line.Point},_=>Array.Empty<Point>()}))).ToArray();
        }
        var earlier=RollingTrace(0);var later=RollingTrace(2);Assert.True(earlier.Length>20,$"Points: {earlier.Length}; ranges: {history.ChartRanges("cpu/usage",0,3600,200).Count}; history: {history.Count}");Assert.Equal(earlier.Length,later.Length);
        for(int i=0;i<earlier.Length;i++){Assert.Equal(earlier[i].Y,later[i].Y,8);Assert.Equal(earlier[i].X-600d*2/3600,later[i].X,8);}
        session.Settings.HistorySeconds=21600;
        var results=new List<object>();
        void Measure(string name,Action<int> action)
        {
            var times=new List<double>();var allocations=new List<long>();
            for(int i=0;i<(output is null?4:15);i++)
            {
                long before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();action(i);watch.Stop();
                if(i>2){times.Add(watch.Elapsed.TotalMilliseconds);allocations.Add(GC.GetAllocatedBytesForCurrentThread()-before);}
                WaitPrepared();
            }
            results.Add(new {Name=name,MedianMs=times.Order().ElementAt(times.Count/2),MaxMs=times.Max(),MedianAllocatedBytes=allocations.Order().ElementAt(allocations.Count/2)});
        }
        Measure("four-six-hour-charts",_=>{foreach(var chart in charts){chart.Refresh();Draw(chart);}});
        Measure("map-cached",_=>Draw(map));
        Measure("map-refresh",i=>{map.SetData(points.Select(p=>p with {ReceivedBytes=100+i}).ToArray(),new(41,-87),true,"Synthetic performance fixture");Draw(map);});
        Measure("map-pan",i=>{map.SetView(new(43,-103+i*.1),2.5);Draw(map);});
        Assert.All(charts,c=>Assert.False((bool)typeof(HistoryChart).GetField("preparationFailed",flags)!.GetValue(c)!));
        string? preview=Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        if(preview is not null)
        {
            Directory.CreateDirectory(preview);session.Settings.GraphStyles["cpu"]=new("solid",true);
            foreach(var (chart,index) in charts.Select((chart,index)=>(chart,index)))
            {
                chart.Measure(new(700,200));chart.Arrange(new Rect(0,0,700,200));chart.Refresh();
                var visual=new DrawingVisual();using(var context=visual.RenderOpen())
                {context.DrawRectangle(Theme.Brush("Background"),null,new Rect(0,0,700,200));typeof(HistoryChart).GetMethod("OnRender",flags)!.Invoke(chart,[context]);}
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(700,200,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(preview,$"long-range-{index}.png"));encoder.Save(file);
            }
        }
        if(output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output,JsonSerializer.Serialize(new {Note="Synthetic drawing-command CPU and allocations only; excludes native layout/compositor/GPU timing. Background preparation is awaited outside UI timing.",Results=results},new JsonSerializerOptions {WriteIndented=true}));
        }
    }
    private sealed class OffscreenSource : PresentationSource,IDisposable
    {
        private Visual? root;
        public OffscreenSource(Visual visual){AddSource();RootVisual=visual;}
        public override Visual RootVisual {get=>root!;set{var previous=root;root=value;RootChanged(previous,root);}}
        protected override CompositionTarget GetCompositionTargetCore()=>null!;
        public override bool IsDisposed=>false;
        public void Dispose(){RootVisual=null!;RemoveSource();}
    }
}
