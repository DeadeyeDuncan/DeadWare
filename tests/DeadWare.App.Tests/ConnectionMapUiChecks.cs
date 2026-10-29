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
        CheckDirectionalFlowAndVolume();
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

    private static void CheckIndependentAssetRecovery()
    {
        const string validLand = "[[[0,0],[1,0],[0,1],[0,0]]]";
        const string validCities = "[{\"id\":\"fixture\",\"name\":\"Fixture city\",\"country\":\"Fixture country\",\"latitude\":1,\"longitude\":2}]";
        var load = typeof(ConnectionAssets).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failures = new List<Exception?>();
        foreach (string rejected in new[] { "land", "cities" })
        foreach (string failure in new[] { "malformed", "oversized", "missing" })
        {
            failures.Add(Record.Exception(() =>
            {
                string Read(string name) => name != rejected ? name == "land" ? validLand : validCities : failure switch
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
                Assert.Equal(rejected == "land" ? "Map geography unavailable" : "Home city choices unavailable", Value<string>("Status"));
                if (rejected == "land")
                {
                    Assert.Empty(land); Assert.Equal(new HomeCity("fixture", "Fixture city", "Fixture country", new(1, 2)), Assert.Single(cities));
                }
                else
                {
                    Assert.Empty(cities); Assert.Equal(new[] { new GeoPoint(0, 0), new(0, 1), new(1, 0), new(0, 0) }, Assert.Single(land));
                }
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
