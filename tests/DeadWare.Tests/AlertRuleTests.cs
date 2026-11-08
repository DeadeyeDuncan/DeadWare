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
        Assert.Equal("first-network-use", Assert.Single(fresh.Alerts).Id); Assert.True(fresh.AlertNotifications);
        var read = SettingsCodec.Read("""
            {"Alerts":[{"Id":"a","Kind":0,"Target":"CPU","Threshold":90.4,"Minutes":7,"Enabled":true,"Created":"2026-11-02T00:00:00+00:00"},
                       {"Id":"b","Kind":9,"Target":"cpu","Threshold":1,"Minutes":1,"Enabled":true,"Created":"2026-11-02T00:00:00+00:00"},
                       {"Id":"c","Kind":3,"Target":"","Threshold":20,"Minutes":1,"Enabled":false,"Created":"2026-11-02T00:00:00+00:00"}],
             "AlertNotifications":false}
            """).Settings;
        Assert.Equal(new[] { "a", "c", "first-network-use" }, read.Alerts.Select(r => r.Id));
        Assert.Equal(("cpu", 90.0, 5, true), (read.Alerts[0].Target, read.Alerts[0].Threshold, read.Alerts[0].Minutes, read.Alerts[0].Enabled));
        Assert.False(read.Alerts[1].Enabled); Assert.False(read.AlertNotifications);
        var roundtrip = SettingsCodec.Read(SettingsCodec.Write(read)).Settings;
        Assert.Equal(read.Alerts, roundtrip.Alerts); Assert.False(roundtrip.AlertNotifications);
        Assert.Equal(AlertKind.FirstNetworkUse, Assert.Single(SettingsCodec.Read("""{"Alerts":null}""").Settings.Alerts).Kind);
        Assert.True(SettingsCodec.Read("""{"BatteryRecording":false}""").Settings.AlertNotifications);
    }

    [Fact]
    public void First_network_use_rules_normalize_to_one_rule_with_no_target_and_a_fixed_description()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var r = AlertRules.Normalize(new AlertRule("x", AlertKind.FirstNetworkUse, "chrome.exe", 500, 15, false, now))!;
        Assert.Equal(("", 1d, 1, false), (r.Target, r.Threshold, r.Minutes, r.Enabled));
        Assert.Equal(["a", "b"], AlertRules.Normalize([new AlertRule("a", AlertKind.FirstNetworkUse, "", 1, 1, true, now), new("b", AlertKind.Metric, "cpu", 90, 5, true, now), new("c", AlertKind.FirstNetworkUse, "", 1, 1, true, now)]).Select(x => x.Id));
        Assert.Equal("First network use", AlertRules.TargetName(AlertKind.FirstNetworkUse, "")); Assert.Equal("An app uses the network for the first time", AlertRules.Describe(r));
        Assert.Equal(new AlertRule("first-network-use", AlertKind.FirstNetworkUse, "", 1, 1, true, now), AlertRules.DefaultFirstNetworkUse(now)); Assert.Equal(4, (int)AlertKind.FirstNetworkUse);
    }

    private static string Rules(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i =>
        $"{{\"Id\":\"r{i}\",\"Kind\":0,\"Target\":\"cpu\",\"Threshold\":90,\"Minutes\":5,\"Enabled\":true,\"Created\":\"2026-11-02T00:00:00+00:00\"}}")) + "]";

    [Fact]
    public void Settings_add_the_first_network_use_rule_once_and_normalize_the_dont_alert_list()
    {
        var fresh = new UserSettings(); Assert.Equal("first-network-use", Assert.Single(fresh.Alerts).Id); Assert.True(fresh.FirstUseRuleAdded);
        var s63 = SettingsCodec.Read($"{{\"Alerts\":{Rules(63)}}}").Settings; Assert.Equal(64, s63.Alerts.Count); Assert.Equal(AlertKind.FirstNetworkUse, s63.Alerts[^1].Kind); Assert.True(s63.FirstUseRuleAdded);
        var s64 = SettingsCodec.Read($"{{\"Alerts\":{Rules(64)}}}").Settings; Assert.Equal(64, s64.Alerts.Count); Assert.DoesNotContain(s64.Alerts, a => a.Kind == AlertKind.FirstNetworkUse); Assert.True(s64.FirstUseRuleAdded);
        Assert.Empty(SettingsCodec.Read($"{{\"Alerts\":{Rules(0)},\"FirstUseRuleAdded\":true}}").Settings.Alerts);
        var mine = SettingsCodec.Read("{\"Alerts\":[{\"Id\":\"mine\",\"Kind\":4,\"Target\":\"\",\"Threshold\":1,\"Minutes\":1,\"Enabled\":false,\"Created\":\"2026-11-02T00:00:00+00:00\"}]}").Settings;
        Assert.Equal("mine", Assert.Single(mine.Alerts).Id);
        foreach (var json in new[] { "{}", "{\"FirstUseIgnoredApps\":null}", "{\"FirstUseIgnoredApps\":[\"PID 4\",\"a.exe\",\"A.EXE\"]}" }) Assert.Null(SettingsCodec.Read(json).Error);
        Assert.Equal(["a.exe"], SettingsCodec.Read("{\"FirstUseIgnoredApps\":[\"PID 4\",\"a.exe\",\"A.EXE\"]}").Settings.FirstUseIgnoredApps);
        var s = new UserSettings { FirstUseIgnoredApps = ["a.exe"] }; var back = SettingsCodec.Read(SettingsCodec.Write(s)).Settings;
        Assert.Equal(s.Alerts, back.Alerts); Assert.Equal(["a.exe"], back.FirstUseIgnoredApps); Assert.True(back.FirstUseRuleAdded);
    }
}
