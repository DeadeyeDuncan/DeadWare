using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class AlertRuleTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    private static AlertRule Rule(AlertKind kind, string target, double threshold, int minutes = 5, string id = "rule-1", bool enabled = true)
        => new(id, kind, target, threshold, minutes, enabled, Created);

    [Fact]
    public void Normalization_repairs_ranges_and_drops_what_it_cannot()
    {
        var cpu = AlertRules.Normalize(Rule(AlertKind.Metric, " CPU ", 90.4, 7))!;
        Assert.Equal(("cpu", 90.0, 5), (cpu.Target, cpu.Threshold, cpu.Minutes));
        Assert.Equal(150, AlertRules.Normalize(Rule(AlertKind.Metric, "gputemp", 200))!.Threshold);
        Assert.Equal(1, AlertRules.Normalize(Rule(AlertKind.Metric, "memory", -5))!.Threshold);
        Assert.Equal(1, AlertRules.Normalize(Rule(AlertKind.NetworkRate, "upload", 0.4, 60))!.Threshold);
        Assert.Equal(60, AlertRules.Normalize(Rule(AlertKind.NetworkRate, "upload", 0.4, 60))!.Minutes);
        var app = AlertRules.Normalize(Rule(AlertKind.ApplicationDaily, " chrome.exe ", 2e9, 15))!;
        Assert.Equal(("chrome.exe", 1), (app.Target, app.Minutes));
        var battery = AlertRules.Normalize(Rule(AlertKind.Battery, "ignored", 150, 60))!;
        Assert.Equal(("", 99.0, 1), (battery.Target, battery.Threshold, battery.Minutes));
        Assert.Equal(DateTimeOffset.UnixEpoch, AlertRules.Normalize(Rule(AlertKind.Battery, "", 20) with { Created = default })!.Created);
        Assert.Null(AlertRules.Normalize(Rule(AlertKind.Metric, "gpufan", 50)));
        Assert.Null(AlertRules.Normalize(Rule(AlertKind.NetworkRate, "cpu", 50)));
        Assert.Null(AlertRules.Normalize(Rule(AlertKind.ApplicationDaily, "   ", 50)));
        Assert.Null(AlertRules.Normalize(Rule(AlertKind.Metric, "cpu", double.NaN)));
        Assert.Null(AlertRules.Normalize(Rule(AlertKind.Metric, "cpu", 50, id: "bad id")));
        Assert.Null(AlertRules.Normalize(Rule((AlertKind)9, "cpu", 50)));
        Assert.Null(AlertRules.Normalize((AlertRule?)null));
    }

    [Fact]
    public void Lists_drop_invalid_rules_and_later_duplicates_and_cap_the_count()
    {
        var rules = new List<AlertRule?>
        {
            Rule(AlertKind.Metric, "cpu", 90, id: "a"), Rule(AlertKind.Metric, "gpu", 80, id: "a"), null,
            Rule(AlertKind.Metric, "nope", 1, id: "b"), Rule(AlertKind.Battery, "", 20, id: "c"),
        };
        var normalized = AlertRules.Normalize(rules);
        Assert.Equal(new[] { "a", "c" }, normalized.Select(r => r.Id));
        Assert.Equal("cpu", normalized[0].Target);
        Assert.Empty(AlertRules.Normalize((IEnumerable<AlertRule?>?)null));
        var many = Enumerable.Range(0, 70).Select(i => Rule(AlertKind.Metric, "cpu", 50, id: "r" + i));
        Assert.Equal(AlertRules.MaximumRules, AlertRules.Normalize(many).Count);
    }

    [Fact]
    public void Descriptions_thresholds_and_hysteresis_follow_the_rule_kind()
    {
        Assert.Equal("CPU temperature ≥ 85°C for 15 minutes", AlertRules.Describe(Rule(AlertKind.Metric, "cputemp", 85, 15)));
        Assert.Equal("RAM usage ≥ 90% for 1 minute", AlertRules.Describe(Rule(AlertKind.Metric, "memory", 90, 1)));
        Assert.Equal("Download ≥ 5.0 MB/s for 5 minutes", AlertRules.Describe(Rule(AlertKind.NetworkRate, "download", 5_000_000)));
        Assert.Equal("chrome.exe ≥ 2.0 GB in a day", AlertRules.Describe(Rule(AlertKind.ApplicationDaily, "chrome.exe", 2_000_000_000, 1)));
        Assert.Equal("Battery ≤ 20% on battery", AlertRules.Describe(Rule(AlertKind.Battery, "", 20, 1)));
        Assert.Equal(3, AlertRules.Hysteresis(Rule(AlertKind.Metric, "gputemp", 80)));
        Assert.Equal(5, AlertRules.Hysteresis(Rule(AlertKind.Metric, "cpu", 80)));
        Assert.Equal(5, AlertRules.Hysteresis(Rule(AlertKind.Battery, "", 20)));
        Assert.Equal(100, AlertRules.Hysteresis(Rule(AlertKind.NetworkRate, "upload", 1000)));
        Assert.Equal("—", AlertRules.FormatBytes(double.NaN));
        Assert.Equal("1.5 KB", AlertRules.FormatBytes(1500));
    }

    [Fact]
    public void Settings_keep_rules_and_the_notification_switch_and_default_to_none_and_on()
    {
        var fresh = new UserSettings();
        Assert.Empty(fresh.Alerts); Assert.True(fresh.AlertNotifications);
        var read = SettingsCodec.Read("""
            {"Alerts":[{"Id":"a","Kind":0,"Target":"CPU","Threshold":90.4,"Minutes":7,"Enabled":true,"Created":"2026-11-02T00:00:00+00:00"},
                       {"Id":"b","Kind":9,"Target":"cpu","Threshold":1,"Minutes":1,"Enabled":true,"Created":"2026-11-02T00:00:00+00:00"},
                       {"Id":"c","Kind":3,"Target":"","Threshold":20,"Minutes":1,"Enabled":false,"Created":"2026-11-02T00:00:00+00:00"}],
             "AlertNotifications":false}
            """).Settings;
        Assert.Equal(new[] { "a", "c" }, read.Alerts.Select(r => r.Id));
        Assert.Equal(("cpu", 90.0, 5, true), (read.Alerts[0].Target, read.Alerts[0].Threshold, read.Alerts[0].Minutes, read.Alerts[0].Enabled));
        Assert.False(read.Alerts[1].Enabled); Assert.False(read.AlertNotifications);
        var roundtrip = SettingsCodec.Read(SettingsCodec.Write(read)).Settings;
        Assert.Equal(read.Alerts, roundtrip.Alerts); Assert.False(roundtrip.AlertNotifications);
        Assert.Empty(SettingsCodec.Read("""{"Alerts":null}""").Settings.Alerts);
        Assert.True(SettingsCodec.Read("""{"BatteryRecording":false}""").Settings.AlertNotifications);
    }
}
