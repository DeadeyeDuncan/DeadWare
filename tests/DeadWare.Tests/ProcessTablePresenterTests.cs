using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ProcessTablePresenterTests
{
    // Decimals are written in the user's locale; the expected texts here are for the invariant one.
    public ProcessTablePresenterTests() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

    static ProcessSample P(int pid, string name, double? cpu, long memory) => new(pid, 1, name, 1, cpu, memory, null, null, default, null);

    static readonly ProcessSample[] Sample =
    [
        P(10, "chrome.exe", 5, 1_000_000), P(11, "chrome.exe", 7.5, 2_000_000), P(20, "notepad.exe", 1, 500_000), P(30, "zed.exe", 30, 9_000_000),
    ];

    [Fact] public void Headers_are_the_name_then_the_visible_columns()
    {
        Assert.Equal(["NAME", "CPU", "MEMORY"], ProcessTablePresenter.Headers(["cpu", "memory"]));
    }

    [Fact] public void Processes_of_one_app_share_a_row_with_their_count_and_totals()
    {
        var rows = ProcessTablePresenter.Rows(Sample, ["cpu", "memory"], "name", false, "");
        Assert.Equal(3, rows.Count);
        var chrome = rows[0];
        Assert.Equal(["chrome.exe (2)", "12.5 %", "3.0 MB"], chrome.Cells);
        Assert.Equal("2 processes", chrome.Tip);
        Assert.Equal(["notepad.exe", "1 %", "500.0 KB"], rows[1].Cells);
        Assert.Equal("PID 20", rows[1].Tip);
    }

    [Fact] public void Rows_default_to_busiest_cpu_first_and_to_name_when_cpu_is_not_shown()
    {
        Assert.Equal(["zed.exe", "chrome.exe (2)", "notepad.exe"], ProcessTablePresenter.Rows(Sample, ["cpu"], null, false, "").Select(r => r.Cells[0]));
        Assert.Equal(["chrome.exe (2)", "notepad.exe", "zed.exe"], ProcessTablePresenter.Rows(Sample, ["memory"], null, true, "").Select(r => r.Cells[0]));
    }

    [Fact] public void A_sort_on_a_hidden_column_falls_back_to_the_default()
    {
        Assert.Equal("zed.exe", ProcessTablePresenter.Rows(Sample, ["cpu"], "memory", false, "")[0].Cells[0]);
    }

    [Fact] public void Rows_sort_by_the_chosen_column_and_direction()
    {
        Assert.Equal(["notepad.exe", "chrome.exe (2)", "zed.exe"], ProcessTablePresenter.Rows(Sample, ["cpu", "memory"], "memory", false, "").Select(r => r.Cells[0]));
        Assert.Equal(["zed.exe", "chrome.exe (2)", "notepad.exe"], ProcessTablePresenter.Rows(Sample, ["cpu", "memory"], "memory", true, "").Select(r => r.Cells[0]));
    }

    [Fact] public void Search_keeps_the_matching_apps()
    {
        Assert.Equal(["notepad.exe"], ProcessTablePresenter.Rows(Sample, ["cpu"], null, false, "note").Select(r => r.Cells[0]));
        Assert.Empty(ProcessTablePresenter.Rows(Sample, ["cpu"], null, false, "no such app"));
    }

    [Fact] public void An_unknown_value_is_a_dash()
    {
        var rows = ProcessTablePresenter.Rows([P(1, "idle.exe", null, 0)], ["cpu", "network", "gpuengine"], "name", false, "");
        Assert.Equal(["idle.exe", "—", "—", "—"], rows[0].Cells);
    }

    [Fact] public void Clicking_a_header_toggles_its_direction_or_starts_a_new_column_in_its_natural_direction()
    {
        Assert.Equal(("cpu", false), ProcessTablePresenter.Clicked("cpu", true, "cpu"));
        Assert.Equal(("cpu", true), ProcessTablePresenter.Clicked("cpu", false, "cpu"));
        Assert.Equal(("memory", true), ProcessTablePresenter.Clicked("cpu", true, "memory"));     // numbers start largest first
        Assert.Equal(("name", false), ProcessTablePresenter.Clicked("cpu", true, "name"));        // text starts A to Z
        Assert.Equal(("cpu", true), ProcessTablePresenter.Clicked(null, false, "cpu"));
    }

    [Theory]
    [InlineData("cpu", 9.04, "9 %")] [InlineData("gpu", 12.5, "12.5 %")] [InlineData("memory", 1_500_000, "1.5 MB")]
    [InlineData("gpumemory", 0, "0 B")] [InlineData("gpushared", 2_000_000, "2.0 MB")] [InlineData("network", 2000, "2.0 KB/s")] [InlineData("pid", 4321, "4321")]
    public void Values_are_formatted_by_column(string key, double value, string expected) => Assert.Equal(expected, ProcessTablePresenter.Format(key, value));
}
