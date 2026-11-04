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

public class HoverCoreLineTests
{
    [Fact] public void Hover_core_lines_take_the_eight_highest_and_count_the_rest()
    {
        var lines=HistoryChart.HoverCoreLines(Enumerable.Range(0,12).Select(i=>(i,(double)i*5)).ToArray());
        Assert.Equal(9,lines.Count);Assert.Equal("Core 12 · 55%",lines[0].Text);Assert.Equal("Core 5 · 20%",lines[7].Text);Assert.Equal(("+4 more",-1),lines[8]);
        Assert.Equal(3,HistoryChart.HoverCoreLines([(0,1),(1,2),(2,3)]).Count);
    }
}
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
            var values=new Dictionary<string,double?>
            {
                ["cpu/usage"]=30+i%31,["gpu/usage"]=i%101,["cpu/temperature"]=60+i%20,
                ["cpu/pump"]=2000+i%1200,["gpu/temperature"]=55+i%30,["gpu/fan"]=1000+i%1500,["memory/usage"]=48+i%3,
                ["network/download"]=i%181==0?20_000_000:150_000,["network/upload"]=80_000
            };
            // Eight per-core traces; core 3 drops out on every tenth sample.
            for(int core=0;core<8;core++)values[CpuCoreUsage.Key(core)]=core==3&&i%10==0?null:(core*11+i)%100;
            history.Add(new(epoch.AddSeconds(time),time,values){PollingIntervalSeconds=.5});
        }
        var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector,"clock",new Stopwatch());Set(collector,"<History>k__BackingField",history);
        Set(collector,"latest",new Reading(history.Read()[^1],[new("gpu","Fixture GPU")],[],[],"Fixture CPU","","","",false){CpuCoreCount=8});
        var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session,"<Collector>k__BackingField",collector);Set(session,"<Settings>k__BackingField",new UserSettings {HistorySeconds=21600,SelectedGpuId="gpu",ShowGpuFanSpeed=true});
        var charts=new[]{"cpu","cputemp","memory","network"}.Select(k=>new HistoryChart(session,k,true){CompareDevices=k is "cpu" or "cputemp"}).ToArray();
        var map=new ConnectionMap {Flat=true,MapOnly=true,AnimationEnabled=false};
        var points=Enumerable.Range(0,256).Select(i=>new ConnectionMapPoint("perf"+i,new(i%100-50,i-128),100,100,false)).ToArray();
        map.SetData(points,new(41,-87),true,"Synthetic performance fixture");map.SetView(new(43,-103),2.5);
        DrawingGroup Draw(FrameworkElement visual)
        {
            var drawing=new DrawingGroup();using var context=drawing.Open();
            visual.GetType().GetMethod("OnRender",flags)!.Invoke(visual,[context]);
            return drawing;
        }
        // Overview CPU card subtitle: core count only for the usage card, only with the switch on and cores present.
        var cards=new[]{"cpu","cputemp"}.Select(k=>new ChartCard(session,k,false,true)).ToArray();
        string Subtitle(ChartCard card)=>((TextBlock)typeof(ChartCard).GetField("subtitle",flags)!.GetValue(card)!).Text;
        void RefreshCard(ChartCard card)=>typeof(ChartCard).GetMethod("Refresh",flags)!.Invoke(card,null);
        foreach(var chart in charts){chart.Measure(new(300,120));chart.Arrange(new Rect(0,0,300,120));chart.UpdateLayout();}
        map.Measure(new(300,180));map.Arrange(new Rect(0,0,300,180));map.UpdateLayout();Draw(map);
        var root=new Grid();foreach(var chart in charts)root.Children.Add(chart);
        foreach(var card in cards)root.Children.Add(card);
        using var source=new OffscreenSource(root);
        void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
        Pump();Assert.All(charts,chart=>Assert.True(chart.IsLoaded));
        {
            var latest=collector.Latest!;
            session.Settings.ShowCpuCoreUsage=true;foreach(var card in cards)RefreshCard(card);
            Assert.EndsWith("Fixture CPU · 8 cores",Subtitle(cards[0]));Assert.Equal("Fixture CPU",Subtitle(cards[1]));
            session.Settings.ShowCpuCoreUsage=false;RefreshCard(cards[0]);Assert.Equal("Fixture CPU",Subtitle(cards[0]));
            session.Settings.ShowCpuCoreUsage=true;Set(collector,"latest",latest with {CpuCoreCount=0});
            RefreshCard(cards[0]);Assert.DoesNotContain("cores",Subtitle(cards[0]));
            Set(collector,"latest",latest);session.Settings.ShowCpuCoreUsage=false;
        }
        bool Preparing()=>charts.Any(c=>(bool)typeof(HistoryChart).GetField("preparing",flags)!.GetValue(c)!);
        void WaitPrepared(){var wait=Stopwatch.StartNew();while(Preparing()&&wait.Elapsed<TimeSpan.FromSeconds(10)){Pump();Thread.Sleep(1);}Assert.False(Preparing());}
        // Repeated polls coalesce, and an old worker cannot overwrite a changed range.
        foreach(var chart in charts)Draw(chart);
        for(int i=0;i<20;i++)foreach(var chart in charts)chart.Refresh();
        WaitPrepared();
        // Refined glass frame: solid 1 px hairline grid under a GuidelineSet, value labels inside the plot,
        // hover crosshair with a GlassPopup chip only over real samples, and a cache keyed on the theme.
        {
            var cpu=charts[0];var frame=Draw(cpu);
            object hairline=Application.Current.Resources["Hairline"],lineBrush=Application.Current.Resources["Line"];
            var pens=Shapes(frame).Where(s=>s.Pen is not null).Select(s=>s.Pen).ToArray();
            Assert.Contains(pens,pen=>ReferenceEquals(pen.Brush,hairline));
            Assert.All(pens.Where(pen=>ReferenceEquals(pen.Brush,hairline)||ReferenceEquals(pen.Brush,lineBrush)),pen=>{Assert.Empty(pen.DashStyle.Dashes);Assert.Equal(1,pen.Thickness);});
            Assert.True(HasGuidelines(frame),"Grid lines need a GuidelineSet to stay crisp at fractional DPI.");
            var plot=cpu.PlotRect;Assert.True(plot.Width>100&&plot.Height>30,$"Plot {plot}");
            foreach(var glyph in Glyphs(frame).Where(g=>new string(g.GlyphRun.Characters.ToArray()).EndsWith('%')))
                Assert.True(plot.Contains(glyph.Bounds),$"Value label {glyph.Bounds} lies outside the plot {plot}.");
            bool HasPopup(Drawing d)=>Shapes(d).Any(s=>ReferenceEquals(s.Brush,Application.Current.Resources["GlassPopup"]));
            Assert.False(HasPopup(frame));Assert.Equal(-1,cpu.HoverIndex);
            // The fixture holds the last 4096 s of the 6 h range: 95 % across lands on data, 1 px in does not.
            cpu.SetHover(new Point(plot.Left+plot.Width*.95,plot.Top+10));
            Assert.True(cpu.HoverIndex>=0,"Hover over recorded samples must resolve a sample.");Assert.True(HasPopup(Draw(cpu)));
            // The perf fixture (DEADWARE_PERF_OUTPUT) fills the whole 6 h range, so 1 px in has data there.
            if(output is null){cpu.SetHover(new Point(plot.Left+1,plot.Top+10));Assert.Equal(-1,cpu.HoverIndex);Assert.False(HasPopup(Draw(cpu)));}
            cpu.SetHover(null);Assert.Equal(-1,cpu.HoverIndex);
            // Hover re-renders reuse the frame's area fill instead of rebuilding it.
            cpu.SetHover(new Point(plot.Left+plot.Width*.95,plot.Top+10));
            var firstArea=Shapes(Draw(cpu)).FirstOrDefault(s=>s.Geometry is PathGeometry&&s.Brush is LinearGradientBrush)?.Geometry;
            cpu.SetHover(new Point(plot.Left+plot.Width*.9,plot.Top+10));
            var secondArea=Shapes(Draw(cpu)).FirstOrDefault(s=>s.Geometry is PathGeometry&&s.Brush is LinearGradientBrush)?.Geometry;
            if(firstArea is not null)Assert.Same(firstArea,secondArea);
            cpu.SetHover(null);
            // The chip's time comes from the wall clock captured with the frame, not from the clock at hover time.
            var wide=new HistoryChart(session,"cpu",false);wide.Measure(new(600,200));wide.Arrange(new Rect(0,0,600,200));wide.UpdateLayout();Draw(wide);
            var widePlot=wide.PlotRect;wide.SetHover(new Point(widePlot.Left+widePlot.Width*.95,widePlot.Top+10));Assert.True(wide.HoverIndex>=0);
            string Stamp()=>Glyphs(Draw(wide)).Select(g=>new string(g.GlyphRun.Characters.ToArray())).Single(t=>System.Text.RegularExpressions.Regex.IsMatch(t,@"^\d\d:\d\d:\d\d$"));
            string before=Stamp();Thread.Sleep(1100);Assert.Equal(before,Stamp());
            wide.SetHover(null);
            Theme.Apply("Ember");cpu.Refresh();
            var trace=Shapes(Draw(cpu)).First(s=>s.Geometry is StreamGeometry&&s.Pen is not null);
            Assert.Same(Application.Current.Resources["Graph/cpu"],trace.Pen.Brush);
            Theme.Apply("Midnight");cpu.Refresh();Draw(cpu);
        }
        // An RPM overlay draws its labels inside the plot: the plot keeps its geometry and the labels never touch the value labels.
        {
            var temp=new HistoryChart(session,"cputemp",false);temp.Measure(new(600,200));temp.Arrange(new Rect(0,0,600,200));temp.UpdateLayout();
            session.Settings.ShowCpuPumpSpeed=false;temp.Refresh();Draw(temp);
            void WaitTemp(){var wait=Stopwatch.StartNew();while((bool)typeof(HistoryChart).GetField("preparing",flags)!.GetValue(temp)!&&wait.Elapsed<TimeSpan.FromSeconds(10)){Pump();Thread.Sleep(1);}}
            WaitTemp();Draw(temp);var plain=temp.PlotRect;
            session.Settings.ShowCpuPumpSpeed=true;temp.Refresh();Draw(temp);WaitTemp();
            var overlay=Draw(temp);var plot=temp.PlotRect;
            Assert.Equal(plain.Left,plot.Left);Assert.Equal(plain.Width,plot.Width);
            string Text(GlyphRunDrawing g)=>new string(g.GlyphRun.Characters.ToArray());
            var rpm=Glyphs(overlay).Where(g=>Text(g).EndsWith("RPM")).ToArray();var degrees=Glyphs(overlay).Where(g=>Text(g).EndsWith('°')).ToArray();
            Assert.NotEmpty(rpm);Assert.NotEmpty(degrees);
            Assert.All(rpm,g=>Assert.True(plot.Contains(g.Bounds),$"RPM label {g.Bounds} lies outside the plot {plot}."));
            Assert.All(rpm,r=>Assert.DoesNotContain(degrees,d=>r.Bounds.IntersectsWith(d.Bounds)));
            temp.SetHover(new Point(plot.Left+plot.Width*.95,plot.Top+10));Assert.True(temp.HoverIndex>=0);temp.SetHover(null);
            session.Settings.ShowCpuPumpSpeed=false;
        }
        IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)=>drawing is GlyphRunDrawing glyph?[glyph]:drawing is DrawingGroup group?group.Children.SelectMany(Glyphs):[];
        bool HasGuidelines(Drawing drawing)=>drawing is DrawingGroup group&&(group.GuidelineSet is not null||group.Children.Any(HasGuidelines));
        var preparedField=typeof(HistoryChart).GetField("prepared",flags)!;
        object Prepared(HistoryChart chart)=>preparedField.GetValue(chart)!;
        var expected=history.Summarize("cpu/usage",0,21600);
        var statistics=(Summary)Prepared(charts[0]).GetType().GetProperty("Statistics")!.GetValue(Prepared(charts[0]))!;
        Assert.Equal(expected.Minimum,statistics.Minimum);Assert.Equal(expected.Maximum,statistics.Maximum);
        Assert.Equal(expected.Average!.Value,statistics.Average!.Value,8);
        bool HasRangeBand(Drawing drawing)=>Shapes(drawing).Any(shape=>shape.Geometry is StreamGeometry&&shape.Pen is null);
        Assert.True(HasRangeBand(Draw(charts[0])));
        // Toggle while an old smoothed request is in flight. It must never overwrite raw mode.
        charts[0].Refresh();session.Settings.GraphSmoothing=false;
        foreach(var chart in charts){chart.Refresh();Assert.False(HasRangeBand(Draw(chart)));}
        var rawPrepared=charts.Select(Prepared).ToArray();WaitPrepared();
        for(int i=0;i<charts.Length;i++)
        {
            Assert.Same(rawPrepared[i],Prepared(charts[i]));
            var data=Prepared(charts[i]);
            Assert.Empty((Dictionary<string,IReadOnlyList<ChartRangePoint>>)data.GetType().GetProperty("Ranges")!.GetValue(data)!);
            var series=(Dictionary<string,IReadOnlyList<SamplePoint>>)data.GetType().GetProperty("Series")!.GetValue(data)!;
            foreach(var (metric,rawPoints) in series)
            {
                var original=history.Series(metric,0,21600).Where(p=>p.Value.HasValue).ToDictionary(p=>p.MonotonicSeconds,p=>p.Value);
                Assert.All(rawPoints.Where(p=>p.Value.HasValue),point=>Assert.Equal(original[point.MonotonicSeconds],point.Value));
                Assert.Equal(original.Values.Min(),rawPoints.Min(p=>p.Value));Assert.Equal(original.Values.Max(),rawPoints.Max(p=>p.Value));
                Assert.InRange(rawPoints.Count,1,4*210);
            }
        }
        var rawSummary=(Summary)rawPrepared[0].GetType().GetProperty("Statistics")!.GetValue(rawPrepared[0])!;
        Assert.Equal(expected,rawSummary); // Statistics use all readings, not just the retained extrema.
        charts[0].Refresh();session.Settings.GraphSmoothing=true;
        foreach(var chart in charts){chart.Refresh();Assert.True(HasRangeBand(Draw(chart)));}
        var smoothAgain=charts.Select(Prepared).ToArray();WaitPrepared();
        for(int i=0;i<charts.Length;i++)Assert.Same(smoothAgain[i],Prepared(charts[i]));
        charts[0].Refresh();session.Settings.HistorySeconds=60;charts[0].Refresh();Draw(charts[0]);
        var shortRange=Prepared(charts[0]);WaitPrepared();Assert.Same(shortRange,Prepared(charts[0]));
        Assert.False(HasRangeBand(Draw(charts[0])));session.Settings.GraphSmoothing=false;charts[0].Refresh();
        Assert.False(HasRangeBand(Draw(charts[0])));session.Settings.GraphSmoothing=true;
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
                typeof(HistoryChart).GetMethod("DrawSeries",flags)!.Invoke(charts[0],[context,rolling,now,100d,0d,0d,600d,120d,"Graph/cpu",false,false,true,null,1d]);
            var path=PathGeometry.CreateFromGeometry(Shapes(drawing).Single(shape=>shape.Geometry is StreamGeometry).Geometry);
            return path.Figures.SelectMany(figure=>new[]{figure.StartPoint}.Concat(figure.Segments.SelectMany(segment=>segment switch
                {PolyLineSegment poly=>poly.Points.AsEnumerable(),LineSegment line=>new[]{line.Point},BezierSegment bezier=>new[]{bezier.Point1,bezier.Point2,bezier.Point3},PolyBezierSegment curve=>curve.Points.AsEnumerable(),_=>Array.Empty<Point>()}))).ToArray();
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
        // Per-core CPU traces: thin core-tinted strokes, only on a standalone chart or one that opts in beside the GPU.
        {
            var coreCharts=new[]{new HistoryChart(session,"cpu",true),new HistoryChart(session,"cpu",true){CompareDevices=true,CoresWithDevices=true},new HistoryChart(session,"cpu",true){CompareDevices=true}};
            foreach(var chart in coreCharts){chart.Measure(new(300,120));chart.Arrange(new Rect(0,0,300,120));chart.UpdateLayout();}
            // Counting by brush identity relies on Theme.CoreBrush caching one frozen brush per (colour, index, count).
            Assert.Same(Theme.CoreBrush(0,8),Theme.CoreBrush(0,8));
            var coreBrushes=Enumerable.Range(0,8).Select(i=>(object)Theme.CoreBrush(i,8)).ToHashSet(ReferenceEqualityComparer.Instance);
            Pen[] CoreStrokes(Drawing drawing)=>Shapes(drawing).Where(s=>s.Pen is not null&&coreBrushes.Contains(s.Pen.Brush)).Select(s=>s.Pen).ToArray();
            Pen[] Strokes(Drawing drawing,string key)=>Shapes(drawing).Where(s=>s.Geometry is StreamGeometry&&s.Pen is not null&&ReferenceEquals(s.Pen.Brush,Application.Current.Resources[key])).Select(s=>s.Pen).ToArray();
            double PrimaryThickness(Drawing drawing)=>Strokes(drawing,"Graph/cpu").Min(pen=>pen.Thickness);
            int restoreSeconds=session.Settings.HistorySeconds;
            bool restoreSmoothing=session.Settings.GraphSmoothing;
            // 60 s raw; 21600 s smoothed (per-core ChartRanges); 21600 s unsmoothed (reduced raw polylines).
            foreach(var (seconds,smoothing) in new[]{(60,true),(21600,true),(21600,false)})
            {
                session.Settings.HistorySeconds=seconds;session.Settings.GraphSmoothing=smoothing;
                session.Settings.ShowCpuCoreUsage=false;
                var primaryThickness=new double[coreCharts.Length];
                for(int c=0;c<coreCharts.Length;c++){coreCharts[c].Refresh();var drawing=Draw(coreCharts[c]);Assert.Empty(CoreStrokes(drawing));primaryThickness[c]=PrimaryThickness(drawing);}
                session.Settings.ShowCpuCoreUsage=true;
                for(int c=0;c<coreCharts.Length;c++)
                {
                    coreCharts[c].Refresh();var drawing=Draw(coreCharts[c]);var cores=CoreStrokes(drawing);
                    if(c==2){Assert.Empty(cores);Assert.NotEmpty(Strokes(drawing,"Graph/cpu"));continue;}
                    Assert.Equal(8,cores.Length);
                    Assert.Equal(8,cores.Select(pen=>pen.Brush).Distinct(ReferenceEqualityComparer.Instance).Count());
                    // The cores replace the aggregate CPU trace; the GPU comparison trace stays.
                    Assert.Empty(Strokes(drawing,"Graph/cpu"));
                    if(c==1)Assert.NotEmpty(Strokes(drawing,"Graph/gpu"));
                    Assert.All(cores,pen=>{Assert.Equal(primaryThickness[c]/2,pen.Thickness,6);Assert.Empty(pen.DashStyle.Dashes);});
                }
                session.Settings.ShowCpuCoreUsage=false;
                foreach(var chart in coreCharts){chart.Refresh();Assert.Empty(CoreStrokes(Draw(chart)));}
            }
            // The hover chip lists cores by value under the device lines.
            session.Settings.HistorySeconds=60;session.Settings.GraphSmoothing=true;session.Settings.ShowCpuCoreUsage=true;
            var hovered=coreCharts[0];hovered.Refresh();Draw(hovered);
            var hp=hovered.PlotRect;hovered.SetHover(new Point(hp.Left+hp.Width*.5,hp.Top+10));Assert.True(hovered.HoverIndex>=0);
            var texts=Glyphs(Draw(hovered)).Select(g=>new string(g.GlyphRun.Characters.ToArray())).ToArray();
            Assert.Contains(texts,t=>t.StartsWith("Core ",StringComparison.Ordinal));
            // The hidden aggregate trace still anchors the hover and keeps its CPU total line in the chip.
            Assert.Contains(Glyphs(Draw(hovered)),g=>ReferenceEquals(g.ForegroundBrush,Application.Current.Resources["Graph/cpu"]));
            hovered.SetHover(null);
            session.Settings.ShowCpuCoreUsage=false;session.Settings.HistorySeconds=restoreSeconds;session.Settings.GraphSmoothing=restoreSmoothing;
        }
        Measure("four-six-hour-charts",_=>{foreach(var chart in charts){chart.Refresh();Draw(chart);}});
        // Same charts with 32 per-core traces on the smoothed six-hour range (report only, no threshold).
        {
            var coreHistory=new SampleHistory();
            for(int i=0;i<=samples;i++)
            {
                double time=(i-samples)*.5;
                var values=new Dictionary<string,double?>{["cpu/usage"]=30+i%31,["gpu/usage"]=i%101,["cpu/temperature"]=60+i%20,["gpu/temperature"]=55+i%30,["gpu/fan"]=1000+i%1500,["memory/usage"]=48+i%3,["network/download"]=150_000,["network/upload"]=80_000};
                for(int core=0;core<32;core++)values[CpuCoreUsage.Key(core)]=core==3&&i%10==0?null:(core*11+i)%100;
                coreHistory.Add(new(epoch.AddSeconds(time),time,values){PollingIntervalSeconds=.5});
            }
            var restoreHistory=collector.History;var restoreLatest=collector.GetType().GetField("latest",flags)!.GetValue(collector)!;
            bool restoreCores=session.Settings.ShowCpuCoreUsage,restoreSmooth=session.Settings.GraphSmoothing;int restoreRange=session.Settings.HistorySeconds;
            Set(collector,"<History>k__BackingField",coreHistory);
            Set(collector,"latest",new Reading(coreHistory.Read()[^1],[new("gpu","Fixture GPU")],[],[],"Fixture CPU","","","",false){CpuCoreCount=32});
            session.Settings.ShowCpuCoreUsage=true;session.Settings.GraphSmoothing=true;session.Settings.HistorySeconds=21600;
            // Same four charts, with the CPU chart opted in beside the GPU so its cores draw.
            var coreSet=new[]{"cpu","cputemp","memory","network"}.Select(k=>new HistoryChart(session,k,true){CompareDevices=k is "cpu" or "cputemp",CoresWithDevices=k=="cpu"}).ToArray();
            foreach(var chart in coreSet){chart.Measure(new(300,120));chart.Arrange(new Rect(0,0,300,120));chart.UpdateLayout();}
            Measure("four-six-hour-charts-32-cores",_=>{foreach(var chart in coreSet){chart.Refresh();Draw(chart);}});
            Assert.All(coreSet,c=>Assert.False((bool)typeof(HistoryChart).GetField("preparationFailed",flags)!.GetValue(c)!));
            Set(collector,"<History>k__BackingField",restoreHistory);Set(collector,"latest",restoreLatest);
            session.Settings.ShowCpuCoreUsage=restoreCores;session.Settings.GraphSmoothing=restoreSmooth;session.Settings.HistorySeconds=restoreRange;
            foreach(var chart in charts){chart.Refresh();Draw(chart);}WaitPrepared();
        }
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
