# Usage History Implementation Plan (0.7.0)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Execute inline under the user's standing preference.

**Goal:** Keep day, week and month history of CPU, GPU, RAM and network usage plus per-application network totals in an independent local store with its own page and window.

**Architecture:** A bounded background service folds the collector's existing immutable publications into per-minute metric rows and per-minute application rows, written to a new versioned SQLite store with 400-day retention and whole-day eviction at a size cap. A themed WPF page and detached window read bounded SQL buckets and totals and export CSV. The six-hour live cache, Cooling Health, Battery history and Connections are untouched.

**Tech Stack:** Existing .NET 10, WPF, Microsoft.Data.Sqlite and xUnit. No new package dependencies.

**Spec:** `docs/superpowers/specs/2026-11-02-usage-history-design.md`

## Global Constraints

- New store only; no schema change to `live-graph-history.db`, `cooling-health.db`, `battery-history.db` or `connections.db`.
- Minute rows carry sample counts and a completeness flag; incomplete minutes and absent minutes are never drawn as continuous data or counted as zero traffic.
- ETW-unavailable minutes become capture-gap rows, never zero application traffic.
- Writes, reads and CSV stay off the collector and UI threads; chart and total results are bounded in SQL.
- Reuse the 15-second flush, normal-Quit drain, 20-second history deadline and 3-second sign-out path; a store failure surfaces without stopping live monitoring.
- Source is shared through GitHub; usage rows stay local and out of diagnostics by default.

## Review Focus

- A minute that straddles a polling-interval change or a restart must not be marked complete (Task 1).
- Same-name processes with different PIDs must sum, and a PID reuse must not double count (Task 1).
- Whole-day eviction at the cap must never remove the current day or leave a partial oldest day (Task 2).
- Month reads must stay bounded and preserve gaps inside buckets (Tasks 2/3).
- Hidden page, closed window and sign-out must not leave UI continuations or lose the final batch (Tasks 3/4).

### Task 1: Minute aggregation and application ledger

**Files:** Create `src/DeadWare.Core/UsageHistory.cs`, `tests/DeadWare.Tests/UsageHistoryTests.cs`.

**Interfaces:** `UsageMetricMinute(string Metric, string? HardwareId, DateTimeOffset Minute, double Average, double Minimum, double Maximum, int Samples, bool Complete, string Segment)`; `UsageApplicationMinute(string Name, DateTimeOffset Minute, long Received, long Sent, string Segment)`; `UsageMinuteBatch(Minute, Metrics, Applications, bool CaptureGap, Segment)` carries the capture-gap flag for the whole minute; `UsageMinuteFolder.Observe(Snapshot sample, IReadOnlyList<ProcessTraffic> processes, bool captureAvailable, string? gpuId)` returns the previous minute when a sample starts a later one; `UsageMinuteFolder.Flush()` closes the open minute as incomplete. A batch keeps the segment its samples belonged to, so the store can tell a restart within the same minute apart and merge the two rows (Task 2).

- [x] Write tests for one-second and ten-second cadence completeness, partial first/last minutes, interval change mid-minute, null readings leaving Samples unchanged, restart/clock-jump segment boundaries, GPU id switching, same-name PID summing, PID generation reuse, name normalization/truncation and ETW failure minutes.
- [x] Run the focused tests and observe missing-feature failures. (The cloud session could not install the .NET SDK, so the first run is the pull request's Windows CI.)
- [x] Implement the folder as a pure core type with no timers or threads; the metric list reuses the `cpu`, `cputemp`, `gpu`, `gputemp`, `memory` identifiers and splits `network` into `download` and `upload`.
- [ ] Run focused tests; expected PASS (CI on the pull request).
- [x] Commit the tested aggregation layer.

### Task 2: Versioned SQLite store with retention and eviction

**Files:** Create `src/DeadWare.Core/UsageHistoryStore.cs`, `tests/DeadWare.Tests/UsageHistoryStoreTests.cs`.

**Interfaces:** `UsageHistoryStore(string path, long maximumDatabaseBytes = 256 MiB)` with `Initialize`, `Append(string machine, IReadOnlyList<UsageMinuteBatch> batches, DateTimeOffset now)`, `ReadMetric(machine, metric, hardwareId, from, to, maxPoints)` returning `UsageHistoryView` of aligned `UsageHistoryBucket(Start, End, Average, Minimum, Maximum, Minutes, CompleteMinutes, Samples)` plus sample-weighted `Statistics`, `ReadApplications(machine, from, to, maxNames)` returning `UsageApplicationView` with per-name totals, the `Other` remainder, capture-gap and recorded minute counts, `EarliestRetained(machine)`, `StorageBytes`, `ExportMetricsCsv` (minute rows) and `ExportApplicationsCsv` (per-name totals). A minute already stored from a different segment merges (weighted average, min/max, summed bytes) and is marked incomplete; the same segment repeated is ignored, so a retried batch cannot double count. Capture gaps live in their own `capture_gaps` table.

- [x] Write tests for initialize/close/reopen, newer `user_version` refusal, machine partitioning, 400-day retention, whole-day eviction at a small test cap that keeps today and leaves no partial oldest day, bucketed Day/Week/Month reads with gap preservation and bounded point counts, application totals ordering, the 200-name remainder, capture-gap counts, and CSV content with formula neutralization.
- [x] Run focused tests; expected missing-feature FAIL. (First execution is the pull request's Windows CI; see Task 1.)
- [x] Implement `metric_minutes`, `application_minutes` and `capture_gaps`, each keyed on machine, key and minute, with `PRAGMA user_version=1`, WAL, the bounded journal and the same open/size-check pattern as `GraphHistoryStore`. Bucket in SQL by integer division of the minute; stream CSV rows. On `SQLITE_FULL`, evict the oldest whole day across all tables and retry, never the day being recorded.
- [ ] Run focused tests; expected PASS (CI on the pull request). Measure a synthetic 13-month fixture's file size and Month query time and record them in the validation notes (needs a Windows machine with the SDK).
- [x] Commit the tested persistence layer and push the branch.

### Task 3: Background service and lifecycle integration

**Files:** Create `src/DeadWare.App/UsageHistoryService.cs`, `tests/DeadWare.App.Tests/UsageHistoryServiceTests.cs`; modify `src/DeadWare.App/UiSession.cs`, `src/DeadWare.Core/Monitoring.cs` (add `UsageRecording` to `UserSettings`, default true).

**Interfaces:** `UsageHistoryService(string path, string machineKey, TimeProvider?, TimeSpan? flushInterval)` with `Start`, `TryRecord(Reading)`, `Configure(bool recording)`, `ReadMetricAsync`, `ReadApplicationsAsync`, `ExportCsvAsync(path, applications, from, to)`, `Status`, `Failed`, `StopAsync`. GPUs are recorded for every unique model in the publication, keyed by model name (`UsageHistoryService.Gpus`), so a GPU enumeration-order change does not split history and the page can show whichever GPU the dashboard selects. Capture availability comes from the publication's connection sample, falling back to whether any process traffic was attributed.

- [x] Write tests for enqueue-only `TryRecord`, minute completion through the folder, flush timer and batch bounds, recording toggle leaving the open minute incomplete, queue-loss accounting in `Status`, store failure reported without throwing into the collector, and `StopAsync` draining the final batch within the history deadline.
- [x] Run focused tests; expected FAIL. (First execution is the pull request's Windows CI.)
- [x] Implement the service mirroring `GraphHistoryService` and `BatteryHistoryService`; subscribe it to `Collector.SampleAvailable` in `UiSession`, start it with the other services and add it to the Quit drain list and the sign-out path. The uninitialized-session test fixtures set the new `Usage` field.
- [ ] Run focused tests plus the existing shutdown tests; expected PASS (CI on the pull request).
- [x] Commit the tested service and integration.

### Task 4: Usage history page, chart and window

**Files:** Create `src/DeadWare.App/UsageHistoryPage.cs`, `src/DeadWare.App/UsageHistoryChart.cs`, `tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs`; modify `src/DeadWare.App/Windows.cs`, `src/DeadWare.App/UiSession.cs`, `src/DeadWare.Core/StartupLayout.cs` (allow key `usagehistory`), the Settings page for the recording checkbox.

**Interfaces:** `UsageHistoryPage(UiSession)` uses `session.Usage`; `UsageHistoryChart.SetData(UsageHistoryView)` draws the average line and min/max band with gaps; `UsageHistoryWindow` restores under key `usagehistory`.

- [ ] Add failing window-key, settings and offscreen UI tests before integration: Day/Week/Month selection and date stepping, GPU selection following the dashboard, six metric charts, application table sorting and search, remainder row, capture-gap notice, storage and earliest-retained text, recording checkbox, both CSV exports, and the detached window's placement restoration.
- [ ] Implement the page with the existing theme, glass and `HistoryChart` primitives; keep all reads asynchronous with stale-result rejection when the range or GPU changes; show "no history yet" and gap notices honestly.
- [ ] Run focused and full solution tests; expected PASS. Render offscreen in Midnight, Daylight, Blackout and High Contrast at narrow and wide sizes with empty, one-day, one-month and gap-heavy fixtures; inspect the PNGs under an ignored `artifacts/ui-usage-history/`.
- [ ] Commit the tested UI.

### Task 5: Versioned delivery and GitHub handoff

**Files:** Update `README.md`, `CHANGELOG.md`, `Directory.Build.props`, `docs/development.md`, `docs/validation.md`; create `docs/releases/v0.7.0.md`.

- [ ] Review the full change against the approved design inline; fix material issues with regression proof.
- [ ] Record for at least an hour on a real machine and compare the Day view against the live graph for the same period; state in validation notes that this is a software check, not a hardware soak.
- [ ] Run solution tests/build and `scripts/publish.ps1 -Version 0.7.0`; expected zero failures and a complete portable ZIP, checksum and manifest.
- [ ] Update README's feature list, a new **Usage history** section, the download links and publish example to 0.7.0, and the development guide's current-version references.
- [ ] Commit and push the feature branch, open a pull request against `main`, inspect CI, and report the pull-request link with the verification result.

## Inline self-review

Every spec requirement maps to a task: aggregation and application identity (1), storage, retention, eviction and bounded reads (2), worker and shutdown (3), page, window, settings and exports (4), delivery and documentation (5). Live values keep coming from fresh collector data; historical views come only from the new store. Nothing modifies an existing database. Implementation waits for the user's approval of the spec.
