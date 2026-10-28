using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class MemoryUsageTests
{
    [Theory]
    [InlineData(0, 16, 0)]
    [InlineData(8, 16, 50)]
    [InlineData(16, 16, 100)]
    [InlineData(double.MaxValue, double.MaxValue, 100)]
    public void Percentage_uses_valid_physical_memory_including_zero(double used, double total, double expected)
        => Assert.Equal(expected, MemoryUsage.Percentage(used, total));

    [Theory]
    [InlineData(null, 16d)]
    [InlineData(8d, null)]
    [InlineData(null, null)]
    [InlineData(0d, 0d)]
    [InlineData(8d, 0d)]
    [InlineData(8d, -1d)]
    [InlineData(-1d, 16d)]
    [InlineData(17d, 16d)]
    [InlineData(double.NaN, 16d)]
    [InlineData(8d, double.NaN)]
    [InlineData(double.PositiveInfinity, 16d)]
    [InlineData(8d, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 16d)]
    public void Invalid_or_missing_memory_is_unknown_instead_of_a_zero_or_clamped_percentage(double? used, double? total)
        => Assert.Null(MemoryUsage.Percentage(used, total));

    [Fact]
    public void Capacity_changes_use_each_samples_own_total()
    {
        Assert.Equal(50, MemoryUsage.Percentage(8, 16));
        Assert.Equal(25, MemoryUsage.Percentage(8, 32));
        Assert.Null(MemoryUsage.Percentage(8, null));
        Assert.Equal(50, MemoryUsage.Percentage(16, 32));
    }
}
