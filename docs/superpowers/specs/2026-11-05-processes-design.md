# Processes (0.13.0 live view, 0.14.0 history)

Status: proposed on 2026-11-05. Purpose, history depth, placement, grouping, the disk column, actions, the approach and all five design sections were approved by the user in the same session, including the changes listed under the decisions below. This is the third of the three planned releases: per-core CPU shipped as 0.10.0, Storage as 0.11.0, and now Processes. The Storage spec and the 0.11.0 notes called this release "0.12"; that number went instead to 0.12.0 (per-core line styles and roomier overview graphs) and its 0.12.1–0.12.4 follow-ups, so Processes ships as 0.13.0 (live view and actions) and 0.14.0 (saved history). The 0.13.0 notes say so and answer CHANGELOG.md's "Per-application disk I/O … is planned for 0.12" line: per-application Disk ships in 0.13.0 for administrators or, if the disk gate fails, is announced for 0.13.1, and the I/O column is never presented as that disk data.

## Intent

DeadWare shows how busy the machine is, but not *who* is making it busy. Per-application data exists today only for network traffic, and only as administrator: the Network page table, Usage history's Applications view and Connections. When the PC feels slow, the user has to open Task Manager to find the process responsible, and nothing records which app spiked CPU, memory or I/O overnight.

This work adds a live Processes page: CPU, memory, I/O, disk and network per app, with End task and Open file location. A later release adds 30 days of saved per-app CPU, memory and I/O history on Usage history, so "what was busy at 3 am?" has an answer. Disk per app stays live-only.

Minor-version milestones:
- 0.13.0 adds a new sidebar page, two saved settings (Per-process monitoring and the remembered process columns) and new per-process collector data: a Windows process scan and, as administrator, a kernel disk trace (unless the disk gate moves Disk to 0.13.1).
- 0.14.0 adds a new local database and a new Usage history card.

## Decisions taken in brainstorming

- **Purpose:** a live view first, plus saved per-process history.
- **History:**
  - Saved to disk for about 30 days.
  - Each minute keeps the union of the top 10 apps by CPU, the top 10 by memory and the top 10 by I/O (at most 30), plus an Everything else total.
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
- **Click a moment:** clicking a point on Usage history's Device CPU or RAM chart lists the busiest apps at that time. This ships in 0.14.0.
- **No test run on the user's PC:** every measurement happens on GitHub's Windows runner in CI.
- **Disk gate:** if CI cannot prove disk attribution (at least 90 % of a 256 MB test write credited to the test's own process), 0.13.0 ships without the Disk columns, and 0.13.1 adds them using DeadWare's own thread tracking.
- **Opt-in:** **Per-process monitoring** is off by default, whatever the measurements show. The user turns it on.
- **Workflow:** as recent releases, a feature branch with a pull request to `main`; CI is the gate.

## Proposed experience

### Processes page (0.13.0)

- **Sidebar:** **Processes** sits directly after Storage, with its own icon.
  - The rail keeps Storage immediately after Network.
  - The page has no history-range control, so the live toolbar is collapsed.
- **When monitoring is off** (the default), the page shows "Per-process monitoring is off. Turning it on adds one Windows process scan per poll." with a **Turn on** button. The button calls the same `UiSession.SetProcessMonitoring` as the Settings checkbox, whose help line repeats the second sentence. That sentence follows what the build does:
  - When DeadWare runs as administrator in a build that includes the Disk columns, it reads "Turning it on adds one Windows process scan per poll and a disk trace."
  - If CI moves the scan to its own loop (see Cost), "per poll" reads "per poll (at most one a second)".
  - From 0.14.0 another sentence follows: "While usage history is recorded, it also keeps the busiest apps for 30 days."
- **Top strip:**
  - A search box, "Find app or PID", matches the name or a PID.
  - **End task…** and **Open file location** buttons.
  - One status banner when something is limited, for example "Disk and network need administrator", with a **Restart as administrator** button.
- **Table:** one row per app, summed over its processes.
  - Processes belong to one app when their image names are equal under `StringComparer.OrdinalIgnoreCase`, the rule `UsageMinuteFolder` already uses. Windows file names ignore case, but the scan keeps the casing each process was started with, so `Explorer.EXE` and `explorer.exe` are one app. Row identity (selection, expansion, calm order) uses the case-insensitive name, never the displayed text, and the row shows the casing of the app's first process in the scan. The same rule decides which processes End task… ends for an app, and saved history uses the same key.
  - A chevron expands the row into its processes. An app with one process shows its PID inline and has no chevron. Rows are collapsed by default.
  - The default columns are **NAME · CPU · MEMORY · I/O · DISK · NETWORK**. Headers are uppercased like the Network table.
  - The **Columns** button and a header right-click open a checkable list of every column except NAME, which is always shown. The list holds CPU, MEMORY, I/O, DISK and NETWORK (checked by default), then PID, I/O READ, I/O WRITE, DISK READ, DISK WRITE, ↓ DOWNLOAD and ↑ UPLOAD (unchecked by default). Any entry can be checked or unchecked.
  - Shown columns keep one fixed order, whatever order they were checked in: NAME · PID · CPU · MEMORY · I/O · I/O READ · I/O WRITE · DISK · DISK READ · DISK WRITE · NETWORK · ↓ DOWNLOAD · ↑ UPLOAD. The choice is saved. Expanded process rows show the same columns.
  - Without administrator, DISK, NETWORK and their four split columns are hidden. In the picker those six entries are disabled, labelled "(administrator)", and keep their saved state. If the disk gate fails, 0.13.0 leaves DISK, DISK READ and DISK WRITE out of both the table and the picker until 0.13.1 (see Disk column).
  - The page decides this hiding, the picker state and the banner from the published reading's `Elevated` flag (`session.Current.Elevated`), never from the static `Collector.IsElevated`, so UI checks can show the standard-user state on GitHub's elevated runner. Apart from the disk ship gate, only elevation hides a column the user has checked: when DeadWare is elevated, an unavailable Disk or Network column stays visible and shows "—" with its status.
- **Sorting:**
  - Click a header to sort. Sorting is numeric, nulls sort last in both directions, and ties sort by name, then PID.
  - Process rows sort within their app. The default is CPU, descending.
  - If the sorted column is hidden, the table returns to that default, or to NAME ascending when CPU is hidden.
- **Calm order:**
  - Values change when a new Reading arrives or the current one turns stale. Rows re-sort on their own only when a new Reading arrives, never on a `UiSession.Changed` that brings none (its timer fires at least once a second). Every update keeps the selection, the expanded rows and the scroll position.
  - The order is held while the pointer is over the table, while a menu or dialog is open, and for 3 s after a keyboard navigation key. During a hold, a new Reading updates values in place, removes the rows of processes and apps that have exited, appends new apps at the bottom, and adds new processes of an expanded app at the end of that app's rows. Nothing is reordered.
  - Once none of those conditions is true, the next new Reading re-sorts the table. Outside a hold, every new Reading re-sorts it, so new apps take their sorted place at once.
  - F5, a header click or a search change re-sorts immediately, even during a hold. Expanding or collapsing a row adds or removes only that app's process rows.
- **Explained gaps:** every "—" has a tooltip naming its reason: first sample (also for the other three null cases under Rates: a Δt that is not positive or exceeds the freshness limit, a counter that went backwards, and a running process the previous scan missed), stale reading, traffic or disk activity not matched to this process (a join miss; see Network column and Disk column), Windows dropped disk events, or the column's status text. A missing administrator right never produces a "—": for a standard user, DISK, NETWORK and their split columns are hidden instead, as above, and the banner and the picker's "(administrator)" label say why.
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
  2. **Open once:**
     - DeadWare opens the process with `PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`.
     - When elevated, this open runs on a short-lived thread whose impersonation token has **SeDebugPrivilege** enabled, and the thread reverts immediately afterwards. End task enables the privilege for no other call. In an elevated DeadWare whose token holds the privilege, it is already enabled on the process token: .NET's `System.Diagnostics.Process` enables SeDebugPrivilege the first time it reads process information, and `GuiInstance` does that at every start (`Process.GetCurrentProcess().SessionId`). End task does not rely on that.
     - If the PID no longer exists (`ERROR_INVALID_PARAMETER`), the process has already exited.
     - If the open is denied, DeadWare reopens the process with `PROCESS_QUERY_LIMITED_INFORMATION` only, without enabling the privilege itself, just to read the facts, so that a refused process shows its refusal reason rather than "Needs administrator".
  3. **Read the facts and classify** (Core `EndTaskPolicy`), on the handle from step 2:
     - First the creation time. If it no longer equals the row's Generation, the process has already exited.
     - Then `IsProcessCritical`, the protection level, the image path (`QueryFullProcessImageNameW` in Win32 form, flags 0, as `NativeProcessIdentity.Read` reads it) and the token user (`OpenProcessToken` with `TOKEN_QUERY`).
     - **Refused**, with the reason shown even when the terminate open was denied:
       - Critical processes.
       - Protected processes (a protection level other than none).
       - **Windows' own processes:** either the image is inside the Windows folder *and* the token user is a system or service account (S-1-5-18, -19, -20, S-1-5-90-*, S-1-5-96-*), or the image is the Windows `svchost.exe` (directly in the Windows folder's `System32` or `SysWOW64`) whatever the token user.
         - The Windows folder is the one `GetSystemWindowsDirectoryW` returns. The App reads it once and passes it to `EndTaskPolicy` with the other facts; the `%SystemRoot%` environment variable is not used. Inside means the image path starts with that folder followed by `\`, compared with `StringComparison.OrdinalIgnoreCase`, the containment test of `SafeFiles.Child` (`src/DeadWare.UpdateCore/Packages.cs`). So a folder that reads `C:\WINDOWS` matches `C:\Windows\System32\svchost.exe`, and `C:\Windows.old\…` and `C:\WindowsAzure\…` do not match.
         - The driver store, `System32\DriverStore\` under the Windows folder, does not count as inside for this rule. Windows runs services installed by driver packages from there, such as NVIDIA's Display Container and Intel graphics and Realtek audio services.
         - The svchost condition is needed because per-user service hosts (`CDPUserSvc_*`, `WpnUserService_*`, `cbdhsvc_*`, `OneSyncSvc_*` and similar) run as the signed-in user.
         - So every Windows svchost is refused, including the per-user ones, and so are lsass, dwm and services.exe. The user's own Windows tools are allowed (Explorer, Command Prompt, PowerShell, Notepad, Task Manager). Third-party services are also allowed when DeadWare runs as administrator, including driver services in the driver store.
         - Known limit: a third-party service whose image sits elsewhere under the Windows folder (for example directly in System32 or SysWOW64, as some older drivers install) is refused as a Windows process, and the README says so.
     - **An unreadable fact never allows ending.** A process can be ended only when the terminate open succeeded, its creation time matches, and every refusal rule is known not to apply. For the Windows' own rule, a known image path outside the Windows folder (or in the driver store) settles it without the token user. A path inside it that is not a Windows svchost also needs the token user, and an unknown path never settles the rule.
     - **Otherwise it is not ended.** When the terminate open was denied, or a rule cannot be decided (including when even the facts-only open fails), the result is "Needs administrator" for a standard user or "Windows denied" when elevated. For a standard user, the banner's **Restart as administrator** button is the offer (see step 4).
     - Handles of processes that can be ended are kept until termination, so a PID reused in between can never be hit. Every other handle is closed at once.
  4. **Confirmation**, only when at least one member can be ended:
     - A Yes/No box whose question counts only the processes that will be ended. When every member can be ended it reads "End chrome.exe (31 processes)? Unsaved work in it will be lost." Otherwise it reads "End 28 of 31 chrome.exe processes? Unsaved work in them will be lost." The second number counts the members still running when the box opens.
     - Members that will not be ended are summarised under the question by reason, with counts, never one by one, for example "Not ended: 3 need administrator." Each member counts once, under the first rule above that refuses it. Members that have already exited are left out.
     - The default button is **No**. This is a new pattern, behind an internal `Confirm` hook for tests.
     - **When no member can be ended,** no box opens. The status line gives the reason, with counts for an app, for example "Use Quit DeadWare" or "Not ended: 2 Windows' own processes". If any member needs administrator, the line says so and points to the banner's **Restart as administrator** button, which a standard user always sees on this page. No second prompt opens.
  5. **Yes:**
     - A member whose kept handle is already signalled has exited and counts as already exited. Every other member that can be ended gets `TerminateProcess(handle, 1)`, and then one wait of up to 2 s in total covers them all.
     - One aggregated status line counts every member once: "Ended N · not ended M · already exited K · still running R". Not ended covers the refused and denied members. Still running counts members whose handle is not signalled by the end of that wait, whether their `TerminateProcess` failed or not. A member whose `TerminateProcess` failed but whose handle is signalled by then had exited meanwhile and counts as already exited.
  - All native work runs off the UI thread. The app manifest stays `asInvoker`.
- **Open file location:**
  - Checks that the process still has the row's Generation.
  - Reads the image path on demand with `QueryFullProcessImageNameW` and checks the file exists.
  - A short-lived STA thread calls `SHOpenFolderAndSelectItems`, falling back to `explorer.exe /select,"<path>"`.
  - The path stays in local variables and is never stored or logged.
  - An app whose processes run from more than one path says "runs from 2 locations; expand to choose one".

### Settings (0.13.0)

- **Settings → Monitoring → Per-process monitoring** is a checkbox saved as `UserSettings.ProcessMonitoring` (a bool), **off by default**. It applies live at the next poll, like the polling interval: `UiSession.SetProcessMonitoring(bool)` saves it and calls `Collector.SetProcessMonitoring(bool)`, which sits beside `SetPollingInterval`. The page's **Turn on** button calls the same method. The checkbox's help line is the off state's second sentence (see Processes page).
  - **Off:** no scan, no disk trace, baselines cleared, and the page shows its off state.
  - **On:** the scan starts at the next poll. The disk trace starts when DeadWare is elevated, in a build that includes the Disk columns.
- The column choice is saved in `UserSettings.ProcessColumns`, a list of column keys. NAME is always shown and has no key. The keys, in a fixed order that is also the column order after NAME, are `pid, cpu, memory, io, ioread, iowrite, disk, diskread, diskwrite, network, download, upload`, spelled like the existing `GraphColorSettings` keys. The default is `cpu, memory, io, disk, network`.
  - The list records the user's choice, not what is visible now. The administrator-only keys (`disk`, `diskread`, `diskwrite`, `network`, `download`, `upload`) stay saved while DeadWare is not elevated. The Disk keys also stay valid and saved in a 0.13.0 that ships without Disk, so 0.13.1 shows Disk by default without a settings migration. This matters because `UiSession.Start` saves settings on every launch.
  - `SettingsCodec.Read` normalizes the list with a Core helper modelled on `WidgetGraphSelection.Normalize`: a null or missing list becomes the default; null, unknown (for example from a newer build) and duplicate keys are dropped; the keys are put in the fixed order; and an empty list (NAME only) is kept. Unlike `WidgetGraphs`, a corrected list is fixed quietly, as `OpenWindows` is, without the "Invalid settings were replaced with defaults." notice.
- Nothing else is new. Disk tracing follows elevation.
- Saved history (0.14.0) has no switch of its own. It follows the existing **Record usage history on this device** switch and also needs Per-process monitoring on. So 0.14.0 updates the text that says what that switch keeps:
  - Settings → History and alerts: the help line keeps its opening "Keeps day, week and month history" (ShellUiChecks finds it by that prefix) and gains: "While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory and I/O for 30 days."
  - Usage history → Keep a longer record: the description that begins "One row per minute for CPU, GPU, RAM and network" keeps "up to 400 days" (UsageHistoryUiChecks checks it) and gains the same sentence, ending "for 30 days (up to 128 MiB)". The privacy line becomes: "History stays in this Windows profile in usage-history.db and process-history.db and is never uploaded. Application names and byte totals describe what runs on this computer, so treat exports as private. Back up the databases only while DeadWare is fully quit."
  - The Processes page's off state gains its 0.14.0 sentence (see Processes page).
- Rollback safety: an older build ignores both keys and drops them on its next save. Returning to this build then shows monitoring off and the default columns, so nothing important is lost.

### Usage history: Application resources (0.14.0)

- **Placement:** a new card, **Application resources**, sits under Applications and above "Keep a longer record". It uses the page's Day, Week and Month range and its earlier and later controls.
- **Contents:**
  - Rank by CPU time, Average memory or I/O.
  - A search box and a share bar of the ranked metric's period total, Everything else included: CPU time, Σ(memory × Δt) for Average memory, or I/O read + written.
  - A mixed-case table like its Applications neighbour: Application · CPU time · Avg CPU · Avg memory while listed · I/O read · I/O written · Minutes listed.
  - An **Everything else** row.
  - **Definitions:** over the period, CPU time, I/O read and I/O written are sums over the minutes the app was listed. Avg CPU = 100 × CPU time ÷ Σ(N × covered seconds) over all the period's recorded minutes, a share of the whole machine like the live CPU column. Avg memory while listed = Σ(memory × Δt) ÷ the summed covered seconds of only the minutes the app was listed. Rank by Average memory sorts by Avg memory while listed, and Rank by I/O sorts by read + written. Everything else uses the same formulas and is present in every recorded minute.
- **Selecting an app** shows its own `UsageHistoryChart` (the existing control) in the card, under the table, with one series for the metric chosen in Rank by:
  - **CPU time:** CPU % of all logical processors, on a fixed 0–100 scale like the Device CPU chart.
  - **Average memory:** memory, in a new byte unit in the chart's formatter, shown with `Ui.Bytes`, with an automatic maximum. The Device charts don't use it.
  - **I/O:** I/O read + written, in B/s.
  - Changing Rank by redraws the chart for the same app.
  - `ProcessHistoryStore` returns the series as a `UsageHistoryView`, bucketed in SQL the way `ReadMetric` buckets the Device charts (max(1 minute, ⌈period ÷ points⌉), aligned to the period start), with the Device charts' point count capped at 1,000 to stay within the store's `LIMIT`. So one point can cover several minutes, as on the Device charts.
  - A bucket uses only the minutes in which the app was listed. Its point divides their sums by their covered seconds only (by N × covered seconds for CPU %), and its band spans their lowest and highest minute values. Minutes when the app was not in that minute's busiest set are gaps, never zeros, so a bucket missing any of its minutes is partial and is never connected to its neighbours, as on the Device charts.
- **Click a moment:**
  - Clicking a point on the Device card's **CPU** or **RAM** chart selects that bucket's time span.
  - The card then lists the busiest apps in that span, for example "Busiest apps · 03:13–03:16". It is ranked by CPU time when the CPU chart was clicked, and by average memory when the RAM chart was clicked, and it includes Everything else.
  - A bucket can cover several minutes on every range, Day included. It is the slot `ReadMetric` already returns: [from + k × bucket, from + (k + 1) × bucket), where from is the period start (not minute-aligned) and bucket = max(1 minute, ⌈period ÷ points⌉), with points the CPU chart's width in pixels clamped to 60–1,500. The CPU and RAM charts share this grid. At the default window size a slot is about 3 minutes on Day, 23 minutes on Week and 1.6 hours on Month; a Day slot is one minute only when the chart is at least 1,440 px wide.
  - The list aggregates exactly the stored minutes the clicked point averaged, those whose start falls in the slot (`ReadMetric`'s (minute − from) ÷ bucket grouping), with the card's formulas: CPU time and I/O summed, Avg CPU over the slot's covered seconds, and memory as each app's Σ(memory × Δt) ÷ the covered seconds of the minutes it was listed.
  - The heading names the minutes listed, from the first one's start to the last one's end, in local time, with the date on Week and Month (for example "Busiest apps · Nov 3 03:05–03:28").
  - The selection draws as a hairline on both charts. Esc or **Clear** returns to the period ranking.
  - Keyboard: the CPU and RAM charts become focusable. Left and Right move the selected bucket, Enter lists it.
  - This extends the shared `UsageHistoryChart` with an optional selection and the byte unit, so the other charts are unchanged.
- **Gaps:** a notice states how many minutes in the period have no process data (monitoring off, DeadWare not running, or a capture gap), plus a storage line for the history file.

## Data and collection (0.13.0)

### Process scan (every poll on the collector thread, unless Cost moves it to a loop)

- **The call:** one `NtQuerySystemInformation(SystemProcessInformation = 5)` per scan.
  - In the every-poll placement it runs just before `lock(publicationGate)` in the collector pass, after storage and battery.
  - No process handles are opened and no elevation is needed.
  - It is skipped while the collector's monitoring flag is off (see Switch).
- **Switch:** a starting value and a live flag, as for the polling interval.
  - `CollectorOptions` gains `ProcessMonitoring`, default false. It is only the starting value: the constructor passes it to the live flag, as it does `PollingIntervalSeconds`.
  - `Collector.SetProcessMonitoring(bool)` sets that volatile flag, beside `SetPollingInterval`. `UiSession` builds the collector with `ProcessMonitoring: Settings.ProcessMonitoring`, and `UiSession.SetProcessMonitoring` saves each change and calls it (see Settings). `--diagnose` sets the option unless `--no-processes` is given (see Diagnostics).
  - Each pass reads the flag once, at its start. When it is off, the pass skips the scan, clears the baselines and publishes `ProcessActivity.Off`.
  - Tests that build real collectors leave it false, so they never scan.
- **Buffer:**
  - A reused 1 MiB buffer pinned on the POH (`GC.AllocateUninitializedArray(pinned: true)`).
  - On `STATUS_INFO_LENGTH_MISMATCH` (0xC0000004) it grows to the returned size plus 256 KiB. That is retried at most three times per scan, with a 32 MiB cap.
  - x64 only (`IntPtr.Size == 8`).
- **Parsing** is done by a pure Core `ProcessSnapshotParser`, using `Span` and `BinaryPrimitives` over the x64 `SYSTEM_PROCESS_INFORMATION` layout:

  | Offset | Field |
  |---|---|
  | 0x00 | NextEntryOffset |
  | 0x04 | NumberOfThreads |
  | 0x08 | WorkingSetPrivateSize |
  | 0x20 | CreateTime |
  | 0x28 | UserTime |
  | 0x30 | KernelTime |
  | 0x38 / 0x40 | ImageName: length / buffer |
  | 0x50 | UniqueProcessId |
  | 0x64 | SessionId |
  | 0xE8 | ReadTransferCount |
  | 0xF0 | WriteTransferCount |

  The fixed part is 0x100 bytes. It is followed by NumberOfThreads thread entries of 0x50 bytes each, which are skipped, so an entry is at least 0x100 + NumberOfThreads × 0x50 bytes. The name string normally follows inside the entry, so `NextEntryOffset` is usually larger. NumberOfThreads also supplies the thread counts that CI records. The layout is undocumented, so CI tests on real Windows pin it (see Testing).
- **What fails the whole scan:**
  - An entry, including the last one, whose 0x100 + NumberOfThreads × 0x50 bytes do not fit inside the returned length.
  - A non-zero `NextEntryOffset` smaller than 0x100 + NumberOfThreads × 0x50, or one that reaches past the returned length. A `NextEntryOffset` of 0 marks the last entry and ends the walk; it is not a failure.
  - More than 65,536 entries.
  - An odd name length.
  - A non-empty name outside the buffer. A zero-length name is allowed, because PID 0 (System Idle Process) has one.
  - A failed scan publishes the process data as unavailable. It never throws into the pass.
- **Names** are decoded only for keys not seen in the previous scan.
- **PID 0 is dropped.** The parser checks the System Idle Process entry and then skips it, so it never reaches the tracker. Its kernel time is the idle time of every logical processor, so as a row it would read about (100 − CPU usage) % and top the default CPU sort. It is never published, shown as a row, summed or counted against the 4,096 cap, and it is never recorded in history. End task still refuses PID 0, as a guard. Any other process with an empty name is shown as "Unknown application" (`UsageMetrics.NormalizeName`).
- **Failures:** after one, the native call is retried at most once a minute. The hardware status notes "Process scan unavailable (NTSTATUS 0x…)" only after 60 s of failure, the same pacing as the storage counters.
- **Identity:** `(Pid, Generation)`, where Generation is `CreateTime + 504911232000000000`. That value equals `NativeProcessIdentity`'s `DateTime.FromFileTimeUtc(created).Ticks` exactly, with no exception path, so ETW rows join without guessing.
- **Cost:**
  - Measured by a CI test over 240 scans (p50, p95, buffer size, process and thread counts). That test alone decides the placement below. `--diagnose` also reports the scan time as `windows/processes`, for troubleshooting only.
  - If CI p95 exceeds 15 ms, the scan and the tracker move off the poll thread into their own loop, in the style of `StorageSensorLoop`, with a period of max(1 s, the current polling interval), so it never scans more often than every-poll scanning would.
    - The loop times its scans with the collector's monotonic clock, scans only while the monitoring flag is on, and is joined within the 5 s hardware stop budget like the health loop.
    - Each poll publishes the loop's latest tracked scan, joined with that poll's ETW take, and EtwDisk's reconcile uses the latest scan's index.
    - The tracker's exact deltas (CPU time, I/O bytes, memory × Δt and Δt) accumulate until a poll takes them, so history counts every scan once, even when a poll finds no new scan or two.
    - The off-state sentence changes to match (see Processes page).
  - Monitoring is opt-in either way.

### Rates (Core `ProcessActivityTracker`)

- Each `(Pid, Generation)` keeps a baseline from the previous scan.
- **CPU %** is Δ(kernel + user) / (Δt × 10⁷ × N) × 100.
  - N is `GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)`, and Δt comes from the collector's monotonic clock.
  - It is a time-based share of all logical processors, matching Task Manager's Details tab and the CPU card's 0–100 scale.
- **Memory** is the private working set, in bytes. It is a level, not a rate, so it is published whenever the scan succeeds, including on a process's first scan.
- **I/O read/write** are rates of the transfer counts.
- **When a rate is null:** CPU %, the I/O rates, and the ΔCPU and ΔI/O bytes kept for history are null in four cases:
  - There is no previous successful scan (the first scan after DeadWare starts or after monitoring is turned on).
  - Δt ≤ 0, or Δt exceeds `PollingPolicy.FreshnessSeconds` for the longer of the two scans' polling intervals.
  - One of the process's counters went backwards.
  - The process was missing from the previous scan but its CreateTime is more than 1 s before that scan's UTC time. It was running but not seen.
- **New processes:** a process missing from the previous scan whose CreateTime is at or after that scan's UTC time − 1 s started during the interval. Its whole counters are therefore its delta, and it is credited in full. The tracker keeps each scan's UTC wall-clock time for this test, alongside the monotonic time used for Δt. Usage history's `UsageMinuteFolder` also credits new keys in full, but it does so for every key that appears after its first publication, with no creation-time test. The test is new here because scan counters run from process creation.
- **Exact deltas:** for history, the tracker also keeps ΔCPU (100 ns units), ΔI/O read and write bytes, memory × Δt, and Δt.
- **Cap:** at most 4,096 processes are published per scan, ranked on the scan's own values: CPU % descending, then I/O (read + write) descending, then private working set descending, with null counting as 0, and finally PID ascending. The first scan, which has no CPU or I/O values, therefore keeps the largest by memory. `Omitted` counts the rest and carries their summed private working set and exact deltas. The page notes "Showing 4,096 of N processes".

### Network column

The column joins on `(Pid, Generation)` against `capture.Processes`, the result of the poll's single network take, `etw.TakeSnapshot(timestamp, now, options.EtwEnabled)` inside `lock(publicationGate)`, so the join runs inside that lock, after that call (see Error isolation). The process code never takes the network capture itself, with `EtwNetwork.TakeSnapshot` or its unused public `Take(double)` wrapper: every take resets the per-app rate baselines and drains the connection ledger's pending deltas, so an extra take would corrupt the Network page's per-app rates and the Connections history.

| Case | Value |
|---|---|
| Matching row found | The published rates (null on the first take) |
| PID absent while capture is available | 0 B/s (the ledger lists only processes with bytes) |
| PID present under a different Generation | Null, with the tooltip "traffic not matched to this process", counted as a miss |
| Capture unavailable | Null, plus the existing ETW status |

When at least 4 ETW rows have their PID in the scan and more than 50 % of them miss, the column becomes unavailable with "Per-app traffic could not be matched to processes". It never shows silent zeros.

### Disk column (administrator)

- **`EtwDisk`:** a second kernel `TraceEventSession` named `DeadWare-Disk-<pid>-<guid>`. It never shares the network session or its attribution, so disk event loss can never reset network attribution.
  - Keywords are `DiskIO | Thread | Process`. TraceEvent adds Process and Thread to kernel masks in any case.
  - `DiskIORead` and `DiskIOWrite` use `data.ProcessID` after TraceEvent's issuing-thread fix-up. A value of −1 counts as unattributed.
  - It uses its own `ProcessAttribution` and `ProcessLedger` instances, never the network ones, and adds no counters to them or to `ProcessTraffic`. Each event's `TransferSize` goes in `Record`'s `down` argument for `DiskIORead` and in `up` for `DiskIOWrite`, which keeps the eventTime ≥ Generation guard against PID reuse. EtwDisk renames the take at its own boundary (`ReceivedBytes` = cumulative bytes read, `SentBytes` = cumulative bytes written), so nothing downstream reads a download field as disk.
  - Start events, and events from PIDs it does not track yet, resolve through `NativeProcessIdentity.Read`, as network does.
  - For the 5 s reconcile, `ProcessAttribution` gains an overload, `Reconcile(Func<int, ProcessIdentity?> index)`. EtwDisk's take calls it with the latest scan's pid-to-identity index (the same pass's while the scan runs every poll). A tracked PID whose Generation matches the index is kept without reopening it. A PID the index lacks, or lists under another Generation (older, as for a process that started after that scan, or newer), is re-read through `NativeProcessIdentity.Read`, so it is neither dropped as exited nor swapped for its PID's previous owner. The existing `Reconcile()` keeps the constructor's resolver, so `EtwNetwork` is unchanged.
- **Rates** come from a rolling window of 2 s or more (Core `DiskRateWindow`), because real-time buffers flush about every second. `ProcessLedger.Take`'s own rates cover only the gap since the previous take (one poll), so they are not published. The window is computed from the cumulative bytes read and written per `(Pid, Generation)`, and restarts when a loss resets the totals.
- **Join:** Disk cells join on `(Pid, Generation)` against the poll's single disk take, like Network. The first matching case applies:

  | Case | Value |
  |---|---|
  | Tracing starting, failed or paused, or a take with lost events | Null, plus the disk status |
  | Less than 2 s of window since tracing started or since a loss reset | Null ("first sample") for every row, because unflushed buffers mean an absent PID is not yet a zero |
  | Matching row found | The rolling-window rates |
  | PID absent from the disk ledger | 0 B/s (the ledger lists only processes with bytes) |
  | PID present under a different Generation | Null, with the tooltip "disk activity not matched to this process", counted as a disk miss |

  When at least 4 disk rows have their PID in the scan and more than 50 % of them miss, or when more than half of the window's disk events are unattributed (−1), the Disk columns become unavailable with "Per-app disk activity could not be matched to processes". They never show silent zeros.
- **Buffer size and loss storm:**
  - `BufferSizeMB` starts at TraceEvent's default, the larger of 64 MB and 2 MB per logical processor. EtwNetwork never sets it, so the network session already runs at that size.
  - A loss storm is 10 consecutive lossy takes, a fixed value.
  - The real-Windows disk test (see Testing) records events lost and the longest run of lossy takes. If events are lost, it repeats at double the size, up to four times the default. EtwDisk uses the smallest size that lost nothing; if every size lost events, it uses four times the default and the loss is recorded as a known limit. The chosen size and the measurements are recorded in docs/validation.md.
- **Loss:**
  - A take with lost events nulls that take and resets disk totals only. Disk then shows first-sample nulls until the window holds 2 s again.
  - A loss storm pauses tracing for 5 minutes, with a status.
- **Lifecycle:**
  - Every start and stop runs on one serialized pool task, never on the poll thread. A start costs about 200 ms plus rundown.
  - **Shutdown:** `Collector.DisposeCoreAsync` requests the disk stop before it awaits the worker, next to `StopNetworkCaptureAsync()`, and awaits it with the network stop. A stuck hardware provider therefore cannot delay it. It is not part of the network stop, so the final connections drain never waits for it. Disposal first closes the lifecycle task to new starts: a queued start is dropped, and a start already running finishes and is then stopped. The new field is null-guarded, because the shutdown fixtures build `Collector` with `GetUninitializedObject` (`CoolingShutdownTests`).
  - **Deadline:** the existing 5 s `HardwareStopBudget` bounds only the storage-health join (and the scan loop's join, if Cost moves the scan there), not the disk stop, so the collector adds no timeout of its own. The callers bound the stop: UiSession's provider deadline (5 s on Quit, 3 s at sign-out) and `--diagnose`'s 3 s shutdown wait, which fails the run when exceeded. A normal stop, including one queued behind a start, must therefore finish within 3 s. A stop still running at the deadline appears as the outstanding "hardware provider" stage in `collector_stop_timeout`. The orphan cleanup before the next disk-session start removes any session it left.
  - Before starting, it stops sessions named `DeadWare-Disk-<pid>-*` or `DeadWare-<pid>-*` whose PID is not a running DeadWare. These are orphans from a crash.
- **Gating:** `CollectorOptions` gains `ProcessDiskTracing`, default false. It only permits a disk session and never changes after the collector is built.
  - Only a build that includes the Disk columns sets it. There, `UiSession` passes `ProcessDiskTracing: Collector.IsElevated`, and `--diagnose` sets it when elevated, unless `--no-processes` or `--no-etw` is given (see Diagnostics).
  - When it is set, `EtwDisk` runs only while the monitoring flag is on (see Switch under Process scan). On the first pass, and whenever the flag changes, the pass queues an EtwDisk start (flag on) or stop (flag off) on the serialized pool task. So an elevated user who turns monitoring on gets Disk without restarting.
  - Tests that build real collectors leave it false, so they never open a kernel session, even on CI's elevated runner.
- **Standard users:** no session is started, and the Disk columns are hidden.
- **Ship gate:** CI must credit at least 90 % of 256 MB of `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH` writes, at the chosen `BufferSizeMB`, to the test's own `(Pid, Generation)`. That share alone decides the gate; the share of −1 events is recorded and never gates.
  - **If it falls short,** 0.13.0 ships without Disk for every user, elevated or not:
    - The table and the column picker leave out DISK, DISK READ and DISK WRITE. They are never shown disabled as "(administrator)".
    - A standard user's banner reads "Network needs administrator", and the footer leaves out the Disk definition.
    - Nothing sets `ProcessDiskTracing`, so no `EtwDisk` session or orphan cleanup runs, even when DeadWare is elevated.
    - The Disk keys stay among the codec's known keys and in the default, so saved lists keep them. A normalizer that knew only the offered columns would drop them, as `WidgetGraphSelection.Normalize` drops keys it does not know, and `UiSession.Start` saves settings on every launch, so 0.13.1 would then not show Disk by default.
    - The 0.13.0 notes say per-app I/O ships now and per-app Disk follows in 0.13.1, which adds DeadWare's own thread-to-process map built from Thread events.
  - **The same test gates both releases and stays in CI.** While a build leaves Disk out, it only records the share. In any build that includes the Disk columns it fails below 90 %, and also fails, instead of skipping, on a CI run that is not elevated and so has no share. If the thread map also falls short, Disk stays out and the plan stops for the user.

### Publication

- The collector publishes an immutable `ProcessActivity` as a new init-only `Reading.ProcessActivity`, marked `[JsonIgnore]`. It is set on all three publish paths: normal, failed pass and stopped. On the failed-pass and stopped paths it is `ProcessActivity.Unavailable(<exception type>)`, for the exception that path caught, while the monitoring flag is on, and `ProcessActivity.Off` while it is off. It holds:
  - the sequence, timestamps, interval and processor count;
  - per-process samples with nullable values;
  - per-column availability and status;
  - the omitted count and the omitted processes' summed memory and exact deltas;
  - `None`, `Off` and `Unavailable(status)` instances.
- **Aggregation:** an app's value is the sum of its known members. It is null only when no member is known, matching the storage total rule.
- **Error isolation:** process work runs in two collector-local try/catch blocks, and neither encloses the publication, so a failure yields `ProcessActivity.Unavailable(<exception type>)` and never gaps the other metrics.
  - The scan and the tracker update run in the first, just before `lock(publicationGate)` (inside the loop when Cost moves the scan there).
  - The disk take and then the network and disk joins run in the second, inside the lock, right after the poll's only `etw.TakeSnapshot` (the network join needs `capture.Processes`) and before the `Reading` is built. It is skipped when the scan failed, and the activity is that `Unavailable`.
  - The `Reading` is then built and published as usual. Neither block wraps `new Reading`, `History.Add` or `SampleAvailable`, so a process failure never reaches the pass's own catch, which would publish an empty Snapshot.
- **Unchanged:** `Reading.Processes`, `EtwNetwork`, usage-history.db (v1), `AlertKind`, `Snapshot.Values` and the six-hour graph cache.
- **Diagnostics:**
  - `--diagnose` builds its own `CollectorOptions` and never reads settings.json, so the saved Per-process monitoring switch does not apply to it. The app itself is unaffected: it follows the saved switch, which is off by default.
    - It sets `ProcessMonitoring`, so it runs the process scan, unless a new `--no-processes` flag is given.
    - In a build that includes the Disk columns, it sets `ProcessDiskTracing` when elevated, unless `--no-processes` or `--no-etw` is given. `--no-etw` already isolates per-app tracing.
    - The README's `--diagnose` paragraph lists `--no-processes`.
  - Each sample carries a `ProcessSummary`: process count, per-column availability and status, network and disk join misses (counted separately), and unattributed disk events.
    - It is a new init-only `Reading.ProcessSummary`, serialized like `Reading.Storage`, not `[JsonIgnore]` like `ProcessActivity`.
    - The collector sets it on all three publish paths, only when `DiagnosticTimings` is on, like `ProviderMilliseconds`.
    - With `--no-processes` it reports Off.
  - Timing keys: `windows/processes` and `processes/join` appear whenever the scan runs. `etw/disk` and `open/etwdisk` appear only when the disk session ran (elevated, in a build that includes the Disk columns, without `--no-processes` or `--no-etw`).
  - No per-process names or paths are written for the new data, and the existing network list is unchanged.

## History (0.14.0)

- **`ProcessMinuteFolder`** follows `UsageMinuteFolder`'s breaks and segments, but sums the tracker's exact deltas per app: CPU time, I/O read bytes, I/O write bytes and memory × Δt.
  - Each publication's per-process amounts are added to their app, keyed as the live table is (the image name under `OrdinalIgnoreCase`), so case variants rank and are stored as one app.
  - Each minute also stores, once and not per app, its covered seconds (the summed Δt of its scans that produced deltas) and the processor count N. For one minute, an app's memory is Σ(memory × Δt) ÷ covered seconds, its CPU % is 100 × CPU time in seconds ÷ (N × covered seconds), and its I/O rate is (read + write) ÷ covered seconds.
  - At each minute's close it keeps the union of the top 10 apps by CPU time, by that memory and by I/O read + write (at most 30 apps). A zero value never qualifies, and ties break by name.
  - Everything else is stored as one row. Processes with an empty name (shown live as "Unknown application"), and each publication's omitted sums, go into Everything else, so it remains the total of everything not listed. PID 0 never reaches the folder, so idle time is never counted.
    - From 0.14.1, a name that reads as Everything else (ignoring case), or that holds `\`, `/` or `*`, which Windows file names never contain, goes into Everything else too. Each unpaired surrogate is saved as U+FFFD, the form SQLite's UTF-8 stores, so names that differ only there are one app (the live table still lists them apart). A name cut at 256 characters is trimmed again, so a saved name is stable (`SavedName` returns it unchanged) and the store accepts it. `ProcessHistoryStore` rejects a batch with any other name (`ProcessHistory.SavedName`).
  - Partial minutes keep their rows with `complete = 0`, and their stored covered seconds show how much of the minute was observed. Minutes with zero covered time are skipped.
- **`ProcessHistoryStore`** writes `process-history.db`, its own file with its own `application_id` and `user_version` 1.
  - **On open:** the file is inspected read-only with an integrity check first, as Connections history does.
  - **Durability:** WAL mode, `synchronous FULL` and an 8 MiB journal guard.
  - **Limits:** a 128 MiB cap with whole-day eviction (today is never evicted) and 30-day retention.
  - **Names:** a names table referenced by integer ids. It is unique on `ToUpperInvariant(name)` (the form `OrdinalIgnoreCase` compares) and keeps the first stored casing for display. Different casings still arrive across minutes and restarts, and this key gives them one id. It does not use SQLite `NOCASE`, which folds only ASCII and would split non-ASCII names that the live table merges.
  - **Restarts within a minute:** the first segment wins the minute, and later segments only clear `complete`.
  - **Reads:** at most 31 days, with `LIMIT` 1000 or less.
  - **Unreadable files:** newer, foreign or corrupt files are preserved and reported, never overwritten.
- **`ProcessHistoryService`** copies `UsageHistoryService`: a 120-slot bounded channel with `TryWrite`, drop counting, a 15 s flush and `Fail`.
  - It enqueues only the `ProcessActivity` and its sequence.
  - It records only while both Per-process monitoring and Record usage history are on.
  - It drains within Quit's 20 s history cap and sign-out's 3 s budget, null-guarded like Connections.
- **Expected size:** each minute stores at most 31 rows (the 30-app cap plus Everything else), so the worst case, recording around the clock with 30 apps listed every minute, is about 2 MB per day.
  - The schema depends only on that cap, not on a measured union size, so nothing is measured on the user's PC before it freezes. Typical union sizes stay an estimate: `--diagnose` collects at most 60 samples and CI never runs it.
  - A Core store test writes one day at the cap (1,440 complete minutes, each with the same 30 app names plus Everything else and full-width counter values) and asserts the files stay under 4 MiB, as ThermalStore's one-day size test does. So 30 days plus today (31 × 4 MiB = 124 MiB) fit the 128 MiB cap.
  - The 128 MiB cap with whole-day eviction bounds the file whatever the real union sizes turn out to be.

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

- Program paths are never stored or logged; Open file location and End task read them on demand.
- Process names exist in memory for the live view. In 0.14.0, app names are saved for 30 days in `process-history.db`, as Usage history already does for network per app.
- Names never enter `Snapshot.Values` or the graph cache.
- Diagnostics get only counts and timings for the new data.
- The README privacy section is updated in each release.

## Testing

- **Core (xUnit):**
  - The parser, against synthetic buffers: PID 0 (its zero-length name passes the checks and the entry is dropped), malformed offsets, a last entry ended by a zero `NextEntryOffset`, odd name lengths, the entry cap.
  - The tracker: the first scan (rates null, Memory set), the 12.5 % CPU case (one thread busy for the whole interval on eight logical processors reads 12.5 %), PID reuse, crediting new processes, Δt edges, the 4,096 cap (ranking order, an all-null first scan, omitted sums), a bound over 10,000 create/exit cycles.
  - The network and disk joins: match, 0, null and the miss guard, plus the disk 2 s warm-up, a lossy take and the unattributed share.
  - `ProcessAttribution.Reconcile(index)`: a matching Generation is kept without a native read, and a PID the index lacks or lists under another Generation (older or newer) is re-read through the constructor's resolver.
  - Grouping and sums, including case variants: `Explorer.EXE` and `explorer.exe` give one live row (first casing shown, row identity stable) and one history name id, and so do two casings of a non-ASCII name.
  - The calm-order row planner (moves only rows outside the longest increasing subsequence).
  - The disk rolling window.
  - The `EndTaskPolicy` refusal matrix:
    - The Windows-folder plus service-account rule. A `C:\WINDOWS` folder matches `C:\Windows\System32\…` (refused under a service-account SID, allowed under the user's own SID), and `C:\Windows.old\…` and `C:\WindowsAzure\…` do not count as inside `C:\Windows`.
    - A System32 svchost whose token user is the signed-in user (refused).
    - The driver-store exception: a LocalSystem image under `System32\DriverStore\FileRepository\` is allowed when elevated, and one directly in System32 is refused.
    - Unreadable facts: an unreadable token user with the image inside and outside the Windows folder, an unreadable image path, an unknown critical state or protection level, and a denied terminate open.
  - Settings: both defaults, `ProcessColumns` subsets round-tripping, and its normalization (null, unknown and duplicate keys, out of order, empty, and administrator-only keys kept while not elevated).
  - 0.14.0: the minute folder (at most 30 apps plus Everything else a minute) and the store, including the one-day size test at that cap.
- **App:**
  - Collector tests with a fake and a throwing process source: CPU and memory keys survive, and switching off takes effect within one poll. Switching on starts `EtwDisk` only when `ProcessDiskTracing` is set, without rebuilding the collector.
  - `EtwDisk` through test seams, including a stuck hardware worker that still stops the disk session (`CoolingShutdownTests`' stuck-worker fixture).
  - `--diagnose` builds options that run the scan (Off with `--no-processes`), and its JSON, serialized from a reading whose `ProcessActivity` holds named processes, contains a `ProcessSummary` that is not Off and none of those names.
  - Actions through the native-control and confirm hooks.
- **UI checks:**
  - A Processes-page phase after Storage: the off state, rows, expansion, the column picker, the hidden admin columns, disabled picker entries and banner from a fixture reading with `Elevated` false, the full column set with `Elevated` true, and tooltips.
  - Shell checks for 12 rail entries and Storage + 1 == Processes.
  - The settings switch.
  - 0.14.0: the Application resources card and click-a-moment in the Usage history checks.
- **Preview:** add the `processes` route and the missing `storage` route to `tools/OverviewPreview/Program.cs`, and render `processes` at 900 × 640 as well as 1220 × 900.
  - Per-process monitoring is off by default, so the fixture turns it on in the preview's `UserSettings`, as `ShowCpuCoreUsage=true` does for per-core CPU. In `UpdateSample` it sets a synthetic `Reading.ProcessActivity` with a few apps, one of them multi-process.
  - Preview pages are never loaded, so ProcessesPage renders once from `session.Current` in its constructor, as StoragePage does.
  - The check requires the Processes table to be at least 70 px tall at 900 × 640 and to show the fixture's rows. The off state is covered by the UI checks, not the preview.
- **Real Windows in CI** (GitHub's Windows runner, which runs elevated; this is the measurement step, and nothing runs on the user's PC). All of these tests skip when `GITHUB_ACTIONS` is not `true`, so a local `dotnet test` never writes 256 MB or opens a kernel session. The checks also skip when the test process is not elevated. The measurement tests, the disk-attribution test among them, never skip on GitHub: they check elevation in their own body, because xUnit 2.9.3 decides a skip in the test's attribute before the body runs (as `ElevatedFactAttribute` does), and a skipped test can neither write its file nor fail. On a run that is not elevated, each measurement test measures nothing, writes only `elevated: false` to its file and passes, except that the disk-attribution test fails in a build that includes the Disk columns (see Ship gate).
  - **Checks, which fail the build:**
    - The struct offsets: the scan contains the test's own PID with `Path.GetFileName(Environment.ProcessPath)` (ignoring case), PID 4 is System, and the test's own CPU time never decreases.
    - After a re-scan that discards reused PIDs, every process `NativeProcessIdentity.Read` returns has an equal Generation. Image names are equal too, ignoring case, unless `Read` used its `PID n` fallback because Windows gives no image path for that process. Only System (PID 4) and the image-less Registry, Memory Compression and Secure System processes may get that fallback; a fallback on any other process fails the test.
    - Orphan-session cleanup.
    - End task on real processes, ending only the test's own:
      - With the Confirm hook answering No, the real fact reader and `EndTaskPolicy` refuse PID 4, csrss, lsass, services and every svchost, plus dwm and MsMpEng when they are running. The facts read for csrss report it as critical and protected.
      - A helper that the test starts as LocalSystem from its own temp folder, through a one-shot scheduled task, stands in for a third-party service and for another account's process. It is classified Allowed. With the debug privilege disabled on the test's own token (`System.Diagnostics.Process` may have enabled it), opening it for termination is denied. End task's own open, which enables the privilege for that one call, succeeds and ends it.
      - All other End-task probes use the test's own child processes.
    - A standard-user pass, because the runner is elevated and `Collector.IsElevated` is fixed for each process. The test starts a helper child process from a restricted copy of its own token (Administrators deny-only, privileges removed, medium integrity), so the child is not elevated.
      - The child's scan must list every process that the elevated scans taken just before and just after it both hold under the same Generation. That includes PID 4, session-0 services and protected processes such as csrss.exe.
      - Each process's CPU time and transfer counts must lie between its two elevated values. Its private working set must be non-zero wherever both elevated scans show a non-zero one.
      - End task, run from the child against an elevated process the test started, must report Needs administrator and leave that process running.
  - **Measurements, which record their numbers instead of asserting a threshold,** so a result past a threshold leads to a decision, not a red build. The one exception is the disk-attribution share in a build that includes the Disk columns, which fails below 90 % (see Ship gate).
    - 240-scan timing (p50, p95, buffer, process and thread counts), which decides every poll versus the loop described under Cost.
    - Disk attribution: the share of 256 MB of `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH` writes credited to the test's own `(Pid, Generation)`. This alone decides the disk gate (at least 90 % at the chosen `BufferSizeMB`). The share of −1 (unattributed) events is recorded and never gates.
    - Events lost and the longest run of lossy takes, taking every 0.5 s with an `EtwNetwork` session running, during the 256 MB write and during 30 s of 4 KiB unbuffered random writes from 32 concurrent requests. This decides whether `BufferSizeMB` grows past TraceEvent's default (see Disk column).
  - **Where the numbers go:** the measurement tests write JSON to the repository's `artifacts/measurements/` folder, one file per test, and append their values to the job summary (`GITHUB_STEP_SUMMARY`). They record whether the run was elevated; scan p50 and p95, the returned buffer size, and process and thread counts; the attribution share and the share of −1 events at each `BufferSizeMB` tried; and events lost and the longest run of consecutive lossy takes. `dotnet test` at its default verbosity does not print a passing test's output, so `build.yml` uploads that folder as a `DeadWare-measurements` artifact with `if: always()`.
  - **Recording the decisions:** before the page tasks start, the PR description links that run and records the numbers and what they set: every poll or the loop (p95 above 15 ms means the loop), `BufferSizeMB`, and the disk gate (at least 90 %). docs/validation.md repeats them in the 0.13.0 notes. A missing attribution share, for example from a run that was not elevated, counts as a failed gate, so Disk never ships unproven.
- **Pending by eye** (listed in docs/validation.md and never claimed as verified):
  - Task Manager Details comparison.
  - End task against real third-party services (including a driver-store service such as NVIDIA Display Container) and another signed-in user's process, as a standard user and as administrator.
  - Open file location in Program Files, WindowsApps and paths with spaces and commas.
  - Behaviour at 0.5 s and 10 s polling, during sleep, and on Quit during an elevated copy.

## Phasing and release

- **0.13.0:**
  1. Measurement tasks first: the Core types, `WindowsProcesses`, publication, `ProcessSummary`, `EtwDisk`, and the real-Windows CI tests that set the loop placement, `BufferSizeMB` and the disk gate. Their numbers and decisions are recorded before step 2 (see Testing and Workflow).
  2. Then the Processes page, actions, settings and preview.
  3. Then README, CHANGELOG (including the "planned for 0.12" answer), `docs/releases/v0.13.0.md`, validation notes and the version.
- **0.13.1**, only if the disk gate failed: Disk columns via DeadWare's own thread-to-process map. They are shown only when the same CI test credits at least 90 % of the 256 MB write to the test's own `(Pid, Generation)`. If it still falls short, Disk stays out and the plan stops for the user.
- **0.14.0:** the minute folder, store, service, the Application resources card and click-a-moment, the updated text for the usage-history switch (see Settings), with release docs.
- **Workflow:** as recent releases, a feature branch with a PR to `main`, CI as the gate, and a release when the user says go.
  - Recent releases used `claude/*` branches, and `build.yml` runs on push only for `main`, `Dev`, `feat/**` and `fix/**`, so on such a branch only a PR runs CI. The 0.13.0 PR is therefore opened as a draft with the first pushed task, not in the last task as in the Storage plan.
  - Step 2 starts only after a CI run on that PR has finished step 1's real-Windows tests and the PR description records their numbers and what they set: every poll or the loop, `BufferSizeMB`, and Disk in 0.13.0 or in 0.13.1 (see Recording the decisions under Testing). `build.yml` cancels a run in progress when a newer push arrives, so page work is not pushed before that run finishes.
  - The PR is marked ready for review once step 3 is done.
  - The implementation plan is written to `docs/superpowers/plans/2026-11-05-processes.md` and follows the Storage plan, `docs/superpowers/plans/2026-11-03-storage.md`, as its template: Goal, Architecture, Global Constraints and Review Focus, then tasks of five test-first steps (failing tests, verify failure, implement, verify pass, commit).

## Out of scope

- Per-process GPU.
- Processes cards on the Overview or the widget.
- Alert rules on processes.
- Ending process trees.
- Changing priority or affinity.
- Command lines and services views.
- Per-process disk or network history (network per app is already in Usage history's Applications).
- Feeding `EtwNetwork`'s identity from the process scan, in place of its native lookups and 5 s reconcile. `EtwNetwork` keeps its 5 s reconcile through `NativeProcessIdentity.Read`, so its "PID n" fallback names, which are Usage history's application keys, stay as they are. Only `EtwDisk`'s reconcile reads the scan index (see Disk column).
- Excluding the existing `Reading.Processes` from `--diagnose`.
