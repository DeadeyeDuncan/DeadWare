using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public sealed class UiTypographyTests
{
    [Fact]
    public void Range_choices_match_history_ranges()
    {
        Assert.Equal(HistoryRanges.Seconds,RangeSegments.Choices.Select(c=>c.Seconds));
        Assert.Equal(["1 min","5 min","15 min","1 h","3 h","6 h"],RangeSegments.Choices.Select(c=>c.Label));
    }
}
