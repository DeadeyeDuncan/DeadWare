# Per-core CPU usage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw one thin line per CPU core under the bold total on the dashboard CPU usage card, the separate CPU usage pop-out and the widget Usage graph, behind a saved Settings switch, with the core traces persisted in the six-hour live graph history.

**Architecture:** The collector already receives LibreHardwareMonitor's `CPU Core #k` load sensors each poll; a new selector turns them (or a Windows per-processor fallback) into `cpu/core/{i}/usage` snapshot keys. `GraphHistoryProjection` persists those keys under the existing CPU identity. `HistoryChart` (shared by the overview card, `CompactGraph` and `WidgetGraph`) gains core traces as extra `TraceSpec`s drawn thin, without fill, glow or markers, with tint brushes `Graph/cpucore/{i}` derived from the CPU graph colour, and a hover chip listing the eight highest cores.

**Tech Stack:** .NET 10, WPF, LibreHardwareMonitorLib 0.9.6, xUnit; `dotnet test DeadWare.slnx -c Release --nologo`.

**Spec:** `docs/superpowers/specs/2026-11-02-cpu-per-core-design.md`

## Global Constraints

- Version becomes `0.10.0` in `Directory.Build.props` (Task 9 only).
- Setting name and copy: `ShowCpuCoreUsage`, default `false`, label **Show per-core CPU usage on usage graphs**, in Settings → **Monitoring** card.
- Snapshot keys: `cpu/core/{i}/usage`, `i` zero-based, at most **64** cores published, persisted and drawn.
- Core traces appear only on `HistoryChart` kind `cpu` when `!CompareDevices || CoresWithDevices`. `WidgetGraph` builds its Usage chart with `CompareDevices=true` (`WidgetGraph.cs:23`), so it sets `CoresWithDevices=true`; `CompactGraph`'s joined pop-out does not. Usage history, Alerts and Cooling Health never read `cpu/core/*`.
- Core line: stroke width 0.5 × the total's width (before the user's thickness multiplier), solid, no area fill, no under-glow, no rune trace, no end marker; total drawn last.
- Hover chip: after the total, up to **8** cores in descending value as `Core {k} · {v:0}%` (`k` one-based), then `+{N} more` when more exist.
- Card subtitle appends ` · {N} cores` when the switch is on and `N > 0`; hardware status appends ` · Per-core CPU usage unavailable.` when the switch is on and `N == 0`.
- Core labels are `Core 1`…`Core N` in sensor order; no P/E naming.
- Code style follows the repo: dense C#, file-scoped namespaces, records, no new NuGet packages.

## Review Focus

1. A hybrid chip reporting both `CPU Core #3` and `CPU Core #3 Thread #1/#2` must yield one line for core 3, not three (Task 1 test).
2. A core sensor whose value is null this poll must leave a gap in that core's trace while the others draw (Task 1 and Task 7 tests).
3. Turning the switch off while a pop-out and the widget are open must drop the core traces on the next render without a restart (Task 7 UI check).
4. A saved history with 16 core keys reopened on a machine now exposing 8 cores must restore only 8 and never throw (Task 4 test).
5. Blackout and High Contrast must still show distinct, flat core tints with no glow (Task 6 test).

---

### Task 1: Core-load selector

**Files:**
- Create: `src/DeadWare.App/CpuCoreUsage.cs`
- Test: `tests/DeadWare.App.Tests/CpuCoreUsageTests.cs`

**Interfaces:**
- Produces: `public static class CpuCoreUsage { public const int MaximumCores = 64; public static IReadOnlyList<double?> FromSensors(IReadOnlyList<SensorReading> sensors); public static string Key(int index) => $"cpu/core/{index}/usage"; }`
  - `FromSensors` returns one entry per core from sensors with `HardwareType == "Cpu"`, `Unit == "%"`, `SensorType == "Load"` and a name matching `^(CPU Core|P-Core|E-Core) #(\d+)$` (case-insensitive; LibreHardwareMonitor 0.9.6 emits all three prefixes, plus `CPU Core Max` and `CPU Cores`, which must not match). Order: `P-Core` entries, then `E-Core`, then `CPU Core`, each group ascending by number. Names matching the same prefixes followed by ` Thread #(\d+)` are used only when no plain core sensor exists, each thread then becoming an entry ordered by `(group, k, t)`. Entries beyond `MaximumCores` are dropped. Returns an empty list when nothing matches. A matched sensor with `Value == null` yields a `null` entry.

- [ ] **Step 1: Write the failing tests**

```csharp
public class CpuCoreUsageTests
{
    [Fact] public void Plain_core_sensors_order_by_number_and_keep_null_gaps()
    {   // sensors given as #3, #1, #2(null)
        var cores=CpuCoreUsage.FromSensors([Load("CPU Core #3",30),Load("CPU Core #1",10),Load("CPU Core #2",null)]);
        Assert.Equal([10d,null,30d],cores);
    }
    [Fact] public void Thread_sensors_are_ignored_when_plain_core_sensors_exist()
        =>Assert.Equal([10d],CpuCoreUsage.FromSensors([Load("CPU Core #1",10),Load("CPU Core #1 Thread #1",9),Load("CPU Core #1 Thread #2",11)]));
    [Fact] public void Thread_only_chips_get_one_entry_per_thread()
        =>Assert.Equal([9d,11d,20d],CpuCoreUsage.FromSensors([Load("CPU Core #2 Thread #1",20),Load("CPU Core #1 Thread #2",11),Load("CPU Core #1 Thread #1",9)]));
    [Fact] public void Total_max_other_units_and_other_hardware_do_not_count()
        =>Assert.Empty(CpuCoreUsage.FromSensors([Load("CPU Total",50),Load("CPU Core Max",90),Load("CPU Cores",40),Load("CPU Core #1",3000) with {Unit="MHz",SensorType="Clock"},Load("CPU Core #1",40) with {HardwareType="GpuNvidia"}]));
    [Fact] public void Hybrid_p_cores_come_before_e_cores()
        =>Assert.Equal([5d,6d,1d,2d],CpuCoreUsage.FromSensors([Load("E-Core #2",2),Load("P-Core #2",6),Load("E-Core #1",1),Load("P-Core #1",5)]));
    [Fact] public void More_than_sixty_four_cores_are_capped()
        =>Assert.Equal(64,CpuCoreUsage.FromSensors(Enumerable.Range(1,70).Select(k=>Load($"CPU Core #{k}",k)).ToArray()).Count);
    [Fact] public void Key_is_zero_based()=>Assert.Equal("cpu/core/0/usage",CpuCoreUsage.Key(0));
    private static SensorReading Load(string name,double? value)=>new("/cpu/0/load/"+name.GetHashCode(),"Test CPU",name,"%",value,"/cpu/0","Cpu","Load");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/DeadWare.App.Tests -c Release --nologo --filter CpuCoreUsageTests`
Expected: build error, `CpuCoreUsage` not found.

- [ ] **Step 3: Implement `CpuCoreUsage` in `src/DeadWare.App/CpuCoreUsage.cs`** per the Interfaces block. Use two compiled `Regex` constants; prefer plain-core matches, fall back to thread matches.

- [ ] **Step 4: Run to verify it passes**

Run: same filter. Expected: 8 passed.

- [ ] **Step 5: Commit**

```bash
git add src/DeadWare.App/CpuCoreUsage.cs tests/DeadWare.App.Tests/CpuCoreUsageTests.cs
git commit -m "feat: select per-core CPU load sensors"
```

### Task 2: Windows per-processor fallback

**Files:**
- Create: `src/DeadWare.App/WindowsCpuCores.cs`
- Test: `tests/DeadWare.App.Tests/WindowsCpuCoresTests.cs`

**Interfaces:**
- Produces: `public sealed class WindowsCpuCores { public IReadOnlyList<double?> Read(); internal static IReadOnlyList<double?> Delta(ReadOnlySpan<(ulong Idle,ulong Kernel,ulong User)> previous, ReadOnlySpan<(ulong Idle,ulong Kernel,ulong User)> current); }`
  - `Read()` calls `NtQuerySystemInformation(SystemProcessorPerformanceInformation = 8)` from `ntdll.dll` with a buffer of `Environment.ProcessorCount` × 48-byte `SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION` (IdleTime, KernelTime, UserTime, DpcTime, InterruptTime, InterruptCount), keeps the previous sample, and returns `Delta(previous, current)`. First call, a failed call, or a processor-count change returns an empty list and resets the previous sample.
  - `Delta` computes per processor `100 × (busy − idle) / busy` where `busy = (kernel − prevKernel) + (user − prevUser)` and `idle = idleDelta`, exactly as `Collector.Cpu()` does machine-wide (kernel time includes idle). A processor with `busy == 0`, a counter that went backwards, or `idle > busy` yields `null`. More than `CpuCoreUsage.MaximumCores` processors are truncated.

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
    [Fact] public void First_read_is_empty_and_second_is_sized_like_the_machine()
    {   var cores=new WindowsCpuCores();
        Assert.Empty(cores.Read());Thread.Sleep(50);
        Assert.Equal(Math.Min(64,Environment.ProcessorCount),cores.Read().Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — filter `WindowsCpuCoresTests`; expected build error.
- [ ] **Step 3: Implement `WindowsCpuCores`** with a `[DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int returned);` and a sequential struct; treat non-zero NTSTATUS as failure.
- [ ] **Step 4: Run to verify it passes** — expected 4 passed.
- [ ] **Step 5: Commit** — `git commit -m "feat: per-processor CPU load from Windows as a fallback"`

### Task 3: Collector publishes core keys

**Files:**
- Modify: `src/DeadWare.App/Collector.cs:11` (Reading), `:126-152` (poll loop), `:160` (hardware status)
- Test: `tests/DeadWare.App.Tests/CollectorPollingTests.cs` (add one test)

**Interfaces:**
- Consumes: `CpuCoreUsage.FromSensors`, `CpuCoreUsage.Key`, `WindowsCpuCores.Read`.
- Produces: `Reading` gains `public int CpuCoreCount {get;init;}` (default 0). Snapshot `Values` carries `cpu/core/{i}/usage` for `i < CpuCoreCount`. Static helper `internal static int PublishCores(Dictionary<string,double?> values, IReadOnlyList<double?> cores)` writes the keys and returns `cores.Count`.

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
- [ ] **Step 3: Implement.** In the poll loop after `values["cpu/usage"]=…`: `var cores=CpuCoreUsage.FromSensors(sensors); if(cores.Count==0)cores=windowsCores.Read();` where `windowsCores` is a new `WindowsCpuCores` field created beside `windows`; call `PublishCores`; pass `CpuCoreCount=cores.Count` in the `Reading` initializer. The failed-reading path at `:186` keeps `CpuCoreCount` 0. Do **not** append status text here (Task 8 owns it, since it depends on the setting).
- [ ] **Step 4: Run to verify it passes** — filter `PublishCores`; expected 1 passed. Then run the full App test project to confirm nothing else broke.
- [ ] **Step 5: Commit** — `git commit -m "feat: publish per-core CPU usage keys from the collector"`

### Task 4: Persist core traces in graph history

**Files:**
- Modify: `src/DeadWare.App/GraphHistoryProjection.cs:21-26`
- Test: `tests/DeadWare.App.Tests/GraphHistoryServiceTests.cs` (add two tests)

**Interfaces:**
- Consumes: `Reading.CpuCoreCount`, `CpuCoreUsage.Key`.
- Produces: `Mappings` includes `{cpuIdentity}/core/{i}/usage → cpu/core/{i}/usage` for `i < reading.CpuCoreCount`, in addition to `usage`, `temperature`, `pump`. Identity hash input unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Core_metrics_join_the_unchanged_cpu_identity()
{
    var reading=CpuReading(cores:3);   // helper: one "/cpu/0" temperature sensor, CpuCoreCount=3, like CpuPumpSelectionTests.Graph_mapping…
    var mappings=GraphHistoryProjection.Mappings(reading,"machine");
    string prefix=Assert.Single(mappings,p=>p.Value=="cpu/usage").Key[..^"usage".Length];
    Assert.Equal("cpu/core/2/usage",mappings[prefix+"core/2/usage"]);
    Assert.DoesNotContain(prefix+"core/3/usage",mappings.Keys);
    Assert.Equal(prefix,Assert.Single(GraphHistoryProjection.Mappings(CpuReading(cores:0),"machine"),p=>p.Value=="cpu/usage").Key[..^"usage".Length]);
}
[Fact] public void Restore_returns_only_the_cores_present_in_the_current_reading()
{   // Use the existing GraphHistoryService test harness: write 16 core keys via a reading with CpuCoreCount=16,
    // reopen with a reading of CpuCoreCount=8, assert the restored Snapshot has cpu/core/7/usage and not cpu/core/8/usage, and no exception.
}
```

- [ ] **Step 2: Run to verify it fails** — filter `Core_metrics|Restore_returns_only`; expected assertion failures.
- [ ] **Step 3: Implement**: build the metric list `["usage","temperature","pump", ..Enumerable.Range(0,reading.CpuCoreCount).Select(i=>$"core/{i}/usage")]` and pass it to `Add`.
- [ ] **Step 4: Run to verify it passes** — expected 2 passed; run `GraphHistoryServiceTests` fully.
- [ ] **Step 5: Commit** — `git commit -m "feat: persist per-core CPU traces in six-hour graph history"`

### Task 5: Setting, session and Settings card

**Files:**
- Modify: `src/DeadWare.Core/Monitoring.cs:313` (after `ShowCpuPumpSpeed`), `src/DeadWare.App/UiSession.cs:133` (after `SetCpuPumpSpeed`), `src/DeadWare.App/Windows.cs:33,124-127,191,254`
- Test: `tests/DeadWare.Tests/CpuCoreSettingsTests.cs` (new), `tests/DeadWare.App.Tests/ShellUiChecks.cs` (extend the six-cards check)

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

- [ ] **Step 2: Run to verify they fail** — filter `CpuCoreSettingsTests`; expected build error. `ShellUiChecks` runs inside the STA fixture (`BatteryUiTests`); expected assertion failure.
- [ ] **Step 3: Implement** the property, the setter and the checkbox wiring mirroring `cpuPumpSpeed`.
- [ ] **Step 4: Run to verify they pass** — `dotnet test DeadWare.slnx -c Release --nologo --filter "CpuCoreSettingsTests|BatteryUiTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat: per-core CPU usage setting"`

### Task 6: Core tint brushes

**Files:**
- Modify: `src/DeadWare.App/Theme.cs:22-33`
- Test: `tests/DeadWare.App.Tests/ThemeTokenTests.cs` (add one test)

**Interfaces:**
- Produces: `Theme.CoreKey(int index) => $"Graph/cpucore/{index}"`; `ApplyGraphColors` also registers resources `Graph/cpucore/0..63` as frozen `SolidColorBrush`es computed by `internal static Color CoreTint(Color cpu, int index, int count)`: convert `cpu` to HSL, hue shifted by `−18° + 36° × index/(count−1)`, lightness moved toward the surface by `0.12 + 0.25 × index/(count−1)` (toward white in dark themes, toward black in Daylight), saturation −15%; `count == 1` returns the cpu colour itself. Under `SystemParameters.HighContrast` the tints are the CPU brush stepped only in lightness. `ApplyGraphColors` runs on theme change and on custom CPU colour change (both already call it), so tints follow the user's custom CPU colour.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public void Core_tints_are_distinct_in_every_theme()
{
    foreach(string theme in Theme.Names)            // the existing nine-theme list used by the token tests
    {
        var cpu=((SolidColorBrush)Theme.ThemeBrush(theme,"Cpu")).Color;   // reuse whatever accessor ThemeTokenTests already uses for a theme's palette colour
        foreach(int count in new[]{1,8,64})
        {
            var tints=Enumerable.Range(0,count).Select(i=>Theme.CoreTint(cpu,i,count)).ToArray();
            Assert.Equal(count,tints.Distinct().Count());
            Assert.All(tints,t=>Assert.True(Math.Abs(t.R-cpu.R)+Math.Abs(t.G-cpu.G)+Math.Abs(t.B-cpu.B)<=3*140,theme));
        }
        Assert.Equal(cpu,Theme.CoreTint(cpu,0,1));
    }
}
```

- [ ] **Step 2: Run to verify it fails** — filter `Core_tints`; expected build error.
- [ ] **Step 3: Implement** `CoreTint`, `CoreKey`, and the resource registration loop (64 brushes, frozen).
- [ ] **Step 4: Run to verify it passes** — filter `ThemeTokenTests`.
- [ ] **Step 5: Commit** — `git commit -m "feat: per-core tint brushes derived from the CPU graph colour"`

### Task 7: Core traces and hover in HistoryChart

**Files:**
- Modify: `src/DeadWare.App/Charts.cs:105-150` (ctor flags, Query/Prepare), `:194` (TraceSpec), `:240-296` (specs and draw order), `:298-316` (hover), `:341-356` (DrawTrace); `src/DeadWare.App/WidgetGraph.cs:23` (add `CoresWithDevices=true`)
- Test: `tests/DeadWare.App.Tests/LiveRenderingUiChecks.cs` (extend fixture and add checks)

**Interfaces:**
- Consumes: `Settings.ShowCpuCoreUsage`, `Reading.CpuCoreCount` via `session.Current`, `Theme.CoreKey`, `CpuCoreUsage.Key`.
- Produces:
  - `HistoryChart` gains `public bool CoresWithDevices {get;init;}` beside `PumpWithDevices`; `private bool ShowsCores()=>session.Settings.ShowCpuCoreUsage&&kind=="cpu"&&(!CompareDevices||CoresWithDevices)`.
  - `Query` gains `int Cores` (`ShowsCores() ? session.Current?.CpuCoreCount ?? 0 : 0`); `Prepare` reads `CpuCoreUsage.Key(i)` series for `i < Cores` exactly like the fan/pump metrics (smoothed ranges included).
  - `TraceSpec` gains `bool Core` (default false). Specs order: core specs first (so the total draws last), then the existing primary/secondary/fan/pump order unchanged.
  - `DrawTrace` for `Core`: width 0.5 × the normal width, skip the area, band stays, skip `UnderGlow`, skip `RuneTrace`, skip the end marker, `GraphStroke.Draw` with `dashed:false, dotted:false`.
  - `DrawHover`: collect `(k, value)` for core specs instead of adding them inline; after the non-core lines add up to 8 lines `Core {k} · {value:0}%` sorted descending by value using each core's own brush, then `+{N} more` in `Muted` when more cores had values. The hover dot is drawn only for non-core specs.
  - `internal static IReadOnlyList<(string Text,int Core)> HoverCoreLines(IReadOnlyList<(int Core,double Value)> cores)` is the pure helper that produces the texts (incl. the `+N more` line with `Core = -1`), so it can be unit-tested.

- [ ] **Step 1: Write the failing checks**

```csharp
[Fact] public void Hover_core_lines_take_the_eight_highest_and_count_the_rest()
{
    var lines=HistoryChart.HoverCoreLines(Enumerable.Range(0,12).Select(i=>(i,(double)i*5)).ToArray());
    Assert.Equal(9,lines.Count);Assert.Equal("Core 12 · 55%",lines[0].Text);Assert.Equal("Core 5 · 20%",lines[7].Text);Assert.Equal(("+4 more",-1),lines[8]);
    Assert.Equal(3,HistoryChart.HoverCoreLines([(0,1),(1,2),(2,3)]).Count);
}
```
In `LiveRenderingUiChecks.Run`: add `cpu/core/0..7/usage` series to the fixture (core 3 null on every 10th sample), set `CpuCoreCount=8` on the fixture `Reading`, and:
  - with `ShowCpuCoreUsage=false`, a `cpu` chart's rendered `DrawingGroup` has the same geometry-drawing count as before (record it first);
  - with `ShowCpuCoreUsage=true`, the count rises by exactly 8 stroked geometries and the core strokes have half the primary stroke's thickness;
  - a `cpu` chart with `CompareDevices=true` and the switch on draws no extra geometries, while one with `CompareDevices=true, CoresWithDevices=true` (the widget's shape) draws the 8 core strokes;
  - setting the switch back to `false` and re-rendering returns to the original count (Review Focus 3).

- [ ] **Step 2: Run to verify they fail** — filter `Hover_core_lines|BatteryUiTests`.
- [ ] **Step 3: Implement** per the Interfaces block. Build core specs with `new(CpuCoreUsage.Key(i),Theme.CoreKey(i),false,false,maximum,0){Core=true}`.
- [ ] **Step 4: Run to verify they pass** — same filter, then the full App test project.
- [ ] **Step 5: Commit** — `git commit -m "feat: draw per-core CPU traces with a bounded hover chip"`

### Task 8: Subtitle and status copy

**Files:**
- Modify: `src/DeadWare.App/Charts.cs:497` (overview card subtitle), `src/DeadWare.App/Windows.cs:264-273` (dashboard status line)
- Test: `tests/DeadWare.App.Tests/LiveRenderingUiChecks.cs` or `ShellUiChecks.cs` (one check each)

**Interfaces:**
- Consumes: `Reading.CpuCoreCount`, `Settings.ShowCpuCoreUsage`.
- Produces: CPU usage card subtitle `"{CpuName} · {N} cores"` when the switch is on and `N > 0` (kind `cpu` only, not `cputemp`); dashboard `status` text appends ` · Per-core CPU usage unavailable.` when the switch is on and `N == 0` and the reading is not null.

- [ ] **Step 1: Write the failing checks** — render the overview CPU card with the fixture reading (`CpuCoreCount=8`, switch on) and assert the subtitle ends with ` · 8 cores`; with `CpuCoreCount=0` assert the dashboard status contains `Per-core CPU usage unavailable.` and the subtitle has no `cores` suffix.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass** — `BatteryUiTests` filter.
- [ ] **Step 5: Commit** — `git commit -m "feat: core count in the CPU card subtitle and an unavailable notice"`

### Task 9: Release documentation and packaging

**Files:**
- Modify: `Directory.Build.props` (Version 0.10.0), `README.md` (Features bullet + a short paragraph under "Graph pop-outs" after the pump paragraph), `CHANGELOG.md` (new `## 0.10.0 — Per-core CPU usage` section at top), `docs/validation.md` (new section at top, same shape as the 0.9.0 entry), `docs/releases/v0.10.0.md` (new, same shape as `v0.9.1.md`)
- Verify: `tools/OverviewPreview` render

- [ ] **Step 1: Bump the version and write the docs.** CHANGELOG bullet copy: *Add **Settings → Show per-core CPU usage on usage graphs**. One thin line per core, tinted from the CPU graph colour, draws under the bold total on the dashboard CPU usage card, the separate CPU usage pop-out and the widget Usage graph; hover a graph to see the eight busiest cores. Core traces are saved in the six-hour graph history. Off by default; the joined CPU/GPU pop-out, Usage history and Alerts are unchanged.*
- [ ] **Step 2: Full build and suite** — `dotnet build DeadWare.slnx -c Release --nologo` (0 warnings) then `dotnet test DeadWare.slnx -c Release --nologo`; record the pass count in `docs/validation.md`.
- [ ] **Step 3: Render previews** — `dotnet run --project tools/OverviewPreview -c Release -- --render artifacts/ui-cpu-cores` with `ShowCpuCoreUsage` forced on in the preview fixture (add the switch to the preview's settings if the tool builds its own `UserSettings`); inspect Midnight, Blackout and Daylight frames; expected `"failures": []`.
- [ ] **Step 4: Commit** — `git commit -m "docs: per-core CPU usage release notes, changelog, validation and 0.10.0"`
- [ ] **Step 5: Push and open the pull request** against `main` from `claude/cpu-per-core`, title `Per-core CPU usage (0.10.0)`, body summarising the spec; verify CI green.
