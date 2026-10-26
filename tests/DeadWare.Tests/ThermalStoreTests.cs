using System.Globalization;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DeadWare.Tests;

public sealed class ThermalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DeadWare-store-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ThermalDevice Device = new("cpu-local", "CPU, \"test\"", "CPU", "setup-1", "typed-map");
    private readonly ITestOutputHelper output;
    private string Database => Path.Combine(directory, "history.db");
    public ThermalStoreTests(ITestOutputHelper output) { this.output = output; Directory.CreateDirectory(directory); }
    public void Dispose() => Directory.Delete(directory, true);
    private ThermalStore Store()
    {
        var store = new ThermalStore(Database); store.Initialize(); return store;
    }
    private ThermalWindow Window(int minute, double temperature = 60, int count = 60, string session = "run-1") =>
        new(Device, session, epoch.AddMinutes(minute), epoch.AddMinutes(minute).AddSeconds(59), minute * 60, 59, count,
            new(count, temperature, temperature, temperature), new(count, 50, 50, 50), new(count, 60, 60, 60),
            new(0, null, null, null), new(count, 0, 0, 0), true, "Comparable");
    private ThermalReference Reference(string id, int start = 0) => new(id, Device.Id, Device.ConfigurationId, "Reference " + id,
        epoch.AddDays(1).AddMinutes(start), epoch.AddMinutes(start), epoch.AddMinutes(start + 30),
        Enumerable.Range(start, 30).Select(i => Window(i)).ToArray());

    [Fact] public void ReopenPreservesValuesMissingMetricsAndZeroFan()
    {
        Store().Append([Window(0)], epoch.AddMinutes(1));
        var reopened = Store();
        var row = Assert.Single(reopened.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1)));
        Assert.Equal(60, row.Temperature.Average); Assert.Null(row.Clock.Average); Assert.Equal(0, row.FanRpm.Average);
        Assert.Equal(Device, Assert.Single(reopened.Devices()));
        Assert.True(reopened.StorageBytes > 0);
    }

    [Fact] public void DuplicateInsertDoesNotDoubleCountRollup()
    {
        var store = Store(); store.Append([Window(0)], epoch.AddMinutes(1)); store.Append([Window(0)], epoch.AddMinutes(1));
        Assert.Single(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1), detailedOnly: true));
        store.Append([], epoch.AddDays(31));
        Assert.Equal(60, Assert.Single(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1))).Temperature.Count);
    }

    [Fact] public void RollupsUseValidCountsAndRemainIneligible()
    {
        var store = Store(); store.Append([Window(0, 40, 10), Window(1, 80, 30)], epoch.AddMinutes(2));
        store.Append([], epoch.AddDays(31));
        var row = Assert.Single(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddHours(1)));
        Assert.Equal(70, row.Temperature.Average); Assert.Equal(40, row.Temperature.Minimum); Assert.Equal(80, row.Temperature.Maximum);
        Assert.Equal(40, row.Temperature.Count); Assert.False(row.Eligible); Assert.Equal(3600, row.ResolutionSeconds);
        Assert.Empty(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddHours(1), detailedOnly: true));
    }

    [Fact] public void SummariesPreserveGapsAndSeparateSessions()
    {
        var store = Store(); store.Append([Window(0), Window(3), Window(4, session: "run-2")], epoch.AddHours(1));
        store.Append([], epoch.AddDays(31));
        var rows = store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddHours(1));
        Assert.Equal(3, rows.Count); Assert.Equal(new[] { "run-1", "run-1", "run-2" }, rows.Select(x => x.SessionId));
    }

    [Fact] public void SummaryRetentionDoesNotExpireFrozenReference()
    {
        var store = Store(); store.Append([Window(0)], epoch.AddMinutes(1)); store.SaveReference(Reference("fixed"));
        store.Append([], epoch.AddDays(91));
        Assert.Empty(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(92)));
        Assert.Equal(30, store.ActiveReference(Device.Id, Device.ConfigurationId)!.Windows.Count);
    }

    [Fact] public void ReferenceReplacementRetainsOldCopyAndCanActivateIt()
    {
        var store = Store(); store.SaveReference(Reference("first")); store.SaveReference(Reference("second", 30));
        Assert.Equal("second", store.ActiveReference(Device.Id, Device.ConfigurationId)!.Id);
        Assert.Equal(2, store.References(Device.Id, Device.ConfigurationId).Count);
        store.ActivateReference("first");
        Assert.Equal("first", Store().ActiveReference(Device.Id, Device.ConfigurationId)!.Id);
        Assert.Throws<InvalidOperationException>(() => store.SaveReference(Reference("first") with { Label = "rewrite" }));
    }

    [Fact] public void ReferencesAreBoundedAndRejectMismatchedDevice()
    {
        var store = Store();
        for (int i = 0; i < 12; i++) store.SaveReference(Reference("ref-" + i, i));
        Assert.Equal(10, store.References(Device.Id, Device.ConfigurationId).Count);
        Assert.Throws<ArgumentException>(() => store.SaveReference(Reference("wrong") with { DeviceId = "other" }));
    }

    [Fact] public void ConfigurationSeparatesHistoryAndReference()
    {
        var store = Store(); store.Append([Window(0), Window(1) with { Device = Device with { ConfigurationId = "setup-2" } }], epoch.AddMinutes(2));
        store.SaveReference(Reference("fixed"));
        Assert.Single(store.Read(Device.Id, "setup-1", epoch, epoch.AddDays(1)));
        Assert.Single(store.Read(Device.Id, "setup-2", epoch, epoch.AddDays(1)));
        Assert.Null(store.ActiveReference(Device.Id, "setup-2"));
    }

    [Fact] public void QueryBoundsWithoutConnectingFragmentedSamples()
    {
        var store = Store(); store.Append(Enumerable.Range(0, 200).Select(i => Window(i * 3)).ToArray(), epoch.AddDays(1));
        var rows = store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1), maxPoints: 20);
        Assert.Equal(20, rows.Count); Assert.All(rows, x => Assert.Equal(59, x.DurationSeconds));
        Assert.Equal(epoch.AddMinutes(597), rows[^1].Start);
    }

    [Fact] public void QueryAggregatesContiguousRowsAndNeverDuplicatesDetailedAndSummary()
    {
        var store = Store(); store.Append(Enumerable.Range(0, 120).Select(i => Window(i)).ToArray(), epoch.AddHours(2));
        var rows = store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddHours(2), maxPoints: 12);
        Assert.True(rows.Count <= 12); Assert.Equal(120 * 60, rows.Sum(x => x.Temperature.Count));
    }

    [Fact] public void WarningAcknowledgmentAndCooldownEvidenceSurviveReopen()
    {
        var store = Store(); store.AddWarning(new("warning", Device.Id, Device.ConfigurationId, "ref", epoch, "Hotter", 8));
        Assert.False(Assert.Single(store.Warnings(Device.Id, Device.ConfigurationId)).Acknowledged);
        store.AcknowledgeWarning("warning");
        Assert.True(Assert.Single(Store().Warnings(Device.Id, Device.ConfigurationId)).Acknowledged);
    }

    [Fact] public void CsvUsesInvariantNumbersEscapesNamesAndDoesNotOverwrite()
    {
        var store = Store(); store.Append([Window(0, 60.25)], epoch.AddMinutes(1));
        string output = Path.Combine(directory, "history.csv"); var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(1, store.ExportCsv(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1), output));
        }
        finally { CultureInfo.CurrentCulture = previous; }
        string csv = File.ReadAllText(output);
        Assert.Contains("60.25", csv); Assert.Contains("\"CPU, \"\"test\"\"\"", csv);
        Assert.Contains("resolution_seconds", csv); Assert.Contains("quality", csv);
        Assert.Throws<IOException>(() => store.ExportCsv(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1), output));
    }

    [Fact] public void CorruptDatabaseIsNotReplaced()
    {
        File.WriteAllText(Database, "preserve broken history");
        Assert.Throws<SqliteException>(() => new ThermalStore(Database).Initialize());
        Assert.Equal("preserve broken history", File.ReadAllText(Database));
    }

    [Fact] public void LockedWriterFailsWithinBoundedTimeoutAndExistingRowsSurvive()
    {
        var store = Store(); store.Append([Window(0)], epoch.AddMinutes(1));
        using var locked = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); locked.Open();
        using var transaction = locked.BeginTransaction();
        using (var command = locked.CreateCommand()) { command.Transaction = transaction; command.CommandText = "UPDATE devices SET payload=payload"; command.ExecuteNonQuery(); }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<SqliteException>(() => store.Append([Window(1)], epoch.AddMinutes(2)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4));
        transaction.Rollback();
        Assert.Single(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1)));
    }

    [Fact] public void DatabaseUsesWalAndPageCap()
    {
        Store();
        using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
        command.CommandText = "SELECT sqlite_version()";
        string version = (string)command.ExecuteScalar()!;
        Assert.True(Version.Parse(version) >= new Version(3, 50, 2), version);
        output.WriteLine("Native SQLite version: " + version);
        // Cap is reapplied to every writer connection (SQLite does not persist this pragma).
        Assert.Equal(256L * 1024 * 1024, ThermalStore.MaximumDatabaseBytes);
    }

    [Fact] public void FullDatabaseFailsAtomicallyAndKeepsPreviousHistory()
    {
        var store = new ThermalStore(Database, 128 * 1024); store.Initialize();
        store.Append([Window(0)], epoch.AddMinutes(1));
        var huge = Window(1) with { Device = Device with { SensorMap = new string('x', 256 * 1024) } };
        var failure = Assert.Throws<SqliteException>(() => store.Append([huge], epoch.AddMinutes(2)));
        Assert.Equal(13, failure.SqliteErrorCode); // SQLITE_FULL, exercised with the same cap used in production.
        Assert.Equal("typed-map", Assert.Single(store.Devices()).SensorMap);
        Assert.Single(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1)));
    }

    [Fact] public void OneDayOfHistoryDoesNotRepeatLargeSensorMapInEveryRow()
    {
        var store = Store(); var device = Device with { SensorMap = new string('s', 4096) };
        var windows = Enumerable.Range(0, 1440).Select(i => Window(i) with { Device = device }).ToArray();
        store.Append(windows, epoch.AddDays(1));
        output.WriteLine($"1,440 minute windows with a 4-KiB sensor map: {store.StorageBytes:N0} bytes (including journals).");
        Assert.True(store.StorageBytes < 4 * 1024 * 1024, $"One device-day used {store.StorageBytes:N0} bytes.");
        var row = Assert.Single(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddMinutes(1), detailedOnly: true));
        Assert.Equal(device, row.Device);
        store.Append([], epoch.AddDays(31));
        Assert.All(store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1)), row => Assert.Equal(device, row.Device));
    }

    [Fact] public void FrozenReferencesCompactRepeatedProvenanceAndRoundTripEveryWindow()
    {
        var store = Store(); var device = Device with { SensorMap = new string('s', 4096) };
        var windows = Enumerable.Range(0, 1440).Select(i => Window(i, 60 + i % 5) with { Device = device }).ToArray();
        for (int i = 0; i < 3; i++) store.SaveReference(new("large-" + i, Device.Id, Device.ConfigurationId, "One day", epoch.AddDays(2).AddMinutes(i), epoch, epoch.AddDays(1), windows));
        Assert.True(store.StorageBytes < 4 * 1024 * 1024, $"Three one-day references used {store.StorageBytes:N0} bytes.");
        var restored = Store().References(Device.Id, Device.ConfigurationId);
        Assert.Equal(3, restored.Count);
        foreach (var reference in restored)
        {
            Assert.Equal(1440, reference.Windows.Count);
            Assert.All(reference.Windows, row => Assert.Equal(device, row.Device));
            Assert.Equal(windows.Select(row => row.Temperature.Average), reference.Windows.Select(row => row.Temperature.Average));
        }
        output.WriteLine($"Three 1,440-window frozen references, 4-KiB metadata: {store.StorageBytes:N0} bytes.");
    }

    [Fact] public void RetentionBoundaryDoesNotHideAnHourWhenMinuteWindowsStraddleIt()
    {
        var store = Store();
        var rows = Enumerable.Range(0, 120).Select(i => Window(i) with
        { Start = Window(i).Start.AddSeconds(30), End = Window(i).End.AddSeconds(30), StartMonotonic = i * 60 + 30 }).ToArray();
        store.Append(rows, epoch.AddHours(3));
        store.Append([], epoch.AddDays(30).AddHours(1));
        var retained = store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddHours(3));
        Assert.Equal(120 * 60, retained.Sum(row => row.Temperature.Count));
        Assert.Equal(60, retained.Count(row => row.ResolutionSeconds == 60));
        Assert.Single(retained, row => row.ResolutionSeconds == 3600);
    }

    [Fact] public void ReplayedExpiredMinuteCannotDuplicateItsHourlySummary()
    {
        var store = Store(); store.Append([Window(0)], epoch.AddMinutes(1));
        store.Append([], epoch.AddDays(31));
        store.Append([Window(0)], epoch.AddDays(31));
        Assert.Equal(60, store.Read(Device.Id, Device.ConfigurationId, epoch, epoch.AddDays(1)).Sum(row => row.Temperature.Count));
    }

    [Fact] public void ReferenceHeadersReturnMetadataWithoutWindowPayload()
    {
        var store = Store(); var reference = Reference("header"); store.SaveReference(reference);
        var header = Assert.Single(store.ReferenceHeaders(Device.Id, Device.ConfigurationId));
        Assert.Equal(reference.Id, header.Id); Assert.Equal(reference.Label, header.Label);
        Assert.Equal(reference.Created, header.Created); Assert.Equal(reference.Start, header.Start); Assert.Equal(reference.End, header.End);
        Assert.Equal(reference.DeviceId, header.DeviceId); Assert.Equal(reference.ConfigurationId, header.ConfigurationId);
        Assert.Empty(header.Windows);
        Assert.Equal(30, store.ActiveReference(Device.Id, Device.ConfigurationId)!.Windows.Count);
        // A header query must not decode the large comparison payload.
        using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "UPDATE reference_periods SET payload='broken-comparison-payload'"; command.ExecuteNonQuery();
        Assert.Equal("header", Assert.Single(store.ReferenceHeaders(Device.Id, Device.ConfigurationId)).Id);
    }

    [Fact] public void VersionOneReferenceHeadersAreBackfilledTransactionally()
    {
        var store = Store(); store.SaveReference(Reference("old"));
        using (var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "ALTER TABLE reference_periods DROP COLUMN header; PRAGMA user_version=1;"; command.ExecuteNonQuery();
        }
        var reopened = Store();
        Assert.Equal("old", Assert.Single(reopened.ReferenceHeaders(Device.Id, Device.ConfigurationId)).Id);
        Assert.Empty(Assert.Single(reopened.ReferenceHeaders(Device.Id, Device.ConfigurationId)).Windows);
        Assert.Equal(30, reopened.ActiveReference(Device.Id, Device.ConfigurationId)!.Windows.Count);
    }
}
