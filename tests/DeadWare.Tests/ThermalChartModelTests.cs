using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ThermalChartModelTests
{
    public ThermalChartModelTests() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

    static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    static readonly ChartPlot Plot = new(10, 20, 600, 100);
    static readonly ThermalDevice Cpu = new("cpu0", "Ryzen", "CPU", "setup-a", "map");

    static ThermalWindow Minute(int minute, double? temperature, string session = "s", ThermalDevice? device = null)
    {
        var value = temperature is double t ? new MetricAggregate(60, t - 2, t + 2, t) : new MetricAggregate(0, null, null, null);
        return new(device ?? Cpu, session, T0.AddMinutes(minute), T0.AddMinutes(minute + 1), minute * 60, 60, 60,
            value, new(60, 10, 30, 20), new(60, 40, 60, 50), new(0, null, null, null), new(0, null, null, null), true, "ok");
    }

    [Fact] public void Temperature_axis_never_tops_out_below_40_degrees()
    {
        var frame = ThermalChartModel.Build([Minute(0, 30)], "temperature", T0, T0.AddHours(1), Plot, TimeZoneInfo.Utc);
        Assert.Equal(40, frame.Maximum);
        Assert.Equal(["0 °C", "20 °C", "40 °C"], frame.Lines.Select(l => l.Label));
        Assert.Equal("Nov 12 10:00", frame.FromLabel);
    }

    [Fact] public void A_point_sits_at_the_middle_of_its_window_with_its_spread()
    {
        var frame = ThermalChartModel.Build([Minute(30, 20)], "temperature", T0, T0.AddHours(1), Plot, TimeZoneInfo.Utc);
        var point = Assert.Single(frame.Points);
        Assert.Equal(10 + 600 * 30.5 / 60, point.X, 6);
        Assert.Equal(70, point.Y, 6);
        Assert.Equal((65d, 75d), (Math.Round(point.Spread!.Value.Top, 6), Math.Round(point.Spread.Value.Bottom, 6)));
    }

    [Fact] public void Consecutive_minutes_join_and_a_gap_or_new_session_breaks_the_run()
    {
        var frame = ThermalChartModel.Build([Minute(0, 50), Minute(1, 51), Minute(5, 52), Minute(6, 53, "other")], "temperature", T0, T0.AddHours(1), Plot, TimeZoneInfo.Utc);
        Assert.Equal([2, 1, 1], frame.Runs.Select(r => r.Count));
    }

    [Fact] public void A_window_without_the_metric_is_not_plotted_and_breaks_the_run()
    {
        var frame = ThermalChartModel.Build([Minute(0, 50), Minute(1, null), Minute(2, 51)], "temperature", T0, T0.AddHours(1), Plot, TimeZoneInfo.Utc);
        Assert.Equal(2, frame.Points.Count);
        Assert.Equal(2, frame.Runs.Count);
        Assert.False(ThermalChartModel.Build([Minute(0, 50)], "fan", T0, T0.AddHours(1), Plot, TimeZoneInfo.Utc).HasReadings);
    }

    [Fact] public void Hover_reads_the_nearest_window_inside_the_plot_only()
    {
        var frame = ThermalChartModel.Build([Minute(30, 50)], "temperature", T0, T0.AddHours(1), Plot, TimeZoneInfo.Utc);
        var hover = ThermalChartModel.Hover(frame, frame.Points[0].X + 3, 60, TimeZoneInfo.Utc);
        Assert.Equal(("Nov 12 10:30", "50 °C"), (hover!.Time, hover.Text));
        Assert.Null(ThermalChartModel.Hover(frame, frame.Points[0].X + 40, 60, TimeZoneInfo.Utc));
        Assert.Null(ThermalChartModel.Hover(frame, frame.Points[0].X, 5, TimeZoneInfo.Utc));
    }

    [Fact] public void Summary_weights_the_average_by_valid_samples()
    {
        string text = ThermalChartModel.Summary([Minute(0, 50), Minute(1, 60)], "temperature");
        Assert.StartsWith("MIN 48.0 °C    AVG 55.0 °C    MAX 62.0 °C\n120 valid samples · 2 plotted windows · 1 min resolution · 2 eligible", text);
        Assert.StartsWith("No valid samples", ThermalChartModel.Summary([Minute(0, 50)], "clock"));
    }

    [Fact] public void Device_choices_order_by_kind_and_mark_older_setups_and_sensor_maps()
    {
        var gpu = new ThermalDevice("gpu0", "Radeon", "GPU", "setup-a", "map");
        var old = new ThermalDevice("cpu0", "Ryzen", "CPU", "oldsetup1", "map");
        var remapped = new ThermalDevice("cpu9abcdef", "Ryzen", "CPU", "setup-a", "map2");
        var choices = ThermalChartModel.DeviceChoices([gpu, Cpu, old, remapped], "setup-a");
        Assert.Equal(["CPU · Ryzen · current setup", "CPU · Ryzen · previous setup oldset", "CPU · Ryzen · current setup · older sensor map cpu9ab", "GPU · Radeon · current setup"], choices.Select(c => c.Label));
    }

    [Fact] public void Local_times_parse_strictly()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 18, 30, 0, TimeSpan.Zero), ThermalChartModel.ParseLocal(" 2026-10-25 18:30 ", TimeZoneInfo.Utc));
        Assert.Throws<ArgumentException>(() => ThermalChartModel.ParseLocal("25/10/2026", TimeZoneInfo.Utc));
    }
}
