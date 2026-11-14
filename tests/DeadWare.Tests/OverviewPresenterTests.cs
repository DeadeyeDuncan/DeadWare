using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class OverviewPresenterTests
{
    public OverviewPresenterTests() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

    static StorageDrive Drive(string id, int index, bool removable = false) => new(id, id, "Model " + id, index, "NVMe", 2_000_000_000_000, removable, []);

    [Fact] public void Readings_carry_their_unit_and_a_dash_when_missing()
    {
        Assert.Equal("41.5 °C", OverviewPresenter.Value("cputemp", 41.5));
        Assert.Equal("12.0 %", OverviewPresenter.Value("gpu", 12));
        Assert.Equal("1.5 MB/s", OverviewPresenter.Value("network", 1_500_000));
        Assert.Equal("—", OverviewPresenter.Value("cpu", null));
        Assert.Equal("41.5°C", OverviewPresenter.Statistic("gputemp", 41.5));
        Assert.Equal("—", OverviewPresenter.Statistic("memory", double.NaN));
    }

    [Fact] public void Columns_and_rows_follow_the_width_and_the_grid_grows_into_spare_height()
    {
        Assert.Equal(1, OverviewPresenter.Columns(899));
        Assert.Equal(2, OverviewPresenter.Columns(900));
        Assert.Equal(3, OverviewPresenter.Columns(1500));
        Assert.Equal(4, OverviewPresenter.Rows(7, 2));
        Assert.Equal(3, OverviewPresenter.Rows(9, 3));
        Assert.Equal(1080, OverviewPresenter.GridHeight(7, 2, 800, 60));
        Assert.Equal(1340, OverviewPresenter.GridHeight(7, 2, 1400.6, 60));
    }

    [Fact] public void Device_lines_name_the_device_and_say_why_a_reading_is_missing()
    {
        Assert.Equal("Discovering hardware…", OverviewPresenter.DeviceSubtitle("cpu", false, "", "", 0, false, false));
        Assert.Equal("Ryzen · 16 cores", OverviewPresenter.DeviceSubtitle("cpu", true, "Ryzen", "RTX", 16, true, false));
        Assert.Equal("Ryzen · sensor unavailable", OverviewPresenter.DeviceSubtitle("cputemp", true, "Ryzen", "RTX", 16, false, false));
        Assert.Equal("RTX · stale", OverviewPresenter.DeviceSubtitle("gpu", true, "Ryzen", "RTX", 0, false, true));
    }

    [Fact] public void Fan_and_network_lines_describe_the_trace_pattern()
    {
        Assert.Equal("GPU fan · 1200 RPM · dotted · left RPM", OverviewPresenter.Rpm("GPU fan", false, 1200.4, OverviewPresenter.Pattern(null, dotted: true)));
        Assert.Equal("CPU pump · stale · dashed · left RPM", OverviewPresenter.Rpm("CPU pump", true, 900, OverviewPresenter.Pattern("dashed", dotted: true)));
        Assert.Equal("CPU pump · unavailable · solid · left RPM", OverviewPresenter.Rpm("CPU pump", false, null, "solid"));
        Assert.Equal("GPU fan · 845 RPM", OverviewPresenter.Rpm("GPU fan", false, 845));
        Assert.Equal("Solid: download    ·    Dashed: upload    ·    Shared scale", OverviewPresenter.NetworkSubtitle("default", null));
    }

    [Fact] public void Storage_lines_cover_discovery_no_drives_and_a_drive_card()
    {
        Assert.Equal("Discovering drives…", OverviewPresenter.StorageSubtitle(false, 0, null));
        Assert.Equal("No drives detected", OverviewPresenter.StorageSubtitle(true, 0, null));
        Assert.Equal("Model a · 2 drives", OverviewPresenter.StorageSubtitle(true, 2, "Model a"));
        Assert.Equal("NVMe · 2.0 TB · removable · ↑ 3.0 MB/s write", OverviewPresenter.DriveSubtitle(Drive("a", 0, true), 3_000_000));
        Assert.Equal("Drive not detected", OverviewPresenter.DriveSubtitle(null, null));
    }

    [Fact] public void The_named_drive_changes_only_after_another_leads_for_five_polls()
    {
        var drives = new[] { Drive("a", 0), Drive("b", 1) };
        var rates = new Dictionary<string, double> { ["a"] = 10, ["b"] = 0 };
        var busiest = new BusiestDrive();
        Assert.Equal("a", busiest.Pick(drives, d => rates[d.Id]).Id);
        rates["b"] = 50;
        for (int i = 0; i < OverviewPresenter.BusiestHold - 1; i++) Assert.Equal("a", busiest.Pick(drives, d => rates[d.Id]).Id);
        Assert.Equal("b", busiest.Pick(drives, d => rates[d.Id]).Id);
    }

    [Fact] public void A_repeated_drive_id_reuses_each_kept_card_once()
    {
        Assert.Equal(new int?[] { 1, 0, 2, null }, OverviewPresenter.Reuse(["x", "y", "x"], ["y", "x", "x", "x"]));
    }

    [Fact] public void Status_puts_discovery_and_staleness_first()
    {
        Assert.Equal("Discovering hardware and starting the shared collector…\nhistory", OverviewPresenter.Status(false, false, "", "", "history", "", false, 0));
        Assert.Equal("STALE — the collector has not published a recent sample. hw\nnet\nhistory\nsaved · Per-core CPU usage unavailable.",
            OverviewPresenter.Status(true, true, "hw", "net", "history", "saved", true, 0));
    }
}
