using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeadWare.WinUI;

/// <summary>Every sensor the collector reads, with its current value and its minimum, average and maximum over the selected range.</summary>
internal sealed class SensorsPage : Page
{
    private readonly MonitorSession session;
    private readonly TextBox search = new() { PlaceholderText = "Search hardware or sensor", MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TableView table = new([new("Hardware / sensor", 3), new("Current", 1, true), new("Min", 1, true), new("Average", 1, true), new("Max", 1, true)]);
    private readonly TextBlock count = new() { Opacity = 0.7 };
    private bool refreshing;

    public SensorsPage(MonitorSession session)
    {
        this.session = session;
        Content = Pages.Layout(search, Pages.Card(table), count);
        search.TextChanged += (_, _) => Refresh();
        this.WhileLoaded(() => { session.Changed += Refresh; Refresh(); }, () => session.Changed -= Refresh);
    }

    private async void Refresh()
    {
        if (session.Current is not { } reading || refreshing) return;
        refreshing = true;
        try
        {
            string query = search.Text;
            int seconds = session.HistorySeconds;
            double now = session.Collector.ElapsedSeconds;
            var matching = reading.Sensors.Where(sensor => (sensor.Hardware + " " + sensor.Name).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            var summaries = await Task.Run(() => session.Collector.History.Summaries(matching.Select(sensor => sensor.Id), now, seconds));
            // A search or range change while the summaries were read must not publish the older query over the new view.
            if (!IsLoaded || query != search.Text || seconds != session.HistorySeconds) return;
            table.SetRows(matching.Select(sensor =>
            {
                var summary = summaries[sensor.Id];
                string unit = " " + sensor.Unit;
                return new TableRow(sensor.Id,
                [
                    $"{sensor.Hardware}  /  {sensor.Name}", TableSync.Number(session.Stale ? null : sensor.Value, unit),
                    TableSync.Number(summary.Minimum, unit), TableSync.Number(summary.Average, unit), TableSync.Number(summary.Maximum, unit),
                ]);
            }).ToArray());
            count.Text = $"{matching.Length} sensors · statistics over {HistoryRanges.Label(seconds)} · — means unavailable";
        }
        finally { refreshing = false; }
    }
}

/// <summary>Applications by network traffic, under the network chart.</summary>
internal sealed class NetworkPage : Page
{
    private readonly MonitorSession session;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 }, count = new() { Opacity = 0.7 };
    private readonly TextBox search = new() { PlaceholderText = "Search application name or PID", MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TableView table = new([new("Application", 2), new("PID", 0.6, true), new("↓ Download", 1, true), new("↑ Upload", 1, true), new("Received", 1, true), new("Sent", 1, true)]);

    public NetworkPage(MonitorSession session)
    {
        this.session = session;
        var chart = Pages.Card(new HistoryChartView(session, "network"));
        chart.Height = 300;
        Content = Pages.Layout(chart, status, search, Pages.Card(table), count);
        search.TextChanged += (_, _) => Refresh();
        this.WhileLoaded(() => { session.Changed += Refresh; Refresh(); }, () => session.Changed -= Refresh);
    }

    private void Refresh()
    {
        if (session.Current is not { } reading) return;
        status.Text = reading.EtwStatus;
        string query = search.Text;
        var rows = reading.Processes
            .Where(process => process.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || process.Pid.ToString().Contains(query))
            .OrderByDescending(process => (process.DownloadBytesPerSecond ?? 0) + (process.UploadBytesPerSecond ?? 0))
            .Select(process => new TableRow($"{process.Pid}/{process.Generation}",
            [
                session.IsExcluded(process.Name) ? process.Name + ProcessCopy.NotRecordedSuffix : process.Name, process.Pid.ToString(),
                HistoryChartModel.Bytes(session.Stale ? null : process.DownloadBytesPerSecond, true), HistoryChartModel.Bytes(session.Stale ? null : process.UploadBytesPerSecond, true),
                HistoryChartModel.Bytes(process.ReceivedBytes), HistoryChartModel.Bytes(process.SentBytes),
            ]))
            .ToArray();
        table.SetRows(rows);
        count.Text = rows.Length > 0 ? $"{rows.Length} applications"
            : reading.Processes.Count == 0 ? "No attributed traffic yet. Check the accounting status above." : "No applications match your search.";
    }
}

/// <summary>Layout shared by the ported pages.</summary>
internal static class Pages
{
    /// <summary>A card around one element.</summary>
    public static Border Card(UIElement child) => new()
    {
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(8),
        Child = child,
    };

    /// <summary>Stacks the parts with the page's margins; the part that is a card around a table takes the spare height.</summary>
    public static Grid Layout(params FrameworkElement[] parts)
    {
        var grid = new Grid { Padding = new Thickness(24), RowSpacing = 12 };
        for (int i = 0; i < parts.Length; i++)
        {
            bool fills = parts[i] is Border { Child: TableView };
            grid.RowDefinitions.Add(new RowDefinition { Height = fills ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(parts[i], i);
            grid.Children.Add(parts[i]);
        }
        return grid;
    }
}
