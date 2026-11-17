using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class MapAnimationTests
{
    [Fact] public void A_map_in_use_draws_about_thirty_frames_a_second()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(31), MapAnimation.Interval(0));
        Assert.Equal(TimeSpan.FromMilliseconds(31), MapAnimation.Interval(59.9));
        // Not knowing how long it has been idle is treated as just touched.
        Assert.Equal(TimeSpan.FromMilliseconds(31), MapAnimation.Interval(double.NaN));
        Assert.Equal(TimeSpan.FromMilliseconds(31), MapAnimation.Interval(-5));
    }

    [Fact] public void An_untouched_map_slows_after_a_minute_and_again_after_five()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(62), MapAnimation.Interval(MapAnimation.IdleSeconds));
        Assert.Equal(TimeSpan.FromMilliseconds(62), MapAnimation.Interval(299));
        Assert.Equal(TimeSpan.FromMilliseconds(93), MapAnimation.Interval(MapAnimation.LongIdleSeconds));
        Assert.Equal(TimeSpan.FromMilliseconds(93), MapAnimation.Interval(double.PositiveInfinity));
    }

    [Fact] public void Each_interval_is_met_on_the_clock_tick_it_aims_for()
    {
        // Windows' default timer tick; an interval just over a whole number of ticks would wait for one more.
        const double tick = 15.625;
        foreach (var (idle, ticks) in new[] { (0d, 2), (MapAnimation.IdleSeconds, 4), (MapAnimation.LongIdleSeconds, 6) })
        {
            double milliseconds = MapAnimation.Interval(idle).TotalMilliseconds;
            Assert.InRange(milliseconds, (ticks - 1) * tick + 1, ticks * tick);
        }
    }

    [Fact] public void A_frame_step_never_goes_back_or_jumps()
    {
        Assert.Equal(.031, MapAnimation.Step(.031), 10);
        Assert.Equal(0, MapAnimation.Step(-1));
        Assert.Equal(MapAnimation.MaximumStepSeconds, MapAnimation.Step(4));
        Assert.Equal(0, MapAnimation.Step(double.NaN));
        // The slowest rate's frames still advance by their real time.
        Assert.Equal(MapAnimation.Interval(MapAnimation.LongIdleSeconds).TotalSeconds, MapAnimation.Step(MapAnimation.Interval(MapAnimation.LongIdleSeconds).TotalSeconds), 10);
    }

    static readonly ScreenRect Area = new(100, 100, 300, 200);

    [Fact] public void Nothing_above_leaves_the_area_visible()
    {
        Assert.False(ScreenCoverage.Covered(Area, []));
        Assert.False(ScreenCoverage.Covered(Area, [new(0, 0, 50, 50), new(400, 0, 500, 500)]));
    }

    [Fact] public void One_window_over_all_of_it_covers_it()
    {
        Assert.True(ScreenCoverage.Covered(Area, [new(0, 0, 1920, 1080)]));
        Assert.True(ScreenCoverage.Covered(Area, [Area]));
    }

    [Fact] public void Windows_that_cover_it_between_them_cover_it()
    {
        Assert.True(ScreenCoverage.Covered(Area, [new(0, 0, 200, 1000), new(200, 0, 1000, 150), new(150, 140, 400, 400)]));
    }

    [Fact] public void A_single_pixel_left_showing_keeps_it_visible()
    {
        Assert.False(ScreenCoverage.Covered(Area, [new(0, 0, 199, 1000), new(200, 0, 1000, 1000)]));
        Assert.False(ScreenCoverage.Covered(Area, [new(0, 0, 1000, 199), new(101, 199, 1000, 1000), new(0, 200, 1000, 1000)]));
    }

    [Fact] public void An_empty_area_counts_as_covered_and_empty_windows_cover_nothing()
    {
        Assert.True(ScreenCoverage.Covered(new ScreenRect(10, 10, 10, 50), []));
        Assert.False(ScreenCoverage.Covered(Area, [new(300, 300, 0, 0), new(150, 150, 150, 150)]));
    }

    [Fact] public void A_screen_too_fragmented_to_judge_is_called_visible()
    {
        // A grid of small windows with gaps between them breaks the area into more pieces than the bound allows.
        var covers = new List<ScreenRect>();
        for (int x = 100; x < 300; x += 10)
            for (int y = 100; y < 200; y += 10)
                covers.Add(new(x, y, x + 9, y + 9));
        covers.Add(new(0, 0, 1920, 1080));
        Assert.False(ScreenCoverage.Covered(Area, covers));
    }
}
