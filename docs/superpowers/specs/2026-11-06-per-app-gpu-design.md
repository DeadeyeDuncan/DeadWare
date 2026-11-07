# Per-app GPU (0.15.0) — design

Status: approved in brainstorming on 2026-11-06 and revised after an adversarial review the same day; the 192 MiB history cap is a user decision from that review. Builds on the 0.13.0 Processes page (`docs/superpowers/specs/2026-11-05-processes-design.md`) and the 0.14.0 per-app history. Where this document is silent, the Processes spec's rules apply unchanged (null, never zero, for an unknown value; every "—" has a tooltip naming its reason; status text holds fixed copy, counts and error codes, never names or paths; all native work off the UI thread; colours from theme resources only).

## Goal

Show how much each app uses the GPU and GPU memory, live on the Processes page and in the 30-day app history, the way Task Manager does.

Decisions made with the user:

- **Scope:** both GPU utilization and GPU memory, live and in history.
- **Several GPUs:** like Task Manager — one **GPU** column holding the app's busiest engine on any adapter, plus a **GPU engine** column naming which one ("GPU 1 · 3D").
- **Default columns:** **GPU** and **GPU memory** (dedicated) join the defaults; **GPU engine** and **Shared GPU memory** are in the column picker.
- **History:** a fourth "top 10 by GPU" list each minute; GPU-seconds and dedicated GPU memory stored for every listed app; the Application resources card ranks by GPU; "click a moment" works on the GPU chart.
- **Approach:** Windows' GPU performance counters, read through PDH (the same data behind Task Manager's GPU columns).
- **Storage:** the `process-history.db` cap rises from 128 MiB to 192 MiB so that 30 days still fit (Store format).

Assumptions confirmed by the user: per-app GPU runs behind the existing **Per-process monitoring** switch (still off by default) and needs no administrator rights; in history, "GPU memory" means dedicated memory and shared memory stays live-only; 0.15.0 ships after 0.14.1; the history store moves to a new format version and keeps existing minutes.

Success means: on the user's PC the GPU column agrees with Task Manager's GPU column within a few percentage points for a steady GPU load, GPU memory agrees with Task Manager's "Dedicated GPU memory", and a slow counter read does not stay on the collector thread (a runtime guard moves it, Placement and cost). CI records a floor for the read cost; the cost on a real GPU is a manual check.

Out of scope: per-app GPU temperature or power, GPU columns on the overview cards, alerts on per-app GPU, D3DKMT or ETW sources, shared GPU memory in history, a per-app GPU memory chart.

## Data source

`WindowsGpuCounters` (App) owns one PDH query, opened while Per-process monitoring is on and closed when it is turned off or on stop. It adds three wildcard counters with `PdhAddEnglishCounterW`; the paths are English names, and `PdhAddCounterW` expects localized names, so it would fail on non-English Windows.

- `\GPU Engine(*)\Utilization Percentage`
- `\GPU Process Memory(*)\Dedicated Usage`
- `\GPU Process Memory(*)\Shared Usage`

Each read calls `PdhCollectQueryData` once and reads each counter with `PdhGetFormattedCounterArrayW` (`PDH_FMT_DOUBLE` for utilization, `PDH_FMT_LARGE` for memory); `PDH_MORE_DATA` is the buffer-sizing step, not an error. Utilization is a rate between two collections of the same instance name; memory values are instantaneous.

**Statuses** (classified in Core from the raw PDH results, so these rules are tested on Linux):

- An item is *ready* when its CStatus is `PDH_CSTATUS_VALID_DATA` or `PDH_CSTATUS_NEW_DATA`. Any other item status is *not ready*, for example a utilization instance that first appeared in this collection and so has no earlier raw value. A not-ready item is never counted in `Skipped` and never read as 0 (Joining says what its process shows).
- `PDH_CSTATUS_INVALID_DATA` (0xC0000BBA, which `windows-latest` returns for utilization on the first collection after the query opens), `PDH_INVALID_DATA`, `PDH_CALC_NEGATIVE_DENOMINATOR` or `PDH_CALC_NEGATIVE_VALUE` from the array call makes every item of that counter not ready for that read; the first collection after the query opens reports utilization this way or with not-ready items. If such reads go on for 60 s, it becomes a failure (Errors and limits).
- No instances: `PDH_NO_DATA` or `PDH_CSTATUS_NO_INSTANCE` from the collect or array call, or zero items. For the engine counter the column is unavailable for that read ("GPU counters unavailable"), never 0 % for every process; for a memory counter it means no memory instances. The query stays open.
- Any other non-success status is a failure (Errors and limits).

**Instance names** are parsed by a pure Core parser (`GpuCounterInstance.TryParse`):

- Engine: `pid_<pid>_luid_0x<hi>_0x<lo>_phys_<p>_eng_<e>_engtype_<type>`.
- Process memory: `pid_<pid>_luid_0x<hi>_0x<lo>_phys_<p>`.
- `<hi>` and `<lo>` are 1–8 hex digits; `<pid>`, `<p>` and `<e>` are decimal. `<type>` is everything after the first `_engtype_` to the end of the name, kept verbatim, and may contain `_`, digits and spaces (`3D`, `Copy`, `VideoDecode`, `Compute_0`, `Graphics_1`, `High Priority 3D`). An empty type reads "Engine".
- A name that does not match is skipped and counted (`Skipped`); parsing never throws.
- An engine is identified by (LUID, phys, eng), with LUID = (hi << 32) | lo; `phys` tells linked physical adapters under one LUID apart. **Engine order**, used for every tie: numbered adapters by number, then "GPU ?" adapters by LUID, then phys, then eng.

**Adapters** are named from DXGI (`CreateDXGIFactory1`, `EnumAdapters1`, a new factory on each re-read). Adapters without `DXGI_ADAPTER_FLAG_SOFTWARE` are numbered "GPU 0", "GPU 1", … in enumeration order. That order follows the primary display and DeadWare's own graphics preference, so it usually, but not always, matches Task Manager's numbers, and a re-read can renumber adapters; the engine tooltip, which names the adapter, is the reliable identity. A LUID that is not a numbered hardware adapter (one DXGI does not list, such as a compute-only NPU, or one flagged software, such as the Microsoft Basic Render Driver) keeps its counter values and is named "GPU ?". The adapter list is read when the query opens and again when an unknown LUID appears (at most once a minute).

## Joining to processes

`GpuCounterRead` (Core) is one read's result: the parsed engine and memory items with their ready flags, the adapter names by LUID, the `Skipped` count, the column state, the collection's UTC time, the UTC time of the previous successful collection on the same query, the interval between the two on the collector's monotonic clock, and `Credit` (false when a poll joins a read again for display only, Placement and cost). The previous collection is null when there is none (the first read after the query opens, including after monitoring is turned on or a failure closed the query) or when the interval is not positive or exceeds `PollingPolicy.FreshnessSeconds` for the polling interval, the tracker's Δt rule.

`GpuProcessJoin.Join(IReadOnlyList<ProcessSample> samples, GpuCounterRead? read)` (Core) is called by `ProcessActivityTracker.Update`, which gains an optional `GpuCounterRead? gpu`, on every sample before the 4,096 cut, so omitted processes carry GPU values into their sums. A null read, or one whose column state is unavailable, leaves every GPU value null. Instances are grouped by process ID:

- **GPU %** is the process's busiest ready engine instance and **GPU engine** is that engine's adapter and type; ties go to the first engine in engine order. Values are clamped to 0–100 % per engine (counter rounding can briefly exceed 100).
- **GPU memory** and **Shared GPU memory** are summed over the process's ready memory instances on all adapters.
- **Not ready:** a process whose engine instances are all not ready gets null GPU % and GPU engine with gap `FirstSample`; a process with at least one ready engine uses its busiest ready one. A not-ready memory item makes that process's GPU memory (or Shared GPU memory) null with `FirstSample`. Other values are unaffected.
- **First sample (a GPU-specific rule):** PDH keeps each instance's previous raw value by name, and the name carries only the process ID, not a creation time, so a process that reuses an ID would have its first rate computed against its predecessor's value. So a process whose `Generation` is at or after (the read's previous collection − 1 s, the tracker's tolerance) gets null GPU % and GPU engine with `FirstSample` for that read. With no previous collection, GPU % and GPU engine are null with `FirstSample` for every process. This rule comes before the known-zero rule below. It is the opposite of CPU and I/O, which credit a process created after the previous scan in full because their counters start at its creation. Memory is instantaneous and belongs to whoever holds the ID at collection, so it follows only its item status.
- **Known zeros:** a process with no engine instance, or whose ready engines are all at 0 %, reports 0 % and an empty GPU engine (Windows lists only processes that have used the GPU); with no memory instance it reports 0 B. These are known zeros, not gaps.

Model additions:

- `ProcessSample` gains `GpuPercent` (double?), `GpuEngine` (string?, "GPU 1 · 3D"), `GpuAdapter` (string?, the adapter description for the tooltip only; null when DXGI did not name it), `GpuEngines` (IReadOnlyList<GpuEngineUse>?), `GpuDedicatedBytes` (long?), `GpuSharedBytes` (long?) and `GpuGap` (ProcessGap).
- `GpuEngineUse(long Luid, int Phys, int Engine, string Type, string Label, string? Adapter, double Percent)`: one entry per ready engine instance above 0 %, at most the 32 busiest per process, in descending Percent with ties in engine order. `Label` is the GPU engine cell text and `Percent` is clamped. The cap bounds the published data; a dropped engine is never the process's busiest and can only understate an app's per-engine sum by its own share. `GpuEngines` is null exactly when `GpuPercent` is null and empty for a known 0 %; `GpuPercent`, `GpuEngine` and `GpuAdapter` come from its first entry.
- `TrackedScan` and `ProcessActivity` gain `Gpu` (`ProcessColumnState`, like `Disk` and `Network`) and `GpuIntervalSeconds` (double?): the joined read's interval, null when no read was joined, the column is unavailable, the read has no previous collection or its `Credit` is false.
- `ProcessDeltas` gains `GpuSeconds` (double?, GPU % ÷ 100 × `GpuIntervalSeconds`) and `GpuMemoryByteSeconds` (double?, dedicated bytes × `GpuIntervalSeconds`). Each is null when its sample value or `GpuIntervalSeconds` is null. `ProcessDeltas.Add` sums them with the `CpuTime` rule (null only when both are null), `ProcessDeltas.Zero` has 0 for both, and the tracker's omitted sum starts both at null when Δt is known. `ProcessOmitted` gains no members: its `Deltas` carry the omitted processes' GPU sums.

## Placement and cost

- The read runs in the collector's process block immediately before the scan, behind the same switch, with its own timing key `windows/gpucounters` for `--diagnose`, recorded for a poll only when a read ran (or, in the loop, completed) since the previous poll. A failure is isolated to the GPU columns; the scan, the other joins and the hardware readings continue.
- GitHub's runner has no real GPU, so CI cannot measure the case that matters: a desktop whose GPU Engine set holds one instance per GPU-using process, engine and adapter, often thousands. Placement is therefore decided at run time. A guard keeps the times of the last 20 reads, not counting the first after the query opens; when more than one of them exceeds 15 ms (their p95 is above 15 ms, the Processes spec's threshold), the read moves to its own loop until Per-process monitoring is turned off or DeadWare restarts. No `ProcessFeatures` switch is added.
- The loop follows `StorageSensorLoop`'s style with a period of max(1 s, the polling interval), so it never reads more often than every-poll reading would. It times reads on the collector's monotonic clock, reads only while monitoring is on, and is joined within the 5 s hardware stop budget.
- In the loop, each poll joins the latest completed read it has not used. Join uses that read's own previous collection and interval, so the first-sample rule and GPU time mean the same in both placements. A poll that finds no new read joins the latest read again with `Credit` false, for display only, so it adds nothing to history; once that read is older than two loop periods the GPU values are a stale reading. A read that a poll passes over because a newer one completed adds neither GPU-seconds nor GPU-covered time, so averages stay unbiased.
- `Gpu_counter_timing_over_240_reads` records p50/p95 and instance counts in CI as a floor; it decides nothing.

## Processes page

New column keys, appended after `upload` in the fixed order: `pid, cpu, memory, io, ioread, iowrite, disk, diskread, diskwrite, network, download, upload, gpu, gpuengine, gpumemory, gpushared`. Headers: `GPU · GPU ENGINE · GPU MEMORY · SHARED GPU MEMORY`. None of the GPU keys is administrator-only.

- **Defaults** become `cpu, memory, io, disk, network, gpu, gpumemory`. The column picker lists CPU, MEMORY, I/O, DISK, NETWORK, GPU and GPU MEMORY (checked by default), then PID, I/O READ, I/O WRITE, DISK READ, DISK WRITE, ↓ DOWNLOAD, ↑ UPLOAD, GPU ENGINE and SHARED GPU MEMORY (unchecked by default).
- **Upgrading saved settings:** DeadWare writes `ProcessColumns` on every launch save, so most 0.13/0.14 users have the old default stored without ever choosing it. `UserSettings` gains `ProcessColumnsVersion` (int, initializer 2, so settings built when the file is missing or unreadable are already current). `SettingsCodec.Read` alone, never `ProcessColumnKeys.Normalize` or `Toggle`, applies the upgrade, once. A file without the property (found with `document.RootElement.TryGetProperty`, as for `WindowPlacementFormat`) or with a value below 2 is upgraded: if its `ProcessColumns`, after `Normalize`, equals `cpu, memory, io, disk, network` as a set, the list becomes the new default. Any other list is kept exactly as saved, and the GPU entries appear unchecked in the picker. Either way Read sets the version to 2 and the launch save writes it, so later reads never upgrade and a user who unticks GPU and GPU memory keeps the 0.13 set. An older build drops the property and the GPU keys on its next save, so returning to 0.15 runs the upgrade once more.
- **App rows:** members' `GpuEngines` entries are grouped by engine (LUID, phys, eng) and their Percent values summed. The app's **GPU %** is the highest sum, clamped to 100 % (ties in engine order), and **GPU engine** and its tooltip are that engine's Label and Adapter. Members with null `GpuEngines` add nothing; when at least one member's list is non-null but no member has an entry, the app shows 0 % with an empty engine. **GPU memory** and **Shared GPU memory** are the sums of members' values. An app value is null only when every member's value is null (the existing rule).
- **Gaps:** first-match order — stale reading; `FirstSample` ("first sample"); GPU column unavailable (the column status, one of the four texts under Errors and limits). A null GPU engine shows "—" with the GPU cell's reason. Known zeros show "0 %" / "0 B" with no tooltip and an empty GPU engine.
- **GPU engine cell:** "GPU 1 · 3D". The tooltip joins the adapter description and "<type> engine" with " · " ("NVIDIA GeForce RTX 4070 · 3D engine"); without a description it is "3D engine". An empty type shows "Engine" in the cell and "engine" in the tooltip.
- **Sorting:** GPU, GPU MEMORY and SHARED GPU MEMORY sort numerically, descending on the first header click, like the other numeric columns. GPU ENGINE is a text column, not a numeric sort: it sorts by its shown text under `StringComparer.OrdinalIgnoreCase`, ascending on the first click like NAME. In both directions an empty engine sorts after every named engine and "—" after that; ties sort by name, then PID.
- Search, calm order and the 4,096 cap behave as for the other columns. Spoken labels: `gpu` "GPU", `gpuengine` "GPU engine", `gpumemory` "GPU memory", `gpushared` "shared GPU memory". An empty GPU engine cell is left out of the row's spoken summary; "—" is spoken as "GPU engine unknown (reason)" like the other columns.
- The footer gains one line: "GPU is the app's busiest GPU engine, as in Task Manager; GPU memory is dedicated video memory."

## History

`ProcessMinuteFolder` adds a fourth ranking: top 10 apps by GPU-seconds per minute (zero never qualifies; ties by key ordinal), joining the CPU, memory and I/O lists. `ProcessHistory.MaximumApplications` becomes 40 (four lists of 10) plus Everything else.

- **One GPU definition, live and history:** for each folded activity (the folder folds only activities with a scan Δt) that has a `GpuIntervalSeconds` g, the folder computes each app's GPU % by the App rows rule and adds GPU % ÷ 100 × g to that app's GPU-seconds; it never sums per-process `GpuSeconds` within an app. An app whose members are all null adds nothing for that activity. Its GPU memory byte-seconds add Σ members' known `GpuDedicatedBytes` × g. A listed app's history therefore matches its live GPU column and never exceeds its GPU-covered seconds.
- **Everything else** at minute close is the sum of every unlisted app's GPU-seconds and GPU memory byte-seconds, plus the per-process `Deltas.GpuSeconds` and `GpuMemoryByteSeconds` of processes with the unknown name (which go straight into Everything else) and of omitted processes (`activity.Omitted.Deltas`, null as 0). Because it is a sum across apps, its GPU can exceed 100 %.
- `ProcessMinuteBatch` gains `GpuCoveredSeconds` (double): Σ g over the minute's folded activities. `ProcessAppMinute` gains `GpuSeconds` and `GpuMemoryByteSeconds` (double?): null on every row when the minute's `GpuCoveredSeconds` is 0, otherwise the sum of known values (0 when none was known).
- `ValidateBatch` also requires `GpuCoveredSeconds` finite and ≥ 0, every GPU value null or finite and ≥ 0, and every GPU value null when `GpuCoveredSeconds` is 0.

### Store format

`process-history.db` moves from `user_version` 1 to 2:

- `app_minutes` gains `gpu_seconds REAL NULL` and `gpu_memory_seconds REAL NULL`; `minutes` gains `gpu_covered REAL NULL`. A minute without GPU-covered time stores NULL in all three, as version 1 rows have.
- `Inspect` (read-only, integrity check first) accepts `user_version` 1 or 2; any other value still reports "Unsupported process history; the file was preserved." A version 2 file must also have the three columns (checked with `pragma_table_info`), otherwise "Incompatible process history objects; the file was preserved."
- A new file is created directly at version 2: `Schema` includes the three columns and the creation transaction sets `PRAGMA user_version=2`.
- A version 1 file is opened in the existing order: `Inspect`, then `Open` (whose 8 MiB journal guard may checkpoint, or throw, in which case `Initialize` fails as in 0.14.0 and the file stays at version 1), then `journal_mode=WAL`. One transaction on that writable connection then re-reads `user_version` and, if it is still 1, runs `ALTER TABLE app_minutes ADD COLUMN gpu_seconds REAL NULL`, `ALTER TABLE app_minutes ADD COLUMN gpu_memory_seconds REAL NULL`, `ALTER TABLE minutes ADD COLUMN gpu_covered REAL NULL` and `PRAGMA user_version=2`, and commits, all before `Initialize`'s final checkpoint. A failure rolls back and leaves a valid version 1 file. Existing rows keep null GPU values.
- **Returning to 0.14.x:** its `Inspect` (a read-only open, before any write) throws InvalidDataException "Unsupported process history; the file was preserved.", or, for a file over 128 MiB, `Open`'s size check first throws IOException "Process history exceeds its size limit; the file was preserved." `ProcessHistoryService.Fail` shows only the exception type, so the Application resources card reads "Application resource history unavailable (InvalidDataException). Saved data was not deleted. Live monitoring continues; resolve storage access and restart to retry." (or "(IOException)"), shows no saved application history and records none, on every 0.14.x run while the version 2 file exists. The file is left untouched and 0.15 reads it normally again. 0.15.0 cannot change this path; `docs/releases/v0.15.0.md` says that returning to 0.14.x pauses Application resources history until 0.15 or later is reinstalled.
- **Size:** retention stays 30 days; the cap rises from 128 MiB to 192 MiB (`ProcessHistoryStore.MaximumDatabaseBytes` and its `Cap_and_retention_constants` assertion; this replaces the Processes spec's 128 MiB in its Limits and Caps). Up to 41 rows per minute instead of 31, each with two more 8-byte REAL columns, make the worst-case day about two thirds larger: measured with the store's schema and pragmas, about 3.4 MiB → 5.6 MiB with a 15-character machine key (`Environment.MachineName` is at most 15 characters). The one-day size test writes 1,440 complete minutes, each with the same 40 app names plus Everything else and full-width values in every column (both GPU columns and `gpu_covered` included), under the machine key "DESKTOP-ABCDEFG", and asserts the files stay under 6 MiB, so 30 days plus today (31 × 6 MiB = 186 MiB) fit the 192 MiB cap.

### Reads and the card

- `ProcessRank` gains `Gpu` only. `ProcessResourceRow` gains `GpuSeconds` (double?: Σ gpu_seconds over the app's listed minutes, null when all are null), `AverageGpuPercent` and `AverageGpuMemoryBytes` (double?); `PartOf(Gpu)` returns `GpuSeconds ?? 0`. `ProcessResourceView` gains `GpuCoveredSeconds` (double: Σ gpu_covered over the period's recorded minutes, null as 0).
- **Definitions**, for every row including Everything else:
  - Avg GPU = 100 × `GpuSeconds` ÷ the view's `GpuCoveredSeconds`, a share of all the period's GPU-recorded time like Avg CPU. Version 1 rows and minutes without GPU data are in neither sum, so they never dilute it.
  - Avg GPU memory while listed = Σ gpu_memory_seconds ÷ Σ gpu_covered over only the minutes the app was listed with non-null GPU values, like Avg memory while listed.
  - Either is null when its numerator is null or its divisor is 0.
  - Rank by GPU sorts by Σ gpu_seconds descending, then display name. RankTotal for the share bar is Σ gpu_seconds over the period, Everything else included.
- The card adds a **GPU** Rank by choice after I/O, and **Avg GPU** and **Avg GPU memory while listed** columns after Avg memory while listed. Their header tooltips give the view's `GpuCoveredSeconds` in the card's duration format: "GPU recorded for 12 h 4 min of this range".
- A null value shows "—" with the tooltip "No GPU data in this range" when the view's `GpuCoveredSeconds` is 0, and "No GPU data in the minutes this app was listed" otherwise. Everything else's Avg GPU is shown unclamped with the tooltip "Sum over many apps; can exceed 100 %".
- `ReadSeries` with `ProcessRank.Gpu` returns the app's GPU %, using only the minutes in which the app was listed with non-null gpu_seconds and gpu_covered > 0. Each minute's value is 100 × a.gpu_seconds ÷ m.gpu_covered; a bucket's point is 100 × Σ gpu_seconds ÷ Σ gpu_covered over those minutes, its band spans their lowest and highest values, and its `Minutes` counts only them. Every other minute is a gap, never a zero, so a bucket missing any of its minutes is partial. Selecting an app while ranked by GPU charts this series, titled "GPU % of the busiest engine", in the `Graph/gpu` colour on a fixed 0–100 scale (an automatic maximum for Everything else). There is no GPU memory series; GPU memory history shows in the Avg GPU memory while listed column.

### Click a moment

- Works on the Usage history GPU chart, on the CPU chart's slot grid (the GPU chart is read with the CPU chart's points). Clicking it ranks the slot by GPU, using each app's stored GPU-seconds (its busiest engine on any adapter) even though the chart plots only the dashboard's selected GPU; with no GPU selected the chart has no points to click. The list includes Everything else, keeps the 0.14.0 heading ("Busiest apps · 03:13–03:16"), and computes Avg GPU over the slot's GPU-covered seconds.
- The CPU, RAM and GPU charts are all focusable (Left and Right move the selected bucket, Enter lists it). The selection draws as a hairline on all three, and Esc or **Clear** removes it from all three.
- A slot with no recorded minutes shows "No process data in this span." (unchanged from 0.14.0). After a GPU chart click, a slot with recorded minutes but no GPU-covered time (including minutes saved before the upgrade) shows "No GPU data in this span." instead of a table. Clear stays available in both.

## Copy

- `ProcessCopy.ScanSentence` (the Processes off state and the Settings → Per-process monitoring help line) names the GPU read: "Turning it on adds one Windows process scan per poll" + (scanLoop ? " (at most one a second)" : "") + (elevated && diskColumns ? ", one GPU counter read per poll and a disk trace" : " and one GPU counter read per poll") + ".", for example "Turning it on adds one Windows process scan per poll and one GPU counter read per poll." The GPU part stays the same when the guard moves the read, because the loop never reads more often than once a poll. The 0.14.0 history sentence follows unchanged, and ShellUiChecks still finds the help line by the prefix "Turning it on adds one Windows process scan".
- Settings → History and alerts: "…it also keeps the busiest apps' CPU, memory and I/O for 30 days." becomes "While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days."
- Usage history → Keep a longer record: the same sentence ends "for 30 days (up to 192 MiB)."
- Application resources card note: "Each minute lists up to 30 of the busiest applications by CPU, memory and I/O; Everything else holds the rest." becomes "Each minute lists up to 40 of the busiest applications by CPU, memory, I/O and GPU; Everything else holds the rest." It gains "Avg GPU is its busiest GPU engine's share of the time GPU data was recorded; Avg GPU memory while listed averages its dedicated GPU memory over the minutes it was listed with GPU data."
- ShellUiChecks' `EndsWith` assertion, `UsageHistoryUiChecks.ProcessHistorySentence` and its "for 30 days (up to 192 MiB)." check, and the exact `ScanSentence` cases in ProcessSettingsTests change to these texts.

## Errors and limits

The GPU column status is one of four texts, used by the "—" tooltips and `--diagnose`:

- "GPU counters unavailable (error 0x{status:X8})", the PDH status as eight upper-case hex digits: `PdhOpenQueryW`, `PdhAddEnglishCounterW`, `PdhCollectQueryData` or `PdhGetFormattedCounterArrayW` failed with a status not covered under Data source, or not-ready array statuses went on for 60 s. The query is closed and reopened at most once a minute while monitoring is on; the reopened query's first read is a first sample.
- "GPU counters unavailable": the machine has no GPU counter set (`PdhAddEnglishCounterW` returns `PDH_CSTATUS_NO_OBJECT` or `PDH_CSTATUS_NO_COUNTER` for any of the three paths: Windows 10 before version 1709, or performance counters disabled or damaged), reopened at most once a minute; or a read found no GPU Engine instances, with the query kept open. The counter sets are machine-wide, so remote desktop sessions and VMs normally have them.
- "GPU counters unreadable": more than half of the read's instances, counted across the three arrays, were skipped; for that read.
- "Too many GPU counter instances": a counter's array held more than 65,536 items; for that read.

A DXGI failure names engines "GPU ?" and leaves the adapter out of tooltips; values are unaffected and DXGI is re-read at most once a minute. In every case the process scan, the other joins and the hardware readings continue.

## Privacy

Live per-process GPU values stay in memory. History stores only app names (as 0.14.0) and numbers. Adapter descriptions are hardware names shown in tooltips; they are not stored in history. `--diagnose` gains a GPU summary with counts and timings only: instances read, instances skipped, adapters, read milliseconds, the column state and the placement (every poll or own loop).

## Testing

**Core (Linux and CI):**

- Instance-name parsing: valid engine and memory names; LUID halves of 1–8 hex digits; engine types with `_`, digits and spaces (`Compute_0`, `High Priority 3D`); an empty type; malformed names; empty input.
- Read classification from a fake PDH result: ready and not-ready items (neither `Skipped` nor 0); a benign array status gives not ready for that read and "GPU counters unavailable (error 0x{status:X8})" only after 60 s; no engine instances gives "GPU counters unavailable"; more than half skipped gives "GPU counters unreadable"; 65,537 items give "Too many GPU counter instances"; an open or collect failure is not retried within a minute (fake clock).
- Adapter naming: "GPU ?" for a LUID DXGI does not list, for a software adapter and on a DXGI failure, with values kept and DXGI re-read at most once a minute.
- The join: busiest ready engine with ties in engine order; one ready and one not-ready engine; all not ready giving `FirstSample`; memory sums; known zeros; clamping; the 32-engine cap; the first-sample rule for a process created at or after the previous collection − 1 s, taking precedence over a known zero; a read with no previous collection (GPU % null for every process, memory published); an interval beyond the freshness limit; omitted processes' GPU sums in `Omitted.Deltas`.
- App-row aggregation: per-engine sums across members; an app busiest engine that is no single member's busiest; two phys values under one LUID kept apart; members with only empty lists giving 0 %; tie-breaking; memory sums; all-null members.
- Columns and settings: key order, defaults and picker order; a version-less file holding the 0.13 set in any order reads as the new default with version 2; any other version-less list is kept; the new default with gpu and gpumemory removed survives a Write/Read round trip at version 2, and so does a fresh `new UserSettings()` with them removed; `Normalize` and `Toggle` never upgrade.
- Folder: the GPU ranking; app-level GPU-seconds (two members busy on different engines record the higher sum, not both); Everything else including unknown-name and omitted processes; GPU-covered seconds; null GPU fields in a minute without GPU data.
- Store: the upgrade from a real version 1 file (rows preserved with null GPU, `user_version` 2); a failed upgrade leaving version 1; a new file at version 2; a version 2 file missing a column and a version 3 file rejected; version 2 reads; ranking by GPU and RankTotal; averages over GPU-covered time only; ReadSeries GPU with null-GPU minutes as gaps; `Cap_and_retention_constants` at 192 MiB; the size test.
- The `ScanSentence` cases.

**App/UI checks (CI):**

- The page shows GPU and GPU memory by default for a fresh settings file and for a version-less saved 0.13 default list, and not for a customized saved list.
- Gap tooltips with each of the four status texts; the engine tooltip; GPU ENGINE sorting; spoken labels.
- Collector tests with a fake and a throwing GPU source: CPU, memory and process columns survive a GPU failure; turning Per-process monitoring off closes the query within one poll; a fake source slower than 15 ms moves the read to its own loop, where a poll with no new read adds nothing to history.
- The card's GPU rank, columns, header and "—" tooltips; the copy texts above.
- Click a moment on the GPU chart: ranked by GPU, hairline on all three charts, keyboard, Clear, "No GPU data in this span." and "No process data in this span.".
- `--diagnose` JSON serialized from a reading with named adapters and processes contains the GPU summary and none of those names or adapter descriptions.

**Real Windows (CI, elevated runner; says nothing about standard users, see Manual):**

- `Gpu_counters_open_and_parse` takes a process scan, opens the real query, collects, collects again 1 s later and takes a second scan. It asserts that parsing never throws and that every parsed instance's process ID appears in one of the two scans; a process ID in neither fails the test. It records skipped instances and, for each LUID seen, whether DXGI lists it, its DXGI flags and its engine types.
- `Gpu_counter_timing_over_240_reads` records p50/p95 and instance counts: a floor that decides nothing.
- If the counter set is absent ("GPU counters unavailable" with no code) or a collection has no instances, both tests record `counters: absent` or `instances: 0` and pass. Any other PDH failure fails `Gpu_counters_open_and_parse`; the timing test records its code and passes.
- Both write through `MeasurementRecorder` to the `DeadWare-measurements` artifact and append their values to the job summary (`GITHUB_STEP_SUMMARY`), as the 0.13.0 measurements do.

**Manual (user's PC, pending by eye, never claimed as verified):**

- GPU % and GPU memory against Task Manager while a game or video plays.
- The read cost: `DeadWare.exe --diagnose gpu.json --samples 60` while a game plays; `docs/validation.md` records `windows/gpucounters` p50 and p95 from the 60 samples, the instances read and the placement. A p95 above 15 ms with the read still on the collector thread is a failed check.
- GPU engine naming on a laptop with integrated and discrete GPUs, and whether GPU numbers match Task Manager there and, where available, on a desktop whose monitor is on the discrete GPU while the integrated GPU is enabled, and on a PC with an NPU (whether NPU engines appear in GPU %).
- A remote desktop session: record what the GPU, GPU engine and GPU memory columns show next to Task Manager in the same session; no outcome is claimed in advance.
- DeadWare started without administrator rights (the Processes banner offers Restart as administrator), with Per-process monitoring on: GPU and GPU memory show values that agree with Task Manager, not "GPU counters unavailable". This is the only check of the no-administrator assumption.
- History and the card after a day with GPU data.

## Release

0.15.0, one PR from main after 0.14.1 has merged. CHANGELOG, README (Processes columns, the history card, the 192 MiB history cap, privacy, and the `--diagnose` GPU summary, which `--no-processes` also leaves out) and `docs/releases/v0.15.0.md` (including the 0.14.x downgrade note) follow `docs/development.md`; `docs/validation.md` records the CI measurement as a floor and the manual checks, including the read cost on the user's PC, as pending.
