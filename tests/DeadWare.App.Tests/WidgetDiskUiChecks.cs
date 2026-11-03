using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Offscreen checks for the widget's optional Disk graph; run from the shared STA UI test.</summary>
internal static class WidgetDiskUiChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Disk_graph_shows_legend_with_both_directions();
            Disk_graph_shows_dashes_when_a_side_is_missing();
            Disk_graph_uses_the_storage_chart_kind();
            Widget_window_renders_disk_last_and_default_layout_has_none();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void Disk_graph_shows_legend_with_both_directions()
    {
        var texts = Texts(Graph(Session(2_000_000, 500_000))).Select(t => t.Trim()).ToList();
        Assert.Contains("Disk", texts);
        Assert.Contains($"{Ui.Bytes(2_000_000, true)} ↓", texts);
        Assert.Contains($"{Ui.Bytes(500_000, true)} ↑", texts);
    }

    private static void Disk_graph_shows_dashes_when_a_side_is_missing()
    {
        var texts = Texts(Graph(Session(null, 500_000))).Select(t => t.Trim()).ToList();
        Assert.Contains("— ↓", texts);
        Assert.Contains($"{Ui.Bytes(500_000, true)} ↑", texts);
    }

    private static void Disk_graph_uses_the_storage_chart_kind()
    {
        var graph = Graph(Session(1000, 500));
        var chart = (HistoryChart)typeof(WidgetGraph).GetField("chart", Fields)!.GetValue(graph)!;
        Assert.Equal("storage", (string)typeof(HistoryChart).GetField("kind", Fields)!.GetValue(chart)!);
        Assert.True(Descendants(graph).OfType<System.Windows.Shapes.Line>().Count() >= 2);
    }

    private static void Widget_window_renders_disk_last_and_default_layout_has_none()
    {
        var session = Session(1000, 500);
        var window = new WidgetWindow(session);
        var content = (FrameworkElement)window.Content;
        Layout(content, 300, 700);
        var visible = Descendants(content).OfType<WidgetGraph>().Where(g => g.Visibility == Visibility.Visible).ToArray();
        Assert.Equal(4, visible.Length);
        Assert.DoesNotContain(visible, g => Texts(g).Contains("Disk"));
        session.Settings.WidgetGraphs = ["cpu", "disk"];
        ((Action)typeof(UiSession).GetField("Changed", Fields)!.GetValue(session)!).Invoke();
        Layout(content, 300, 700);
        visible = Descendants(content).OfType<WidgetGraph>().Where(g => g.Visibility == Visibility.Visible).ToArray();
        Assert.Equal(2, visible.Length);
        Assert.Contains("Disk", Texts(visible[1]));
        window.Close();
    }

    private static WidgetGraph Graph(UiSession session)
    {
        var graph = new WidgetGraph(session, "disk");
        graph.Refresh();
        Layout(graph, 300, 150);
        return graph;
    }

    private static void Layout(FrameworkElement element, double width, double height)
    { element.Measure(new(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout(); }

    private static List<string> Texts(DependencyObject root) => Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToList();

    private static UiSession Session(double? read, double? write)
    {
        void Set(object target, string field, object value) => target.GetType().GetField(field, Fields)!.SetValue(target, value);
        var history = new SampleHistory();
        var epoch = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        for (int second = -10; second <= 0; second++)
            history.Add(new(epoch.AddSeconds(second), second, new Dictionary<string, double?> { ["storage/total/read"] = read, ["storage/total/write"] = write }));
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector, "clock", new Stopwatch()); Set(collector, "<History>k__BackingField", history);
        Set(collector, "latest", new Reading(history.Read()[^1], [], [], [], "Fixture CPU", "", "", "", false));
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session, "<Collector>k__BackingField", collector);
        Set(session, "<Settings>k__BackingField", new UserSettings { HistorySeconds = 60 });
        Set(session, "captureWindows", new Dictionary<Window, Action>());
        return session;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
