using System.Globalization;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class FirstNetworkUseTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset L = new(2026, 11, 7, 23, 59, 0, TimeSpan.Zero);
    private static readonly string T = L.ToString("t", En);

    [Fact]
    public void App_keys_are_upper_case_normalized_names_and_pid_and_unknown_names_are_never_learnable()
    {
        Assert.Equal("CHROME.EXE", AppKey.Of(" Chrome.exe\u0001 "));
        Assert.All(new[] { "PID 4", "pid 4294967295", "Unknown application", "" }, n => Assert.False(FirstNetworkUse.IsLearnable(n)));
        Assert.All(new[] { "PID 4.exe", "PID.exe", new string('a', 256) }, n => Assert.True(FirstNetworkUse.IsLearnable(n)));
        Assert.Equal(["chrome.exe"], FirstNetworkUse.NormalizeIgnored(["chrome.exe", "CHROME.EXE", "PID 4", "", " chrome.exe "]));
        Assert.Equal(256, FirstNetworkUse.NormalizeIgnored(Enumerable.Range(0, 300).Select(i => $"app{i}.exe")).Count);
        Assert.Empty(FirstNetworkUse.NormalizeIgnored(null));
    }

    [Fact]
    public void Loopback_is_127_dot_or_the_ipv6_loopback()
    {
        Assert.All(new[] { "127.0.0.1", "127.255.0.3", "::1" }, a => Assert.True(FirstNetworkUse.IsLoopback(a)));
        Assert.All(new[] { "192.168.1.5", "169.254.1.1", "8.8.8.8", "::2", "2001:db8::1", "" }, a => Assert.False(FirstNetworkUse.IsLoopback(a)));
    }

    [Fact]
    public void Learning_ends_at_local_midnight_that_begins_the_third_day_after_the_start_day()
    {
        var plus1 = TimeZoneInfo.CreateCustomTimeZone("plus1", TimeSpan.FromHours(1), "plus1", "plus1");
        var thursday = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal(thursday, FirstNetworkUse.LearningEnd(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)), plus1));
        Assert.Equal(thursday, FirstNetworkUse.LearningEnd(new DateTimeOffset(2026, 11, 2, 23, 50, 0, TimeSpan.FromHours(1)), plus1));
        Assert.Equal(TimeSpan.Zero, FirstNetworkUse.LearningEnd(thursday, plus1).Offset);

        // Spring: a custom zone that moves 02:00 to 03:00 on Tuesday 2026-11-03: the end is Thursday 00:00 at the post-change offset.
        var spring = CustomZone("spring", new DateTime(2026, 11, 3, 2, 0, 0), new DateTime(2026, 12, 30, 3, 0, 0));
        var springEnd = FirstNetworkUse.LearningEnd(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), spring);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.FromHours(1)).UtcTicks, springEnd.UtcTicks);

        // Gap: a custom zone whose clocks jump 00:00 to 01:00 on Thursday 2026-11-05: the end is Thursday 01:00 local.
        var gap = CustomZone("gap", new DateTime(2026, 11, 5, 0, 0, 0), new DateTime(2026, 12, 30, 3, 0, 0));
        Assert.True(gap.IsInvalidTime(new DateTime(2026, 11, 5, 0, 0, 0)));
        var gapEnd = FirstNetworkUse.LearningEnd(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), gap);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.FromHours(1)).UtcTicks, gapEnd.UtcTicks);
        Assert.Equal(new DateTime(2026, 11, 5, 1, 0, 0), TimeZoneInfo.ConvertTime(gapEnd, gap).DateTime);

        // The returned value's UtcTicks are the same whichever zone later converts it.
        Assert.Equal(springEnd.UtcTicks, TimeZoneInfo.ConvertTime(springEnd, plus1).UtcTicks);
        Assert.Equal(TimeSpan.Zero, springEnd.Offset);
    }

    private static TimeZoneInfo CustomZone(string id, DateTime springForward, DateTime fallBack)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1).Add(springForward.TimeOfDay), springForward.Month, springForward.Day),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1).Add(fallBack.TimeOfDay), fallBack.Month, fallBack.Day));
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.Zero, id, id, id + " dst", [rule]);
    }

    [Fact]
    public void Balloon_text_names_one_app_lists_up_to_three_and_counts_the_rest_within_250_characters()
    {
        string B(int count, params string[] names) => FirstNetworkUse.BalloonText(new(L, names, count), Utc, En);
        Assert.Equal($"a.exe used the network for the first time at {T}. Open Alerts to review.", B(1, "a.exe"));
        Assert.Equal($"2 apps used the network for the first time from {T}: a.exe and b.exe. Open Alerts to review.", B(2, "a.exe", "b.exe"));
        Assert.Equal($"3 apps used the network for the first time from {T}: a.exe, b.exe and c.exe. Open Alerts to review.", B(3, "a.exe", "b.exe", "c.exe"));
        Assert.Equal($"4 apps used the network for the first time from {T}: a.exe, b.exe, c.exe and 1 more. Open Alerts to review.", B(4, "a.exe", "b.exe", "c.exe", "d.exe"));
        string n256 = new string('n', 256); Assert.Contains(new string('n', 39) + "… used", B(1, n256));
        Assert.Contains(new string('p', 38) + "\U0001F600… used", B(1, new string('p', 38) + "\U0001F600tail.exe"));   // 39 text elements; the pair stays whole
        Assert.Equal(225, B(8192, n256, n256, n256).Length); Assert.Equal(229, B(999_999, n256, n256, n256).Length);
    }

    [Fact]
    public void Summary_and_limits_match_the_spec()
    {
        Assert.Equal("a.exe used the network for the first time", FirstNetworkUse.Summary("a.exe"));
        Assert.Equal("first-network-use", FirstNetworkUse.RuleId);
        Assert.Equal((8192, 256, 1024, 64, 65536, 1024, 400),
            (FirstNetworkUse.MaximumKnown, FirstNetworkUse.MaximumIgnored, FirstNetworkUse.MaximumUses, FirstNetworkUse.MaximumGroupNames,
             FirstNetworkUse.MaximumSeedNames, FirstNetworkUse.MaximumChanges, FirstNetworkUse.ForgetDays));
        Assert.Equal(TimeSpan.FromSeconds(60), FirstNetworkUse.GroupWindow);
    }
}
