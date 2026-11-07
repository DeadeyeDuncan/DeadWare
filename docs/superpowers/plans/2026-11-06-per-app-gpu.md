# Per-app GPU (0.15.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship 0.15.0: per-app GPU utilization and GPU memory on the Processes page and in the 30-day Application resources history, read from Windows' GPU performance counters behind the existing Per-process monitoring switch.

**Architecture:** `WindowsGpuCounters` (App) owns one PDH query and the DXGI adapter list behind Core's `IGpuCounterApi`. Core's `GpuCounterReader` classifies every PDH result into a `GpuCounterRead`, and `GpuProcessJoin`, called from `ProcessActivityTracker.Update` before the 4,096 cut, puts GPU %, engine and memory on each `ProcessSample` and GPU-seconds into its deltas. The collector reads just before the scan; a runtime `GpuPlacementGuard` moves the read to a `GpuCounterLoop` when reads are slow. The page, `ProcessMinuteFolder` (a fourth top 10), `process-history.db` version 2 (192 MiB) and the Application resources card follow, with click a moment on the GPU chart.

**Tech Stack:** .NET 10, WPF, `pdh.dll` (`PdhOpenQueryW`, `PdhAddEnglishCounterW`, `PdhCollectQueryData`, `PdhGetFormattedCounterArrayW`, `PdhCloseQuery`), `dxgi.dll` (`CreateDXGIFactory1`, `IDXGIFactory1.EnumAdapters1`, `IDXGIAdapter1.GetDesc1`), Microsoft.Data.Sqlite (existing), xUnit 2.9.3. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-06-per-app-gpu-design.md`, approved 2026-11-06 and binding, including its deliberate departures from the Processes spec: the GPU first-sample rule (the opposite of CPU and I/O); 192 MiB in place of 128 MiB, with 40 history rows a minute in place of 30 and format version 2; placement decided at run time by a guard, not by the CI measurement, with no `ProcessFeatures` switch; the read cost on a real GPU as a manual check; GPU ENGINE as a text column sorted ascending on the first click; Everything else's Avg GPU shown unclamped. The four GPU status texts are new copy. Where it is silent, `docs/superpowers/specs/2026-11-05-processes-design.md` applies. Executors read both. The Processes spec is not edited: the GPU spec states which of its rules it replaces.

## Builds on 0.14.1

This plan starts from `main` after the 0.14.1 cleanup release (`/home/user/DeadWare-cleanup/docs/superpowers/plans/2026-11-06-cleanup-0.14.1.md`) has merged. It never re-implements 0.14.1 work. Where it touches the same members, it uses 0.14.1's names:

- `ProcessHistory.SavedName(string?)`: the folder's target rule (`if(ProcessHistory.SavedName(p.Name) is not{} name)target=other;`) and the store's name clause in `ValidateBatch`. `ProcessHistoryStore.MaximumNameLength` no longer exists.
- `Collector.RunAsync`'s `finally` already requests `diskStop` before `computer?.Close()` and awaits it with `StopEtwAsync()`.
- `App.DiagnosticReport(string status,string? stage,int deadlineSeconds,CollectorOptions options,IReadOnlyList<Reading> samples)` and `Reading.NetworkApplications`.
- `UsageHistoryChart.SelectionMoved`, and on `UsageHistoryPage`: `marked`, `MoveMoment(string kind,int index)`, `Middle(UsageHistoryBucket)` and `MarkMoment()`, which loop over `Moments`.
- `ApplicationResourcesCard`: the `ResourceRow` class with `Key`, `Name`, six cell properties and `Update(ProcessResourceRow)`, the `rows` collection and `Key(ProcessResourceRow)`.
- `ProcessesPage.PageKey` and `MainWindow.PageKey`: unchanged here; F5 keeps re-sorting through `Render(resort:true)`.
- Tests: `ProcessTableTests.Full()` and `Each_column_key_reads_its_own_field`; `One_day_at_the_cap_stays_under_four_mebibytes` with machine `"DESKTOP-ABCDEFG"`; in `ApplicationResourcesUiChecks`, `Left_and_right_mark_both_charts_and_the_minute_refresh_keeps_the_cursor` and `Esc_clears_a_cursor_that_lists_nothing_and_the_refresh_keeps_it_cleared`, run right after `The_minute_refresh_keeps_the_slot_and_moves_its_hairlines_with_the_grid`. The second ends with a CPU slot listed for `A_new_period_clears_the_moment`.

## Global Constraints

From the spec, verbatim:

- "Where this document is silent, the Processes spec's rules apply unchanged (null, never zero, for an unknown value; every "—" has a tooltip naming its reason; status text holds fixed copy, counts and error codes, never names or paths; all native work off the UI thread; colours from theme resources only)."
- "per-app GPU runs behind the existing **Per-process monitoring** switch (still off by default) and needs no administrator rights"
- "It adds three wildcard counters with `PdhAddEnglishCounterW`; the paths are English names, and `PdhAddCounterW` expects localized names, so it would fail on non-English Windows." The paths: `\GPU Engine(*)\Utilization Percentage`, `\GPU Process Memory(*)\Dedicated Usage`, `\GPU Process Memory(*)\Shared Usage`.
- "`PDH_FMT_DOUBLE` for utilization, `PDH_FMT_LARGE` for memory; `PDH_MORE_DATA` is the buffer-sizing step, not an error."
- "No `ProcessFeatures` switch is added."
- Column keys: "`pid, cpu, memory, io, ioread, iowrite, disk, diskread, diskwrite, network, download, upload, gpu, gpuengine, gpumemory, gpushared`. Headers: `GPU · GPU ENGINE · GPU MEMORY · SHARED GPU MEMORY`. None of the GPU keys is administrator-only."
- "**Defaults** become `cpu, memory, io, disk, network, gpu, gpumemory`."
- The four GPU column statuses: "GPU counters unavailable (error 0x{status:X8})", "GPU counters unavailable", "GPU counters unreadable", "Too many GPU counter instances".
- Caps: "at most the 32 busiest per process"; "a counter's array held more than 65,536 items"; "`ProcessHistory.MaximumApplications` becomes 40 (four lists of 10) plus Everything else"; "retention stays 30 days; the cap rises from 128 MiB to 192 MiB".
- Placement: "when more than one of them exceeds 15 ms (their p95 is above 15 ms, the Processes spec's threshold), the read moves to its own loop until Per-process monitoring is turned off or DeadWare restarts." The loop has "a period of max(1 s, the polling interval)".
- "Live per-process GPU values stay in memory. History stores only app names (as 0.14.0) and numbers. Adapter descriptions are hardware names shown in tooltips; they are not stored in history."
- "Out of scope: per-app GPU temperature or power, GPU columns on the overview cards, alerts on per-app GPU, D3DKMT or ETW sources, shared GPU memory in history, a per-app GPU memory chart."
- Manual checks are "pending by eye, never claimed as verified".
- "0.15.0, one PR from main after 0.14.1 has merged."

For this plan:

- Every task's requirements include the spec. Where this plan and the spec differ, the spec wins.
- Local checks run on Linux with .NET SDK 10.0.114.
  - Build: `dotnet build DeadWare.slnx -c Release -p:EnableWindowsTargeting=true --nologo`. Expected: 0 errors, and only the warnings `main` had at the branch point (Task 1 Step 0 records the count), none in a file the task touched.
  - Core tests: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "<filter>"`.
  - **Core baseline**: the full Core run fails exactly the 26 Windows-only tests in `/home/user/DeadWare-processes/.superpowers/sdd/2026-11-05-processes/linux-baseline-failures.txt`. This prints nothing:
    `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo 2>&1 | grep -E '^  Failed ' | sed -E 's/ \[[^]]*\]$//' | sort | diff - <(sort /home/user/DeadWare-processes/.superpowers/sdd/2026-11-05-processes/linux-baseline-failures.txt)`
- App tests (`tests/DeadWare.App.Tests`) and anything that needs real PDH, DXGI or a GPU run only in Windows CI (GitHub `windows-latest`, which may have no GPU). Locally they only need to compile. UI checks run inside the single STA fact `BatteryUiTests.Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows`. A UI check "passes in CI" when that fact passes, and "fails" when the fact fails during the named phase.
- Red checks:
  - A test that needs a new member is red when the local build fails with the named `error CS…`.
  - A test that compiles without the change is red only in CI. Push its `test(...)` commit alone and confirm the named failure before pushing the change. CI's `dotnet test DeadWare.slnx` runs both test projects, so Core failures in the same commit do not hide the UI fact's.
- Core verification commands are written out in full where a task first runs them. A later step that says "Run the Step N command" means exactly that command.
- **Run (CI)** means: `git push`, then the pull request's `build` check on the pushed head must finish green. The `gh` CLI is limited to search here, so whoever runs the plan watches checks, reads failing job logs and fetches artifacts through the GitHub tools (`pull_request_read` → `get_check_runs`, `get_job_logs`; `actions_list` → `list_workflow_run_artifacts`; `actions_get` → `download_workflow_run_artifact`), never `gh`. Under subagent-driven execution the controller does this; implementers push and return without waiting. `build.yml` cancels an in-progress run when a newer push arrives, so let each run finish before pushing again.
- Workflow:
  - One branch, `claude/per-app-gpu-0.15.0`, from `origin/main` after 0.14.1 has merged, with one pull request to `main`. Open it as a draft with Task 1's push: CI runs on `claude/*` branches only through a pull request.
  - Fetch and inspect `origin` before each task. Keep unrelated local work. Never force-push or rewrite pushed commits.
  - Each task ends with its commits pushed, `git fetch origin && git status -sb` showing no ahead or behind, and CI green, as `AGENTS.md` requires.
  - Tasks 4–14 start only after Task 2 has recorded the CI floor in the PR description. Mark the PR ready after Task 14. Merging and tagging v0.15.0 wait for the user's go-ahead.
- Commits use `test(...)` for tests, `feat(...)` for features, `fix(...)` for fixes and `docs:` for docs.
  - Every commit message ends with two lines: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_017AZ6FXcD7T9s63fqUnw9Js`. The `git commit -m` commands below show the subject only; add the two lines as a final `-m` paragraph.
  - The PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`, a blank line, then the same session link.
  - Never commit anything under `.superpowers/`.
- Line numbers are at `dd4fb74` (v0.14.0). 0.14.1 moves some of them; anchor on the quoted text or member.
- Diagnostics data added by this release is counts, states and timings only. Adapter descriptions and process names never reach `--diagnose`, status text or `process-history.db`.
- Real-Windows tests: `[ElevatedGitHubFact]` checks fail the build; `[GitHubWindowsFact]` measurements never skip on GitHub and record what they find. Every class under `RealWindows/` carries `[Collection("RealWindows")]`.
- Release: `<Version>0.15.0</Version>` and the heading `## 0.15.0 — Per-app GPU`.

## Review Focus

1. The polling interval changes while the read runs in its own loop (0.5 s → 2 s → 0.5 s). Expected: the loop period follows max(1 s, interval), each read is joined with credit at most once, and no read's interval exceeds `PollingPolicy.FreshnessSeconds(Math.Max(previous interval, current interval))`, the tracker's rule, so no first-sample storm. Tests in Task 3 (`Interval_changes_keep_each_read_fresh_once`) and Task 1 (`A_slower_previous_interval_keeps_its_freshness_limit`).
2. Per-process monitoring is turned off and on within one loop period while a slow read is in flight. Expected: the query is never used by two threads at once, it is closed once the read in flight ends, placement returns to every poll after On, and the first read after On is a first sample. Test in Task 5 (`Rapid_toggles_during_a_loop_read_never_read_concurrently`).
3. GPU counters exist but no process has used the GPU yet (an idle VM or a fresh boot), and then one does. Expected: "GPU counters unavailable" while the GPU Engine set is empty, not 0 % for every process; the query is never reopened for it; values appear once instances exist. Test in Task 1 (`Engines_that_appear_later_are_read_without_reopening`).
4. The dashboard's GPU selection is cleared while a GPU moment is marked or listed. Expected: the GPU chart shows no hairline, the CPU and RAM hairlines and the listed slot stay, and nothing throws. Test in Task 13 (`Clearing_the_dashboard_gpu_keeps_the_listed_gpu_moment`).
5. Rank by GPU on the first day after the upgrade, over a period holding only version 1 minutes. Expected: the rows rank by name, every Avg GPU cell is "—" with "No GPU data in this range", the share bar is hidden, and a selected app's GPU chart is all gaps. Test in Task 12 (`Rank_by_gpu_over_version_1_minutes_shows_dashes_and_no_share_bar`).

---

## 0.15.0 — Step 1: the counter read, its CI floor and the runtime guard

### Task 1: Core counter instances, read classification and adapter naming

**Files:**
- Create: `src/DeadWare.Core/GpuCounters.cs`
- Test: `tests/DeadWare.Tests/GpuCounterTests.cs`

**Interfaces:**
- Consumes: `ProcessColumnState` (`ProcessModels.cs`), `PollingPolicy.FreshnessSeconds`.
- Produces:
  - `public enum GpuCounterKind{Engine,Dedicated,Shared}`
  - `public sealed record GpuCounterInstance(int Pid,long Luid,int Phys,int? Engine,string? Type){ public static bool TryParse(string? name,GpuCounterKind kind,[NotNullWhen(true)] out GpuCounterInstance? instance); }`. Engine and Type are null for memory names. LUID = ((long)hi << 32) | lo, both halves unsigned. `<type>` is everything after the first `_engtype_`, kept verbatim ("" allowed). Each LUID half is 1–8 hex digits of either case, checked by length and parsed with `NumberStyles.AllowHexSpecifier` and `CultureInfo.InvariantCulture`. Decimals are parsed with `NumberStyles.None` and `CultureInfo.InvariantCulture` (no sign, no whitespace) and must fit `int`. Never throws.
  - `public static class GpuPdhStatus { public const uint CStatusValidData=0x00000000,CStatusNewData=0x00000001,CStatusNoInstance=0x800007D1,MoreData=0x800007D2,NoData=0x800007D5,CalcNegativeDenominator=0x800007D6,CalcNegativeValue=0x800007D8,CStatusNoObject=0xC0000BB8,CStatusNoCounter=0xC0000BB9,InvalidData=0xC0000BC6; }`. These are the `pdhmsg.h` values; cross-check them against the Windows SDK header.
  - `public readonly record struct GpuRawItem(string Name,uint CStatus,double Value);`: percent for engines, bytes for memory (`PDH_FMT_LARGE` values as double, exact below 2^53).
  - `public sealed record GpuRawArray(uint Status,IReadOnlyList<GpuRawItem> Items);`
  - `public sealed record GpuAdapterInfo(long Luid,string Description,uint Flags){ public const uint SoftwareFlag=2; public bool Software=>(Flags&SoftwareFlag)!=0; }`
  - `public interface IGpuCounterApi { uint Open(); uint Collect(); GpuRawArray Read(GpuCounterKind kind); IReadOnlyList<GpuAdapterInfo>? Adapters(); void Close(); }`. `Open` returns the first failing status of `PdhOpenQueryW` and the three adds, or 0; after a failing add it has already closed its query and holds no handle. `Adapters` returns null on a DXGI failure. `Close` is idempotent.
  - `public sealed record GpuAdapterName(int? Number,string? Description){ public static GpuAdapterName Unknown{get;}=new(null,null); public string Name=>Number is{} n?$"GPU {n}":"GPU ?"; public string Label(string type)=>$"{Name} · {(type.Length==0?"Engine":type)}"; }`
  - `public sealed record GpuEngineItem(GpuCounterInstance Instance,bool Ready,double Percent);` and `public sealed record GpuMemoryItem(GpuCounterInstance Instance,bool Ready,long Bytes);`
  - `public sealed record GpuCounterRead(ProcessColumnState State,DateTimeOffset UtcTime,double MonotonicSeconds,DateTimeOffset? PreviousUtcTime,double? IntervalSeconds,IReadOnlyList<GpuEngineItem> Engines,IReadOnlyList<GpuMemoryItem> Dedicated,IReadOnlyList<GpuMemoryItem> Shared,IReadOnlyDictionary<long,GpuAdapterName> Adapters,int Instances,int Skipped)`, with init members `EnginesReady`, `DedicatedReady`, `SharedReady` and `Credit`, each defaulting to true, and `public static GpuCounterRead Unavailable(string status,double monotonicSeconds,DateTimeOffset utcTime)` (empty lists and no previous collection). `Instances` counts the items of all three arrays.
  - `public static class GpuCounterStatus { public const string Unavailable="GPU counters unavailable",Unreadable="GPU counters unreadable",TooMany="Too many GPU counter instances"; public static string Error(uint status)=>$"GPU counters unavailable (error 0x{status:X8})"; }`
  - `public sealed class GpuCounterReader(IGpuCounterApi api){ public const double RetrySeconds=60,NotReadySeconds=60,AdapterRereadSeconds=60; public const int MaximumItems=65536; public GpuCounterRead Read(double monotonicSeconds,DateTimeOffset utcTime,double pollingIntervalSeconds); public void Close(); public bool IsOpen{get;} public bool LastReadOpened{get;} }`

`Read` follows the spec's Data source and Errors and limits. The plan decides what the spec leaves open:
- While a retry is not yet due, `Read` returns the last failure's state without calling the API. A successful open reads `Adapters()` and sets `LastReadOpened` for that read. After any failed `Open` the reader also calls `api.Close()`.
- Freshness follows the tracker's Δt rule: the reader keeps the polling interval of its previous successful collection, and the previous collection is null when the interval is not positive or exceeds `PollingPolicy.FreshnessSeconds(Math.Max(that interval, pollingIntervalSeconds))`.
- A `Collect` that returns `NoData` or `CStatusNoInstance` keeps the query open and does not become the previous collection.
- No engine instances means `NoData` or `CStatusNoInstance` from the collect call or the engine array call, or an engine array with status 0 and zero items. A benign array status is never "no instances".
- A benign array status (`InvalidData`, `CalcNegativeDenominator`, `CalcNegativeValue`) returns no items, so it sets that counter's `…Ready` to false. The not-ready run starts at the first read with any benign status and ends at a read with none. The 60 s failure uses that read's status.
- An exception from `Open`, `Collect` or `Read(kind)` is a failure with state `GpuCounterStatus.Unavailable` (no code): the reader calls `api.Close()` (itself guarded) and waits `RetrySeconds` before the next open, as for a failing status. An exception from `Adapters()` counts as a DXGI failure (null). So `GpuCounterReader.Read` never throws, and a persistent exception never reopens the query on every poll.
- State order, first match: `Error`, `TooMany`, `Unavailable` (no engine instances), `Unreadable` (`Skipped × 2 > Instances`), else `Ready`.
- A software adapter keeps its description with `Number` null. A LUID DXGI does not list is absent from `Adapters`, and callers use `GpuAdapterName.Unknown`.

- [ ] **Step 0: Fetch, check 0.14.1 is on main, create the branch and record the warning count**

```bash
git fetch origin
git status --short --branch                                               # unrelated local work stays where it is
git show origin/main:Directory.Build.props | grep -c '<Version>0.14.1</Version>'      # 1; if 0, stop: 0.14.1 has not merged
git show origin/main:src/DeadWare.Core/ProcessHistory.cs | grep -c 'SavedName'         # at least 1
git ls-remote --heads origin claude/per-app-gpu-0.15.0                    # empty before the first push; if not, continue that branch without force-pushing
git switch -c claude/per-app-gpu-0.15.0 origin/main
git checkout claude/determined-keller-uggb29 -- docs/superpowers/specs/2026-11-06-per-app-gpu-design.md
# The plan: check it out the same way if it is committed on claude/determined-keller-uggb29, otherwise copy the file from that worktree.
git add docs/superpowers/specs/2026-11-06-per-app-gpu-design.md docs/superpowers/plans/2026-11-06-per-app-gpu.md
git commit -m "docs: per-app GPU spec and plan (0.15.0)"
dotnet build DeadWare.slnx -c Release -p:EnableWindowsTargeting=true --nologo 2>&1 | grep -cE 'warning [A-Z]+[0-9]+'   # record this count in the PR body
```

- [ ] **Step 1: Write the failing tests**

```csharp
// FakeGpuApi : IGpuCounterApi holds OpenStatus, CollectStatus, Arrays[kind] (default new(0,[]): an empty engine array is "no instances") and Adapters
// (default [new(0xA,"NVIDIA GeForce RTX 4070",0)]), and counts Opens, Closes, Collects and AdapterReads. Throws names the call that throws
// InvalidOperationException ("" for none, "Open", "Collect", "Read", "Adapters"). E(pid,eng,value,cstatus=0,luid=0xA,phys=0,type="3D") and M(pid,bytes,cstatus=0,luid=0xA,phys=0)
// build items with real names ("pid_8_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D"); Bad(n) is a name that does not parse.
// T(s)=Epoch.AddSeconds(s); R(s,interval=1)=reader.Read(s,T(s),interval).
[Theory]
[InlineData("pid_1234_luid_0x00000000_0x0000D3A2_phys_0_eng_3_engtype_3D",1234,0xD3A2L,0,3,"3D")]
[InlineData("pid_8_luid_0x1_0xFFFFFFFF_phys_1_eng_12_engtype_Compute_0",8,0x1FFFFFFFFL,1,12,"Compute_0")]
[InlineData("pid_8_luid_0x0_0xa_phys_0_eng_0_engtype_High Priority 3D",8,0xAL,0,0,"High Priority 3D")]
[InlineData("pid_8_luid_0x0_0xA_phys_0_eng_2_engtype_Graphics_1",8,0xAL,0,2,"Graphics_1")]
[InlineData("pid_8_luid_0x0_0xA_phys_0_eng_0_engtype_",8,0xAL,0,0,"")]
public void Engine_names_parse(string name,int pid,long luid,int phys,int engine,string type){
    Assert.True(GpuCounterInstance.TryParse(name,GpuCounterKind.Engine,out var i));Assert.Equal(new GpuCounterInstance(pid,luid,phys,engine,type),i);}
[Fact] public void Memory_names_parse(){ Assert.True(GpuCounterInstance.TryParse("pid_1234_luid_0x00000000_0x0000D3A2_phys_1",GpuCounterKind.Dedicated,out var i));
    Assert.Equal(new GpuCounterInstance(1234,0xD3A2,1,null,null),i);}
[Theory][InlineData(null)][InlineData("")][InlineData("pid_x_luid_0x0_0xA_phys_0")][InlineData("pid_1_luid_0x123456789_0xA_phys_0")][InlineData("pid_1_luid_0x_0xA_phys_0")]
[InlineData("pid_1_luid_0x0_0xG_phys_0")][InlineData("pid_1_luid_0x0_0xA_phys_0_eng_1")][InlineData("pid_1_luid_0x0_0xA_phys_0_tail")][InlineData("pid_99999999999_luid_0x0_0xA_phys_0")]
[InlineData("pid_+8_luid_0x0_0xA_phys_0")][InlineData("pid_ 8_luid_0x0_0xA_phys_0")][InlineData("pid_8_luid_0x000000000_0xA_phys_0")][InlineData("pid_8_luid_0x 1_0xA_phys_0")]
public void Malformed_names_do_not_parse(string? name){ Assert.False(GpuCounterInstance.TryParse(name,GpuCounterKind.Engine,out _));Assert.False(GpuCounterInstance.TryParse(name,GpuCounterKind.Shared,out _));}
[Fact] public void Engine_and_memory_shapes_do_not_cross(){ Assert.False(GpuCounterInstance.TryParse("pid_8_luid_0x0_0xA_phys_0",GpuCounterKind.Engine,out _));
    Assert.False(GpuCounterInstance.TryParse("pid_8_luid_0x0_0xA_phys_0_eng_0_engtype_3D",GpuCounterKind.Dedicated,out _));}

[Fact] public void Items_are_ready_only_with_valid_or_new_data_and_never_skipped(){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,40),E(8,1,10,cstatus:GpuPdhStatus.CStatusNewData),E(9,0,99,cstatus:0x800007D3)]);
    var r=R(0);Assert.Equal([true,true,false],r.Engines.Select(e=>e.Ready));Assert.Equal(0,r.Skipped);Assert.Equal(3,r.Instances);Assert.True(r.State.Available);}
[Fact] public void The_first_read_after_open_has_no_previous_collection(){ var first=R(10);Assert.True(reader.LastReadOpened);Assert.Null(first.PreviousUtcTime);Assert.Null(first.IntervalSeconds);
    var second=R(11.5);Assert.False(reader.LastReadOpened);Assert.Equal(T(10),second.PreviousUtcTime);Assert.Equal(1.5,second.IntervalSeconds!.Value,6);}
[Theory][InlineData(4.01)][InlineData(0.0)][InlineData(-1.0)]
public void An_interval_outside_freshness_has_no_previous_collection(double dt){ R(10);var r=R(10+dt);Assert.Null(r.PreviousUtcTime);Assert.Null(r.IntervalSeconds);}   // 1 s polling: 4 s
[Fact] public void A_slower_previous_interval_keeps_its_freshness_limit(){ R(0,interval:10);var r=R(10,interval:0.5);   // FreshnessSeconds(Math.Max(10,0.5)) = 13 s, as the tracker
    Assert.Equal(T(0),r.PreviousUtcTime);Assert.Equal(10,r.IntervalSeconds!.Value,6);Assert.Null(R(20,interval:0.5).IntervalSeconds);}   // then 0.5 s on both sides: 4 s
[Theory][InlineData(GpuPdhStatus.InvalidData)][InlineData(GpuPdhStatus.CalcNegativeDenominator)][InlineData(GpuPdhStatus.CalcNegativeValue)]
public void A_benign_array_status_is_not_ready_and_fails_only_after_60_s(uint status){ api.Arrays[GpuCounterKind.Engine]=new(status,[]);
    foreach(double s in new[]{0,30,59.9}){var r=R(s);Assert.True(r.State.Available);Assert.False(r.EnginesReady);Assert.Equal(0,r.Skipped);}
    Assert.Equal(new ProcessColumnState(false,$"GPU counters unavailable (error 0x{status:X8})"),R(60).State);Assert.Equal(1,api.Closes);
    api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);R(119.9);Assert.Equal(1,api.Opens);
    var reopened=R(120);Assert.Equal(2,api.Opens);Assert.True(reopened.State.Available);Assert.Null(reopened.PreviousUtcTime);}
[Fact] public void A_ready_read_ends_the_not_ready_run(){ api.Arrays[GpuCounterKind.Engine]=new(GpuPdhStatus.InvalidData,[]);R(0);R(50);
    api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);R(55);api.Arrays[GpuCounterKind.Engine]=new(GpuPdhStatus.InvalidData,[]);R(56);Assert.True(R(114).State.Available);Assert.Equal(0,api.Closes);}
[Theory][InlineData(GpuPdhStatus.NoData)][InlineData(GpuPdhStatus.CStatusNoInstance)][InlineData(0u)]
public void No_engine_instances_make_the_column_unavailable_and_keep_the_query_open(uint status){ api.Arrays[GpuCounterKind.Engine]=new(status,[]);api.Arrays[GpuCounterKind.Dedicated]=new(0,[M(8,100)]);
    Assert.Equal(new ProcessColumnState(false,"GPU counters unavailable"),R(0).State);Assert.True(reader.IsOpen);Assert.Equal(0,api.Closes);}
[Theory][InlineData(GpuPdhStatus.NoData)][InlineData(GpuPdhStatus.CStatusNoInstance)]
public void No_instances_from_the_collect_call_keep_the_query_open_and_the_previous_collection(uint status){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);
    Assert.True(R(0).State.Available);api.CollectStatus=status;Assert.Equal("GPU counters unavailable",R(1).State.Status);Assert.True(reader.IsOpen);Assert.Equal(0,api.Closes);
    api.CollectStatus=0;var r=R(2);Assert.True(r.State.Available);Assert.Equal(T(0),r.PreviousUtcTime);}
[Fact] public void No_memory_instances_are_known_empty(){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);api.Arrays[GpuCounterKind.Dedicated]=new(GpuPdhStatus.NoData,[]);
    var r=R(0);Assert.True(r.State.Available);Assert.Empty(r.Dedicated);Assert.True(r.DedicatedReady);}
[Fact] public void More_than_half_skipped_is_unreadable_for_that_read(){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5),Bad(1)]);api.Arrays[GpuCounterKind.Shared]=new(0,[M(8,1),Bad(2),Bad(3)]);
    var r=R(0);Assert.Equal(new ProcessColumnState(false,"GPU counters unreadable"),r.State);Assert.Equal(3,r.Skipped);Assert.Equal(5,r.Instances);
    api.Arrays[GpuCounterKind.Shared]=new(0,[M(8,1)]);Assert.True(R(1).State.Available);}
[Fact] public void More_than_65536_items_is_too_many_for_that_read(){ api.Arrays[GpuCounterKind.Engine]=new(0,[..Enumerable.Range(0,65537).Select(i=>E(8,i,1))]);
    Assert.Equal(new ProcessColumnState(false,"Too many GPU counter instances"),R(0).State);Assert.True(reader.IsOpen);
    api.Arrays[GpuCounterKind.Engine]=new(0,[..Enumerable.Range(0,65536).Select(i=>E(8,i,1))]);Assert.True(R(1).State.Available);}
[Theory][InlineData(GpuPdhStatus.CStatusNoObject,"GPU counters unavailable")][InlineData(GpuPdhStatus.CStatusNoCounter,"GPU counters unavailable")][InlineData(0xC0000BBCu,"GPU counters unavailable (error 0xC0000BBC)")]
public void An_open_failure_is_retried_at_most_once_a_minute(uint status,string text){ api.OpenStatus=status;Assert.Equal(new ProcessColumnState(false,text),R(0).State);Assert.Equal(api.Opens,api.Closes);
    Assert.Equal(text,R(59.9).State.Status);Assert.Equal(1,api.Opens);api.OpenStatus=0;api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);Assert.True(R(60).State.Available);Assert.Equal(2,api.Opens);}
[Theory][InlineData("Open")][InlineData("Collect")][InlineData("Read")]
public void An_api_exception_is_a_failure_retried_at_most_once_a_minute(string call){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);api.Throws=call;
    Assert.Equal(new ProcessColumnState(false,"GPU counters unavailable"),R(0).State);Assert.False(reader.IsOpen);Assert.Equal(api.Opens,api.Closes);
    R(30);Assert.Equal(1,api.Opens);api.Throws="";R(59.9);Assert.Equal(1,api.Opens);var r=R(60);Assert.Equal(2,api.Opens);Assert.True(r.State.Available);Assert.Null(r.PreviousUtcTime);}
[Fact] public void A_throwing_dxgi_read_is_a_dxgi_failure(){ api.Throws="Adapters";api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);var r=R(0);Assert.True(r.State.Available);Assert.Empty(r.Adapters);Assert.True(reader.IsOpen);}
[Fact] public void A_more_data_status_left_after_sizing_is_a_failure(){ api.Arrays[GpuCounterKind.Engine]=new(GpuPdhStatus.MoreData,[]);
    Assert.Equal("GPU counters unavailable (error 0x800007D2)",R(0).State.Status);Assert.False(reader.IsOpen);}
[Fact] public void A_collect_failure_closes_and_reopens_at_most_once_a_minute(){ R(0);api.CollectStatus=0xC0000BBC;Assert.Equal("GPU counters unavailable (error 0xC0000BBC)",R(1).State.Status);Assert.Equal(1,api.Closes);
    api.CollectStatus=0;R(60.9);Assert.Equal(1,api.Opens);var r=R(61);Assert.Equal(2,api.Opens);Assert.Null(r.PreviousUtcTime);}
[Fact] public void Any_other_array_status_is_a_failure(){ api.Arrays[GpuCounterKind.Shared]=new(0xC0000BBD,[]);Assert.Equal("GPU counters unavailable (error 0xC0000BBD)",R(0).State.Status);Assert.False(reader.IsOpen);}
[Fact] public void Engines_that_appear_later_are_read_without_reopening(){   // Review Focus 3
    Assert.Equal("GPU counters unavailable",R(0).State.Status);Assert.Equal("GPU counters unavailable",R(1).State.Status);
    api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,12)]);var r=R(2);Assert.True(r.State.Available);Assert.Equal(12,Assert.Single(r.Engines).Percent);Assert.Equal(1,api.Opens);Assert.Equal(0,api.Closes);}
[Fact] public void Hardware_adapters_are_numbered_in_enumeration_order_and_the_rest_are_not(){
    api.Adapters=[new(0xA,"NVIDIA GeForce RTX 4070",0),new(0xB,"Microsoft Basic Render Driver",GpuAdapterInfo.SoftwareFlag),new(0xC,"Intel(R) UHD Graphics 770",0)];
    api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,10,luid:0xA),E(8,0,10,luid:0xB),E(8,0,10,luid:0xC),E(8,0,10,luid:0xD)]);var r=R(0);
    Assert.Equal(new GpuAdapterName(0,"NVIDIA GeForce RTX 4070"),r.Adapters[0xA]);Assert.Equal(new GpuAdapterName(1,"Intel(R) UHD Graphics 770"),r.Adapters[0xC]);
    Assert.Equal(new GpuAdapterName(null,"Microsoft Basic Render Driver"),r.Adapters[0xB]);Assert.False(r.Adapters.ContainsKey(0xD));Assert.Equal(4,r.Engines.Count);
    Assert.Equal("GPU 1 · 3D",r.Adapters[0xC].Label("3D"));Assert.Equal("GPU ? · Engine",GpuAdapterName.Unknown.Label(""));}
[Fact] public void A_dxgi_failure_or_an_unknown_luid_rereads_adapters_at_most_once_a_minute(){ api.Adapters=null;api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,10)]);
    var r=R(0);Assert.Empty(r.Adapters);Assert.Single(r.Engines);R(30);Assert.Equal(1,api.AdapterReads);
    api.Adapters=[new(0xA,"NVIDIA GeForce RTX 4070",0)];Assert.Equal(0,R(60).Adapters[0xA].Number);Assert.Equal(2,api.AdapterReads);
    api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,10,luid:0xE)]);R(61);Assert.Equal(2,api.AdapterReads);R(120);Assert.Equal(3,api.AdapterReads);}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~GpuCounterTests"`
Expected: the build fails with `error CS0246` for `GpuCounterInstance`, `GpuCounterReader` and `IGpuCounterApi`.

- [ ] **Step 3: Implement `GpuCounters.cs`**

The parser walks the name with `ReadOnlySpan<char>` (`StartsWith`/`IndexOf` on the fixed tokens; `int.TryParse(span,NumberStyles.None,CultureInfo.InvariantCulture,out …)` for the decimals; a 1–8 length check, then `uint.TryParse(span,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out …)` for each LUID half). No regex and no exceptions.

- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.

- [ ] **Step 5: Commit, push and open the draft PR**

```bash
git add tests/DeadWare.Tests/GpuCounterTests.cs
git commit -m "test(core): GPU counter names, read classification and adapter naming"
git add src/DeadWare.Core/GpuCounters.cs
git commit -m "feat(core): classify GPU performance counter reads"
git push -u origin HEAD
```
Open the draft PR through the GitHub tools: base `main`, head `claude/per-app-gpu-0.15.0`, title "Per-app GPU (0.15.0)". The body names the spec and this plan and records Step 0's warning count. It says the CI floor follows Task 2, and ends with the attribution in Global Constraints. Run (CI). Expected: green.

---

### Task 2: `WindowsGpuCounters` and the CI checks and floor

**Files:**
- Create: `src/DeadWare.App/WindowsGpuCounters.cs`
- Create: `tests/DeadWare.App.Tests/RealWindows/GpuCounterChecks.cs`
- Modify: `tests/DeadWare.App.Tests/RealWindows/ProcessMeasurementTests.cs`: its `Percentile(double[] sorted,double fraction)` helper becomes `internal static`, so both measurement classes share one nearest-rank percentile. That is its only change.

**Interfaces:**
- Consumes: Task 1 `IGpuCounterApi`, `GpuCounterReader`, `GpuCounterInstance`, `GpuPdhStatus`, `GpuCounterStatus`; `WindowsProcesses.Scan`; `MeasurementRecorder`; `GitHubWindowsFactAttribute`, `ElevatedGitHubFactAttribute`.
- Produces:
  - `internal interface IGpuCounterSource { GpuCounterRead Read(double monotonicSeconds,double pollingIntervalSeconds); bool LastReadOpened{get;} bool IsOpen{get;} void Close(); }`. `Read` never throws, and one caller at a time uses it.
  - `internal sealed class PdhGpuApi : IGpuCounterApi { internal const string EnginePath=@"\GPU Engine(*)\Utilization Percentage",DedicatedPath=@"\GPU Process Memory(*)\Dedicated Usage",SharedPath=@"\GPU Process Memory(*)\Shared Usage"; internal const uint FormatDouble=0x00000200,FormatLarge=0x00000400; }`
  - `internal sealed class WindowsGpuCounters : IGpuCounterSource`. It wraps `new GpuCounterReader(new PdhGpuApi())` and passes `DateTimeOffset.UtcNow`. It has no catch of its own: the reader turns API exceptions into a failure with the 60 s retry (Task 1).

Native details (no unsafe code; `DeadWare.App.csproj` does not allow it):
- `[DllImport("pdh.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]` for `PdhOpenQueryW(string? source,IntPtr user,out IntPtr query)`, `PdhAddEnglishCounterW(IntPtr query,string path,IntPtr user,out IntPtr counter)`, `PdhCollectQueryData(IntPtr query)`, `PdhGetFormattedCounterArrayW(IntPtr counter,uint format,ref uint bytes,out uint count,IntPtr buffer)` and `PdhCloseQuery(IntPtr query)`, all returning `uint`. `Open` calls `PdhCloseQuery` itself when an add fails, so it never leaves a handle.
- Formats: `FormatDouble` for the engine counter and `FormatLarge` for memory, without `PDH_FMT_NOCAP100`, so PDH caps utilization at 100.
- `Read(kind)` keeps one unmanaged buffer per kind from `Marshal.AllocHGlobal`, grown (`Marshal.FreeHGlobal` then `AllocHGlobal`) to the size PDH asks for, and freed by `Close`. It calls with the buffer's current size (0 at first). Status 0 with `count` 0 is zero items. `MoreData` returns the size needed: grow and call again, at most 3 calls in all. If the third still returns `MoreData`, return `new GpuRawArray(GpuPdhStatus.MoreData,[])`, a failure under Task 1's "any other status".
- Items are x64 `PDH_FMT_COUNTERVALUE_ITEM_W`, 24 bytes each: the name pointer at +0 (`Marshal.ReadIntPtr`), `CStatus` at +8 (`Marshal.ReadInt32`) and the value at +16 (`BitConverter.Int64BitsToDouble(Marshal.ReadInt64(…))` for `FormatDouble`, `Marshal.ReadInt64` for `FormatLarge`). Names point inside the buffer, so copy them with `Marshal.PtrToStringUni` before the next call.
- DXGI: `[DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid riid,[MarshalAs(UnmanagedType.Interface)] out object factory)` with `IID_IDXGIFactory1 = 770aae78-f26f-4dba-a829-253c83d1b387`. Declare `[ComImport]`, `[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]` `IDXGIFactory1` and `IDXGIAdapter1` (`29038f61-3839-4626-91fd-086879011a05`) with every vtable slot in order up to the methods used (the `IDXGIObject` methods first), and `[PreserveSig] int` on every method, so `EnumAdapters1`'s end-of-list `DXGI_ERROR_NOT_FOUND` (0x887A0002) is a return value, not an exception. Call `EnumAdapters1(i)` until it returns `DXGI_ERROR_NOT_FOUND`, then `GetDesc1`. Make a new factory per `Adapters()` call, release every COM object with `Marshal.ReleaseComObject`, and return null on any other failure.
- `DXGI_ADAPTER_DESC1` as `[StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]`: `[MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] string Description; uint VendorId, DeviceId, SubSysId, Revision; nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory; uint LuidLow; int LuidHigh; uint Flags;`. On x64 it is 312 bytes with `Flags` at offset 304. The LUID is `((long)LuidHigh<<32)|LuidLow`, which matches the parser's `(hi<<32)|lo`.

- [ ] **Step 1: Write the checks and the measurement**

`[Collection("RealWindows")] public sealed class GpuCounterChecks` (spec, Testing → Real Windows) writes `gpu-counters.json` and `gpu-timing.json` through `MeasurementRecorder` (the `DeadWare-measurements` artifact and the job summary). Neither records an adapter description or a process name.

`[ElevatedGitHubFact] public async Task Gpu_counters_open_and_parse()` gathers everything, calls `MeasurementRecorder.Write("gpu-counters",…)`, and only then asserts, so a failure still leaves its values. `api.Close()` runs in a `finally`.
1. `scans=new WindowsProcesses()`, `first=scans.Scan(0,1)!`, `api=new PdhGpuApi()`, `open=api.Open()`. If `open` is `CStatusNoObject` or `CStatusNoCounter`: write `{counters="absent"}` and return.
2. `collect1=api.Collect()`, then the three arrays (`early`); `await Task.Delay(1000)`; `collect2=api.Collect()`, then the three arrays (`late`); `second=scans.Scan(1,1)!`; `adapters=api.Adapters()`.
3. Parse every `late` item with `GpuCounterInstance.TryParse(name,kind,out …)`, per kind. `pids` is the union of both scans' PIDs.
4. Record: `counters="present"`; `open`, `collect1` and `collect2` as `0x{status:X8}`; for `early` and `late`, each kind's array status and its item count per CStatus; `instances` and `skipped` per kind; `unmatched` (parsed instances whose PID is in neither scan) with up to 10 of those PIDs; up to 10 skipped instance names (they carry only PIDs, LUIDs, engine numbers and types); `readyEngines` and `readyMemory`; `adapters` (null on a DXGI failure, else the count) with each adapter's `Flags`; and for each LUID seen, whether DXGI lists it, its flags and its engine types. A set with no instances (below) records `instances=0`.
5. Assert, in order:
   - `open` is 0 (message `Open failed with 0x{open:X8}`).
   - No instances: if `collect1` or `collect2` is `NoData` or `CStatusNoInstance`, or every `late` array has status `NoData` or `CStatusNoInstance` or no items, return (it passes).
   - Both collects are 0. Every `early` and `late` array status is one of 0, `NoData`, `CStatusNoInstance`, `InvalidData`, `CalcNegativeDenominator` or `CalcNegativeValue`; the message names the kind, the read and the status.
   - `unmatched` is 0 (message: the kind and the PID). Parsing never threw (no exception reached the test).
   - `adapters` is not null and not empty, and every entry has a non-empty `Description`.
   - Every ready `late` engine value is finite and within 0–100. Every ready memory value is finite, at least 0 and a whole number.

`[GitHubWindowsFact] public async Task Gpu_counter_timing_over_240_reads()` makes `new WindowsGpuCounters()` and reads 240 times, 100 ms apart, with `Read(clock.Elapsed.TotalSeconds,1)` on a `Stopwatch` clock, timing each read with `Stopwatch.GetTimestamp`. A read with `LastReadOpened` sets `firstMs` (the first such read only); the others go to `times`. If the first read's state is `GpuCounterStatus.Unavailable` with no instances and `!source.IsOpen`, it writes `{elevated,counters="absent"}` and returns. Otherwise it writes `elevated`; `reads=240`; `p50Ms` and `p95Ms` from `double[] sorted=[..times.Order()]` through `ProcessMeasurementTests.Percentile(sorted,0.50)` and `(sorted,0.95)`, both null when `sorted` is empty; `firstMs`; the last read's engine and memory item counts, `Skipped`, `Adapters.Count` and state text (an error code is recorded here); `instances=0` when the last read had none; and, recorded only, how many reads had `EnginesReady` false and how many had a state that was not available. It always passes. `source.Close()` runs in a `finally`.

- [ ] **Step 2: Build to verify failure.** Expected: `error CS0246` for `PdhGpuApi` and `WindowsGpuCounters`.
- [ ] **Step 3: Implement `WindowsGpuCounters.cs`**
- [ ] **Step 4: Build, commit, push and verify in CI**

```bash
git add tests/DeadWare.App.Tests/RealWindows/GpuCounterChecks.cs tests/DeadWare.App.Tests/RealWindows/ProcessMeasurementTests.cs
git commit -m "test(gpu): real GPU counter parse check and read-cost measurement"
git add src/DeadWare.App/WindowsGpuCounters.cs
git commit -m "feat(gpu): read GPU counters through PDH and name adapters through DXGI"
```
Run (CI). Expected: green. `Gpu_counters_open_and_parse` passes (not skipped: the runner is elevated), and the run's `DeadWare-measurements` artifact holds `gpu-counters.json` and `gpu-timing.json`.

- [ ] **Step 5: Record the floor (controller, GitHub tools)**

Fetch `gpu-timing.json` and `gpu-counters.json` from the run's artifact. If the tools cannot fetch it, ask the user for the run summary's two tables. Add to the PR description: the run link; `counters`; p50/p95 and the first read in ms; engine and memory instance counts; skipped; the adapter count and software flags; the counts of not-ready and unavailable reads. Then add the sentence "A floor only: the runner has no real GPU workload; placement is decided at run time by the guard (Task 3)."

Gate: stop before Task 3 and report to the user, with the recorded values, when any of these holds. Do not change the parser's rules, the reader's status rules or the check to pass.
- `Gpu_counters_open_and_parse` fails on Windows' real names, PIDs or statuses rather than a code bug: for example an unmatched PID (the recorded PIDs show which, such as PID 0, which the scan parser drops, or a process that had exited), or an `early` array status outside the spec's set.
- The check passed, but instances exist and `skipped` is above 0: names in another shape, shown by the recorded skipped names.
- DXGI lists adapters and engine instances exist, but no instance LUID is listed. Check the LUID composition (Native details) first; a fault there is a code bug to fix in this task. Otherwise report it.

---

### Task 3: The runtime placement guard and `GpuCounterLoop`

**Files:**
- Modify: `src/DeadWare.Core/GpuCounters.cs` (`GpuPlacementGuard`)
- Create: `src/DeadWare.App/GpuCounterLoop.cs`
- Test: `tests/DeadWare.Tests/GpuCounterTests.cs` (guard), `tests/DeadWare.App.Tests/GpuCounterLoopTests.cs`

**Interfaces:**
- Consumes: Task 2 `IGpuCounterSource`; Task 1 `GpuCounterRead`.
- Produces:
  - `public sealed class GpuPlacementGuard { public const int Window=20; public const double ThresholdMilliseconds=15; public void Record(double milliseconds,bool firstAfterOpen); public bool OwnLoop{get;} public void Reset(); }`. It keeps the last 20 times that are not first after open. `OwnLoop` latches true once at least two kept times exceed 15 ms, and only `Reset` clears it.
  - `internal sealed record GpuCounterTake(GpuCounterRead Read,bool Fresh,double? ReadMilliseconds);`
  - `internal sealed class GpuCounterLoop(IGpuCounterSource source,Func<double> monotonic,Func<double> pollingInterval,Func<bool> enabled) : IAsyncDisposable { public static double Period(double pollingInterval)=>Math.Max(1,pollingInterval); public void Start(); public GpuCounterTake? Take(); public Task Stopped{get;} public ValueTask DisposeAsync(); }`

The loop follows `StorageSensorLoop`'s style. The period is recomputed each pass. It reads with `source.Read(monotonic(),pollingInterval())` and times each read on the same clock.
- While `enabled()` is false, it does not read. On the first pass that finds it false, it calls `source.Close()` and drops its latest read.
- `Take()` is null before the first completed read.
  - The latest completed read that has not been taken comes back `Fresh` with its read time.
  - Otherwise the same read comes back with `Read with{Credit=false}`, `Fresh` false and null milliseconds.
  - A read that a newer one replaced before any `Take` is never returned.
- `Stopped` completes only after the loop has exited, its last read has returned and `source.Close()` has run. The collector gates every other use of the source on it (Task 5).
- `DisposeAsync` cancels the loop and waits for `Stopped` for at most 5 s, the hardware stop budget, then returns. A read still in flight keeps running; the loop calls `source.Close()` once it ends, never under it.

- [ ] **Step 1: Write the failing tests**

```csharp
// GpuCounterTests (Core)
[Fact] public void One_slow_read_in_twenty_keeps_every_poll_and_a_second_moves_the_read(){ var g=new GpuPlacementGuard();
    for(int i=0;i<19;i++)g.Record(2,false);g.Record(40,false);Assert.False(g.OwnLoop);g.Record(15,false);Assert.False(g.OwnLoop);g.Record(15.01,false);Assert.True(g.OwnLoop);}
[Fact] public void The_first_read_after_open_is_not_counted(){ var g=new GpuPlacementGuard();g.Record(500,true);g.Record(500,true);g.Record(500,false);Assert.False(g.OwnLoop);}
[Fact] public void A_slow_read_leaves_the_window_after_twenty_newer(){ var g=new GpuPlacementGuard();g.Record(40,false);for(int i=0;i<20;i++)g.Record(1,false);g.Record(40,false);Assert.False(g.OwnLoop);}
[Fact] public void The_loop_placement_holds_until_reset(){ var g=new GpuPlacementGuard();g.Record(20,false);g.Record(20,false);for(int i=0;i<100;i++)g.Record(1,false);Assert.True(g.OwnLoop);
    g.Reset();Assert.False(g.OwnLoop);g.Record(20,false);Assert.False(g.OwnLoop);}

// GpuCounterLoopTests (App). FakeGpuSource : IGpuCounterSource returns reads with MonotonicSeconds = the time passed and IntervalSeconds = the gap since its previous read
// (null for the first after open), records each call's time (CallGaps(), LastReadAt), counts Reads and Closes, tracks IsOpen and the most Reads in flight at once (MaxConcurrent),
// and has WaitForReads(n) (15 s limit). With block:true each Read waits for Release(); Entered completes on the first Read.
// Loop(interval=()=>0.5, enabled=()=>true, source=new FakeGpuSource()) starts a GpuCounterLoop on a Stopwatch clock and returns (loop, source).
[Fact] public async Task Period_is_at_least_one_second_and_follows_slower_polling(){ double interval=0.5;var (loop,fake)=Loop(()=>interval);await fake.WaitForReads(3);
    Assert.All(fake.CallGaps(),g=>Assert.True(g>=0.95,$"gap {g:0.00} s"));interval=2;int n=fake.Reads;await fake.WaitForReads(n+2);Assert.All(fake.CallGaps().Skip(n),g=>Assert.True(g>=1.95,$"gap {g:0.00} s"));}
[Fact] public async Task A_read_is_taken_fresh_once_then_again_without_credit(){ var (loop,fake)=Loop();await fake.WaitForReads(1);var first=loop.Take()!;Assert.True(first.Fresh);Assert.True(first.Read.Credit);Assert.NotNull(first.ReadMilliseconds);
    var again=loop.Take()!;Assert.False(again.Fresh);Assert.False(again.Read.Credit);Assert.Null(again.ReadMilliseconds);Assert.Equal(first.Read.MonotonicSeconds,again.Read.MonotonicSeconds);}
[Fact] public async Task A_read_passed_over_is_never_returned(){ var (loop,fake)=Loop();await fake.WaitForReads(3);var taken=loop.Take()!;Assert.Equal(fake.LastReadAt,taken.Read.MonotonicSeconds);}
[Fact] public async Task A_disabled_loop_does_not_read_and_closes_the_query(){ bool on=true;var (loop,fake)=Loop(enabled:()=>on);await fake.WaitForReads(2);on=false;await Task.Delay(1500);
    int reads=fake.Reads;await Task.Delay(1500);Assert.Equal(reads,fake.Reads);Assert.False(fake.IsOpen);Assert.Null(loop.Take());}
[Fact] public async Task Dispose_completes_within_the_hardware_budget_and_closes_after_the_read(){ var fake=new FakeGpuSource(block:true);var (loop,_)=Loop(source:fake);await fake.Entered;
    var sw=Stopwatch.StartNew();await loop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));Assert.True(sw.Elapsed<TimeSpan.FromSeconds(5.5));Assert.Equal(0,fake.Closes);
    Assert.False(loop.Stopped.IsCompleted);fake.Release();await loop.Stopped.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,fake.Closes);Assert.Equal(1,fake.MaxConcurrent);}
[Fact] public async Task Interval_changes_keep_each_read_fresh_once(){   // Review Focus 1
    double interval=0.5,before=0.5;var (loop,fake)=Loop(()=>interval);var fresh=new List<double>();   // before: the interval in effect until the latest change
    async Task Poll(int count){for(int i=0;i<count;i++){if(loop.Take() is {Fresh:true} t){fresh.Add(t.Read.MonotonicSeconds);
        Assert.True(t.Read.IntervalSeconds is null||t.Read.IntervalSeconds<=PollingPolicy.FreshnessSeconds(Math.Max(before,interval)),$"interval {t.Read.IntervalSeconds:0.00} s");}
        await Task.Delay(TimeSpan.FromSeconds(interval));}}
    await Poll(6);before=interval;interval=2;await Poll(3);before=interval;interval=0.5;await Poll(6);Assert.Equal(fresh.Count,fresh.Distinct().Count());Assert.True(fresh.Count>=5);}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS0246` for `GpuPlacementGuard` and `GpuCounterLoop`.
- [ ] **Step 3: Implement the guard and the loop**
- [ ] **Step 4: Run `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~GpuCounterTests"`, then the Core baseline, then commit and push.** Expected: PASS, then no output.

```bash
git add tests/DeadWare.Tests/GpuCounterTests.cs tests/DeadWare.App.Tests/GpuCounterLoopTests.cs
git commit -m "test(gpu): runtime placement guard and the GPU counter loop"
git add src/DeadWare.Core/GpuCounters.cs src/DeadWare.App/GpuCounterLoop.cs
git commit -m "feat(gpu): move slow GPU counter reads to their own loop at run time"
```
Run (CI). Expected: green; `GpuCounterLoopTests` pass.

---

## 0.15.0 — Step 2: per-process GPU values

### Task 4: Model additions, `GpuProcessJoin` and the tracker

**Files:**
- Modify: `src/DeadWare.Core/ProcessModels.cs` (`ProcessSample`, `ProcessDeltas`, `TrackedScan`, `ProcessActivity`)
- Create: `src/DeadWare.Core/GpuProcessJoin.cs`
- Modify: `src/DeadWare.Core/ProcessActivityTracker.cs` (`Update`)
- Test: `tests/DeadWare.Tests/GpuProcessJoinTests.cs`, `tests/DeadWare.Tests/ProcessActivityTrackerTests.cs` (two facts)

**Interfaces:**
- Consumes: Task 1 `GpuCounterRead`, `GpuEngineItem`, `GpuMemoryItem`, `GpuAdapterName`.
- Produces:
  - `ProcessSample` init members: `double? GpuPercent`, `string? GpuEngine`, `string? GpuAdapter`, `IReadOnlyList<GpuEngineUse>? GpuEngines`, `long? GpuDedicatedBytes`, `long? GpuSharedBytes`, `ProcessGap GpuGap`.
  - `ProcessDeltas` init members `double? GpuSeconds` and `double? GpuMemoryByteSeconds` (default null). `Zero` sets both to 0. `Add` sums them with the `CpuTime` rule. The positional constructor is unchanged, so existing `new ProcessDeltas(…)` calls keep compiling, and the tracker's `new ProcessDeltas(null,null,null,0,0)` omitted sum starts both at null.
  - `TrackedScan` and `ProcessActivity` init members `ProcessColumnState Gpu` (default `ProcessColumnState.Ready`, like `Disk` and `Network`) and `double? GpuIntervalSeconds`.
  - `public sealed record GpuEngineUse(long Luid,int Phys,int Engine,string Type,string Label,string? Adapter,double Percent);`
  - `public sealed record GpuJoinResult(IReadOnlyList<ProcessSample> Processes,ProcessColumnState State,double? IntervalSeconds);`
  - `public static class GpuProcessJoin { public const int MaximumEngines=32; public static GpuJoinResult Join(IReadOnlyList<ProcessSample> samples,GpuCounterRead? read); public static double? IntervalSeconds(GpuCounterRead? read); public static int CompareEngineOrder(GpuEngineUse a,GpuEngineUse b); }`
  - `public TrackedScan ProcessActivityTracker.Update(ProcessScan scan,GpuCounterRead? gpu=null)`. It joins every sample before the 4,096 cut and sets `TrackedScan.Gpu` and `GpuIntervalSeconds` from the result.

Join rules: the spec's Joining to processes, plus these plan decisions:
- `IntervalSeconds(read)` is `read.IntervalSeconds` when `read.State.Available && read.Credit`, else null.
- A null read (in loop placement, before any read has completed) leaves every GPU value null with `GpuGap=FirstSample` and state `Ready`, so every "—" has a reason. An unavailable read leaves them null with `GpuGap=None` and its own state.
- `EnginesReady` false is treated like no previous collection. `DedicatedReady` or `SharedReady` false makes that memory value null with `FirstSample` for every process.
- An entry's `Label` is its adapter's `GpuAdapterName.Label(type)` (`Unknown` for an unlisted LUID), and its `Adapter` is that adapter's description.
- `CompareEngineOrder` reads the adapter number from the `"GPU <n> · "` prefix that `GpuAdapterName.Label` writes. Numbered adapters come first, then `"GPU ?"` by LUID, then Phys, then Engine.
- GPU deltas are set only on a non-null `Deltas`.

- [ ] **Step 1: Write the failing tests**

```csharp
// GpuProcessJoinTests. Prev=T(0); Read(engines,dedicated=[],shared=[],previous=Prev,interval=1,adapters=Two) builds a GpuCounterRead at T(1) with State Ready;
// Two = {0xA:(0,"NVIDIA GeForce RTX 4070"),0xB:(1,"Intel(R) UHD Graphics 770")}. En(pid,luid,eng,pct,type="3D",ready=true,phys=0) and Mem(pid,luid,bytes,ready=true) build items.
// S(pid,gen=Old) builds a sample with Deltas new(0,0,0,0,1); Old is 10 s before Prev. Join(read,samples)=GpuProcessJoin.Join(samples,read).Processes.
[Fact] public void The_busiest_ready_engine_wins_and_ties_go_to_engine_order(){
    var p=Join(Read([En(8,0xD,0,40),En(8,0xB,0,40),En(8,0xA,0,40,phys:1),En(8,0xA,1,40,"Copy"),En(8,0xC,0,40),En(8,0xA,0,40),En(8,0xA,2,10)]),S(8)).Single();
    Assert.Equal(40,p.GpuPercent);Assert.Equal("GPU 0 · 3D",p.GpuEngine);Assert.Equal("NVIDIA GeForce RTX 4070",p.GpuAdapter);
    // Numbered adapters by number, then "GPU ?" by LUID, then phys, then eng; the 10 % engine last.
    Assert.Equal([(0xAL,0,0),(0xAL,0,1),(0xAL,1,0),(0xBL,0,0),(0xCL,0,0),(0xDL,0,0),(0xAL,0,2)],p.GpuEngines!.Select(e=>(e.Luid,e.Phys,e.Engine)));
    Assert.Equal(["GPU 0 · 3D","GPU 0 · Copy","GPU 0 · 3D","GPU 1 · 3D","GPU ? · 3D","GPU ? · 3D","GPU 0 · 3D"],p.GpuEngines!.Select(e=>e.Label));Assert.Null(p.GpuEngines![4].Adapter);}
[Fact] public void A_not_ready_engine_is_ignored_when_another_is_ready(){ var p=Join(Read([En(8,0xA,0,90,ready:false),En(8,0xA,1,20,"Copy")]),S(8)).Single();Assert.Equal(20,p.GpuPercent);Assert.Equal(ProcessGap.None,p.GpuGap);}
[Fact] public void All_engines_not_ready_is_a_first_sample(){ var p=Join(Read([En(8,0xA,0,90,ready:false)]),S(8)).Single();Assert.Null(p.GpuPercent);Assert.Null(p.GpuEngine);Assert.Null(p.GpuEngines);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);}
[Fact] public void Memory_sums_over_adapters_and_a_not_ready_item_makes_it_unknown(){ var p=Join(Read([],[Mem(8,0xA,100),Mem(8,0xB,200)],[Mem(8,0xA,5,ready:false)]),S(8)).Single();
    Assert.Equal(300,p.GpuDedicatedBytes);Assert.Null(p.GpuSharedBytes);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);}
[Fact] public void A_process_without_instances_or_with_idle_engines_is_a_known_zero(){ var ps=Join(Read([En(9,0xA,0,0)]),S(8),S(9));
    Assert.All(ps,p=>{Assert.Equal(0,p.GpuPercent);Assert.Equal("",p.GpuEngine);Assert.Null(p.GpuAdapter);Assert.Empty(p.GpuEngines!);Assert.Equal(0,p.GpuDedicatedBytes);Assert.Equal(0,p.GpuSharedBytes);Assert.Equal(ProcessGap.None,p.GpuGap);});}
[Fact] public void Engine_values_are_clamped(){ var p=Join(Read([En(8,0xA,0,104.5),En(8,0xA,1,-3,"Copy")]),S(8)).Single();Assert.Equal(100,p.GpuPercent);Assert.Single(p.GpuEngines!);}
[Fact] public void At_most_32_engines_are_published_busiest_first(){ var p=Join(Read([..Enumerable.Range(0,40).Select(i=>En(8,0xA,i,i+1))]),S(8)).Single();
    Assert.Equal(32,p.GpuEngines!.Count);Assert.Equal(40,p.GpuEngines[0].Percent);Assert.Equal(9,p.GpuEngines[^1].Percent);}
[Fact] public void A_process_created_at_or_after_the_previous_collection_less_one_second_is_a_first_sample(){
    var ps=Join(Read([],[Mem(8,0xA,100)]),S(8,gen:Prev.AddSeconds(-0.5).UtcTicks),S(9,gen:Prev.AddSeconds(-1.5).UtcTicks),S(10,gen:Prev.AddSeconds(-1).UtcTicks));
    Assert.Null(ps[0].GpuPercent);Assert.Equal(ProcessGap.FirstSample,ps[0].GpuGap);Assert.Equal(100,ps[0].GpuDedicatedBytes);Assert.Equal(0,ps[1].GpuPercent);   // before the known-zero rule
    Assert.Null(ps[2].GpuPercent);Assert.Equal(ProcessGap.FirstSample,ps[2].GpuGap);}   // exactly previous − 1 s: "at or after"
[Fact] public void Without_a_previous_collection_gpu_percent_is_unknown_and_memory_is_published(){ var ps=Join(Read([En(8,0xA,0,50)],[Mem(8,0xA,100)],previous:null,interval:null),S(8),S(9));
    Assert.All(ps,p=>{Assert.Null(p.GpuPercent);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);});Assert.Equal(100,ps[0].GpuDedicatedBytes);Assert.Equal(0,ps[1].GpuDedicatedBytes);}
[Fact] public void A_not_ready_engine_counter_is_a_first_sample_for_every_process(){ var p=Join(Read([],[Mem(8,0xA,7)]) with{EnginesReady=false},S(8)).Single();Assert.Null(p.GpuPercent);Assert.Equal(7,p.GpuDedicatedBytes);}
[Fact] public void A_null_or_unavailable_read_leaves_every_value_unknown(){ var none=GpuProcessJoin.Join([S(8)],null);Assert.Equal(ProcessGap.FirstSample,none.Processes[0].GpuGap);Assert.True(none.State.Available);Assert.Null(none.IntervalSeconds);
    var off=GpuProcessJoin.Join([S(8)],GpuCounterRead.Unavailable("GPU counters unreadable",1,T(1)));var p=off.Processes[0];
    Assert.Equal("GPU counters unreadable",off.State.Status);Assert.Null(p.GpuPercent);Assert.Null(p.GpuDedicatedBytes);Assert.Equal(ProcessGap.None,p.GpuGap);}
[Fact] public void Gpu_deltas_are_percent_and_bytes_times_the_interval(){ var r=Read([En(8,0xA,0,40)],[Mem(8,0xA,300)],interval:2);var p=GpuProcessJoin.Join([S(8)],r).Processes[0];
    Assert.Equal(0.8,p.Deltas!.GpuSeconds!.Value,9);Assert.Equal(600,p.Deltas.GpuMemoryByteSeconds!.Value,9);Assert.Equal(2,GpuProcessJoin.IntervalSeconds(r));
    var display=GpuProcessJoin.Join([S(8)],r with{Credit=false});Assert.Null(display.IntervalSeconds);Assert.Null(display.Processes[0].Deltas!.GpuSeconds);Assert.Equal(40,display.Processes[0].GpuPercent);
    Assert.Null(GpuProcessJoin.Join([S(8) with{Deltas=null}],r).Processes[0].Deltas);}
[Fact] public void Gpu_deltas_add_with_the_cpu_rule(){ var a=new ProcessDeltas(1,1,1,1,1);Assert.Null(a.Add(a).GpuSeconds);Assert.Equal(0.5,a.Add(a with{GpuSeconds=0.5}).GpuSeconds);
    Assert.Equal(0,ProcessDeltas.Zero.GpuSeconds);Assert.Equal(0,ProcessDeltas.Zero.GpuMemoryByteSeconds);}
// ProcessActivityTrackerTests
[Fact] public void Omitted_processes_carry_their_gpu_sums(){ /* 4,100 entries; a GpuCounterRead with previous collection and interval 1 giving each PID 1 % and 1,000 dedicated bytes */
    Assert.Equal(4*0.01,r.Omitted.Deltas.GpuSeconds!.Value,9);Assert.Equal(4000,r.Omitted.Deltas.GpuMemoryByteSeconds!.Value,6);Assert.True(r.Gpu.Available);Assert.Equal(1,r.GpuIntervalSeconds);}
[Fact] public void Omitted_gpu_sums_start_unknown_when_no_process_knows_them(){ /* 4,100 entries, second scan, Update(scan,null) */ Assert.Null(r.Omitted.Deltas.GpuSeconds);Assert.Null(r.GpuIntervalSeconds);}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~GpuProcessJoinTests|FullyQualifiedName~ProcessActivityTrackerTests"`. Expected: `error CS0246` for `GpuProcessJoin` and `GpuEngineUse`.
- [ ] **Step 3: Implement the members, the join and the tracker call**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/GpuProcessJoinTests.cs tests/DeadWare.Tests/ProcessActivityTrackerTests.cs
git commit -m "test(core): join GPU counters to processes"
git add src/DeadWare.Core/ProcessModels.cs src/DeadWare.Core/GpuProcessJoin.cs src/DeadWare.Core/ProcessActivityTracker.cs
git commit -m "feat(core): per-process GPU, engine, memory and GPU-seconds"
```
Run (CI). Expected: green.

---

### Task 5: Collector wiring, placement and the `--diagnose` GPU summary

**Files:**
- Modify: `src/DeadWare.App/Collector.cs`:
  - the internal constructor and the public one (:87-88);
  - process block 1 (after `// Process block 1:`, :245-260);
  - `PublishProcesses` (:316-340);
  - `RunAsync`'s `finally` (0.14.1's form);
  - `DisposeCoreAsync` (:404-427).
- Modify: `src/DeadWare.Core/ProcessModels.cs` (`GpuCounterSummary`, `ProcessSummary.Gpu`, `ProcessActivity.GpuStale` and `GpuSummary`)
- Test: Create `tests/DeadWare.App.Tests/CollectorGpuTests.cs`; Modify `tests/DeadWare.App.Tests/DiagnosticOptionsTests.cs` (two facts)

**Interfaces:**
- Consumes: Tasks 2–4; `CollectorProcessTests`' `FakeProcesses` (PIDs 100 and 200, Generation 1); 0.14.1's `App.DiagnosticReport`.
- Produces:
  - `internal Collector(CollectorOptions? options,IStorageFastSource? storage,IStorageHealthSource? healthSource=null,TimeSpan? healthPeriod=null,IProcessSource? processes=null,Func<IDiskTrace>? diskTrace=null,IGpuCounterSource? gpuCounters=null)`. The public constructor passes `gpuCounters:new WindowsGpuCounters()`. With null, every poll that has monitoring on joins `GpuCounterRead.Unavailable(GpuCounterStatus.Unavailable,…)` and records no GPU timing.
  - `public sealed record GpuCounterSummary(int Instances,int Skipped,int Adapters,double? ReadMilliseconds,ProcessColumnState State,string Placement){ public const string EveryPoll="every poll",OwnLoop="own loop"; }`
  - `ProcessActivity` init members `bool GpuStale` and `GpuCounterSummary? GpuSummary`. `ProcessSummary` gains `public GpuCounterSummary? Gpu{get;init;}`, which `From` copies from `activity.GpuSummary`.

Rules (spec, Placement and cost):
- Block 1, with monitoring on, runs just before `processes.Scan` with `stage="Reading GPU counters"`.
  - Every poll: `gpu.Read(clock.Elapsed.TotalSeconds,intervalSeconds)`, timed, then `guard.Record(ms,gpu.LastReadOpened)`. Once `guard.OwnLoop` is true, start `new GpuCounterLoop(gpu,()=>clock.Elapsed.TotalSeconds,()=>PollingIntervalSeconds,()=>processMonitoring)`. This poll keeps its own read; later polls use `loop.Take()`.
  - An exception from the source gives `GpuCounterRead.Unavailable(GpuCounterStatus.Unavailable,…)`. The scan, the other joins and the hardware readings continue.
  - `timings["windows/gpucounters"]` is that read's milliseconds, recorded only when a read ran (every poll) or the take is `Fresh` (loop).
  - Then `tracker.Update(scan,read)`.
- `GpuStale`: in loop placement, true when `now − read.MonotonicSeconds > 2 × GpuCounterLoop.Period(intervalSeconds)`. Always false in every-poll placement and for a null read.
- Monitoring off: `tracker.Reset()`, then:
  - Every-poll placement: `gpu.Close()`, so the query closes within that poll.
  - Loop placement: start `loop.DisposeAsync()` without awaiting it on the poll, keep `gpuLoopStop=loop.Stopped` and set the loop to null. The query closes as soon as the read in flight ends, at most one read later.
  - Then `guard.Reset()`.
  - While `gpuLoopStop` has not completed, a poll with monitoring on joins a null read and does not read, so one thread at a time uses the source.
- The worker's `finally` calls `gpu?.Close()` before `computer?.Close()` only when no loop exists and `gpuLoopStop` is null or completed; otherwise the loop's own `Close` covers it. `DisposeCoreAsync` disposes the loop and awaits any pending `gpuLoopStop` beside `healthLoop` under `HardwareStopBudget`; on a timeout the loop still closes the query when its read ends.
- `PublishProcesses` gains the joined read (or null), its milliseconds and the placement. It sets `Gpu`, `GpuIntervalSeconds` (from `tracked`) and `GpuStale`. With `options.DiagnosticTimings`, it also sets `GpuSummary`: Instances, Skipped, `Adapters.Count`, the milliseconds, `State` and `Placement`. For a null joined read it is `new(0,0,0,null,tracked.Gpu,placement)`, so a poll with monitoring on always has a summary.

- [ ] **Step 1: Write the failing tests**

```csharp
// CollectorGpuTests (HardwareEnabled:false, EtwEnabled:false, interval 0.5, FakeProcesses). FakeGpuCounters : IGpuCounterSource returns ready reads giving PID 100
// "GPU 0 · 3D" at 25 % and 64,000,000 dedicated bytes. The first Read after construction or after Close has no previous collection and sets LastReadOpened;
// later reads have one. It can delay each read (delay:), and after Stall() each Read waits for Release(). It counts Reads and Closes, tracks IsOpen and MaxConcurrent.
// ThrowingGpuCounters throws InvalidOperationException from Read. Start/NextWhere/NextReadingAfter as in CollectorProcessTests; WaitFor(condition,limit) polls every 20 ms
// and fails at the limit.
[Fact] public async Task Gpu_values_join_the_published_activity(){ var r=await NextWhere(Start(fake),r=>r.ProcessActivity.GpuIntervalSeconds is not null);var a=r.ProcessActivity.Processes.Single(p=>p.Pid==100);
    Assert.Equal(25,a.GpuPercent);Assert.Equal("GPU 0 · 3D",a.GpuEngine);Assert.Equal(64_000_000,a.GpuDedicatedBytes);Assert.Equal(0,r.ProcessActivity.Processes.Single(p=>p.Pid==200).GpuPercent);Assert.True(r.ProcessActivity.Gpu.Available);
    Assert.False(r.ProcessActivity.GpuStale);}   // every-poll placement is never stale
[Fact] public async Task A_throwing_gpu_source_keeps_cpu_memory_and_the_process_columns(){ var r=await NextWhere(Start(new ThrowingGpuCounters()),r=>r.ProcessActivity.Processes.Any(p=>p.CpuPercent is not null));
    Assert.Equal(ProcessActivityKind.Available,r.ProcessActivity.Kind);Assert.Equal(new ProcessColumnState(false,"GPU counters unavailable"),r.ProcessActivity.Gpu);
    Assert.All(r.ProcessActivity.Processes,p=>Assert.Null(p.GpuPercent));Assert.True(r.Sample.Values.ContainsKey("cpu/usage"));Assert.True(r.Sample.Values.ContainsKey("memory/usage"));}
[Fact] public async Task Turning_monitoring_off_closes_the_query_within_one_poll(){ var c=Start(fake);var on=await NextWhere(c,r=>fake.Reads>=2);c.SetProcessMonitoring(false);
    var off=await NextReadingAfter(c,on);Assert.Equal(ProcessActivityKind.Off,off.ProcessActivity.Kind);Assert.False(fake.IsOpen);int reads=fake.Reads;await NextReadingAfter(c,off);Assert.Equal(reads,fake.Reads);}
[Fact] public async Task A_slow_source_moves_to_its_own_loop_where_an_unrefreshed_poll_adds_nothing(){ var fake=new FakeGpuCounters(delay:TimeSpan.FromMilliseconds(30));var c=Start(fake,new(ProcessMonitoring:true,DiagnosticTimings:true));
    var looped=await NextWhere(c,r=>r.ProcessSummary?.Gpu?.Placement==GpuCounterSummary.OwnLoop);
    var rejoined=await NextWhere(c,r=>r.ProcessActivity.Gpu.Available&&r.ProcessActivity.GpuIntervalSeconds is null&&r.Sample.MonotonicSeconds>looped.Sample.MonotonicSeconds);
    Assert.All(rejoined.ProcessActivity.Processes,p=>Assert.Null(p.Deltas?.GpuSeconds));Assert.DoesNotContain("windows/gpucounters",rejoined.ProviderMilliseconds!.Keys);
    Assert.Equal(25,rejoined.ProcessActivity.Processes.Single(p=>p.Pid==100).GpuPercent);}                      // shown, not credited
[Fact] public async Task A_loop_read_older_than_two_periods_is_stale_and_still_carried(){ var fake=new FakeGpuCounters(delay:TimeSpan.FromMilliseconds(30));
    var c=Start(fake,new(ProcessMonitoring:true,DiagnosticTimings:true));
    var fresh=await NextWhere(c,r=>r.ProcessSummary?.Gpu?.Placement==GpuCounterSummary.OwnLoop&&r.ProviderMilliseconds!.ContainsKey("windows/gpucounters"));Assert.False(fresh.ProcessActivity.GpuStale);
    fake.Stall();var stale=await NextWhere(c,r=>r.ProcessActivity.GpuStale);   // the read in flight is older than 2 × Period(0.5) = 2 s
    Assert.True(stale.ProcessActivity.Gpu.Available);Assert.Equal(25,stale.ProcessActivity.Processes.Single(p=>p.Pid==100).GpuPercent);fake.Release();}   // released before disposal
[Fact] public async Task The_timing_key_appears_only_when_a_read_ran(){ var r=await Second(new(ProcessMonitoring:true,DiagnosticTimings:true),fake);Assert.Contains("windows/gpucounters",r.ProviderMilliseconds!.Keys);
    var off=await Second(new(DiagnosticTimings:true),new FakeGpuCounters());Assert.DoesNotContain("windows/gpucounters",off.ProviderMilliseconds!.Keys);Assert.Null(off.ProcessSummary!.Gpu);}
[Fact] public async Task Rapid_toggles_during_a_loop_read_never_read_concurrently(){   // Review Focus 2
    var fake=new FakeGpuCounters(delay:TimeSpan.FromMilliseconds(300));var c=Start(fake,new(ProcessMonitoring:true,DiagnosticTimings:true));
    await NextWhere(c,r=>r.ProcessSummary?.Gpu?.Placement==GpuCounterSummary.OwnLoop);
    c.SetProcessMonitoring(false);c.SetProcessMonitoring(true);c.SetProcessMonitoring(false);var off=await NextReadingAfter(c,c.Latest!);
    await WaitFor(()=>!fake.IsOpen&&fake.Closes>=1,TimeSpan.FromSeconds(2));   // loop placement: closed once the 300 ms read in flight ends
    c.SetProcessMonitoring(true);
    var back=await NextWhere(c,r=>r.ProcessActivity.Kind==ProcessActivityKind.Available&&r.Sample.MonotonicSeconds>off.Sample.MonotonicSeconds);
    Assert.Equal(GpuCounterSummary.EveryPoll,back.ProcessSummary!.Gpu!.Placement);Assert.All(back.ProcessActivity.Processes,p=>Assert.Null(p.GpuPercent));Assert.Equal(1,fake.MaxConcurrent);
    c.SetProcessMonitoring(false);await NextReadingAfter(c,await NextReadingAfter(c,back));Assert.False(fake.IsOpen);}
// DiagnosticOptionsTests
[Fact] public void Diagnostic_report_has_the_gpu_summary_and_no_names_or_adapters(){ var activity=ActivityWith("secret-app.exe") with{Processes=[/* secret-app.exe with GpuEngine "GPU 0 · 3D",
    GpuAdapter "Secret Adapter 9000", GpuEngines=[that engine] */],GpuSummary=new(12,1,1,0.8,ProcessColumnState.Ready,GpuCounterSummary.EveryPoll)};
    string json=App.DiagnosticReport("Completed","Idle between samples",15,App.DiagnosticOptions(["--diagnose","o.json"],false),[Fixture() with{ProcessActivity=activity,ProcessSummary=ProcessSummary.From(activity)}]);
    Assert.DoesNotContain("Secret Adapter",json);Assert.DoesNotContain("secret-app",json,StringComparison.OrdinalIgnoreCase);
    var gpu=JsonDocument.Parse(json).RootElement.GetProperty("Samples")[0].GetProperty("ProcessSummary").GetProperty("Gpu");
    Assert.Equal(12,gpu.GetProperty("Instances").GetInt32());Assert.Equal(1,gpu.GetProperty("Adapters").GetInt32());Assert.Equal("every poll",gpu.GetProperty("Placement").GetString());}
[Fact] public void The_summary_has_no_gpu_part_while_monitoring_is_off()=>Assert.Null(ProcessSummary.From(ProcessActivity.Off).Gpu);
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1739` for `gpuCounters`, and `error CS0246` for `GpuCounterSummary`.
- [ ] **Step 3: Implement the summary records and the collector wiring**
- [ ] **Step 4: Build, Core baseline, commit, push and verify in CI**

```bash
git add tests/DeadWare.App.Tests/CollectorGpuTests.cs tests/DeadWare.App.Tests/DiagnosticOptionsTests.cs
git commit -m "test(collector): GPU read before the scan, isolation, placement and the diagnose summary"
git add src/DeadWare.App/Collector.cs src/DeadWare.Core/ProcessModels.cs
git commit -m "feat(collector): read GPU counters with each process scan"
```
Run (CI). Expected: green. `CollectorGpuTests`, `CollectorProcessTests`, `EtwDiskTests`, `CoolingShutdownTests` and `DiagnosticOptionsTests` pass.

---

## 0.15.0 — Step 3: Processes page

### Task 6: Table model: app GPU, engine text, sorting and copy (Core)

**Files:**
- Modify: `src/DeadWare.Core/ProcessTable.cs`: `ProcessCopy` (`ScanSentence`, `Footer`), `ProcessAppRow`, `ProcessGrouping.Value` (both overloads), `ProcessSort`, `ProcessTableColumns`
- Test: `tests/DeadWare.Tests/ProcessTableTests.cs`, `tests/DeadWare.Tests/ProcessSettingsTests.cs` (the `ScanSentence` cases)

**Interfaces:**
- Consumes: Task 4 `GpuEngineUse`, `GpuProcessJoin.CompareEngineOrder`.
- Produces:
  - `public static (double? Percent,GpuEngineUse? Busiest) ProcessGrouping.GpuUse(IReadOnlyList<ProcessSample> members)`. It follows the spec's App rows rule. `Busiest` is the winning engine's entry with Percent replaced by the clamped sum. The result is null when every member's list is null, and (0, null) when some list is non-null but no member has an entry.
  - `ProcessAppRow` gains `double? Gpu` and `GpuEngineUse? GpuBusiest`, computed from `Members` through `GpuUse` like the other sums. It also gains `double? GpuMemory` and `double? GpuShared`: sums of known members, null only when none is known.
  - `ProcessGrouping.Value`: `"gpu"`, `"gpumemory"` and `"gpushared"` read those values. `"gpuengine"` is null, since it is a text column.
  - `public static string? ProcessGrouping.EngineText(ProcessSample p)` (null when `GpuPercent` is null, else `GpuEngine ?? ""`) and `EngineText(ProcessAppRow r)` (null when `Gpu` is null, else `GpuBusiest?.Label ?? ""`).
  - `public static string ProcessCopy.EngineTip(GpuEngineUse engine)` joins the adapter description, when there is one, and `"<type> engine"` (`"engine"` for an empty type) with `" · "`.
  - `ProcessSort.Compare` (both overloads) for `"gpuengine"` compares `EngineText` under `StringComparer.OrdinalIgnoreCase` and flips only that comparison when descending. In both directions, "" sorts after every named engine and null after "". Ties go by name, then PID, as today.
  - `public static bool ProcessTableColumns.AscendingFirst(string key)=>key is "name" or "gpuengine";`
  - `ProcessCopy.Footer(bool)` ends, in both builds, with `"GPU is the app's busiest GPU engine, as in Task Manager; GPU memory is dedicated video memory."`
  - `ProcessCopy.ScanSentence(bool elevated,bool diskColumns,bool scanLoop,bool history=false)` returns `"Turning it on adds one Windows process scan per poll"+(scanLoop?" (at most one a second)":"")+(elevated&&diskColumns?", one GPU counter read per poll and a disk trace":" and one GPU counter read per poll")+"."`, then the unchanged history sentence. The UI checks compare against this function, so they follow it.

- [ ] **Step 1: Write the failing tests**

```csharp
// U(luid,eng,pct,type="3D",phys=0,label=null,adapter=null) builds a GpuEngineUse, whose label defaults to "GPU 0 · <type>" for luid 0xA, "GPU 1 · <type>" for 0xB and "GPU ? · <type>" otherwise.
// GS(pid,engines) builds an "a.exe" sample with those GpuEngines and GpuPercent from the first entry (0 for []). G(...) and S(...) are the file's helpers.
[Fact] public void An_app_sums_each_engine_across_members(){ var r=G(GS(1,[U(0xA,1,50,"Copy"),U(0xA,0,30)]),GS(2,[U(0xA,0,40)]));
    Assert.Equal(70,r.Gpu);Assert.Equal("GPU 0 · 3D",r.GpuBusiest!.Label);Assert.Equal(70,r.GpuBusiest.Percent);}   // no member's own busiest
[Fact] public void Two_phys_values_under_one_luid_stay_apart()=>Assert.Equal(40,G(GS(1,[U(0xA,0,30,phys:0)]),GS(2,[U(0xA,0,40,phys:1)])).Gpu);
[Fact] public void Empty_lists_are_zero_and_all_unknown_is_unknown(){ var zero=G(GS(1,[]),S(2,"a.exe",1));Assert.Equal(0,zero.Gpu);Assert.Null(zero.GpuBusiest);Assert.Equal("",ProcessGrouping.EngineText(zero));
    var unknown=G(S(1,"a.exe",1),S(2,"a.exe",1));Assert.Null(unknown.Gpu);Assert.Null(ProcessGrouping.EngineText(unknown));}
[Fact] public void An_app_sum_is_clamped_and_ties_go_to_engine_order(){ Assert.Equal(100,G(GS(1,[U(0xA,0,60)]),GS(2,[U(0xA,0,60)])).Gpu);
    Assert.Equal("GPU 0 · 3D",G(GS(1,[U(0xB,0,50)]),GS(2,[U(0xA,0,50)])).GpuBusiest!.Label);Assert.Equal("GPU 1 · 3D",G(GS(1,[U(0xC,0,50)]),GS(2,[U(0xB,0,50)])).GpuBusiest!.Label);
    Assert.Equal(0xCL,G(GS(1,[U(0xD,0,50)]),GS(2,[U(0xC,0,50)])).GpuBusiest!.Luid);Assert.Equal(0,G(GS(1,[U(0xA,0,50,phys:1)]),GS(2,[U(0xA,0,50)])).GpuBusiest!.Phys);}   // "GPU ?" by LUID, then phys
[Fact] public void Gpu_memory_sums_and_is_unknown_only_when_every_member_is(){ var r=G(S(1,"a.exe",1) with{GpuDedicatedBytes=100},S(2,"a.exe",1));
    Assert.Equal(100,r.GpuMemory);Assert.Null(r.GpuShared);Assert.Equal(100,ProcessGrouping.Value(r,"gpumemory"));}
// 0.14.1's Each_column_key_reads_its_own_field: Full() gains GpuPercent=12.5,GpuEngines=[U(0xA,0,12.5)],GpuEngine="GPU 0 · 3D",GpuDedicatedBytes=8192,GpuSharedBytes=1024,
// and the theory gains [InlineData("gpu",12.5)][InlineData("gpumemory",8192)][InlineData("gpushared",1024)] (two members on one engine: 25, 16384, 2048).
[Theory][InlineData("NVIDIA GeForce RTX 4070","3D","NVIDIA GeForce RTX 4070 · 3D engine")][InlineData(null,"3D","3D engine")][InlineData(null,"","engine")][InlineData("Intel(R) UHD Graphics 770","","Intel(R) UHD Graphics 770 · engine")]
public void Engine_tooltip_names_the_adapter_and_type(string? adapter,string type,string expected)=>Assert.Equal(expected,ProcessCopy.EngineTip(U(0xA,0,10,type,adapter:adapter)));
[Fact] public void Gpu_engine_sorts_as_text_with_empty_then_unknown_last_both_ways(){
    var rows=new[]{G(GS(1,[U(0xB,0,9)])) with{Name="x1"},G(GS(2,[U(0xA,1,9,"Copy",label:"gpu 0 · Copy")])) with{Name="x2"},G(GS(3,[])) with{Name="x3"},G(S(4,"a.exe",1)) with{Name="x4"},G(GS(5,[U(0xA,1,9,"Copy")])) with{Name="x5"}};
    string[] By(bool d)=>[..rows.Order(Comparer<ProcessAppRow>.Create((a,b)=>ProcessSort.Compare(a,b,"gpuengine",d))).Select(r=>r.Name)];
    Assert.Equal(["x2","x5","x1","x3","x4"],By(false));Assert.Equal(["x1","x2","x5","x3","x4"],By(true));}
[Fact] public void Only_name_and_gpu_engine_sort_ascending_first(){ Assert.True(ProcessTableColumns.AscendingFirst("gpuengine"));Assert.True(ProcessTableColumns.AscendingFirst("name"));Assert.False(ProcessTableColumns.AscendingFirst("gpu"));}
[Fact] public void The_footer_ends_with_the_gpu_line(){ Assert.Equal("GPU is the app's busiest GPU engine, as in Task Manager; GPU memory is dedicated video memory.",ProcessCopy.Footer(true)[^1]);Assert.Equal(5,ProcessCopy.Footer(true).Count);}
// Footer_omits_disk_without_the_disk_build: Footer(false).Count 3 becomes 4, and Footer(true).Count 4 becomes 5.
// ProcessSettingsTests.Scan_sentence_follows_the_build, the four cases:
//   (false,false,false) "Turning it on adds one Windows process scan per poll and one GPU counter read per poll."
//   (true,true,false)   "Turning it on adds one Windows process scan per poll, one GPU counter read per poll and a disk trace."
//   (true,false,true)   "Turning it on adds one Windows process scan per poll (at most one a second) and one GPU counter read per poll."
//   (true,true,true)    "Turning it on adds one Windows process scan per poll (at most one a second), one GPU counter read per poll and a disk trace."
// Scan_sentence_adds_the_history_sentence: "Turning it on adds one Windows process scan per poll and one GPU counter read per poll. While usage history is recorded, it also keeps the busiest apps for 30 days."
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ProcessTableTests|FullyQualifiedName~ProcessSettingsTests"`. Expected: `error CS0117` for `GpuUse`, `EngineText`, `EngineTip` and `AscendingFirst`, and `error CS1061` for `ProcessAppRow.Gpu`, `GpuBusiest`, `GpuMemory` and `GpuShared`.
- [ ] **Step 3: Implement the members, the text sort and the copy**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ProcessTableTests.cs tests/DeadWare.Tests/ProcessSettingsTests.cs
git commit -m "test(core): app GPU by engine, engine text, tooltip, sort, footer and scan sentence"
git add src/DeadWare.Core/ProcessTable.cs
git commit -m "feat(core): per-app GPU and GPU memory in the Processes table model"
```
Run (CI). Expected: green. The page footer, off state and Settings help line follow `Footer` and `ScanSentence`.

---

### Task 7: GPU column keys and the Processes page columns

**Files:**
- Modify: `src/DeadWare.Core/ProcessColumnKeys.cs` (`All`, `Default`)
- Modify: `src/DeadWare.Core/ProcessTable.cs` (`ProcessTableColumns.PickerOrder`, `Header`)
- Modify: `src/DeadWare.App/ProcessesPage.cs`:
  - `OnSorting` (:257-264): `sortDescending=!ProcessTableColumns.AscendingFirst(key)`, and its comment;
  - `Cells`, `State`, `Gap` and `Format` (:426-460);
  - `SpokenLabels` and `Spoken` (:462-476).
- Test: `tests/DeadWare.Tests/ProcessSettingsTests.cs`, `tests/DeadWare.Tests/ProcessTableTests.cs`, `tests/DeadWare.App.Tests/ProcessesPageUiChecks.cs`

**Interfaces:**
- Consumes: Tasks 4 and 6.
- Produces:
  - `ProcessColumnKeys.All` gains `"gpu","gpuengine","gpumemory","gpushared"` after `"upload"`. `Default` becomes `["cpu","memory","io","disk","network","gpu","gpumemory"]`. `AdministratorOnly` and `DiskKeys` are unchanged.
  - `Header`: `"gpu"=>"GPU","gpuengine"=>"GPU ENGINE","gpumemory"=>"GPU MEMORY","gpushared"=>"SHARED GPU MEMORY"`. `PickerOrder` becomes `["cpu","memory","io","disk","network","gpu","gpumemory","pid","ioread","iowrite","diskread","diskwrite","download","upload","gpuengine","gpushared"]`.
  - Page (private):
    - `State(activity,key)` returns `activity.Gpu` for the four GPU keys, and `Gap(process,key)` returns `process.GpuGap`. The app overload keeps its first-reason rule.
    - A GPU key is stale when `stale||activity.GpuStale`.
    - `Format`: `"gpu"` like CPU (`"0.#"` + " %", so a known zero is "0 %"); `"gpumemory"` and `"gpushared"` give "0 B" for a known zero and `Ui.Bytes(value)` otherwise (`Ui.Bytes(0)` would be "0.0 B").
    - The `"gpuengine"` cell takes `EngineText`. Null gives "—" with the GPU cell's reason. "" gives "" with no tooltip. A label gives the label, with the tooltip `ProcessCopy.EngineTip(app.GpuBusiest!)`, or `process.GpuEngines![0]` on a process row.
    - `SpokenLabels`: `["gpu"]="GPU",["gpuengine"]="GPU engine",["gpumemory"]="GPU memory",["gpushared"]="shared GPU memory"`. `Spoken` skips a cell whose text is "".

- [ ] **Step 1: Update and write the tests and checks**

```csharp
// ProcessSettingsTests: Defaults_are_off_and_the_five_default_columns becomes Defaults_are_off_and_the_seven_default_columns, expecting ["cpu","memory","io","disk","network","gpu","gpumemory"].
// Unknown_null_and_duplicate_keys_are_dropped_and_ordered: "gpu" (now known) becomes "gputemp"; ["cpu","upload"] stays.
// Toggle_changes_only_that_key: the lists gain "gpu","gpumemory" (pid on: ["pid","cpu","memory","io","disk","network","gpu","gpumemory"]; cpu off: ["memory","io","disk","network","gpu","gpumemory"]).
// ProcessTableTests.Standard_user_hides_the_six_administrator_columns expects ["cpu","memory","io","gpu","gpuengine","gpumemory","gpushared"].
[Fact] public void Gpu_keys_follow_upload_and_none_needs_administrator(){ Assert.Equal(["gpu","gpuengine","gpumemory","gpushared"],ProcessColumnKeys.All.Skip(12));Assert.Equal(16,ProcessColumnKeys.All.Count);
    Assert.Empty(ProcessColumnKeys.AdministratorOnly.Intersect(ProcessColumnKeys.All.Skip(12)));
    Assert.Equal(["GPU","GPU ENGINE","GPU MEMORY","SHARED GPU MEMORY"],ProcessColumnKeys.All.Skip(12).Select(ProcessTableColumns.Header));}
// ProcessTableTests.Picker_disables_admin_entries_and_keeps_their_saved_state: the key list becomes PickerOrder above; every GPU entry is Enabled while not elevated.

// ProcessesPageUiChecks. Sample(pid,name,cpu) gains {GpuPercent=0,GpuEngine="",GpuEngines=[],GpuDedicatedBytes=1_000_000,GpuSharedBytes=0}, so fixtures hold known zeros. Existing asserts:
//   Column_picker_has_every_column_in_fixed_order: headers ["CPU","MEMORY","I/O","DISK","NETWORK","GPU","GPU MEMORY","PID","I/O READ","I/O WRITE","DISK READ","DISK WRITE","↓ DOWNLOAD","↑ UPLOAD","GPU ENGINE","SHARED GPU MEMORY"],
//     checked 7 × true then 9 × false;
//   Header_right_click_opens_picker: Assert.Equal(16,PickerItems(p).Count) (was 12);
//   Picker_toggle_keeps_hidden_admin_and_disk_keys: PID on gives settings ["pid","cpu","memory","io","disk","network","gpu","gpumemory"] and headers ["NAME","PID","CPU","MEMORY","I/O","GPU","GPU MEMORY"];
//     CPU off gives ["pid","memory","io","disk","network","gpu","gpumemory"] and ["NAME","PID","MEMORY","I/O","GPU","GPU MEMORY"];
//   Standard_user_hides_admin_columns…: ["NAME","CPU","MEMORY","I/O","GPU","GPU MEMORY"]; Elevated_shows_the_full_set…: ["NAME","CPU","MEMORY","I/O","DISK","NETWORK","GPU","GPU MEMORY"];
//   Automation_names_and_spoken_summary: "chrome.exe, 3 processes, CPU 9 %, memory 192.0 MB, I/O 9.0 KB/s, disk 22.5 KB/s, network 12.0 KB/s, GPU 0 %, GPU memory 3.0 MB".
// Unknown(activity,gap=None) nulls every sample's GPU values and sets GpuGap. WithEngine(pct,name,label,adapter) gives that app one engine.
// WithEngines() holds a.exe "GPU 0 · Copy", b.exe "GPU 1 · 3D", zero.exe as a known zero and unknown.exe as unknown. ColumnNamed(p,header) finds a DataGrid column by header.
private static void Gpu_dashes_name_each_reason(){
    foreach(string status in new[]{"GPU counters unavailable (error 0xC0000BBC)","GPU counters unavailable","GPU counters unreadable","Too many GPU counter instances"})
    {var (p,_)=Page(activity:Unknown(Fixture()) with{Gpu=new(false,status)});Assert.Equal("—",Cell(p,"Code.exe","gpu"));Assert.Equal(status,Tip(p,"Code.exe","gpu"));Assert.Equal(status,Tip(p,"Code.exe","gpumemory"));}
    var (first,_)=Page(columns:[..ProcessColumnKeys.All],activity:Unknown(Fixture(),ProcessGap.FirstSample));Assert.Equal("first sample",Tip(first,"Code.exe","gpu"));
    Assert.Equal("—",Cell(first,"Code.exe","gpuengine"));Assert.Equal("first sample",Tip(first,"Code.exe","gpuengine"));
    var (stale,_)=Page(activity:Unknown(Fixture(),ProcessGap.FirstSample) with{GpuStale=true});Assert.Equal("stale reading",Tip(stale,"Code.exe","gpu"));Assert.Null(Tip(stale,"Code.exe","cpu"));}
private static void Gpu_engine_cell_tooltip_and_known_zeros(){ var (p,_)=Page(columns:[..ProcessColumnKeys.All],activity:WithEngine(40,"Code.exe","GPU 1 · 3D","NVIDIA GeForce RTX 4070"));
    Assert.Equal("GPU 1 · 3D",Cell(p,"Code.exe","gpuengine"));Assert.Equal("NVIDIA GeForce RTX 4070 · 3D engine",Tip(p,"Code.exe","gpuengine"));Assert.Equal("40 %",Cell(p,"Code.exe","gpu"));
    Assert.Equal("0 %",Cell(p,"Explorer.EXE","gpu"));Assert.Null(Tip(p,"Explorer.EXE","gpu"));Assert.Equal("",Cell(p,"Explorer.EXE","gpuengine"));Assert.Null(Tip(p,"Explorer.EXE","gpuengine"));
    Assert.Equal("0 B",Cell(p,"Explorer.EXE","gpushared"));}
private static void Gpu_engine_sorts_ascending_first_and_gpu_descending_first(){ var (p,_)=Page(columns:[..ProcessColumnKeys.All],activity:WithEngines());
    ClickHeader(p,"GPU ENGINE");Assert.Equal(ListSortDirection.Ascending,ColumnNamed(p,"GPU ENGINE").SortDirection);Assert.Equal(["a.exe","b.exe","zero.exe","unknown.exe"],AppNames(p));
    ClickHeader(p,"GPU ENGINE");Assert.Equal(["b.exe","a.exe","zero.exe","unknown.exe"],AppNames(p));
    foreach(string header in new[]{"GPU","GPU MEMORY","SHARED GPU MEMORY"}){ClickHeader(p,header);Assert.Equal(ListSortDirection.Descending,ColumnNamed(p,header).SortDirection);}}
private static void Gpu_spoken_labels(){ var (p,_)=Page(columns:[..ProcessColumnKeys.All],activity:WithEngine(40,"Code.exe","GPU 1 · 3D","NVIDIA GeForce RTX 4070"));
    Assert.EndsWith(", GPU 40 %, GPU engine GPU 1 · 3D, GPU memory 1.0 MB, shared GPU memory 0 B",Row(p,"Code.exe").Spoken);Assert.DoesNotContain("GPU engine",Row(p,"Explorer.EXE").Spoken);
    var (u,_)=Page(columns:[..ProcessColumnKeys.All],activity:Unknown(Fixture(),ProcessGap.FirstSample));Assert.Contains("GPU engine unknown (first sample)",Row(u,"Code.exe").Spoken);}
// Run calls the four new checks after Footer_lists_the_definitions().
```

- [ ] **Step 2: Run the Core tests, build, commit the tests, push and verify failure**

Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ProcessSettingsTests|FullyQualifiedName~ProcessTableTests"`. Expected: these fail on their assertions: `Gpu_keys_follow_upload_and_none_needs_administrator`, `Defaults_are_off_and_the_seven_default_columns`, `Toggle_changes_only_that_key`, `Standard_user_hides_the_six_administrator_columns` and `Picker_disables_admin_entries_and_keeps_their_saved_state`. The build has 0 errors: the App checks compile.

```bash
git add tests/DeadWare.Tests/ProcessSettingsTests.cs tests/DeadWare.Tests/ProcessTableTests.cs tests/DeadWare.App.Tests/ProcessesPageUiChecks.cs
git commit -m "test(processes): GPU columns, gaps, engine tooltip, sorting and spoken labels"
```
Push this commit alone. Run (CI). Expected: red. The five Core facts above fail, and the UI fact fails in phase `"processes page"` at `Column_picker_has_every_column_in_fixed_order`, the first changed check in `Run`.

- [ ] **Step 3: Implement the keys, headers, picker order and the page cells**
- [ ] **Step 4: Run the Step 2 command and the Core baseline, commit, push and verify in CI.** Expected: PASS, then no output.

```bash
git add src/DeadWare.Core/ProcessColumnKeys.cs src/DeadWare.Core/ProcessTable.cs src/DeadWare.App/ProcessesPage.cs
git commit -m "feat(processes): GPU, GPU engine, GPU memory and shared GPU memory columns"
```
Run (CI). Expected: green. The UI fact passes `"processes page"`, `"processes shell keys"` and `"shell chrome"`.

---

### Task 8: The one-time column upgrade for saved settings

**Files:**
- Modify: `src/DeadWare.Core/ProcessColumnKeys.cs` (`PreviousDefault`, `CurrentVersion`)
- Modify: `src/DeadWare.Core/Monitoring.cs`: `UserSettings` (after `ProcessColumns`, :404) and `SettingsCodec.Read` (after `s.ProcessColumns=ProcessColumnKeys.Normalize(s.ProcessColumns);`, :443)
- Test: `tests/DeadWare.Tests/ProcessSettingsTests.cs`, `tests/DeadWare.App.Tests/ProcessesPageUiChecks.cs` (one check)

**Interfaces:**
- Consumes: Task 7 `ProcessColumnKeys.Default`.
- Produces:
  - `public static IReadOnlyList<string> ProcessColumnKeys.PreviousDefault{get;}=["cpu","memory","io","disk","network"]` and `public const int ProcessColumnKeys.CurrentVersion=2`.
  - `UserSettings.ProcessColumnsVersion` (int, initializer `ProcessColumnKeys.CurrentVersion`).
  - `SettingsCodec.Read` upgrades a file without the property (`document.RootElement.TryGetProperty(nameof(UserSettings.ProcessColumnsVersion),out _)` false) or with a value below 2. A normalized list that equals `PreviousDefault` as a set becomes `[..ProcessColumnKeys.Default]`; any other list stays. Read then always sets the version to 2. `Normalize` and `Toggle` never upgrade.

The UI check compiles without the change, so it goes first and is seen red in CI (Global Constraints, Red checks); the Core tests need new members.

- [ ] **Step 1: Write the UI check**

```csharp
// ProcessesPageUiChecks, called from Run after the Task 7 checks:
private static void Gpu_columns_show_for_fresh_and_0_13_settings_but_not_a_custom_list(){
    IReadOnlyList<string> Saved(string json)=>SettingsCodec.Read(json).Settings.ProcessColumns;
    Assert.Equal(["NAME","CPU","MEMORY","I/O","GPU","GPU MEMORY"],Headers(Page(columns:Saved("{}")).Page));
    Assert.Equal(["NAME","CPU","MEMORY","I/O","GPU","GPU MEMORY"],Headers(Page(columns:Saved("{\"ProcessColumns\":[\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]}")).Page));
    var custom=Page(columns:Saved("{\"ProcessColumns\":[\"cpu\",\"memory\"]}")).Page;Assert.Equal(["NAME","CPU","MEMORY"],Headers(custom));
    Assert.False(PickerItem(custom,"GPU").IsChecked);Assert.False(PickerItem(custom,"GPU MEMORY").IsChecked);}
```

- [ ] **Step 2: Build, commit the check, push and verify failure**

```bash
git add tests/DeadWare.App.Tests/ProcessesPageUiChecks.cs
git commit -m "test(processes): GPU columns for fresh and saved 0.13 settings"
```
Push this commit alone. Run (CI). Expected: red. The UI fact fails in phase `"processes page"` at `Gpu_columns_show_for_fresh_and_0_13_settings_but_not_a_custom_list` (the version-less 0.13 list still shows no GPU columns).

- [ ] **Step 3: Write the failing Core tests**

```csharp
// Defaults_are_off_and_the_seven_default_columns also asserts Assert.Equal(2,s.ProcessColumnsVersion).
[Theory][InlineData("[\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]")][InlineData("[\"network\",\"disk\",\"io\",\"memory\",\"cpu\",\"cpu\"]")][InlineData("[\"cpu\",\"memory\",\"io\",\"disk\",\"network\",\"bogus\",null]")]
public void A_version_less_0_13_default_reads_as_the_new_default(string list){ var s=SettingsCodec.Read($"{{\"ProcessColumns\":{list}}}").Settings;
    Assert.Equal(ProcessColumnKeys.Default,s.ProcessColumns);Assert.Equal(2,s.ProcessColumnsVersion);}
[Fact] public void A_version_below_2_is_upgraded_too()=>Assert.Equal(ProcessColumnKeys.Default,SettingsCodec.Read("{\"ProcessColumnsVersion\":1,\"ProcessColumns\":[\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]}").Settings.ProcessColumns);
[Theory][InlineData("[\"cpu\",\"memory\"]","cpu,memory")][InlineData("[\"pid\",\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]","pid,cpu,memory,io,disk,network")][InlineData("[]","")]
public void Any_other_version_less_list_is_kept(string list,string expected){ var s=SettingsCodec.Read($"{{\"ProcessColumns\":{list}}}").Settings;
    Assert.Equal(expected,string.Join(",",s.ProcessColumns));Assert.Equal(2,s.ProcessColumnsVersion);}
[Fact] public void The_new_default_without_gpu_survives_round_trips(){
    var once=SettingsCodec.Read("{\"ProcessColumnsVersion\":2,\"ProcessColumns\":[\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]}").Settings;
    var twice=SettingsCodec.Read(SettingsCodec.Write(once)).Settings;Assert.Equal(ProcessColumnKeys.PreviousDefault,twice.ProcessColumns);Assert.Equal(2,twice.ProcessColumnsVersion);
    var fresh=new UserSettings();fresh.ProcessColumns=ProcessColumnKeys.Toggle(ProcessColumnKeys.Toggle(fresh.ProcessColumns,"gpu",false),"gpumemory",false);
    Assert.Equal(ProcessColumnKeys.PreviousDefault,SettingsCodec.Read(SettingsCodec.Write(SettingsCodec.Read(SettingsCodec.Write(fresh)).Settings)).Settings.ProcessColumns);}
[Fact] public void Normalize_and_toggle_never_upgrade(){ Assert.Equal(ProcessColumnKeys.PreviousDefault,ProcessColumnKeys.Normalize(ProcessColumnKeys.PreviousDefault));
    Assert.Equal(["pid","cpu","memory","io","disk","network"],ProcessColumnKeys.Toggle(ProcessColumnKeys.PreviousDefault,"pid",true));}
```

- [ ] **Step 4: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ProcessSettingsTests"`. Expected: `error CS0117` for `PreviousDefault`, and `error CS1061` for `ProcessColumnsVersion`.
- [ ] **Step 5: Implement the version and the upgrade**
- [ ] **Step 6: Run the Step 4 command and the Core baseline, commit, push and verify in CI.** Expected: PASS, then no output.

```bash
git add tests/DeadWare.Tests/ProcessSettingsTests.cs
git commit -m "test(settings): the saved 0.13 default gains the GPU columns once"
git add src/DeadWare.Core/ProcessColumnKeys.cs src/DeadWare.Core/Monitoring.cs
git commit -m "feat(settings): upgrade saved 0.13 default columns to the new default once"
```
Run (CI). Expected: green. The UI fact passes `"processes page"`.

---

## 0.15.0 — Step 4: history

### Task 9: `ProcessMinuteFolder` GPU ranking

**Files:**
- Modify: `src/DeadWare.Core/ProcessHistory.cs`: `ProcessHistory`, `ProcessAppMinute`, `ProcessMinuteBatch`, and `ProcessMinuteFolder.Fold` and `Close`
- Test: `tests/DeadWare.Tests/ProcessMinuteFolderTests.cs`; `tests/DeadWare.Tests/ProcessHistoryStoreTests.cs` (`One_day_at_the_cap_stays_under_four_mebibytes` only)

**Interfaces:**
- Consumes: Task 4 `ProcessActivity.GpuIntervalSeconds` and `ProcessDeltas.GpuSeconds`/`GpuMemoryByteSeconds`; Task 6 `ProcessGrouping.GpuUse`; 0.14.1 `ProcessHistory.SavedName`.
- Produces:
  - `ProcessHistory.MaximumApplications=40`.
  - `ProcessAppMinute` init members `double? GpuSeconds` and `double? GpuMemoryByteSeconds`.
  - `ProcessMinuteBatch` init member `double GpuCoveredSeconds`.
  - `Fold`, for an activity with `GpuIntervalSeconds` g:
    - adds g to the minute's GPU-covered seconds;
    - groups the activity's processes by the same target rule as CPU (0.14.1's `SavedName`, then `ProcessGrouping.Key`) and adds `GpuUse(members).Percent / 100 × g` to each app; an all-null app adds nothing;
    - adds Σ members' known `GpuDedicatedBytes` × g to each app's GPU memory;
    - credits unknown-name processes and `activity.Omitted.Deltas` to Everything else with their `Deltas.GpuSeconds`/`GpuMemoryByteSeconds` (null as 0).
  - `Close` takes a fourth `Top(apps,t=>t.Gpu)`; zero never qualifies and ties go by key ordinal. Unlisted apps' GPU goes to Everything else. When GPU-covered is 0, every row's GPU values are null; otherwise they are the sums (0 when none was known).

- [ ] **Step 1: Write the failing tests**

```csharp
// P(...) gains gpu (double?) and gpuMem (long?): gpu null leaves GpuEngines null; gpu 0 gives []; otherwise one GpuEngineUse(0xA,0,0,"3D","GPU 0 · 3D",null,gpu).
// Engine(type,eng) picks another engine. A(...) gains gpuInterval (double?) for ProcessActivity.GpuIntervalSeconds. App(b,name) is b.Applications.Single(a=>a.Name==name).
[Fact] public void Gpu_is_a_fourth_top_ten(){ /* 50 apps: 10 CPU leaders, 10 memory leaders, 10 I/O leaders, 10 GPU leaders (gpu 10..19 %), 10 small; 60 activities with gpuInterval 1 */
    Assert.Equal(40,ProcessHistory.MaximumApplications);Assert.Equal(41,b.Applications.Count);Assert.All(gpuLeaders,n=>Assert.Contains(b.Applications,a=>a.Name==n));Assert.Equal(60,b.GpuCoveredSeconds,6);}
[Fact] public void Two_members_busy_on_different_engines_record_the_higher_sum(){ /* a.exe pid 1 "3D" 30 %, pid 2 "Copy" 50 %, 60 activities, gpuInterval 1 */ Assert.Equal(30,App(b,"a.exe").GpuSeconds!.Value,6);}   // 0.5 × 60, not 0.8 × 60
[Fact] public void Gpu_memory_adds_known_members_times_the_interval(){ /* a.exe pid 1 gpuMem 1e6, pid 2 gpuMem null, 60 × gpuInterval 1 */ Assert.Equal(6e7,App(b,"a.exe").GpuMemoryByteSeconds!.Value,1);}
[Fact] public void Everything_else_holds_unlisted_unknown_name_and_omitted_gpu(){ /* 11 GPU apps (11th is the smallest); "" with Deltas.GpuSeconds 0.2; Omitted Deltas GpuSeconds 0.3, GpuMemoryByteSeconds 10; 60 activities */
    var e=b.Applications.Single(a=>a.EverythingElse);Assert.Equal(unlisted+60*0.2+60*0.3,e.GpuSeconds!.Value,6);Assert.Equal(600,e.GpuMemoryByteSeconds!.Value,6);}
[Fact] public void Gpu_covered_seconds_count_only_folded_activities_with_an_interval(){ /* 30 with gpuInterval 1, 30 with gpuInterval null, 1 with dt null and gpuInterval 1 */ Assert.Equal(30,b.GpuCoveredSeconds,6);}
[Fact] public void A_minute_without_gpu_data_has_null_gpu_on_every_row(){ var b=Close(P("a.exe",cpu:5),P("b.exe",cpu:1,pid:2));Assert.Equal(0,b.GpuCoveredSeconds);
    Assert.All(b.Applications,a=>{Assert.Null(a.GpuSeconds);Assert.Null(a.GpuMemoryByteSeconds);});}
[Fact] public void A_minute_with_gpu_data_reads_zero_for_apps_without_known_values(){ /* a.exe gpu 10; b.exe gpu null, gpuMem null; gpuInterval 1 */ Assert.Equal(0,App(b,"b.exe").GpuSeconds);Assert.Equal(0,App(b,"b.exe").GpuMemoryByteSeconds);}
[Fact] public void Zero_gpu_never_qualifies_and_gpu_ties_break_by_key(){ /* 12 apps with only gpu: "idle.exe" 0 %, eleven tied at 5 % named k00..k10 */ Assert.DoesNotContain(b.Applications,a=>a.Name=="idle.exe");Assert.DoesNotContain(b.Applications,a=>a.Name=="k10.exe");}
// ProcessHistoryStoreTests.One_day_at_the_cap_stays_under_four_mebibytes: Enumerable.Range(0,ProcessHistory.MaximumApplications) becomes Enumerable.Range(0,30), with the comment
// "// 0.14.0's 30-app format; Task 10 moves this test to MaximumApplications and 6 MiB." At 41 rows a minute this setup is about 4.4 MiB (0.14.0 measured 3,522,560 B at 31 rows).
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ProcessMinuteFolderTests|FullyQualifiedName~ProcessHistoryStoreTests"`. Expected: `error CS1061` for `GpuCoveredSeconds` and `GpuSeconds`.
- [ ] **Step 3: Implement the fourth ranking and the GPU totals**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output. `ProcessHistoryStoreTests` pass: the size test is pinned to 30 names, the store's `ValidateBatch` allows `MaximumApplications + 1` rows, and the GPU fields default to null and 0.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ProcessMinuteFolderTests.cs tests/DeadWare.Tests/ProcessHistoryStoreTests.cs
git commit -m "test(history): a fourth top ten by GPU and app-level GPU-seconds"
git add src/DeadWare.Core/ProcessHistory.cs
git commit -m "feat(history): fold per-app GPU-seconds and GPU memory into each minute"
```
Run (CI). Expected: green.

---

### Task 10: `process-history.db` version 2, the upgrade and the 192 MiB cap

**Files:**
- Modify: `src/DeadWare.Core/ProcessHistoryStore.cs`:
  - `MaximumDatabaseBytes` (:31) and `Schema` (:40-50);
  - `Initialize` (:63-80) and `Inspect` (:82-100);
  - `Append`'s two INSERTs (:128-150) and `ValidateBatch` (:297-313).
- Test: `tests/DeadWare.Tests/ProcessHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 9 batches.
- Produces:
  - `MaximumDatabaseBytes=192L*1024*1024`.
  - `Schema` adds `gpu_covered REAL NULL` after `segment` in `minutes`, and `gpu_seconds REAL NULL, gpu_memory_seconds REAL NULL` after `everything_else` in `app_minutes`, matching the upgraded layout. Creation sets `PRAGMA user_version=2`.
  - `Inspect` accepts `user_version` 1 or 2; anything else is "Unsupported process history; the file was preserved.". A version 2 file must have the three columns (`SELECT COUNT(*) FROM pragma_table_info('app_minutes') WHERE name IN('gpu_seconds','gpu_memory_seconds')` = 2 and `pragma_table_info('minutes')` holding `gpu_covered`), else "Incompatible process history objects; the file was preserved.".
  - `private static void Upgrade(SqliteConnection db,Action? beforeCommit)`, called from `Initialize` for an existing file right after `PRAGMA journal_mode=WAL` and before the final `Checkpoint`. In one transaction it re-reads `user_version` and, if still 1, runs the spec's three `ALTER TABLE … ADD COLUMN … REAL NULL` statements and `PRAGMA user_version=2`, calls `beforeCommit`, then commits. Any failure rolls back.
  - The test seam `private Action? beforeUpgradeCommit = null;` is set by reflection like `afterFirstRead`, with the same comment style.
  - `Append` writes `gpu_covered` as NULL when `GpuCoveredSeconds` is 0, else the value, and writes the app GPU values as given (`DBNull` for null).
  - `ValidateBatch` also rejects:
    - `GpuCoveredSeconds` that is not finite or is below 0;
    - an app GPU value that is not null and either not finite or below 0;
    - any app GPU value when `GpuCoveredSeconds` is 0;
    - more than `MaximumApplications + 1` rows (now 41).
    0.14.1's `SavedName` clause stays.

- [ ] **Step 1: Write the failing tests**

```csharp
// WriteVersion1(path, apps): runs the 0.14.0 schema text verbatim (git show dd4fb74:src/DeadWare.Core/ProcessHistoryStore.cs, the Schema constant at :40-50),
// PRAGMA application_id=ApplicationId and user_version=1, and inserts minute Day with the given (name,cpu) rows; ClearAllPools after. Columns(table) reads pragma_table_info names.
[Fact] public void A_new_file_is_version_2_with_the_gpu_columns(){ using var f=new Fixture();f.Open();Assert.Equal(2L,f.Count("PRAGMA user_version"));
    Assert.Contains("gpu_covered",f.Columns("minutes"));Assert.Contains("gpu_seconds",f.Columns("app_minutes"));Assert.Contains("gpu_memory_seconds",f.Columns("app_minutes"));}
[Fact] public void A_version_1_file_is_upgraded_in_place_and_keeps_its_rows(){ using var f=new Fixture();WriteVersion1(f.DbPath,[("a.exe",10_000_000)]);var store=f.Open();
    Assert.Equal(2L,f.Count("PRAGMA user_version"));var row=store.ReadApplications("m",Day,Day.AddMinutes(1),ProcessRank.CpuTime,Now).Rows.Single();
    Assert.Equal(("a.exe",1.0),(row.Name,row.CpuSeconds));Assert.Equal(1L,f.Count("SELECT COUNT(*) FROM minutes WHERE gpu_covered IS NULL"));
    Assert.Equal(1L,f.Count("SELECT COUNT(*) FROM app_minutes WHERE gpu_seconds IS NULL AND gpu_memory_seconds IS NULL"));}   // ProcessResourceRow's GPU members come in Task 11
[Fact] public void A_failed_upgrade_leaves_a_valid_version_1_file(){ using var f=new Fixture();WriteVersion1(f.DbPath,[("a.exe",10_000_000)]);var store=new ProcessHistoryStore(f.DbPath);
    typeof(ProcessHistoryStore).GetField("beforeUpgradeCommit",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(store,(Action)(()=>throw new InvalidOperationException("seam")));
    Assert.Throws<InvalidOperationException>(store.Initialize);SqliteConnection.ClearAllPools();
    Assert.Equal(1L,f.Count("PRAGMA user_version"));Assert.DoesNotContain("gpu_seconds",f.Columns("app_minutes"));Assert.Equal(1L,f.Count("SELECT COUNT(*) FROM app_minutes"));
    f.Open();Assert.Equal(2L,f.Count("PRAGMA user_version"));}
[Theory][InlineData("gpu_covered")][InlineData("gpu_seconds")][InlineData("gpu_memory_seconds")]
public void A_version_2_file_missing_a_gpu_column_is_rejected_and_preserved(string missing){ using var f=new Fixture();WriteVersion1(f.DbPath,[]);
    foreach(var (table,column) in new[]{("minutes","gpu_covered"),("app_minutes","gpu_seconds"),("app_minutes","gpu_memory_seconds")})
        if(column!=missing)f.Execute($"ALTER TABLE {table} ADD COLUMN {column} REAL NULL");
    f.Execute("PRAGMA user_version=2");
    byte[] before=File.ReadAllBytes(f.DbPath);var e=Assert.Throws<InvalidDataException>(new ProcessHistoryStore(f.DbPath).Initialize);
    Assert.Equal("Incompatible process history objects; the file was preserved.",e.Message);Assert.Equal(before,File.ReadAllBytes(f.DbPath));}
// Unreadable_files_are_preserved_and_reported("newer") now writes user_version=3 and also asserts the message "Unsupported process history; the file was preserved.".
[Fact] public void Gpu_values_are_stored_and_a_minute_without_gpu_stores_null(){ using var f=new Fixture();var store=f.Open();
    store.Append("m",[Batch(Day,apps:[App("a.exe",5) with{GpuSeconds=12,GpuMemoryByteSeconds=6e9},Else(1) with{GpuSeconds=0,GpuMemoryByteSeconds=0}]) with{GpuCoveredSeconds=60},Batch(Day.AddMinutes(1),apps:[App("a.exe",5)])],Now);
    Assert.Equal(60.0,f.Query("SELECT gpu_covered FROM minutes ORDER BY minute",r=>{r.Read();return r.GetDouble(0);}));Assert.Equal(1L,f.Count("SELECT COUNT(*) FROM minutes WHERE gpu_covered IS NULL"));
    Assert.Equal(12.0,f.Query("SELECT gpu_seconds FROM app_minutes a JOIN names n ON n.id=a.name_id WHERE n.upper='A.EXE' ORDER BY minute",r=>{r.Read();return r.GetDouble(0);}));}
[Fact] public void Invalid_gpu_values_are_rejected_without_writing(){ using var f=new Fixture();var store=f.Open();var ok=Batch(Day,apps:[App("a.exe",5)]);
    foreach(var bad in new[]{ok with{GpuCoveredSeconds=double.NaN},ok with{GpuCoveredSeconds=-1},ok with{Applications=[App("a.exe",5) with{GpuSeconds=1}]},
        ok with{Applications=[App("a.exe",5) with{GpuMemoryByteSeconds=1}]},
        ok with{GpuCoveredSeconds=60,Applications=[App("a.exe",5) with{GpuSeconds=-1}]},ok with{GpuCoveredSeconds=60,Applications=[App("a.exe",5) with{GpuMemoryByteSeconds=double.PositiveInfinity}]},
        ok with{Applications=[..Enumerable.Range(0,41).Select(i=>App($"a{i}.exe",1)),Else(1)]}})
        Assert.Throws<ArgumentException>(()=>store.Append("m",[bad],Now));
    Assert.Empty(f.Days());}
// Cap_and_retention_constants: Assert.Equal(192L*1024*1024,ProcessHistoryStore.MaximumDatabaseBytes).
// One_day_at_the_cap_stays_under_four_mebibytes (pinned to 30 names in Task 9) becomes One_day_at_the_cap_stays_under_six_mebibytes: 1,440 complete minutes under "DESKTOP-ABCDEFG",
// each with the same 40 names (Enumerable.Range(0,ProcessHistory.MaximumApplications) again; the Task 9 comment goes) plus Everything else, big = long.MaxValue/4 for cpu/io_read/io_write, and memory, GpuSeconds and GpuMemoryByteSeconds = 1.2345678901234567e15 + i
// (a fractional part, so SQLite keeps 8-byte REALs), with GpuCoveredSeconds = 59.987654321. Assert.True(store.StorageBytes < 6 * 1024 * 1024, $"One day used {store.StorageBytes:N0} bytes.");
```
`Fixture` gains `Execute(string sql)` (a connection with `Pooling = false`, as `Unreadable_files_are_preserved_and_reported` opens) and `Columns(string table)` beside `Query`.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ProcessHistoryStoreTests"`. Expected: `A_new_file_is_version_2_with_the_gpu_columns`, `A_version_1_file_is_upgraded_in_place_and_keeps_its_rows`, `Cap_and_retention_constants` and the GPU value facts fail (version 1, no columns, 128 MiB), and each `A_version_2_file_missing_a_gpu_column_is_rejected_and_preserved` case fails on its message ("Unsupported process history; the file was preserved."). `A_failed_upgrade_leaves_a_valid_version_1_file` fails at `GetField("beforeUpgradeCommit")` (null).
- [ ] **Step 3: Implement the schema, inspection, upgrade, writes and validation**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output. Note the one-day size in the PR description for Task 14.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ProcessHistoryStoreTests.cs
git commit -m "test(history): format version 2, the in-place upgrade and the 192 MiB cap"
git add src/DeadWare.Core/ProcessHistoryStore.cs
git commit -m "feat(history): store per-app GPU-seconds and GPU memory (format 2, 192 MiB)"
```
Run (CI). Expected: green. `ProcessHistoryServiceTests` and `CoolingShutdownTests` pass on version 2 files.

---

### Task 11: History reads ranked by GPU

**Files:**
- Modify: `src/DeadWare.Core/ProcessHistoryStore.cs`:
  - `ProcessRank` (:6), `ProcessResourceRow` (:9-19) and `ProcessResourceView` (:23-25);
  - `ReadApplications` (:177-234): its `order`, `Columns`, `total` and the recorded-minutes query;
  - `ReadSeries` (:238-285) and `Row` (:287-292).
- Test: `tests/DeadWare.Tests/ProcessHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 10's columns.
- Produces:
  - `public enum ProcessRank { CpuTime, AverageMemory, Io, Gpu }`
  - `ProcessResourceRow` init members `double? GpuSeconds` (`SUM(a.gpu_seconds)`, null when all are null), `double? AverageGpuPercent` and `double? AverageGpuMemoryBytes`. `PartOf(ProcessRank.Gpu)` is `GpuSeconds ?? 0`.
  - `ProcessResourceView` init member `double GpuCoveredSeconds` (`TOTAL(gpu_covered)` over the period's recorded minutes).
  - Avg GPU = 100 × `GpuSeconds` ÷ the view's `GpuCoveredSeconds`, for every row including Everything else (unclamped).
  - Avg GPU memory = `SUM(a.gpu_memory_seconds)` ÷ `TOTAL(CASE WHEN a.gpu_memory_seconds IS NOT NULL THEN m.gpu_covered END)`.
  - Each average is null when its numerator is null or its divisor is 0.
  - Rank `Gpu` orders by `TOTAL(a.gpu_seconds) DESC, n.display`. `RankTotal` is `TOTAL(gpu_seconds)` over the period, Everything else included.
  - `ReadSeries(…,ProcessRank.Gpu,…)` uses only the app's minutes with `a.gpu_seconds IS NOT NULL AND m.gpu_covered>0`, which also go into bucket `Minutes` and the statistics. Sum is `100.0*TOTAL(a.gpu_seconds)`, weight is `TOTAL(m.gpu_covered)`, and each value is `100.0*a.gpu_seconds/m.gpu_covered`.

- [ ] **Step 1: Write the failing tests**

```csharp
// G(minute,covered,apps) = Batch(minute,apps:apps) with{GpuCoveredSeconds=covered}; Gpu(name,seconds,mem=0)=App(name,1) with{GpuSeconds=seconds,GpuMemoryByteSeconds=mem};
// Read(rank,minutes=1)=store.ReadApplications("m",Day,Day.AddMinutes(minutes),rank,Now) on the fixture's store.
[Fact] public void Rank_by_gpu_orders_by_gpu_seconds_and_the_total_includes_everything_else(){ store.Append("m",[G(Day,60,[Gpu("b.exe",10),Gpu("a.exe",30),App("c.exe",99) with{GpuSeconds=0,GpuMemoryByteSeconds=0},Else(1) with{GpuSeconds=5,GpuMemoryByteSeconds=0}])],Now);
    var v=Read(ProcessRank.Gpu);Assert.Equal(["a.exe","b.exe","c.exe"],v.Rows.Select(r=>r.Name));Assert.Equal(45,v.RankTotal,6);Assert.Equal(30,v.Rows[0].PartOf(ProcessRank.Gpu),6);}
[Fact] public void Avg_gpu_is_a_share_of_gpu_covered_time_only(){ var none=Else(1) with{GpuSeconds=0,GpuMemoryByteSeconds=0};
    store.Append("m",[G(Day,60,[Gpu("a.exe",30),Else(1) with{GpuSeconds=180,GpuMemoryByteSeconds=0}]),G(Day.AddMinutes(1),60,[Gpu("a.exe",0),none]),
        Batch(Day.AddMinutes(2),apps:[App("a.exe",1),Else(1)]),Batch(Day.AddMinutes(3),apps:[App("a.exe",1),Else(1)]),G(Day.AddMinutes(4),30,[Gpu("b.exe",6),none])],Now);
    var v=Read(ProcessRank.Gpu,minutes:5);Assert.Equal(150,v.GpuCoveredSeconds,6);
    // 100 × 30 ÷ 150: not 25 (÷ the 120 s a.exe was listed, or the mean of its minutes' 50 % and 0 %) and not 16.7 (the mean of 50 %, 0 % and 0 %).
    Assert.Equal(20,v.Rows.Single(r=>r.Name=="a.exe").AverageGpuPercent!.Value,6);Assert.Equal(4,v.Rows.Single(r=>r.Name=="b.exe").AverageGpuPercent!.Value,6);
    Assert.Equal(120,v.EverythingElse.AverageGpuPercent!.Value,6);}   // unclamped
[Fact] public void Avg_gpu_memory_while_listed_divides_by_the_listed_gpu_minutes(){ store.Append("m",[G(Day,60,[Gpu("a.exe",1,6e9)]),Batch(Day.AddMinutes(1),apps:[App("a.exe",1)]),G(Day.AddMinutes(2),60,[Gpu("b.exe",1)])],Now);
    Assert.Equal(1e8,Read(ProcessRank.Gpu,minutes:3).Rows.Single(r=>r.Name=="a.exe").AverageGpuMemoryBytes!.Value,1);}
[Fact] public void Gpu_values_are_null_without_gpu_data(){ store.Append("m",[Batch(Day,apps:[App("a.exe",5),Else(1)])],Now);var v=Read(ProcessRank.Gpu);var r=v.Rows.Single();
    Assert.Equal(0,v.GpuCoveredSeconds);Assert.Null(r.GpuSeconds);Assert.Null(r.AverageGpuPercent);Assert.Null(r.AverageGpuMemoryBytes);Assert.Equal(0,r.PartOf(ProcessRank.Gpu));Assert.Equal(0,v.RankTotal);}
[Fact] public void The_gpu_series_uses_only_minutes_with_gpu_and_leaves_the_rest_as_gaps(){
    store.Append("m",[G(Day,60,[Gpu("a.exe",30)]),G(Day.AddMinutes(1),30,[Gpu("a.exe",3)]),Batch(Day.AddMinutes(2),apps:[App("a.exe",1)]),G(Day.AddMinutes(3),60,[Gpu("a.exe",6)]),
        G(Day.AddMinutes(4),60,[Gpu("b.exe",1)])],Now);
    var s=store.ReadSeries("m","a.exe",ProcessRank.Gpu,Day,Day.AddMinutes(6),3,Now);Assert.Equal(120_000,s.BucketMilliseconds);Assert.Equal(2,s.Buckets.Count);   // the third bucket has no GPU minute of a.exe
    var two=s.Buckets[0];Assert.Equal(2L,two.Minutes);Assert.Equal(100.0*33/90,two.Average,6);Assert.Equal((10.0,50.0),(two.Minimum,two.Maximum));   // Σ ÷ Σ, not the mean 30
    Assert.Equal((Day.AddMinutes(2),1L,10.0),(s.Buckets[1].Start,s.Buckets[1].Minutes,s.Buckets[1].Average));   // minute 2 (no GPU data) is a gap
    Assert.Equal(26,s.Statistics.Average!.Value,6);}   // 100 × 39 ÷ 150
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ProcessHistoryStoreTests"`. Expected: `error CS0117` for `ProcessRank.Gpu`.
- [ ] **Step 3: Implement the rank, the averages and the GPU series**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ProcessHistoryStoreTests.cs
git commit -m "test(history): rank by GPU, GPU averages over GPU-covered time and the GPU series"
git add src/DeadWare.Core/ProcessHistoryStore.cs
git commit -m "feat(history): read per-app GPU history"
```
Run (CI). Expected: green. `ProcessHistoryService` passes `ProcessRank.Gpu` through unchanged.

---

### Task 12: Application resources card and the history copy

**Files:**
- Modify: `src/DeadWare.App/ApplicationResourcesCard.cs`:
  - `rank.ItemsSource` (:63);
  - the columns (:70-72);
  - 0.14.1's `ResourceRow` class and `RefreshRows`;
  - the note (:77);
  - `Metric` (:360-365).
- Modify: `src/DeadWare.App/Windows.cs:159` (History and alerts help)
- Modify: `src/DeadWare.App/UsageHistoryPage.cs:112` (Keep a longer record)
- Test: `tests/DeadWare.App.Tests/ApplicationResourcesUiChecks.cs`, `tests/DeadWare.App.Tests/ShellUiChecks.cs:87`, `tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs:23` and `:60`

**Interfaces:**
- Consumes: Task 11 `ProcessRank.Gpu`, the row and view members.
- Produces (card-private):
  - `new RankChoice("GPU",ProcessRank.Gpu)` after I/O.
  - Columns "Avg GPU" and "Avg GPU memory while listed" after "Avg memory while listed", each with a cell tooltip binding. Their header tooltips read `$"GPU recorded for {Duration(view.GpuCoveredSeconds)} of this range"` and are refreshed on each `Apply`.
  - `ResourceRow` gains `AverageGpu`, `AverageGpuMemory`, `AverageGpuTip` and `AverageGpuMemoryTip`, and `Update(ProcessResourceRow row,double gpuCovered)` fills them:
    - A null value shows "—". Its tip is "No GPU data in this range" when `gpuCovered` is 0, else "No GPU data in the minutes this app was listed".
    - Avg GPU is `"0.#"` + " %", unclamped. Everything else's tip is "Sum over many apps; can exceed 100 %".
    - Avg GPU memory uses `Ui.Bytes`.
  - Copy: the note's sentence "Each minute lists up to 30 of the busiest applications by CPU, memory and I/O; Everything else holds the rest of the apps the scan saw." becomes the spec's text exactly: "Each minute lists up to 40 of the busiest applications by CPU, memory, I/O and GPU; Everything else holds the rest." (the spec quoted the old sentence shortened; its new text is used as written). The note then gains, after its "Avg memory while listed …" sentence, "Avg GPU is its busiest GPU engine's share of the time GPU data was recorded; Avg GPU memory while listed averages its dedicated GPU memory over the minutes it was listed with GPU data."
  - `Metric(ProcessRank.Gpu)` is `("%", 100, "GPU % of the busiest engine", "GPU", Theme.GraphKey("gpu"))`, with an automatic maximum (null) when the charted app is Everything else.
  - Windows.cs:159 ends "While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days.". UsageHistoryPage.cs:112 ends "While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days (up to 192 MiB).".

- [ ] **Step 1: Update and write the checks**

```csharp
// Table_has_the_seven_columns_and_everything_else_last becomes Table_has_the_nine_columns_and_everything_else_last:
//   ["Application","CPU time","Avg CPU","Avg memory while listed","Avg GPU","Avg GPU memory while listed","I/O read","I/O written","Minutes listed"].
// ShellUiChecks:87 EndsWith(" While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days.");
// UsageHistoryUiChecks: ProcessHistorySentence = " While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days (up to 192 MiB).", and :60 EndsWith("for 30 days (up to 192 MiB).").
// GpuView(covered,chrome,everythingElse) (new), shaped like View(from,to,recorded) (:741): From now.AddDays(-1), To now, 60 recorded minutes, rows chrome.exe and Everything else, GpuCoveredSeconds covered;
// each row's AverageGpuPercent is the given value, its GpuSeconds that value × covered ÷ 100, and its AverageGpuMemoryBytes 1e6 when the value is known, else all null.
private static void Gpu_rank_columns_and_header_tooltips(string folder){ var card=new ApplicationResourcesCard(Session(null,folder));
    Assert.Equal(["CPU time","Average memory","I/O","GPU"],RankLabels(card));card.Apply(GpuView(43_440,10,20),DateTimeOffset.UtcNow);
    Assert.Equal("GPU recorded for 12 h 4 min of this range",HeaderTip(card,"Avg GPU"));Assert.Equal("GPU recorded for 12 h 4 min of this range",HeaderTip(card,"Avg GPU memory while listed"));}
private static void Gpu_dashes_name_their_reason(string folder){ var card=new ApplicationResourcesCard(Session(null,folder));
    card.Apply(GpuView(0,null,null),DateTimeOffset.UtcNow);Assert.Equal("—",Cell(Row(card,"chrome.exe"),"AverageGpu"));Assert.Equal("No GPU data in this range",Tip(card,"chrome.exe","Avg GPU"));
    card.Apply(GpuView(60,null,30),DateTimeOffset.UtcNow);Assert.Equal("No GPU data in the minutes this app was listed",Tip(card,"chrome.exe","Avg GPU memory while listed"));
    card.Apply(GpuView(60,10,125),DateTimeOffset.UtcNow);Assert.Equal("125 %",Cell(Row(card,"Everything else"),"AverageGpu"));Assert.Equal("Sum over many apps; can exceed 100 %",Tip(card,"Everything else","Avg GPU"));}
private static void Selecting_an_app_while_ranked_by_gpu_charts_its_gpu(ApplicationResourcesCard card){ SetRank(card,ProcessRank.Gpu);WaitFor(()=>!card.Loading&&card.Rank==ProcessRank.Gpu);
    Select(card,"chrome.exe");WaitFor(()=>Traces(card).Count==1);Assert.Contains("chrome.exe · GPU % of the busiest engine",Texts(card));Assert.Equal("chrome.exe GPU",Traces(card)[0].Label);
    Assert.Equal("%",ChartUnit(card));Assert.Equal(100d,ChartMaximum(card));
    Select(card,"Everything else");Assert.Null(ChartMaximum(card));SetRank(card,ProcessRank.CpuTime);}
private static void Rank_by_gpu_over_version_1_minutes_shows_dashes_and_no_share_bar(string folder){   // Review Focus 5
    // A ProcessHistoryStore at Path.Combine(folder,"gpu-v1","process-history.db") holds 60 minutes ending ten minutes ago without GPU data (GpuCoveredSeconds 0) for chrome.exe,
    // code.exe and Everything else. card=new ApplicationResourcesCard(Session(new ProcessHistoryService(path,Machine),folder)); card.Load(now.AddDays(-1),now,Points).
    SetRank(card,ProcessRank.Gpu);WaitFor(()=>!card.Loading&&card.Rank==ProcessRank.Gpu&&card.View is {GpuCoveredSeconds:0});
    Assert.Equal(["chrome.exe","code.exe","Everything else"],RowNames(card));Assert.All(Rows(card),r=>Assert.Equal("—",Cell(r,"AverageGpu")));
    Assert.Equal("No GPU data in this range",Tip(card,"chrome.exe","Avg GPU"));Assert.Equal(Visibility.Collapsed,Named(card,"Share of period total").Visibility);
    Select(card,"chrome.exe");WaitFor(()=>Traces(card).Count==1);Assert.Empty(Traces(card)[0].View.Buckets);}
private static void The_note_names_gpu(ApplicationResourcesCard card){ var note=Texts(card).Single(t=>t.StartsWith("CPU time, I/O read and I/O written",StringComparison.Ordinal));
    Assert.Contains("Each minute lists up to 40 of the busiest applications by CPU, memory, I/O and GPU; Everything else holds the rest. Usage after",note);
    Assert.Contains("Avg GPU is its busiest GPU engine's share of the time GPU data was recorded; Avg GPU memory while listed averages its dedicated GPU memory over the minutes it was listed with GPU data.",note);}
// New helpers beside Headers and Cell: GpuView (above); Row(card,name)=Rows(card).Single(r=>Cell(r,"Name")==name); HeaderTip(card,header), the column header's tooltip text;
// Tip(card,app,header), the tooltip text of that row's cell; RankLabels(card), the Rank by choices' labels. ChartMaximum, ChartUnit, Traces, Texts and Named already exist.
// Run calls the folder checks with the other folder checks, and the card checks after Selecting_an_app_shows_its_chart_and_rank_by_redraws.
```

- [ ] **Step 2: Build to verify the checks compile, commit, push and verify failure**

```bash
git add tests/DeadWare.App.Tests/ApplicationResourcesUiChecks.cs tests/DeadWare.App.Tests/ShellUiChecks.cs tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs
git commit -m "test(usage): GPU rank, columns, tooltips, chart and copy on Application resources"
```
Run (CI). Expected: the UI fact fails at the first changed copy assertion, in phase `"shell chrome"` or `"usage history"`, whichever runs first.

- [ ] **Step 3: Implement the rank, columns, row cells, note, chart metric and copy**
- [ ] **Step 4: Commit, push and verify pass**

```bash
git add src/DeadWare.App/ApplicationResourcesCard.cs src/DeadWare.App/Windows.cs src/DeadWare.App/UsageHistoryPage.cs
git commit -m "feat(usage): rank and average application resources by GPU"
```
Run (CI). Expected: green. The UI fact passes `"shell chrome"` and `"usage history"`, including 0.14.1's in-place row check (`The_minute_refresh_updates_the_rows_in_place_and_keeps_the_current_cell`).

---

### Task 13: Click a moment on the GPU chart

**Files:**
- Modify: `src/DeadWare.App/UsageHistoryPage.cs`: `Moments` (:30) and its comment (:29); `ShowMoment`'s doc comment (:154-156)
- Modify: `src/DeadWare.App/ApplicationResourcesCard.cs`: `ShowSlot` (:122-130) and `ClearSlot` (the GPU chart flag), and `RefreshRows`'s summary (:275-289: the "No GPU data in this span." text)
- Test: `tests/DeadWare.App.Tests/ApplicationResourcesUiChecks.cs`

**Interfaces:**
- Consumes: 0.14.1's `SelectionMoved`, `marked`, `MoveMoment`, `MarkMoment` and the Esc handler, all of which loop over `Moments`; `ShowSlot(DateTimeOffset,DateTimeOffset,ProcessRank)`; Task 11's `ProcessResourceView.GpuCoveredSeconds`.
- Produces: `Moments = [("cpu", ProcessRank.CpuTime), ("memory", ProcessRank.AverageMemory), ("gpu", ProcessRank.Gpu)]`.
  - The GPU chart becomes `Selectable` and focusable. It shares the CPU chart's grid, because every metric is read with the CPU chart's `points`.
  - `ShowMoment` and `MoveMoment` return when `data` has no `"gpu"` view, so with no GPU selected there is nothing to click.
  - `MarkMoment` sets the GPU chart's index to null when it has no bucket at `marked`.
  - The comment at :29 names CPU, RAM and GPU.
  - On the card, `private bool slotFromGpu` is set by `ShowSlot` to `ranking == ProcessRank.Gpu` (the GPU chart's call) and cleared by `ClearSlot`. While `slotFromGpu` and Rank by is GPU, a slot whose view has recorded minutes but `GpuCoveredSeconds` 0 shows "No GPU data in this span." in place of the table (frame collapsed), as the empty-slot case does. "No process data in this span." still wins when the slot has no recorded minutes. A slot listed from the CPU or RAM chart and then ranked by GPU shows its table, with "—" in the GPU columns (spec: the message follows a GPU chart click). Clear stays.

- [ ] **Step 1: Write the failing checks**

```csharp
// Only_the_cpu_and_ram_charts_are_selectable becomes Only_the_cpu_ram_and_gpu_charts_are_selectable: Selectable and Focusable on "cpu", "memory" and "gpu", and on no other chart.
// Data(page) (new; the existing PageData returns only From and To) reads the page's data field as UsagePageData by reflection, as 0.14.1's checks do. WithGpu(page) applies Data(page)
// with GpuName "Fixture GPU" and a copy of Metrics whose "gpu" view is the CPU view, so the GPU chart has the CPU chart's buckets; WithoutGpu(data) is a copy with GpuName null and
// no "gpu" view. The shared fixture's minutes have no GPU data. Each page check starts on Day with the period ranking shown and nothing listed, and ends the same way.
static readonly string[] Three=["cpu","memory","gpu"];
private static void Clicking_a_gpu_point_ranks_the_slot_by_gpu(UsageHistoryPage page,ApplicationResourcesCard card){ WithGpu(page);Activate(page.Chart("gpu"),1);
    WaitFor(()=>!card.Loading&&card.Slot is not null&&card.Rank==ProcessRank.Gpu);Assert.StartsWith("Busiest apps · ",Heading(card));
    Assert.Equal([1,1,1],Three.Select(k=>page.Chart(k).SelectedIndex));Layout(page);Assert.All(Three,k=>Assert.Equal(1,Hairlines(page.Chart(k))));
    Assert.Contains("No GPU data in this span.",Texts(card));Assert.False(IsShown(Table(card),card));Assert.True(IsShown(Button(card,"Clear"),card));
    Activate(page.Chart("cpu"),1);WaitFor(()=>!card.Loading&&card.Rank==ProcessRank.CpuTime);Assert.DoesNotContain("No GPU data in this span.",Texts(card));Assert.True(IsShown(Table(card),card));
    SetRank(card,ProcessRank.Gpu);WaitFor(()=>!card.Loading&&card.Rank==ProcessRank.Gpu);   // a CPU-listed slot ranked by GPU keeps its table
    Assert.DoesNotContain("No GPU data in this span.",Texts(card));Assert.True(IsShown(Table(card),card));Click(Button(card,"Clear"));}
private static void Keys_on_the_gpu_chart_move_all_three_and_esc_clears(UsageHistoryPage page,ApplicationResourcesCard card){ WithGpu(page);var gpu=page.Chart("gpu");
    Press(gpu,Key.Right);Assert.Equal([0,0,0],Three.Select(k=>page.Chart(k).SelectedIndex));Press(gpu,Key.Right);Press(gpu,Key.Right);Assert.Equal([2,2,2],Three.Select(k=>page.Chart(k).SelectedIndex));
    Press(gpu,Key.Left);Assert.Equal([1,1,1],Three.Select(k=>page.Chart(k).SelectedIndex));
    Assert.True(gpu.HandleKey(Key.Enter));WaitFor(()=>!card.Loading&&card.Rank==ProcessRank.Gpu&&card.Slot is not null);
    Press(gpu,Key.Escape);Assert.Null(card.Slot);Assert.All(Three,k=>Assert.Null(page.Chart(k).SelectedIndex));}
private static void Clear_removes_the_gpu_hairline_too(UsageHistoryPage page,ApplicationResourcesCard card){ WithGpu(page);Activate(page.Chart("gpu"),1);WaitFor(()=>card.Slot is not null);
    Click(Button(card,"Clear"));Assert.All(Three,k=>Assert.Null(page.Chart(k).SelectedIndex));}
private static void A_gpu_click_on_a_slot_without_process_data_says_so(UsageHistoryPage page,ApplicationResourcesCard card,DateTimeOffset lastProcessMinute){
    // The empty bucket Clicking_a_slot_without_process_data_says_so uses (after lastProcessMinute), activated on the GPU chart.
    WithGpu(page);Activate(page.Chart("gpu"),EmptyIndex(page,lastProcessMinute));WaitFor(()=>!card.Loading&&card.Slot is not null);
    Assert.Contains("No process data in this span.",Texts(card));Assert.DoesNotContain("No GPU data in this span.",Texts(card));Assert.True(IsShown(Button(card,"Clear"),card));Click(Button(card,"Clear"));}
private static void Clearing_the_dashboard_gpu_keeps_the_listed_gpu_moment(UsageHistoryPage page,ApplicationResourcesCard card){   // Review Focus 4
    WithGpu(page);Activate(page.Chart("gpu"),1);WaitFor(()=>card.Slot is not null);var slot=card.Slot;page.Apply(WithoutGpu(Data(page)));
    Assert.Null(page.Chart("gpu").SelectedIndex);Assert.Equal(1,page.Chart("cpu").SelectedIndex);Assert.Equal(1,page.Chart("memory").SelectedIndex);Assert.Equal(slot,card.Slot);
    // The empty GPU chart has no point to click (Activate would assert one) and its keys do nothing.
    var gpu=page.Chart("gpu");Layout(page);gpu.HandleClick(new(gpu.PlotRect.Left+gpu.PlotRect.Width/2,gpu.PlotRect.Top+gpu.PlotRect.Height/2));
    Assert.False(gpu.HandleKey(Key.Right));Assert.False(gpu.HandleKey(Key.Enter));Assert.Null(gpu.SelectedIndex);Assert.Equal(slot,card.Slot);Assert.Equal(1,page.Chart("cpu").SelectedIndex);
    Press(page.Chart("cpu"),Key.Right);Assert.Equal(2,page.Chart("memory").SelectedIndex);Press(page.Chart("cpu"),Key.Escape);Assert.Null(card.Slot);}
private static void A_gpu_slot_with_gpu_data_lists_apps_by_gpu_and_everything_else(string folder){
    // A ProcessHistoryStore at Path.Combine(folder,"gpu-slot","process-history.db") with one minute (ten minutes ago): a.exe 30 GPU-s, b.exe 6 GPU-s, Everything else 3 GPU-s,
    // GpuCoveredSeconds 60. Without a ProcessHistory service the card would only say "Application resource history is not available."
    var card=new ApplicationResourcesCard(Session(new ProcessHistoryService(path,Machine),folder));card.ShowSlot(minute,minute.AddMinutes(1),ProcessRank.Gpu);
    WaitFor(()=>!card.Loading&&card.View is not null);
    Assert.Equal(["a.exe","b.exe","Everything else"],RowNames(card));Assert.Equal("50 %",Cell(Row(card,"a.exe"),"AverageGpu"));Assert.Equal("5 %",Cell(Row(card,"Everything else"),"AverageGpu"));}
// Run calls the page checks right after Clicking_a_slot_without_process_data_says_so(page, card, minute.AddMinutes(-11)), which ends on Day with nothing listed (after Esc), so
// 0.14.1's Esc_clears_… still hands A_new_period_clears_the_moment its listed slot. The folder check runs with the other folder checks.
// EmptyIndex(page,lastProcessMinute) is the lookup Clicking_a_slot_without_process_data_says_so already does, moved to a helper that both use: the CPU chart's last bucket index,
// asserting that bucket starts after lastProcessMinute and has minutes.
```

- [ ] **Step 2: Build, commit the checks, push and verify failure**

```bash
git add tests/DeadWare.App.Tests/ApplicationResourcesUiChecks.cs
git commit -m "test(usage): click a moment on the GPU chart"
```
Run (CI). Expected: the UI fact fails in phase `"usage history"` at `Only_the_cpu_ram_and_gpu_charts_are_selectable` (the GPU chart is not selectable).

- [ ] **Step 3: Add the GPU moment and its guards**
- [ ] **Step 4: Commit, push and verify pass**

```bash
git add src/DeadWare.App/UsageHistoryPage.cs src/DeadWare.App/ApplicationResourcesCard.cs
git commit -m "feat(usage): click a moment on the GPU chart ranks the slot by GPU"
```
Run (CI). Expected: green. The UI fact passes `"usage history"`, including the 0.14.1 cursor checks, which now also move the GPU chart when it has data.

---

## 0.15.0 — Release

### Task 14: 0.15.0 release docs and version

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.15.0</Version>`)
- Modify: `CHANGELOG.md` (new top section)
- Create: `docs/releases/v0.15.0.md`, in the shape of `docs/releases/v0.14.1.md`
- Modify: `docs/validation.md` (new top section)
- Modify: `README.md` (line numbers at `dd4fb74`; anchor on the quoted text):
  - :23 (feature list): "Processes: CPU, memory and I/O per app for every user" becomes "Processes: CPU, memory, I/O, GPU and GPU memory per app for every user"; the rest of the bullet stays.
  - :35 (feature list, "Usage history: …"): Application resources ranks the busiest apps' CPU, memory, I/O and GPU, and clicking a CPU, RAM or GPU point lists that moment's busiest apps.
  - :47: the download link's `v0.14.1` (both occurrences) becomes `v0.15.0`. This is the same release mechanics as 0.14.1.
  - The Processes paragraph (:105, "Enable **Per-process monitoring**…"): the default columns gain GPU and GPU memory, and the picker list gains GPU engine and Shared GPU memory. Add one sentence on GPU (busiest engine, as in Task Manager; dedicated memory; no administrator needed). Its last sentence saves "the busiest apps' CPU, memory, I/O and GPU".
  - :157 (Application resources): Rank by **GPU**, and the Avg GPU and Avg GPU memory while listed columns with their definitions.
  - :159 (click a moment): the Device **CPU**, **RAM** or **GPU** chart; a GPU point ranks the slot by GPU and needs a GPU selected on the dashboard; a hairline marks the slot on all three charts; a GPU slot without GPU data says "No GPU data in this span."
  - :163 (process-history.db): the top 10 by CPU time, by memory, by I/O and by GPU, at most 40 apps; the file is capped at 192 MiB; "a full day at the 40-app cap measured N MiB, so 30 such days fit", where N is the one-day size Task 10 noted in the PR description, in MiB to one decimal; GPU history begins when 0.15.0 first runs, and earlier minutes have no GPU data.
  - Privacy (:191-199): :193's saved values include GPU; adapter descriptions appear only in tooltips and are never saved; `--diagnose` gains a GPU summary with counts and timings only, which `--no-processes` also leaves out.

**Interfaces:**
- Consumes: Tasks 1–13 as merged on the branch. Re-read the touched code before writing; the docs describe the code, not this plan.

- [ ] **Step 1: Bump the version and write `CHANGELOG.md` under `## 0.15.0 — Per-app GPU`.** One bullet each:
  - the GPU and GPU memory columns (defaults, the picker's GPU engine and Shared GPU memory, the busiest-engine definition, the one-time column upgrade for saved 0.13/0.14 defaults);
  - the counter source (Windows' GPU performance counters, behind Per-process monitoring, no administrator rights);
  - the history (top 10 by GPU, Rank by GPU, the two averages, click a moment on the GPU chart, 192 MiB);
  - the downgrade note.

  The English counter paths are named as the design for non-English Windows, never as a tested result; the same holds in the release notes.
- [ ] **Step 2: Write `docs/releases/v0.15.0.md`.** The same points as user-facing bullets, then `## Verification`, which lists the automated coverage by area and the checks by eye as pending. It must contain the spec's downgrade note: returning to 0.14.x pauses Application resources history until 0.15 or later is reinstalled. That is, 0.14.x reads the version 2 file as "Application resource history unavailable (InvalidDataException)", or "(IOException)" for a file over 128 MiB, leaves it untouched, and 0.15 reads it normally again.
- [ ] **Step 3: Write the `docs/validation.md` section** `## Per-app GPU (0.15.0, YYYY-MM-DD)`, dated with the UTC date this step runs:
  - `### CI measurement (a floor)`: Task 2's run link and numbers (`counters`, p50/p95, first read, instance counts, skipped, adapter count and software flags, not-ready and unavailable read counts) with the spec's caveat that the runner has no real GPU workload and that placement is decided at run time.
  - `### Automated coverage`: Core by class (`GpuCounterTests`, `GpuProcessJoinTests`, the tracker, table, settings, folder and store tests, and the one-day size from Task 10); App on the Windows CI build only, by test and check name.
  - `### Pending by eye`, never claimed as verified: the spec's Manual list, every clause kept:
    - GPU % and GPU memory against Task Manager while a game or video plays.
    - The read cost: `DeadWare.exe --diagnose gpu.json --samples 60` while a game plays; `docs/validation.md` records `windows/gpucounters` p50 and p95 from the 60 samples, the instances read and the placement. A p95 above 15 ms with the read still on the collector thread is a failed check.
    - GPU engine naming on a laptop with integrated and discrete GPUs, and whether GPU numbers match Task Manager there and, where available, on a desktop whose monitor is on the discrete GPU while the integrated GPU is enabled, and on a PC with an NPU (whether NPU engines appear in GPU %).
    - A remote desktop session: record what the GPU, GPU engine and GPU memory columns show next to Task Manager in the same session; no outcome is claimed in advance.
    - DeadWare started without administrator rights (the Processes banner offers Restart as administrator), with Per-process monitoring on: GPU and GPU memory show values that agree with Task Manager, not "GPU counters unavailable". This is the only check of the no-administrator assumption.
    - History and the card after a day with GPU data.

    Then two items this plan adds, also pending:
    - Launch and close apps while watching the GPU column: note whether every row blanks to "first sample" for a poll when a new GPU instance appears (a whole-array benign status), and whether that ever runs on long enough to show "GPU counters unavailable (error …)". Task 2's timing run records the not-ready reads it saw on the idle runner.
    - A non-English Windows display language: record the GPU column state and the `--diagnose` GPU summary. The English counter paths are meant to work there; this has not been tested.
  - `### Results`: the build line, the Core suite on Linux (passed, skipped, the 26 baseline failures), and the id of the green Windows CI run on the PR head.
- [ ] **Step 4: Check the docs**

```bash
grep -c '<Version>0.15.0</Version>' Directory.Build.props                 # 1
grep -c '^## 0.15.0 — Per-app GPU$' CHANGELOG.md                          # 1
grep -c 'releases/tag/v0.15.0' README.md                                  # 1
grep -c '0.14.x' docs/releases/v0.15.0.md                                 # at least 1
grep -c '192 MiB' README.md docs/releases/v0.15.0.md                      # at least 1 each
grep -c 'must run before tagging' docs/releases/v0.15.0.md docs/validation.md   # 0 and 0
grep -cE 'at most 30 apps|30-app cap|128 MiB|CPU or RAM point|CPU, memory and I/O' README.md   # 0
grep -c 'No GPU data in this span.' README.md                              # 1
grep -c 'whether NPU engines appear in GPU %' docs/validation.md          # 1
```
Then run the build and the Core baseline. Expected: as in Global Constraints.

- [ ] **Step 5: Commit, push, verify, mark ready**

```bash
git add Directory.Build.props CHANGELOG.md docs/releases/v0.15.0.md docs/validation.md README.md
git commit -m "docs: 0.15.0 release notes, validation and version"
```
Run (CI). Expected: green. Mark the PR ready, confirm GitHub has the commit (GitHub tools) and report the PR link. Merging, running the Release workflow for `v0.15.0` and checking the published release wait for the user's go-ahead.
