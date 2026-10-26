using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class ThermalAnalysisTests
{
    [Fact]
    public void Ambiguous_identity_is_recorded_but_never_becomes_reference_eligible()
    {
        var device=Cpu with {ComparisonAllowed=false,IdentityNote="Physical identity is ambiguous"};
        var rows=Feed(new ThermalAggregator(),0,1200,i=>Sample(i,device:device));
        Assert.NotEmpty(rows);Assert.All(rows,w=>Assert.False(w.Eligible));
        Assert.All(rows,w=>Assert.Contains("ambiguous",w.Quality));
        Assert.Equal("Identity not verified",CoolingAnalyzer.Assess(rows,Reference(),Epoch.AddMinutes(20)).State);
    }
    private static readonly DateTimeOffset Epoch = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly ThermalDevice Cpu = new("cpu-a", "Test CPU", "CPU", "setup-a", "temperature:package;load:total;power:package");
    private static ThermalSample Sample(double seconds, double? temperature = 60, double? load = 50,
        double? power = 80, double? fan = 0, string session = "session-a", ThermalDevice? device = null,
        double? utcSeconds = null, bool throttled = false) =>
        new(device ?? Cpu, session, Epoch.AddSeconds(utcSeconds ?? seconds), seconds,
            temperature, load, power, 4500, fan, throttled);

    private static List<ThermalWindow> Feed(ThermalAggregator aggregator, int from, int to,
        Func<int, ThermalSample>? source = null)
    {
        var result = new List<ThermalWindow>();
        for (int i = from; i <= to; i++) result.AddRange(aggregator.Add(source?.Invoke(i) ?? Sample(i)));
        return result;
    }

    private static MetricAggregate Metric(double? value, int count = 60) =>
        new(value.HasValue ? count : 0, value, value, value);

    private static ThermalWindow Window(int minute, double temperature = 60, double power = 80,
        double load = 50, bool eligible = true, ThermalDevice? device = null, string session = "session-a") =>
        new(device ?? Cpu, session, Epoch.AddMinutes(minute), Epoch.AddMinutes(minute).AddSeconds(59),
            minute * 60, 59, 60, Metric(temperature), Metric(load), Metric(power), Metric(4500), Metric(0),
            eligible, eligible ? "Stable" : "Missing required readings");

    private static ThermalReference Reference(int count = 30, double temperature = 60) =>
        new("reference-a", Cpu.Id, Cpu.ConfigurationId, "Known setup", Epoch.AddHours(1), Epoch,
            Epoch.AddMinutes(count), Enumerable.Range(0, count).Select(m => Window(m, temperature)).ToArray());

    private static ThermalWindow[] Recent(double temperature = 60, double power = 80, double load = 50) =>
        Enumerable.Range(100, 10).Select(m => Window(m, temperature, power, load)).ToArray();

    [Fact]
    public void MinuteWindowsPreserveRealCountsAndExcludeFirstFiveMinutes()
    {
        var aggregator = new ThermalAggregator();
        var windows = Feed(aggregator, 0, 420);
        Assert.Equal(7, windows.Count);
        Assert.All(windows.Take(5), window => Assert.False(window.Eligible));
        Assert.All(windows.Skip(5), window => Assert.True(window.Eligible));
        Assert.Equal(60, windows[5].SampleCount);
        Assert.Equal(59, windows[5].DurationSeconds);
        Assert.Equal(Epoch.AddMinutes(5), windows[5].Start);
        Assert.Equal(60, windows[5].Temperature.Average);
        Assert.Equal(0, windows[5].FanRpm.Average);
        Assert.Equal(60, windows[5].FanRpm.Count);
    }

    [Fact]
    public void MissingRequiredDataIsNeverAZeroAndDisqualifiesWholeWindow()
    {
        var aggregator = new ThermalAggregator();
        var windows = Feed(aggregator, 0, 420, i => i == 320 ? Sample(i, power: null) : Sample(i));
        Assert.False(windows[5].Eligible);
        Assert.Equal(59, windows[5].Power.Count);
        Assert.Equal(80, windows[5].Power.Average);
        Assert.Contains("Missing", windows[5].Quality, StringComparison.OrdinalIgnoreCase);
        Assert.False(windows[6].Eligible);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteInputsDoNotContaminateAggregates(double invalid)
    {
        var aggregator = new ThermalAggregator();
        var windows = Feed(aggregator, 0, 360, i => i == 320 ? Sample(i, temperature: invalid) : Sample(i));
        Assert.Equal(59, windows[5].Temperature.Count);
        Assert.Equal(60, windows[5].Temperature.Average);
        Assert.False(windows[5].Eligible);
    }

    [Fact]
    public void MissingOptionalFanAndClockDoNotPreventComparison()
    {
        var aggregator = new ThermalAggregator();
        var windows = Feed(aggregator, 0, 360, i => Sample(i, fan: null) with { Clock = null });
        Assert.True(windows[5].Eligible);
        Assert.Null(windows[5].FanRpm.Average);
        Assert.Null(windows[5].Clock.Average);
    }

    [Theory]
    [InlineData(130, 50)]
    [InlineData(80, 90)]
    public void WorkloadChangesAcrossWindowBoundariesRestartWarmup(double power, double load)
    {
        var aggregator = new ThermalAggregator();
        var windows = Feed(aggregator, 0, 720, i => i >= 360 ? Sample(i, power: power, load: load) : Sample(i));
        Assert.True(windows[5].Eligible);
        Assert.All(windows.Where(w => w.StartMonotonic >= 360 && w.StartMonotonic < 660), w => Assert.False(w.Eligible));
        Assert.True(windows.Single(w => w.StartMonotonic == 660).Eligible);
    }

    [Fact]
    public void InWindowInstabilityAndThrottlingCannotTrainReference()
    {
        var aggregator = new ThermalAggregator();
        var windows = Feed(aggregator, 0, 420, i => i == 320 ? Sample(i, power: 140) : Sample(i));
        Assert.False(windows[5].Eligible);
        Assert.False(windows[6].Eligible);
        aggregator.Reset();
        windows = Feed(aggregator, 0, 360, i => Sample(i, throttled: i == 325));
        Assert.False(windows[5].Eligible);
        Assert.Contains("throttl", windows[5].Quality, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SleepGapSplitsWindowsAndRestartsWarmup()
    {
        var aggregator = new ThermalAggregator();
        Feed(aggregator, 0, 320);
        var broken = aggregator.Add(Sample(400));
        Assert.Single(broken);
        Assert.False(broken[0].Eligible);
        var resumed = Feed(aggregator, 401, 760);
        Assert.All(resumed.Where(w => w.StartMonotonic < 700), w => Assert.False(w.Eligible));
        Assert.True(resumed.Single(w => w.StartMonotonic == 700).Eligible);
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(-3600)]
    public void WallClockChangesSplitDataEvenWithContinuousMonotonicClock(int jump)
    {
        var aggregator = new ThermalAggregator();
        Feed(aggregator, 0, 320);
        var broken = aggregator.Add(Sample(321, utcSeconds: 321 + jump));
        Assert.Single(broken);
        Assert.False(broken[0].Eligible);
        var continued = Feed(aggregator, 322, 681, i => Sample(i, utcSeconds: i + jump));
        Assert.All(continued.Where(w => w.StartMonotonic < 621), w => Assert.False(w.Eligible));
        Assert.True(continued.Single(w => w.StartMonotonic == 621).Eligible);
    }

    [Fact]
    public void SessionChangeAndResetNeverInheritStableRun()
    {
        var aggregator = new ThermalAggregator();
        Feed(aggregator, 0, 320);
        var ended = aggregator.Add(Sample(0, session: "session-b", utcSeconds: 321));
        Assert.Single(ended);
        Assert.False(ended[0].Eligible);
        var restarted = Feed(aggregator, 1, 60, i => Sample(i, session: "session-b", utcSeconds: 321 + i));
        Assert.False(Assert.Single(restarted).Eligible);
        aggregator.Reset();
        Assert.Empty(aggregator.Flush());
        Assert.Empty(aggregator.Add(Sample(500)));
    }

    [Fact]
    public void PartialFlushRemainsIneligibleAndDoesNotDuplicateRows()
    {
        var aggregator = new ThermalAggregator();
        Feed(aggregator, 0, 359);
        var final = Assert.Single(aggregator.Flush());
        Assert.False(final.Eligible);
        Assert.Equal(59, final.DurationSeconds);
        Assert.Contains("Partial", final.Quality, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(aggregator.Flush());
    }

    [Fact]
    public void DeviceConfigurationAndSensorMappingHaveSeparateWindows()
    {
        var aggregator = new ThermalAggregator();
        Feed(aggregator, 0, 320);
        var other = Cpu with { Id = "cpu-b" };
        var configured = Cpu with { ConfigurationId = "setup-b" };
        var remapped = Cpu with { SensorMap = "new sensor source" };
        aggregator.Add(Sample(321, device: other));
        aggregator.Add(Sample(321, device: configured));
        aggregator.Add(Sample(321, device: remapped));
        var windows = aggregator.Flush();
        Assert.Equal(4, windows.Count);
        Assert.All(windows, w => Assert.False(w.Eligible));
    }

    [Fact]
    public void ComparableSteadyTemperaturesReportEvidenceWithoutHealthClaim()
    {
        var assessment = CoolingAnalyzer.Assess(Recent(61), Reference(), Epoch.AddMinutes(110));
        Assert.False(assessment.ShouldWarn);
        Assert.Equal(10, assessment.MatchedWindows);
        Assert.Equal(1, assessment.Delta);
        Assert.Equal(60, assessment.ReferenceMinimum);
        Assert.Equal(60, assessment.ReferenceMaximum);
        Assert.DoesNotContain("healthy", assessment.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SustainedDriftWarnsWithoutChangingFixedReference()
    {
        var reference = Reference();
        var assessment = CoolingAnalyzer.Assess(Recent(68), reference, Epoch.AddMinutes(110));
        Assert.True(assessment.ShouldWarn);
        Assert.Equal(10, assessment.MatchedWindows);
        Assert.Equal(8, assessment.Delta);
        Assert.All(reference.Windows, w => Assert.Equal(60, w.Temperature.Average));
        Assert.DoesNotContain("pump fail", assessment.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneSpikeOrOneCoolingMinuteCannotProduceSustainedWarning()
    {
        var recent = Recent(60);
        recent[^1] = Window(109, 80);
        Assert.False(CoolingAnalyzer.Assess(recent, Reference(), Epoch.AddMinutes(110)).ShouldWarn);
        recent = Recent(68);
        recent[5] = Window(105, 61);
        Assert.False(CoolingAnalyzer.Assess(recent, Reference(), Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Theory]
    [InlineData(130, 50)]
    [InlineData(80, 85)]
    public void DissimilarPowerOrUtilizationDoesNotRaiseFalseAlarm(double power, double load)
    {
        var assessment = CoolingAnalyzer.Assess(Recent(80, power, load), Reference(), Epoch.AddMinutes(110));
        Assert.False(assessment.ShouldWarn);
        Assert.Equal(0, assessment.MatchedWindows);
        Assert.Contains("comparable", assessment.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoReferenceAndSparseReferenceRemainLearning()
    {
        Assert.False(CoolingAnalyzer.Assess(Recent(80), null, Epoch.AddMinutes(110)).ShouldWarn);
        var sparse = CoolingAnalyzer.Assess(Recent(80), Reference(29), Epoch.AddMinutes(110));
        Assert.False(sparse.ShouldWarn);
        Assert.Contains("reference", sparse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidReferenceWindowsDoNotCountTowardMinimumCoverage()
    {
        var reference = Reference() with { Windows = Reference().Windows.Select((w, i) => i == 0 ? w with { Eligible = false } : w).ToArray() };
        Assert.False(CoolingAnalyzer.Assess(Recent(80), reference, Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Fact]
    public void FewerThanTenMatchingReferenceWindowsCannotSupportWarning()
    {
        var reference = Reference() with { Windows = Enumerable.Range(0, 30).Select(i => Window(i, power: i < 9 ? 80 : 150)).ToArray() };
        var assessment = CoolingAnalyzer.Assess(Recent(80), reference, Epoch.AddMinutes(110));
        Assert.False(assessment.ShouldWarn);
        Assert.Equal(0, assessment.MatchedWindows);
    }

    [Fact]
    public void BroadReferenceRangeRequiresExceedingItsUpperRangeToo()
    {
        var reference = Reference() with { Windows = Enumerable.Range(0, 30).Select(i => Window(i, i < 27 ? 55 : 70)).ToArray() };
        Assert.False(CoolingAnalyzer.Assess(Recent(65), reference, Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Fact]
    public void DataGapsAndRestartBreakSustainedComparison()
    {
        var recent = Recent(80);
        recent[4] = recent[4] with { Eligible = false };
        Assert.False(CoolingAnalyzer.Assess(recent, Reference(), Epoch.AddMinutes(110)).ShouldWarn);
        recent = Recent(80).Where((_, i) => i != 4).ToArray();
        Assert.False(CoolingAnalyzer.Assess(recent, Reference(), Epoch.AddMinutes(110)).ShouldWarn);
        recent = Recent(80);
        recent[5] = recent[5] with { SessionId = "other-session" };
        Assert.False(CoolingAnalyzer.Assess(recent, Reference(), Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Fact]
    public void StaleOrFutureDataDoesNotGenerateWarning()
    {
        Assert.False(CoolingAnalyzer.Assess(Recent(80), Reference(), Epoch.AddMinutes(113)).ShouldWarn);
        Assert.False(CoolingAnalyzer.Assess(Recent(80), Reference(), Epoch.AddMinutes(109)).ShouldWarn);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("configuration")]
    [InlineData("mapping")]
    public void ChangedIdentityCannotReuseReference(string change)
    {
        var device = change switch
        {
            "device" => Cpu with { Id = "other" },
            "configuration" => Cpu with { ConfigurationId = "other" },
            _ => Cpu with { SensorMap = "other" }
        };
        var recent = Recent(80).Select(w => w with { Device = device }).ToArray();
        Assert.False(CoolingAnalyzer.Assess(recent, Reference(), Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Fact]
    public void HourlySummariesAndNonFiniteAggregatesCannotGenerateWarnings()
    {
        var hourly = Recent(80).Select(w => w with { ResolutionSeconds = 3600 }).ToArray();
        Assert.False(CoolingAnalyzer.Assess(hourly, Reference(), Epoch.AddMinutes(110)).ShouldWarn);
        var bad = Recent(80);
        bad[9] = bad[9] with { Temperature = Metric(double.NaN) };
        Assert.False(CoolingAnalyzer.Assess(bad, Reference(), Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Fact]
    public void ImplausibleTemperaturesInPersistedDataCannotGenerateWarning()
    {
        Assert.False(CoolingAnalyzer.Assess(Recent(900), Reference(), Epoch.AddMinutes(110)).ShouldWarn);
        Assert.False(CoolingAnalyzer.Assess(Recent(68), Reference(30, -900), Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Fact]
    public void DuplicateSamplesAndDuplicateReferenceWindowsDoNotInflateCoverage()
    {
        var aggregator = new ThermalAggregator();
        var windows = Feed(aggregator, 0, 360, i =>
        {
            aggregator.Add(Sample(i));
            return Sample(i);
        });
        // Completion happened during the first add; only the active bucket remains here.
        Assert.Empty(windows);
        Assert.Equal(1, Assert.Single(aggregator.Flush()).SampleCount);
        var reference = Reference() with { Windows = Enumerable.Repeat(Window(0), 30).ToArray() };
        Assert.False(CoolingAnalyzer.Assess(Recent(80), reference, Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Fact]
    public void LowPowerAndInvalidLoadCannotTrainReference()
    {
        var aggregator = new ThermalAggregator();
        var low = Feed(aggregator, 0, 360, i => Sample(i, power: 5));
        Assert.False(low[5].Eligible);
        aggregator.Reset();
        var bad = Feed(aggregator, 0, 360, i => i == 320 ? Sample(i, load: 101) : Sample(i));
        Assert.Equal(59, bad[5].Load.Count);
        Assert.False(bad[5].Eligible);
    }

    [Fact]
    public void InvalidMonotonicInputIsIgnoredWithoutCreatingCorruptWindows()
    {
        var aggregator = new ThermalAggregator();
        Assert.Empty(aggregator.Add(Sample(0) with { MonotonicSeconds = double.NaN }));
        Assert.Empty(aggregator.Add(Sample(0) with { MonotonicSeconds = double.PositiveInfinity }));
        Assert.Empty(aggregator.Add(Sample(0) with { MonotonicSeconds = -1 }));
        Assert.Empty(aggregator.Flush());
    }

    [Theory]
    [InlineData(1200, 2000)]
    [InlineData(0, 200)]
    [InlineData(200, 0)]
    public void DifferentAvailableFanContextPreventsComparison(double currentFan, double referenceFan)
    {
        var recent = Recent(80).Select(w => w with { FanRpm = Metric(currentFan) }).ToArray();
        var reference = Reference() with { Windows = Reference().Windows.Select(w => w with { FanRpm = Metric(referenceFan) }).ToArray() };
        Assert.False(CoolingAnalyzer.Assess(recent, reference, Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Theory]
    [InlineData(1000, 1200)]
    [InlineData(2000, 2350)]
    [InlineData(0, 0)]
    public void ComparableFanContextAllowsTemperatureComparison(double currentFan, double referenceFan)
    {
        var recent = Recent(80).Select(w => w with { FanRpm = Metric(currentFan) }).ToArray();
        var reference = Reference() with { Windows = Reference().Windows.Select(w => w with { FanRpm = Metric(referenceFan) }).ToArray() };
        Assert.True(CoolingAnalyzer.Assess(recent, reference, Epoch.AddMinutes(110)).ShouldWarn);
    }

    [Fact]
    public void MissingFanContextIsDisclosedWhilePowerAndLoadComparisonRemainsAvailable()
    {
        var recent = Recent(80).Select(w => w with { FanRpm = Metric(null) }).ToArray();
        var assessment = CoolingAnalyzer.Assess(recent, Reference(), Epoch.AddMinutes(110));
        Assert.True(assessment.ShouldWarn);
        Assert.Contains("Fan", assessment.Message);
        Assert.Contains("ambient", assessment.Message);
        Assert.Contains("coolant", assessment.Message);
    }

    [Fact]
    public void RetiredDeviceMappingsCannotGrowActiveAggregationWithoutBound()
    {
        var aggregator = new ThermalAggregator();
        var evicted = new List<ThermalWindow>();
        for (int i = 0; i < 40; i++) evicted.AddRange(aggregator.Add(Sample(i, device: Cpu with { ConfigurationId = "setup-" + i })));
        Assert.Equal(8, evicted.Count);
        Assert.All(evicted, w => Assert.False(w.Eligible));
        Assert.Equal(32, aggregator.Flush().Count);
    }

    [Fact]
    public void AlertPolicyRequiresAnActualSustainedWarning()
    {
        var now = Epoch.AddMinutes(110);
        Assert.False(CoolingAlertPolicy.ShouldNotify(CoolingAnalyzer.Assess(Recent(), Reference(), now), Reference(), [], now));
        Assert.True(CoolingAlertPolicy.ShouldNotify(CoolingAnalyzer.Assess(Recent(80), Reference(), now), Reference(), [], now));
    }

    [Fact]
    public void UnacknowledgedWarningSuppressesRepeatsUntilAcknowledgedRegardlessOfAge()
    {
        var now = Epoch.AddDays(10);
        var reference = Reference();
        var assessment = CoolingAnalyzer.Assess(Recent(80), reference, Epoch.AddMinutes(110));
        var warning = new CoolingWarning("warning-a", Cpu.Id, Cpu.ConfigurationId, reference.Id, now.AddDays(-5), "Cooling change", 8);
        Assert.False(CoolingAlertPolicy.ShouldNotify(assessment, reference, [warning], now));
        Assert.True(CoolingAlertPolicy.ShouldNotify(assessment, reference, [warning with { Acknowledged = true }], now));
    }

    [Theory]
    [InlineData(23.99, false)]
    [InlineData(24, true)]
    [InlineData(25, true)]
    [InlineData(-1, false)]
    public void AcknowledgmentDoesNotBypassCooldownAndFutureTimestampsDoNotSpam(double hoursOld, bool expected)
    {
        var now = Epoch.AddDays(10);
        var reference = Reference();
        var assessment = CoolingAnalyzer.Assess(Recent(80), reference, Epoch.AddMinutes(110));
        var warning = new CoolingWarning("warning-a", Cpu.Id, Cpu.ConfigurationId, reference.Id, now.AddHours(-hoursOld), "Cooling change", 8, true);
        Assert.Equal(expected, CoolingAlertPolicy.ShouldNotify(assessment, reference, [warning], now));
    }

    [Fact]
    public void WarningForAnotherReferenceDoesNotMuteCurrentReference()
    {
        var now = Epoch.AddMinutes(110);
        var reference = Reference();
        var assessment = CoolingAnalyzer.Assess(Recent(80), reference, now);
        var warning = new CoolingWarning("warning-a", Cpu.Id, Cpu.ConfigurationId, "old-reference", now, "Cooling change", 8);
        Assert.True(CoolingAlertPolicy.ShouldNotify(assessment, reference, [warning], now));
    }
}
