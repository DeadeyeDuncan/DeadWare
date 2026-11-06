# Per-app GPU (0.15.0) — design

Status: approved in brainstorming on 2026-11-06. Builds on the 0.13.0 Processes page (`docs/superpowers/specs/2026-11-05-processes-design.md`) and the 0.14.0 per-app history. Where this document is silent, the Processes spec's rules apply unchanged (null, never zero, for an unknown value; every "—" has a tooltip naming its reason; status text holds fixed copy, counts and error codes, never names or paths; all native work off the UI thread; colours from theme resources only).

## Goal

Show how much each app uses the GPU and GPU memory, live on the Processes page and in the 30-day app history, the way Task Manager does.

Decisions made with the user:

- **Scope:** both GPU utilization and GPU memory, live and in history.
- **Several GPUs:** like Task Manager — one **GPU** column holding the app's busiest engine on any adapter, plus a **GPU engine** column naming which one ("GPU 1 · 3D").
- **Default columns:** **GPU** and **GPU memory** (dedicated) join the defaults; **GPU engine** and **Shared GPU memory** are in the column picker.
- **History:** a fourth "top 10 by GPU" list each minute; GPU-seconds and dedicated GPU memory stored for every listed app; the Application resources card ranks by GPU; "click a moment" works on the GPU chart.
- **Approach:** Windows' GPU performance counters (the same data behind Task Manager's GPU columns).

Assumptions confirmed by the user: per-app GPU runs behind the existing **Per-process monitoring** switch (still off by default) and needs no administrator rights; in history, "GPU memory" means dedicated memory and shared memory stays live-only; 0.15.0 ships after 0.14.1; the history store moves to a new format version and keeps existing minutes.

Success means: on the user's PC the GPU column agrees with Task Manager's GPU column within a few percentage points for a steady GPU load, GPU memory agrees with Task Manager's "Dedicated GPU memory", and the counter read adds little time per poll (measured in CI, below).

Out of scope: per-app GPU temperature or power, GPU columns on the overview cards, alerts on per-app GPU, D3DKMT or ETW sources, shared GPU memory in history.

## Data source

`WindowsGpuCounters` (App) owns one PDH query with wildcard counter paths, opened while Per-process monitoring is on and closed when it is turned off or on stop:

- `\GPU Engine(*)\Utilization Percentage`
- `\GPU Process Memory(*)\Dedicated Usage`
- `\GPU Process Memory(*)\Shared Usage`

Each poll calls `PdhCollectQueryData` once and reads every instance with `PdhGetFormattedCounterArray` (double for utilization, large integer for memory). Utilization is a cooked rate between two collections, so the first collection after the query opens yields no utilization ("first sample"); memory values are instantaneous.

Instance names are parsed by a pure Core parser (`GpuCounterInstance.TryParse`):

- Engine: `pid_<pid>_luid_0x<hi>_0x<lo>_phys_<n>_eng_<n>_engtype_<type>`.
- Process memory: `pid_<pid>_luid_0x<hi>_0x<lo>_phys_<n>`.

A name that does not match is skipped and counted (`Skipped`); parsing never throws. `<type>` is kept as text (for example `3D`, `Copy`, `VideoDecode`, `Compute`); an empty type reads "Engine".

Adapters are named from DXGI (`CreateDXGIFactory1`, `EnumAdapters1`): each adapter's LUID maps to its description and an index in DXGI enumeration order, shown as "GPU 0", "GPU 1", … like Task Manager. A LUID that DXGI does not list keeps its counter values and is named "GPU ?". The adapter list is read when the query opens and again when an unknown LUID appears (at most once a minute).

## Joining to processes

`GpuProcessJoin.Join(scan, counters, previousPollUtc)` (Core) runs after the process scan:

- Instances are grouped by process ID. For each process the join takes the busiest engine instance (highest utilization) as **GPU %** and its adapter and type as **GPU engine**; **GPU memory** and **Shared GPU memory** are summed over that process's memory instances on all adapters.
- Counter instance names carry no creation time, so a reused process ID cannot be told apart. A process whose `Generation` is newer than the previous poll's scan time gets null GPU values with gap `FirstSample` for that poll, the same rule CPU and I/O use. Memory follows the same rule.
- A running process with no engine instance reports 0 % and no engine (Windows lists only processes that have used the GPU); with no memory instance it reports 0 B. These are known zeros, not gaps.
- Values are clamped to 0–100 % per engine (counter rounding can briefly exceed 100).

`ProcessSample` gains `GpuPercent` (double?), `GpuEngine` (string?, "GPU 1 · 3D"), `GpuAdapter` (string?, the adapter description, for the tooltip only), `GpuDedicatedBytes` (long?), `GpuSharedBytes` (long?), `GpuGap` (ProcessGap). `ProcessActivity` gains a `Gpu` column state (`ProcessColumnState`) like `Disk` and `Network`. `ProcessOmitted` gains the omitted processes' GPU sums for history (GPU-seconds and dedicated byte-seconds). `ProcessDeltas` gains `GpuSeconds` (double?, utilization ÷ 100 × Δt of the process's busiest engine) and `GpuMemoryByteSeconds` (double, dedicated bytes × Δt).

## Placement and cost

The counter read runs in the collector's process block beside the scan, behind the same switch, with its own timing key `windows/gpucounters` (recorded only when it ran) for `--diagnose`. A failure is isolated to the GPU columns.

Placement is decided by a CI measurement, as in 0.13.0: `Gpu_counter_timing_over_240_reads` records p50/p95 and instance counts. If p95 > 15 ms the read moves to its own loop with the same cadence rules as `ProcessScanLoop` would have had (Processes spec, Cost); otherwise it runs every poll. `ProcessFeatures.GpuLoop` holds the decision. GitHub's runner has no real GPU and may expose few or no instances, so the measurement is a floor; the plan records it and the manual check confirms behaviour on a real GPU.

## Processes page

New column keys, appended after the network group in the fixed order: `gpu`, `gpuengine`, `gpumemory`, `gpushared`. Headers: `GPU · GPU ENGINE · GPU MEMORY · SHARED GPU MEMORY`.

- Defaults become `cpu, memory, io, disk, network, gpu, gpumemory`.
- Upgrading saved settings: DeadWare writes `ProcessColumns` whenever settings are saved, so most 0.13/0.14 users have the old default stored even though they never chose it. On read, a saved list that equals the 0.13 default set exactly (`cpu, memory, io, disk, network`, in any order) is treated as never customized and becomes the new default. Any other saved list — one the user changed with the picker — is kept exactly as saved, and the new GPU entries appear unchecked in the picker. (A user who deliberately re-chose exactly the old default is indistinguishable and gets GPU columns; they can untick them.)
- None of the GPU keys is administrator-only.
- **App rows:** for each engine (adapter + engine index), the members' utilization on that engine is summed; the app's **GPU %** is the highest such sum, clamped to 100 %, and **GPU engine** is that engine. **GPU memory** and **Shared GPU memory** are the sums of members' values. An app value is null only when every member's value is null (the existing rule).
- **Gaps:** first-match order — stale reading; `FirstSample` ("first sample"); GPU column unavailable ("GPU counters unavailable (error N)" or "GPU counters unavailable"). Known zeros show "0 %" / "0 B" with no tooltip.
- **GPU engine cell:** "GPU 1 · 3D"; tooltip names the adapter ("NVIDIA GeForce RTX 4070 · 3D engine"). Empty when GPU % is 0.
- Sorting, search, calm order, copy and the 4,096 cap behave as for the other columns. The footer gains one line: "GPU is the app's busiest GPU engine, as in Task Manager; GPU memory is dedicated video memory."

## History

`ProcessMinuteFolder` adds a fourth ranking: top 10 apps by GPU-seconds per minute (zero never qualifies; ties by key ordinal), joining the CPU, memory and I/O lists. `ProcessHistory.MaximumApplications` becomes 40 (four lists of 10) plus Everything else. Every listed app stores its GPU-seconds and dedicated GPU memory byte-seconds; Everything else carries the totals of everything not listed, including omitted processes.

`ProcessAppMinute` gains `GpuSeconds` (double?) and `GpuMemoryByteSeconds` (double?). They are null when the minute had no GPU data at all (GPU column unavailable for the whole minute); a minute with partial GPU coverage sums the known values and stores the GPU-covered seconds (see below).

`ProcessMinuteBatch` gains `GpuCoveredSeconds` (double): the summed Δt of scans in which the GPU column was available.

### Store format

`process-history.db` moves from `user_version` 1 to 2:

- `app_minutes` gains `gpu_seconds REAL NULL` and `gpu_memory_seconds REAL NULL`; `minutes` gains `gpu_covered REAL NULL`.
- On open, a version 1 file passes the existing read-only checks, then is upgraded in one transaction (`ALTER TABLE … ADD COLUMN` ×3, `PRAGMA user_version=2`). Existing rows keep null GPU values. The upgrade runs before the WAL size guard's first checkpoint and is covered by the integrity check that precedes it.
- A version 1 build (0.14.x) opening a version 2 file already reports "Unsupported process history; the file was preserved." and leaves it untouched (current `ProcessHistoryStore` behaviour); no change is needed for that path.
- The 128 MiB cap and 30-day retention are unchanged. Up to 41 rows per minute instead of 31 raise a typical day by roughly a quarter; the one-day size test is updated to the new worst case with a 15-character machine key and must stay under 5 MiB.

### Reads and the card

- `ProcessRank` gains `Gpu`; `ProcessResourceRow` gains `AverageGpuPercent` (double?) and `AverageGpuMemoryBytes` (double?). Averages divide by GPU-covered seconds only, so minutes without GPU data do not dilute them; when the range has no GPU-covered time the values are null.
- The Application resources card adds a **GPU** rank choice and **Avg GPU** and **Avg GPU memory** columns. Their header tooltips state how much of the period had GPU data ("GPU recorded for 12 h 4 min of this range").
- `ReadSeries` serves `gpu` (percent) and `gpumemory` (bytes) per app for charts, with the same bucketing as the other metrics.
- **Click a moment** works on the Usage history GPU chart: the card lists the busiest apps by GPU for that slot. When the slot has process data but no GPU data, or no process data at all, the card says which, instead of an empty table.

## Errors and limits

- PDH open or collect failure: the GPU column state is unavailable with "GPU counters unavailable (error 0x…)" (PDH status code); retried at most once a minute while monitoring is on. The process scan, joins and hardware readings continue.
- No GPU counters on the machine (common in remote sessions and some VMs): the same unavailable state with "GPU counters unavailable", no error code; not retried more than once a minute.
- DXGI failure: engines are named "GPU ?" and the tooltip omits the adapter; values are unaffected.
- Malformed instance names are skipped and counted; more than half of all instances skipped in one read marks the column unavailable for that poll ("GPU counters unreadable").
- Up to 65,536 instances per read are processed; beyond that the read is unavailable for that poll ("Too many GPU counter instances").

## Privacy

Live per-process GPU values stay in memory. History stores only app names (as 0.14.0) and numbers. Adapter descriptions are hardware names shown in tooltips; they are not stored in history. `--diagnose` gains a GPU summary with counts and timings only: instances read, instances skipped, adapters, read milliseconds, and the column state.

## Testing

- **Core (Linux and CI):** instance-name parsing (valid engine and memory names, odd LUID widths, unknown engine types, malformed names, empty input); the join (busiest engine, memory sums, known zeros, first-sample rule for a process created after the previous poll, clamping); app-row aggregation (per-engine sums across members, a different busiest engine than any single member's, memory sums, all-null members); column keys (order, defaults, the 0.13 default set upgraded to the new default in any order, any other saved list kept unchanged); the folder's GPU ranking and GPU-covered seconds; the store upgrade from a real version 1 file (rows preserved with null GPU), version 2 reads, ranking by GPU, averages over GPU-covered time only, and the size test.
- **App/UI checks (CI):** the page shows GPU and GPU memory by default for a fresh settings file and for a saved 0.13 default list, and not for a customized saved list; gap tooltips; engine tooltip; the card's GPU rank and columns; click a moment on the GPU chart including the "no GPU data" message.
- **Real Windows (CI, elevated runner):** `Gpu_counters_open_and_parse` opens the real query, collects twice, and asserts parsing never throws and every parsed instance has a process ID present in a scan or recently exited; skipped instances are recorded. `Gpu_counter_timing_over_240_reads` records cost (the placement decision). Both write to the `DeadWare-measurements` artifact and the job log.
- **Manual (user's PC, pending by eye, never claimed as verified):** GPU % and GPU memory against Task Manager while a game or video plays; GPU engine naming on a laptop with integrated and discrete GPUs; a remote desktop session showing "GPU counters unavailable"; history and the card after a day with GPU data.

## Release

0.15.0, one PR from main after 0.14.1 has merged. CHANGELOG, README (Processes columns, history card, privacy) and `docs/releases/v0.15.0.md` follow `docs/development.md`; `docs/validation.md` records the CI measurement, the decision, and the manual checks as pending.
