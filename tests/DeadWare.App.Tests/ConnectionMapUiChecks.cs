using System.IO;
using System.Reflection;
using System.Windows;
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
        foreach (string name in new[] { "Midnight", "Ember", "Glacier", "Amethyst", "Neon", "Daylight" })
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
        GeometryDrawing[] Ellipses()
        {
            static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
            Render(map,new(600,300),"connections-directional-fixture");return Shapes(VisualTreeHelper.GetDrawing(map)!).Where(s=>s.Geometry is EllipseGeometry).ToArray();
        }
        phase.SetValue(map,.5d);map.InvalidateVisual();var first=Ellipses();
        var heads=first.Where(s=>((EllipseGeometry)s.Geometry).RadiusX<2.6).ToArray();Assert.Equal(3,heads.Length);
        var highHeads=heads.Where(s=>Math.Abs(((EllipseGeometry)s.Geometry).Center.Y-((EllipseGeometry)heads[0].Geometry).Center.Y)<.1).ToArray();Assert.Equal(2,highHeads.Length);
        phase.SetValue(map,1d);map.InvalidateVisual();var second=Ellipses().Where(s=>((EllipseGeometry)s.Geometry).RadiusX<2.6).ToArray();
        // The two colors on the equatorial route travel in opposite directions.
        double left=((EllipseGeometry)highHeads[0].Geometry).Center.X;
        double right=((EllipseGeometry)highHeads[1].Geometry).Center.X;
        var moved=second.Where(s=>Math.Abs(((EllipseGeometry)s.Geometry).Center.Y-((EllipseGeometry)highHeads[0].Geometry).Center.Y)<.1).ToArray();
        Assert.True((((EllipseGeometry)moved[0].Geometry).Center.X-left)*(((EllipseGeometry)moved[1].Geometry).Center.X-right)<0);
        var markerRadii=first.Select(s=>(EllipseGeometry)s.Geometry).Where(e=>e.RadiusX>=2.6&&e.RadiusX!=4).Select(e=>e.RadiusX).ToArray();Assert.Equal(2,markerRadii.Length);Assert.True(markerRadii.Max()>markerRadii.Min()+1);
        map.AnimationEnabled=false;Assert.False(map.IsAnimating);Assert.Equal(3,Ellipses().Length);
        map.AnimationEnabled=true;map.SetData([],new(0,-60),true,"Empty");Assert.False(map.IsAnimating);
        map.SetData([new("stale",new(0,60),60000,30000,false)],new(0,-60),false,"Stale");Assert.False(map.IsAnimating);Assert.Equal(2,Ellipses().Length);
        map.SetData([new("quiet",new(0,60),60000,30000,false,false,false)],new(0,-60),true,"Quiet peer with old byte totals");Assert.False(map.IsAnimating);Assert.Equal(2,Ellipses().Length);
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
        phase.SetValue(map,1.01d);map.InvalidateVisual();var first=Filaments();Assert.Equal(3,first.Length);
        phase.SetValue(map,1.09d);map.InvalidateVisual();var sameTick=Filaments();Assert.All(Enumerable.Range(0,3),i=>Assert.Same(first[i].Geometry,sameTick[i].Geometry));
        string geometry=string.Join(";",first.Select(s=>s.Geometry.ToString()));
        phase.SetValue(map,2d);map.InvalidateVisual();Assert.NotEqual(geometry,string.Join(";",Filaments().Select(s=>s.Geometry.ToString())));
        map.AnimationEnabled=false;var stopped=string.Join(";",Filaments().Select(s=>s.Geometry.ToString()));
        phase.SetValue(map,4d);map.InvalidateVisual();Assert.Equal(stopped,string.Join(";",Filaments().Select(s=>s.Geometry.ToString())));
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
        foreach (string rejected in new[] { "land", "borders", "cities" })
        foreach (string failure in new[] { "malformed", "oversized", "missing" })
        {
            failures.Add(Record.Exception(() =>
            {
                string Read(string name) => name != rejected ? name == "land" ? validLand : name == "borders" ? validBorders : validCities : failure switch
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
                Assert.Equal(rejected == "land" ? "Map geography unavailable" : rejected == "borders" ? "Country borders unavailable" : "Home city choices unavailable", Value<string>("Status"));
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
