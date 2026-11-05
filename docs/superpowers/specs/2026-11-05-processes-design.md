# Processes (0.13.0 live view, 0.14.0 history)

Status: proposed on 2026-11-05. Purpose, history depth, placement, grouping, the disk column, actions, the approach and all five design sections were approved by the user in the same session, including the changes listed under the decisions below. This is the third of the three planned releases: per-core CPU shipped as 0.10.0, Storage as 0.11.0, and now Processes. The Storage spec and the 0.11.0 notes called this release "0.12"; that number went to the 0.12.x fixes, so Processes ships as 0.13.0 (live view and actions) and 0.14.0 (saved history). The 0.13.0 notes say so, answering CHANGELOG.md's "planned for 0.12".

## Intent

DeadWare shows how busy the machine is, but not *who* is making it busy. Per-application data exists today only for network traffic, and only as administrator: the Network page table, Usage history's Applications view and Connections. When the PC feels slow, the user has to open Task Manager to find the process responsible, and nothing records which process spiked CPU, memory or disk overnight.

This work adds a live Processes page: CPU, memory, I/O, disk and network per app, with End task and Open file location. A later release adds 30 days of saved per-app history on Usage history, so "what was busy at 3 am?" has an answer.

Minor-version milestones:
- 0.13.0 adds a new sidebar page, a new saved setting and a new collector data source.
- 0.14.0 adds a new local database and a new Usage history card.

## Decisions taken in brainstorming

- **Purpose:** a live view first, plus saved per-process history.
- **History:**
  - Saved to disk for about 30 days.
  - Each minute keeps the union of the top 10 apps by CPU, the top 10 by memory and the top 10 by I/O (typically 12–18, at most 30), plus an Everything else total.
- **Placement:** a new **Processes** sidebar page holds the live table. Saved history appears on the existing **Usage history** page as a new card under Applications.
- **Rows:** one row per app (executable name, summed). Each row expands to its individual processes. History is kept per app name.
- **Columns:**
  - The default view is Name, CPU, Memory, I/O (read + write), Disk (read + write, administrator) and Network (download + upload, administrator).
  - A remembered column picker adds PID, I/O read, I/O write, Disk read, Disk write, Download and Upload separately.
  - When DeadWare is not running as administrator, the Disk and Network columns are **hidden**. A banner offers Restart as administrator, and the picker shows the hidden columns disabled, marked "(administrator)".
- **I/O and Disk:**
  - **I/O** comes from Windows' per-process I/O counters, for every user. It is always labelled I/O, never Disk.
  - True physical **Disk** comes from a kernel trace, only as administrator.
- **Actions:** End task, with a default-No confirmation, and Open file location. End task follows Task Manager's rules:
  - It refuses PID 0 and 4, DeadWare itself, critical and protected processes, and Windows' own processes.
  - It may end third-party services and other users' processes when DeadWare runs as administrator.
  - For that one call it enables the debug privilege, as Task Manager does.
- **Approach:** reuse-first, in two releases: 0.13.0 for the live page and actions, 0.14.0 for history.
- **Click a moment:** clicking a point on Usage history's CPU or RAM chart lists the busiest apps at that time. This ships in 0.14.0.
- **No test run on the user's PC:** every measurement happens on GitHub's Windows runner in CI.
- **Disk gate:** if CI cannot prove disk attribution, 0.13.0 ships without the Disk columns, and 0.13.1 adds them using DeadWare's own thread tracking.
- **Opt-in:** **Per-process monitoring** is off by default, whatever the measurements show. The user turns it on.

## Proposed experience

### Processes page (0.13.0)

- **Sidebar:** **Processes** sits directly after Storage, with its own icon.
  - The rail keeps Storage immediately after Network.
  - The page has no history-range control, so the live toolbar is collapsed.
- **When monitoring is off** (the default), the page shows "Per-process monitoring is off. Turning it on adds one Windows process scan per poll." with a **Turn on** button. The button sets the same setting as Settings.
- **Top strip:**
  - A search box, "Find app or PID", matches the name or a PID.
  - **End task…** and **Open file location** buttons.
  - One status banner when something is limited, for example "Disk and network need administrator", with a **Restart as administrator** button.
- **Table:** one row per app, summed over its processes.
  - A chevron expands the row into its processes. An app with one process shows its PID inline and has no chevron. Rows are collapsed by default.
  - The default columns are **NAME · CPU · MEMORY · I/O · DISK · NETWORK**. Headers are uppercased like the Network table.
  - The **Columns** button and a header right-click open a checkable list that adds PID, I/O READ, I/O WRITE, DISK READ, DISK WRITE, ↓ DOWNLOAD and ↑ UPLOAD.
  - The choice is saved. Expanded process rows show the same columns.
  - Without administrator, DISK, NETWORK and their split columns are hidden. In the picker they are disabled, labelled "(administrator)".
- **Sorting:**
  - Click a header to sort. Sorting is numeric, nulls sort last in both directions, and ties sort by name, then PID.
  - Process rows sort within their app. The default is CPU, descending.
- **Calm order:**
  - Values update in place but rows don't move while the pointer is over the table, while a menu or dialog is open, or for 3 s after a keyboard navigation key.
  - New apps are appended at the bottom.
  - F5 or a header click re-sorts immediately.
- **Explained gaps:** every "—" has a tooltip naming its reason: needs administrator, first sample, stale reading, Windows dropped disk events, or the column's status text.
- **Footer of definitions:**
  - CPU is the share of all logical processors, as in Task Manager's Details tab, so it sums to at most 100 %.
  - Memory is the private working set, Task Manager's "Memory (private working set)".
  - I/O counts all file, pipe and device transfers, including cached reads, and is not disk activity.
  - Disk is physical drive transfers. Delayed cache writes are credited to System.
- **Keyboard and accessibility:**
  - Left and Right collapse and expand, Enter toggles, Delete opens End task…, and Ctrl+F focuses the search box.
  - Esc clears the search, and F5 re-sorts.
  - The grid, search box, buttons and chevrons have automation names, and each row has a spoken summary.
  - Colours come from theme resources only.

### Actions (0.13.0)

- **End task…** acts on the selected process, or on every process of the selected app. It never kills a process tree.
  1. **Refused without opening anything:**
     - PID 0 and 4.
     - DeadWare itself ("Use Quit DeadWare").
  2. **Refused after reading the process's facts:**
     - Critical processes (`IsProcessCritical`).
     - Protected processes (a protection level other than none).
     - **Windows' own processes:** the image is under the Windows folder (`%SystemRoot%`) *and* the token user is a system or service account (S-1-5-18, -19, -20, S-1-5-90-*, S-1-5-96-*). So svchost, lsass, dwm and services.exe are refused. The user's own Windows tools are allowed (Explorer, Command Prompt, PowerShell, Notepad, Task Manager), and so are third-party services when DeadWare runs as administrator.
  3. **Can it be ended?**
     - DeadWare opens the process with `PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`.
     - When elevated, the open runs on a short-lived thread whose impersonation token has **SeDebugPrivilege** enabled, and is reverted immediately afterwards.
     - If it is denied, the result is "Needs administrator" for a standard user (offering Restart as administrator) or "Windows denied" when elevated.
     - The handle from this check is kept until termination, so a PID reused in between can never be hit.
     - If the creation time no longer equals the row's Generation, the process has already exited.
  4. **Confirmation:**
     - A Yes/No box: "End chrome.exe (31 processes)? Unsaved work in it will be lost."
     - The default button is **No**. This is a new pattern, behind an internal `Confirm` hook for tests.
     - Refused or denied members of an app are listed with their reason, and only the allowed ones are ended.
  5. **Yes:**
     - `TerminateProcess(handle, 1)`, waiting up to 2 s for each process to exit.
     - One aggregated status line: ended N, refused M, already exited K.
  - All native work runs off the UI thread. The app manifest stays `asInvoker`.
- **Open file location:**
  - Checks that the process still has the row's Generation.
  - Reads the image path on demand with `QueryFullProcessImageNameW` and checks the file exists.
  - A short-lived STA thread calls `SHOpenFolderAndSelectItems`, falling back to `explorer.exe /select,"<path>"`.
  - The path stays in local variables and is never stored or logged.
  - An app whose processes run from more than one path says "runs from 2 locations; expand to choose one".

### Settings (0.13.0)

- **Settings → Monitoring → Per-process monitoring** is a checkbox, **off by default**, applied live at the next poll like the polling interval.
  - **Off:** no scan, no disk trace, baselines cleared, and the page shows its off state.
  - **On:** the scan starts at the next poll. The disk trace starts when DeadWare is elevated.
- The visible column set is saved in `UserSettings.ProcessColumns`, a list of column keys whose default is `cpu, memory, io, disk, network`.
- Nothing else is new. Saved history (0.14.0) follows the existing **Record usage history on this device** switch. Disk tracing follows elevation.
- Rollback safety: both settings are plain fields with safe defaults, so an older build that drops them loses nothing important.

### Usage history: Application resources (0.14.0)

- **Placement:** a new card, **Application resources**, sits under Applications and above "Keep a longer record". It uses the page's Day, Week and Month range and its earlier and later controls.
- **Contents:**
  - Rank by CPU time, Average memory or I/O.
  - A search box and a share bar.
  - A mixed-case table like its Applications neighbour: Application · CPU time · Avg CPU · Avg memory while listed · I/O read · I/O written · Minutes listed.
  - An **Everything else** row.
- **Selecting an app** draws its minute-by-minute series (CPU %, memory, I/O rate) in the existing `UsageHistoryChart`. Minutes when the app was not in that minute's busiest set are gaps, never zeros.
- **Click a moment:**
  - Clicking a point on the Device card's **CPU** or **RAM** chart selects that bucket's time span.
  - The card then lists the busiest apps in that span: "Busiest apps · 03:12–03:13". It is ranked by CPU time when the CPU chart was clicked, and by average memory when the RAM chart was clicked, and it includes Everything else.
  - On Week and Month, a bucket covers several minutes. The list aggregates those minutes: summed CPU time and I/O, and memory averaged over covered time.
  - The selection draws as a hairline on both charts. Esc or **Clear** returns to the period ranking.
  - Keyboard: the CPU and RAM charts become focusable. Left and Right move the selected bucket, Enter lists it.
  - This extends the shared `UsageHistoryChart` with an optional selection, so the other charts are unchanged.
- **Gaps:** a notice states how many minutes in the period have no process data (monitoring off, DeadWare not running, or a capture gap), plus a storage line for the history file.

## Data and collection (0.13.0)

### Process scan (every poll, collector thread)

- **The call:** one `NtQuerySystemInformation(SystemProcessInformation = 5)` per poll.
  - It runs just before `lock(publicationGate)` in the collector pass, after storage and battery.
  - No process handles are opened and no elevation is needed.
  - It is skipped while Per-process monitoring is off.
- **Buffer:**
  - A reused 1 MiB buffer pinned on the POH (`GC.AllocateUninitializedArray(pinned: true)`).
  - On `STATUS_INFO_LENGTH_MISMATCH` (0xC0000004) it grows to the returned size plus 256 KiB. That is retried at most three times per poll, with a 32 MiB cap.
  - x64 only (`IntPtr.Size == 8`).
- **Parsing** is done by a pure Core `ProcessSnapshotParser`, using `Span` and `BinaryPrimitives` over the x64 `SYSTEM_PROCESS_INFORMATION` layout:

  | Offset | Field |
  |---|---|
  | 0x00 | NextEntryOffset |
  | 0x08 | WorkingSetPrivateSize |
  | 0x20 | CreateTime |
  | 0x28 | UserTime |
  | 0x30 | KernelTime |
  | 0x38 / 0x40 | ImageName: length / buffer |
  | 0x50 | UniqueProcessId |
  | 0x64 | SessionId |
  | 0xE8 | ReadTransferCount |
  | 0xF0 | WriteTransferCount |

  The fixed part is 0x100 bytes, and each thread entry is 0x50 bytes; thread entries are skipped. The layout is undocumented, so CI tests on real Windows pin it (see Testing).
- **What fails the whole scan:**
  - A `NextEntryOffset` shorter than its entry, or past the returned length.
  - More than 65,536 entries.
  - An odd name length.
  - A non-empty name outside the buffer. A zero-length name (System Idle, PID 0) is allowed.
  - A failed scan publishes the process data as unavailable. It never throws into the pass.
- **Names** are decoded only for keys not seen in the previous scan.
- **Failures:** after one, the native call is retried at most once a minute. The hardware status notes "Process scan unavailable (NTSTATUS 0x…)" only after 60 s of failure, the same pacing as the storage counters.
- **Identity:** `(Pid, Generation)`, where Generation is `CreateTime + 504911232000000000`. That value equals `NativeProcessIdentity`'s `DateTime.FromFileTimeUtc(created).Ticks` exactly, with no exception path, so ETW rows join without guessing.
- **Cost:**
  - Measured by a CI test over 240 scans (p50, p95, buffer size, process and thread counts) and by the `windows/processes` timing key in `--diagnose`.
  - If CI p95 exceeds 15 ms, the scan moves to its own 1-second loop, in the style of `StorageSensorLoop`, instead of running every poll.
  - Monitoring is opt-in either way.

### Rates (Core `ProcessActivityTracker`)

- Each `(Pid, Generation)` keeps a baseline from the previous scan.
- **CPU %** is Δ(kernel + user) / (Δt × 10⁷ × N) × 100.
  - N is `GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)`, and Δt comes from the collector's monotonic clock.
  - It is a time-based share of all logical processors, matching Task Manager's Details tab and the CPU card's 0–100 scale.
- **Memory** is the private working set, in bytes.
- **I/O read/write** are rates of the transfer counts.
- **When a value is null:**
  - On a process's first scan, when Δt ≤ 0, when Δt exceeds the freshness limit, or when a counter goes backwards.
  - A process created since the previous scan (CreateTime ≥ previous scan − 1 s) is credited in full, as Usage history's minute folding does.
- **Exact deltas:** the tracker also keeps ΔCPU (100 ns units), ΔI/O bytes and Δt for history.
- **Cap:** at most 4,096 processes are published, keeping the busiest. `Omitted` counts the rest, and the page notes them.

### Network column

The column joins on `(Pid, Generation)` from the poll's single ETW take, `capture.Processes`. It never calls `EtwNetwork.Take` a second time.

| Case | Value |
|---|---|
| Matching row found | The published rates (null on the first take) |
| PID absent while capture is available | 0 B/s (the ledger lists only processes with bytes) |
| PID present under a different Generation | Null, counted as a miss |
| Capture unavailable | Null, plus the existing ETW status |

When at least 4 ETW rows have their PID in the scan and more than 50 % of them miss, the column becomes unavailable with "Per-app traffic could not be matched to processes". It never shows silent zeros.

### Disk column (administrator)

- **`EtwDisk`:** a second kernel `TraceEventSession` named `DeadWare-Disk-<pid>-<guid>`. It never shares the network session, so disk event loss can never reset network attribution.
  - Keywords are `DiskIO | Thread | Process`. TraceEvent adds Process and Thread to kernel masks in any case.
  - `DiskIORead` and `DiskIOWrite` use `data.ProcessID` after TraceEvent's issuing-thread fix-up. A value of −1 counts as unattributed.
  - It reuses `ProcessAttribution` and `ProcessLedger` with read and write counters, which keeps the eventTime ≥ Generation guard against PID reuse.
  - Start events and unknown PIDs resolve through `NativeProcessIdentity.Read`, as network does. The 5 s reconcile reads the same pass's scan index instead of reopening every process.
- **Rates** use a rolling window of 2 s or more, because real-time buffers flush about every second.
- **Buffer size and loss storm:** `BufferSizeMB` and the loss-storm threshold come from the CI measurement (default 10 consecutive lossy takes).
- **Loss:**
  - A take with lost events nulls that take and resets disk totals only.
  - A loss storm pauses tracing for 5 minutes, with a status.
- **Lifecycle:**
  - Every start and stop runs on one serialized pool task, never on the poll thread. A start costs about 200 ms plus rundown.
  - It stops within the existing 5 s hardware stop budget.
  - Before starting, it stops sessions named `DeadWare-Disk-<pid>-*` or `DeadWare-<pid>-*` whose PID is not a running DeadWare. These are orphans from a crash.
- **Gating:** created only when `CollectorOptions.ProcessDiskTracing` is set. `UiSession` sets it while monitoring is on and DeadWare is elevated. The default is false, so tests that build real collectors never open a kernel session.
- **Standard users:** no session is started, and the Disk columns are hidden.
- **Ship gate:** CI must credit at least 90 % of 256 MB of `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH` writes to the test's own `(Pid, Generation)`. Otherwise 0.13.0 ships with Disk hidden, and 0.13.1 adds DeadWare's own thread-to-process map built from Thread events.

### Publication

- The collector publishes an immutable `ProcessActivity` as a new init-only `Reading.ProcessActivity`, marked `[JsonIgnore]`. It is set on all three publish paths: normal, failed pass and stopped. It holds:
  - the sequence, timestamps, interval and processor count;
  - per-process samples with nullable values;
  - per-column availability and status;
  - the omitted count;
  - `None`, `Off` and `Unavailable(status)` instances.
- **Aggregation:** an app's value is the sum of its known members. It is null only when no member is known, matching the storage total rule.
- **Error isolation:** the scan, the disk take and the join share one collector-local try/catch, so a failure yields `ProcessActivity.Unavailable(<exception type>)` and never gaps the other metrics.
- **Unchanged:** `Reading.Processes`, `EtwNetwork`, usage-history.db (v1), `AlertKind`, `Snapshot.Values` and the six-hour graph cache.
- **Diagnostics:** `--diagnose` serializes a `ProcessSummary`: process count, per-column availability and status, join misses, unattributed disk events, and the busiest-union size. It adds the timing keys `windows/processes`, `processes/join`, `etw/disk` and `open/etwdisk`. No per-process names or paths are written for the new data, and the existing network list is unchanged.

## History (0.14.0)

- **`ProcessMinuteFolder`** follows `UsageMinuteFolder`'s breaks and segments, but sums the tracker's exact deltas: CPU time, I/O bytes, memory × Δt, and covered seconds.
  - At each minute's close it keeps the union of the top 10 by CPU time, by average memory and by I/O bytes (at most 30). A zero value never qualifies, and ties break by name.
  - Everything else is stored as one row. Processes with unknown names go into Everything else.
  - Partial minutes keep their rows with `complete = 0` and a gap count. Minutes with zero covered time are skipped.
- **`ProcessHistoryStore`** writes `process-history.db`, its own file with its own `application_id` and `user_version` 1.
  - **On open:** the file is inspected read-only with an integrity check first, as Connections history does.
  - **Durability:** WAL mode, `synchronous FULL` and an 8 MiB journal guard.
  - **Limits:** a 128 MiB cap with whole-day eviction (today is never evicted) and 30-day retention.
  - **Names:** a `NOCASE` names table referenced by integer ids.
  - **Restarts within a minute:** the first segment wins the minute, and later segments only clear `complete`.
  - **Reads:** at most 31 days, with `LIMIT` 1000 or less.
  - **Unreadable files:** newer, foreign or corrupt files are preserved and reported, never overwritten.
- **`ProcessHistoryService`** copies `UsageHistoryService`: a 120-slot bounded channel with `TryWrite`, drop counting, a 15 s flush and `Fail`.
  - It enqueues only the `ProcessActivity` and its sequence.
  - It records only while both Per-process monitoring and Record usage history are on.
  - It drains within Quit's 20 s history cap and sign-out's 3 s budget, null-guarded like Connections.
- **Expected size:** about 2 MB per day in the worst case. 0.13.0's `ProcessSummary` records the real union size, so the schema is checked before it freezes.

## Errors and limits

- Sources never throw into the poll. A process-data failure shows "—" and a status, and never blanks CPU, RAM or other metrics.
- Status text holds fixed copy, counts, NTSTATUS codes or exception type names. It never holds process names or paths.
- Actions map Win32 error codes to copy, and nothing reaches the dispatcher as an exception.
- Caps:
  - 4,096 published processes;
  - a 32 MiB scan buffer;
  - 30 history rows per minute plus Everything else;
  - 128 MiB history;
  - a disk-tracing pause after a loss storm.
- A history failure stops only its worker, and the other histories continue.

## Privacy

- Program paths are never stored or logged; Open file location reads them on demand.
- Process names exist in memory for the live view. In 0.14.0, app names are saved for 30 days in `process-history.db`, as Usage history already does for network per app.
- Names never enter `Snapshot.Values` or the graph cache.
- Diagnostics get only counts and timings for the new data.
- The README privacy section is updated in each release.

## Testing

- **Core (xUnit):**
  - The parser, against synthetic buffers: PID 0, malformed offsets, odd name lengths, the entry cap.
  - The tracker: the 12.5 % CPU case, PID reuse, crediting new processes, Δt edges, the 4,096 cap, a bound over 10,000 create/exit cycles.
  - The network join: match, 0, null, and the miss guard.
  - Grouping and sums.
  - The calm-order row planner (moves only rows outside the longest increasing subsequence).
  - The disk rolling window.
  - The `EndTaskPolicy` refusal matrix, including the Windows-folder plus service-account rule.
  - Settings defaults and round trip.
  - 0.14.0: the minute folder and the store.
- **App:**
  - Collector tests with a fake and a throwing process source: CPU and memory keys survive, and switching off takes effect within one poll.
  - `EtwDisk` through test seams.
  - `--diagnose` JSON contains `ProcessSummary` and no process names from the new data.
  - Actions through the native-control and confirm hooks.
- **UI checks:**
  - A Processes-page phase after Storage: the off state, rows, expansion, the column picker, the hidden admin columns, the banner and tooltips.
  - Shell checks for 12 rail entries and Storage + 1 == Processes.
  - The settings switch.
  - 0.14.0: the Application resources card and click-a-moment in the Usage history checks.
- **Preview:** a `processes` route (and the missing `storage` route), a synthetic fixture, and the 900 × 640 shell frame with a table of at least 70 px.
- **Real Windows in CI** (GitHub's Windows runner, which runs elevated; this is the measurement step, and nothing runs on the user's PC):
  - The struct offsets: the scan contains the test's own PID with `Path.GetFileName(Environment.ProcessPath)`, PID 4 is System, and the test's own CPU time never decreases.
  - Generation equality with `NativeProcessIdentity.Read`, and image-name equality, for every readable process after a re-scan that discards reused PIDs.
  - 240-scan timing (p50, p95, buffer, process and thread counts), which decides every poll versus a 1 s loop.
  - Disk attribution of 256 MB of unbuffered writes (at least 90 %) and the share of −1 events, which decides the disk gate. Also the buffer size and loss-storm threshold.
  - Orphan-session cleanup.
  - End-task probes against the test's own child processes only.
- **Pending by eye** (listed in docs/validation.md and never claimed as verified):
  - Task Manager Details comparison.
  - The End-task matrix against real services and other users.
  - Open file location in Program Files, WindowsApps and paths with spaces and commas.
  - Behaviour at 0.5 s and 10 s polling, during sleep, and on Quit during an elevated copy.

## Phasing and release

- **0.13.0:**
  1. Measurement tasks first: the Core types, `WindowsProcesses`, publication, `ProcessSummary`, `EtwDisk`, and the real-Windows CI tests that set the loop placement and the disk gate.
  2. Then the Processes page, actions, settings and preview.
  3. Then README, CHANGELOG (including the "planned for 0.12" answer), `docs/releases/v0.13.0.md`, validation notes and the version.
- **0.13.1**, only if the disk gate failed: Disk columns via DeadWare's own thread-to-process map.
- **0.14.0:** the minute folder, store, service, the Application resources card and click-a-moment, with release docs.
- **Workflow:** as recent releases, a feature branch, then a PR to `main` with CI as the gate, then a release when the user says go. The implementation plan follows the Storage plan's template at `docs/superpowers/plans/2026-11-05-processes.md`.

## Out of scope

- Per-process GPU.
- Processes cards on the Overview or the widget.
- Alert rules on processes.
- Ending process trees.
- Changing priority or affinity.
- Command lines and services views.
- Per-process disk or network history (network per app is already in Usage history's Applications).
- Feeding ETW identity from the process scan (replacing the 5 s reconcile).
- Excluding the existing `Reading.Processes` from `--diagnose`.
