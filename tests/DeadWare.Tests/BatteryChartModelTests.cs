using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class BatteryChartModelTests
{
    // Decimals and dates are written in the user's locale; the expected texts here are for the invariant one.
    public BatteryChartModelTests() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

    static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    static readonly ChartPlot Plot = new(10, 20, 600, 100);

    /// <summary>One single-observation bucket a minute apart from the last, in one recording segment.</summary>
    static BatteryHistoryBucket One(int minute, double? value, long saver = 0, long saverKnown = 1, long charging = 0, string? segment = "s") =>
        new(T0.AddMinutes(minute), T0.AddMinutes(minute), value, value, value, 1, value.HasValue ? 1 : 0, charging, saver, saverKnown, segment);

    static BatteryHistoryView View(params BatteryHistoryBucket[] buckets) => new(T0, T0.AddHours(1), buckets, buckets.Length, T0, 4096);

    [Fact] public void The_axis_has_five_lines_from_0_to_100_percent()
    {
        var chart = BatteryChartModel.Build(View(), Plot, TimeZoneInfo.Utc);
        Assert.Equal(["0%", "25%", "50%", "75%", "100%"], chart.Lines.Select(l => l.Label));
        Assert.Equal([120d, 95d, 70d, 45d, 20d], chart.Lines.Select(l => l.Y));
    }

    [Fact] public void A_point_sits_at_its_bucket_s_time_and_charge()
    {
        var chart = BatteryChartModel.Build(View(One(30, 50)), Plot, TimeZoneInfo.Utc);
        var point = Assert.Single(chart.Points);
        Assert.Equal((310d, 70d), (point.X, point.Y));
        Assert.Equal(BatterySaverLook.Off, point.Look);
        Assert.True(point.Certain);
        Assert.Null(point.Connector);
    }

    [Fact] public void Consecutive_single_observations_in_one_segment_are_joined_in_the_earlier_one_s_look()
    {
        var chart = BatteryChartModel.Build(View(One(30, 50, saver: 1), One(31, 40)), Plot, TimeZoneInfo.Utc);
        var second = chart.Points[1];
        Assert.Equal((310d, 70d, BatterySaverLook.On), second.Connector);
        Assert.Equal(BatterySaverLook.Off, second.Look);
    }

    [Fact] public void A_gap_a_new_segment_or_a_summarised_bucket_is_not_joined()
    {
        Assert.Null(BatteryChartModel.Build(View(One(30, 50), One(33, 40)), Plot, TimeZoneInfo.Utc).Points[1].Connector);              // more than 90 s apart
        Assert.Null(BatteryChartModel.Build(View(One(30, 50), One(31, 40, segment: "other")), Plot, TimeZoneInfo.Utc).Points[1].Connector);
        var summarised = One(31, 40) with { Samples = 5, ValidSamples = 5 };
        Assert.Null(BatteryChartModel.Build(View(One(30, 50), summarised), Plot, TimeZoneInfo.Utc).Points[1].Connector);
    }

    [Fact] public void Saver_state_decides_the_look_and_mixed_or_unknown_state_is_uncertain()
    {
        var known = One(10, 50, saver: 1);
        var unknown = One(11, 50, saverKnown: 0);
        var mixed = One(12, 50) with { Samples = 4, ValidSamples = 4, SaverSamples = 2, SaverKnownSamples = 4 };
        var points = BatteryChartModel.Build(View(known, unknown, mixed), Plot, TimeZoneInfo.Utc).Points;
        Assert.Equal([BatterySaverLook.On, BatterySaverLook.Unknown, BatterySaverLook.On], points.Select(p => p.Look));
        Assert.Equal([true, false, false], points.Select(p => p.Certain));
    }

    [Fact] public void A_summarised_bucket_carries_its_range()
    {
        var bucket = new BatteryHistoryBucket(T0.AddMinutes(20), T0.AddMinutes(40), 50, 25, 75, 20, 20, 0, 0, 20, "s");
        var point = BatteryChartModel.Build(View(bucket), Plot, TimeZoneInfo.Utc).Points[0];
        Assert.Equal(310d, point.X);                                   // the middle of the bucket
        Assert.Equal((45d, 95d), point.Range);                         // 75 % above, 25 % below
        Assert.Equal(100d, point.Tolerance);                           // half the bucket's width
    }

    [Fact] public void A_bucket_with_no_charge_reading_has_no_point_but_keeps_its_marks()
    {
        var chart = BatteryChartModel.Build(View(One(30, null, charging: 1)), Plot, TimeZoneInfo.Utc);
        Assert.Empty(chart.Points);
        var mark = Assert.Single(chart.Marks);
        Assert.Equal((310d, BatteryMarkKind.Charging, true), (mark.X, mark.Kind, mark.Whole));
        Assert.False(chart.HasReadings);
    }

    [Fact] public void Marks_show_charging_and_saver_whole_or_partial()
    {
        var bucket = new BatteryHistoryBucket(T0.AddMinutes(20), T0.AddMinutes(40), 50, 25, 75, 4, 4, ChargingSamples: 4, SaverSamples: 1, SaverKnownSamples: 4, "s");
        var marks = BatteryChartModel.Build(View(bucket), Plot, TimeZoneInfo.Utc).Marks;
        Assert.Equal([(BatteryMarkKind.Charging, true), (BatteryMarkKind.Saver, false)], marks.Select(m => (m.Kind, m.Whole)));
    }

    [Fact] public void Date_labels_show_the_time_up_to_two_days_and_the_year_beyond()
    {
        var day = BatteryChartModel.Build(View(), Plot, TimeZoneInfo.Utc);
        Assert.Equal(("Nov 12 10:00", "Nov 12 11:00"), (day.FromLabel, day.ToLabel));
        var year = BatteryChartModel.Build(new BatteryHistoryView(T0.AddDays(-365), T0, [], 0, null, 0), Plot, TimeZoneInfo.Utc);
        Assert.Equal(("Nov 12 2025", "Nov 12 2026"), (year.FromLabel, year.ToLabel));
    }

    [Fact] public void Hover_picks_the_nearest_point_within_its_tolerance_inside_the_plot()
    {
        var chart = BatteryChartModel.Build(View(One(30, 50), One(31, 40)), Plot, TimeZoneInfo.Utc);
        var hover = BatteryChartModel.Hover(chart, 311, 60)!;
        Assert.Equal((310d, 70d, "Nov 12 10:30", "50%"), (hover.X, hover.Y, hover.Time, hover.Text));
        Assert.Null(BatteryChartModel.Hover(chart, 100, 60));        // nothing recorded near there
        Assert.Null(BatteryChartModel.Hover(chart, 311, 300));       // below the plot
    }

    [Theory]
    [InlineData(0, 100)] [InlineData(600, 0)] [InlineData(double.NaN, 100)]
    public void A_plot_with_no_area_gives_an_empty_chart(double width, double height)
    {
        var chart = BatteryChartModel.Build(View(One(30, 50)), new ChartPlot(0, 0, width, height), TimeZoneInfo.Utc);
        Assert.Empty(chart.Points); Assert.Empty(chart.Lines); Assert.Empty(chart.Marks);
    }

    [Fact] public void The_page_summarises_the_view_and_the_local_store()
    {
        var view = View(One(30, 50), One(31, 40.5), One(32, null));
        Assert.Equal("MIN 40.5%    MAX 50.0%    ·    3 observations in this view", BatteryPresenter.Summary(view));
        Assert.Equal("No charge observations in this range yet. Saved readings will appear as monitoring continues.", BatteryPresenter.Summary(View()));
        Assert.StartsWith("Local history: 4.1 KB · 3 observations retained indefinitely\nRecorded since ", BatteryPresenter.Storage(view));
        Assert.Equal("Local history: 0.0 B · 0 observations retained indefinitely", BatteryPresenter.Storage(new BatteryHistoryView(T0, T0, [], 0, null, 0)));
    }

    [Theory]
    [InlineData(BatteryPowerState.NoBattery, false, "No battery detected")] [InlineData(BatteryPowerState.Charging, false, "Charging")]
    [InlineData(BatteryPowerState.OnBattery, false, "On battery")] [InlineData(BatteryPowerState.PluggedIn, false, "Plugged in · not charging")]
    [InlineData(BatteryPowerState.Unknown, false, "Battery reading unavailable")] [InlineData(BatteryPowerState.Charging, true, "Waiting for a fresh reading")]
    public void The_power_line_names_the_state(BatteryPowerState state, bool stale, string expected) => Assert.Equal(expected, BatteryPresenter.Power(state, stale));
}
