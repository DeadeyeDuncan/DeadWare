using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

// Invoked inside BatteryUiTests' single STA Application fixture.
internal static class ConnectionMapUiChecks
{
    public static void Run(Application app)
    {
        CheckIndependentAssetRecovery();
        Assert.Equal(333,ConnectionAssets.Borders.Count);
        Assert.Equal(2173,ConnectionAssets.StateBorders.Count);
        Assert.Equal(32584,ConnectionAssets.StateBorders.Sum(p=>p.Count));
        Assert.Contains(ConnectionAssets.Borders,p=>p[0]!=p[^1]);
        Assert.Equal(2,Assert.Single(ConnectionAssets.ParseBorders("[[[0,0],[1,1]]]" )).Count);
        foreach(string invalid in new[]{"{}","[[[0,0]]]","[[[181,0],[1,1]]]","[[[0,91],[1,1]]]","[[[0,0,0],[1,1]]]"})
            Assert.Throws<InvalidDataException>(()=>ConnectionAssets.ParseBorders(invalid));
        Assert.True(ConnectionAssets.Land.Count > 100);
        Assert.Contains(ConnectionAssets.Cities, c => c.Name == "London");
        Assert.Throws<InvalidDataException>(() => ConnectionAssets.ParseLand("[[[200,0],[1,0],[2,0],[200,0]]]"));
        Assert.Throws<InvalidDataException>(() => ConnectionAssets.ParseLand("{}"));
        Assert.Throws<InvalidDataException>(() => ConnectionAssets.ParseCities("[{\"id\":\"x\",\"name\":\"X\",\"country\":\"Y\",\"latitude\":91,\"longitude\":0}]"));
        var map = new ConnectionMap { AnimationEnabled = false };
        map.SetData([], null, false, "Capture unavailable");
        Assert.Contains("Capture unavailable", map.StatusSummary);
        Assert.Contains("origin", map.StatusSummary, StringComparison.OrdinalIgnoreCase);
        var points = new[] { new ConnectionMapPoint("london", new(51.5, -.12), 200, 100, true),
            new ConnectionMapPoint("tokyo", new(35.7, 139.7), 0, 150, false), new ConnectionMapPoint("sydney", new(-33.9, 151.2), 80, 0, false) };
        var theme = typeof(Collector).Assembly.GetType("DeadWare.App.Theme")!;
        foreach (string name in new[] { "Midnight", "Ember", "Glacier", "Amethyst", "Neon", "Blackout", "Skyrim", "Mage", "Daylight" })
        {
            theme.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [name]);
            foreach (bool flat in new[] { false, true })
            foreach (var size in new[] { new Size(284, 145), new Size(560, 280), new Size(1000, 400) })
            {
                map.Flat = flat; map.ResetView(); map.SetData(points, new(41.9, -87.6), true, "Synthetic fixture");
                Render(map, size, $"connections-{name}-{flat}-{size.Width}");
                Assert.Equal(3, map.DisplayedPointCount);
                var drawings = VisualTreeHelper.GetDrawing(map)!;
                Assert.True(CountGeometry(drawings) > 10, "Coastlines, graticules, paths and markers must draw.");
                Assert.True(CountGlyphRuns(drawings) > 0, "The full-page map keeps its legend and status text.");
            }
        }
        foreach (string status in new[] { "No public locations", "Only local / special addresses", "Location database unavailable" })
        {
            map.SetData([], null, true, status); Render(map, new(284, 145), "connections-empty-" + status.Split(' ')[0]);
            Assert.Contains(status, map.StatusSummary); Assert.Equal(0, map.DisplayedPointCount);
        }
        map.SetData(Enumerable.Range(0, 300).Select(i => new ConnectionMapPoint("p" + i, new(i % 80, i), 1, 1, false)).ToArray(), null, true, "Bounded fixture");
        Assert.Equal(256, map.DisplayedPointCount); Assert.Equal(44, map.OverflowCount); Assert.Contains("44", map.StatusSummary);
        map.ZoomBy(double.NaN); map.ZoomBy(1e10); Assert.Equal(8, map.Camera.Zoom);
        map.ResetView(); Assert.Equal(1, map.Camera.Zoom);
        Assert.False(map.IsAnimating); map.AnimationEnabled = true;
        // An offscreen visual is not effectively visible until attached to a presentation source.
        using var source = new OffscreenSource(map);
        map.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Assert.True(map.IsAnimating);
        map.Visibility = Visibility.Hidden; Assert.False(map.IsAnimating);
        map.Visibility = Visibility.Visible; Assert.True(map.IsAnimating);
        map.AnimationEnabled = false; Assert.False(map.IsAnimating);
        map.AnimationEnabled = true; map.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Assert.False(map.IsAnimating);
        theme.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, ["Midnight"]);
        CheckMapOnlyPresentationSwitch();
        CheckDirectionalFlowAndVolume();
        CheckFineConnectionLayers();
        CheckStateBordersAndWidgetMarkers();
        CheckActivityFade();
        CheckRefinedGlassLayers();
        CheckHighlight();
    }

    private static void CheckMapOnlyPresentationSwitch()
    {
        var map=new ConnectionMap {Flat=true,AnimationEnabled=false};
        map.SetData([new("peer",new(0,60),1,0,false)],new(0,-60),true,"Presentation fixture");map.ResetView();
        static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
        double Width(double thickness)=>Assert.Single(Shapes(VisualTreeHelper.GetDrawing(map)!),d=>d.Geometry is StreamGeometry&&Math.Abs((d.Pen?.Thickness??0)-thickness)<.0001).Geometry.Bounds.Width;
        foreach(bool mapOnly in new[]{false,true,false,true})
        {
            map.MapOnly=mapOnly;Render(map,new(600,150),"connections-presentation-"+mapOnly);
            Assert.Equal(mapOnly,CountGlyphRuns(VisualTreeHelper.GetDrawing(map)!)==0);
            Assert.InRange(Width(1.2),mapOnly?190:60,mapOnly?205:70);
            Assert.InRange(Width(1),mapOnly?580:180,mapOnly?600:200);
        }
    }

    private static void CheckDirectionalFlowAndVolume()
    {
        var map=new ConnectionMap {Flat=true};map.SetData([new("high",new(0,60),60000,30000,false),new("low",new(30,40),1,0,false)],new(0,-60),true,"Flow fixture");
        using var source=new OffscreenSource(map);map.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var phase=typeof(ConnectionMap).GetField("phase",BindingFlags.Instance|BindingFlags.NonPublic)!;
        static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
        GeometryDrawing[] Ellipses(){Render(map,new(600,300),"connections-directional-fixture");return Shapes(VisualTreeHelper.GetDrawing(map)!).Where(s=>s.Geometry is EllipseGeometry).ToArray();}
        // Particles are the 2.2 px dots in a direction color; nodes are the solid cores with an ink ring.
        var downloadColor=((SolidColorBrush)Theme.Brush(Theme.GraphKey("download"))).Color;var uploadColor=((SolidColorBrush)Theme.Brush(Theme.GraphKey("upload"))).Color;
        static bool Dot(GeometryDrawing s)=>s.Brush is SolidColorBrush&&s.Pen is null&&Math.Abs(((EllipseGeometry)s.Geometry).RadiusX-1.1)<.01;
        static bool Core(GeometryDrawing s)=>s.Brush is SolidColorBrush&&s.Pen is not null&&((EllipseGeometry)s.Geometry).RadiusX>=2.6;
        double X(GeometryDrawing s)=>((EllipseGeometry)s.Geometry).Center.X;double Y(GeometryDrawing s)=>((EllipseGeometry)s.Geometry).Center.Y;
        phase.SetValue(map,.5d);map.InvalidateVisual();var first=Ellipses();
        var dots=first.Where(Dot).ToArray();
        // Three dots per active direction at full level: two directions on "high", one on "low".
        Assert.Equal(3*ArcParticles.Count(1),dots.Length);
        double equator=map.PointPosition("high")!.Value.Y;
        double[] Lane(GeometryDrawing[] shapes,Color color)=>shapes.Where(s=>Dot(s)&&Math.Abs(Y(s)-equator)<.1&&((SolidColorBrush)s.Brush).Color==color).Select(X).OrderBy(x=>x).ToArray();
        var down=Lane(first,downloadColor);var up=Lane(first,uploadColor);
        Assert.Equal(3,down.Length);Assert.Equal(3,up.Length);
        // Download dots sit at 1-f where upload dots sit at f, so the two lanes mirror each other on the straight equatorial route.
        double span=down[0]+up[2];
        Assert.Equal(span,down[1]+up[1],0);Assert.Equal(span,down[2]+up[0],0);
        // Advancing time moves download toward the origin (left) and upload away from it.
        double middle=span/2;
        double Nearest(double[] lane)=>lane.OrderBy(x=>Math.Abs(x-middle)).First();
        double downBefore=Nearest(down),upBefore=Nearest(up);
        phase.SetValue(map,.6d);map.InvalidateVisual();var second=Ellipses();
        var downAfter=Lane(second,downloadColor).OrderBy(x=>Math.Abs(x-downBefore)).First();var upAfter=Lane(second,uploadColor).OrderBy(x=>Math.Abs(x-upBefore)).First();
        Assert.True(downAfter<downBefore,$"download {downBefore}->{downAfter}");Assert.True(upAfter>upBefore,$"upload {upBefore}->{upAfter}");
        var markerRadii=first.Where(Core).Select(s=>((EllipseGeometry)s.Geometry).RadiusX).ToArray();Assert.Equal(2,markerRadii.Length);Assert.True(markerRadii.Max()>markerRadii.Min()+1);
        map.AnimationEnabled=false;Assert.False(map.IsAnimating);var still=Ellipses();Assert.Empty(still.Where(Dot));Assert.Equal(2,still.Count(Core));
        map.AnimationEnabled=true;map.SetData([],new(0,-60),true,"Empty");Assert.False(map.IsAnimating);
        map.SetData([new("stale",new(0,60),60000,30000,false)],new(0,-60),false,"Stale");Assert.False(map.IsAnimating);var stale=Ellipses();Assert.Empty(stale.Where(Dot));Assert.Single(stale.Where(Core));
        map.SetData([new("quiet",new(0,60),60000,30000,false,false,false)],new(0,-60),true,"Quiet peer with old byte totals");Assert.False(map.IsAnimating);Assert.Empty(Ellipses().Where(Dot));
        map.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }

    private static void CheckFineConnectionLayers()
    {
        var map=new ConnectionMap {Flat=true};map.SetData([new("selected",new(20,40),60000,30000,true)],new(0,-60),true,"Filament fixture");
        using var source=new OffscreenSource(map);map.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
        var phase=typeof(ConnectionMap).GetField("phase",BindingFlags.Instance|BindingFlags.NonPublic)!;
        GeometryDrawing[] Filaments()
        {
            Render(map,new(1000,400),"connections-filaments-selected");
            return Shapes(VisualTreeHelper.GetDrawing(map)!).Where(s=>s.Geometry is StreamGeometry&&s.Pen?.Brush==Theme.Brush("Ink")&&s.Pen.Thickness<1).ToArray();
        }
        // The selected route carries one half-pixel ink line on its arc; the arc no longer waves, so it is cached across frames.
        phase.SetValue(map,1.01d);map.InvalidateVisual();var first=Assert.Single(Filaments());Assert.Equal(.5,first.Pen.Thickness);
        phase.SetValue(map,2d);map.InvalidateVisual();Assert.Same(first.Geometry,Assert.Single(Filaments()).Geometry);
        map.AnimationEnabled=false;phase.SetValue(map,4d);map.InvalidateVisual();Assert.Same(first.Geometry,Assert.Single(Filaments()).Geometry);
        map.SetView(new(10,-20),1.5);Assert.NotSame(first.Geometry,Assert.Single(Filaments()).Geometry);map.ResetView();
        foreach(bool mapOnly in new[]{false,true})
        {
            map.MapOnly=mapOnly;Render(map,new(600,300),"connections-borders-"+mapOnly);
            var border=Assert.Single(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>s.Geometry is StreamGeometry&&s.Pen?.Brush==Theme.Brush("Muted")&&s.Pen.Thickness==.65);
            Assert.False(border.Geometry.IsEmpty());
            var cached=border.Geometry;map.InvalidateVisual();Render(map,new(600,300),"connections-borders-"+mapOnly);
            Assert.Contains(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>ReferenceEquals(s.Geometry,cached));
            map.ZoomBy(1.25);Render(map,new(600,300),"connections-borders-zoom-"+mapOnly);
            Assert.DoesNotContain(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>ReferenceEquals(s.Geometry,cached));
        }
        map.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        CheckDenseMapRendering();
    }

    private static void CheckStateBordersAndWidgetMarkers()
    {
        static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
        var map=new ConnectionMap {AnimationEnabled=false};
        GeometryDrawing[] Draw(Size size,string name)
        {
            Render(map,size,name);return Shapes(VisualTreeHelper.GetDrawing(map)!).ToArray();
        }
        bool State(GeometryDrawing s)=>s.Geometry is StreamGeometry&&s.Pen?.Brush==Theme.Brush("Muted")&&s.Pen.Thickness==.5;
        foreach(bool flat in new[]{false,true})
        foreach(bool widget in new[]{false,true})
        {
            map.Flat=flat;map.MapOnly=widget;map.SetData([],null,true,"State borders fixture");map.SetView(new(43,-103),1);
            var size=widget?new Size(600,300):new Size(1000,500);
            Assert.DoesNotContain(Draw(size,"states-world"),State);
            map.SetView(new(43,-103),2.5);
            var first=Assert.Single(Draw(size,$"states-NA-{flat}-{widget}"),State).Geometry;Assert.False(first.IsEmpty());
            map.InvalidateVisual();Assert.Same(first,Assert.Single(Draw(size,"states-cached"),State).Geometry);
            Assert.NotSame(first,Assert.Single(Draw(new(size.Width-20,size.Height),"states-resized"),State).Geometry);
            map.SetView(new(35,-100),3);Assert.NotSame(first,Assert.Single(Draw(size,"states-panned"),State).Geometry);
            map.ResetView();Assert.DoesNotContain(Draw(size,"states-reset"),State);
        }
        map.Flat=true;map.MapOnly=false;map.SetData([new("wave",new(0,60),100,100,true)],new(0,-60),true,"Oscilloscope fixture");map.ResetView();
        var main=Draw(new(1000,400),"oscilloscope-main");
        // Each active direction draws a 6 px and 2.5 px glow pass plus a 1.2 px core in its own color.
        var arcs=main.Where(s=>s.Geometry is StreamGeometry&&s.Pen?.Brush==Theme.Brush(Theme.GraphKey("download"))).Select(s=>s.Pen.Thickness).OrderBy(t=>t).ToArray();
        Assert.Equal([1.2,2.5,6],arcs);
        Assert.DoesNotContain(main,s=>s.Pen?.DashStyle.Dashes.Count>0);
        double Marker(GeometryDrawing[] shapes)=>shapes.Where(s=>s.Geometry is EllipseGeometry&&s.Brush==Theme.Brush(Theme.GraphKey("download"))).Select(s=>((EllipseGeometry)s.Geometry).RadiusX).Single();
        double full=Marker(main);map.MapOnly=true;
        var compact=Draw(new(600,300),"oscilloscope-widget");Assert.Equal(full/2,Marker(compact));
        map.SelectPoint(null);var unselected=Draw(new(300,171),"oscilloscope-widget-small");Assert.InRange(Marker(unselected),1.5,3);
    }

    private static void CheckActivityFade()
    {
        static IEnumerable<(GeometryDrawing Shape,double Alpha)> Shapes(Drawing drawing,double alpha=1)=>drawing is GeometryDrawing shape?[(shape,alpha)]:drawing is DrawingGroup group?group.Children.SelectMany(d=>Shapes(d,alpha*group.Opacity)):[];
        foreach(bool widget in new[]{false,true})
        {
            var map=new ConnectionMap {Flat=true,MapOnly=widget};
            var peer=new ConnectionMapPoint("fade",new(0,60),100,100,false);
            map.SetData([peer],new(0,-60),true,"Active");
            using var source=new OffscreenSource(map);map.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var phase=typeof(ConnectionMap).GetField("phase",BindingFlags.Instance|BindingFlags.NonPublic)!;
            (string Path,double Alpha) Shape(double time,string direction="download")
            {
                phase.SetValue(map,time);map.InvalidateVisual();Render(map,new(600,300),$"activity-{widget}-{time}");
                var wave=Assert.Single(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>s.Shape.Geometry is StreamGeometry&&s.Shape.Pen?.Brush==Theme.Brush(Theme.GraphKey(direction))&&s.Shape.Pen.Thickness==1.2);
                return (wave.Shape.Geometry.ToString(),wave.Alpha);
            }
            // Turning quiet must retain the animation subscription long enough to ease out.
            var active=Shape(1);map.SetData([peer with {ReceiveActive=false,SendActive=false}],new(0,-60),true,"Quiet");
            Assert.True(map.IsAnimating);
            Assert.Equal(active,Shape(1));
            var fading=Shape(1.4);Assert.True(map.IsAnimating);Assert.InRange(fading.Alpha,.38,.56);
            map.SetData([peer],new(0,-60),true,"Brief burst");Assert.Equal(fading,Shape(1.4));
            Assert.True(Shape(1.5).Alpha>fading.Alpha);
            map.SetData([peer with {ReceiveActive=false,SendActive=false}],new(0,-60),true,"Quiet refresh");
            var stopped=Shape(5);Assert.False(map.IsAnimating);Assert.Equal(.30,stopped.Alpha,10);Assert.Equal(stopped,Shape(6));
            map.SetData([peer],new(0,-60),true,"Active again");Shape(7);
            map.SetData([peer with {ReceiveActive=false}],new(0,-60),true,"Only upload active");
            string Dots(double time)
            {
                phase.SetValue(map,time);map.InvalidateVisual();Render(map,new(600,300),$"activity-dots-{widget}-{time}");
                return string.Join(";",Shapes(VisualTreeHelper.GetDrawing(map)!).Where(s=>s.Shape.Geometry is EllipseGeometry e&&Math.Abs(e.RadiusX-1.1)<.01).Select(s=>((EllipseGeometry)s.Shape.Geometry).Center));
            }
            var download=Shape(11);var upload=Shape(11,"upload");Assert.True(map.IsAnimating);
            // Arcs are static; the moving dots carry the motion.
            Assert.Equal(download,Shape(12));Assert.Equal(upload.Path,Shape(12,"upload").Path);
            Assert.NotEqual(Dots(11),Dots(12));Assert.NotEmpty(Dots(12));
            map.AnimationEnabled=false;var paused=Shape(12,"upload");Assert.False(map.IsAnimating);Assert.Equal(paused,Shape(20,"upload"));Assert.Empty(Dots(20));
            map.SetData([],null,true,"Empty");Assert.False(map.IsAnimating);
            Assert.Empty((System.Collections.IDictionary)typeof(ConnectionMap).GetField("activity",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(map)!);
            map.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }

    private static void CheckDenseMapRendering()
    {
        var map=new ConnectionMap {Flat=true,MapOnly=true};
        map.SetData(Enumerable.Range(0,256).Select(i=>new ConnectionMapPoint("dense"+i,new(i%100-50,i-128),100,100,i==0)).ToArray(),new(41,-87),true,"Dense map fixture");
        using var source=new OffscreenSource(map);map.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var phase=typeof(ConnectionMap).GetField("phase",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var render=typeof(ConnectionMap).GetMethod("OnRender",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var times=new List<double>();
        foreach(var size in new[]{new Size(284,145),new Size(1000,400)})
        {
            map.MapOnly=size.Width<350;times.Clear();Render(map,size,"connections-dense-"+size.Width);
            for(int i=0;i<24;i++)
            {
                phase.SetValue(map,i/30d);var drawing=new DrawingGroup();
                var watch=System.Diagnostics.Stopwatch.StartNew();using(var context=drawing.Open())render.Invoke(map,[context]);
                watch.Stop();if(i>2)times.Add(watch.Elapsed.TotalMilliseconds);
            }
            Assert.Equal(256,map.DisplayedPointCount);
            string? output=Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
            if(output is not null)File.WriteAllText(Path.Combine(output,$"dense-{size.Width}-render-ms.txt"),$"256 routes; OnRender drawing-command CPU only (not native frame/GPU timing): median {times.Order().ElementAt(times.Count/2):F1} ms; max {times.Max():F1} ms.");
        }
        map.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }

    private static void CheckIndependentAssetRecovery()
    {
        const string validLand = "[[[0,0],[1,0],[0,1],[0,0]]]";
        const string validBorders = "[[[0,0],[1,1]]]";
        const string validCities = "[{\"id\":\"fixture\",\"name\":\"Fixture city\",\"country\":\"Fixture country\",\"latitude\":1,\"longitude\":2}]";
        var load = typeof(ConnectionAssets).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failures = new List<Exception?>();
        foreach (string rejected in new[] { "land", "borders", "states", "cities" })
        foreach (string failure in new[] { "malformed", "oversized", "missing" })
        {
            failures.Add(Record.Exception(() =>
            {
                string Read(string name) => name != rejected ? name == "land" ? validLand : name is "borders" or "states" ? validBorders : validCities : failure switch
                {
                    "malformed" => "{}",
                    "oversized" => new string(' ', 2_000_001),
                    _ => throw new InvalidDataException("Missing embedded geography.")
                };
                // Invoke the same boundary used by Lazy<Assets>; parsers and independent catches are real.
                var assets = load.Invoke(null, [(Func<string, string>)Read])!;
                T Value<T>(string name) => (T)assets.GetType().GetProperty(name)!.GetValue(assets)!;
                var land = Value<IReadOnlyList<IReadOnlyList<GeoPoint>>>("Land");
                var cities = Value<IReadOnlyList<HomeCity>>("Cities");
                Assert.Equal(rejected == "land" ? "Map geography unavailable" : rejected == "borders" ? "Country borders unavailable" : rejected == "states" ? "State borders unavailable" : "Home city choices unavailable", Value<string>("Status"));
                if(rejected=="states")Assert.Empty(Value<IReadOnlyList<IReadOnlyList<GeoPoint>>>("StateBorders"));
                else Assert.Equal(new[]{new GeoPoint(0,0),new(1,1)},Assert.Single(Value<IReadOnlyList<IReadOnlyList<GeoPoint>>>("StateBorders")));
                if(rejected=="borders")Assert.Empty(Value<IReadOnlyList<IReadOnlyList<GeoPoint>>>("Borders"));
                else Assert.Equal(new[]{new GeoPoint(0,0),new(1,1)},Assert.Single(Value<IReadOnlyList<IReadOnlyList<GeoPoint>>>("Borders")));
                if (rejected == "land")
                {
                    Assert.Empty(land); Assert.Equal(new HomeCity("fixture", "Fixture city", "Fixture country", new(1, 2)), Assert.Single(cities));
                }
                else if(rejected=="cities")
                {
                    Assert.Empty(cities); Assert.Equal(new[] { new GeoPoint(0, 0), new(0, 1), new(1, 0), new(0, 0) }, Assert.Single(land));
                }
                else {Assert.Single(land);Assert.Single(cities);}
            }));
        }
        Assert.All(failures, failure => Assert.Null(failure));
    }

    private static void CheckRefinedGlassLayers()
    {
        static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
        var map=new ConnectionMap {AnimationEnabled=false};
        var points=new ConnectionMapPoint[]{new("both",new(51.5,-.12),2000,1000,false,true,true,"browser.exe","London · ↓ 1.0 MB/s · ↑ 0.5 MB/s"),new("down",new(35.7,139.7),500,0,false),new("up",new(-33.9,151.2),0,300,false)};
        map.SetData(points,new(41.9,-87.6),true,"Layers fixture");map.ResetView();
        Render(map,new(600,400),"connections-layers");
        var shapes=Shapes(VisualTreeHelper.GetDrawing(map)!).ToArray();
        Assert.Contains(shapes,s=>ReferenceEquals(s.Brush,Application.Current.Resources["Surface2"])&&s.Geometry is StreamGeometry&&!s.Geometry.IsEmpty());
        Assert.Contains(shapes,s=>s.Brush is RadialGradientBrush&&s.Geometry is EllipseGeometry);
        Assert.Contains(shapes,s=>s.Geometry is StreamGeometry&&s.Pen?.Thickness==6);
        Assert.Contains(shapes,s=>s.Geometry is StreamGeometry&&s.Pen?.Thickness==1.2);
        Assert.DoesNotContain(shapes,s=>s.Pen is not null&&s.Pen.DashStyle.Dashes.Count>0);
        Assert.True(CountGlyphRuns(VisualTreeHelper.GetDrawing(map)!)>0);
        // Hover: a node within 9 px shows a chip and a hand cursor; 20 px away shows nothing.
        var position=map.PointPosition("both")!.Value;
        string? raised=null;map.PointHovered+=key=>raised=key;
        map.SetHover(position);Assert.Equal("both",map.HoveredKey);Assert.Equal("both",raised);Assert.Equal(Cursors.Hand,map.Cursor);
        Render(map,new(600,400),"connections-layers-hover");
        var hovered=Shapes(VisualTreeHelper.GetDrawing(map)!).ToArray();
        Assert.Contains(hovered,s=>ReferenceEquals(s.Brush,Application.Current.Resources["GlassPopup"]));
        Assert.Contains(hovered,s=>s.Geometry is StreamGeometry&&s.Pen?.Thickness==.5);
        map.SetHover(new Point(position.X+20,position.Y));Assert.Null(map.HoveredKey);Assert.Null(raised);Assert.Equal(Cursors.Arrow,map.Cursor);
        Render(map,new(600,400),"connections-layers-unhover");
        Assert.DoesNotContain(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>ReferenceEquals(s.Brush,Application.Current.Resources["GlassPopup"]));
        map.SetHover(null);
        // Widget: larger hit target, no chip and no text at all.
        map.MapOnly=true;Render(map,new(600,400),"connections-layers-widget");
        var widgetPosition=map.PointPosition("both")!.Value;
        map.SetHover(new Point(widgetPosition.X+11,widgetPosition.Y));Assert.Equal("both",map.HoveredKey);
        Render(map,new(600,400),"connections-layers-widget-hover");
        Assert.DoesNotContain(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>ReferenceEquals(s.Brush,Application.Current.Resources["GlassPopup"]));
        Assert.Equal(0,CountGlyphRuns(VisualTreeHelper.GetDrawing(map)!));
        map.SetHover(null);map.MapOnly=false;
        // A poll that moves or drops the hovered node clears the hover instead of following the key.
        Render(map,new(600,400),"connections-layers-rehover");position=map.PointPosition("both")!.Value;
        map.SetHover(position);Assert.Equal("both",map.HoveredKey);
        map.SetData([points[0] with {Point=new(-33.9,151.2)},points[1]],new(41.9,-87.6),true,"Moved");Render(map,new(600,400),"connections-layers-moved");
        Assert.Null(map.HoveredKey);Assert.Null(raised);Assert.Equal(Cursors.Arrow,map.Cursor);
        map.SetHover(null);map.SetData(points,new(41.9,-87.6),true,"Layers fixture");map.ResetView();
        // Halos follow a custom traffic color without a theme switch.
        if(Theme.GlassEnabled)
        {
            var custom=new SolidColorBrush(Color.FromRgb(255,0,255));custom.Freeze();Application.Current.Resources[Theme.GraphKey("download")]=custom;
            map.InvalidateVisual();Render(map,new(600,400),"connections-layers-custom-color");
            Assert.Contains(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>s.Geometry is EllipseGeometry&&s.Brush is RadialGradientBrush halo&&halo.GradientStops[0].Color.R==255&&halo.GradientStops[0].Color.G==0&&halo.GradientStops[0].Color.B==255);
            Theme.Apply("Midnight");
        }
        // Disabled transparency keeps the map flat: no gradient ocean, no limb shading, no glow.
        var glassProperty=typeof(Theme).GetProperty("GlassEnabled")!;bool glassWas=Theme.GlassEnabled;
        glassProperty.SetValue(null,false);
        try
        {
            map.InvalidateVisual();Render(map,new(600,400),"connections-layers-flat");
            var flat=Shapes(VisualTreeHelper.GetDrawing(map)!).ToArray();
            Assert.DoesNotContain(flat,s=>s.Brush is RadialGradientBrush||s.Brush is LinearGradientBrush);
            Assert.Contains(flat,s=>ReferenceEquals(s.Brush,Theme.Brush("Surface2"))&&s.Geometry is StreamGeometry);
        }
        finally { glassProperty.SetValue(null,glassWas); }
        map.InvalidateVisual();Render(map,new(600,400),"connections-layers-restored");
        // Particles need the animation clock.
        using(var source=new OffscreenSource(map))
        {
            map.AnimationEnabled=true;map.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));Assert.True(map.IsAnimating);
            typeof(ConnectionMap).GetField("phase",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(map,.5d);map.InvalidateVisual();
            Render(map,new(600,400),"connections-layers-particles");
            var dots=Shapes(VisualTreeHelper.GetDrawing(map)!).Where(s=>s.Geometry is EllipseGeometry e&&Math.Abs(e.RadiusX-1.1)<.01).ToArray();
            Assert.True(dots.Length>=ArcParticles.Count(1),$"{dots.Length} particles");
            map.AnimationEnabled=false;map.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
        // Missing land still paints the ocean, legend and status; nothing to draw still paints the ocean.
        map.LandOverride=[];Render(map,new(600,400),"connections-no-land");
        Assert.True(CountGlyphRuns(VisualTreeHelper.GetDrawing(map)!)>0);
        Assert.Contains(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>s.Brush is RadialGradientBrush&&s.Geometry is EllipseGeometry);
        map.LandOverride=null;
        map.SetData([],null,true,"");Render(map,new(600,400),"connections-nothing");
        Assert.Contains(Shapes(VisualTreeHelper.GetDrawing(map)!),s=>s.Brush is RadialGradientBrush&&s.Geometry is EllipseGeometry);
        Assert.Equal(0,map.DisplayedPointCount);
    }

    private static void CheckHighlight()
    {
        static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
        // Equal traffic gives both nodes the same base radius; "near" and "far" round to different location keys.
        var points=new ConnectionMapPoint[]{new("near",new(20,40),100,0,false),new("far",new(-20,-20),100,0,false)};
        foreach(string name in new[]{"Daylight","Midnight"})
        {
            Theme.Apply(name);
            var map=new ConnectionMap {Flat=true,AnimationEnabled=false};
            map.SetData(points,new(0,-60),true,"Ring fixture");map.ResetView();
            GeometryDrawing[] Draw(string step){Render(map,new(600,300),$"connections-highlight-{name}-{step}");return Shapes(VisualTreeHelper.GetDrawing(map)!).ToArray();}
            bool At(GeometryDrawing s,string key)=>s.Geometry is EllipseGeometry e&&(e.Center-map.PointPosition(key)!.Value).Length<.1;
            // The node core: a solid fill with an ink outline (halos have no pen; rings have no fill).
            GeometryDrawing Node(GeometryDrawing[] shapes,string key)=>Assert.Single(shapes,s=>At(s,key)&&ReferenceEquals(s.Brush,Theme.Brush(Theme.GraphKey("download")))&&ReferenceEquals(s.Pen?.Brush,Theme.Brush("Ink")));
            GeometryDrawing[] Rings(GeometryDrawing[] shapes,string key)=>shapes.Where(s=>At(s,key)&&s.Brush is null&&ReferenceEquals(s.Pen?.Brush,Theme.Brush("Accent"))).ToArray();
            int Filaments(GeometryDrawing[] shapes)=>shapes.Count(s=>s.Geometry is StreamGeometry&&ReferenceEquals(s.Pen?.Brush,Theme.Brush("Ink"))&&s.Pen.Thickness==.5);
            var plain=Draw("plain");
            double radius=((EllipseGeometry)Node(plain,"near").Geometry).RadiusX;
            Assert.Equal(.6,Node(plain,"near").Pen.Thickness,6);Assert.Empty(Rings(plain,"near"));Assert.Equal(0,Filaments(plain));
            Assert.Empty(map.HighlightedKeys);Assert.DoesNotContain("highlighted",AutomationProperties.GetName(map));

            map.Highlight([new GeoPoint(20.0001,40)]);
            Assert.Equal(new[]{"near"},map.HighlightedKeys);
            var lit=Draw("lit");
            var node=Node(lit,"near");var ring=Assert.Single(Rings(lit,"near"));
            Assert.Equal(radius+1.5,((EllipseGeometry)node.Geometry).RadiusX,6);Assert.Equal(1.2,node.Pen.Thickness,6);
            Assert.Equal(radius+1.5+4,((EllipseGeometry)ring.Geometry).RadiusX,6);Assert.Equal(1,ring.Pen.Thickness,6);
            Assert.Equal(1,Filaments(lit));
            Assert.Equal(radius,((EllipseGeometry)Node(lit,"far").Geometry).RadiusX,6);Assert.Empty(Rings(lit,"far"));
            Assert.EndsWith(" · 1 highlighted",AutomationProperties.GetName(map));
            // The same key set does not regroup.
            var displayed=map.DisplayedPoints;map.Highlight([new GeoPoint(20,40)]);Assert.Same(displayed,map.DisplayedPoints);

            map.Highlight(null);
            Assert.Empty(map.HighlightedKeys);Assert.DoesNotContain("highlighted",AutomationProperties.GetName(map));
            var cleared=Draw("cleared");Assert.Empty(Rings(cleared,"near"));Assert.Equal(radius,((EllipseGeometry)Node(cleared,"near").Geometry).RadiusX,6);

            map.Highlight([new GeoPoint(20,40)]);map.SetData(points,new(0,-60),true,"Ring fixture");
            Assert.Equal(new[]{"near"},map.HighlightedKeys);Assert.EndsWith(" · 1 highlighted",AutomationProperties.GetName(map));
            Assert.Single(Rings(Draw("kept"),"near"));

            // A regroup keeps the SelectPoint key.
            map.SelectPoint("far");
            map.Highlight([new GeoPoint(-20,-20),new GeoPoint(20,40)]);
            Assert.True(map.DisplayedPoints.Single(p=>p.Key=="far").Selected);Assert.False(map.DisplayedPoints.Single(p=>p.Key=="near").Selected);
            Assert.Equal(new[]{"far","near"},map.HighlightedKeys.Order());Assert.EndsWith(" · 2 highlighted",AutomationProperties.GetName(map));
            map.Highlight(null);
            Assert.True(map.DisplayedPoints.Single(p=>p.Key=="far").Selected);Assert.Empty(map.HighlightedKeys);
        }
    }

    private sealed class OffscreenSource : PresentationSource, IDisposable
    {
        private Visual? root;
        public OffscreenSource(Visual visual) { AddSource(); RootVisual = visual; }
        public override Visual RootVisual { get => root!; set { var old = root; root = value; RootChanged(old, root); } }
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override bool IsDisposed { get; } = false;
        public void Dispose() { RootVisual = null!; RemoveSource(); }
    }

    private static int CountGeometry(Drawing drawing) => drawing is GeometryDrawing ? 1 : drawing is DrawingGroup group ? group.Children.Sum(CountGeometry) : 0;
    private static int CountGlyphRuns(Drawing drawing) => drawing is GlyphRunDrawing ? 1 : drawing is DrawingGroup group ? group.Children.Sum(CountGlyphRuns) : 0;
    private static void Render(ConnectionMap map, Size size, string name)
    {
        map.Measure(size); map.Arrange(new Rect(size)); map.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(map);
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        if (output is null) return;
        Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
