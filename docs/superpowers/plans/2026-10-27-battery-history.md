# Battery History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Execute inline under the user's standing preference.

**Goal:** Preserve battery-percentage, charging and battery-saver history for the lifetime of this device, with an independent chart/window.

**Architecture:** Add native Windows battery status to existing collector publications. A bounded background service samples minute observations and state changes into an independent SQLite store without expiry. A dedicated themed WPF page/window reads bounded summaries and exports original observations.

**Tech Stack:** Existing .NET 10, WPF, Microsoft.Data.Sqlite and xUnit; GetSystemPowerStatus P/Invoke. No new package dependencies.

**Spec:** `docs/superpowers/specs/2026-10-27-battery-history-design.md`

## Global Constraints

- Keep saved observations indefinitely; no automatic expiration/deletion.
- Preserve unavailable values and real gaps; never label unknown saver state as disabled.
- Source is shared through GitHub; readings/settings stay local to this device.
- Writes, queries and CSV export stay off the collector/UI thread; queue/chart results are bounded.
- Reuse the collector, existing theme and lifecycle, and bounded three-second sign-out.

## Review Focus

- Unknown battery flags/percentage/saver state must not produce fictitious charge or charging periods (Task 1).
- Sleep, restart, clock jumps and queue loss must break the trace (Tasks 1/2).
- Year/All queries must keep the earliest data and bound memory (Task 2).
- A storage failure must surface without interrupting the other monitors (Tasks 2/3).
- Hidden views and sign-out must not introduce UI continuations or lose normal-Quit batches (Tasks 2/3).

### Task 1: Native battery observation and recording policy

**Files:** Create `src/DeadWare.Core/BatteryHistory.cs`, `src/DeadWare.App/WindowsBattery.cs`, `tests/DeadWare.Tests/BatteryHistoryTests.cs`; modify `src/DeadWare.App/Collector.cs`.

**Interfaces:** `BatteryReading(double? Percentage, BatteryPowerState PowerState, bool? SaverEnabled)`; `BatterySample(DateTimeOffset Timestamp, double MonotonicSeconds, BatteryReading Reading, string Segment)`; `BatteryRecorder.Observe(...)` yields sparse samples. `WindowsBattery.Read()` supplies normalized native flags; `Reading.Battery` carries the optional immutable observation.

- [x] Write tests for 0/100/unknown/no battery, charging/on-AC/discharging, saver on/off/unknown, one-minute cadence and immediate state changes, gaps/restart and clock jumps.
- [x] Run the focused tests and observe missing-feature failures.
- [x] Implement normalization, sparse sampling and native adapter. Keep battery failures independent of the hardware provider.
- [x] Run focused tests; expected PASS. Run a short native diagnostic; compare percentage/state with this Surface.
- [x] Commit the tested observation layer.

### Task 2: Permanent SQLite history and background worker

**Files:** Create `src/DeadWare.Core/BatteryHistoryStore.cs`, `src/DeadWare.App/BatteryHistoryService.cs`, `tests/DeadWare.Tests/BatteryHistoryStoreTests.cs`, `tests/DeadWare.App.Tests/BatteryHistoryServiceTests.cs`.

**Interfaces:** `BatteryHistoryStore.Initialize/Append/Read/ExportCsv`, `BatteryHistoryView` with bounded `BatteryHistoryBucket` results; `BatteryHistoryService.Start/TryRecord/Configure/ReadViewAsync/ExportCsvAsync/StopAsync`.

- [x] Write close/reopen, machine partition, multi-year oldest retention, bound/min/max/charging/saver summaries, CSV, corrupt/newer DB and lock-failure tests. Write service cadence, recording toggle, queue-gap and final-drain tests.
- [x] Run focused tests; expected missing-feature FAIL.
- [x] Implement versioned append-only schema with UTC timestamps, segment, percent, power state and nullable saver. Limit queries with SQL time buckets; stream CSV; do not read lifetime data into memory. Serialize service operations off-thread and flush on timer/Quit.
- [x] Run focused tests; expected PASS; measure multi-year fixture size/query behavior.
- [x] Commit the tested persistence layer and synchronize GitHub.

### Task 3: Dedicated chart, page/window and lifecycle

**Files:** Create `src/DeadWare.App/BatteryHistoryPage.cs`, `src/DeadWare.App/BatteryHistoryChart.cs`; modify `UiSession.cs`, `Windows.cs`, `Monitoring.cs`, `StartupLayout.cs`; add app UI tests/probe and window/settings regressions.

**Interfaces:** `BatteryHistoryPage(UiSession)` uses `session.Battery`; `BatteryHistoryChart.SetData(view)` draws percentage ranges and charging/saver markers. Detached key `batteryhistory` restores a dedicated window.

- [x] Add failing window-key/settings and UI/worker-shutdown coverage before integration.
- [x] Add Battery history navigation, current percentage/power/saver status, Day/Week/Month/Year/All selection, recording control, CSV export, storage/status and open-window action. Show mixed charging/saver coverage honestly in aggregated views; connect only contiguous unaggregated same-segment observations.
- [x] Subscribe/start/drain the battery worker alongside existing history services, including the bounded sign-out path.
- [x] Run focused and full solution tests; expected PASS. Render actual controls offscreen in Midnight/Daylight at narrow/wide sizes and empty/day/multi-year data.
- [x] Commit the tested UI/integration.

### Task 4: Versioned delivery and GitHub handoff

**Files:** Update `README.md`, `CHANGELOG.md`, `Directory.Build.props`, `scripts/publish.ps1`, `docs/validation.md`; create `docs/releases/v0.5.0.md` and a short cross-computer workflow document.

- [x] Review full change against the approved design inline. Fix material issues with regression proof.
- [x] Run solution tests/build and `scripts/publish.ps1 -Version 0.5.0`; expected zero failures and a complete portable ZIP/checksum.
- [x] Verify archive version/content, run read-only collector diagnostics, and retain validation evidence without private device readings in Git.
- [x] Commit and synchronize all source/docs/tests to `feat/battery-history`, create and attach a pull request, and inspect CI. Keep main history intact; provide the tested package and links.

## Inline self-review

All requirements map to Tasks 1–4. UI and worker consume the same immutable battery observation; current values come from fresh collector data, while historical views come from the independent store. Display rollups do not change retention. User approval to proceed with the complete design supplies execution authorization; no further routine plan confirmation is needed.
