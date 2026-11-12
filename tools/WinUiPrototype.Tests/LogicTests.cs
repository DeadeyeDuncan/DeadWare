using WinUiPrototype.Logic;
using Xunit;

namespace WinUiPrototype.Tests;

public class LogicTests
{
    [Fact] public void Series_keeps_only_the_newest_capacity_samples()
    {
        var s = new SampleSeries(3);
        foreach (var v in new double?[] { 1, 2, 3, 4 }) s.Add(v);
        Assert.Equal(3, s.Count); Assert.Equal(2, s[0]); Assert.Equal(4, s[2]); Assert.Equal(4, s.Latest);
    }
    [Fact] public void Series_latest_is_null_when_empty_or_missing()
    {
        var s = new SampleSeries(3); Assert.Null(s.Latest);
        s.Add(5); s.Add(null); Assert.Null(s.Latest); Assert.Equal(2, s.Count);
    }
    [Fact] public void Source_is_deterministic_in_range_and_gaps_every_97th()
    {
        var a = new SyntheticSource(7); var b = new SyntheticSource(7);
        for (int call = 1; call <= 300; call++)
        {
            var x = a.Next(); Assert.Equal(x, b.Next());
            if (call % 97 == 0) Assert.Null(x); else Assert.InRange(x!.Value, 0, 100);
        }
    }
    [Fact] public void Runs_split_at_gaps_and_newest_sits_at_the_right_edge()
    {
        var s = new SampleSeries(5);
        foreach (var v in new double?[] { 0, 100, null, 50, 50 }) s.Add(v);
        var runs = ChartGeometry.Runs(s, 400, 100, 0, 100);
        Assert.Equal(2, runs.Count); Assert.Equal(2, runs[0].Count); Assert.Equal(2, runs[1].Count);
        Assert.Equal((0f, 100f), runs[0][0]);
        Assert.Equal((100f, 0f), runs[0][1]);
        Assert.Equal((400f, 50f), runs[1][1]);
    }
    [Fact] public void Runs_of_a_part_filled_series_stay_right_aligned()
    {
        var s = new SampleSeries(5); s.Add(10); s.Add(10);
        var runs = ChartGeometry.Runs(s, 400, 100, 0, 100);
        Assert.Equal(300f, runs[0][0].X); Assert.Equal(400f, runs[0][1].X);
    }
    [Theory]
    [InlineData(0f, 100f)] [InlineData(400f, 0f)] [InlineData(-1f, 100f)]
    public void Runs_are_empty_for_a_canvas_with_no_area(float w, float h)
    {
        var s = new SampleSeries(5); s.Add(1); s.Add(2);
        Assert.Empty(ChartGeometry.Runs(s, w, h, 0, 100));
    }
    [Fact] public void Runs_are_empty_for_an_empty_or_all_missing_series()
    {
        var s = new SampleSeries(5); Assert.Empty(ChartGeometry.Runs(s, 400, 100, 0, 100));
        s.Add(null); s.Add(null); Assert.Empty(ChartGeometry.Runs(s, 400, 100, 0, 100));
    }
    [Fact] public void Runs_of_a_flat_range_sit_on_the_middle_line_and_are_finite()
    {
        var s = new SampleSeries(5); s.Add(5); s.Add(5);
        var runs = ChartGeometry.Runs(s, 400, 100, 5, 5);
        Assert.All(runs[0], p => { Assert.Equal(50f, p.Y); Assert.True(float.IsFinite(p.X)); });
    }
    [Fact] public void Runs_clamp_values_outside_the_range()
    {
        var s = new SampleSeries(2); s.Add(-50); s.Add(500);
        var runs = ChartGeometry.Runs(s, 400, 100, 0, 100);
        Assert.Equal(100f, runs[0][0].Y); Assert.Equal(0f, runs[0][1].Y);
    }
    [Fact] public void Fill_tops_the_series_up_to_capacity_from_the_source()
    {
        var s = new SampleSeries(600); s.Add(1);
        s.Fill(new SyntheticSource(3));
        Assert.Equal(600, s.Count); Assert.Equal(1, s[0]);
        Assert.Equal(6, Enumerable.Range(0, 600).Count(i => s[i] is null)); // every 97th of 599 calls
    }
}
