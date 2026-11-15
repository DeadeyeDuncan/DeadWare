using System.Globalization;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class UsageChartModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ChartPlot Plot = new(10, 10, 600, 100);

    public UsageChartModelTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    private static UsageHistoryBucket Bucket(int index, double average, long minutes = 10, double spread = 5)
        => new(Start.AddMinutes(index * 10), Start.AddMinutes(index * 10 + 10), average, average - spread, average + spread, minutes, minutes, minutes * 60);

    private static UsageChartSeries Series(params UsageHistoryBucket[] buckets)
        => new(new UsageHistoryView(Start, Start.AddHours(1), 600_000, buckets, new Summary(1, 9, 5), buckets.Sum(b => b.Minutes), Start, 0), "cpu", false, "CPU");

    [Fact]
    public void AdjacentFullBucketsJoinAndAGapOrPartialBucketBreaksTheLine()
    {
        var frame = UsageChartModel.Build([Series(Bucket(0, 20), Bucket(1, 40), Bucket(3, 60), Bucket(4, 50, minutes: 3), Bucket(5, 10))], "%", 100, Plot, zone: TimeZoneInfo.Utc);
        var trace = Assert.Single(frame.Traces);
        Assert.Equal([2, 1, 1, 1], trace.Runs.Select(r => r.Count));
        Assert.Equal(5, trace.Bands.Count);
        Assert.Equal(5, frame.Targets.Count);
        Assert.True(frame.Any);
        Assert.Equal(60, frame.Targets[0].X, 6);
        Assert.Equal(50, frame.Targets[0].Tolerance, 6);
        Assert.Equal(90, trace.Runs[0][0].Y, 6);
        Assert.Equal("Nov 1 00:00", frame.FromLabel);
        Assert.Equal("Nov 1 01:00", frame.ToLabel);
        Assert.Equal(["0%", "25%", "50%", "75%", "100%"], frame.Lines.Select(l => l.Label));
    }

    [Fact]
    public void AutomaticScaleAddsHeadroomAndTemperatureKeepsAFloor()
    {
        Assert.Equal(40, UsageChartModel.Build([Series(Bucket(0, 20))], "°C", null, Plot).Maximum);
        Assert.Equal(105 * 1.15, UsageChartModel.Build([Series(Bucket(0, 100))], "B/s", null, Plot).Maximum, 6);
        Assert.False(UsageChartModel.Build([Series()], "%", 100, Plot).Any);
    }

    [Fact]
    public void HoverFindsTheBucketUnderThePointerOnlyInsideThePlotAndItsOwnWidth()
    {
        var series = new[] { Series(Bucket(0, 20), Bucket(3, 60)) };
        var frame = UsageChartModel.Build(series, "%", 100, Plot, zone: TimeZoneInfo.Utc);
        Assert.Equal(0, UsageChartModel.BucketAt(frame, 30, 50));
        Assert.Equal(-1, UsageChartModel.BucketAt(frame, 200, 50));
        Assert.Equal(1, UsageChartModel.BucketAt(frame, 350, 50));
        Assert.Equal(-1, UsageChartModel.BucketAt(frame, 30, 500));
        var hover = UsageChartModel.Hover(frame, series, 1, "%", zone: TimeZoneInfo.Utc);
        Assert.NotNull(hover);
        Assert.Equal("Nov 1 00:30", hover.Time);
        Assert.Equal("60%", Assert.Single(hover.Values).Text);
        Assert.Null(UsageChartModel.Hover(frame, series, 2, "%"));
    }

    [Fact]
    public void StepMovesTheSelectionAndStopsAtTheEdges()
    {
        Assert.Equal(4, UsageChartModel.Step(null, 5, -1));
        Assert.Equal(0, UsageChartModel.Step(null, 5, 1));
        Assert.Equal(0, UsageChartModel.Step(0, 5, -1));
        Assert.Equal(4, UsageChartModel.Step(4, 5, 1));
        Assert.Equal(2, UsageChartModel.Step(3, 5, -1));
        Assert.Null(UsageChartModel.Step(1, 0, 1));
        Assert.Equal(1, UsageChartModel.BucketHolding([Bucket(0, 1), Bucket(2, 1)], Start.AddMinutes(25)));
        Assert.Null(UsageChartModel.BucketHolding([Bucket(0, 1), Bucket(2, 1)], Start.AddMinutes(15)));
    }

    [Fact]
    public void FormatsValuesDurationsAndSpans()
    {
        Assert.Equal("0.4%", UsageChartModel.Format(0.4, "%", tenths: true));
        Assert.Equal("0%", UsageChartModel.Format(0.4, "%"));
        Assert.Equal("45°C", UsageChartModel.Format(45.2, "°C"));
        Assert.Equal("12.5 s", UsagePresenter.Duration(12.5));
        Assert.Equal("18 min 0 s", UsagePresenter.Duration(1080));
        Assert.Equal("2 h 5 min", UsagePresenter.Duration(7500));
        Assert.Equal("—", UsagePresenter.Duration(-1));
        Assert.Equal(3, UsagePresenter.ElapsedMinutes(Start, Start.AddMinutes(10), Start.AddMinutes(3).AddSeconds(30)));
        Assert.Equal("03:13–03:16", UsagePresenter.SlotSpan(Start.AddMinutes(193), Start.AddMinutes(196), false, TimeZoneInfo.Utc));
        Assert.Equal("Busiest apps · Nov 1 23:50–Nov 2 00:10", UsagePresenter.SlotHeading(Start.AddMinutes(1430), Start.AddMinutes(1450), true, TimeZoneInfo.Utc));
        Assert.Equal("No readings in this period.", UsagePresenter.Statistics(Series().View, "%"));
        Assert.Equal("MIN 1.0%    AVG 5.0%    MAX 9.0%    ·    10 minutes", UsagePresenter.Statistics(Series(Bucket(0, 5)).View, "%"));
    }

    [Fact]
    public void ApplicationRowsFilterAndKeepTheOtherRowOnlyWithoutASearch()
    {
        var view = new UsageApplicationView(Start, Start.AddHours(1), [new("chrome.exe", 3000, 1000, 5), new("code.exe", 500, 500, 5)], 2, 4000, 1000, 0, 60, Start, 0);
        var all = UsagePresenter.ApplicationRows(view, "");
        Assert.Equal(3, all.Count);
        Assert.Equal("\0", all[2].Key);
        Assert.Equal("Other applications (2 more)", all[2].Cells[0]);
        Assert.Equal("40%", all[0].Cells[4]);
        var found = UsagePresenter.ApplicationRows(view, "CODE");
        Assert.Equal("code.exe", Assert.Single(found).Key);
        Assert.Equal("1 of 4 applications match.", UsagePresenter.ApplicationSummary(view, "CODE", 1, false));
        Assert.StartsWith("4 applications · ", UsagePresenter.ApplicationSummary(view, "", 3, false));
    }
}
