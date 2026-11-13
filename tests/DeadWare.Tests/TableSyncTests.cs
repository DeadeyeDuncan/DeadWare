using System.Collections.ObjectModel;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class TableSyncTests
{
    // Decimals are written in the user's locale; the expected texts here are for the invariant one.
    public TableSyncTests() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

    static TableRow Row(string key, params string[] cells) => new(key, cells);

    static (ObservableCollection<TableRow> Rows, Func<int> Changes) Watched(params TableRow[] rows)
    {
        var collection = new ObservableCollection<TableRow>(rows);
        int changes = 0;
        collection.CollectionChanged += (_, _) => changes++;
        return (collection, () => changes);
    }

    [Fact] public void Rows_with_the_same_key_cells_and_tip_are_equal()
    {
        Assert.Equal(Row("a", "1", "2"), Row("a", "1", "2"));
        Assert.NotEqual(Row("a", "1", "2"), Row("a", "1", "3"));
        Assert.NotEqual(Row("a", "1"), Row("b", "1"));
        Assert.NotEqual(Row("a", "1"), Row("a", "1") with { Tip = "why" });
        Assert.Equal(Row("a", "1").GetHashCode(), Row("a", "1").GetHashCode());
    }

    [Fact] public void Unchanged_rows_are_not_written()
    {
        var (rows, changes) = Watched(Row("a", "1"), Row("b", "2"));
        Assert.Equal(0, TableSync.Apply(rows, [Row("a", "1"), Row("b", "2")]));
        Assert.Equal(0, changes());
    }

    [Fact] public void Only_the_changed_row_is_replaced()
    {
        var (rows, changes) = Watched(Row("a", "1"), Row("b", "2"), Row("c", "3"));
        Assert.Equal(1, TableSync.Apply(rows, [Row("a", "1"), Row("b", "9"), Row("c", "3")]));
        Assert.Equal(1, changes());
        Assert.Equal(Row("b", "9"), rows[1]);
    }

    [Fact] public void A_shorter_list_drops_the_tail_and_a_longer_one_appends()
    {
        var (rows, _) = Watched(Row("a", "1"), Row("b", "2"), Row("c", "3"));
        Assert.Equal(2, TableSync.Apply(rows, [Row("a", "1")]));
        Assert.Equal([Row("a", "1")], rows);
        Assert.Equal(2, TableSync.Apply(rows, [Row("a", "1"), Row("x", "7"), Row("y", "8")]));
        Assert.Equal([Row("a", "1"), Row("x", "7"), Row("y", "8")], rows);
    }

    [Fact] public void An_empty_source_clears_and_an_empty_target_fills()
    {
        var (rows, _) = Watched(Row("a", "1"));
        TableSync.Apply(rows, []);
        Assert.Empty(rows);
        TableSync.Apply(rows, [Row("b", "2")]);
        Assert.Equal([Row("b", "2")], rows);
    }

    [Theory]
    [InlineData(null, "", "—")] [InlineData(double.NaN, "", "—")] [InlineData(12.34, " °C", "12.3 °C")] [InlineData(0d, "", "0.0")]
    public void Numbers_are_written_to_one_decimal_with_their_unit(double? value, string unit, string expected) => Assert.Equal(expected, TableSync.Number(value, unit));
}
