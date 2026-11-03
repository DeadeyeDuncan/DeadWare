# Per-core CPU usage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw one thin line per CPU core under the bold total on the dashboard CPU usage card, the separate CPU usage pop-out and the widget Usage graph, behind a saved Settings switch, with the core traces persisted in the six-hour live graph history.

**Architecture:** The collector already receives LibreHardwareMonitor's per-core load sensors each poll; a new selector turns them (or a Windows per-processor fallback) into `cpu/core/{i}/usage` snapshot keys. `GraphHistoryProjection` persists up to 48 of those keys under short identity-prefixed names at a bounded cadence so the existing store limits hold. `HistoryChart` (shared by the overview card, `CompactGraph` and `WidgetGraph`) gains core traces as extra `TraceSpec`s drawn thin, without fill, glow or markers, with tint brushes from a pure `ThemeTokens.CoreTint`, and a hover chip listing the eight highest cores.

**Tech Stack:** .NET 10, WPF, LibreHardwareMonitorLib 0.9.6, xUnit; `dotnet test DeadWare.slnx -c Release --nologo`.

**Spec:** `docs/superpowers/specs/2026-11-02-cpu-per-core-design.md`

## Global Constraints

- Version becomes `0.10.0` in `Directory.Build.props` (Task 9 only).
- Setting name and copy: `ShowCpuCoreUsage`, default `false`, label **Show per-core CPU usage on usage graphs**, in Settings → **Monitoring** card.
- Snapshot keys: `cpu/core/{i}/usage`, `i` zero-based, at most **64** entries live (`CpuCoreUsage.MaximumCores`), values rounded to 0.1.
- Persistence: at most **48** cores (`GraphHistoryProjection.MaximumPersistedCores`), stored key `{identity[..12]}/c{i}`, written at most once per **1 s** (once per **2 s** when the reading has more than 32 cores). The store's existing limits (64 values per record, 32768-char payload, 64 MiB file) are **not** changed, so a rolled-back 0.9.x build still reads the file.
- Core traces appear only on `HistoryChart` kind `cpu` when `!CompareDevices || CoresWithDevices`. `WidgetGraph` builds its Usage chart with `CompareDevices=true` (`WidgetGraph.cs:23`) and sets `CoresWithDevices=true`; `CompactGraph`'s joined pop-out does not. Usage history, Alerts and Cooling Health never read `cpu/core/*`.
- Core line: stroke width 0.5 × the total's width × the user's **CPU** thickness multiplier, solid, no area fill, no under-glow, no rune trace, no end marker; the total is drawn after the cores. `frame.Specs[0]` stays the primary metric (hover depends on it).
- Hover chip: after the non-core lines, up to **8** cores in descending value as `Core {k} · {v:0}%` (`k` = position + 1), then `+{N} more` when more cores had a value.
- Card subtitle appends ` · {N} cores` when the switch is on and `N > 0` (kind `cpu` only); the dashboard status line appends ` · Per-core CPU usage unavailable.` when the switch is on and `N == 0`.
- A line is one LibreHardwareMonitor load sensor: a plain core sensor, or one thread sensor where the chip exposes threads. Labels are positional (`Core 1`…`Core N`); no P/E naming.
- Code style follows the repo: dense C#, file-scoped namespaces, records, no new NuGet packages.

## Review Focus

1. An Intel hybrid chip reporting `CPU Core #1 Thread #1/#2` for P-cores and plain `CPU Core #9` for E-cores must yield lines for both kinds (Task 1 test).
2. A core sensor whose value is null this poll must leave a gap in that core's trace while the others draw and the position does not shift (Task 1 and Task 7 tests).
3. Turning the switch off while a pop-out and the widget are open must drop the core traces on the next render without a restart (Task 7 UI check).
4. A saved history with 16 core keys reopened on a machine now exposing 8 cores must restore only 8 and never throw; a 64-core reading at 0.5 s polling must never produce a record over 64 values (Task 4 tests).
5. Blackout and High Contrast must still show distinct, flat core tints with no glow, and a white or black CPU colour must not collapse the ramp (Task 6 test).

---

### Task 1: Core-load selector

**Files:**
- Create: `src/DeadWare.App/CpuCoreUsage.cs`
- Test: `tests/DeadWare.App.Tests/CpuCoreUsageTests.cs`

**Interfaces:**
- Produces: `public static class CpuCoreUsage { public const int MaximumCores = 64; public static IReadOnlyList<double?> FromSensors(IReadOnlyList<SensorReading> sensors); public static string Key(int index) => $"cpu/core/{index}/usage"; }`
  - Candidates: sensors with `HardwareType == "Cpu"`, `Unit == "%"`, `SensorType == "Load"` whose name matches, case-insensitively, `^(CPU Core|P-Core|E-Core)(?: #(\d+))?(?: Thread #(\d+))?$`. A missing `#k` means `k = 1` (single-core chips report `CPU Core`). `CPU Core Max`, `CPU Cores` and `CPU Total` do not match.
  - Grouping: for each `(prefix, k)`, if a plain sensor (no thread) exists it is the single entry for that group; otherwise each thread sensor is an entry. Order: prefix group `P-Core`, then `E-Core`, then `CPU Core`; within a group ascending `k`, then ascending `t`. Entries beyond `MaximumCores` are dropped. A sensor with `Value == null` yields a `null` entry at its position. Values are rounded to one decimal. Empty list when nothing matches.

- [ ] **Step 1: Write the failing tests**

```csharp
public class CpuCoreUsageTests
{
    [Fact] public void Plain_core_sensors_order_by_number_and_keep_null_gaps()
        =>Assert.Equal([10d,null,30.5d],CpuCoreUsage.FromSensors([Load("CPU Core #3",30.46),Load("CPU Core #1",10),Load("CPU Core #2",null)]));
    [Fact] public void Thread_sensors_are_ignored_for_a_core_that_also_has_a_plain_sensor()
        =>Assert.Equal([10d],CpuCoreUsage.FromSensors([Load("CPU Core #1",10),Load("CPU Core #1 Thread #1",9),Load("CPU Core #1 Thread #2",11)]));
    [Fact] public void Thread_only_cores_get_one_entry_per_thread()
        =>Assert.Equal([9d,11d,20d],CpuCoreUsage.FromSensors([Load("CPU Core #2 Thread #1",20),Load("CPU Core #1 Thread #2",11),Load("CPU Core #1 Thread #1",9)]));
    [Fact] public void Hybrid_threaded_p_cores_and_plain_e_cores_both_appear()
        =>Assert.Equal([1d,2d,3d,4d,50d,60d],CpuCoreUsage.FromSensors([Load("CPU Core #9",50),Load("CPU Core #1 Thread #1",1),Load("CPU Core #1 Thread #2",2),Load("CPU Core #10",60),Load("CPU Core #2 Thread #1",3),Load("CPU Core #2 Thread #2",4)]));
    [Fact] public void P_cores_come_before_e_cores_before_generic()
        =>Assert.Equal([5d,6d,1d,2d,9d],CpuCoreUsage.FromSensors([Load("CPU Core #1",9),Load("E-Core #2",2),Load("P-Core #2",6),Load("E-Core #1",1),Load("P-Core #1",5)]));
    [Fact] public void Single_core_without_a_number_is_core_one()
        =>Assert.Equal([42d],CpuCoreUsage.FromSensors([Load("CPU Core",42)]));
    [Fact] public void Total_max_cores_other_units_and_other_hardware_do_not_count()
        =>Assert.Empty(CpuCoreUsage.FromSensors([Load("CPU Total",50),Load("CPU Core Max",90),Load("CPU Cores",40),Load("CPU Core #1",3000) with {Unit="MHz",SensorType="Clock"},Load("CPU Core #1",40) with {HardwareType="GpuNvidia"}]));
    [Fact] public void More_than_sixty_four_entries_are_capped()
        =>Assert.Equal(64,CpuCoreUsage.FromSensors(Enumerable.Range(1,70).Select(k=>Load($"CPU Core #{k}",k)).ToArray()).Count);
    [Fact] public void Key_is_zero_based()=>Assert.Equal("cpu/core/0/usage",CpuCoreUsage.Key(0));
    private static SensorReading Load(string name,double? value)=>new("/cpu/0/load/"+name,"Test CPU",name,"%",value,"/cpu/0","Cpu","Load");
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/DeadWare.App.Tests -c Release --nologo --filter CpuCoreUsageTests`; expected build error, `CpuCoreUsage` not found.
- [ ] **Step 3: Implement `CpuCoreUsage`** per the Interfaces block (one compiled `Regex`; group with a dictionary keyed by `(int Group,int K)`).
- [ ] **Step 4: Run to verify it passes** — expected 9 passed.
- [ ] **Step 5: Commit** — the controller commits: `feat: select per-core CPU load sensors`.

### Task 2: Windows per-processor fallback

**Files:**
- Create: `src/DeadWare.App/WindowsCpuCores.cs`
- Test: `tests/DeadWare.App.Tests/WindowsCpuCoresTests.cs`

**Interfaces:**
- Produces: `public sealed class WindowsCpuCores { public IReadOnlyList<double?> Read(); internal static IReadOnlyList<double?> Delta(ReadOnlySpan<(ulong Idle,ulong Kernel,ulong User)> previous, ReadOnlySpan<(ulong Idle,ulong Kernel,ulong User)> current); }`
  - `Read()` sizes its buffer from `GetActiveProcessorCount(ALL_PROCESSOR_GROUPS = 0xFFFF)` (`kernel32.dll`), calls `NtQuerySystemInformation(SystemProcessorPerformanceInformation = 8)` (`ntdll.dll`), derives the processor count from the returned length ÷ 48, keeps the previous sample, and returns `Delta(previous, current)`. The first call, a non-zero NTSTATUS, or a count change returns an empty list and resets the previous sample. The struct `SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION` is `[StructLayout(LayoutKind.Sequential)]` with `long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime; uint InterruptCount;` and `Marshal.SizeOf` 48.
  - `Delta` computes per processor `100 × (busy − idle) / busy` where `busy = kernelΔ + userΔ` and `idle = idleΔ` (kernel time includes idle, as in `WindowsCounters.Cpu()`), rounded to one decimal. `busy == 0`, any delta going backwards, or `idle > busy` yields `null`. Output truncated to `CpuCoreUsage.MaximumCores`; spans of different lengths return an empty list.

- [ ] **Step 1: Write the failing tests**

```csharp
public class WindowsCpuCoresTests
{
    [Fact] public void Delta_gives_percent_busy_per_processor()
        =>Assert.Equal([25d,100d],WindowsCpuCores.Delta([(0,0,0),(0,0,0)],[(75,100,0),(0,50,50)]));
    [Fact] public void Backwards_or_zero_counters_yield_null()
        =>Assert.Equal([null,null],WindowsCpuCores.Delta([(10,10,10),(0,0,0)],[(5,10,10),(0,0,0)]));
    [Fact] public void Processor_count_change_yields_empty()
        =>Assert.Empty(WindowsCpuCores.Delta([(0,0,0)],[(0,1,1),(0,1,1)]));
    [Fact] public void Struct_is_forty_eight_bytes()
        =>Assert.Equal(48,System.Runtime.InteropServices.Marshal.SizeOf<WindowsCpuCores.ProcessorPerformance>());
    [Fact] public void First_read_is_empty_and_second_is_sized_like_the_machine()
    {   var cores=new WindowsCpuCores();
        Assert.Empty(cores.Read());Thread.Sleep(50);
        Assert.Equal(Math.Min(64,Environment.ProcessorCount),cores.Read().Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — filter `WindowsCpuCoresTests`; expected build error.
- [ ] **Step 3: Implement `WindowsCpuCores`** (make the struct `internal`).
- [ ] **Step 4: Run to verify it passes** — expected 5 passed.
- [ ] **Step 5: Commit** — `feat: per-processor CPU load from Windows as a fallback`.

### Task 3: Collector publishes core keys

**Files:**
- Modify: `src/DeadWare.App/Collector.cs:11` (Reading), `:126-152` (poll loop), `:186` (failed reading)
- Test: `tests/DeadWare.App.Tests/CollectorPollingTests.cs` (add one test)

**Interfaces:**
- Consumes: `CpuCoreUsage.FromSensors`, `CpuCoreUsage.Key`, `WindowsCpuCores.Read`.
- Produces: `Reading` gains `public int CpuCoreCount {get;init;}` (default 0). Snapshot `Values` carries `cpu/core/{i}/usage` for `i < CpuCoreCount`. Static helper `internal static int PublishCores(Dictionary<string,double?> values, IReadOnlyList<double?> cores)` writes the keys and returns `cores.Count`.
- Source lock: a private `bool hardwareCores` becomes true the first time `FromSensors` returns a non-empty list; while true the Windows fallback is never consulted. While false, `WindowsCpuCores.Read()` is used whenever LibreHardwareMonitor gave nothing.

- [ ] **Step 1: Write the failing test** (pure helper, no hardware)

```csharp
[Fact] public void PublishCores_writes_zero_based_keys_and_returns_count()
{
    var values=new Dictionary<string,double?>();
    Assert.Equal(3,Collector.PublishCores(values,[10,null,30]));
    Assert.Equal(10d,values["cpu/core/0/usage"]);Assert.Null(values["cpu/core/1/usage"]);Assert.Equal(30d,values["cpu/core/2/usage"]);
    Assert.Equal(0,Collector.PublishCores(new(),[]));
}
```

- [ ] **Step 2: Run to verify it fails** — filter `PublishCores`; expected build error.
- [ ] **Step 3: Implement.** After `values["cpu/usage"]=…`: `var cores=CpuCoreUsage.FromSensors(sensors); if(cores.Count>0)hardwareCores=true; else if(!hardwareCores)cores=windowsCores.Read();` with `windowsCores` a new `WindowsCpuCores` field beside `windows`; call `PublishCores`; pass `CpuCoreCount=cores.Count` in the `Reading` initializer. The failed-reading path keeps `CpuCoreCount` 0.
- [ ] **Step 4: Run to verify it passes** — filter `PublishCores`; then the full App test project.
- [ ] **Step 5: Commit** — `feat: publish per-core CPU usage keys from the collector`.

### Task 4: Persist core traces in graph history

**Files:**
- Modify: `src/DeadWare.App/GraphHistoryProjection.cs:10-34`, `src/DeadWare.App/GraphHistoryService.cs:82-107` (Process)
- Test: `tests/DeadWare.App.Tests/GraphHistoryServiceTests.cs` (add three tests; reuse its `Reading(...)` helper at `:271` with `with {CpuCoreCount=n}` and core values added to `Sample.Values`)

**Interfaces:**
- Consumes: `Reading.CpuCoreCount`, `CpuCoreUsage.Key`.
- Produces:
  - `GraphHistoryProjection.MaximumPersistedCores = 48`. `Mappings` adds, for `i < Math.Min(CpuCoreCount, 48)` and only when the single-CPU identity exists, `identity[..12] + "/c" + i → CpuCoreUsage.Key(i)`. The CPU identity hash input is unchanged.
  - `internal static bool IsCore(string liveKey) => liveKey.StartsWith("cpu/core/", StringComparison.Ordinal)`.
  - `internal static TimeSpan CoreCadence(int coreCount) => coreCount > 32 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1)`.
  - In `GraphHistoryService.Process`, when building `projected`, core mappings are included only if `sample.Timestamp − lastCoreStamp >= CoreCadence(reading.CpuCoreCount)` (then `lastCoreStamp = sample.Timestamp`); other samples omit the core keys entirely (not null values). Restore is unchanged: a record without core keys simply contributes no core values.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Core_metrics_join_the_unchanged_cpu_identity_with_short_keys()
{
    var reading=Reading(DateTimeOffset.UtcNow,1) with {CpuCoreCount=50};
    var mappings=GraphHistoryProjection.Mappings(reading,"machine");
    string identity=Assert.Single(mappings,p=>p.Value=="cpu/usage").Key[..^"/usage".Length];
    Assert.Equal("cpu/core/47/usage",mappings[identity[..12]+"/c47"]);
    Assert.DoesNotContain(mappings.Values,v=>v=="cpu/core/48/usage");
    Assert.Equal(identity,Assert.Single(GraphHistoryProjection.Mappings(reading with {CpuCoreCount=0},"machine"),p=>p.Value=="cpu/usage").Key[..^"/usage".Length]);
    Assert.True(mappings.Count<=64);
}
[Fact] public void Core_values_are_written_at_the_cadence_and_never_exceed_the_record_limit()
{   // Feed 64-core readings 0.5 s apart for 10 s through the service harness; read the store:
    // every record has <= 64 values, records carrying any "/c" key are >= 2 s apart (64 > 32 cores), and non-core keys are on every record.
}
[Fact] public void Restore_returns_only_the_cores_present_in_the_current_reading()
{   // Write readings with CpuCoreCount=16, stop; start a new service with CpuCoreCount=8 readings; the restored Snapshot has cpu/core/7/usage, not cpu/core/8/usage; no exception; status does not report an error.
}
```

- [ ] **Step 2: Run to verify they fail** — filter `Core_metrics|Core_values|Restore_returns_only`.
- [ ] **Step 3: Implement** per the Interfaces block.
- [ ] **Step 4: Run to verify they pass**; run `GraphHistoryServiceTests` fully.
- [ ] **Step 5: Commit** — `feat: persist per-core CPU traces in six-hour graph history`.

### Task 5: Setting, session and Settings card

**Files:**
- Modify: `src/DeadWare.Core/Monitoring.cs:313` (after `ShowCpuPumpSpeed`), `src/DeadWare.App/UiSession.cs:133` (after `SetCpuPumpSpeed`), `src/DeadWare.App/Windows.cs:33,124-127,191,254`
- Test: `tests/DeadWare.Tests/CpuCoreSettingsTests.cs` (new), `tests/DeadWare.App.Tests/ShellUiChecks.cs` (extend)

**Interfaces:**
- Produces: `UserSettings.ShowCpuCoreUsage {get;set;}` (bool, default false); `UiSession.SetCpuCoreUsage(bool enabled)` with the `Changed?.Invoke(); _ = SaveAsync();` pattern; a `CheckBox cpuCoreUsage` with Content and AutomationProperties name **Show per-core CPU usage on usage graphs**, added to the **monitoring** group after the Extended-sensors help text, followed by muted help text `One thin line per core under the total on the CPU usage card, its pop-out and the widget. Core traces are kept in the six-hour graph history.`; synced in the settings-refresh block at `:254`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class CpuCoreSettingsTests
{
    [Fact] public void Legacy_settings_keep_per_core_off()
        =>Assert.False(SettingsCodec.Read("{\"ShowCpuPumpSpeed\":true}").Settings.ShowCpuCoreUsage);
    [Theory][InlineData(false)][InlineData(true)] public void Per_core_preference_roundtrips(bool enabled)
    {
        var loaded=SettingsCodec.Read($"{{\"ShowCpuCoreUsage\":{enabled.ToString().ToLowerInvariant()}}}");
        Assert.Equal(enabled,loaded.Settings.ShowCpuCoreUsage);
        using var json=JsonDocument.Parse(SettingsCodec.Write(loaded.Settings));
        Assert.Equal(enabled,json.RootElement.GetProperty("ShowCpuCoreUsage").GetBoolean());
    }
}
```
In `ShellUiChecks`, where the Settings page is built, assert a `CheckBox` with automation name `Show per-core CPU usage on usage graphs` exists inside the **Monitoring** card and toggling it flips `session.Settings.ShowCpuCoreUsage` and raises `Changed`.

- [ ] **Step 2: Run to verify they fail** — filter `CpuCoreSettingsTests|BatteryUiTests`.
- [ ] **Step 3: Implement** mirroring `cpuPumpSpeed`.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `feat: per-core CPU usage setting`.

### Task 6: Core tint brushes

**Files:**
- Modify: `src/DeadWare.App/ThemeTokens.cs` (pure helper), `src/DeadWare.App/Theme.cs:22-33` (brush cache)
- Test: `tests/DeadWare.App.Tests/ThemeTokenTests.cs` (add two tests)

**Interfaces:**
- Produces:
  - `public static Color ThemeTokens.CoreTint(Color cpu, int index, int count, bool highContrast)`: `count <= 1` returns `cpu`. Otherwise `p = index / (count − 1.0)`. Normal: HSL of `cpu` with hue `+ (−18° + 36° p)`, saturation × 0.85, lightness moved by `0.12 + 0.25 p` toward 0.78 if `cpu`'s lightness < 0.5 else toward 0.28, clamped to [0.22, 0.85]. High contrast: hue and saturation unchanged, lightness `0.35 + 0.40 p` (always distinct, never white or black).
  - `public static SolidColorBrush Theme.CoreBrush(int index, int count)`: frozen brush of `CoreTint(((SolidColorBrush)Brush("Graph/cpu")).Color, index, count, SystemParameters.HighContrast)`, cached in a `Dictionary<(Color,int,int),SolidColorBrush>` that `ApplyGraphColors` clears (theme change and custom CPU colour both reach `ApplyGraphColors`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Core_tints_are_distinct_for_every_count_and_endpoint_colour()
{
    foreach(var cpu in new[]{Color.FromRgb(0x5B,0xC8,0xFF),Color.FromRgb(0xFF,0x7A,0x3D),Colors.White,Colors.Black,Color.FromRgb(0x30,0xD1,0x58)})
    foreach(bool hc in new[]{false,true})
    foreach(int count in new[]{1,8,64})
    {
        var tints=Enumerable.Range(0,count).Select(i=>ThemeTokens.CoreTint(cpu,i,count,hc)).ToArray();
        Assert.Equal(count,tints.Distinct().Count());
        Assert.All(tints,t=>Assert.Equal(255,t.A));
    }
    Assert.Equal(Colors.White,ThemeTokens.CoreTint(Colors.White,0,1,false));
}
[Fact] public void High_contrast_tints_stay_off_white_and_black()
    =>Assert.All(Enumerable.Range(0,16).Select(i=>ThemeTokens.CoreTint(Colors.White,i,16,true)),t=>Assert.True(t.R<250&&t.R>40));
```

- [ ] **Step 2: Run to verify they fail** — filter `Core_tints|High_contrast_tints`.
- [ ] **Step 3: Implement** (an HSL round-trip helper may live in `ThemeTokens`).
- [ ] **Step 4: Run to verify they pass** — filter `ThemeTokenTests`.
- [ ] **Step 5: Commit** — `feat: per-core tint colours derived from the CPU graph colour`.

### Task 7: Core traces and hover in HistoryChart

**Files:**
- Modify: `src/DeadWare.Core/Monitoring.cs:90` (add `SeriesMany`), `src/DeadWare.App/GraphStroke.cs` (add `DrawPlain`), `src/DeadWare.App/Charts.cs:105-150` (ctor flag, Query/Prepare), `:194` (TraceSpec), `:240-296` (specs, draw order), `:298-316` (hover), `:341-356` (DrawTrace); `src/DeadWare.App/WidgetGraph.cs:23` (`CoresWithDevices=true`)
- Test: `tests/DeadWare.Tests/SampleHistoryTests.cs` or the nearest existing SampleHistory test file (one test), `tests/DeadWare.App.Tests/LiveRenderingUiChecks.cs` (extend)

**Interfaces:**
- Consumes: `Settings.ShowCpuCoreUsage`, `Reading.CpuCoreCount` via `session.Current`, `Theme.CoreBrush`, `CpuCoreUsage.Key`.
- Produces:
  - `SampleHistory.SeriesMany(IReadOnlyList<string> ids, double now, int seconds) → IReadOnlyDictionary<string,IReadOnlyList<SamplePoint>>`: one pass over the samples under the lock, same per-point fields as `Series`.
  - `GraphStroke.DrawPlain(DrawingContext dc, Geometry geometry, Brush brush, double width)`: solid round-joined pen, no glow, no dashes.
  - `HistoryChart.CoresWithDevices {get;init;}`; `private bool ShowsCores() => session.Settings.ShowCpuCoreUsage && kind=="cpu" && (!CompareDevices || CoresWithDevices)`.
  - `Query` gains `int Cores` (`ShowsCores() ? session.Current?.CpuCoreCount ?? 0 : 0`). `Prepare` reads core series through `SeriesMany` in the raw path and `ChartRanges` per core in the smoothed path.
  - `TraceSpec` gains `bool Core` and `Brush? Brush` (null for existing specs). Specs order: primary first (unchanged), then the existing secondary/fan/pump, then core specs `new(CpuCoreUsage.Key(i), Theme.GraphKey("cpu"), false, false, maximum, 0){Core=true, Brush=Theme.CoreBrush(i, cores)}`.
  - Draw order in `OnRender`: `foreach spec where Core` then `foreach spec where !Core`.
  - `DrawTrace` for `Core`: `width = 0.5 × width × GraphStroke.Style(session.Settings, Theme.GraphKey("cpu")).Thickness`; skip the area, keep the band using `spec.Brush`, skip `UnderGlow`, `RuneTrace` and the end marker; `GraphStroke.DrawPlain(dc, path.Path, spec.Brush!, width)`.
  - `DrawHover`: non-core specs unchanged (dot + line using `spec.Brush ?? Theme.Brush(spec.Color)`); core specs collect `(index, value)` and, after the loop, append `HistoryChart.HoverCoreLines(...)` texts, each with that core's brush and `+N more` in `Muted`. No hover dot for cores.
  - `internal static IReadOnlyList<(string Text,int Core)> HoverCoreLines(IReadOnlyList<(int Core,double Value)> cores)` returns at most 8 entries sorted by value descending (`Core {k+1} · {v:0}%`) then `("+N more", -1)` when more remain.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void SeriesMany_matches_Series_per_id()
{   // add 5 snapshots with "a" and "b"; Assert SeriesMany(["a","b"],now,60)["a"] equals Series("a",now,60) point-for-point, and a missing id yields all-null points.
}
[Fact] public void Hover_core_lines_take_the_eight_highest_and_count_the_rest()
{
    var lines=HistoryChart.HoverCoreLines(Enumerable.Range(0,12).Select(i=>(i,(double)i*5)).ToArray());
    Assert.Equal(9,lines.Count);Assert.Equal("Core 12 · 55%",lines[0].Text);Assert.Equal("Core 5 · 20%",lines[7].Text);Assert.Equal(("+4 more",-1),lines[8]);
    Assert.Equal(3,HistoryChart.HoverCoreLines([(0,1),(1,2),(2,3)]).Count);
}
```
In `LiveRenderingUiChecks.Run`: add `cpu/core/0..7/usage` series to the fixture (core 3 null on every 10th sample), set `CpuCoreCount=8` on the fixture `Reading`, and build three `cpu` charts: standalone (`CompareDevices=false`), widget-shaped (`CompareDevices=true, CoresWithDevices=true`), joined (`CompareDevices=true`). Identify core strokes as `GeometryDrawing`s whose `Pen.Brush` is a `Theme.CoreBrush` instance. Assert: switch off → 0 core strokes on all three; switch on → 8 on standalone and widget-shaped, 0 on joined, each core pen thickness equal to half the primary pen's; the core strokes are present for both a 60-second and a 21600-second (smoothed) history; switch back off and re-render → 0 again.

- [ ] **Step 2: Run to verify they fail** — filter `SeriesMany|Hover_core_lines|BatteryUiTests`.
- [ ] **Step 3: Implement** per the Interfaces block.
- [ ] **Step 4: Run to verify they pass**; then the full solution tests.
- [ ] **Step 5: Commit** — `feat: draw per-core CPU traces with a bounded hover chip`.

### Task 8: Subtitle and status copy

**Files:**
- Modify: `src/DeadWare.App/Charts.cs:497` (overview card subtitle), `src/DeadWare.App/Windows.cs:264-273` (dashboard status line)
- Test: `tests/DeadWare.App.Tests/LiveRenderingUiChecks.cs` or `ShellUiChecks.cs` (one check each)

**Interfaces:**
- Consumes: `Reading.CpuCoreCount`, `Settings.ShowCpuCoreUsage`.
- Produces: CPU usage card subtitle `"{CpuName} · {N} cores"` when the switch is on and `N > 0` (kind `cpu` only); dashboard `status` text appends ` · Per-core CPU usage unavailable.` when the switch is on, the reading is not null and `N == 0`.

- [ ] **Step 1: Write the failing checks** — overview CPU card with the fixture reading (`CpuCoreCount=8`, switch on) has a subtitle ending ` · 8 cores`; with `CpuCoreCount=0` the dashboard status contains `Per-core CPU usage unavailable.` and the subtitle has no `cores` suffix; the `cputemp` card never gains the suffix.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass** — `BatteryUiTests` filter.
- [ ] **Step 5: Commit** — `feat: core count in the CPU card subtitle and an unavailable notice`.

### Task 9: Release documentation, previews and packaging

**Files:**
- Modify: `Directory.Build.props` (Version 0.10.0), `README.md` (Features bullet + a paragraph under "Graph pop-outs" after the pump paragraph), `CHANGELOG.md` (new `## 0.10.0 — Per-core CPU usage` section at top), `docs/validation.md` (new section at top, same shape as the 0.9.0 entry), `docs/releases/v0.10.0.md` (new, same shape as `v0.9.1.md`), `tools/OverviewPreview/Program.cs:117-145` (fixture: add `cpu/core/0..7/usage` waves, `CpuCoreCount=8`, and `ShowCpuCoreUsage=true` in the preview settings)

- [ ] **Step 1: Bump the version and write the docs.** CHANGELOG bullet copy: *Add **Settings → Show per-core CPU usage on usage graphs**. One thin line per core, tinted from the CPU graph colour, draws under the bold total on the dashboard CPU usage card, the separate CPU usage pop-out and the widget Usage graph; hover a graph to see the eight busiest cores. Core traces are saved in the six-hour graph history (up to 48 cores, once a second). Off by default; the joined CPU/GPU pop-out, Usage history and Alerts are unchanged.* README also states: where LibreHardwareMonitor exposes threads, each thread is a line; Windows fallback traces are live-only when no CPU identity is available.
- [ ] **Step 2: Full build and suite** — `dotnet build DeadWare.slnx -c Release --nologo` (0 warnings) then `dotnet test DeadWare.slnx -c Release --nologo`; record the pass count in `docs/validation.md`.
- [ ] **Step 3: Render previews** — `dotnet run --project tools/OverviewPreview -c Release -- --render artifacts/ui-cpu-cores` and `--render-shell artifacts/ui-cpu-cores-shell`; the shell report's `"failures"` must be `[]`; open the Midnight, Blackout and Daylight overview frames and confirm eight thin core lines under the CPU total.
- [ ] **Step 4: Package** — `pwsh -File scripts/publish.ps1` succeeds.
- [ ] **Step 5: Commit** — `docs: per-core CPU usage release notes, changelog, validation and 0.10.0`; push; open the pull request against `main` from `claude/cpu-per-core`, title `Per-core CPU usage (0.10.0)`; verify CI green.
