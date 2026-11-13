using DeadWare.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// Stands in for Overview until it is ported: live readings from the real collector as plain text, which shows that
/// the services run under this front end, and the five Overview charts drawn by the new chart kit.
/// </summary>
internal sealed class StatusPage : Page
{
    private readonly MonitorSession session;
    private readonly Dictionary<string, TextBlock> values = [];
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly string backdrop;

    public StatusPage(MonitorSession session, bool mica)
    {
        this.session = session;
        backdrop = mica ? "Mica" : "solid";
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (string label in new[] { "CPU usage", "CPU temperature", "Graphics card", "GPU usage", "GPU temperature", "Memory in use" })
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label };
            var value = new TextBlock { Text = "—", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            Grid.SetRow(name, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
            values[label] = value;
        }
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = grid,
        };
        var intro = new TextBlock
        {
            Text = "The WinUI interface is being built page by page. This page shows live readings and charts from the same monitoring services as the current app; Network, Processes and Sensors are ported; the remaining pages are placeholders.",
            TextWrapping = TextWrapping.Wrap,
        };
        var charts = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        charts.ColumnDefinitions.Add(new ColumnDefinition());
        charts.ColumnDefinitions.Add(new ColumnDefinition());
        (string Title, string Kind, bool Compare)[] kinds =
            [("CPU / GPU usage", "cpu", true), ("CPU / GPU temperature", "cputemp", true), ("RAM usage", "memory", false), ("Network activity", "network", false), ("Disk activity", "storage", false)];
        for (int i = 0; i < kinds.Length; i++)
        {
            if (i % 2 == 0) charts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(220) });
            var chartCard = ChartCard(kinds[i].Title, new HistoryChartView(session, kinds[i].Kind, kinds[i].Compare));
            Grid.SetRow(chartCard, i / 2);
            Grid.SetColumn(chartCard, i % 2);
            charts.Children.Add(chartCard);
        }
        Content = new ScrollViewer
        {
            Content = new StackPanel { Padding = new Thickness(24), Spacing = 16, Children = { intro, card, charts, status } },
        };
        Loaded += (_, _) => { session.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => session.Changed -= Refresh;
    }

    /// <summary>A titled card holding one chart, with the main trace's minimum, average and maximum beside the title.</summary>
    private static Border ChartCard(string title, HistoryChartView chart)
    {
        var heading = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var statistics = new TextBlock { Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
        chart.StatisticsReady += summary => statistics.Text = summary.Average is { } average && summary.Minimum is { } minimum && summary.Maximum is { } maximum
            ? $"min {minimum:0.#} · avg {average:0.#} · max {maximum:0.#}" : "";
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(chart, 1);
        layout.Children.Add(heading);
        layout.Children.Add(statistics);
        layout.Children.Add(chart);
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = layout,
        };
    }

    private void Refresh()
    {
        static string Percent(double? value) => value is { } v ? $"{v:0.0} %" : "—";
        static string Degrees(double? value) => value is { } v ? $"{v:0.0} °C" : "—";
        values["CPU usage"].Text = Percent(session.Value(session.MetricId("cpu")));
        values["CPU temperature"].Text = Degrees(session.Value(session.MetricId("cputemp")));
        values["Graphics card"].Text = session.GpuName;
        values["GPU usage"].Text = Percent(session.Value(session.MetricId("gpu")));
        values["GPU temperature"].Text = Degrees(session.Value(session.MetricId("gputemp")));
        values["Memory in use"].Text = Percent(session.Value(session.MetricId("memory")));
        string readings = session.Current is null ? "Waiting for the first reading." : session.Stale ? "Readings are stale." : "Readings are current.";
        string administrator = Collector.IsElevated ? "Running as administrator." : "Not running as administrator, so some sensors and per-app capture are unavailable.";
        status.Text = string.Join(" ", new[] { readings, administrator, session.SettingsStatus, $"Version {App.Version}, {backdrop} background." }.Where(text => text.Length > 0));
    }
}

/// <summary>A destination that has not been ported yet.</summary>
internal sealed class PlaceholderPage : Page
{
    public PlaceholderPage(string label) =>
        Content = new TextBlock
        {
            Text = $"{label} has not been ported to the WinUI interface yet. Use the current DeadWare app for it.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
            Opacity = 0.7,
        };
}
