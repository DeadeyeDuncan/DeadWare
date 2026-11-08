using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HistoryClearTests
{
    [Fact]
    public void Clear_boundaries()
    {
        var t = new DateTimeOffset(2026, 11, 7, 10, 31, 45, TimeSpan.Zero);
        var older = HistoryClear.Create("Chrome.exe", allTime: false, t);
        Assert.Equal(("CHROME.EXE", new DateTimeOffset(2026, 10, 31, 10, 31, 0, TimeSpan.Zero), t, false), (older.AppKey, older.Before, older.Requested, older.AllTime)); Assert.Equal(older.Before, older.AlertCutoff);
        var all = HistoryClear.Create(null, allTime: true, t); Assert.Null(all.AppKey); Assert.Equal(new DateTimeOffset(2026, 11, 7, 10, 32, 0, TimeSpan.Zero), all.Before); Assert.True(all.AllTime); Assert.Equal(t, all.AlertCutoff);
        Assert.True(older.Covers("chrome.EXE", older.Before.AddMinutes(-1))); Assert.False(older.Covers("chrome.exe", older.Before)); Assert.False(older.Covers("other.exe", older.Before.AddMinutes(-1)));
    }
}
