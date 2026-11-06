# Processes (0.13.0, 0.13.1, 0.14.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a live Processes page (per-app CPU, memory, I/O, disk and network, End task and Open file location) behind an opt-in Per-process monitoring switch in 0.13.0, per-app Disk via DeadWare's own thread map in 0.13.1 only if the disk gate fails, and 30 days of saved per-app CPU, memory and I/O history on Usage history in 0.14.0.

**Architecture:** `WindowsProcesses` makes one `NtQuerySystemInformation(SystemProcessInformation)` call per poll (or per loop period, if CI's p95 says so); pure Core types parse the buffer (`ProcessSnapshotParser`), turn scans into rates and exact deltas (`ProcessActivityTracker`), join them to the poll's single network take and to a separate kernel disk session (`EtwDisk`, own attribution and ledger), and publish an immutable `Reading.ProcessActivity`. The page groups by image name under `OrdinalIgnoreCase`, sorts calmly and acts through Core `EndTaskPolicy` and an App native seam. 0.14.0 folds activities into minutes (`ProcessMinuteFolder`) and stores them in `process-history.db` (`ProcessHistoryStore`) behind a `ProcessHistoryService` worker.

**Tech Stack:** .NET 10, WPF, `ntdll!NtQuerySystemInformation`, Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0 (second kernel session), Microsoft.Data.Sqlite (existing), xUnit 2.9.3. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-05-processes-design.md`

## Global Constraints

- Every task's requirements include the spec; where this plan and the spec differ, the spec wins.
- Nothing builds or runs locally (no .NET SDK). The only verification is CI: `scripts/publish.ps1` on `windows-latest` runs `dotnet test DeadWare.slnx -c Release --nologo`. "Run (CI)" below means: `git push`, then `gh pr checks --watch`, then for failures `gh run view --log-failed | grep -E "Failed |error CS"`.
- `build.yml` cancels an in-progress run when a newer push arrives: wait for each run to finish before pushing again.
- Each task's Step 2 commits and pushes its failing tests (`test(...)` commit) and checks that CI fails for the stated reason; Step 4 commits and pushes the implementation and checks that CI is green; Step 5 confirms GitHub has the commit (`git fetch origin && git status -sb` shows no ahead/behind; `gh pr checks` green), as `AGENTS.md` requires.
- UI checks run inside the single STA fact `BatteryUiTests.Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows`; a UI check "passes in CI" when that fact passes, and "fails" when it fails during the named phase.
- Workflow: one feature branch per release with a pull request to `main`: `claude/processes-0.13.0` (Task 1), `claude/processes-disk-0.13.1` (Task 20), `claude/processes-history-0.14.0` (Task 22). `build.yml` runs on push only for `main`, `Dev`, `feat/**` and `fix/**`, so on these `claude/*` branches CI runs only through the pull request; that is why each PR is opened as a draft with the release's first push. Fetch and inspect the remote branch before starting; never force-push. The 0.13.0 PR is opened as a **draft with the first pushed task** (Task 1). Tasks 10–19 start only after Task 8 has recorded the measurements and decisions in the PR description. The PR is marked ready after Task 19.
- Per-process monitoring (`UserSettings.ProcessMonitoring`) is **off by default**, whatever the measurements show. `CollectorOptions.ProcessMonitoring` and `CollectorOptions.ProcessDiskTracing` default to `false`; tests that build real collectors leave both false.
- Column keys, in this fixed order after NAME: `pid, cpu, memory, io, ioread, iowrite, disk, diskread, diskwrite, network, download, upload`. Default: `cpu, memory, io, disk, network`. Administrator-only: `disk, diskread, diskwrite, network, download, upload`.
- Headers: `NAME · PID · CPU · MEMORY · I/O · I/O READ · I/O WRITE · DISK · DISK READ · DISK WRITE · NETWORK · ↓ DOWNLOAD · ↑ UPLOAD`.
- Identity: `(Pid, Generation)`, Generation = `CreateTime + 504911232000000000` (equals `DateTime.FromFileTimeUtc(created).Ticks`). App key: image name under `StringComparer.OrdinalIgnoreCase`; stored key `ToUpperInvariant(name)`.
- Null, never zero, for an unknown value; every "—" has a tooltip naming its reason. A missing administrator right hides columns instead of producing "—".
- Status text holds fixed copy, counts, NTSTATUS codes or exception type names, never process names or paths. Paths are read on demand and never stored or logged. Diagnostics get counts and timings only.
- Caps: 4,096 published processes per scan; 1 MiB initial / +256 KiB growth / 3 retries / 32 MiB scan buffer; 65,536 parsed entries; 30 history apps per minute plus Everything else; 128 MiB `process-history.db`, 30-day retention, reads ≤ 31 days and `LIMIT` ≤ 1000.
- Disk gate: ≥ 90 % of a 256 MB `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH` write credited to the test's own `(Pid, Generation)` at the chosen `BufferSizeMB`. Scan placement: CI p95 > 15 ms moves the scan to its own loop.
- All native work runs off the UI thread; `app.manifest` stays `asInvoker`; colours come from theme resources only.
- Real-Windows tests skip unless `GITHUB_ACTIONS == "true"`; checks also skip when not elevated; measurement tests never skip on GitHub and check elevation in their body.
- Versions and headings: `## 0.13.0 — Processes`, `## 0.13.1 — Per-app disk`, `## 0.14.0 — Application resource history`; `Directory.Build.props` `<Version>` bumped in each release task.

## Review Focus

1. Changing the polling interval while monitoring is on (1 s → 10 s and back): the first scan after the change must still produce rates when Δt is within `FreshnessSeconds` of the longer interval, never a stale null or a spike. Test in Task 2.
2. Toggling Per-process monitoring off and on quickly as administrator: the disk session must end in the state of the last request, with no session left running after Off. Test in Task 6.
3. Search text with surrounding spaces or a partial PID (`" 12"`): trimmed, and matched like the Network page (name contains, or PID text contains). Test in Task 11.
4. The selected app or process exits, or drops out of the 4,096 published set, while selected: the selection clears without an exception and End task… / Open file location disable. Test in Task 17.
5. Clicking a Device-chart point whose slot has no process data (monitoring was off): the card says so instead of showing an empty table, and Clear returns to the period ranking. Test in Task 27.

---

## 0.13.0 — Step 1: measurement tasks

### Task 1: Core scan models and `ProcessSnapshotParser`

**Files:**
- Create: `src/DeadWare.Core/ProcessModels.cs`, `src/DeadWare.Core/ProcessSnapshotParser.cs`, `src/DeadWare.Core/ProcessFeatures.cs`
- Test: `tests/DeadWare.Tests/ProcessSnapshotParserTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ProcessScanEntry(int Pid,long Generation,string Name,int SessionId,int Threads,long PrivateWorkingSet,long KernelTime,long UserTime,long ReadTransfer,long WriteTransfer){ public long CpuTime=>KernelTime+UserTime; }` (`Name` is the raw image name, `""` when empty).
  - `public sealed record ProcessScan(DateTimeOffset UtcTime,double MonotonicSeconds,double PollingIntervalSeconds,IReadOnlyList<ProcessScanEntry> Processes,int Threads,int BufferBytes);`
  - `public static class ProcessSnapshotParser { public const int FixedSize=0x100,ThreadEntrySize=0x50,MaximumEntries=65536; public const long GenerationOffset=504911232000000000; public static bool TryParse(ReadOnlySpan<byte> buffer,ulong bufferAddress,IReadOnlyDictionary<(int Pid,long Generation),string>? knownNames,out IReadOnlyList<ProcessScanEntry> entries,out int threads,out string? error); }`
  - `public static class ProcessFeatures { public static bool DiskColumns {get;}=false; public static bool ScanLoop {get;}=false; public static int DiskBufferMultiplier {get;}=1; }` (Task 8 sets the values; get-only statics avoid unreachable-code warnings).

Offsets (x64, little-endian): 0x00 NextEntryOffset (u32), 0x04 NumberOfThreads (u32), 0x08 WorkingSetPrivateSize (i64), 0x20 CreateTime, 0x28 UserTime, 0x30 KernelTime, 0x38 ImageName.Length (u16), 0x40 ImageName.Buffer (pointer; offset in buffer = pointer − `bufferAddress`), 0x50 UniqueProcessId (pointer-sized), 0x64 SessionId (u32), 0xE8 ReadTransferCount, 0xF0 WriteTransferCount. Fails the whole scan (returns false, `error` set): an entry whose `0x100 + threads × 0x50` bytes do not fit in the buffer (including the last); a non-zero `NextEntryOffset` smaller than that or reaching past the buffer; more than 65,536 entries; an odd name length; a non-empty name outside the buffer. `NextEntryOffset == 0` ends the walk. PID 0 is checked like any entry, then skipped (not returned, threads still counted). A name is decoded only when its `(Pid, Generation)` is absent from `knownNames`.

- [ ] **Step 0: Fetch, inspect and create the 0.13.0 branch**

```bash
git fetch origin
git status --short --branch
git log --oneline origin/main..HEAD   # only the spec and plan commits; anything else is unrelated work to preserve, not to carry
git switch -c claude/processes-0.13.0   # from the branch that holds the spec and plan commits on top of origin/main; rebase it onto origin/main first if main moved
```
Expected: a clean tree; the new branch is `origin/main` plus the spec and plan commits. It is a `claude/*` branch, so pushes run CI only through the PR opened in Step 2.

- [ ] **Step 1: Write the failing tests**

`ScanBuffer` is a test helper that lays out entries at the offsets above (name UTF-16 placed after the thread entries, pointer = `Base + offset`), with `Base = 0x10_0000`.

```csharp
[Fact] public void Reads_every_field_at_its_x64_offset(){ var b=ScanBuffer.Of(Entry(4,"System",threads:3,create:100,user:7,kernel:9,ws:4096,read:11,write:12,session:0));
    Assert.True(ProcessSnapshotParser.TryParse(b,Base,null,out var e,out int threads,out _));var s=Assert.Single(e);
    Assert.Equal((4,100+ProcessSnapshotParser.GenerationOffset,"System",0,3,4096L,9L,7L,11L,12L),(s.Pid,s.Generation,s.Name,s.SessionId,s.Threads,s.PrivateWorkingSet,s.KernelTime,s.UserTime,s.ReadTransfer,s.WriteTransfer));Assert.Equal(3,threads);}
[Fact] public void Idle_entry_with_empty_name_passes_checks_and_is_dropped(){ Assert.True(ProcessSnapshotParser.TryParse(ScanBuffer.Of(Entry(0,"",threads:8),Entry(4,"System")),Base,null,out var e,out int t,out _));
    Assert.Equal([4],e.Select(x=>x.Pid));Assert.Equal(9,t);}
[Fact] public void Zero_next_offset_ends_the_walk()=>Assert.Equal(2,Parse(ScanBuffer.Of(Entry(4,"System"),Entry(8,"a.exe"))).Count);
[Fact] public void Last_entry_whose_threads_overrun_the_buffer_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System",threads:1000)).Truncate(0x100+0x50));
[Fact] public void Next_offset_smaller_than_the_entry_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System",threads:2),Entry(8,"a.exe")).WithNext(0,0x100));
[Fact] public void Next_offset_past_the_buffer_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System")).WithNext(0,1<<20));
[Fact] public void Odd_name_length_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System")).WithNameLength(0,11));
[Fact] public void Name_outside_the_buffer_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System")).WithNamePointer(0,Base+(1UL<<30)));
[Fact] public void More_than_65536_entries_fails()=>AssertFails(ScanBuffer.Of(Enumerable.Range(1,65537).Select(i=>Entry(i*4,"")).ToArray()));
[Fact] public void Known_names_are_reused_without_decoding(){ var gen=100+ProcessSnapshotParser.GenerationOffset;
    ProcessSnapshotParser.TryParse(ScanBuffer.Of(Entry(8,"fresh.exe",create:100)),Base,new Dictionary<(int,long),string>{[(8,gen)]="cached.exe"},out var e,out _,out _);Assert.Equal("cached.exe",e[0].Name);}
```

- [ ] **Step 2: Commit the failing tests, push, open the draft PR and verify failure**

```bash
git add tests/DeadWare.Tests/ProcessSnapshotParserTests.cs
git commit -m "test(core): process snapshot parser"
git push -u origin HEAD
gh pr create --draft --base main --title "Processes: live per-app CPU, memory, I/O, disk and network (0.13.0)" --body "Implements docs/superpowers/plans/2026-11-05-processes.md. Measurements and decisions are recorded here after Task 7's CI run."
```
Run (CI). Expected: build fails with `error CS0103`/`CS0246` for `ProcessSnapshotParser`.

- [ ] **Step 3: Implement the records, `ProcessFeatures` and `ProcessSnapshotParser.TryParse`**

Use `BinaryPrimitives.ReadUInt32LittleEndian`/`ReadInt64LittleEndian` over the span; decode names with `MemoryMarshal.Cast<byte,char>` and `new string(...)`.

- [ ] **Step 4: Commit, push and verify pass**

```bash
git add src/DeadWare.Core/ProcessModels.cs src/DeadWare.Core/ProcessSnapshotParser.cs src/DeadWare.Core/ProcessFeatures.cs
git commit -m "feat(core): parse SYSTEM_PROCESS_INFORMATION into process scan entries"
```
Run (CI). Expected: green; every `ProcessSnapshotParserTests` test passes.

- [ ] **Step 5: Confirm GitHub has the commit and the draft PR check is green**

---

### Task 2: `ProcessActivityTracker` and the published activity model

**Files:**
- Modify: `src/DeadWare.Core/ProcessModels.cs` (activity records)
- Create: `src/DeadWare.Core/ProcessActivityTracker.cs`
- Test: `tests/DeadWare.Tests/ProcessActivityTrackerTests.cs`

**Interfaces:**
- Consumes: Task 1 `ProcessScan`, `ProcessScanEntry`; `PollingPolicy.FreshnessSeconds`; `ProcessIdentity` (`Recovery.cs:2`).
- Produces:
  - `public enum ProcessGap {None,FirstSample,NotMatched}`
  - `public sealed record ProcessDeltas(long? CpuTime,long? IoReadBytes,long? IoWriteBytes,double MemoryByteSeconds,double Seconds){ public static ProcessDeltas Zero{get;} public ProcessDeltas Add(ProcessDeltas other); }` (CPU in 100 ns units; `Add` treats a null side as 0 only when the other is known).
  - `public sealed record ProcessSample(int Pid,long Generation,string Name,int SessionId,double? CpuPercent,long PrivateBytes,double? IoReadPerSecond,double? IoWritePerSecond,ProcessGap RateGap,ProcessDeltas? Deltas){ public double? DownloadPerSecond{get;init;} public double? UploadPerSecond{get;init;} public ProcessGap NetworkGap{get;init;} public double? DiskReadPerSecond{get;init;} public double? DiskWritePerSecond{get;init;} public ProcessGap DiskGap{get;init;} }`
  - `public sealed record ProcessOmitted(int Count,long PrivateBytes,ProcessDeltas Deltas){ public static ProcessOmitted None{get;} }`
  - `public sealed record ProcessColumnState(bool Available,string Status){ public static ProcessColumnState Ready{get;}=new(true,""); }`
  - `public enum ProcessActivityKind {None,Off,Unavailable,Available}`
  - `public sealed record ProcessActivity(ProcessActivityKind Kind,long Sequence,DateTimeOffset Timestamp,double MonotonicSeconds,double IntervalSeconds,int ProcessorCount,IReadOnlyList<ProcessSample> Processes,ProcessOmitted Omitted,int TotalProcesses,double? DeltaSeconds){ public ProcessColumnState Scan{get;init;} public ProcessColumnState Disk{get;init;} public ProcessColumnState Network{get;init;} public int NetworkMisses{get;init;} public int DiskMisses{get;init;} public long UnattributedDiskEvents{get;init;} public static ProcessActivity None{get;} public static ProcessActivity Off{get;} public static ProcessActivity Unavailable(string status); }` — `Scan`, `Disk` and `Network` default to `ProcessColumnState.Ready`; `Unavailable(status)` sets `Scan=new(false,status)`.
  - `public sealed record TrackedScan(ProcessScan Scan,IReadOnlyList<ProcessSample> Processes,ProcessOmitted Omitted,double? DeltaSeconds,int TotalProcesses,IReadOnlyDictionary<int,ProcessIdentity> Index);` (`Index` covers every scanned process, not only the published ones.)
  - `public sealed class ProcessActivityTracker(int processorCount){ public const int MaximumPublished=4096; public TrackedScan Update(ProcessScan scan); public void Reset(); public int TrackedCount{get;} }`

Rules (spec, Rates): CPU % = Δ(kernel+user) / (Δt × 10⁷ × N) × 100. Memory is published on every successful scan. CPU %, I/O rates and the ΔCPU/ΔI/O deltas are null when: no previous scan; Δt ≤ 0 or Δt > `PollingPolicy.FreshnessSeconds(max(previous.PollingIntervalSeconds, scan.PollingIntervalSeconds))`; a counter went backwards; or the process is missing from the previous scan and its CreateTime is more than 1 s before that scan's `UtcTime`. A missing process whose CreateTime is ≥ previous `UtcTime` − 1 s is credited in full. `RateGap=FirstSample` for every null-rate case. `Deltas` is null when the scan has no valid Δt; otherwise `MemoryByteSeconds = PrivateWorkingSet × Δt`, `Seconds = Δt`. Cap order: CPU % desc, I/O (read+write) desc, private working set desc (null as 0), PID asc; the rest go to `Omitted`. Baselines hold only the current scan's keys.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void First_scan_has_null_rates_and_memory(){ var t=new ProcessActivityTracker(8);var r=t.Update(Scan(0,Entry(8,cpu:0,ws:4096)));
    var s=r.Processes.Single();Assert.Null(s.CpuPercent);Assert.Null(s.IoReadPerSecond);Assert.Equal(4096,s.PrivateBytes);Assert.Equal(ProcessGap.FirstSample,s.RateGap);Assert.Null(s.Deltas);Assert.Null(r.DeltaSeconds);}
[Fact] public void One_busy_thread_on_eight_processors_reads_twelve_and_a_half_percent(){ var t=new ProcessActivityTracker(8);t.Update(Scan(0,Entry(8,cpu:0)));
    Assert.Equal(12.5,t.Update(Scan(1,Entry(8,cpu:10_000_000))).Processes[0].CpuPercent!.Value,6);}
[Fact] public void Pid_reuse_starts_a_new_baseline(){ /* pid 8 gen A cpu 5e6, then pid 8 gen B created before previous UtcTime-1s */ Assert.Null(second.Processes[0].CpuPercent);}
[Fact] public void Process_started_during_the_interval_is_credited_in_full(){ /* created at previous UtcTime-0.5 s, cpu 2e6, read 1000 */ Assert.Equal(2_000_000,s.Deltas!.CpuTime);Assert.Equal(1000,s.Deltas.IoReadBytes);}
[Fact] public void Running_process_missed_by_the_previous_scan_is_null(){ /* created 10 s before previous UtcTime */ Assert.Null(s.CpuPercent);Assert.Null(s.Deltas!.CpuTime);}
[Theory][InlineData(0.0)][InlineData(-1.0)][InlineData(4.01)] public void Delta_t_outside_freshness_is_null(double dt){ /* interval 1 s → freshness 4 s */ Assert.Null(Second(dt).Processes[0].CpuPercent);}
[Fact] public void Counter_going_backwards_is_null()=>Assert.Null(Backwards().Processes[0].IoWritePerSecond);
[Fact] public void Interval_change_uses_the_longer_interval_for_freshness(){ /* previous scan interval 1, next 10, Δt 9 s → freshness 13 s */ Assert.NotNull(r.Processes[0].CpuPercent);}
[Fact] public void Cap_keeps_4096_ranked_by_cpu_then_io_then_memory_then_pid(){ /* 4100 processes */ Assert.Equal(4096,r.Processes.Count);Assert.Equal(4,r.Omitted.Count);Assert.Equal(4100,r.TotalProcesses);Assert.Equal(expectedFirstPids,r.Processes.Take(3).Select(p=>p.Pid));}
[Fact] public void All_null_first_scan_keeps_the_largest_by_memory(){ Assert.DoesNotContain(r.Processes,p=>p.PrivateBytes<smallestKept);Assert.Equal(omittedMemory,r.Omitted.PrivateBytes);}
[Fact] public void Omitted_carries_summed_exact_deltas(){ Assert.Equal(sumCpu,r.Omitted.Deltas.CpuTime);}
[Fact] public void Index_covers_every_scanned_process()=>Assert.Equal(4100,r.Index.Count);
[Fact] public void Ten_thousand_create_exit_cycles_keep_baselines_bounded(){ for(int i=0;i<10_000;i++)t.Update(Scan(i,Entry(1000+i)));Assert.True(t.TrackedCount<=1);}
[Fact] public void Reset_returns_to_first_sample()=>Assert.Null(AfterReset().Processes[0].CpuPercent);
[Fact] public void Off_and_unavailable_instances(){ Assert.Equal(ProcessActivityKind.Off,ProcessActivity.Off.Kind);var u=ProcessActivity.Unavailable("IOException");Assert.Equal("IOException",u.Scan.Status);Assert.False(u.Scan.Available);}
```

- [ ] **Step 2: Commit the failing tests, push and verify failure**

Run (CI). Expected: `error CS0246` for `ProcessActivityTracker`.

- [ ] **Step 3: Implement the records and `ProcessActivityTracker.Update`/`Reset`**

Keep per-key baselines in a `Dictionary<(int,long),Baseline>` replaced each scan; keep the previous scan's `UtcTime`, `MonotonicSeconds` and interval.

- [ ] **Step 4: Commit, push and verify pass**

Run (CI). Expected: green; `ProcessActivityTrackerTests` pass.

- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 3: Network and disk joins, `DiskRateWindow`, `ProcessAttribution.Reconcile(index)`

**Files:**
- Create: `src/DeadWare.Core/ProcessJoins.cs`, `src/DeadWare.Core/DiskRateWindow.cs`
- Modify: `src/DeadWare.Core/Recovery.cs:35-38` (add the overload beside `Reconcile()`)
- Test: `tests/DeadWare.Tests/ProcessJoinTests.cs`

**Interfaces:**
- Consumes: Task 2 `ProcessSample`, `ProcessColumnState`, `ProcessGap`; `ProcessTraffic` (`Monitoring.cs:318`).
- Produces:
  - `public enum DiskTraceState {Off,Starting,Running,Failed,Paused}`
  - `public sealed record DiskProcessTotal(int Pid,long Generation,long BytesRead,long BytesWritten);`
  - `public sealed record DiskProcessRate(int Pid,long Generation,double Read,double Write);`
  - `public sealed record DiskTake(DiskTraceState State,string Status,bool Lossy,bool Warm,IReadOnlyList<DiskProcessRate> Rates,double UnattributedShare,long UnattributedEvents){ public static DiskTake Off{get;} }`
  - `public sealed record ProcessJoinResult(IReadOnlyList<ProcessSample> Processes,ProcessColumnState State,int Misses);`
  - `public static class ProcessJoins { public const int MinimumMatchedRows=4; public const string NetworkUnmatched="Per-app traffic could not be matched to processes"; public const string DiskUnmatched="Per-app disk activity could not be matched to processes"; public static ProcessJoinResult Network(IReadOnlyList<ProcessSample> processes,IReadOnlyList<ProcessTraffic> capture,bool captureAvailable,string captureStatus); public static ProcessJoinResult Disk(IReadOnlyList<ProcessSample> processes,DiskTake take); }`
  - `public sealed class DiskRateWindow(double minimumSeconds=2){ public void Reset(double now); public void Observe(double now,IReadOnlyList<DiskProcessTotal> totals,long events,long unattributed); public bool Warm{get;} public IReadOnlyList<DiskProcessRate> Rates(); public double UnattributedShare{get;} }`
  - `ProcessAttribution.Reconcile(Func<int,ProcessIdentity?> index)`

Join tables are the spec's (Network column, Disk column): first matching case applies. Network: match → published rates (null on first take, `NetworkGap=FirstSample`); PID absent while available → 0; PID present under another Generation → null, `NetworkGap=NotMatched`, counted; unavailable → null with `captureStatus`. Miss guard: when ≥ 4 capture rows have their PID in the scan and > 50 % of them miss, the column state is `(false, NetworkUnmatched)` and every value is null. Disk adds: state not Running or `Lossy` → null with `take.Status`; `!Warm` → null, `DiskGap=FirstSample` for every row; and the miss guard also trips when `UnattributedShare > 0.5`. `DiskRateWindow` rates use the newest observation that is at least `minimumSeconds` older than the latest; a key absent from that older observation counts from 0; `Warm` is false until such an observation exists since the last `Reset`. `Reconcile(index)`: a tracked PID whose `index(pid)?.Generation` equals its Generation is kept without calling the constructor's resolver; otherwise `Refresh(pid)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Network_match_absent_and_other_generation(){ var r=ProcessJoins.Network([P(8,1),P(9,1),P(10,1)],[T(8,1,100,50),T(10,2,1,1)],true,"");
    Assert.Equal(100,r.Processes[0].DownloadPerSecond);Assert.Equal(0,r.Processes[1].DownloadPerSecond);Assert.Null(r.Processes[2].DownloadPerSecond);Assert.Equal(ProcessGap.NotMatched,r.Processes[2].NetworkGap);Assert.Equal(1,r.Misses);Assert.True(r.State.Available);}
[Fact] public void Network_first_take_is_null_first_sample()=>Assert.Equal(ProcessGap.FirstSample,ProcessJoins.Network([P(8,1)],[T(8,1,null,null)],true,"").Processes[0].NetworkGap);
[Fact] public void Network_unavailable_carries_capture_status(){ var r=ProcessJoins.Network([P(8,1)],[],false,"Per-app ETW unavailable (X)");Assert.Null(r.Processes[0].DownloadPerSecond);Assert.Equal("Per-app ETW unavailable (X)",r.State.Status);}
[Fact] public void Majority_misses_over_four_rows_make_network_unavailable(){ var r=ProcessJoins.Network(Five(),FiveWithThreeMisses(),true,"");Assert.False(r.State.Available);Assert.Equal(ProcessJoins.NetworkUnmatched,r.State.Status);Assert.All(r.Processes,p=>Assert.Null(p.DownloadPerSecond));}
[Fact] public void Three_rows_never_trip_the_guard()=>Assert.True(ProcessJoins.Network(Three(),ThreeAllMiss(),true,"").State.Available);
[Fact] public void Disk_warm_up_is_first_sample_for_every_row(){ var r=ProcessJoins.Disk([P(8,1),P(9,1)],Take(warm:false));Assert.All(r.Processes,p=>{Assert.Null(p.DiskReadPerSecond);Assert.Equal(ProcessGap.FirstSample,p.DiskGap);});}
[Fact] public void Lossy_take_nulls_with_status()=>Assert.Equal("Windows dropped disk events",ProcessJoins.Disk([P(8,1)],Take(lossy:true,status:"Windows dropped disk events")).State.Status);
[Fact] public void Disk_match_absent_and_miss(){ var r=ProcessJoins.Disk([P(8,1),P(9,1),P(10,1)],Take(rates:[R(8,1,read:500,write:2000),R(10,2,read:1,write:1)]));
    Assert.Equal(500,r.Processes[0].DiskReadPerSecond);Assert.Equal(2000,r.Processes[0].DiskWritePerSecond);Assert.Equal(0,r.Processes[1].DiskReadPerSecond);Assert.Equal(0,r.Processes[1].DiskWritePerSecond);
    Assert.Null(r.Processes[2].DiskReadPerSecond);Assert.Equal(ProcessGap.NotMatched,r.Processes[2].DiskGap);Assert.Equal(1,r.Misses);Assert.True(r.State.Available);}
[Fact] public void Majority_unattributed_events_make_disk_unavailable()=>Assert.Equal(ProcessJoins.DiskUnmatched,ProcessJoins.Disk([P(8,1)],Take(unattributedShare:0.51)).State.Status);
[Fact] public void Window_needs_two_seconds_and_restarts_after_reset(){ var w=new DiskRateWindow();w.Observe(0,[D(8,1,0,0)],0,0);w.Observe(1,[D(8,1,0,1000)],1,0);Assert.False(w.Warm);
    w.Observe(2,[D(8,1,0,4000)],2,0);Assert.True(w.Warm);Assert.Equal(2000,w.Rates().Single().Write);w.Reset(2.5);Assert.False(w.Warm);}
[Fact] public void Window_counts_a_new_key_from_zero(){ /* key appears in the latest observation only */ Assert.Equal(500,rate.Read);}
[Fact] public void Window_share_of_unattributed_events(){ /* 10 events, 6 unattributed in window */ Assert.Equal(0.6,w.UnattributedShare,6);}
[Fact] public void Reconcile_with_index_keeps_matching_generation_without_a_native_read(){ int reads=0;var a=new ProcessAttribution(pid=>{reads++;return new(pid,1,"a");});a.Refresh(8);reads=0;
    a.Reconcile(pid=>new(pid,1,"a"));Assert.Equal(0,reads);}
[Theory][InlineData(true,0L)][InlineData(false,0L)][InlineData(false,2L)] public void Reconcile_with_index_rereads_missing_older_or_newer(bool missing,long indexGeneration){ /* tracked gen 1; index lacks pid or lists gen 0/2 */ Assert.Equal(1,reads);}
```
`Take(...)` builds a `DiskTake` defaulting to `State:Running,Status:"",Lossy:false,Warm:true,Rates:[],UnattributedShare:0,UnattributedEvents:0`; `R(pid,gen,read,write)` builds a `DiskProcessRate`.

- [ ] **Step 2: Commit the failing tests, push and verify failure**

Run (CI). Expected: `error CS0246` for `ProcessJoins`, `DiskRateWindow`; `CS1501` for `Reconcile`.

- [ ] **Step 3: Implement `ProcessJoins`, `DiskRateWindow` and the `Reconcile(index)` overload**

`Reconcile(index)` takes `gate` like `Reconcile()`; `EtwNetwork` keeps calling `Reconcile()`.

- [ ] **Step 4: Commit, push and verify pass**

Run (CI). Expected: green; `ProcessJoinTests` and the existing `RecoveryTests` pass.

- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 4: `WindowsProcesses`, the monitoring switch and publication on all three paths

**Files:**
- Create: `src/DeadWare.App/WindowsProcesses.cs`
- Modify: `src/DeadWare.Core/PollingPolicy.cs:3` (`CollectorOptions` gains `bool ProcessMonitoring=false,bool ProcessDiskTracing=false`)
- Modify: `src/DeadWare.App/Collector.cs` (`Reading` at 11–18; constructors 84–85; switch beside `SetPollingInterval` at 88; first try block before `lock(publicationGate)` at 221; second block after `etw.TakeSnapshot` at 225 and before `new Reading` at 233; failed path 237–245; stopped path 251–258; hardware status 211–217)
- Test: `tests/DeadWare.App.Tests/WindowsProcessesTests.cs`, `tests/DeadWare.App.Tests/CollectorProcessTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:
  - `internal interface IProcessSource { int ProcessorCount{get;} ProcessScan? Scan(double monotonicSeconds,double pollingIntervalSeconds); string Status{get;} }` (null scan = failure; `Status` holds the paced failure text).
  - `internal sealed class WindowsProcesses : IProcessSource { internal const int InitialBufferBytes=1<<20,GrowthBytes=256*1024,MaximumBufferBytes=32<<20,MaximumRetries=3; internal static int? NextBufferSize(int returnedBytes); internal static bool ShouldAttempt(double now,double? lastFailure); internal static string FailureStatus(uint ntStatus); internal static string PacedStatus(double now,double? failingSince,string failure); }` — `FailureStatus(0xC0000004)` = `"Process scan unavailable (NTSTATUS 0xC0000004)"`; `Status` is `PacedStatus(<the monotonicSeconds of the last Scan call>, <monotonic time of the first failure of the current run of failures, null after a success>, <that run's latest FailureStatus>)`, which returns `failure` once `now − failingSince ≥ 60` and `""` otherwise (the clock is the `monotonicSeconds` the collector passes to `Scan`, so tests drive it).
  - `Reading` gains `[JsonIgnore] public ProcessActivity ProcessActivity{get;init;}=ProcessActivity.None;`.
  - `Collector.SetProcessMonitoring(bool enabled)`, `public bool ProcessMonitoring{get;}`; the constructor seeds the volatile flag from `options.ProcessMonitoring` as it seeds the polling interval.
  - `internal Collector(CollectorOptions? options,IStorageFastSource? storage,IStorageHealthSource? healthSource=null,TimeSpan? healthPeriod=null,IProcessSource? processes=null)`; the public constructor passes `new WindowsProcesses()`.

The worker builds its `ProcessActivityTracker(processes.ProcessorCount)` once at worker start, before the poll loop, so an exception from it reaches the stopped path (the outer `catch` at 251–258); tests use that as the stopped-path seam.

Native details: one `NtQuerySystemInformation(5, buffer, length, out returned)` into a reused `GC.AllocateUninitializedArray<byte>(size, pinned: true)`; on `0xC0000004` grow to `NextBufferSize(returned)` (null past 32 MiB) and retry, at most 3 times per scan; x64 only (`IntPtr.Size == 8`, else `Status` = `"Process scan needs 64-bit Windows"`); after a failure, attempts at most once a minute. `ProcessorCount` = `GetActiveProcessorCount(0xFFFF)`. Names from the previous successful scan are passed as `knownNames`. Pass flow: read the flag once at the pass start; block 1 (before the lock): flag off → `tracker.Reset()`; on → `Scan` then `tracker.Update`; any exception is caught and kept as its type name. Block 2 (inside the lock, right after the only `etw.TakeSnapshot`): skipped when block 1 failed; otherwise `ProcessJoins.Network(tracked.Processes, capture.Processes, capture.Connections.Available, EtwStatus)` and build `ProcessActivity(Kind.Available, ++sequence, …)`; its own exception gives `Unavailable(type)`. Flag off publishes `ProcessActivity.Off`. Neither block wraps `new Reading`, `History.Add` or `SampleAvailable`. Failed-pass and stopped paths set `ProcessActivity = flag ? Unavailable(e.GetType().Name) : Off`. Timing keys `windows/processes` (block 1) and `processes/join` (block 2) when the scan ran. Append a non-empty `WindowsProcesses.Status` to `hardwareStatus`.

- [ ] **Step 1: Write the failing tests**

```csharp
// WindowsProcessesTests
[Fact] public void Buffer_grows_to_returned_plus_256_KiB_and_stops_at_32_MiB(){ Assert.Equal(2_000_000+262_144,WindowsProcesses.NextBufferSize(2_000_000));Assert.Null(WindowsProcesses.NextBufferSize(33_000_000));}
[Fact] public void Failure_retries_at_most_once_a_minute(){ Assert.False(WindowsProcesses.ShouldAttempt(30,0));Assert.True(WindowsProcesses.ShouldAttempt(60,0));Assert.True(WindowsProcesses.ShouldAttempt(0,null));}
[Fact] public void Status_names_the_ntstatus_in_hex()=>Assert.Equal("Process scan unavailable (NTSTATUS 0xC0000004)",WindowsProcesses.FailureStatus(0xC0000004));
[Fact] public void Status_appears_only_after_60_s_of_continuous_failure(){ const string f="Process scan unavailable (NTSTATUS 0xC0000004)";
    Assert.Equal("",WindowsProcesses.PacedStatus(59.9,0,f));Assert.Equal(f,WindowsProcesses.PacedStatus(60,0,f));Assert.Equal(f,WindowsProcesses.PacedStatus(500,0,f));Assert.Equal("",WindowsProcesses.PacedStatus(500,null,f));Assert.Equal("",WindowsProcesses.PacedStatus(130,100,f));}

// CollectorProcessTests (HardwareEnabled:false, EtwEnabled:false, interval 0.5; FakeProcesses yields scripted scans with rising CPU and counts calls;
// ProcessorCountThrows throws InvalidCastException from ProcessorCount; Start/NextWhere/NextReadingAfter subscribe to SampleAvailable with a 10 s timeout;
// FirstWith(source, options = new(ProcessMonitoring:true)) returns the first published reading)
[Fact] public async Task Monitoring_is_off_by_default_and_never_scans(){ var (r,fake)=await Third(new());Assert.Equal(ProcessActivityKind.Off,r.ProcessActivity.Kind);Assert.Equal(0,fake.Calls);}
[Fact] public async Task Fake_scan_publishes_activity_and_keeps_device_metrics(){ var (r,_)=await Second(new(ProcessMonitoring:true));
    Assert.Equal(ProcessActivityKind.Available,r.ProcessActivity.Kind);Assert.NotEmpty(r.ProcessActivity.Processes);Assert.True(r.Sample.Values.ContainsKey("cpu/usage"));Assert.True(r.Sample.Values.ContainsKey("memory/usage"));}
[Fact] public async Task Throwing_source_publishes_unavailable_and_keeps_cpu_and_memory(){ var r=await FirstWith(new ThrowingProcesses());
    Assert.Equal(ProcessActivityKind.Unavailable,r.ProcessActivity.Kind);Assert.Equal("InvalidOperationException",r.ProcessActivity.Scan.Status);Assert.True(r.Sample.Values.ContainsKey("cpu/usage"));Assert.True(r.Sample.Values.ContainsKey("memory/usage"));}
[Fact] public async Task Switching_off_takes_effect_within_one_poll_and_clears_baselines(){ var (c,fake)=Start(new(ProcessMonitoring:true));var rated=await NextWhere(c,r=>r.ProcessActivity.Processes.Any(p=>p.CpuPercent is not null));
    c.SetProcessMonitoring(false);int callsAtOff=fake.Calls;var next=await NextReadingAfter(c,rated);Assert.Equal(ProcessActivityKind.Off,next.ProcessActivity.Kind);Assert.False(c.ProcessMonitoring);
    var later=await NextReadingAfter(c,next);Assert.Equal(ProcessActivityKind.Off,later.ProcessActivity.Kind);Assert.True(fake.Calls<=callsAtOff+1);
    c.SetProcessMonitoring(true);var afterOn=await NextWhere(c,r=>r.ProcessActivity.Kind==ProcessActivityKind.Available&&r.Sample.MonotonicSeconds>later.Sample.MonotonicSeconds);
    Assert.All(afterOn.ProcessActivity.Processes,p=>{Assert.Equal(ProcessGap.FirstSample,p.RateGap);Assert.Null(p.CpuPercent);});}
[Fact] public async Task Stopped_path_publishes_unavailable_or_off(){ var on=await FirstWith(new ProcessorCountThrows(),new(ProcessMonitoring:true));Assert.StartsWith("Collector stopped (InvalidCastException)",on.HardwareStatus);
    Assert.Equal(ProcessActivityKind.Unavailable,on.ProcessActivity.Kind);Assert.Equal("InvalidCastException",on.ProcessActivity.Scan.Status);
    var off=await FirstWith(new ProcessorCountThrows(),new());Assert.StartsWith("Collector stopped",off.HardwareStatus);Assert.Equal(ProcessActivityKind.Off,off.ProcessActivity.Kind);}
[Fact] public async Task Failed_pass_publishes_unavailable_with_the_exception_type(){ /* FakeStorage whose Drives throws NotSupportedException, monitoring on */ Assert.Equal("NotSupportedException",r.ProcessActivity.Scan.Status);}
[Fact] public async Task Failed_pass_with_monitoring_off_publishes_off()=>Assert.Equal(ProcessActivityKind.Off,r.ProcessActivity.Kind);
[Fact] public async Task Sequence_increases_per_published_activity()=>Assert.True(second.ProcessActivity.Sequence>first.ProcessActivity.Sequence);
```

- [ ] **Step 2: Commit the failing tests, push and verify failure**

Run (CI). Expected: `error CS0246` for `WindowsProcesses`/`IProcessSource`, `CS1061` for `ProcessActivity`.

- [ ] **Step 3: Implement `WindowsProcesses`, the options, the switch and the two blocks in `RunAsync`**

- [ ] **Step 4: Commit, push and verify pass**

Run (CI). Expected: green; `WindowsProcessesTests`, `CollectorProcessTests`, `CollectorStorageTests` and `CollectorPollingTests` pass.

- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 5: `ProcessSummary` and `--diagnose` options

**Files:**
- Modify: `src/DeadWare.Core/ProcessModels.cs` (`ProcessSummary`)
- Modify: `src/DeadWare.App/Collector.cs` (`Reading.ProcessSummary`, set on all three paths when `options.DiagnosticTimings`)
- Modify: `src/DeadWare.App/App.xaml.cs:118` (options from a testable helper)
- Test: `tests/DeadWare.App.Tests/DiagnosticOptionsTests.cs`

**Interfaces:**
- Consumes: Task 2 `ProcessActivity`, Task 1 `ProcessFeatures.DiskColumns`.
- Produces:
  - `public sealed record ProcessSummary(string State,int Processes,int Omitted,ProcessColumnState Scan,ProcessColumnState Disk,ProcessColumnState Network,int NetworkMisses,int DiskMisses,long UnattributedDiskEvents){ public static ProcessSummary From(ProcessActivity activity); }` — `State` is the `Kind` name (`"Off"` for Off).
  - `Reading` gains `public ProcessSummary? ProcessSummary{get;init;}` (serialized, not `[JsonIgnore]`).
  - `internal static CollectorOptions DiagnosticOptions(string[] args,bool elevated)` on `App`: `HardwareEnabled=!--no-hardware`, `EtwEnabled=!--no-etw`, `DiagnosticTimings=true`, `ProcessMonitoring=!--no-processes`, `ProcessDiskTracing=ProcessFeatures.DiskColumns&&elevated&&!--no-processes&&!--no-etw`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Diagnose_runs_the_scan_unless_no_processes(){ Assert.True(App.DiagnosticOptions(["--diagnose","o.json"],false).ProcessMonitoring);Assert.False(App.DiagnosticOptions(["--diagnose","o.json","--no-processes"],false).ProcessMonitoring);}
[Theory][InlineData(new[]{"--diagnose","o"},false,false)][InlineData(new[]{"--diagnose","o","--no-etw"},true,false)][InlineData(new[]{"--diagnose","o","--no-processes"},true,false)]
public void Disk_tracing_needs_elevation_and_the_disk_build(string[] args,bool elevated,bool expected)=>Assert.Equal(expected,App.DiagnosticOptions(args,elevated).ProcessDiskTracing);
[Fact] public void Disk_tracing_follows_the_disk_build_when_elevated()=>Assert.Equal(ProcessFeatures.DiskColumns,App.DiagnosticOptions(["--diagnose","o"],true).ProcessDiskTracing);
[Fact] public void Diagnostic_json_has_a_summary_and_no_process_names(){ var activity=ActivityWith("secret-app.exe","OtherSecret.EXE");
    var reading=Fixture() with {ProcessActivity=activity,ProcessSummary=ProcessSummary.From(activity)};
    string json=JsonSerializer.Serialize(new{Samples=new[]{reading}},new JsonSerializerOptions{WriteIndented=true});
    Assert.Contains("\"ProcessSummary\"",json);Assert.DoesNotContain("\"State\": \"Off\"",json);Assert.DoesNotContain("secret-app",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("OtherSecret",json,StringComparison.OrdinalIgnoreCase);}
[Fact] public async Task Summary_is_set_only_with_diagnostic_timings_and_reports_off(){ var off=await FirstReading(new(DiagnosticTimings:true));Assert.NotNull(off.ProcessSummary);Assert.Equal("Off",off.ProcessSummary!.State);
    var on=await SecondReading(new(DiagnosticTimings:true,ProcessMonitoring:true));Assert.Equal("Available",on.ProcessSummary!.State);Assert.Equal(on.ProcessActivity.Processes.Count,on.ProcessSummary.Processes);
    Assert.Null((await FirstReading(new(DiagnosticTimings:false,ProcessMonitoring:true))).ProcessSummary);
    var failed=await FirstReadingWithThrowingStorage(new(DiagnosticTimings:true,ProcessMonitoring:true));Assert.Equal("Unavailable",failed.ProcessSummary!.State);}
[Fact] public async Task Timing_keys_appear_when_the_scan_runs(){ Assert.Contains("windows/processes",r.ProviderMilliseconds!.Keys);Assert.Contains("processes/join",r.ProviderMilliseconds.Keys);Assert.DoesNotContain("etw/disk",r.ProviderMilliseconds.Keys);}
```
`FirstReading`/`SecondReading` build a collector with `HardwareEnabled:false, EtwEnabled:false` over Task 4's `FakeProcesses`; `FirstReadingWithThrowingStorage` adds Task 4's `FakeStorage` whose `Drives` throws.

- [ ] **Step 2: Commit the failing tests, push and verify failure**

Run (CI). Expected: `error CS0117` for `DiagnosticOptions`, `CS0246` for `ProcessSummary`.

- [ ] **Step 3: Implement the summary, the helper, and call `DiagnosticOptions(args, Collector.IsElevated)` from `DiagnoseAsync`**

- [ ] **Step 4: Commit, push and verify pass**

Run (CI). Expected: green; `DiagnosticOptionsTests` pass.

- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 6: `EtwDisk`, its serialized lifecycle and the collector wiring

**Files:**
- Create: `src/DeadWare.App/EtwDisk.cs`
- Modify: `src/DeadWare.App/Collector.cs` (queue start/stop on the first pass and on flag changes; disk take + `ProcessJoins.Disk` in block 2; `DisposeCoreAsync` at 330–350 requests the disk stop next to `var networkStop=StopNetworkCaptureAsync();` at 338 and awaits it with `networkStop`; `HardwareStopBudget` at 329 does not bound it)
- Test: `tests/DeadWare.App.Tests/EtwDiskTests.cs`, `tests/DeadWare.App.Tests/CoolingShutdownTests.cs` (add a case)

**Interfaces:**
- Consumes: Task 3 `DiskTake`, `DiskRateWindow`, `ProcessAttribution.Reconcile(index)`; `ProcessLedger`; `NativeProcessIdentity.Read`; Task 2 `TrackedScan.Index`.
- Produces:
  - `internal interface IDiskTrace : IAsyncDisposable { void Start(); Task StopAsync(); DiskTake Take(double now,IReadOnlyDictionary<int,ProcessIdentity> index); }`
  - `internal sealed class EtwDisk : IDiskTrace { public const int LossStormTakes=10; public static readonly TimeSpan StormPause=TimeSpan.FromMinutes(5); public static int DefaultBufferSizeMB=>Math.Max(64,2*Environment.ProcessorCount); public static int BufferSizeMB=>DefaultBufferSizeMB*ProcessFeatures.DiskBufferMultiplier; public EtwDisk(int bufferSizeMB); internal EtwDisk(Func<int,ProcessIdentity?> resolve,Func<long>? readLoss=null,Action? stopNative=null,Action? disposeNative=null); internal void RecordIo(int pid,long eventUtcTicks,int bytes,bool write); internal static int? OrphanPid(string sessionName); internal static void RemoveOrphans(IEnumerable<string> sessionNames,Func<int,bool> isRunningDeadWare,Action<string> stopSession); }`
  - `internal sealed class DiskTraceController(Func<IDiskTrace> factory){ public void Request(bool on); public DiskTake Take(double now,IReadOnlyDictionary<int,ProcessIdentity> index); public double? LastOpenMilliseconds{get;} public Task StopAsync(); }` (one serialized pool task; `StopAsync` closes it to new starts: a queued start is dropped, a running start finishes and is then stopped).
  - `internal Collector(…, IProcessSource? processes=null, Func<IDiskTrace>? diskTrace=null)`; the collector creates a `DiskTraceController` only when `options.ProcessDiskTracing` (public constructor factory: `()=>new EtwDisk(EtwDisk.BufferSizeMB)`) and holds it in `private DiskTraceController? disk;`, null-guarded for `GetUninitializedObject` fixtures.
  - Shutdown reporting: the disk stop is awaited inside `Collector.DisposeAsync()`, which `UiSession` already times as the `"hardware provider"` stage (`UiSession.cs:293`, `providerStop`, listed in `stages` at 304), so a disk stop still running at the provider deadline is reported as that outstanding stage in `collector_stop_timeout` with no `UiSession` change. A normal stop, including one queued behind a start, finishes within 3 s.

Session: `DeadWare-Disk-{Environment.ProcessId}-{Guid.NewGuid():N}`, `BufferSizeMB` set, keywords `DiskIO | Thread | Process`; `DiskIORead`/`DiskIOWrite` use `data.ProcessID` (−1 counts as unattributed) and `TransferSize` into `ProcessAttribution.Record`'s `down` (read) or `up` (write); Process start/DCStart/stop as in `EtwNetwork.cs:46-48`. Its own `ProcessAttribution`/`ProcessLedger`; the take maps the ledger's `ReceivedBytes`→`BytesRead`, `SentBytes`→`BytesWritten` at this boundary, feeds `DiskRateWindow`, and calls `Reconcile(index)` every 5 s. Loss: a take whose `EventsLost` rose is `Lossy` with status `"Windows dropped disk events"` and resets attribution and the window; 10 consecutive lossy takes stop the session and pause it for 5 minutes with status `"Disk tracing paused for 5 minutes after Windows dropped events repeatedly"`, after which the controller restarts it if still requested. Start failure status: `$"Disk tracing unavailable ({e.GetType().Name})"`; starting: `"Starting disk tracing…"`. Before each start, `RemoveOrphans(TraceEventSession.GetActiveSessionNames(), pid => <a running process whose image is DeadWare.exe>, name => stop)` for names `DeadWare-Disk-<pid>-*` and `DeadWare-<pid>-*`. Timing keys `etw/disk` (take) and `open/etwdisk` (`LastOpenMilliseconds`) only when the session ran.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Reads_and_writes_reach_the_disk_ledger(){ var d=Disk(resolve:pid=>new(pid,10,"a.exe"));d.RecordIo(8,20,4096,write:true);d.RecordIo(8,21,512,write:false);
    var t=Take(d,at:0);t=Take(d,at:2.5);var rate=Assert.Single(t.Rates,r=>r.Pid==8);Assert.Equal(512/2.5,rate.Read,6);Assert.Equal(4096/2.5,rate.Write,6);}
[Fact] public void Event_before_the_generation_is_not_credited(){ d.RecordIo(8,5,4096,true);/* gen 10 */ Assert.DoesNotContain(TakeWarm(d).Rates,r=>r.Pid==8&&r.Write>0);}
[Fact] public void Unattributed_events_are_counted(){ d.RecordIo(-1,20,4096,true);Assert.Equal(1,TakeWarm(d).UnattributedEvents);}
[Fact] public void Lossy_take_resets_totals_and_warm_up(){ loss=5;var t=d.Take(3,Index);Assert.True(t.Lossy);Assert.Equal("Windows dropped disk events",t.Status);Assert.False(d.Take(4,Index).Warm);}
[Fact] public void Ten_lossy_takes_pause_for_five_minutes(){ for(int i=0;i<10;i++){loss+=1;d.Take(i,Index);}var t=d.Take(11,Index);Assert.Equal(DiskTraceState.Paused,t.State);Assert.True(stopped);}
[Theory][InlineData("DeadWare-Disk-123-abc",123)][InlineData("DeadWare-456-def",456)][InlineData("Other-1-x",null)][InlineData("DeadWare-Disk-x-y",null)]
public void Orphan_session_names_parse_their_pid(string name,int? pid)=>Assert.Equal(pid,EtwDisk.OrphanPid(name));
[Fact] public void Only_sessions_of_exited_deadware_are_stopped(){ var stopped=new List<string>();EtwDisk.RemoveOrphans(["DeadWare-Disk-1-a","DeadWare-2-b","NT Kernel Logger"],pid=>pid==2,stopped.Add);Assert.Equal(["DeadWare-Disk-1-a"],stopped);}
[Fact] public async Task Queued_start_is_dropped_after_stop(){ /* factory blocks first start; Request(true) twice; StopAsync → second start never runs */ Assert.Equal(1,starts);}
[Fact] public async Task Running_start_finishes_then_stops(){ Assert.Equal(["start","stop"],log);}
[Fact] public async Task Rapid_toggles_leave_the_trace_matching_the_last_request(){ c.Request(true);c.Request(false);c.Request(true);c.Request(false);await Drain(c);Assert.False(fake.Running);}
[Fact] public async Task Stop_queued_behind_a_slow_start_finishes_within_three_seconds(){ var fake=new FakeTrace(startDelay:TimeSpan.FromMilliseconds(200));var c=new DiskTraceController(()=>fake);c.Request(true);
    var sw=Stopwatch.StartNew();await c.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));Assert.True(sw.Elapsed<TimeSpan.FromSeconds(3));Assert.Equal(1,fake.Starts);Assert.Equal(1,fake.Stops);Assert.False(fake.Running);}
[Fact] public async Task Monitoring_on_starts_disk_only_when_ProcessDiskTracing_is_set(){ var fake=new FakeTrace();await using(var off=Build(fake,new(ProcessMonitoring:true,ProcessDiskTracing:false))){await Readings(off,3);}Assert.Equal(0,fake.Starts);
    var gated=new FakeTrace();await using var c=Build(gated,new(ProcessMonitoring:false,ProcessDiskTracing:true));await Readings(c,2);Assert.Equal(0,gated.Starts);
    c.SetProcessMonitoring(true);await Readings(c,2);await Drain(c);Assert.Equal(1,gated.Starts);Assert.True(gated.Running);Assert.Equal(1,gated.Factories);}
[Fact] public async Task Monitoring_off_stops_the_disk_trace(){ var fake=new FakeTrace();await using var c=Build(fake,new(ProcessMonitoring:true,ProcessDiskTracing:true));await Readings(c,2);c.SetProcessMonitoring(false);await Readings(c,2);await Drain(c);
    Assert.Equal(1,fake.Stops);Assert.False(fake.Running);}
// CoolingShutdownTests
[Fact] public async Task Stuck_hardware_worker_still_stops_the_disk_session(){ var (session,collector,connections,worker)=StuckSession(Root(),new EtwNetwork(_=>new(11,1,"fixture")));var fake=new FakeTrace();
    var controller=new DiskTraceController(()=>fake);controller.Request(true);await Drain(controller);SetField(collector,"disk",controller);
    try{ var dispose=collector.DisposeAsync().AsTask();await fake.Stopped.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(1,fake.Stops);Assert.False(fake.Running);Assert.False(worker.Task.IsCompleted);Assert.False(dispose.IsCompleted);}
    finally{ worker.TrySetResult();await collector.DisposeAsync();await connections.StopAsync();}}
```

`FakeTrace : IDiskTrace` counts `Starts`, `Stops` and `Factories` (factory calls), exposes `Running` and a `Stopped` task completed by the first stop, and can delay `Start` (`startDelay`); `Build(fake,options)` makes a collector (`HardwareEnabled:false, EtwEnabled:false`, interval 0.5) over Task 4's `FakeProcesses` with `diskTrace:()=>fake` counting `Factories`; `Readings(c,n)` waits for n published readings; `Drain` awaits the queued lifecycle work of a controller, or of a collector's `disk` controller (read by reflection).

- [ ] **Step 2: Commit the failing tests, push and verify failure**

Run (CI). Expected: `error CS0246` for `EtwDisk`, `DiskTraceController`, `IDiskTrace`.

- [ ] **Step 3: Implement `EtwDisk`, `DiskTraceController` and the collector wiring**

Model the seams, `accountingGate` and stop/dispose on `EtwNetwork.cs` (`StopAsync` → `stopNative` → await processing; `DisposeAsync` → `ReleaseResources`). `UiSession` does not set `ProcessDiskTracing` yet (Task 10).

- [ ] **Step 4: Commit, push and verify pass**

Run (CI). Expected: green; `EtwDiskTests` and all `CoolingShutdownTests` pass.

- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 7: Real-Windows checks and measurements in CI

**Files:**
- Create: `tests/DeadWare.App.Tests/RealWindows/RealWindowsFacts.cs`, `RealWindows/MeasurementRecorder.cs`, `RealWindows/RestrictedLauncher.cs`, `RealWindows/ProcessScanChecks.cs`, `RealWindows/ProcessMeasurementTests.cs`
- Create: `tests/DeadWare.ProcessProbe/DeadWare.ProcessProbe.csproj` (`Exe`, `net10.0-windows`, references `src/DeadWare.App`), `tests/DeadWare.ProcessProbe/Program.cs`
- Modify: `src/DeadWare.App/WindowsProcesses.cs` (`[assembly: InternalsVisibleTo("DeadWare.ProcessProbe")]`), `DeadWare.slnx` (add the probe), `tests/DeadWare.App.Tests/DeadWare.App.Tests.csproj` (ProjectReference to the probe), `.github/workflows/build.yml` (upload `artifacts/measurements/` as `DeadWare-measurements`, `if: always()`, `if-no-files-found: warn`, the same pinned `actions/upload-artifact` SHA, after the package upload)

**Interfaces:**
- Consumes: Task 4 `WindowsProcesses`, Task 6 `EtwDisk`, `EtwNetwork`, `NativeProcessIdentity.Read`, Task 1 `ProcessFeatures`.
- Produces:
  - `public sealed class GitHubWindowsFactAttribute : FactAttribute` (Skip unless Windows and `GITHUB_ACTIONS=="true"`); `public sealed class ElevatedGitHubFactAttribute : FactAttribute` (also Skip when not elevated), modelled on `ElevatedFactAttribute` (`tests/DeadWare.Tests/ProtectedUpdateRootTests.cs:171`).
  - `[CollectionDefinition("RealWindows", DisableParallelization = true)] public sealed class RealWindowsCollection;` in `RealWindowsFacts.cs`; every class under `RealWindows/` (here and in Task 15) carries `[Collection("RealWindows")]`, so the 240-scan timing, the 256 MB write and the privilege changes never run beside another test class.
  - `internal static class MeasurementRecorder { public static void Write(string name,object values); public static bool Elevated{get;} }` — writes `<repo>/artifacts/measurements/<name>.json` (repo = first ancestor of `AppContext.BaseDirectory` holding `DeadWare.slnx`) and appends a `### <name>` table to `GITHUB_STEP_SUMMARY` when set.
  - `internal static class RestrictedLauncher { public static Process StartStandardUser(string exe,string arguments); }` — `CreateRestrictedToken` of the test's token with `DISABLE_MAX_PRIVILEGE`, Administrators (S-1-5-32-544) deny-only, medium integrity (S-1-16-8192), `CreateProcessAsUser`.
  - Probe: `DeadWare.ProcessProbe.exe scan <out.json>` writes one scan's `[{Pid,Generation,CpuTime,ReadTransfer,WriteTransfer,PrivateWorkingSet}]` and `elevated`; Task 15 adds `endtask`.

Checks (`[ElevatedGitHubFact]`, fail the build): `Scan_contains_this_process_and_system` (own PID with `Path.GetFileName(Environment.ProcessPath)` ignoring case; PID 4 named `System`; own CPU time non-decreasing across two scans); `Scan_generations_match_native_identity` (scan, `NativeProcessIdentity.Read` each PID, re-scan, drop PIDs whose Generation changed between the scans; all remaining Generations equal, names equal ignoring case unless the name is `PID n`, which is allowed only for PID 4 and the image-less `Registry`, `Memory Compression`, `Secure System`); `Orphan_disk_sessions_are_removed` (start real sessions `DeadWare-Disk-999999-<guid>` and `DeadWare-Disk-<own pid>-<guid>`; `RemoveOrphans` with `pid==Environment.ProcessId` as running; the first is gone, the second remains, then stop it); `Standard_user_scan_sees_the_same_processes` (elevated scan, probe `scan` via `RestrictedLauncher` asserting the probe reports `elevated:false`, elevated scan; every process both elevated scans hold under the same Generation is in the child's scan, including PID 4, session-0 services and `csrss.exe`; each CPU time and transfer count lies between its two elevated values; private working set non-zero wherever both elevated values are).

Measurements (`[GitHubWindowsFact]`, check elevation in the body; on a non-elevated run write only `{"elevated":false}` and pass, except as noted):
- `Scan_timing_over_240_scans` → `scan-timing.json`: `elevated, scans, p50Ms, p95Ms, bufferBytes, processes, threads`.
- `Disk_attribution_and_event_loss` → `disk-attribution.json`: for `size` in `DefaultBufferSizeMB × {1, 2, 4}` (stopping at the first size that lost nothing): start an `EtwNetwork` and an `EtwDisk(size)`, take both every 0.5 s, write 256 MB with `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH` (sector-aligned 1 MiB buffer from `NativeMemory.AlignedAlloc`) to a temp file, then run 30 s of 4 KiB unbuffered random writes with 32 concurrent requests; record per size `bufferSizeMB, share` (bytes written credited to own `(Pid, Generation)` ÷ 256 MB, from the 256 MB phase), `unattributedShare, eventsLost, longestLossyRun`; then `chosenBufferSizeMB` (smallest with no loss, else 4× with `lossKnownLimit:true`) and `diskColumns: ProcessFeatures.DiskColumns`. When `ProcessFeatures.DiskColumns` is true it asserts elevation and `share ≥ 0.90` at the chosen size; otherwise it only records.

- [ ] **Step 1: Write the checks, the measurements and the probe call sites**

- [ ] **Step 2: Commit, push and verify failure**

Run (CI). Expected: `error CS0246` for `MeasurementRecorder`, `RestrictedLauncher` and the missing probe project.

- [ ] **Step 3: Implement the attributes, recorder, launcher, probe, solution entry and the `build.yml` artifact step**

The probe's apphost is copied next to the test assembly by the ProjectReference; `StartStandardUser` takes `Path.Combine(AppContext.BaseDirectory,"DeadWare.ProcessProbe.exe")` and fails with that path in the message when absent.

- [ ] **Step 4: Commit, push and verify pass**

Run (CI). Expected: green; the four checks pass (not skipped: the runner is elevated); the run has a `DeadWare-measurements` artifact with `scan-timing.json` and `disk-attribution.json`, `elevated:true`, and the job summary shows both tables.

- [ ] **Step 5: Confirm GitHub has the commit and the artifact (`gh run download <run-id> -n DeadWare-measurements`)**

---

### Task 8: Record the measurements and set the decisions

**Files:**
- Modify: `src/DeadWare.Core/ProcessFeatures.cs`
- Modify: `docs/validation.md` (new top section `## Processes (0.13.0, <date>)`, measurements subsection)
- Test: `tests/DeadWare.App.Tests/RealWindows/ProcessMeasurementTests.cs` (gate behaviour, unchanged code)

**Interfaces:**
- Consumes: Task 7's artifact from the last green run on the PR.
- Produces: final `ProcessFeatures.ScanLoop`, `ProcessFeatures.DiskBufferMultiplier`, `ProcessFeatures.DiskColumns`.

Rules (spec, Recording the decisions): `ScanLoop = p95Ms > 15`. `DiskBufferMultiplier = chosenBufferSizeMB / DefaultBufferSizeMB` (1, 2 or 4). `DiskColumns = elevated && share ≥ 0.90 at the chosen size`; a missing share (non-elevated run) is a failed gate.

- [ ] **Step 1: Download the artifact and write the numbers into the PR description**

```bash
gh run download <run-id> -n DeadWare-measurements -D /tmp/measurements
gh pr edit --body-file <body.md>   # adds: run link; p50/p95, buffer bytes, process/thread counts; share and −1 share per BufferSizeMB, events lost, longest lossy run; and the three decisions: "every poll" or "loop", BufferSizeMB, "Disk in 0.13.0" or "Disk in 0.13.1"
```

- [ ] **Step 2: Set the three `ProcessFeatures` values and repeat the numbers and decisions in `docs/validation.md`**

- [ ] **Step 3: Commit and push**

```bash
git add src/DeadWare.Core/ProcessFeatures.cs docs/validation.md
git commit -m "chore: record process scan and disk attribution measurements and decisions"
```

- [ ] **Step 4: Verify in CI**

Run (CI). Expected: green. With `DiskColumns=true`, `Disk_attribution_and_event_loss` now enforces ≥ 90 % and still passes; its new `disk-attribution.json` shows `diskColumns:true`. If it fails, set `DiskColumns=false` (Disk moves to 0.13.1), update the PR description and push again.

- [ ] **Step 5: Confirm GitHub has the commit and the PR description holds the numbers; only then start Task 9 (if `ScanLoop`) or Task 10**

---

### Task 9 (only if `ProcessFeatures.ScanLoop` is true): `ProcessScanLoop`

**Files:**
- Create: `src/DeadWare.App/ProcessScanLoop.cs`
- Modify: `src/DeadWare.App/Collector.cs` (block 1 takes from the loop instead of scanning; loop joined in `DisposeCoreAsync` under `HardwareStopBudget` like `healthLoop`)
- Test: `tests/DeadWare.App.Tests/ProcessScanLoopTests.cs`

**Interfaces:**
- Consumes: Task 2 `ProcessActivityTracker`, `TrackedScan`, `ProcessDeltas.Add`; Task 4 `IProcessSource`.
- Produces: `internal sealed class ProcessScanLoop(IProcessSource source,ProcessActivityTracker tracker,Func<double> monotonic,Func<double> pollingInterval,Func<bool> enabled) : IAsyncDisposable { public void Start(); public TrackedScan? Take(); public string Status{get;} }` — period `max(1 s, pollingInterval())`, scans only while `enabled()`; `Take()` returns the latest tracked scan with every process's exact deltas (and `Omitted.Deltas`, `DeltaSeconds`) summed over all scans since the previous `Take()`, or the latest scan with `Deltas=null` and `DeltaSeconds=null` when no scan happened since; null before the first scan. Deltas accumulated for a process that is missing from the latest scan (it exited between loop scans) are added to that `Take()`'s `Omitted.Deltas`, so history still counts them once (under Everything else). The loop owns the tracker: in loop placement the collector never calls `tracker.Reset()` or `Update`; when the loop sees `enabled()` false it calls `tracker.Reset()` itself, discards the pending deltas and the latest scan (`Take()` returns null until the next scan). `Update`, `Reset` and `Take` run under one loop-private lock. Block 1's flag-off branch only publishes `ProcessActivity.Off`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Two_scans_between_takes_sum_their_deltas_once(){ /* fake source cpu 0,1e7,2e7 at 1 s period */ Assert.Equal(20_000_000,taken.Processes[0].Deltas!.CpuTime);Assert.Equal(2,taken.DeltaSeconds!.Value,3);}
[Fact] public async Task Take_without_a_new_scan_has_no_deltas()=>Assert.Null(second.DeltaSeconds);
[Fact] public async Task Deltas_of_a_process_that_exits_between_takes_go_to_omitted(){ /* scan 1: A(cpu 0) B(cpu 0); scan 2: A(1e7) B(3e7); scan 3: A(2e7) only */ var taken=loop.Take()!;
    Assert.DoesNotContain(taken.Processes,p=>p.Pid==B);Assert.Equal(30_000_000,taken.Omitted.Deltas.CpuTime);Assert.Equal(20_000_000,taken.Processes.Single(p=>p.Pid==A).Deltas!.CpuTime);}
[Fact] public async Task Period_is_at_least_one_second_and_follows_slower_polling(){ double interval=0.5;var (loop,fake)=Loop(()=>interval);await fake.WaitForCalls(3);
    Assert.All(fake.CallGaps(),g=>Assert.True(g>=0.95,$"gap {g:0.00} s"));interval=2;int n=fake.Calls;await fake.WaitForCalls(n+2);Assert.All(fake.CallGaps().Skip(n),g=>Assert.True(g>=1.95,$"gap {g:0.00} s"));}
[Fact] public async Task Disabled_loop_never_scans()=>Assert.Equal(0,fake.Calls);
[Fact] public async Task Turning_off_resets_the_tracker_and_the_first_scan_after_on_is_a_first_sample(){ bool on=true;var (loop,fake)=Loop(enabled:()=>on);await fake.WaitForCalls(3);on=false;await Task.Delay(1500);Assert.Null(loop.Take());
    on=true;int n=fake.Calls;await fake.WaitForCalls(n+1);var first=loop.Take()!;Assert.All(first.Processes,p=>{Assert.Equal(ProcessGap.FirstSample,p.RateGap);Assert.Null(p.CpuPercent);});Assert.Null(first.DeltaSeconds);}
[Fact] public async Task Dispose_completes_within_the_hardware_budget(){ var (loop,fake)=Loop(source:new BlockingProcesses());loop.Start();await fake.Entered;var sw=Stopwatch.StartNew();
    await loop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));Assert.True(sw.Elapsed<TimeSpan.FromSeconds(5.5),$"{sw.Elapsed.TotalSeconds:0.0} s");}
```

`Loop(pollingInterval = ()=>0.5, enabled = ()=>true, source = new FakeProcesses())` builds and starts a `ProcessScanLoop` over a real `ProcessActivityTracker(8)` and a `Stopwatch` monotonic clock; `FakeProcesses` (Task 4) records each call's monotonic time (`CallGaps()`), and `WaitForCalls(n)` waits up to 15 s; `BlockingProcesses` blocks every `Scan` until released and completes `Entered` on the first call.

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `ProcessScanLoop`.
- [ ] **Step 3: Implement the loop in the style of `StorageSensorLoop` and switch the collector to it**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `ProcessScanLoopTests`, `CollectorProcessTests` (including `Switching_off_takes_effect_within_one_poll_and_clears_baselines` in loop placement) pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

## 0.13.0 — Step 2: page, actions, settings and preview

### Task 10: Settings, the switch in `UiSession` and the Settings checkbox

**Files:**
- Create: `src/DeadWare.Core/ProcessColumnKeys.cs`, `src/DeadWare.Core/ProcessTable.cs` (only `ProcessCopy.ScanSentence` here; Task 11 fills the file)
- Modify: `src/DeadWare.Core/Monitoring.cs:374-411` (`UserSettings`), `:415-450` (`SettingsCodec.Read`, beside `OpenWindows` at 440)
- Modify: `src/DeadWare.App/UiSession.cs:58` (collector options), add `SetProcessMonitoring`/`SetProcessColumns` beside `SetPollingInterval` (203–209)
- Modify: `src/DeadWare.App/Windows.cs:193-195` (checkbox and help line after Storage sensors)
- Test: `tests/DeadWare.Tests/ProcessSettingsTests.cs`, `tests/DeadWare.App.Tests/ShellUiChecks.cs`

**Interfaces:**
- Consumes: Task 4 `Collector.SetProcessMonitoring`; Task 1 `ProcessFeatures`; nothing else.
- Produces:
  - `public static class ProcessColumnKeys { public static IReadOnlyList<string> All{get;} public static IReadOnlyList<string> Default{get;} public static IReadOnlyList<string> AdministratorOnly{get;} public static IReadOnlyList<string> DiskKeys{get;} public static List<string> Normalize(IEnumerable<string?>? keys); public static List<string> Toggle(IReadOnlyList<string> saved,string key,bool on); }` — `Toggle` adds or removes only `key` and returns `Normalize` of the result, keeping every other saved key (administrator-only keys while not elevated, Disk keys in a build without Disk); the page's picker always goes through it.
  - `UserSettings.ProcessMonitoring` (bool, false), `UserSettings.ProcessColumns` (`List<string>`, `ProcessColumnKeys.Default.ToList()`).
  - `UiSession.SetProcessMonitoring(bool enabled)` (saves, calls `Collector.SetProcessMonitoring`, raises `Changed`); `UiSession.SetProcessColumns(IEnumerable<string> keys)` (normalizes, saves, raises `Changed` only on change).
  - `public static string ProcessCopy.ScanSentence(bool elevated,bool diskColumns,bool scanLoop)` → `"Turning it on adds one Windows process scan per poll."`, with `" (at most one a second)"` after "per poll" when `scanLoop`, and `" and a disk trace"` before the full stop when `elevated && diskColumns`.

`Normalize`: null → default; drop null, unknown and duplicate keys; order by `All`; keep an empty list. `SettingsCodec.Read` assigns it without touching `invalid`. Collector options gain `ProcessMonitoring:Settings.ProcessMonitoring,ProcessDiskTracing:ProcessFeatures.DiskColumns&&Collector.IsElevated`. Checkbox content **Per-process monitoring**, automation name "Per-process monitoring", help line `ProcessCopy.ScanSentence(session.Current?.Elevated??Collector.IsElevated,ProcessFeatures.DiskColumns,ProcessFeatures.ScanLoop)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Defaults_are_off_and_the_five_default_columns(){ var s=new UserSettings();Assert.False(s.ProcessMonitoring);Assert.Equal(["cpu","memory","io","disk","network"],s.ProcessColumns);}
[Fact] public void Subsets_round_trip(){ var s=new UserSettings{ProcessMonitoring=true,ProcessColumns=["pid","upload"]};var r=SettingsCodec.Read(SettingsCodec.Write(s));Assert.True(r.Settings.ProcessMonitoring);Assert.Equal(["pid","upload"],r.Settings.ProcessColumns);}
[Fact] public void Null_or_missing_list_becomes_default(){ Assert.Equal(ProcessColumnKeys.Default,SettingsCodec.Read("{\"ProcessColumns\":null}").Settings.ProcessColumns);Assert.Equal(ProcessColumnKeys.Default,SettingsCodec.Read("{}").Settings.ProcessColumns);}
[Fact] public void Unknown_null_and_duplicate_keys_are_dropped_and_ordered(){ var r=SettingsCodec.Read("{\"ProcessColumns\":[\"upload\",\"gpu\",null,\"cpu\",\"cpu\"]}");Assert.Equal(["cpu","upload"],r.Settings.ProcessColumns);Assert.Null(r.Error);}
[Fact] public void Empty_list_is_kept()=>Assert.Empty(SettingsCodec.Read("{\"ProcessColumns\":[]}").Settings.ProcessColumns);
[Fact] public void Administrator_and_disk_keys_are_always_known()=>Assert.Equal(ProcessColumnKeys.AdministratorOnly,ProcessColumnKeys.Normalize(ProcessColumnKeys.AdministratorOnly));
[Fact] public void Toggle_changes_only_that_key(){ Assert.Equal(["pid","cpu","memory","io","disk","network"],ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"pid",true));
    Assert.Equal(["memory","io","disk","network"],ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"cpu",false));Assert.Equal(ProcessColumnKeys.Default,ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"cpu",true));}
[Theory][InlineData(false,false,false,"Turning it on adds one Windows process scan per poll.")][InlineData(true,true,false,"Turning it on adds one Windows process scan per poll and a disk trace.")]
[InlineData(true,false,true,"Turning it on adds one Windows process scan per poll (at most one a second).")][InlineData(true,true,true,"Turning it on adds one Windows process scan per poll (at most one a second) and a disk trace.")]
public void Scan_sentence_follows_the_build(bool elevated,bool disk,bool loop,string expected)=>Assert.Equal(expected,ProcessCopy.ScanSentence(elevated,disk,loop));
// ShellUiChecks (Run): find CheckBox "Per-process monitoring" in the Monitoring card; toggle; assert session.Settings.ProcessMonitoring follows; help text equals ScanSentence(...)
```

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `ProcessColumnKeys`, `ProcessCopy`.
- [ ] **Step 3: Implement the keys, settings, codec line, session methods, collector options and checkbox**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `ProcessSettingsTests` and the UI fact pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 11: Core table model — grouping, columns, sorting and copy

**Files:**
- Modify: `src/DeadWare.Core/ProcessTable.cs` (created in Task 10)
- Test: `tests/DeadWare.Tests/ProcessTableTests.cs`

**Interfaces:**
- Consumes: Task 2 `ProcessSample`, `ProcessGap`; Task 10 `ProcessColumnKeys`; `UsageMetrics.NormalizeName`.
- Produces:
  - `public sealed record ProcessAppRow(string Key,string Name,IReadOnlyList<ProcessSample> Members){ public double? Cpu{get;} public long Memory{get;} public double? Io{get;} public double? IoRead{get;} public double? IoWrite{get;} public double? Disk{get;} public double? DiskRead{get;} public double? DiskWrite{get;} public double? Network{get;} public double? Download{get;} public double? Upload{get;} }` — each value is the sum of known members, null only when none is known; a member's `Io` is read+write, null when either is null.
  - `public static class ProcessGrouping { public static string Key(string name); public static IReadOnlyList<ProcessAppRow> Group(IReadOnlyList<ProcessSample> processes); public static bool Matches(ProcessAppRow row,string query); public static double? Value(ProcessSample p,string key); public static double? Value(ProcessAppRow r,string key); }` — `Key(name)=UsageMetrics.NormalizeName(name).ToUpperInvariant()`; `Name` is the first member's normalized casing in scan order; `Matches` trims the query, empty matches all, else name contains (OrdinalIgnoreCase) or any member's PID text contains it.
  - `public sealed record ProcessColumnChoice(string Key,string Header,bool Checked,bool Enabled,string Label);`
  - `public static class ProcessTableColumns { public static string Header(string key); public static IReadOnlyList<string> Visible(IReadOnlyList<string> saved,bool elevated,bool diskColumns); public static IReadOnlyList<ProcessColumnChoice> Picker(IReadOnlyList<string> saved,bool elevated,bool diskColumns); public static (string Key,bool Descending) EffectiveSort(string? key,bool descending,IReadOnlyList<string> visible); }` — `Visible` removes the six administrator keys when `!elevated` and the three disk keys when `!diskColumns`; `Picker` lists CPU, MEMORY, I/O, DISK, NETWORK, then PID, I/O READ, I/O WRITE, DISK READ, DISK WRITE, ↓ DOWNLOAD, ↑ UPLOAD, omits disk entries when `!diskColumns`, and disables administrator entries when `!elevated` with label `"<HEADER> (administrator)"` and `Checked` from `saved`; `EffectiveSort` returns the key when visible (or `"name"`), else `("cpu",true)` when CPU is visible, else `("name",false)`.
  - `public static class ProcessSort { public static int Compare(ProcessAppRow a,ProcessAppRow b,string key,bool descending); public static int Compare(ProcessSample a,ProcessSample b,string key,bool descending); }` — numeric, nulls last in both directions, ties by name (OrdinalIgnoreCase) then PID (an app's PID for ties is its lowest member PID).
  - `ProcessCopy` gains: `OffTitle="Per-process monitoring is off."`; `public static string? Banner(bool elevated,bool diskColumns)` → `"Disk and network need administrator"` / `"Network needs administrator"` / null; tooltips `FirstSample="first sample"`, `Stale="stale reading"`, `NetworkNotMatched="traffic not matched to this process"`, `DiskNotMatched="disk activity not matched to this process"`; `public static string CapNote(int total)` → `$"Showing 4,096 of {total:N0} processes"`; `public static IReadOnlyList<string> Footer(bool diskColumns)` → the spec's four definitions (Disk omitted when `!diskColumns`):
    - "CPU is the share of all logical processors, as in Task Manager's Details tab, so it sums to at most 100 %."
    - "Memory is the private working set, Task Manager's \"Memory (private working set)\"."
    - "I/O counts all file, pipe and device transfers, including cached reads, and is not disk activity."
    - "Disk is physical drive transfers. Delayed cache writes are credited to System."

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Case_variants_are_one_row_with_the_first_casing(){ var rows=ProcessGrouping.Group([S(8,"Explorer.EXE",cpu:1),S(9,"explorer.exe",cpu:2)]);var r=Assert.Single(rows);Assert.Equal("Explorer.EXE",r.Name);Assert.Equal(3,r.Cpu);Assert.Equal("EXPLORER.EXE",r.Key);}
[Fact] public void Non_ascii_case_variants_are_one_row()=>Assert.Single(ProcessGrouping.Group([S(8,"Éclair.exe"),S(9,"éCLAIR.EXE")]));
[Fact] public void Row_key_is_stable_when_the_first_casing_changes()=>Assert.Equal(ProcessGrouping.Key("explorer.exe"),ProcessGrouping.Key("EXPLORER.exe"));
[Fact] public void Sum_is_null_only_when_no_member_is_known(){ Assert.Null(G(S(8,"a",cpu:null),S(9,"a",cpu:null)).Cpu);Assert.Equal(2,G(S(8,"a",cpu:null),S(9,"a",cpu:2)).Cpu);}
[Fact] public void Empty_and_control_char_names_normalize_like_usage_history(){ Assert.Equal("Unknown application",G(S(8,"")).Name);Assert.Equal("a.exe",G(S(9,"a\u0007.exe ")).Name);Assert.Equal(256,G(S(10,new string('x',300))).Name.Length);}
[Theory][InlineData(" chrome ",true)][InlineData("12",true)][InlineData(" 12 ",true)][InlineData("zzz",false)][InlineData("",true)]
public void Search_trims_and_matches_name_or_pid(string q,bool hit)=>Assert.Equal(hit,ProcessGrouping.Matches(G(S(1234,"chrome.exe")),q));
[Fact] public void Visible_columns_keep_the_fixed_order()=>Assert.Equal(["pid","cpu","upload"],ProcessTableColumns.Visible(["upload","cpu","pid"],true,true));
[Fact] public void Standard_user_hides_the_six_administrator_columns()=>Assert.Equal(["cpu","memory","io"],ProcessTableColumns.Visible(ProcessColumnKeys.All.Except(["pid","ioread","iowrite"]).ToList(),false,true));
[Fact] public void Disk_build_off_removes_disk_from_table_and_picker(){ Assert.DoesNotContain("disk",ProcessTableColumns.Visible(ProcessColumnKeys.Default,true,false));Assert.DoesNotContain(ProcessTableColumns.Picker(ProcessColumnKeys.Default,false,false),c=>c.Key.StartsWith("disk"));}
[Fact] public void Picker_disables_admin_entries_and_keeps_their_saved_state(){ var p=ProcessTableColumns.Picker(["network"],false,true);var n=p.Single(c=>c.Key=="network");Assert.False(n.Enabled);Assert.True(n.Checked);Assert.Equal("NETWORK (administrator)",n.Label);
    Assert.Equal(["cpu","memory","io","disk","network","pid","ioread","iowrite","diskread","diskwrite","download","upload"],p.Select(c=>c.Key));}
[Fact] public void Hidden_sort_column_falls_back_to_cpu_then_name(){ Assert.Equal(("cpu",true),ProcessTableColumns.EffectiveSort("upload",false,["cpu"]));Assert.Equal(("name",false),ProcessTableColumns.EffectiveSort("upload",false,["memory"]));}
[Fact] public void Nulls_sort_last_both_ways_and_ties_by_name_then_pid(){ /* rows a(null),b(5),c(5) */ Assert.Equal(["b","c","a"],SortBy(true));Assert.Equal(["b","c","a"],SortBy(false));}
[Theory][InlineData(false,true,"Disk and network need administrator")][InlineData(false,false,"Network needs administrator")][InlineData(true,true,null)]
public void Banner_follows_elevation_and_build(bool elevated,bool disk,string? expected)=>Assert.Equal(expected,ProcessCopy.Banner(elevated,disk));
[Fact] public void Footer_omits_disk_without_the_disk_build()=>Assert.Equal(3,ProcessCopy.Footer(false).Count);
```

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `ProcessGrouping`, `ProcessTableColumns`, `ProcessSort`.
- [ ] **Step 3: Implement `ProcessTable.cs`**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `ProcessTableTests` pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 12: Calm-order planner and hold policy

**Files:**
- Create: `src/DeadWare.Core/ProcessRowOrder.cs`
- Test: `tests/DeadWare.Tests/ProcessRowOrderTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record RowMove(string Id,int From,int To);`
  - `public static class CalmOrderPlanner { public static IReadOnlyList<RowMove> Plan(IReadOnlyList<string> current,IReadOnlyList<string> sorted); public static IReadOnlyList<string> Held(IReadOnlyList<string> current,IReadOnlyList<string> present,Func<string,string?> parentOf); }` — `Plan` moves only rows outside the longest increasing subsequence of `current` positions in `sorted` (ids absent from either side are the caller's adds/removes); `Held` removes ids not in `present`, appends new app ids (parent null) at the bottom in `present` order, and inserts new child ids after the last current row of their parent (appended at the bottom if the parent itself is new).
  - `public sealed class OrderHold { public const double KeySeconds=3; public bool PointerOver{get;set;} public bool MenuOpen{get;set;} public void KeyNavigated(double now); public bool IsHeld(double now); }`

Row ids used by the page: `"app:" + ProcessGrouping.Key(name)` and `$"proc:{pid}:{generation}"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Sorted_order_moves_nothing()=>Assert.Empty(CalmOrderPlanner.Plan(["a","b","c"],["a","b","c"]));
[Fact] public void Moves_only_rows_outside_the_longest_increasing_subsequence()=>Assert.Equal(["a"],CalmOrderPlanner.Plan(["a","b","c","d","e"],["b","c","d","e","a"]).Select(m=>m.Id));
[Fact] public void Swap_of_two_neighbours_moves_one_row()=>Assert.Single(CalmOrderPlanner.Plan(["a","b","c"],["b","a","c"]));
[Fact] public void Held_update_removes_exited_and_appends_new_apps_at_the_bottom()=>
    Assert.Equal(["app:A","app:C","app:D"],CalmOrderPlanner.Held(["app:A","app:B","app:C"],["app:D","app:C","app:A"],_=>null));
[Fact] public void Held_update_adds_new_processes_after_their_expanded_app()=>
    Assert.Equal(["app:A","proc:1:1","proc:3:1","app:B"],CalmOrderPlanner.Held(["app:A","proc:1:1","app:B"],["app:A","proc:3:1","proc:1:1","app:B"],id=>id.StartsWith("proc:")?"app:A":null));
[Fact] public void Hold_lasts_three_seconds_after_a_navigation_key(){ var h=new OrderHold();h.KeyNavigated(10);Assert.True(h.IsHeld(12.9));Assert.False(h.IsHeld(13.1));}
[Fact] public void Pointer_or_menu_holds(){ var h=new OrderHold{PointerOver=true};Assert.True(h.IsHeld(0));h.PointerOver=false;h.MenuOpen=true;Assert.True(h.IsHeld(0));h.MenuOpen=false;Assert.False(h.IsHeld(0));}
```

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `CalmOrderPlanner`, `OrderHold`.
- [ ] **Step 3: Implement the planner (patience-sort LIS, O(n log n)) and the hold**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `ProcessRowOrderTests` pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 13: `EndTaskPolicy` and its copy

**Files:**
- Create: `src/DeadWare.Core/EndTaskPolicy.cs`
- Test: `tests/DeadWare.Tests/EndTaskPolicyTests.cs`

**Interfaces:**
- Produces:
  - `public enum EndTaskVerdict {CanEnd,SystemProcess,DeadWareItself,AlreadyExited,Critical,Protected,WindowsProcess,NeedsAdministrator,WindowsDenied}` (declaration order after `CanEnd` is the "first rule that refuses" order).
  - `public sealed record EndTaskFacts(bool Exited,bool TerminateOpened,bool FactsOpened,long? CreationTicks,bool? Critical,bool? Protected,string? ImagePath,string? TokenUserSid);`
  - `public static class EndTaskPolicy { public static EndTaskVerdict? Precheck(int pid,int ownPid); public static EndTaskVerdict Classify(EndTaskFacts facts,long generation,string windowsFolder,bool elevated); public static bool IsServiceAccount(string sid); public static bool IsInsideWindows(string path,string windowsFolder); public static bool IsWindowsSvchost(string path,string windowsFolder); public static string Question(string appName,int pid,int canEnd,int running,bool wholeApp); public static string? NotEnded(IEnumerable<EndTaskVerdict> verdicts); public static string Refusal(IEnumerable<EndTaskVerdict> verdicts); public static string Result(int ended,int notEnded,int alreadyExited,int stillRunning); }`

Classification (spec, Actions step 3): `Precheck` → PID 0/4 `SystemProcess`, own PID `DeadWareItself`. Classify checks, in this order: `Exited` → `AlreadyExited`; `!FactsOpened` or `CreationTicks is null` → undecided (`NeedsAdministrator` when `!elevated`, else `WindowsDenied`), checked before the generation comparison so an unreadable creation time is never `AlreadyExited`; `CreationTicks.Value != generation` → `AlreadyExited`; known `Critical==true` → `Critical`; `Protected==true` → `Protected`; Windows' own rule true → `WindowsProcess`; any unknown rule (null critical/protected, unknown path, inside-Windows non-svchost path with unknown SID) or `!TerminateOpened` → `NeedsAdministrator` when `!elevated`, else `WindowsDenied`; otherwise `CanEnd`. Windows' own rule: `IsWindowsSvchost` (image is `svchost.exe` directly in `<windows>\System32` or `<windows>\SysWOW64`, OrdinalIgnoreCase) → true; `IsInsideWindows` (starts with `windowsFolder + "\"`, OrdinalIgnoreCase, and not under `<windows>\System32\DriverStore\`) and `IsServiceAccount` (S-1-5-18, S-1-5-19, S-1-5-20, S-1-5-90-*, S-1-5-96-*) → true; known path outside (or in the driver store) → false. Copy:
- `Question`: whole app, all endable → `"End chrome.exe (31 processes)? Unsaved work in it will be lost."` (`"(1 process)"` for one); whole app, some → `"End 28 of 31 chrome.exe processes? Unsaved work in them will be lost."`; single process → `"End chrome.exe (PID 1234)? Unsaved work in it will be lost."`
- `NotEnded` (null when nothing): `"Not ended: "` + counts joined with `", "` in verdict order + `"."`, labels `n system process(es)`, `Use Quit DeadWare` (DeadWare itself, no count), `n critical process(es)`, `n protected process(es)`, `n Windows' own process(es)`, `n need(s) administrator`, `n denied by Windows`; `AlreadyExited` is left out.
- `Refusal` (no member can end): `"Use Quit DeadWare"` when the only refusal is DeadWare itself, else `NotEnded` without the full stop; when any member `NeedsAdministrator` append `" Use Restart as administrator on this page."` after a full stop.
- `Result`: `"Ended {N} · not ended {M} · already exited {K} · still running {R}"`.

- [ ] **Step 1: Write the failing tests**

```csharp
const string W=@"C:\WINDOWS";const string User="S-1-5-21-1-2-3-1001";
[Theory][InlineData(0)][InlineData(4)] public void Pid_0_and_4_are_refused_before_opening(int pid)=>Assert.Equal(EndTaskVerdict.SystemProcess,EndTaskPolicy.Precheck(pid,999));
[Fact] public void DeadWare_itself_is_refused()=>Assert.Equal(EndTaskVerdict.DeadWareItself,EndTaskPolicy.Precheck(999,999));
[Fact] public void Changed_creation_time_is_already_exited()=>Assert.Equal(EndTaskVerdict.AlreadyExited,C(F(creation:2),generation:1));
[Theory][InlineData(false,EndTaskVerdict.NeedsAdministrator)][InlineData(true,EndTaskVerdict.WindowsDenied)]
public void Unreadable_creation_time_is_undecided(bool elevated,EndTaskVerdict expected)=>Assert.Equal(expected,C(F(creation:null),elevated:elevated));
[Theory][InlineData(false,EndTaskVerdict.NeedsAdministrator)][InlineData(true,EndTaskVerdict.WindowsDenied)]
public void Failed_facts_only_open_is_undecided(bool elevated,EndTaskVerdict expected)=>Assert.Equal(expected,C(F(terminate:false,factsOpened:false,creation:null,critical:null,prot:null,path:null,sid:null),elevated:elevated));
[Theory][InlineData(@"C:\Windows\System32\spoolsv.exe","S-1-5-18",EndTaskVerdict.WindowsProcess)][InlineData(@"C:\Windows\System32\notepad.exe",User,EndTaskVerdict.CanEnd)]
[InlineData(@"C:\Windows.old\x.exe","S-1-5-18",EndTaskVerdict.CanEnd)][InlineData(@"C:\WindowsAzure\x.exe","S-1-5-18",EndTaskVerdict.CanEnd)][InlineData(@"C:\Windows\System32\x.exe","S-1-5-90-0-1",EndTaskVerdict.WindowsProcess)]
public void Windows_folder_plus_service_account(string path,string sid,EndTaskVerdict expected)=>Assert.Equal(expected,C(F(path:path,sid:sid),elevated:true));
[Fact] public void System32_svchost_of_the_signed_in_user_is_refused()=>Assert.Equal(EndTaskVerdict.WindowsProcess,C(F(path:@"c:\windows\system32\SVCHOST.EXE",sid:User)));
[Fact] public void Driver_store_service_is_allowed_when_elevated()=>Assert.Equal(EndTaskVerdict.CanEnd,C(F(path:@"C:\Windows\System32\DriverStore\FileRepository\nv.inf_amd64\Display.NvContainer\NVDisplay.Container.exe",sid:"S-1-5-18"),elevated:true));
[Fact] public void Service_directly_in_System32_is_refused()=>Assert.Equal(EndTaskVerdict.WindowsProcess,C(F(path:@"C:\Windows\System32\oldvendor.exe",sid:"S-1-5-18"),elevated:true));
[Fact] public void Unreadable_sid_inside_windows_is_undecided()=>Assert.Equal(EndTaskVerdict.NeedsAdministrator,C(F(path:@"C:\Windows\System32\x.exe",sid:null)));
[Fact] public void Unreadable_sid_outside_windows_is_settled_by_the_path()=>Assert.Equal(EndTaskVerdict.CanEnd,C(F(path:@"C:\Program Files\App\app.exe",sid:null)));
[Fact] public void Unknown_path_is_never_settled()=>Assert.Equal(EndTaskVerdict.WindowsDenied,C(F(path:null,sid:User),elevated:true));
[Theory][InlineData(null,false)][InlineData(false,null)] public void Unknown_critical_or_protection_is_undecided(bool? critical,bool? prot)=>Assert.Equal(EndTaskVerdict.NeedsAdministrator,C(F(critical:critical,prot:prot)));
[Fact] public void Denied_terminate_open_keeps_a_known_refusal()=>Assert.Equal(EndTaskVerdict.Critical,C(F(terminate:false,critical:true)));
[Fact] public void Denied_terminate_open_otherwise_needs_administrator()=>Assert.Equal(EndTaskVerdict.NeedsAdministrator,C(F(terminate:false)));
[Fact] public void Questions(){ Assert.Equal("End chrome.exe (31 processes)? Unsaved work in it will be lost.",EndTaskPolicy.Question("chrome.exe",0,31,31,true));
    Assert.Equal("End 28 of 31 chrome.exe processes? Unsaved work in them will be lost.",EndTaskPolicy.Question("chrome.exe",0,28,31,true));}
[Fact] public void Not_ended_summary_counts_by_first_rule()=>Assert.Equal("Not ended: 3 need administrator.",EndTaskPolicy.NotEnded(Enumerable.Repeat(EndTaskVerdict.NeedsAdministrator,3).Append(EndTaskVerdict.AlreadyExited)));
[Fact] public void Refusals(){ Assert.Equal("Use Quit DeadWare",EndTaskPolicy.Refusal([EndTaskVerdict.DeadWareItself]));Assert.Equal("Not ended: 2 Windows' own processes",EndTaskPolicy.Refusal([EndTaskVerdict.WindowsProcess,EndTaskVerdict.WindowsProcess]));
    Assert.EndsWith("Use Restart as administrator on this page.",EndTaskPolicy.Refusal([EndTaskVerdict.NeedsAdministrator]));}
[Fact] public void Result_line()=>Assert.Equal("Ended 2 · not ended 1 · already exited 1 · still running 0",EndTaskPolicy.Result(2,1,1,0));
```
`F(...)` (parameters `exited, terminate, factsOpened, creation, critical, prot, path, sid`) builds `EndTaskFacts` defaulting to `Exited:false,TerminateOpened:true,FactsOpened:true,CreationTicks:1,Critical:false,Protected:false,ImagePath:@"C:\Program Files\App\app.exe",TokenUserSid:User`; `C(f,generation:1,elevated:false)` calls `Classify(f,generation,W,elevated)`.

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `EndTaskPolicy`.
- [ ] **Step 3: Implement `EndTaskPolicy`**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `EndTaskPolicyTests` pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 14: Native End task and Open file location

**Files:**
- Create: `src/DeadWare.App/ProcessActions.cs`
- Test: `tests/DeadWare.App.Tests/ProcessActionsTests.cs`, `tests/DeadWare.App.Tests/FakeProcessControl.cs` (the shared fake; Tasks 16–17 reuse it)

**Interfaces:**
- Consumes: Task 13 `EndTaskPolicy`, `EndTaskFacts`, `EndTaskVerdict`.
- Produces:
  - `internal sealed record ProcessOpen(SafeProcessHandle? Handle,bool Denied,bool Exited);`
  - `internal interface IProcessControl { string WindowsFolder{get;} ProcessOpen OpenForTerminate(int pid,bool enableDebugPrivilege); EndTaskFacts ReadFacts(int pid,ProcessOpen open); bool Terminate(SafeProcessHandle handle); bool IsSignalled(SafeProcessHandle handle); void WaitAll(IReadOnlyList<SafeProcessHandle> handles,TimeSpan timeout); long? CreationTicks(int pid); string? ImagePath(int pid); }`
  - `internal sealed class NativeProcessControl : IProcessControl` — `OpenForTerminate` opens `PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE` (0x0001|0x1000|0x100000); with `enableDebugPrivilege` it runs on a short-lived thread: `ImpersonateSelf(SecurityImpersonation)`, `OpenThreadToken`, `AdjustTokenPrivileges(SeDebugPrivilege enabled)`, `OpenProcess`, `RevertToSelf` in `finally`. `ERROR_INVALID_PARAMETER` → `Exited`; `ERROR_ACCESS_DENIED` and every other open error → `Denied` (not opened, so the facts-only read decides, and an unreadable fact never allows ending). `ReadFacts` uses the open handle, or after a denial a `PROCESS_QUERY_LIMITED_INFORMATION`-only open without the privilege: `GetProcessTimes` creation ticks via `DateTime.FromFileTimeUtc(created).Ticks`, `IsProcessCritical`, `GetProcessInformation(ProcessProtectionLevelInfo)` (level ≠ `PROTECTION_LEVEL_NONE` = protected), `QueryFullProcessImageNameW(flags 0)`, `OpenProcessToken(TOKEN_QUERY)` + `GetTokenInformation(TokenUser)`. `WindowsFolder` from `GetSystemWindowsDirectoryW`, read once.
  - `internal sealed class ProcessActions(IProcessControl control,Func<string,string?,bool> confirm,Func<string,bool> reveal,int ownPid,bool elevated) { public Task<string> EndAsync(string appName,IReadOnlyList<(int Pid,long Generation)> members,bool wholeApp); public Task<string> OpenLocationAsync(IReadOnlyList<(int Pid,long Generation)> members); internal static bool Reveal(string path); }`

`EndAsync` (spec, Actions steps 1–5): prechecks first; then per member open (debug privilege only when `elevated`), facts, `Classify`; handles of `CanEnd` members are kept, every other handle closed at once; no `CanEnd` → return `EndTaskPolicy.Refusal`, no box; else `confirm(Question(...), NotEnded(...))` — `running` counts members not `AlreadyExited`; No → close kept handles, return `""`; Yes → a kept handle already signalled counts as already exited, others get `Terminate(handle)` (exit code 1), then one `WaitAll(…, 2 s)`; signalled-after-wait members count as ended (or already exited when `Terminate` failed), the rest still running; return `EndTaskPolicy.Result`. A `Terminate` that returns false needs no copy of its own: the spec's aggregated line counts that member as still running or already exited. Everything runs on `Task.Run`; nothing is thrown: a `Win32Exception w` becomes `$"End task failed (Windows error {w.NativeErrorCode})"`, any other exception `$"End task failed ({e.GetType().Name})"`. `OpenLocationAsync`: generation check (`"The process has already exited."`), distinct paths (OrdinalIgnoreCase) > 1 → `$"runs from {n} locations; expand to choose one"`; `File.Exists` false → `"The program file is no longer there."`; `reveal(path)` on a short-lived STA thread — `Reveal` calls `SHParseDisplayName` + `SHOpenFolderAndSelectItems` and falls back to `explorer.exe /select,"<path>"`, and returns false when both fail. Open file location failures: `reveal` false → `"Could not open the file location."`; a `Win32Exception w` → `$"Could not open the file location (Windows error {w.NativeErrorCode})."`; any other exception → `$"Could not open the file location ({e.GetType().Name})."`; success → `""`. The path stays in locals. The default confirm used by the page is `MessageBox.Show(question + (summary is null ? "" : "\n\n" + summary), "DeadWare", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes`, marshalled to the dispatcher.

- [ ] **Step 1: Write the failing tests**

```csharp
// FakeProcessControl (internal, own file): scripted ProcessOpen/EndTaskFacts per pid (default: endable, signalled after Terminate), records opens, terminations and closes; handles are fake SafeProcessHandle(IntPtr n,false)
[Fact] public async Task Pid_0_4_and_DeadWare_are_refused_without_opening(){ var (a,c)=Actions(own:999);Assert.Equal("Use Quit DeadWare",await a.EndAsync("DeadWare.exe",[(999,1)],true));Assert.Empty(c.Opened);
    Assert.StartsWith("Not ended: 1 system process",await a.EndAsync("System",[(4,1)],true));}
[Fact] public async Task Confirm_counts_only_endable_members_and_summarises_the_rest(){ /* 28 endable, 3 NeedsAdministrator, standard user */ Assert.Equal("End 28 of 31 chrome.exe processes? Unsaved work in them will be lost.",asked.Question);Assert.Equal("Not ended: 3 need administrator.",asked.Summary);}
[Fact] public async Task No_endable_member_opens_no_box_and_points_to_restart(){ Assert.False(confirmCalled);Assert.EndsWith("Use Restart as administrator on this page.",status);}
[Fact] public async Task No_ends_nothing_and_closes_kept_handles(){ Assert.Empty(c.Terminated);Assert.Equal(c.Opened.Count,c.Closed.Count);}
[Fact] public async Task Yes_terminates_with_one_wait_and_reports_the_aggregate(){ /* 2 end, 1 refused, 1 exited before box, 1 stays running */ Assert.Equal("Ended 2 · not ended 1 · already exited 1 · still running 1",status);Assert.Equal(1,c.Waits);}
[Fact] public async Task Failed_terminate_whose_handle_signals_counts_as_already_exited()=>Assert.Equal("Ended 0 · not ended 0 · already exited 1 · still running 0",status);
[Fact] public async Task Denied_open_shows_the_refusal_from_the_facts_only_read()=>Assert.Equal("Not ended: 1 critical process",status);
[Fact] public async Task Debug_privilege_is_requested_only_when_elevated(){ Assert.All(standard.Opened,o=>Assert.False(o.Debug));Assert.All(elevated.Opened,o=>Assert.True(o.Debug));}
[Fact] public async Task Native_failures_never_throw_and_map_win32_codes(){ Assert.Equal("End task failed (Windows error 5)",await EndWith(c=>c.ThrowOnFacts=new Win32Exception(5)));
    Assert.Equal("End task failed (InvalidOperationException)",await EndWith(c=>c.ThrowOnFacts=new InvalidOperationException()));}
[Fact] public async Task Other_open_errors_count_as_denied()=>Assert.Equal("Not ended: 1 needs administrator. Use Restart as administrator on this page.",await EndWith(c=>c.OpenError(8,error:1450)));
[Fact] public async Task Open_location_failures_map_to_copy(){ Assert.Equal("Could not open the file location.",await OpenWith(reveal:false));
    Assert.Equal("Could not open the file location (Windows error 2).",await OpenWith(throwOnReveal:new Win32Exception(2)));}
[Fact] public async Task Open_location_with_two_paths_asks_to_expand()=>Assert.Equal("runs from 2 locations; expand to choose one",await a.OpenLocationAsync([(8,1),(9,1)]));
[Fact] public async Task Open_location_checks_generation_and_existence(){ Assert.Equal("The process has already exited.",await Stale());Assert.Equal("The program file is no longer there.",await Missing());Assert.Equal(@"C:\Program Files\App\app.exe",revealed);}
```
`EndWith(configure)` runs `EndAsync("a.exe",[(8,1)],false)` as a standard user over a `FakeProcessControl` whose pid 8 opens and reads as endable unless `configure` changes it (`OpenError(pid,error)` makes the open fail with that Win32 code, `ThrowOnFacts` throws from `ReadFacts`); `OpenWith(reveal=true, throwOnReveal=null)` runs `OpenLocationAsync([(8,1)])` with an existing temp file as the image path.

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `ProcessActions`, `IProcessControl`.
- [ ] **Step 3: Implement `NativeProcessControl` and `ProcessActions`**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `ProcessActionsTests` pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 15: Real-Windows End-task checks

**Files:**
- Create: `tests/DeadWare.App.Tests/RealWindows/EndTaskChecks.cs`
- Modify: `tests/DeadWare.ProcessProbe/Program.cs` (`endtask <pid> <generation> <out.json>` mode running `ProcessActions` with `NativeProcessControl`, confirm answering Yes, `elevated:false`)

**Interfaces:**
- Consumes: Task 14 `ProcessActions`, `NativeProcessControl`; Task 7 `ElevatedGitHubFactAttribute`, `RestrictedLauncher`.

Checks (`[ElevatedGitHubFact]`, class `[Collection("RealWindows")]`): `Windows_processes_are_refused_with_a_No_answer` (real control + policy, confirm recording and answering No: PID 4, csrss, lsass, services and every svchost, plus dwm and MsMpEng when running, all classified refused; facts for csrss report `Critical==true` and `Protected==true`; confirm is never asked for them); `LocalSystem_helper_is_allowed_and_ended_only_with_the_debug_privilege` (copy `%WINDIR%\System32\PING.EXE` to a new temp folder as `deadware-probe-helper.exe`; `schtasks /Create /TN DeadWare-Probe-<guid> /SC ONCE /ST 23:59 /RU SYSTEM /TR "\"<copy>\" -n 600 127.0.0.1" /F` then `/Run`; find its PID by image path; facts classify `CanEnd`; with SeDebugPrivilege disabled on the test's own token via `AdjustTokenPrivileges` (keeping the `PreviousState` it returns, and restoring exactly that state in `finally`), a plain `OpenProcess(PROCESS_TERMINATE)` fails with `ERROR_ACCESS_DENIED`; `EndAsync` with Yes reports `"Ended 1 · …"` and the process exits; `finally` deletes the task and folder); `Own_child_processes_end` (the test's own `ping` children: single-process and whole-app forms); `Standard_user_end_task_needs_administrator` (start an elevated `ping` copy; probe `endtask` via `RestrictedLauncher`; its JSON status contains `"1 needs administrator"` and the target is still running; kill it in `finally`).

- [ ] **Step 1: Write the checks and the probe mode call**
- [ ] **Step 2: Commit, push and verify failure** — Expected: the probe rejects `endtask` (unknown mode), so `Standard_user_end_task_needs_administrator` fails.
- [ ] **Step 3: Implement the probe's `endtask` mode**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; all four `EndTaskChecks` pass (not skipped).
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 16: Processes page — shell wiring, off state, table, columns, picker, banner and tooltips

**Files:**
- Create: `src/DeadWare.App/ProcessesPage.cs`
- Modify: `src/DeadWare.App/Windows.cs:60` (`("processes","Processes")` after `storage`), `:74` (`pages["processes"]=new ProcessesPage(session)`), `:244` (add `"processes"` to the collapsed-toolbar list), `:246` (heading `"processes"=>"Processes"`)
- Modify: `src/DeadWare.App/Overview.cs` (`NavGlyph` switch near line 118: `"processes"` glyph, a stroked list with a pulse)
- Create: `tests/DeadWare.App.Tests/ProcessesPageUiChecks.cs`
- Modify: `tests/DeadWare.App.Tests/BatteryUiTests.cs:73-74` (phase `"processes page"` → `ProcessesPageUiChecks.Run()` after `StoragePageUiChecks.Run()`), `tests/DeadWare.App.Tests/ShellUiChecks.cs:28-30`

**Interfaces:**
- Consumes: Tasks 10–14; `UiSession.SetProcessMonitoring`, `SetProcessColumns`, `RestartElevated`, `Current`, `Stale`, `Changed`.
- Produces:
  - `internal sealed class ProcessesPage : UserControl { public ProcessesPage(UiSession session); internal ProcessesPage(UiSession session,bool diskColumns,IProcessControl? control); internal DataGrid Grid{get;} internal Func<string,string?,bool> Confirm{get;set;} internal void Refresh(); }` — the public constructor passes `ProcessFeatures.DiskColumns` and `null` (meaning `new NativeProcessControl()`); `Grid` is the Processes `DataGrid` (Task 18's preview check reads it); `Confirm` defaults to Task 14's `MessageBox` confirm and is the **only** confirmation hook: Task 17 builds `ProcessActions` per action as `new ProcessActions(control,(q,s)=>Confirm(q,s),ProcessActions.Reveal,Environment.ProcessId,session.Current?.Elevated??Collector.IsElevated)`, so UI checks set `Confirm` and pass Task 14's `FakeProcessControl`.
  - `internal sealed class ProcessRowView : INotifyPropertyChanged` (`Id`, `IsApp`, `Expandable`, `Expanded`, `Name`, one display string and one tooltip string per column key, `Spoken`).

Layout and behaviour (spec, Processes page): off state text `ProcessCopy.OffTitle + " " + ProcessCopy.ScanSentence(...)` with **Turn on** → `session.SetProcessMonitoring(true)`; top strip: search box (placeholder/automation name "Find app or PID"), **End task…**, **Open file location** (both disabled until Task 17 wires them), one banner (`ProcessCopy.Banner(session.Current.Elevated, diskColumns)`) with **Restart as administrator** → `session.RestartElevated`; `Grid` holds `ProcessRowView`s with uppercased headers from `ProcessTableColumns.Visible(session.Settings.ProcessColumns, session.Current.Elevated, diskColumns)`. **Columns** button and a right-click on any column header open the same checkable `ContextMenu` from `ProcessTableColumns.Picker`; a toggle of entry `key` calls `session.SetProcessColumns(ProcessColumnKeys.Toggle(session.Settings.ProcessColumns,key,on))`, never a list rebuilt from the menu's items, so disabled administrator keys and the Disk keys a build without Disk leaves out of the menu stay saved. Rows are collapsed by default; the chevron in NAME expands an app into its process rows, which show the same columns and sort within their app by the table's sort; a single-process app shows its PID inline and no chevron. Header click sorts (`ProcessSort`, first click descending for numeric columns, ascending for NAME, a second click flips); `ProcessTableColumns.EffectiveSort` applies whenever the visible set changes. Cap note `ProcessCopy.CapNote(TotalProcesses)` when `TotalProcesses > 4096`; footer lines `ProcessCopy.Footer(diskColumns)`. Tooltips, first match: `Stale` for every value cell (shown as "—") when `session.Stale`; `FirstSample` for `RateGap/NetworkGap/DiskGap==FirstSample`; `NetworkNotMatched`/`DiskNotMatched` for `NotMatched`; else the column's `ProcessColumnState.Status`. Automation names: grid "Processes", search box "Find app or PID", each button by its content, chevrons "Expand <name>"/"Collapse <name>"; `Spoken` like `"chrome.exe, 3 processes, CPU 9 %, memory 512 MB"` (the visible columns in order). Renders once from `session.Current` in its constructor (as `StoragePage` does); in this task every new reading re-sorts (Task 17 adds calm order).

- [ ] **Step 1: Write the failing UI checks**

```csharp
internal static void Run(){ Off_state_shows_turn_on(); Rows_group_case_variants_are_collapsed_and_expand(); Single_process_app_shows_pid_without_chevron(); Expanded_rows_share_columns_and_sort_within_their_app();
    Column_picker_has_every_column_in_fixed_order(); Header_right_click_opens_picker(); Picker_toggle_keeps_hidden_admin_and_disk_keys();
    Standard_user_hides_admin_columns_disables_picker_entries_and_shows_banner(); Elevated_shows_the_full_set_with_dash_and_status(); Disk_build_off_omits_disk_everywhere();
    Gap_tooltips_name_their_reason(); Stale_reading_shows_dash_with_stale_tooltip(); Hidden_sort_column_falls_back(); Cap_note_is_shown(); Footer_lists_the_definitions(); Automation_names_and_spoken_summary(); }
// Session fixture as StoragePageUiChecks.Session: Reading{Elevated=e, ProcessActivity=activity}, Settings{ProcessMonitoring=on, ProcessColumns=columns}; stale:true sets the reading's MonotonicSeconds to −100.
// Fixture(): Sequence 1; chrome.exe PIDs 30/31/32 (CPU 1, 5, 3), Explorer.EXE 10 and explorer.exe 11 (CPU 1 each), Code.exe 40 (CPU 0.5); all with memory, I/O, disk and network values.
static void Off_state_shows_turn_on(){ var (p,session)=Page(monitoring:false);Assert.Contains(Texts(p),t=>t=="Per-process monitoring is off. Turning it on adds one Windows process scan per poll.");Click(Button(p,"Turn on"));Assert.True(session.Settings.ProcessMonitoring);}
static void Rows_group_case_variants_are_collapsed_and_expand(){ var (p,_)=Page();Assert.Equal(["chrome.exe","Explorer.EXE","Code.exe"],AppNames(p));Assert.Equal(3,Rows(p).Count);Expand(p,"chrome.exe");Assert.Equal(6,Rows(p).Count);}
static void Single_process_app_shows_pid_without_chevron(){ var (p,_)=Page(columns:["pid","cpu"]);Assert.Null(Chevron(p,"Code.exe"));Assert.Equal("40",Cell(p,"Code.exe","pid"));Assert.NotNull(Chevron(p,"chrome.exe"));Assert.Equal("",Cell(p,"chrome.exe","pid"));}
static void Expanded_rows_share_columns_and_sort_within_their_app(){ var (p,_)=Page(elevated:true);Expand(p,"chrome.exe");Assert.Equal([31,32,30],ChildPids(p,"chrome.exe"));
    Assert.All(["cpu","memory","io","disk","network"],k=>Assert.NotEqual("",Cell(p,31,k)));Assert.Equal("app:EXPLORER.EXE",Rows(p)[4].Id);}
static void Column_picker_has_every_column_in_fixed_order()=>Assert.Equal(["CPU","MEMORY","I/O","DISK","NETWORK","PID","I/O READ","I/O WRITE","DISK READ","DISK WRITE","↓ DOWNLOAD","↑ UPLOAD"],PickerHeaders(Page(elevated:true,disk:true).Page));
static void Header_right_click_opens_picker(){ var (p,_)=Page(elevated:true);RightClick(Header(p,"CPU"));Assert.True(PickerMenu(p).IsOpen);Assert.Equal(12,PickerItems(p).Count);PickerMenu(p).IsOpen=false;}
static void Picker_toggle_keeps_hidden_admin_and_disk_keys(){ var (p,session)=Page(elevated:false,disk:false);Toggle(PickerItem(p,"PID"));
    Assert.Equal(["pid","cpu","memory","io","disk","network"],session.Settings.ProcessColumns);Toggle(PickerItem(p,"CPU"));Assert.Equal(["pid","memory","io","disk","network"],session.Settings.ProcessColumns);}
static void Standard_user_hides_admin_columns_disables_picker_entries_and_shows_banner(){ var (p,_)=Page(elevated:false,disk:true);Assert.Equal(["NAME","CPU","MEMORY","I/O"],Headers(p));
    Assert.Equal(6,PickerItems(p).Count(i=>i.Header.EndsWith("(administrator)")));Assert.All(PickerItems(p).Where(i=>i.Header.EndsWith("(administrator)")),i=>Assert.False(i.IsEnabled));Assert.Contains("Disk and network need administrator",Texts(p));Assert.NotNull(Button(p,"Restart as administrator"));}
static void Elevated_shows_the_full_set_with_dash_and_status(){ var (p,_)=Page(elevated:true,network:new(false,"Per-app ETW unavailable (X)"));Assert.Equal(["NAME","CPU","MEMORY","I/O","DISK","NETWORK"],Headers(p));Assert.Equal("—",Cell(p,"chrome.exe","network"));Assert.Equal("Per-app ETW unavailable (X)",Tip(p,"chrome.exe","network"));Assert.Null(Button(p,"Restart as administrator"));}
static void Disk_build_off_omits_disk_everywhere(){ var (p,_)=Page(elevated:false,disk:false);Assert.DoesNotContain(PickerHeaders(p),h=>h.StartsWith("DISK"));Assert.Contains("Network needs administrator",Texts(p));
    var (e,_)=Page(elevated:true,disk:false);Assert.DoesNotContain("DISK",Headers(e));}
static void Gap_tooltips_name_their_reason(){ var (p,_)=Page(elevated:true,activity:WithGaps());/* new.exe RateGap FirstSample; moved.exe NetworkGap and DiskGap NotMatched */
    Assert.Equal("—",Cell(p,"new.exe","cpu"));Assert.Equal("first sample",Tip(p,"new.exe","cpu"));Assert.Equal("traffic not matched to this process",Tip(p,"moved.exe","network"));Assert.Equal("disk activity not matched to this process",Tip(p,"moved.exe","disk"));}
static void Stale_reading_shows_dash_with_stale_tooltip(){ var (p,_)=Page(stale:true);Assert.Equal("—",Cell(p,"chrome.exe","cpu"));Assert.Equal("stale reading",Tip(p,"chrome.exe","cpu"));Assert.Equal("stale reading",Tip(p,"chrome.exe","memory"));}
static void Hidden_sort_column_falls_back(){ var (p,_)=Page(elevated:true);ClickHeader(p,"NETWORK");Toggle(PickerItem(p,"NETWORK"));Assert.Equal(("cpu",true),SortState(p));Assert.Equal(["chrome.exe","Explorer.EXE","Code.exe"],AppNames(p));
    Toggle(PickerItem(p,"CPU"));Assert.Equal(("name",false),SortState(p));Assert.Equal(["chrome.exe","Code.exe","Explorer.EXE"],AppNames(p));}
static void Cap_note_is_shown(){ Assert.Contains("Showing 4,096 of 5,000 processes",Texts(Page(activity:Fixture() with {TotalProcesses=5000}).Page));Assert.DoesNotContain(Texts(Page().Page),t=>t.StartsWith("Showing "));}
static void Footer_lists_the_definitions(){ Assert.Equal(ProcessCopy.Footer(true),FooterTexts(Page(elevated:true,disk:true).Page));Assert.Equal(ProcessCopy.Footer(false),FooterTexts(Page(elevated:true,disk:false).Page));}
static void Automation_names_and_spoken_summary(){ var (p,_)=Page(elevated:true);Assert.Equal("Processes",AutomationProperties.GetName(p.Grid));Assert.Equal("Find app or PID",AutomationProperties.GetName(SearchBox(p)));
    Assert.All(["End task…","Open file location","Columns"],b=>Assert.Equal(b,AutomationProperties.GetName(Button(p,b))));Assert.Equal("Expand chrome.exe",AutomationProperties.GetName(Chevron(p,"chrome.exe")));
    Expand(p,"chrome.exe");Assert.Equal("Collapse chrome.exe",AutomationProperties.GetName(Chevron(p,"chrome.exe")));Assert.StartsWith("chrome.exe, 3 processes, CPU ",Row(p,"chrome.exe").Spoken);}
// ShellUiChecks
Assert.Equal(12,navigation.Count);Assert.Equal(order.IndexOf("storage")+1,order.IndexOf("processes"));Assert.Equal(order.IndexOf("network")+1,order.IndexOf("storage"));
```
`Page(...)` returns `(ProcessesPage Page,UiSession Session)` built through the internal constructor (`diskColumns:disk`, `control:new FakeProcessControl()`), defaults `monitoring:true, elevated:false, disk:true, stale:false, columns:ProcessColumnKeys.Default, activity:Fixture()`; `Cell`/`Tip` take an app name or a PID; `Toggle` raises the menu item's `Click` with `IsChecked` flipped.

- [ ] **Step 2: Commit the failing checks, push and verify failure** — Expected: `error CS0246` for `ProcessesPage`.
- [ ] **Step 3: Implement the page, rail entry, glyph, heading and toolbar collapse**

Follow `StoragePage` for the render-in-constructor and `Loaded`/`Unloaded` subscription to `session.Changed`; follow the Network table (`Windows.cs:88`) for column styling.

- [ ] **Step 4: Commit, push and verify pass** — Expected: green; the UI fact passes through phase `"processes page"` and the shell checks.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 17: Processes page — calm order, holds, keyboard, selection and actions

**Files:**
- Modify: `src/DeadWare.App/ProcessesPage.cs`
- Modify: `tests/DeadWare.App.Tests/ProcessesPageUiChecks.cs` (`Run()` calls the new checks after Task 16's)

**Interfaces:**
- Consumes: Task 16 `ProcessesPage`, `Confirm`; Task 12 `CalmOrderPlanner`, `OrderHold`; Task 14 `ProcessActions`, `FakeProcessControl`.
- Produces: no new members; behaviour only.

Calm order (spec, Calm order): re-sort only when `ProcessActivity.Sequence` changes, never on a `Changed` without one; `OrderHold` is held while the pointer is over `Grid`, while a menu or dialog is open (the picker, the confirm call), and for 3 s after `KeyNavigated` on Up/Down/PageUp/PageDown/Home/End; outside a hold apply `CalmOrderPlanner.Plan` (so new apps take their sorted place at once), during one `Held`; F5, a header click and a search change re-sort immediately even during a hold; expanding or collapsing adds or removes only that app's rows; selection, expansion and the `ScrollViewer` vertical offset survive every update. Keys: Left/Right collapse/expand, Enter toggles, Delete opens End task…, Ctrl+F focuses search, Esc clears search, F5 re-sorts. Selection: an app row acts on every member (`wholeApp:true`), a process row on that process; when the selected row's id disappears, selection clears and both action buttons disable. **End task…** and **Open file location** run Task 14's `EndAsync`/`OpenLocationAsync` and show the returned string in a status line under the top strip (empty string clears it).

- [ ] **Step 1: Write the failing UI checks**

```csharp
static void Changed_without_a_new_reading_does_not_resort(){ var (p,session)=Page();var before=AppNames(p);MutateLatest(session,"Code.exe",cpu:99);RaiseChanged(session);Assert.Equal(before,AppNames(p));}
static void New_reading_outside_a_hold_resorts(){ var (p,session)=Page();Publish(session,2,cpu:("Code.exe",99));Assert.Equal("Code.exe",AppNames(p)[0]);}
static void Held_order_appends_new_apps_and_keeps_selection(){ var (p,session)=Page();var before=AppNames(p);Hold(p,pointer:true);Select(p,"chrome.exe");Publish(session,2,withNew:"zoom.exe",cpu:("chrome.exe",0));
    Assert.Equal([..before,"zoom.exe"],AppNames(p));Assert.Equal("chrome.exe",Selected(p));Hold(p,pointer:false);Publish(session,3);Assert.Equal(Sorted(p),AppNames(p));}
static void F5_header_click_and_search_resort_during_hold(){ var (p,session)=Page();Hold(p,pointer:true);Publish(session,2,cpu:("Code.exe",99));Assert.NotEqual("Code.exe",AppNames(p)[0]);
    Key(p,Key.F5);Assert.Equal("Code.exe",AppNames(p)[0]);Publish(session,3,cpu:("Explorer.EXE",200));ClickHeader(p,"CPU");Assert.Equal(Sorted(p),AppNames(p));
    Publish(session,4,cpu:("Code.exe",300));SetSearch(p,"e");Assert.Equal(Sorted(p),AppNames(p));}
static void Updates_keep_selection_expansion_and_scroll(){ var (p,session)=Page(activity:SixtyApps());Expand(p,"chrome.exe");Select(p,"app40.exe");ScrollTo(p,200);Publish(session,2);
    Assert.Equal("app40.exe",Selected(p));Assert.True(IsExpanded(p,"chrome.exe"));Assert.Equal(200,VerticalOffset(p),1);}
static void Keyboard_shortcuts(){ var (p,_)=Page();int asked=0;p.Confirm=(_,_)=>{asked++;return false;};Select(p,"chrome.exe");Key(p,Key.Right);Assert.True(IsExpanded(p,"chrome.exe"));Key(p,Key.Left);Assert.False(IsExpanded(p,"chrome.exe"));
    Key(p,Key.Enter);Assert.True(IsExpanded(p,"chrome.exe"));Key(p,Key.F,ModifierKeys.Control);Assert.Same(SearchBox(p),Keyboard.FocusedElement);SetSearch(p,"code");Key(SearchBox(p),Key.Escape);Assert.Equal("",SearchBox(p).Text);
    Select(p,"chrome.exe");Key(p,Key.Delete);WaitFor(()=>asked==1);}
static void Selected_app_exiting_clears_selection_and_disables_actions(){ var (p,session)=Page();Select(p,"chrome.exe");Assert.True(Button(p,"End task…").IsEnabled);Publish(session,2,without:"chrome.exe");
    Assert.Null(Selected(p));Assert.False(Button(p,"End task…").IsEnabled);Assert.False(Button(p,"Open file location").IsEnabled);}
static void End_task_asks_through_the_confirm_hook_and_shows_the_result(){ var (p,_)=Page();string? asked=null;p.Confirm=(q,_)=>{asked=q;return true;};Select(p,"chrome.exe");Click(Button(p,"End task…"));
    WaitFor(()=>StatusText(p)!="");Assert.Equal("End chrome.exe (3 processes)? Unsaved work in it will be lost.",asked);Assert.Equal("Ended 3 · not ended 0 · already exited 0 · still running 0",StatusText(p));}
```
`Publish(session,sequence,…)` replaces the fixture reading with a new `Sequence` and raises `Changed`; `Sorted(p)` is the app order `ProcessSort` gives the latest reading under the page's current sort; `WaitFor` pumps the dispatcher for up to 5 s; `FakeProcessControl` reads every fixture PID as endable and signals on `Terminate`.

- [ ] **Step 2: Commit the failing checks, push and verify failure** — Expected: the UI fact fails in phase `"processes page"` (first failing check: `Changed_without_a_new_reading_does_not_resort` or `Held_order_appends_new_apps_and_keeps_selection`).
- [ ] **Step 3: Implement calm order, holds, keys, selection and the action wiring**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; the UI fact passes phase `"processes page"`.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 18: Preview routes

**Files:**
- Modify: `tools/OverviewPreview/Program.cs:27` (Routes gain `"storage"` and `"processes"` after `"network"`, 12 routes), `:82` (preview `UserSettings` gains `ProcessMonitoring=true`), `:139` (`UpdateSample` sets `Reading.ProcessActivity`), `:274` (sizes: `route is "network" or "desktop" or "processes"` → 1220×900 and 900×640), `:282` (table height and app-row count for `"processes"` from `ProcessesPage.Grid`), `:306` (Note: `"all eight production pages"` becomes `"all ten production pages"` — Storage and Processes are the two new production pages — and `"Network and Settings also at"` becomes `"Network, Processes and Settings also at"`)

**Interfaces:**
- Consumes: Task 16 `ProcessesPage.Grid`, Task 2 records.

Fixture activity: `chrome.exe` ×3 (one expanded-capable app), `Code.exe`, `explorer.exe`, `DeadWare.exe`, all with CPU, memory, I/O and network values, `Elevated=true`. The check fails the render when the Processes grid is under 70 px tall at 900×640 or shows fewer than 4 app rows.

- [ ] **Step 1: Add the `storage` and `processes` routes, the `processes` sizes and the `processes` check (grid ≥ 70 px tall at 900×640, ≥ 4 app rows) to the render loop, without fixture settings or activity**
- [ ] **Step 2: Commit, push and verify failure** — Expected: the "Render previews" step fails with the `processes` check's message: the page renders its off state (monitoring is off in the preview settings), so the grid has 0 app rows.
- [ ] **Step 3: Add `ProcessMonitoring=true` to the preview settings, the synthetic activity in `UpdateSample` and the Note wording**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `DeadWare-previews` holds `glass-shell-*-processes.png`, `glass-shell-*-processes-900x640.png` and `glass-shell-*-storage.png`.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 19: 0.13.0 release docs and version

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.13.0</Version>`), `CHANGELOG.md` (new top `## 0.13.0 — Processes`), `README.md` (Features bullet; Settings paragraph for Per-process monitoring; privacy section; the `--diagnose` paragraph lists `--no-processes`; End task rules and the known limit for third-party services directly in System32/SysWOW64), `docs/validation.md` (complete the `## Processes (0.13.0, <date>)` section)
- Create: `docs/releases/v0.13.0.md`
- Test: full CI build

CHANGELOG/release notes cover: the Processes page and its columns (I/O for every user, Disk and Network as administrator), End task rules and default-No confirmation, Open file location, the off-by-default **Per-process monitoring** switch and remembered columns; that this is the Processes release the Storage notes called "0.12" (the number went to 0.12.0–0.12.4); the answer to "Per-application disk I/O … is planned for 0.12": per-application Disk ships now for administrators — or, when Task 8 moved it, "per-app I/O ships now and per-app Disk follows in 0.13.1, using DeadWare's own thread-to-process map" — and I/O is never disk data. `docs/validation.md`: the measured numbers and decisions from Task 8, the CI checks as verified, and the "Pending by eye" list from the spec (Task Manager Details comparison; End task against real third-party services including a driver-store service such as NVIDIA Display Container and another signed-in user's process, as standard user and administrator; Open file location in Program Files, WindowsApps and paths with spaces and commas; behaviour at 0.5 s and 10 s polling, during sleep, and on Quit during an elevated copy), never claimed as verified. Release notes follow `docs/development.md` (no installation steps).

- [ ] **Step 1: Write the docs and bump the version**
- [ ] **Step 2: Commit and push**

```bash
git add Directory.Build.props CHANGELOG.md README.md docs/validation.md docs/releases/v0.13.0.md
git commit -m "docs: processes release notes, changelog, validation and 0.13.0"
```

- [ ] **Step 3: Run (CI)** — Expected: green; package and previews uploaded as usual.
- [ ] **Step 4: Mark the PR ready** — `gh pr ready`; update the PR description's summary to the release scope.
- [ ] **Step 5: Confirm GitHub has the commit and report the PR link; the release tag waits for the user's go**

---

## 0.13.1 — only if Task 8 set `DiskColumns=false`

Work on a new feature branch from `main` after 0.13.0 merges; open a draft PR with the first push.

### Task 20: Thread-to-process map for disk events

**Files:**
- Create: `src/DeadWare.Core/DiskThreadMap.cs`
- Modify: `src/DeadWare.App/EtwDisk.cs` (Thread start/DCStart/stop handlers feed the map; `DiskIORead`/`DiskIOWrite` take the PID from the map by the event's issuing thread — check `DiskIOTraceData`'s members in TraceEvent 4.0.0 for the issuing-thread field before using `ThreadID` — and count the event unattributed when the map has no entry)
- Modify: `src/DeadWare.Core/ProcessFeatures.cs` (only after Step 4 passes: `DiskColumns=true`)
- Test: `tests/DeadWare.Tests/DiskThreadMapTests.cs`; the gate is `Disk_attribution_and_event_loss` (Task 7)

**Interfaces:**
- Produces: `public sealed class DiskThreadMap { public void Start(int threadId,int pid,long eventUtcTicks); public void Stop(int threadId,long eventUtcTicks); public int? Resolve(int threadId,long eventUtcTicks); public void Clear(); public int Count{get;} }` — a thread id reused by another process resolves to the newer owner for events at or after its start; `Stop` removes the entry; `Clear` is called on loss.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Started_thread_resolves_to_its_process(){ var m=new DiskThreadMap();m.Start(100,8,10);Assert.Equal(8,m.Resolve(100,11));}
[Fact] public void Unknown_thread_is_unattributed()=>Assert.Null(new DiskThreadMap().Resolve(5,1));
[Fact] public void Reused_thread_id_resolves_to_the_newer_owner(){ m.Start(100,8,10);m.Stop(100,20);m.Start(100,9,30);Assert.Equal(9,m.Resolve(100,31));Assert.Null(m.Resolve(100,25));}
[Fact] public void Clear_empties_the_map_after_loss(){ m.Start(1,2,3);m.Clear();Assert.Equal(0,m.Count);}
```

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `DiskThreadMap`.
- [ ] **Step 3: Implement the map and switch `EtwDisk` to it; temporarily set `DiskColumns=true` so the CI test enforces the gate**
- [ ] **Step 4: Commit, push and verify** — Expected: green, `disk-attribution.json` share ≥ 0.90 at the chosen size. **If the share is below 90 %, revert `DiskColumns` to false, record the numbers in the PR, and stop: report to the user; Disk stays out.**
- [ ] **Step 5: Confirm GitHub has the commit and record the share in the PR description**

---

### Task 21: 0.13.1 release docs and version

**Files:**
- Modify: `Directory.Build.props` (`0.13.1`), `CHANGELOG.md` (`## 0.13.1 — Per-app disk`), `README.md` (Disk columns, `--diagnose` disk timing keys, privacy unchanged), `docs/validation.md` (`## Per-app disk (0.13.1, <date>)` with the share, −1 share, `BufferSizeMB`, events lost)
- Create: `docs/releases/v0.13.1.md`

- [ ] **Step 1: Write the docs and bump the version**
- [ ] **Step 2: Commit and push** (`docs: per-app disk release notes and 0.13.1`)
- [ ] **Step 3: Run (CI)** — Expected: green.
- [ ] **Step 4: Mark the PR ready (`gh pr ready`)**
- [ ] **Step 5: Confirm GitHub has the commit and report the PR link**

---

## 0.14.0 — saved history

Work on a new feature branch from `main` after the 0.13.x release merges; open a draft PR with the first push.

### Task 22: `ProcessMinuteFolder`

**Files:**
- Create: `src/DeadWare.Core/ProcessHistory.cs`
- Test: `tests/DeadWare.Tests/ProcessMinuteFolderTests.cs`

**Interfaces:**
- Consumes: Task 2 `ProcessActivity`, `ProcessDeltas`; Task 11 `ProcessGrouping.Key`; `UsageMetrics.Floor`, `UsageMetrics.ExpectedSamples`, `UsageMetrics.NormalizeName`.
- Produces:
  - `public static class ProcessHistory { public const string EverythingElse="Everything else"; public const int TopPerMetric=10,MaximumApplications=30; }`
  - `public sealed record ProcessAppMinute(string Name,long CpuTime,long IoRead,long IoWrite,double MemoryByteSeconds,bool EverythingElse);`
  - `public sealed record ProcessMinuteBatch(DateTimeOffset Minute,double CoveredSeconds,int ProcessorCount,bool Complete,string Segment,IReadOnlyList<ProcessAppMinute> Applications);`
  - `public sealed class ProcessMinuteFolder { public ProcessMinuteBatch? Observe(ProcessActivity activity); public ProcessMinuteBatch? Flush(); public void Break(); }`

Breaks and segments mirror `UsageMinuteFolder` (`UsageHistory.cs:76-126`): non-finite or non-increasing monotonic time, a monotonic gap > `max(5, max(intervals)+3)`, or wall/monotonic drift > 3 s break the segment; a later minute closes the previous one. Only `Kind == Available` activities with non-null `DeltaSeconds` add: `CoveredSeconds += DeltaSeconds`, and per process (key `ProcessGrouping.Key`, first casing kept) CPU time, I/O read/write (null deltas add nothing) and memory × Δt; empty-name processes and `Omitted.Deltas` go to Everything else. At close: per-app memory = `MemoryByteSeconds / CoveredSeconds`; keep the union of the top 10 by CPU time, by that memory and by read+write (a zero never qualifies; ties by key ordinal); everything else summed into one Everything else row (always present). `Complete = continuous && publications ≥ ExpectedSamples(interval) − 1`. A minute with zero covered seconds returns null.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Minute_keeps_the_union_of_three_top_tens_at_most_thirty(){ /* 40 apps: 10 CPU leaders, 10 memory leaders, 10 I/O leaders, 10 small */ var b=Close();Assert.Equal(31,b.Applications.Count);Assert.Single(b.Applications,a=>a.EverythingElse);Assert.Equal(smallCpuSum,b.Applications.Single(a=>a.EverythingElse).CpuTime);}
[Fact] public void Overlapping_leaders_list_fewer_than_thirty()=>Assert.Equal(11,CloseWithSameLeaders().Applications.Count);
[Fact] public void Case_variants_fold_into_one_app_with_the_first_casing(){ Assert.Single(b.Applications,a=>a.Name=="Explorer.EXE");Assert.DoesNotContain(b.Applications,a=>a.Name=="explorer.exe");}
[Fact] public void Empty_names_and_omitted_sums_go_to_everything_else()=>Assert.Equal(omitted+unnamed,b.Applications.Single(a=>a.EverythingElse).CpuTime);
[Fact] public void Zero_values_never_qualify_and_ties_break_by_name(){ Assert.DoesNotContain(b.Applications,a=>a.Name=="idle.exe");Assert.Equal(["A.EXE","B.EXE"],TiedOrder(b));}
[Fact] public void Covered_seconds_sum_only_activities_with_deltas()=>Assert.Equal(58,b.CoveredSeconds,6);
[Fact] public void Minute_with_zero_covered_time_is_skipped()=>Assert.Null(CloseWithoutDeltas());
[Fact] public void Clock_jump_breaks_the_segment_and_marks_the_minute_partial(){ Assert.False(b.Complete);Assert.NotEqual(b.Segment,next.Segment);}
[Fact] public void Off_and_unavailable_activities_add_nothing()=>Assert.Equal(0,b.CoveredSeconds);
```

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `ProcessMinuteFolder`.
- [ ] **Step 3: Implement `ProcessHistory.cs`**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `ProcessMinuteFolderTests` pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 23: `ProcessHistoryStore`

**Files:**
- Create: `src/DeadWare.Core/ProcessHistoryStore.cs`
- Test: `tests/DeadWare.Tests/ProcessHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 22 batches; `UsageHistoryView`, `UsageHistoryBucket`, `Summary` (`UsageHistoryStore.cs:7-11`).
- Produces:
  - `public enum ProcessRank {CpuTime,AverageMemory,Io}`
  - `public sealed record ProcessResourceRow(string Name,double CpuSeconds,double AverageCpuPercent,double AverageMemoryBytes,long IoRead,long IoWritten,long MinutesListed,bool EverythingElse);`
  - `public sealed record ProcessResourceView(DateTimeOffset From,DateTimeOffset To,IReadOnlyList<ProcessResourceRow> Rows,ProcessResourceRow EverythingElse,double RankTotal,long RecordedMinutes,DateTimeOffset? FirstMinute,DateTimeOffset? LastMinuteEnd,DateTimeOffset? EarliestRetained,long StorageBytes);`
  - `public sealed class ProcessHistoryStore(string path,long maximumDatabaseBytes=ProcessHistoryStore.MaximumDatabaseBytes){ public const long MaximumDatabaseBytes=128L*1024*1024; public const int RetentionDays=30,MaximumReadDays=31,MaximumRows=1000; public const int ApplicationId=0x44575052; public void Initialize(); public void Append(string machine,IReadOnlyList<ProcessMinuteBatch> batches,DateTimeOffset now); public ProcessResourceView ReadApplications(string machine,DateTimeOffset from,DateTimeOffset to,ProcessRank rank,int limit=200); public UsageHistoryView ReadSeries(string machine,string name,ProcessRank metric,DateTimeOffset from,DateTimeOffset to,int maxPoints); public long StorageBytes{get;} }`

Schema `user_version` 1, `application_id` 0x44575052: `names(id INTEGER PRIMARY KEY, upper TEXT UNIQUE NOT NULL, display TEXT NOT NULL)` (upper = `ToUpperInvariant(name)`, display = first stored casing; no `NOCASE`); `minutes(machine, minute, covered REAL, processors INTEGER, complete INTEGER, segment, PRIMARY KEY(machine, minute))`; `app_minutes(machine, minute, name_id, cpu INTEGER, io_read INTEGER, io_write INTEGER, memory_seconds REAL, PRIMARY KEY(machine, minute, name_id))` with Everything else stored under the reserved name `ProcessHistory.EverythingElse` plus a flag column. Open: inspect read-only with `PRAGMA integrity_check` first, then version and `application_id` (as `ConnectionHistoryStore.cs:74-77`); newer, foreign or corrupt → `InvalidDataException`, file preserved. WAL, `synchronous=FULL`, `journal_size_limit=8388608`. A minute already stored from another segment keeps the first segment's rows and only sets `complete=0`. Cap: on `SQLITE_FULL`-style budget overflow drop the oldest whole day (never today) and retry; delete days older than 30. Reads: `to − from ≤ 31 days` else `ArgumentException`; `limit` and `maxPoints` ≤ 1000. Definitions (spec, Application resources): CPU time, I/O sums over listed minutes; Avg CPU = 100 × CPU seconds ÷ Σ(N × covered) over all recorded minutes of the period; Avg memory = Σ memory_seconds ÷ Σ covered of the app's listed minutes; `RankTotal` = the ranked metric's period total including Everything else (CPU time, Σ memory_seconds, or read+written). `ReadSeries` buckets like `ReadMetric` (`bucket = max(60 000 ms, ⌈period ÷ min(points,1000)⌉)`, aligned to `from`, grouped by `(minute−from)/bucket`): CPU % = 100 × Σcpu ÷ Σ(N×covered) of listed minutes; memory = Σ memory_seconds ÷ Σ covered; I/O = Σ(read+write) ÷ Σ covered (B/s); `Minimum`/`Maximum` = lowest/highest per-minute value; `Minutes` counts listed minutes so a bucket missing any minute is partial.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void One_day_at_the_cap_stays_under_four_mebibytes(){ /* 1,440 complete minutes, same 30 names + Everything else, long.MaxValue/4 counters */ Assert.True(store.StorageBytes<4*1024*1024,$"One day used {store.StorageBytes:N0} bytes.");}
[Fact] public void Cap_and_retention_constants(){ Assert.Equal(128L*1024*1024,ProcessHistoryStore.MaximumDatabaseBytes);Assert.Equal(30,ProcessHistoryStore.RetentionDays);}
[Fact] public void Names_are_unique_on_upper_invariant_and_keep_the_first_casing(){ Append("Explorer.EXE",m0);Append("explorer.exe",m1);Append("Éclair.exe",m2);Append("éCLAIR.EXE",m3);
    var v=Read();Assert.Equal(["Explorer.EXE","Éclair.exe"],v.Rows.Select(r=>r.Name).Order());Assert.Equal(2,v.Rows.Single(r=>r.Name=="Explorer.EXE").MinutesListed);}
[Fact] public void Restart_within_a_minute_keeps_the_first_segment_and_clears_complete(){ /* append minute m seg A cpu 10, then seg B cpu 99 */ Assert.Equal(10,cpuSeconds*1e7);Assert.False(MinuteComplete(m));}
[Theory][InlineData("newer")][InlineData("foreign")][InlineData("corrupt")] public void Unreadable_files_are_preserved_and_reported(string kind){ Assert.Throws<InvalidDataException>(store.Initialize);Assert.Equal(before,File.ReadAllBytes(path));}
[Fact] public void Whole_day_eviction_never_evicts_today(){ /* small cap */ Assert.Contains(today,Days());Assert.DoesNotContain(oldest,Days());}
[Fact] public void Days_older_than_thirty_are_deleted()=>Assert.DoesNotContain(now.AddDays(-31).Date,Days());
[Fact] public void Reads_reject_more_than_31_days()=>Assert.Throws<ArgumentException>(()=>store.ReadApplications("m",now.AddDays(-32),now,ProcessRank.CpuTime));
[Fact] public void Averages_follow_the_definitions(){ /* 2 minutes, N=8, covered 60 each; app cpu 48 s in minute 1 only; mem 1 GiB×60 s in minute 1 */
    Assert.Equal(100*48.0/(8*120),r.AverageCpuPercent,6);Assert.Equal(1024d*1024*1024,r.AverageMemoryBytes,1);Assert.Equal(1,r.MinutesListed);}
[Fact] public void Everything_else_is_present_and_ranked_total_includes_it()=>Assert.Equal(v.Rows.Sum(r=>r.CpuSeconds)+v.EverythingElse.CpuSeconds,v.RankTotal,6);
[Fact] public void Series_buckets_like_ReadMetric_and_leaves_unlisted_minutes_as_gaps(){ /* app listed in minutes 0,1,3 of a 4-minute period, 2 points → bucket 2 min */ Assert.Equal(2,s.Buckets.Count);Assert.Equal(2,s.Buckets[0].Minutes);Assert.Equal(1,s.Buckets[1].Minutes);Assert.Equal(120_000,s.BucketMilliseconds);}
[Fact] public void Series_points_are_capped_at_1000()=>Assert.True(store.ReadSeries("m","a.exe",ProcessRank.Io,now.AddDays(-30),now,4000).Buckets.Count<=1000);
```

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `ProcessHistoryStore`.
- [ ] **Step 3: Implement the store, modelled on `UsageHistoryStore` (append, eviction, `ReadMetric`) and `ConnectionHistoryStore` (read-only inspection)**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `ProcessHistoryStoreTests` pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 24: `ProcessHistoryService` and session wiring

**Files:**
- Create: `src/DeadWare.App/ProcessHistoryService.cs`
- Modify: `src/DeadWare.App/UiSession.cs` (property, construction beside `Usage` at 67–69, `Collector.SampleAvailable` subscription, `Start` at 90, drains at 299–301 as `("process history", Begin(ProcessHistory.StopAsync))` null-guarded like Connections, `SetProcessMonitoring` and `SetUsageRecording` (213) call `ProcessHistory.Configure`)
- Test: `tests/DeadWare.App.Tests/ProcessHistoryServiceTests.cs`, `tests/DeadWare.App.Tests/CoolingShutdownTests.cs` (add a case)

**Interfaces:**
- Consumes: Tasks 22–23.
- Produces: `public sealed class ProcessHistoryService { public ProcessHistoryService(string path,string machineKey,TimeProvider? timeProvider=null,TimeSpan? flushInterval=null); public void Configure(bool monitoring,bool usageRecording); public void Start(); public void TryRecord(Reading reading); public Task StopAsync(); public Task<ProcessResourceView> ReadApplicationsAsync(DateTimeOffset from,DateTimeOffset to,ProcessRank rank); public Task<UsageHistoryView> ReadSeriesAsync(string name,ProcessRank metric,DateTimeOffset from,DateTimeOffset to,int maxPoints); public string Status{get;} public bool Failed{get;} public long DroppedSamples{get;} }`; `UiSession.ProcessHistory` (file `process-history.db` beside `usage-history.db`).

Copies `UsageHistoryService` (`UsageHistoryService.cs:12-137`): 120-slot bounded channel with `TryWrite` and drop counting, enqueuing only `(reading.ProcessActivity, reading.ProcessActivity.Sequence)`; 15 s flush; `Fail` stops only this worker; recording only while `monitoring && usageRecording` (a change flushes the open minute and `Break`s). Drains within Quit's 20 s history cap and sign-out's 3 s budget through the existing `StopCollectorAsync` drains.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory][InlineData(true,true,1)][InlineData(true,false,0)][InlineData(false,true,0)] public async Task Records_only_when_both_switches_are_on(bool monitoring,bool usage,int minutes){ /* feed 2 minutes of activities; StopAsync; read */ Assert.Equal(minutes,view.RecordedMinutes);}
[Fact] public async Task Full_queue_drops_and_counts(){ /* blocked worker, 200 TryRecord */ Assert.Equal(80,service.DroppedSamples);}
[Fact] public async Task Stop_flushes_the_open_minute()=>Assert.Equal(1,(await Read()).RecordedMinutes);
[Fact] public async Task Store_failure_stops_only_this_worker(){ /* path is a directory */ Assert.True(service.Failed);Assert.False(usage.Failed);}
// CoolingShutdownTests
[Fact] public async Task Sign_out_with_an_uninitialized_process_history_is_null_guarded(){ /* StuckSession fixture without ProcessHistory; StopCollectorForSessionEnding completes */ }
```

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `ProcessHistoryService`.
- [ ] **Step 3: Implement the service and the session wiring**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; `ProcessHistoryServiceTests`, `CoolingShutdownTests`, `UsageHistoryServiceTests` pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 25: `UsageHistoryChart` byte unit and optional selection

**Files:**
- Modify: `src/DeadWare.App/UsageHistoryChart.cs` (`Format` at 126: `"B"=>Ui.Bytes(value)`; selection; keyboard)
- Modify: `tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs`

**Interfaces:**
- Produces: on `UsageHistoryChart`: `public bool Selectable{get;set;}` (false by default; when true the chart is `Focusable`), `public int? SelectedIndex{get;set;}` (draws a 1 px hairline in theme `Accent` at the bucket's centre; null draws nothing), `public event Action<int>? BucketActivated` (click on a bucket, or Enter), Left/Right move `SelectedIndex` by one bucket within range, `internal int? BucketAt(Point p)`; `SetData(..., "B", null)` uses an automatic maximum.

- [ ] **Step 1: Write the failing UI checks**

```csharp
static void Byte_unit_labels_use_Ui_Bytes(){ chart.SetData([Trace(512*1024*1024)],"B",null);Render(chart);Assert.Contains(GlyphTexts(chart),t=>t.EndsWith("MB")||t.EndsWith("GB"));}
static void Unselectable_chart_draws_exactly_as_before(){ Assert.Equal(DrawingCount(before),DrawingCount(after));Assert.False(chart.Focusable);}
static void Selection_draws_a_hairline_and_moves_with_arrow_keys(){ chart.Selectable=true;chart.SelectedIndex=2;Assert.Single(Hairlines(chart));Key(chart,Key.Right);Assert.Equal(3,chart.SelectedIndex);Key(chart,Key.Enter);Assert.Equal(3,activated);}
static void Click_activates_the_bucket_under_the_pointer(){ Click(chart,BucketCentre(chart,1));Assert.Equal(1,activated);}
```

- [ ] **Step 2: Commit the failing checks, push and verify failure** — Expected: `error CS1061` for `Selectable`/`SelectedIndex`.
- [ ] **Step 3: Implement the unit, selection, hairline and keys**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; the UI fact passes phase `"usage history"`.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 26: Application resources card and the switch text

**Files:**
- Create: `src/DeadWare.App/ApplicationResourcesCard.cs`
- Modify: `src/DeadWare.App/UsageHistoryPage.cs` (insert the card after the Applications card at 88 and before "Keep a longer record" at 90; load it in `LoadAsync` at 162–182; description at 93; privacy line at 94)
- Modify: `src/DeadWare.App/Windows.cs:158` (usage help line), `:193-195` (Per-process monitoring help), `src/DeadWare.Core/ProcessTable.cs` (`ProcessCopy.ScanSentence` gains an optional last parameter `bool history=false`; true appends `" While usage history is recorded, it also keeps the busiest apps for 30 days."`)
- Create: `tests/DeadWare.App.Tests/ApplicationResourcesUiChecks.cs`; Modify: `tests/DeadWare.App.Tests/BatteryUiTests.cs` (phase `"application resources card"` after `"usage history"`), `tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs`, `tests/DeadWare.App.Tests/ShellUiChecks.cs`, `tests/DeadWare.Tests/ProcessSettingsTests.cs`

**Interfaces:**
- Consumes: Task 23 `ProcessResourceView`, Task 24 `ProcessHistoryService`, Task 25 chart.
- Produces: `internal sealed class ApplicationResourcesCard : StackPanel { public ApplicationResourcesCard(UiSession session); internal void Apply(ProcessResourceView view,DateTimeOffset now); internal ProcessRank Rank{get;} internal string? SelectedApp{get;} internal UsageHistoryChart Chart{get;} }`

Card (spec, Usage history: Application resources): heading **Application resources**; Rank by **CPU time** / **Average memory** / **I/O**; search box ("Find application", automation name "Find application in application resources"); share bar of `RankTotal`; table columns `Application · CPU time · Avg CPU · Avg memory while listed · I/O read · I/O written · Minutes listed`, Everything else as the last row; selecting a row shows its `UsageHistoryChart` with one series: CPU % (`"%"`, fixed 100), memory (`"B"`, automatic) or I/O (`"B/s"`); changing Rank by redraws for the same app; gap notice `$"{gaps:N0} minutes in this period have no process data (monitoring off, DeadWare not running, or a capture gap)."` (gaps = elapsed period minutes − `RecordedMinutes`) and a storage line `$"process-history.db · {Ui.Bytes(StorageBytes)}"`. Text updates, exact:
- Settings usage help (`Windows.cs:158`) keeps its current text and appends `" While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory and I/O for 30 days."`
- Keep a longer record description keeps "up to 400 days" and appends `" While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory and I/O for 30 days (up to 128 MiB)."`
- Privacy line becomes `"History stays in this Windows profile in usage-history.db and process-history.db and is never uploaded. Application names and byte totals describe what runs on this computer, so treat exports as private. Back up the databases only while DeadWare is fully quit."`
- Processes off state and Per-process monitoring help use `ScanSentence(…, history:true)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// ApplicationResourcesUiChecks
static void Card_sits_between_applications_and_keep_a_longer_record()=>Assert.Equal(["Device","Applications","Application resources","Keep a longer record"],CardHeadings(page));
static void Table_has_the_seven_columns_and_everything_else_last(){ Assert.Equal(["Application","CPU time","Avg CPU","Avg memory while listed","I/O read","I/O written","Minutes listed"],Headers(card));Assert.Equal("Everything else",LastRowName(card));}
static void Selecting_an_app_shows_its_chart_and_rank_by_redraws(){ Select(card,"chrome.exe");Assert.Equal("%",ChartUnit(card));SetRank(card,ProcessRank.AverageMemory);Assert.Equal("B",ChartUnit(card));Assert.Equal("chrome.exe",card.SelectedApp);}
static void Gap_notice_counts_minutes_without_process_data()=>Assert.Contains("minutes in this period have no process data",Texts(card));
// UsageHistoryUiChecks: description still contains "up to 400 days" and ends "for 30 days (up to 128 MiB)."; privacy line equals the new text
// ShellUiChecks: CardOf("Keeps day, week and month history") is still "History and alerts"; that help ends with "for 30 days."
// ProcessSettingsTests
[Fact] public void Scan_sentence_adds_the_history_sentence()=>Assert.Equal("Turning it on adds one Windows process scan per poll. While usage history is recorded, it also keeps the busiest apps for 30 days.",ProcessCopy.ScanSentence(false,false,false,history:true));
```

- [ ] **Step 2: Commit the failing tests, push and verify failure** — Expected: `error CS0246` for `ApplicationResourcesCard`.
- [ ] **Step 3: Implement the card, the page wiring and the text updates**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; the UI fact passes phases `"usage history"` and `"application resources card"`; `ProcessSettingsTests` pass.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 27: Click a moment

**Files:**
- Modify: `src/DeadWare.App/UsageHistoryPage.cs` (CPU and RAM Device charts `Selectable=true`; `BucketActivated` → card), `src/DeadWare.App/ApplicationResourcesCard.cs`
- Modify: `tests/DeadWare.App.Tests/ApplicationResourcesUiChecks.cs`

**Interfaces:**
- Consumes: Task 25 `BucketActivated`/`SelectedIndex`; Task 23 `ReadApplicationsAsync`; the page's `Period()` and the CPU chart's `points` (`UsageHistoryPage.cs:165`).
- Produces: `internal void ShowSlot(DateTimeOffset slotStart,DateTimeOffset slotEnd,ProcessRank rank)`, `internal void ClearSlot()` on the card; `internal static string SlotHeading(DateTimeOffset first,DateTimeOffset lastEnd,bool withDate)` → `"Busiest apps · 03:13–03:16"` / `"Busiest apps · Nov 3 03:05–03:28"` (local time, `MMM d HH:mm`).

Slot = `[from + k × bucket, from + (k+1) × bucket)` clipped to the period end, with `bucket = CPU view's BucketMilliseconds` (shared by CPU and RAM). CPU click ranks by CPU time, RAM by average memory; the list includes Everything else; the heading names `FirstMinute` to `LastMinuteEnd` with the date on Week and Month; the hairline is set on both charts; Esc or **Clear** calls `ClearSlot()` and clears both `SelectedIndex`. A slot with `RecordedMinutes == 0` shows `"No process data in this span."` and Clear stays available.

- [ ] **Step 1: Write the failing checks**

```csharp
static void Clicking_a_cpu_point_lists_the_busiest_apps_in_its_slot(){ Activate(page.Charts["cpu"],1);Assert.StartsWith("Busiest apps · ",Heading(card));Assert.Equal(ProcessRank.CpuTime,card.Rank);Assert.Equal("Everything else",LastRowName(card));}
static void Ram_click_ranks_by_average_memory()=>Assert.Equal(ProcessRank.AverageMemory,AfterActivate("memory").Rank);
static void Both_charts_show_the_hairline_and_clear_removes_it(){ Assert.Equal(1,page.Charts["cpu"].SelectedIndex);Assert.Equal(1,page.Charts["memory"].SelectedIndex);Click(Button(card,"Clear"));Assert.Null(page.Charts["cpu"].SelectedIndex);}
static void Slot_heading_formats(){ Assert.Equal("Busiest apps · 03:13–03:16",ApplicationResourcesCard.SlotHeading(L(3,13),L(3,16),false));Assert.Equal("Busiest apps · Nov 3 03:05–03:28",ApplicationResourcesCard.SlotHeading(L(3,5),L(3,28),true));}
static void Clicking_a_slot_without_process_data_says_so(){ Activate(EmptySlot);Assert.Contains("No process data in this span.",Texts(card));Key(card,Key.Escape);Assert.Equal("Application resources",Heading(card));}
```

- [ ] **Step 2: Commit the failing checks, push and verify failure** — Expected: `error CS1061` for `ShowSlot`/`SlotHeading`.
- [ ] **Step 3: Implement click-a-moment**
- [ ] **Step 4: Commit, push and verify pass** — Expected: green; the UI fact passes phase `"application resources card"`.
- [ ] **Step 5: Confirm GitHub has the commit**

---

### Task 28: 0.14.0 release docs and version

**Files:**
- Modify: `Directory.Build.props` (`0.14.0`), `CHANGELOG.md` (`## 0.14.0 — Application resource history`), `README.md` (Usage history section: Application resources and click a moment; privacy: app names kept 30 days in `process-history.db`, never uploaded), `docs/validation.md` (`## Application resource history (0.14.0, <date>)`: the one-day size test result, pending by eye: a real day of recording and its file size)
- Create: `docs/releases/v0.14.0.md`

- [ ] **Step 1: Write the docs and bump the version**
- [ ] **Step 2: Commit and push** (`docs: application resource history release notes and 0.14.0`)
- [ ] **Step 3: Run (CI)** — Expected: green.
- [ ] **Step 4: Mark the PR ready (`gh pr ready`)**
- [ ] **Step 5: Confirm GitHub has the commit and report the PR link; the release waits for the user's go**
