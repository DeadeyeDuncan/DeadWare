# Cooling Health Implementation Plan

> **For agentic workers:** Use focused implementation ownership and independent review. Steps use checkbox tracking; the approved design is the authority.

**Goal:** Persist local thermal history and flag sustained changes against an explicit fixed reference without interfering with monitoring.

**Architecture:** The collector publishes into a bounded nonblocking queue. A background service projects typed CPU/GPU readings into minute windows, stores them in SQLite and evaluates fixed references. WPF queries run off the UI thread; the dashboard owns a separate Cooling Health page, and optional alerts use the existing tray.

**Tech Stack:** Existing .NET 10/WPF; Microsoft.Data.Sqlite 10.0.10 (verified published package); xUnit and native-free WPF fixtures.

**Spec:** ../specs/2026-10-25-cooling-health-proposal.md

## Global Constraints

- No Computer Use, second sensor poller, automatic extended sensors, fan control or stress tests.
- Missing readings remain unavailable. Never diagnose a failed component or promise remaining lifespan.
- Keep the live cache, tray behavior, startup and saved layouts compatible.
- Local data only; no process/network data or serial numbers in thermal exports.
- Defaults: 30 days of minute windows, 90 days of hourly summaries, 256 MiB database cap plus bounded journal. Retention selectable as 7/30 detailed days and 30/90 summary days.
- References are explicit immutable copies, at most seven days of eligible minute windows, with prior references retained (latest ten per device/configuration). New setup creates a new configuration identity.
- Runtime DB/analysis faults must never escape into the collector callback or stop live monitoring.

## Review Focus

- Sleep, missing samples and wall-clock changes split windows and comparison runs; no connected graph across gaps.
- Valid zero fan speed is preserved; power/temperature/load validity cannot be inferred from unit labels alone.
- A changed device, configuration or selected sensor mapping cannot reuse an old reference.
- Storage lock/full/corruption visibly stops recording; no silent reset or deletion of history.
- Quit/sign-out remains bounded and does not need a UI continuation to drain history.

## Shared model / interfaces

Core records in `ThermalModels.cs`:
- `ThermalDevice(string Id, string Name, string Kind, string ConfigurationId, string SensorMap)`; SensorMap stores typed selected-sensor provenance, no serials.
- `ThermalSample(ThermalDevice Device, string SessionId, DateTimeOffset Timestamp, double MonotonicSeconds, double? Temperature, double? Load, double? Power, double? Clock, double? FanRpm, bool Throttled=false, string? UnavailableReason=null)`.
- `MetricAggregate(int Count, double? Minimum, double? Maximum, double? Average)`.
- `ThermalWindow(ThermalDevice Device, string SessionId, DateTimeOffset Start, DateTimeOffset End, double StartMonotonic, double DurationSeconds, int SampleCount, MetricAggregate Temperature, MetricAggregate Load, MetricAggregate Power, MetricAggregate Clock, MetricAggregate FanRpm, bool Eligible, string Quality, int ResolutionSeconds=60)`.
- `ThermalReference(string Id, string DeviceId, string ConfigurationId, string Label, DateTimeOffset Created, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<ThermalWindow> Windows)`.
- `CoolingAssessment(string State, string Message, int MatchedWindows, int RequiredWindows, double? CurrentTemperature, double? ReferenceTemperature, double? Delta, double? ReferenceMinimum, double? ReferenceMaximum, bool ShouldWarn)`.
- `CoolingWarning(string Id, string DeviceId, string ConfigurationId, string ReferenceId, DateTimeOffset Created, string Message, double Delta, bool Acknowledged=false)`.

### Task 1: Aggregation and reference comparison
Files: create Core/ThermalModels.cs, Core/ThermalAnalysis.cs; tests/ThermalAnalysisTests.cs.
- [x] Tests first: stable history, heating drift, changing power/load, missing data, intentional fan zero, warm-up, gap/restart/clock changes, different device/configuration, insufficient reference, fixed baseline.
- [x] Implement `ThermalAggregator.Add(ThermalSample): IReadOnlyList<ThermalWindow>`, `Flush(): IReadOnlyList<ThermalWindow>`, `Reset(): void` with per-device minute windows; elapsed monotonic time determines duration. Require >=30 valid samples, >=50 seconds, complete required temperature/load/power coverage, power >5W, power range <=max(8W,15%), load range <=15 percentage points. Exclude first five minutes and five minutes after gaps or unstable load. A >4 second gap or UTC/monotonic mismatch >3 seconds starts a new run. Partial flush stays ineligible.
- [x] Implement `CoolingAnalyzer.Assess(IReadOnlyList<ThermalWindow> recent, ThermalReference? reference, DateTimeOffset now): CoolingAssessment`. Require >=30 eligible reference windows, ten consecutive recent eligible minute windows with no gap/session change and data no older than two minutes. Match each against >=10 reference windows within max(5W,10%) power and 10 percentage points load. Warn only when every matched minute is >=5°C hotter than reference mean and >=3°C above reference 95th percentile. Expose range, delta and matched counts; no fixed absolute temperature health verdict.
- [x] Run focused tests; record fail/pass evidence.

### Task 2: Transactional bounded persistence
Files: create Core/ThermalStore.cs; modify Core csproj; tests/ThermalStoreTests.cs.
- [x] Tests first: reopen durability, frozen/replaced reference, rollup/retention, bounded query, valid-count-weighted summaries, no duplicate minute rows, locked/corrupt DB, warning acknowledgment, CSV invariant formatting/escaping, storage cap.
- [x] Implement `ThermalStore(string path)`, `Initialize()`, `Append(IReadOnlyList<ThermalWindow> windows, DateTimeOffset now, int detailedDays=30, int summaryDays=90)`, `Devices(): IReadOnlyList<ThermalDevice>`, `Read(string deviceId,string configurationId,DateTimeOffset from,DateTimeOffset to,int maxPoints=2000,bool detailedOnly=false): IReadOnlyList<ThermalWindow>`.
- [x] Implement `SaveReference(ThermalReference)`, `References(string deviceId,string configurationId): IReadOnlyList<ThermalReference>`, `ActiveReference(string deviceId,string configurationId): ThermalReference?`, `ActivateReference(string id)`, `AddWarning(CoolingWarning)`, `Warnings(string deviceId,string configurationId): IReadOnlyList<CoolingWarning>`, `AcknowledgeWarning(string id)`, `StorageBytes: long`, `ExportCsv(string deviceId,string configurationId,DateTimeOffset from,DateTimeOffset to,string outputPath): int`.
- [x] SQLite WAL, one-second busy timeout, no pooled handles; versioned schema, no destructive corruption recovery. One-minute detailed rows and incremental weighted hourly summaries; hourly rows remain ineligible for reference analysis. Query uses details where retained and summaries otherwise, never duplicates overlap; bound UI rows by aggregation without connecting gaps. Preserve device/configuration/session boundaries and quality.
- [x] Bound database page count, checkpoint journals, prune before inserts; surface exceptions to service. Limit stored warnings to latest 1000 globally. Read-only UI methods must not alter history. CSV explicitly marks resolution and quality; refuse overwriting an existing export unless UI chose overwrite.
- [x] Run focused tests and measure representative stored bytes.

### Task 3: Collector projection and background service
Files: App/CoolingHistoryService.cs, App/ThermalProjection.cs; Collector.cs, UiSession.cs, App.xaml.cs, TrayIcon.cs, Core/Monitoring.cs settings; focused fixtures.
- [x] Extend SensorReading with optional typed hardware/sensor metadata while preserving existing callers. Select exact CPU package/Tctl and GPU core temperature, load and package power sources; no guessed hotspot/pump. Hash machine+hardware kind/name/provider path+mapping for local device identity; explicit configuration ID separates cooling setups.
- [x] Create service without IO; Start subscribes before collector starts. Queue at most 120 immutable Readings via TryWrite only. Worker owns projection/aggregation; serialize storage operations off UI, query bounded results. Dropped samples create visible status and aggregation gap.
- [x] Persist recording/notifications/retention/configuration settings with safe legacy defaults. Default recording on, tray notifications off. A disabled period must reset aggregation; no inactive interval becomes contiguous data.
- [x] Provide `CoolingView` and service APIs for UI (defined with implementation); save reference only from retained eligible detailed data with >=30 windows and <=7-day span. Keep active reference fixed. Warn at most once per device/reference per 24 hours and suppress while unacknowledged. Persist warnings and cooldown across restart.
- [x] Stop accepting, drain queue and flush during quit; bounded three-second sign-out path. Failure status remains visible without losing live monitoring. Tray warnings dispatch safely and cannot interrupt collector.

### Task 4: Cooling Health UI and verification
Files: App/CoolingHealthPage.cs, App/ThermalHistoryChart.cs, Windows.cs; docs, version/package metadata as appropriate.
- [x] Add page-local device selector, day/week/month views, metric selection (temperature/power/load/clock/fan), UTC-positioned chart with visible gaps and reference comparison evidence, local recording controls/storage usage, retention controls and CSV export.
- [x] Reference start/end controls, save and select prior reference, new-setup action, clear learning/unsupported/stale messages. Expose available coverage, optional notifications, warning log/acknowledgment and confounders; no pump failure label.
- [x] Hide unrelated live range/GPU controls on this page; preserve overview and network fix.
- [x] Run Release build + full tests; native-free fixtures for projection/service/UI, dark/light/empty/data states; storage growth/queue and bounded shutdown checks. Independent whole-change review; fix substantive findings.
- [x] Package reviewable build and document evidence/limits. Request tray Quit only when ready to replace running files, then native/manual smoke with Computer Use off. Publish only after responsive smoke confirmation, consistent with prior release workflow.

## Execution notes

The user has approved the design and explicitly requested implementation. Use parallel ownership for the pure analysis and persistence modules, then integrate UI/runtime and review; no further workflow-choice approval is needed.

Rulings from implementation: match available fan context within max(250 RPM, 20%), with fan-stop separated; log warnings even when notifications are muted; disable reference comparison for ambiguous identical devices; use schema v2 lightweight reference headers and a bounded immutable active-reference cache. SQLitePCLRaw 3.0.5 replaces the vulnerable transitive native package. Native installation and responsive history smoke passed; public release verification is tracked in docs/validation.md.
