# First network use alerts and history controls (0.17.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship 0.17.0, "Watch and control": an alert the first time an app uses the network (after a quiet 3-day learning period, one grouped balloon a minute), **Don't record these apps**, and **Clear history…** for one app or all apps across Connections history, Usage history, Application resources and the alert log, resuming after Quit or a crash.

**Architecture:** Core gains the one app key (`AppKey`), the Don't record list (`AppExclusions`), the clear request (`HistoryClear`) and the first-use policy (`FirstNetworkUse`, `FirstUseTracker`). The Don't record filters sit where each worker folds data, after the live view is built: the connection writer's merge, `UsageMinuteFolder`, `ProcessMinuteFolder` and `AlertEvaluator`. Each store gains a durable clear marker (a `connections.db` `metadata` row, `pending_clear` in `alerts.db`, a sidecar file for the other two) and a bounded `ClearStep`, which its own service worker runs as a queued command between data items. `alerts.db` gains the known-app tables. `AlertService` detects first use on the collector thread and owns the tracker, the seed and the grouped balloon. `UiSession` carries the list, the clear request and the re-read event to Settings, the Clear history window, the Alerts page, the live marks and both strips.

**Tech Stack:** .NET 10, WPF, Microsoft.Data.Sqlite (existing; bundled SQLite 3.53.4 from `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5), `System.Collections.Frozen`, xUnit 2.9.3. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-07-connections-design.md`, approved by the user section by section and then as a written spec on 2026-11-07. It is binding. This plan implements its front matter (Intent, Decisions 1–30, "Settled in the user's review"), Architecture, Release split, the 0.16.0 section's "Writer hooks for 0.17.0", the Destinations section's "With 0.17.0", the two 0.17.0 sections, and "Errors, testing, docs and release (both releases)" as it applies to 0.17.0. The user's "trim the steps" choice (decision 15, the shared Destinations pass) is 0.16.0's and changes nothing here. Executors read both documents; where they differ, the spec wins. The spec's anchors are at 181eb54 (0.14.1). **Line numbers are at 75d6d65** (v0.15.0, `main`) throughout this plan.

## Builds on 0.16.0

0.17.0 starts from `main` after v0.16.0 has merged (`docs/superpowers/plans/2026-11-07-connections-0.16.0.md`). It never re-implements 0.16.0 work. Its names are the contract; if 0.16.0's review renamed one, the merged code wins and Task 1 Step 0 says so in the PR.

### Consumes from 0.16.0

- **Storage (0.16.0 Tasks 1, 2, 4):**
  - `ConnectionMinuteFolder` (`Merge`, `Tick`, `FreezeAll`, `Sendable`, `Sent`, `DropWaiting`, `Held`, `Waiting`, `BatchId`), `ConnectionMinuteUnit` and `ConnectionSendable` in `src/DeadWare.Core/ConnectionMinuteFolder.cs`.
  - The writer in `ConnectionService.WriteLoopAsync`: its merge step (the three checks, then `folder.Merge(sample, item.Generation, item.Origin, complete, accepted)`), its write attempt (`folder.Sendable(clock(), final)`, then `Append`), its tick and its R6 final write. The accounting invariant `buffered` = queued deltas + `folder.Held` + `folder.Waiting`.
  - `ConnectionHistoryStore`'s `private const string GroupedWhere`, `Grouped` (kept for `ExportCsv`), `temp.geo` and `temp.grouped`, `Grouping<T>`, the constructor `(path, maximumDatabaseBytes, maximumGroupedTempPages)` and `ConnectionHistoryLimitException`.
- **The strip (0.16.0 Tasks 6–13):**
  - `TrafficStrip` (ending `DateTimeOffset? RetainedFrom = null, bool CapacityShortened = false`), `TrafficStripSlot`, `TrafficStrips` (`CeilingMinute`, `MinuteStarts`, `IsFull`, `DueUntil`, `SlotAt`).
  - `Read`'s strip steps, which read `floor` and `expiry` once in the read transaction (step 4), and `UsageHistoryStore.ReadApplicationSeries(machine, from, to, maxPoints)`.
  - `ConnectionHistoryQuery.Application`, `app_key` registered in `ConnectionHistoryStore.Open` as `ProcessGrouping.Key`.
  - `TrafficStripChart` (`SetData(strip, dueUntil, now)`, `ChipLines`, `Hatch`, `NotKept`, `HoverAt`, `HandleKey`, `PlotRect`) and `TrafficStripView` (`Apply(strip, dueUntil, now)`, `Legend`, `NotKeptLine`, `Chart`).
  - `ConnectionsPage`: `ReloadHistory()`, `ShowHistory(from, to, application)`, `applicationFilter`, `applicationName`, `trafficStrip`, `clock`, the application filter button (automation name "Clear application filter"), and the expander note.
  - `UiSession.RequestConnectionsHistory(from, to, application)` and `ConnectionsHistoryRequested`.
  - `UsageHistoryPage.ApplyStrip(TrafficStrip?, DateTimeOffset now)`, its `strip` field and `RefreshAsync`, which starts a new strip read each time.
  - `UsageHistoryService.DefaultFlushInterval` and `ConnectionService.DefaultFlushInterval`.
- **Destinations (0.16.0 Tasks 14–19):** `ConnectionDestinations.FromLive` (Live apps keyed by `ProcessGrouping.Key`), `DestinationsCard` (`Rows`, `Grid`, `ShowLive`, `ShowHistory`), `DestinationRow` (`Apps`, `AppsTip`, `Spoken`, `Update(value, context)`), `DestinationRowContext(bool History, bool Available, bool Complete, bool HasLocationDatabase)`, Copy row, and `ExportDestinationsCsv`, which reads `temp.grouped`.
- **Measurement (0.16.0 Task 3):** `ConnectionHistoryMeasurementTests` and its 20-pair per-minute fill to the 256 MiB cap.

### Dependencies for the controller (the spec has 0.16.0 provide these; the 0.16.0 plan leaves them unnamed)

1. **The write attempt** is a local function in 0.16.0's writer with no result. Task 16 makes it return `ConnectionFlushResult` (the spec's "Write result" hook).
2. **The application filter button** has no field name in 0.16.0's Interfaces. Task 24 finds it by its automation name, "Clear application filter".
3. **The 20-pair fill** is inline in 0.16.0's measurement. Task 2 moves it, unchanged, into an internal helper so the clear measurement builds its file "as `Connection_history_rates_and_read_timings` builds its file".
4. **`TrafficStripView` shows one line under the legend** (`NotKeptLine`). Task 24 adds `NoteLines` for the spec's further lines; `NotKeptLine` keeps its meaning.
5. **`DestinationRowContext` has no Don't record set.** Task 23 adds a trailing optional member.

### Anchors

**Untouched by 0.15.0 and 0.16.0**, so the spec's numbers hold:
- Core: `AlertRules.cs`, `AlertStore.cs`, `AlertEvaluator.cs`, `UsageHistory.cs` and `ConnectionLedger.cs`.
- App: `AlertService.cs`, `AlertsPage.cs`, `App.xaml.cs`, `TrayIcon.cs`, `ProcessHistoryService.cs`, `ConnectionsWidget.cs`, `ProcessActions.cs`, `NativeProcessIdentity.cs` and `EtwNetwork.cs`.
- Tests: `AlertRuleTests.cs` (:68, :83), `AlertStoreTests.cs` (:68), `AlertEvaluatorTests.cs`, `AlertServiceTests.cs`, `AlertsUiChecks.cs` (:35, :50, :72-73), `CoolingShutdownTests.cs` and `RealWindows/ProcessScanChecks.cs` (:44).

**Moved by 0.15.0**, re-anchored here:
- `Core/ProcessHistory.cs`: `SavedName` :11-18; `ProcessMinuteFolder` :39; `Fold` :110-152 (spec :100-121), with the unknown-name branch at :120-125 (spec :107), the key at :128 (spec :110) and the GPU busiest-engine loop at :141-147.
- `Core/ProcessHistoryStore.cs`:
  - `MaximumJournalBytes` :46 (spec :34);
  - `Append` :145, whose `ExpireByDay` call is at :153 (spec :111) and whose `names` insert is at :184 (spec :140-142);
  - `Expire` :213-218;
  - `ReadApplications` :222, limit check :225 (spec :177-180), retention cutoff :230-231 (spec :185-186);
  - `KeyOf` :353 (spec :295);
  - `DeleteMinutes` :378-390 with its two statements at :386-387 (spec :315-326, :323-324);
  - `DeleteOrphanedNames` :392-393 (spec :329-330), `ExpireByDay` :399-411 (spec :336-348), `EvictOldestDay` :413-423.
- `Core/ProcessTable.cs`: `ProcessCopy.Footer` :21 (spec :15); `ProcessGrouping.Key` :77 (spec :50).
- `Core/Monitoring.cs`: `UserSettings` :374-414, `Alerts` initializer :390, `SettingsCodec.Read` :418-458, the `WidgetAlwaysOnTop` `TryGetProperty` at :438 (spec :437) and `AlertRules.Normalize` at :449 (spec :444). 0.16.0 adds `ConnectionsDestinationScope` here.
- `App/Collector.cs`: `Reading.Connections` :14, `IsElevated` :134 (spec :128), `SampleAvailable` raised at :304 (spec :282), failure readings at :312 and :326 (spec :290, :304).
- `App/ProcessesPage.cs`: `ProcessRowView` :23, `Confirm` :99, the app row update :383, `Spoken` :495-504 (spec :480-486).
- `App/ApplicationResourcesCard.cs`: the note :129, with "…; Everything else holds the rest." (spec :108).
- `App/UsageHistoryPage.cs`: Keep a longer record :116-121 (spec :113-117); `RefreshAsync` :208-224 (spec :205-221).
- `docs/user-guide.md`: Connections :129-145 (retention :145); Usage history :165-179; Alerts :181-189 (:183 "Open **Alerts** in the sidebar", :187 "Every crossing is one **episode**", :189 "Device and network rules evaluate"); Network measurements and privacy :205-215 (:207 "Interface totals work independently"); Build :230.
- `docs/validation.md`: the expiry-watermark finding of the 0.12.2 connections review is at :832 (spec :785).
- `tests/DeadWare.App.Tests/ShellUiChecks.cs`: edited in place at :87 (the usage-help assert); the alert-help check stays at :89.

**Changed again by 0.16.0.** Numbers below are at 75d6d65 and name the member or quote the text; anchor on that. The files are `ConnectionService.cs`, `ConnectionHistoryStore.cs`, `ConnectionModels.cs`, `ConnectionsPage.cs`, `UiSession.cs`, `UsageHistoryStore.cs`, `UsageHistoryService.cs`, `UsageHistoryPage.cs`, `ApplicationResourcesCard.cs`, `Windows.cs`, `Monitoring.cs`, `ProcessesPage.cs`, the docs, and the tests `BatteryUiTests.cs`, `ConnectionsPageUiChecks.cs`, `UsageHistoryUiChecks.cs`, `ConnectionServiceTests.cs`, `ConnectionHistoryStoreTests.cs` and `UsageHistoryStoreTests.cs`.

**Access between assemblies.** `DeadWare.Core` has no `InternalsVisibleTo`. Core tests use public members, and reach a store's private seam or private `Open` by reflection, as `ProcessHistoryStoreTests` sets `beforeUpgradeCommit` (`tests/DeadWare.Tests/ProcessHistoryStoreTests.cs:251`). `DeadWare.App`'s internals are visible to `DeadWare.App.Tests`.

## Global Constraints

From the spec, verbatim:

- "**House rules:** null, never zero, for an unknown value; every "—" has a tooltip naming its reason; status text holds fixed copy, counts and error codes, never names or paths; native and storage work runs off the UI thread; colours come from theme resources only; everything stays local; no new network traffic; no administrator requirement beyond per-app capture's existing one." And: "a store that cannot be read shows as unavailable, never as an empty period".
- "**Stores.** No store changes its format version." "connections.db, usage-history.db and process-history.db (version 2 after 0.15.0) gain no tables, columns or indexes. connections.db gains only rows in its existing `metadata` table." "alerts.db gains one table, `pending_clear`, beside *First network use alerts*' tables, and stays at `user_version` 1".
- "**`alerts.db`** stays at `user_version` 1, and every build since 0.8.0 still opens it. **0.17.0** adds four tables with `IF NOT EXISTS`, with one evict-and-retry on SQLITE_FULL at the 64 MiB cap". "First-use episodes are kind 4 and are always saved closed."
- Release split, Stores: "No format change; four `alerts.db` tables, clear markers, cleared-time marks (`connections.db` `metadata` rows and `usage-history.cleared.json`), three new settings and the added rule". The settings: "`ExcludedApps` (Don't record these apps, at most 64); `FirstUseIgnoredApps` (Don't alert, at most 256); `FirstUseRuleAdded`; the first-network-use rule (`AlertKind` value 4) in `Alerts`."
- "Keys are compared ordinally everywhere: in memory, in alerts.db and in the settings lists." "New matching in this section never uses SQLite's `upper()` or `COLLATE NOCASE`, which fold only ASCII. One inherited exception remains: the budget re-seed reuses `ReadApplicationMinutes`, whose `COLLATE NOCASE` (`src/DeadWare.Core/UsageHistoryStore.cs:226`) misses non-ASCII case variants, as the startup seed does today."
- New limits: "*First network use alerts:* 8,192 known apps and 256 Don't alert entries"; "*Don't record these apps and Clear history…:* 64 excluded apps, 250 ms clear steps with per-store transaction sizes, and one clear at a time." The sizes: "Connections history: at most 512 rows, halving under its 1.5 MiB budget"; "Usage history: at most 512 rows for one app, and 4,096 for all apps"; "Application resources: at most 6 hours of minutes for one machine"; "the alert log: at most 256 episodes, or 1,024 known rows of other machine keys."
- "**Existing caps and journal limits are unchanged**": `connections.db` 256 MiB and 30 days; `usage-history.db` 256 MiB and 400 days; `process-history.db` 192 MiB (0.15.0) and 30 days; `alerts.db` 64 MiB and 400 days. "Each store's 8 MiB journal bound, the transaction budgets, the queue capacities and `MaximumBufferedDeltas` are unchanged too. Retention has one exception: while a clear marker exists, Usage history's and Application resources' retention deletes wait until the clear ends".
- "**No VACUUM, no `VACUUM INTO` and no file recreation.** Freed pages stay in the file and are reused." "This SQLite build has no `DELETE … LIMIT` (`SQLITE_ENABLE_UPDATE_DELETE_LIMIT` is off)."
- "**Quit and sign-out.** Quit keeps its 20 s history deadline and sign-out its 3 s limit (`UiSession.cs:300`, `:346`). A clear makes them wait for at most one step (250 ms plus one transaction). The final Connections write never purges (`maintenanceTransactions: 0`)."
- "**Status text** holds fixed copy, counts and error codes (exception type names, as today), never app names, addresses or paths." "App names and addresses appear only where the user reads data: cells, legends, tooltips, spoken row text, the clipboard, CSV files, alert log rows and balloons." "`--diagnose` gains nothing."
- "**Threads.** All storage and native work runs off the UI thread": "first-use detection runs on the collector thread, with no I/O and no locks of its own; the alert worker owns the tracker, the seed and every `alerts.db` write; each clear runs on its own store's worker."
- "**No new requirements.** Nothing needs administrator rights beyond per-app capture's existing need. Nothing makes a network request."
- "Page checks run in Windows CI inside the existing STA fact `BatteryUiTests.Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows` … No new STA fact is added. The new check classes (`TrafficStripUiChecks`, `HistoryClearUiChecks`) join that phase list."
- "A measurement records and decides nothing; `docs/validation.md` keeps its numbers as measured, with no threshold." "The clear measurement also asserts that the WAL never exceeded 8 MiB with no reader and that every clear finished." "`First_network_use_sees_this_process_send_one_udp_datagram` is a check, not a measurement. When capture is unavailable it records that and passes."
- "**CI time.** CI's job limit is 20 minutes (`.github/workflows/build.yml:15`). … the 0.17.0 plan's first measurement task times the clear measurement's two fills. Each records the time in its PR description and in its release's `docs/validation.md` section. If the fills do not fit, that goes back to the user before either the file sizes or the limit changes."
- "**Checks by eye** are done by the user on their PC. They are listed in `docs/validation.md` as pending and never claimed as verified until the user reports them".
- "**Docs.** Each release updates the user guide, CHANGELOG, its release notes and `docs/validation.md`. README gets at most a one-line mention per release. The 0.17.0 notes state the rollback effect." Headings: "CHANGELOG: `## 0.17.0 — First network use alerts and history controls`"; "`docs/validation.md`: `## First network use alerts and history controls (0.17.0, <date>)`". The release notes have none.
- "**Release:** 0.17.0 is one PR from main after 0.16.0 has merged; the order is 0.15.0, 0.16.0, 0.17.0." "**Recheck anchors.** Before each plan starts, the anchors that 0.15.0, and for 0.17.0 also 0.16.0, moved are checked again."
- Out of scope (the spec's 0.17.0 lists): hostnames, reputation or online lookups, blocking; "app changed" alerts; first contact with a new country or destination; a timed snooze or a per-rule log-only mode; TCP connect and accept events; unattributed traffic; seeding from Application resources; a new learning period after a clear; shrinking files; wiping exported CSVs, notification history, backups, restore points or drive remnants; wildcards, folders, publishers or paths in the list; excluding by PID or a "PID n" name; clearing by destination, country or custom range; clearing automatically when an app is added; scheduled clears; restoring `expiry` after an all-apps clear; keeping which app a one-app clear removed.

For this plan:

- Every task's requirements include the spec section it cites.
- **Local checks** run on Linux with .NET SDK 10.0.114.
  - Build: `dotnet build DeadWare.slnx -c Release -p:EnableWindowsTargeting=true --nologo`. Expected: 0 errors, and only the warnings `main` had at the branch point (Task 1 Step 0 records the count; 14 at 0.15.0), none in a file the task touched. Count them with Step 0's command, `--no-incremental` included.
  - Core tests: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "<filter>"`.
  - **Core baseline:** the full Core run fails exactly the 26 Windows-only tests in `/home/user/DeadWare-processes/.superpowers/sdd/2026-11-05-processes/linux-baseline-failures.txt`, none of them an alert, history or connection test. This prints nothing:
    `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo 2>&1 | grep -E '^  Failed ' | sed -E 's/ \[[^]]*\]$//' | sort | diff - <(sort /home/user/DeadWare-processes/.superpowers/sdd/2026-11-05-processes/linux-baseline-failures.txt)`
- **Windows-only tests.** App tests (`tests/DeadWare.App.Tests`) and UI checks run only in Windows CI; locally they only need to compile. UI checks run inside the single STA fact, which **stops at its first failing check**. A UI check "passes in CI" when that fact passes, and "fails" when the fact fails at the named check in the named phase.
- **Red checks.**
  - A test that needs a new member is red when the local build fails with the named `error CS…`: CS0103 for a missing static type used only in member access; CS0246 for a missing type in a declaration, a `new`, a type argument or a `typeof`; CS1061 for a missing instance member; CS0117 for a missing static member of an existing type (or an unknown member in an object initializer); CS1739 for a missing named argument.
  - A Core test that compiles without the change is red when its local run fails at the named assertion.
  - A Windows-only test that compiles without the change is red only in CI (**CI-red first**): push its `test(...)` commit alone and stop. The controller confirms the named failure (the first check expected to fail, for the STA fact) before the change is pushed.
- **Run (CI)** is the controller's. Implementers push and return; they never use `gh`. The controller watches checks, reads job logs and fetches the `DeadWare-measurements` artifact through the GitHub tools. `build.yml` cancels an in-progress run when a newer push arrives, so push once per step and let the previous run finish first.
- **Workflow.**
  - One branch, `claude/connections-0.17.0`, from `origin/main` after v0.16.0 has merged, with one pull request to `main`, opened as a draft with Task 1's push. CI runs on `claude/*` branches only through a pull request.
  - Fetch and inspect `origin` before each task. Keep unrelated local work. Never force-push or rewrite pushed commits.
  - Each task ends with its commits pushed, `git fetch origin && git status -sb` showing nothing ahead or behind, and CI green, as `AGENTS.md` requires.
  - Tasks 3–25 start only after Task 2's gate has passed, and Task 5 only after the user has decided the `AlertStoreTests.cs:68` conflict (Task 5, Before this task). Mark the PR ready after Task 25.
- **Merging and publishing.** The spec sets one PR from `main` with CI as the gate; neither it nor the user's approval of it ("spec approved") gives a go-ahead to merge or publish 0.17.0. When every task has been reviewed, the final review and its fix wave are done and CI is green, the controller asks the user for that go-ahead. On it, the controller does what it did for 0.15.0:
  1. Updates the `docs/validation.md` Results line with the final run id and head SHA, and lets that run finish green.
  2. Merges the PR (merge commit).
  3. Runs `release.yml` for tag `v0.17.0` on `main` (`actions_run_trigger` `run_workflow`, input `tag`).
  4. Checks the published release: its body equals `docs/releases/v0.17.0.md`, and the ZIP and its `.sha256` are attached.
- **Commits** use `test(...)`, `feat(...)`, `fix(...)` and `docs:`. Every message ends with the committing session's own `Co-Authored-By:` line (`Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` for the controller; a subagent on another model uses its own) and `Claude-Session: https://claude.ai/code/session_017AZ6FXcD7T9s63fqUnw9Js`. The commands below show only the subject; add the two lines as a final `-m` paragraph. The PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`, a blank line, then the same session link. Never commit anything under `.superpowers/`.
- **Real-Windows tests** are `[GitHubWindowsFact]` (or `[ElevatedGitHubFact]` for the first-use check) facts in classes marked `[Collection("RealWindows")]` under `tests/DeadWare.App.Tests/RealWindows/`. They write through `MeasurementRecorder.Write` and never record an app name, address or path.
- **Copy** is exact. Every user-visible string quoted from the spec is used as written, "—", "…", "·", "≥" and "✕" included.
- **Name clash.** `HistoryClear` has a property named `AppKey`, so inside that record the static class is `global::DeadWare.Core.AppKey`.

## Review Focus

The five failure modes the spec implies but its listed tests do not exercise, most likely first. Each is pinned by a test in its owning task.

1. **A one-app clear of a non-ASCII name typed in another case** (`ÄRGER.EXE` for saved `ärger.exe` and `Ärger.exe`). Expected: every spelling is cleared in all four stores, because matching is by `AppKey.Of` (C# `ToUpperInvariant`), never SQLite's ASCII folding. Test: Task 11, `One_app_clear_matches_non_ascii_case_in_every_store`.
2. **A one-app clear for an app with no saved history** (a typo such as `chrom.exe`). Expected: each store's first step finishes, `Percent` is 100 where the store shows one, every marker is gone, and nothing is deleted. Test: Task 11, `Clearing_an_app_with_no_saved_history_finishes_at_once`.
3. **An app added to Don't record in the middle of a minute** (Connections). Expected: the part of that minute merged before the change is saved when the minute closes, and nothing after the change; the next minute holds none of it. Test: Task 15, `Excluding_an_app_mid_minute_keeps_what_was_merged_before`.
4. **Clear history while Usage recording and Per-process monitoring are off.** Expected: both clears still run and finish ("the workers run while recording is paused"), and nothing is recorded meanwhile. Test: Task 17, `Clear_runs_while_recording_is_paused`.
5. **A step fails (an IOException, as when the 60 s yield limit passes) and the user presses Try again.** Expected: ClearState is Failed with `MarkerSaved` true and the status suffix ` · Clearing history failed (IOException); it resumes when DeadWare next starts.`; recording continues; `UiSession.ClearHistory` refuses a new request; **Try again** finishes the clear from its marker. Tests: Task 17, `Try_again_after_a_failed_step_finishes_the_clear`, and Task 19, `ClearHistory_refuses_a_second_request_and_reports_a_stopped_worker_as_unavailable`.

## File structure

| File | Change | Responsibility |
|---|---|---|
| `src/DeadWare.Core/AppKey.cs` | new | `AppKey` (`Of`, `IsPidName`) and `AppExclusions` (`Accepts`, `Normalize`, `Keys`). |
| `src/DeadWare.Core/HistoryClear.cs` | new | `HistoryClear`, `HistoryClearProgress`, `PendingConnectionClear`, `HistoryClearMarker`, `UsageClearedMarks`, `HistoryClearPhase`, `HistoryClearStore`, `HistoryClearState`, `HistoryClearLine` and `HistoryClearCopy`. |
| `src/DeadWare.Core/FirstNetworkUse.cs` | new | `FirstNetworkUse` (learnability, the Don't alert list, learning end, balloon and rule-line text) and the first-use records. |
| `src/DeadWare.Core/FirstUseTracker.cs` | new | The pure first-use policy object. |
| `src/DeadWare.Core/AlertRules.cs` | modify | `AlertKind.FirstNetworkUse`, `AlertEpisode.Application`, normalization, names and the default rule. |
| `src/DeadWare.Core/AlertEvaluator.cs` | modify | Skip kind 4; budget episodes carry their application; `SetExcluded`, `SeedDay(…, only)`, `EndScope`. |
| `src/DeadWare.Core/AlertStore.cs` | modify | The four tables, `app_key`, links, known rows, metadata, `ReadFirstUse`, the clear. |
| `src/DeadWare.Core/Monitoring.cs` | modify | `ExcludedApps`, `FirstUseIgnoredApps`, `FirstUseRuleAdded`, the default rule. |
| `src/DeadWare.Core/ProcessTable.cs` | modify | `ProcessGrouping.Key` is `AppKey.Of`; the footer line. |
| `src/DeadWare.Core/UsageHistory.cs` | modify | `UsageMinuteFolder.Excluded`, `Boundary`, `Forget`. |
| `src/DeadWare.Core/ProcessHistory.cs` | modify | `ProcessMinuteFolder.Excluded`, `Boundary`, `Forget`, `Discard`, `OpenMinute`. |
| `src/DeadWare.Core/ConnectionMinuteFolder.cs` | modify | `Forget`, `RetireWaiting`. |
| `src/DeadWare.Core/ConnectionHistoryStore.cs` | modify | Bounded maintenance, purge-before-evict, the marker, `BeginClear`, `ClearStep`, the hide predicate, cleared marks, `ReadApplicationNames`. |
| `src/DeadWare.Core/UsageHistoryStore.cs` | modify | The sidecar marker, `BeginClear`, `ClearStep`, cleared marks, the retention wait, `ReadApplicationNames`. |
| `src/DeadWare.Core/ProcessHistoryStore.cs` | modify | The sidecar marker, `BeginClear`, `ClearStep` in 6-hour windows, the retention wait. |
| `src/DeadWare.Core/TrafficStrip.cs` | modify | `TrafficStrip.RetainedByClear` and `AppClearedBefore`. |
| `src/DeadWare.App/AlertService.cs` | modify | Detection, the tracker, the seed, groups, exclusions for budgets, the alert-log clear. |
| `src/DeadWare.App/ConnectionService.cs` | modify | The Don't record filter, the boundary rule, `ConnectionFlushResult`, bounded maintenance, the clear command, `ReadApplicationNamesAsync`. |
| `src/DeadWare.App/UsageHistoryService.cs`, `ProcessHistoryService.cs` | modify | `SetExclusions`, the clear command, `ReadApplicationNamesAsync` (Usage), the read `limit` (process). |
| `src/DeadWare.App/UiSession.cs` | modify | `SetExcludedApps`, `IsExcluded`, `ClearHistory`, `RetryClear`, `HistoryClearChanged`, `IgnoreFirstUse`, `UnignoreFirstUse`, wiring. |
| `src/DeadWare.App/App.xaml.cs` | modify | The first network use balloon. |
| `src/DeadWare.App/AlertsPage.cs` | modify | The kind, the rule line, the Don't alert list, first-use log rows, the copy, the Not counted note. |
| `src/DeadWare.App/ExcludedAppsBlock.cs` | new | Settings → **Don't record these apps** and `AppSuggestions`. |
| `src/DeadWare.App/ClearHistoryWindow.cs` | new | `ClearHistoryWindow`, `HistoryClearProgressView` and `ClearHistoryEntry` (the button with its disabled tooltips). |
| `src/DeadWare.App/Windows.cs`, `ConnectionsPage.cs`, `ConnectionsWidget.cs`, `DestinationsCard.cs`, `ProcessesPage.cs`, `ApplicationResourcesCard.cs`, `UsageHistoryPage.cs`, `TrafficStripChart.cs` | modify | Entry points, " · not recorded" marks, footers, the cleared and not-recorded strip states, page re-reads. |
| `tests/DeadWare.Tests/…` | new / modify | `AppKeyTests`, `HistoryClearTests`, `FirstNetworkUseTests`, `FirstUseTrackerTests` (new); the alert, folder, evaluator and store test classes. |
| `tests/DeadWare.App.Tests/…` | new / modify | `HistoryClearSessionTests`, `HistoryClearUiChecks`, `RealWindows/HistoryClearMeasurementTests`, `RealWindows/FirstNetworkUseChecks` (new); the service tests and the UI check classes. |

---

## 0.17.0 — Shared Core and the CI gate

### Task 1: One app key, the Don't record list and the clear request (Core)

Spec: *Don't record these apps and Clear history…* → One app key, Settings model, Clear history… (Boundary); its Core tests `AppKey_folds_case_spelling_and_surrogates`, `Exclusions_normalize_drop_invalid_and_duplicates_and_cap_at_64`, `Excluded_apps_round_trip_and_a_file_without_them_reads_empty`, `Clear_boundaries`.

**Files:**
- Create: `src/DeadWare.Core/AppKey.cs`, `src/DeadWare.Core/HistoryClear.cs`
- Modify: `src/DeadWare.Core/ProcessTable.cs:77` (`ProcessGrouping.Key`); `src/DeadWare.Core/AlertStore.cs:162-176` (`Open`); `src/DeadWare.Core/Monitoring.cs` (`UserSettings` :374-414, `SettingsCodec.Read` :418-458)
- Test: `tests/DeadWare.Tests/AppKeyTests.cs`, `tests/DeadWare.Tests/HistoryClearTests.cs` (new)

**Interfaces:**
- Consumes: `UsageMetrics.NormalizeName`, `ProcessHistory.SavedName`; 0.16.0's `app_key` registration in `ConnectionHistoryStore.Open`, which passes `ProcessGrouping.Key` and so computes `AppKey.Of` once `Key` delegates to it.
- Produces:
  - `public static class AppKey { public static string Of(string? name); public static bool IsPidName(string? name); }`. `Of`: `NormalizeName`, then each unpaired surrogate to U+FFFD (the UTF-8 round trip `SavedName` uses), `TrimEnd()`, `ToUpperInvariant()`. Never null or empty. `IsPidName`: `Of(name)` matches `^PID [0-9]{1,10}$` (ordinal, culture-invariant regex, no `IgnoreCase`).
  - `public static class AppExclusions { public const int MaximumApps = 64; public static bool Accepts(string? name); public static List<string> Normalize(IEnumerable<string?>? names); public static FrozenSet<string> Keys(IEnumerable<string> names); }`. `Accepts` = `ProcessHistory.SavedName(name) is not null && !AppKey.IsPidName(name)`. `Normalize` keeps each accepted entry whose key no earlier entry has, stores `SavedName(entry)`, at most 64, in saved order. `Keys` is the ordinal `FrozenSet` of `AppKey.Of` values.
  - `ProcessGrouping.Key(string name) => AppKey.Of(name)`.
  - In `AlertStore.Open`, every connection, read-only included: `db.CreateFunction<string, string>("app_key", AppKey.Of, isDeterministic: true)`.
  - `public List<string> ExcludedApps { get; set; } = new();` on `UserSettings`; `Read` sets `s.ExcludedApps = AppExclusions.Normalize(s.ExcludedApps)` and never sets `Error` for it.
  - `public sealed record HistoryClear(string? AppKey, DateTimeOffset Before, DateTimeOffset Requested)` with:
    - `public static HistoryClear Create(string? application, bool allTime, DateTimeOffset requested)`: `AppKey` = `AppKey.Of(application)` or null; `Before` = `UsageMetrics.Floor(requested)` − 7 days, or + 1 minute for all time;
    - `public bool AllTime => Before > Requested;`
    - `public DateTimeOffset AlertCutoff => Before < Requested ? Before : Requested;`
    - `public bool Covers(string? name, DateTimeOffset minute)`: `minute < Before` and (`AppKey` is null or `AppKey.Of(name) == AppKey`, ordinal).

- [ ] **Step 0: Fetch, check that 0.16.0 is on main, create the branch and record the warning count**

```bash
git fetch origin
git status --short --branch                                                         # unrelated local work stays where it is
git show origin/main:Directory.Build.props | grep -c '<Version>0.16.0</Version>'    # 1; if 0, stop: 0.16.0 has not merged
for n in 'class ConnectionMinuteFolder' 'GroupedWhere' 'void ReloadHistory' 'RequestConnectionsHistory' 'record TrafficStrip(' 'ReadApplicationSeries' 'class DestinationsCard' 'Clear application filter'; do
  printf '%s: ' "$n"; git grep -c "$n" origin/main -- src | wc -l; done                # each ≥ 1; a 0 is a renamed contract: note it in the PR and use the merged name
git ls-remote --heads origin claude/connections-0.17.0                              # empty before the first push; otherwise continue that branch without force-pushing
git switch -c claude/connections-0.17.0 origin/main
# The plan: check it out from the branch it is committed on, or copy it from this worktree.
git add docs/superpowers/plans/2026-11-07-connections-0.17.0.md
git commit -m "docs: the 0.17.0 plan"
dotnet build DeadWare.slnx -c Release -p:EnableWindowsTargeting=true --nologo --no-incremental 2>&1 | grep -E 'warning [A-Za-z]+[0-9]+' | sed -E 's/^ +//' | sort -u | wc -l   # record in the PR body
```

Then recheck every anchor this plan names in the files 0.16.0 changed (Anchors), and list the moved ones in the PR body.

- [ ] **Step 1: Write the failing tests**

```csharp
// AppKeyTests. Names = ["Chrome.EXE"," chrome.exe","CHROME.EXE ","chr\u0001ome.exe","","Ärger.exe","a\uD800b.exe",new string('a',300),new string('b',255)+" x","PID 4"].
[Fact] public void AppKey_folds_case_spelling_and_surrogates(){
    Assert.Equal(AppKey.Of("chrome.exe"),AppKey.Of("Chrome.EXE"));Assert.Equal(AppKey.Of("chrome.exe"),AppKey.Of("CHROME.EXE "));Assert.Equal("CHROME.EXE",AppKey.Of(" Chrome.exe\u0001 "));
    Assert.Equal("A\uFFFDB.EXE",AppKey.Of("a\uD800b.exe"));Assert.Equal(AppKey.Of(ProcessHistory.SavedName("a\uD800b.exe")),AppKey.Of("a\uD800b.exe"));
    Assert.Equal("UNKNOWN APPLICATION",AppKey.Of(null));Assert.Equal("UNKNOWN APPLICATION",AppKey.Of(""));
    foreach(var n in Names){Assert.Equal(AppKey.Of(n),ProcessGrouping.Key(n));Assert.Equal(AppKey.Of(n),AppKey.Of(AppKey.Of(n)));}
    Assert.True(AppKey.IsPidName("pid 4294967295"));Assert.False(AppKey.IsPidName("PID 4.exe"));Assert.False(AppKey.IsPidName("PID 12345678901"));
    // process-history.db: one appended minute lists ProcessHistory.SavedName("a\uD800b.exe") ("a�b.exe"; Append rejects a name that differs from its SavedName),
    //   "Chrome.EXE" and "Ärger.exe"; `SELECT display,upper FROM names` on a Pooling=false read-only
    //   connection gives upper == AppKey.Of(display) for each.
    // connections.db and alerts.db: each store's private Open, reached by reflection (alerts.db with readOnly:true too), gives a connection on which
    //   `SELECT app_key($n)` equals AppKey.Of(n) for every name in Names.
}
[Fact] public void Exclusions_normalize_drop_invalid_and_duplicates_and_cap_at_64(){
    Assert.Empty(AppExclusions.Normalize(null));
    Assert.Equal(["chrome.exe"],AppExclusions.Normalize(["","  ","Unknown application","everything ELSE",@"C:\a.exe","a/b","a*","PID 123","pid 9","chrome.exe","Chrome.exe"]));
    var many=Enumerable.Range(0,70).Select(i=>$"app{i}.exe").ToArray();Assert.Equal(many.Take(64),AppExclusions.Normalize(many));
    Assert.False(AppExclusions.Accepts("PID 7"));Assert.True(AppExclusions.Accepts("PID 7.exe"));Assert.Equal(64,AppExclusions.MaximumApps);
    Assert.True(AppExclusions.Keys(["chrome.exe","Steam.exe"]).SetEquals(["CHROME.EXE","STEAM.EXE"]));}
[Fact] public void Excluded_apps_round_trip_and_a_file_without_them_reads_empty(){
    var s=new UserSettings();s.ExcludedApps.AddRange(["chrome.exe","Steam.exe"]);Assert.Equal(["chrome.exe","Steam.exe"],SettingsCodec.Read(SettingsCodec.Write(s)).Settings.ExcludedApps);
    foreach(var json in new[]{"{}","{\"ExcludedApps\":null}","{\"ExcludedApps\":[\"\",\"a*\",\"x.exe\",\"X.EXE\"]}"})Assert.Null(SettingsCodec.Read(json).Error);
    Assert.Empty(SettingsCodec.Read("{}").Settings.ExcludedApps);Assert.Equal(["x.exe"],SettingsCodec.Read("{\"ExcludedApps\":[\"\",\"a*\",\"x.exe\",\"X.EXE\"]}").Settings.ExcludedApps);}
// HistoryClearTests
[Fact] public void Clear_boundaries(){ var t=new DateTimeOffset(2026,11,7,10,31,45,TimeSpan.Zero);
    var older=HistoryClear.Create("Chrome.exe",allTime:false,t);
    Assert.Equal(("CHROME.EXE",new DateTimeOffset(2026,10,31,10,31,0,TimeSpan.Zero),t,false),(older.AppKey,older.Before,older.Requested,older.AllTime));Assert.Equal(older.Before,older.AlertCutoff);
    var all=HistoryClear.Create(null,allTime:true,t);Assert.Null(all.AppKey);Assert.Equal(new DateTimeOffset(2026,11,7,10,32,0,TimeSpan.Zero),all.Before);Assert.True(all.AllTime);Assert.Equal(t,all.AlertCutoff);
    Assert.True(older.Covers("chrome.EXE",older.Before.AddMinutes(-1)));Assert.False(older.Covers("chrome.exe",older.Before));Assert.False(older.Covers("other.exe",older.Before.AddMinutes(-1)));}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~AppKeyTests|FullyQualifiedName~HistoryClearTests"`. Expected: the build fails with `error CS0103` for `AppKey`, `AppExclusions` and `HistoryClear`, and `error CS1061` for `ExcludedApps`.
- [ ] **Step 3: Implement `AppKey.cs`, `HistoryClear.cs`, the delegation, the `app_key` registration and the setting**
- [ ] **Step 4: Run the Step 2 command, the Connections and Processes Core tests (`--filter "FullyQualifiedName~Connection|FullyQualifiedName~Process"`), then the Core baseline.** Expected: PASS, PASS, then no output.
- [ ] **Step 5: Commit, push and open the draft PR**

```bash
git add tests/DeadWare.Tests/AppKeyTests.cs tests/DeadWare.Tests/HistoryClearTests.cs
git commit -m "test(core): one app key, the Don't record list and clear boundaries"
git add src/DeadWare.Core/AppKey.cs src/DeadWare.Core/HistoryClear.cs src/DeadWare.Core/ProcessTable.cs src/DeadWare.Core/AlertStore.cs src/DeadWare.Core/Monitoring.cs
git commit -m "feat(core): AppKey, AppExclusions and HistoryClear"
git push -u origin HEAD
```

Open the draft PR through the GitHub tools: base `main`, head `claude/connections-0.17.0`, title "First network use alerts and history controls (0.17.0)". The body names the spec and this plan, records Step 0's warning count and moved anchors, says that Task 2 records the CI fill times, and ends with the attribution in Global Constraints. Run (CI). Expected: green.

---

### Task 2: CI measurement: the clear measurement's two fills (gate)

Spec: section 7 → New CI measurements (full Connections and Usage clear durations) and CI time; *Don't record these apps and Clear history…* → Tests → Real Windows.

**Files:**
- Create: `tests/DeadWare.App.Tests/RealWindows/HistoryClearMeasurementTests.cs`
- Modify: `tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs` (move the fill into a helper, unchanged)

**Interfaces:**
- Consumes: 0.16.0's fill (step 2 of `Connection_history_rates_and_read_timings`); `ConnectionHistoryStore`, `UsageHistoryStore`, `MeasurementRecorder.Write`, `GitHubWindowsFactAttribute`.
- Produces:
  - `internal sealed record ConnectionFill(double Seconds, long Hours, long TrafficRows, long FileBytes, bool Evicted, DateTimeOffset End);` and `internal static ConnectionFill FillToCap(ConnectionHistoryStore store, DateTimeOffset start, Func<int, string> name)` on `ConnectionHistoryMeasurementTests`. It is 0.16.0's 20-pair per-minute fill exactly, with pair i's process name `name(i)`. 0.16.0's measurement calls it with `i => $"app{i}.exe"`, and `connection-history.json` keeps every field and value meaning.
  - `[Collection("RealWindows")] public sealed class HistoryClearMeasurementTests` with `[GitHubWindowsFact] public void History_clear_timing_on_large_files()`, writing `history-clear.json` through `MeasurementRecorder.Write("history-clear", …)`. Task 9 adds `clears`.

The fills (this task):
- `fills.connections`: `{ seconds, hours, trafficRows, fileBytes, evicted }` from `FillToCap(new ConnectionHistoryStore(tempPath), floor(UtcNow) − 10 days, i => i < 2 ? "app0.exe" : $"app{i}.exe")`. Pairs 0 and 1 share `app0.exe`, so it holds 2 of the 20 pairs: the "about 10 % of the rows" of the one-app clear.
- `fills.usage`: `{ seconds, minutes, applicationRows, fileBytes, evicted }` from a `UsageHistoryStore` on a temp file filled to its 256 MiB cap with 50 apps a minute. Each minute holds one CPU metric row and 50 application rows: five spellings of one app (`app0.exe`, `APP0.EXE`, `App0.exe`, `aPP0.exe`, `app0.EXE`, the one-app clear's 10 %) and `app1.exe`–`app45.exe`. It appends 80 minutes (4,080 rows, within `MaximumBatchRows`) per `Append`, with `now` = the batch's end + 1 min, from `floor(UtcNow) − 120 days`. It stops when the main file is at least `MaximumDatabaseBytes − 4 MiB` or at the current minute. `evicted` = `EarliestRetained` later than the start.
- Each fill is timed with a `Stopwatch`. Both files are deleted at the end.

- [ ] **Step 1: Move the fill and write the measurement**
- [ ] **Step 2: Build.** Expected: 0 errors. A measurement has no red step.
- [ ] **Step 3: Commit, push and run in CI**

```bash
git add tests/DeadWare.App.Tests/RealWindows/HistoryClearMeasurementTests.cs tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs
git commit -m "test(history): time the clear measurement's two full-file fills"
```

Run (CI). Expected: green; the `DeadWare-measurements` artifact holds `history-clear.json` and an unchanged `connection-history.json`.
- [ ] **Step 4: Record the numbers and check the gate (controller, GitHub tools)**

Add to the PR description the run link, `fills.connections.seconds`, `fills.usage.seconds` and the build job's total duration against its 20-minute limit.

**Gate.** Stop before Task 3 and report to the user, with these numbers, when either holds: the build job was cancelled at its 20-minute limit, or this measurement threw. Do not shrink the files or the loads, and do not raise the job limit: the spec sends that choice to the user.

---

## 0.17.0 — First network use alerts: Core

### Task 3: The rule, the Don't alert list, settings and budget applications (Core)

Spec: *First network use alerts* → The rule (all of it), Who counts (`IsLearnable`), FirstUseTracker (Learning end), Balloon (text rules), Interactions (none here), alerts.db → Code changes (`AlertEpisode.Application`, `AlertEvaluator.Start`), The alert worker → Evaluator; its Core tests listed below.

**Files:**
- Modify: `src/DeadWare.Core/AlertRules.cs`: `AlertKind` :5, `AlertEpisode` :11-12, `Normalize` :28-56 and :59-69, `TargetName` :79-89, `Describe` :109-114.
- Create: `src/DeadWare.Core/FirstNetworkUse.cs`
- Modify: `src/DeadWare.Core/Monitoring.cs`: `UserSettings` (:390 `Alerts`), `SettingsCodec.Read` (after `AlertRules.Normalize`, :449).
- Modify: `src/DeadWare.Core/AlertEvaluator.cs`: `ObserveMinute` :109, `Start` :189.
- Test: `tests/DeadWare.Tests/FirstNetworkUseTests.cs` (new), `tests/DeadWare.Tests/AlertRuleTests.cs`, `tests/DeadWare.Tests/AlertEvaluatorTests.cs`

**Interfaces:**
- Consumes: Task 1 `AppKey`.
- Produces:
  - `public enum AlertKind { Metric, NetworkRate, ApplicationDaily, Battery, FirstNetworkUse }` (value 4).
  - `AlertEpisode(…, double Peak, bool Acknowledged = false, string? Application = null)`.
  - `AlertRules.Normalize(AlertRule?)`: the new kind gives Target "", Threshold 1, Minutes 1. The list overload keeps only the first FirstNetworkUse rule (it counts toward the 64). `TargetName` → "First network use"; `Describe` → "An app uses the network for the first time". `public static AlertRule DefaultFirstNetworkUse(DateTimeOffset created) => new("first-network-use", AlertKind.FirstNetworkUse, "", 1, 1, true, created);`
  - `AlertEvaluator`: `ObserveMinute` skips kind 4 beside Battery; `Start` sets `Application = rule.Target` for ApplicationDaily episodes.
  - In `FirstNetworkUse.cs`, the spec's records verbatim: `FirstUseSource`, `NetworkUse`, `ApplicationSeen`, `KnownApplication`, `FirstUseMetadata`, `FirstUseState`, `FirstUseChanges`, `FirstUseGroup`, `FirstUseStatus`.
  - `public static class FirstNetworkUse`:
    - `public const string RuleId = "first-network-use";`
    - `public const int MaximumKnown = 8192, MaximumIgnored = 256, MaximumUses = 1024, MaximumGroupNames = 64, MaximumSeedNames = 65536, MaximumChanges = 1024, ForgetDays = 400;`
    - `public static readonly TimeSpan GroupWindow = TimeSpan.FromSeconds(60);`
    - `public static bool IsLearnable(string? name)`: the key is neither "UNKNOWN APPLICATION" nor a PID name.
    - `public static bool IsLoopback(string remoteAddress)`: starts with "127." or equals "::1".
    - `public static List<string> NormalizeIgnored(IEnumerable<string?>? names)`: `NormalizeName` each, drop the unlearnable, drop later duplicates by key, at most 256.
    - `public static string Summary(string name) => $"{name} used the network for the first time";`
    - `public static DateTimeOffset LearningEnd(DateTimeOffset start, TimeZoneInfo zone)`: the local midnight that begins the third calendar day after start's local date, an invalid midnight moved to 01:00, returned in UTC (offset zero).
    - `public static string BalloonText(FirstUseGroup group, TimeZoneInfo zone, CultureInfo culture)`: the four forms of the spec's Balloon, the time `group.First` in local "t" format, names over 40 text elements cut to 39 (`StringInfo`) and "…".
  - `UserSettings`: `public bool FirstUseRuleAdded { get; set; } = true;`, `public List<string> FirstUseIgnoredApps { get; set; } = new();`, and `Alerts` initialized to `new() { AlertRules.DefaultFirstNetworkUse(DateTimeOffset.UtcNow) }`.
  - `SettingsCodec.Read`, after `s.Alerts = AlertRules.Normalize(s.Alerts)`: when the JSON has no `FirstUseRuleAdded` (`document.RootElement.TryGetProperty`) or it is false, append the default rule if no FirstNetworkUse rule is present and fewer than 64 rules are. Always set the flag to true. `s.FirstUseIgnoredApps = FirstNetworkUse.NormalizeIgnored(s.FirstUseIgnoredApps)`, with no `Error`.

- [ ] **Step 1: Write the failing tests and change the one**

```csharp
// FirstNetworkUseTests. En = CultureInfo.GetCultureInfo("en-US"); Utc = TimeZoneInfo.Utc; L = new DateTimeOffset(2026,11,7,23,59,0,TimeSpan.Zero); T = L.ToString("t",En).
[Fact] public void App_keys_are_upper_case_normalized_names_and_pid_and_unknown_names_are_never_learnable(){
    Assert.Equal("CHROME.EXE",AppKey.Of(" Chrome.exe\u0001 "));
    Assert.All(new[]{"PID 4","pid 4294967295","Unknown application",""},n=>Assert.False(FirstNetworkUse.IsLearnable(n)));
    Assert.All(new[]{"PID 4.exe","PID.exe",new string('a',256)},n=>Assert.True(FirstNetworkUse.IsLearnable(n)));
    Assert.Equal(["chrome.exe"],FirstNetworkUse.NormalizeIgnored(["chrome.exe","CHROME.EXE","PID 4",""," chrome.exe "]));
    Assert.Equal(256,FirstNetworkUse.NormalizeIgnored(Enumerable.Range(0,300).Select(i=>$"app{i}.exe")).Count);Assert.Empty(FirstNetworkUse.NormalizeIgnored(null));}
[Fact] public void Learning_ends_at_local_midnight_that_begins_the_third_day_after_the_start_day(){
    var plus1=TimeZoneInfo.CreateCustomTimeZone("plus1",TimeSpan.FromHours(1),"plus1","plus1");var thursday=new DateTimeOffset(2026,11,5,0,0,0,TimeSpan.FromHours(1));
    Assert.Equal(thursday,FirstNetworkUse.LearningEnd(new DateTimeOffset(2026,11,2,9,0,0,TimeSpan.FromHours(1)),plus1));
    Assert.Equal(thursday,FirstNetworkUse.LearningEnd(new DateTimeOffset(2026,11,2,23,50,0,TimeSpan.FromHours(1)),plus1));Assert.Equal(TimeSpan.Zero,FirstNetworkUse.LearningEnd(thursday,plus1).Offset);
    // Spring: a custom zone that moves 02:00 to 03:00 on Tuesday 2026-11-03 → the end is Thursday 00:00 at the post-change offset.
    // Gap: a custom zone whose clocks jump 00:00 → 01:00 on Thursday 2026-11-05 → the end is Thursday 01:00 local.
    // The returned value's UtcTicks are the same whichever zone later converts it.
}
[Fact] public void Balloon_text_names_one_app_lists_up_to_three_and_counts_the_rest_within_250_characters(){
    string B(int count,params string[] names)=>FirstNetworkUse.BalloonText(new(L,names,count),Utc,En);
    Assert.Equal($"a.exe used the network for the first time at {T}. Open Alerts to review.",B(1,"a.exe"));
    Assert.Equal($"2 apps used the network for the first time from {T}: a.exe and b.exe. Open Alerts to review.",B(2,"a.exe","b.exe"));
    Assert.Equal($"3 apps used the network for the first time from {T}: a.exe, b.exe and c.exe. Open Alerts to review.",B(3,"a.exe","b.exe","c.exe"));
    Assert.Equal($"4 apps used the network for the first time from {T}: a.exe, b.exe, c.exe and 1 more. Open Alerts to review.",B(4,"a.exe","b.exe","c.exe","d.exe"));
    string n256=new string('n',256);Assert.Contains(new string('n',39)+"… used",B(1,n256));
    Assert.Contains(new string('p',38)+"\U0001F600… used",B(1,new string('p',38)+"\U0001F600tail.exe"));   // 39 text elements; the pair stays whole
    Assert.Equal(225,B(8192,n256,n256,n256).Length);Assert.Equal(229,B(999_999,n256,n256,n256).Length);}
// AlertRuleTests
[Fact] public void First_network_use_rules_normalize_to_one_rule_with_no_target_and_a_fixed_description(){ var now=DateTimeOffset.UnixEpoch.AddDays(1);
    var r=AlertRules.Normalize(new AlertRule("x",AlertKind.FirstNetworkUse,"chrome.exe",500,15,false,now))!;Assert.Equal(("",1d,1,false),(r.Target,r.Threshold,r.Minutes,r.Enabled));
    Assert.Equal(["a","b"],AlertRules.Normalize([new AlertRule("a",AlertKind.FirstNetworkUse,"",1,1,true,now),new("b",AlertKind.Metric,"cpu",90,5,true,now),new("c",AlertKind.FirstNetworkUse,"",1,1,true,now)]).Select(x=>x.Id));
    Assert.Equal("First network use",AlertRules.TargetName(AlertKind.FirstNetworkUse,""));Assert.Equal("An app uses the network for the first time",AlertRules.Describe(r));
    Assert.Equal(new AlertRule("first-network-use",AlertKind.FirstNetworkUse,"",1,1,true,now),AlertRules.DefaultFirstNetworkUse(now));Assert.Equal(4,(int)AlertKind.FirstNetworkUse);}
[Fact] public void Settings_add_the_first_network_use_rule_once_and_normalize_the_dont_alert_list(){
    var fresh=new UserSettings();Assert.Equal("first-network-use",Assert.Single(fresh.Alerts).Id);Assert.True(fresh.FirstUseRuleAdded);
    // Rules(n) = a JSON array of n valid metric rules "r0".."r{n-1}".
    var s63=SettingsCodec.Read($"{{\"Alerts\":{Rules(63)}}}").Settings;Assert.Equal(64,s63.Alerts.Count);Assert.Equal(AlertKind.FirstNetworkUse,s63.Alerts[^1].Kind);Assert.True(s63.FirstUseRuleAdded);
    var s64=SettingsCodec.Read($"{{\"Alerts\":{Rules(64)}}}").Settings;Assert.Equal(64,s64.Alerts.Count);Assert.DoesNotContain(s64.Alerts,a=>a.Kind==AlertKind.FirstNetworkUse);Assert.True(s64.FirstUseRuleAdded);
    Assert.Empty(SettingsCodec.Read($"{{\"Alerts\":{Rules(0)},\"FirstUseRuleAdded\":true}}").Settings.Alerts);
    // A file whose Alerts hold one FirstNetworkUse rule "mine" and no flag reads with exactly that rule.
    foreach(var json in new[]{"{}","{\"FirstUseIgnoredApps\":null}","{\"FirstUseIgnoredApps\":[\"PID 4\",\"a.exe\",\"A.EXE\"]}"})Assert.Null(SettingsCodec.Read(json).Error);
    Assert.Equal(["a.exe"],SettingsCodec.Read("{\"FirstUseIgnoredApps\":[\"PID 4\",\"a.exe\",\"A.EXE\"]}").Settings.FirstUseIgnoredApps);
    var s=new UserSettings{FirstUseIgnoredApps=["a.exe"]};var back=SettingsCodec.Read(SettingsCodec.Write(s)).Settings;
    Assert.Equal(s.Alerts,back.Alerts);Assert.Equal(["a.exe"],back.FirstUseIgnoredApps);Assert.True(back.FirstUseRuleAdded);}
// Changed, Settings_keep_rules_and_the_notification_switch_and_default_to_none_and_on (:68): :71's Assert.Empty(fresh.Alerts) becomes
//   Assert.Equal("first-network-use",Assert.Single(fresh.Alerts).Id); :78 expects ["a","c","first-network-use"]; :83 becomes
//   Assert.Equal(AlertKind.FirstNetworkUse,Assert.Single(SettingsCodec.Read("""{"Alerts":null}""").Settings.Alerts).Kind).
// AlertEvaluatorTests
[Fact] public void First_network_use_rules_are_never_evaluated_by_minutes_or_battery_and_budget_episodes_carry_their_application(){
    // Rules: DefaultFirstNetworkUse and an ApplicationDaily "chrome.exe" budget of 100 bytes. ObserveMinute with a CPU row and 200 chrome.exe bytes, and ObserveBattery at 5 % on battery:
    // no episode has Kind FirstNetworkUse, and the budget's started episode has Application "chrome.exe".
}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~FirstNetworkUseTests|FullyQualifiedName~AlertRuleTests|FullyQualifiedName~AlertEvaluatorTests"`. Expected: the build fails with `error CS0117` for `AlertKind.FirstNetworkUse` and `AlertRules.DefaultFirstNetworkUse`, `error CS0103` for `FirstNetworkUse`, and `error CS1061` for `FirstUseRuleAdded` and `Application`.
- [ ] **Step 3: Implement the kind, the records, `FirstNetworkUse`, the settings and the evaluator changes**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/FirstNetworkUseTests.cs tests/DeadWare.Tests/AlertRuleTests.cs tests/DeadWare.Tests/AlertEvaluatorTests.cs
git commit -m "test(alerts): first network use rule, learning end, balloon text and settings"
git add src/DeadWare.Core/AlertRules.cs src/DeadWare.Core/FirstNetworkUse.cs src/DeadWare.Core/Monitoring.cs src/DeadWare.Core/AlertEvaluator.cs
git commit -m "feat(alerts): the first network use rule, added once"
```

Run (CI). Expected: green; `BatteryUiTests.cs:614` passes unchanged.

---

### Task 4: `FirstUseTracker` (Core)

Spec: *First network use alerts* → FirstUseTracker (all of it), Known apps and seeding (Merge, Forgetting), Interactions (Don't record, first step 4); its `FirstUseTrackerTests`.

**Files:**
- Create: `src/DeadWare.Core/FirstUseTracker.cs`
- Test: `tests/DeadWare.Tests/FirstUseTrackerTests.cs`

**Interfaces:**
- Consumes: Task 3's records, `FirstNetworkUse` and `AlertKind.FirstNetworkUse`; Task 1 `AppKey`.
- Produces: `public sealed class FirstUseTracker(TimeZoneInfo zone, TimeSpan? groupWindow = null)` (window default `FirstNetworkUse.GroupWindow`) with the spec's members:
  - `public void Configure(AlertRule? rule, IEnumerable<string> ignored, IEnumerable<string> excluded)`: names or keys; each goes through `AppKey.Of`.
  - `public void Restore(FirstUseState state)`
  - `public void Seed(FirstUseSource source, IReadOnlyList<ApplicationSeen>? names, DateTimeOffset now)`
  - `public IReadOnlyList<AlertEpisode> Observe(DateTimeOffset at, IReadOnlyList<NetworkUse> uses, DateTimeOffset now)`
  - `public FirstUseGroup? TakeDueGroup(DateTimeOffset now)`, `public void Expire(DateTimeOffset now)`, `public void Forget(string? key, DateTimeOffset notBefore)`
  - `public FirstUseChanges TakeChanges(int maximum)` and `public FirstUseStatus Status { get; }`
  - Plan helpers for the worker: `public bool NeedsSeed(FirstUseSource source)` (that store's marker is unset), `public bool HasChanges { get; }` and `public DateTimeOffset? GroupDue { get; }` (the open group's due time).

The spec's Observe steps 1–4, Groups, TakeChanges, Expire and Forget fix the behaviour. The plan decides:
- Times in known rows are whole UTC minutes (`UsageMetrics.Floor`).
- `TakeChanges` keeps changes in recording order. A metadata value (learning start and end, a seed marker) is returned only in the call that takes the last upsert or delete recorded before it, so "each store's marker goes in the transaction of its last chunk".
- `Status.Seeded` is true once both markers are set; `KnownApps` is null until `Restore`.

- [ ] **Step 1: Write the failing tests**

```csharp
// Z = TimeZoneInfo.Utc; S = 2026-11-02 09:00Z; E = FirstNetworkUse.LearningEnd(S,Z) (2026-11-05 00:00Z). R = AlertRules.DefaultFirstNetworkUse(S).
// Tr(AlertRule? rule = null, IEnumerable<string>? ignored = null, IEnumerable<string>? excluded = null, bool noRule = false) => a tracker with a 60 s window,
//   configured with noRule ? null : rule ?? R and the two lists (empty when null), restored from new FirstUseState(null,null,null,null,[]).
// U(name, at) = new NetworkUse(name, at). Obs(t, at, params NetworkUse[] u) => t.Observe(at, u, at). Ups(t) => t.TakeChanges(1024).Upserts.
[Fact] public void Apps_seen_during_learning_are_learned_quietly_and_after_learning_alert_once(){ var t=Tr();
    Assert.Empty(Obs(t,S,U("a.exe",S)));Assert.Empty(Obs(t,E.AddMinutes(-1),U("b.exe",E.AddMinutes(-1))));
    var e=Assert.Single(Obs(t,E.AddMinutes(5),U("c.exe",E.AddMinutes(4.5))));
    Assert.Equal((AlertKind.FirstNetworkUse,E.AddMinutes(4.5),(DateTimeOffset?)E.AddMinutes(4.5),0d,"c.exe","c.exe used the network for the first time","first-network-use"),
        (e.Kind,e.Start,e.End,e.Peak,e.Application,e.Summary,e.RuleId));Assert.DoesNotContain(Ups(t),k=>k.Key=="C.EXE");
    Assert.Empty(Obs(t,E.AddMinutes(9),U("C.exe",E.AddMinutes(9))));Assert.Equal(E.AddMinutes(9),Assert.Single(Ups(t),k=>k.Key=="C.EXE").Last);}
[Fact] public void Seeded_names_merge_by_key_never_alert_never_evict_and_a_failed_store_stays_unseeded(){ var t=Tr(excluded:["ex.exe"]);Obs(t,S,[]);var now=E.AddDays(1);
    t.Seed(FirstUseSource.Usage,[new("chrome.exe",now.AddDays(-9),now.AddDays(-2)),new("Chrome.exe",now.AddDays(-10),now.AddDays(-3)),new("PID 4",now,now),
        new("Unknown application",now,now),new("old.exe",now.AddDays(-500),now.AddDays(-401)),new("ex.exe",now,now)],now);
    var c=t.TakeChanges(1024);var k=Assert.Single(c.Upserts);Assert.Equal(("CHROME.EXE",now.AddDays(-10),now.AddDays(-2)),(k.Key,k.First,k.Last));Assert.Equal(now,c.Metadata.SeededUsage);
    Assert.Empty(Obs(t,now,U("chrome.exe",now)));Assert.Single(Obs(t,now,U("new.exe",now)));   // learning has ended: only the seeded name is quiet
    t.Seed(FirstUseSource.Connections,null,now);Assert.True(t.NeedsSeed(FirstUseSource.Connections));t.Seed(FirstUseSource.Connections,[],now);Assert.False(t.NeedsSeed(FirstUseSource.Connections));
    // A fresh tracker that knows 10 keys, seeded with 8,200 names: it knows 8,192 keys, the 10 included, the seeded ones being those with the latest last seen; no delete is recorded.
}
[Fact] public void Learning_starts_at_the_first_available_capture_and_survives_rule_switch_removal_and_re_adding(){ var t=Tr();Obs(t,S,[]);var start=t.Status.LearningStart;Assert.Equal(S,start);
    t.Configure(R with{Id="other"},[],[]);t.Configure(R with{Enabled=false},[],[]);t.Configure(null,[],[]);t.Configure(R,[],[]);Obs(t,S.AddHours(1),[]);
    Assert.Equal((start,E),(t.Status.LearningStart,t.Status.LearningEnd));}
[Fact] public void A_switched_off_rule_learns_quietly_and_no_rule_learns_nothing(){
    var off=Tr(R with{Enabled=false});Assert.Empty(Obs(off,E.AddHours(1),U("a.exe",E.AddHours(1))));Assert.Single(Ups(off));
    var none=Tr(noRule:true);Assert.Empty(Obs(none,E.AddHours(1),U("a.exe",E.AddHours(1))));Assert.Empty(none.TakeChanges(1024).Upserts);Assert.Null(none.Status.LearningStart);}
[Fact] public void Excluded_pid_and_unknown_names_are_never_learned_or_alerted_and_dont_alert_names_are_learned_without_an_episode(){
    var t=Tr(ignored:["quiet.exe"],excluded:["ex.exe"]);Obs(t,S,[]);
    Assert.Empty(Obs(t,E.AddHours(1),U("PID 4",E),U("Unknown application",E),U("ex.exe",E),U("quiet.exe",E)));Assert.Equal(["QUIET.EXE"],Ups(t).Select(k=>k.Key));
    t.Configure(R,["quiet.exe"],[]);Assert.Single(Obs(t,E.AddHours(2),U("ex.exe",E.AddHours(2))));   // excluded from the start, removed: alerts after learning
    // "known.exe" known before it is excluded keeps its last seen while excluded and stays known after removal (no episode at its next use).
}
[Fact] public void Apps_within_60_seconds_of_the_first_share_one_group_and_an_app_at_the_due_time_opens_the_next(){ var t=Tr();Obs(t,S,[]);var w=E.AddHours(1);
    t.Observe(w,[U("a.exe",w)],w);t.Observe(w,[U("b.exe",w)],w.AddMilliseconds(59_999));t.Observe(w,[U("c.exe",w)],w.AddSeconds(60));
    Assert.Null(t.TakeDueGroup(w.AddSeconds(59)));var g=t.TakeDueGroup(w.AddSeconds(60))!;Assert.Equal((w,2),(g.First,g.Count));Assert.Equal(["a.exe","b.exe"],g.Names);
    // At close, entries now ignored, excluded or no longer known are dropped; a group emptied that way returns null; with the rule switched off it returns null;
    // 70 apps in one group: Names holds the first 64 in detection order and Count is 70.
}
[Fact] public void Forgetting_one_key_or_every_key_makes_the_next_use_alert_keeps_the_learning_start_and_leaves_the_open_group(){
    // After learning, a.exe and b.exe alert into one open group. Forget("A.EXE", n): the group closes with ["b.exe"]; a.exe's next use gives one episode; observed again in the same
    //   window it is listed once. A pending upsert of A.EXE is dropped and a pending delete stays. An episode whose FirstSeen is before n starts at n.
    // Forget(null, n): Status.KnownApps is 0; LearningStart, LearningEnd and both seed markers are unchanged; NeedsSeed is false for both stores.
}
[Fact] public void A_full_known_list_forgets_the_app_seen_least_recently_to_make_room(){
    // 8,192 known keys, two tied for the oldest last seen ("K0001", "K0000"): the 8,193rd app records one delete, "K0000" (ties by key, ordinal), and is handled as new (an episode after learning).
}
[Fact] public void Apps_unseen_for_400_days_are_forgotten_at_the_next_expire_and_alert_again_in_the_same_session(){
    // A key last seen at n − 400 days − 1 minute: Expire(n) forgets it and drops its pending upsert; its next use after learning gives one episode.
}
[Fact] public void Changes_are_taken_in_bounded_batches_and_a_key_never_has_both_an_upsert_and_a_delete(){
    // 3,000 seeded upserts: TakeChanges(1024) three times gives 1024, 1024, 952; SeededUsage appears only in the third. A key upserted then forgotten by the list cap appears only as a delete;
    // forgotten then seen again, only as an upsert. Changes not taken stay for the next call.
}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~FirstUseTrackerTests"`. Expected: the build fails with `error CS0246` for `FirstUseTracker`.
- [ ] **Step 3: Implement `FirstUseTracker.cs`**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/FirstUseTrackerTests.cs
git commit -m "test(alerts): first-use learning, seeding, groups, forgetting and change batches"
git add src/DeadWare.Core/FirstUseTracker.cs
git commit -m "feat(alerts): FirstUseTracker"
```

Run (CI). Expected: green.

---

### Task 5: `alerts.db` gains the first-use tables and `pending_clear` (Core)

Spec: *First network use alerts* → alerts.db (all of it); *Don't record these apps and Clear history…* → How each store clears → Alert log → Table; the `AlertStoreTests` of both sections listed below.

**Files:**
- Modify: `src/DeadWare.Core/AlertStore.cs`: `Initialize` :28-49, `Append` :52-89, `Read` :92-106, `Open(string)` :109-119, `EvictOldestClosed` :134-143, `Validate` :145-152.
- Test: `tests/DeadWare.Tests/AlertStoreTests.cs`

**Interfaces:**
- Consumes: Task 3's records and `AlertEpisode.Application`; Task 1's `app_key` registration and `AppKey`.
- Produces:
  - `Initialize`'s one transaction creates the spec's `known_apps`, `episode_apps` and `alert_metadata` (SQL verbatim) and `CREATE TABLE IF NOT EXISTS pending_clear(id INTEGER PRIMARY KEY CHECK(id=1), app_key TEXT, before INTEGER NOT NULL, requested INTEGER NOT NULL)`. On `SQLITE_FULL` it rolls back, calls `EvictOldestClosed` (which deletes links once `episode_apps` exists) and retries once. No closed episode, or a second `SQLITE_FULL`, fails `Initialize`. Then it deletes `episode_apps` rows whose episode no longer exists. `user_version` stays 1.
  - `public void Append(string machine, IReadOnlyList<AlertEpisode> episodes, DateTimeOffset now, FirstUseChanges? firstUse = null)`, one transaction in the spec's order 1–4. The early return becomes "no episodes and no changes". Every validation rule in the spec runs before any write.
  - `Read` and `Open(string)` fill `Application` with `LEFT JOIN episode_apps`.
  - `public FirstUseState ReadFirstUse(string machine)`: the four `alert_metadata` keys and at most 8,192 known rows by `last_seen` descending.
  - The metadata keys: `first_use_seed_usage`, `first_use_seed_connections`, `first_use_learning_start`, `first_use_learning_end`.

- [ ] **Step 1: Write the failing tests**

```csharp
// The class's Fixture (:111-118): Store() = fixture.Open(); Cap() = fixture.Open(2 * 1024 * 1024). At 64 KiB or 256 KiB, evicting a tenth of the closed episodes
//   frees 0 to 2 pages, fewer than the four root pages the new tables need; at 2 MiB it frees 11 or more (SQLite 3.53.1).
// V016(fixture.Path, closed, open) writes a file with 0.16's exact schema (Initialize's :38-45 statements and user_version 1) and the given episodes, through raw SQL. M = "DESKTOP-ABCDEFG". Fu(name, start) = a closed kind-4 episode (Summary, Start = End, Peak 0, Application name, RuleId "first-network-use").
// Kn(key, first, last) = new KnownApplication(key, first, last). Ch(upserts, deletes, metadata) = new FirstUseChanges(…). Empty = new FirstUseMetadata(null,null,null,null). Minute0 = a whole UTC minute an hour ago.
[Fact] public void A_version_1_file_gains_the_first_use_tables_without_a_version_change(){ // V016 with two episodes; Initialize:
    // sqlite_master holds known_apps, episode_apps, alert_metadata and pending_clear; PRAGMA user_version is 1; the episodes row of sqlite_master has the same sql text as before.
}
[Fact] public void Initialize_at_the_cap_evicts_closed_episodes_and_retries_the_first_use_tables_once(){
    // V016 filled with closed episodes until SQLITE_FULL at Cap's page count: Cap() (which runs Initialize) passes, the three first-use tables exist, and the oldest tenth of the closed episodes is gone.
    // The same fill with open episodes only: Assert.ThrowsAny<Exception>(Initialize) and no first-use table or pending_clear exists.
}
[Fact] public void First_use_episodes_save_closed_with_their_link_and_known_row_in_one_transaction(){ var s=Store();var t=Minute0;
    s.Append(M,[Fu("chrome.exe",t)],t);var e=Assert.Single(s.Read(M,t.AddDays(-1),t.AddDays(1)).Episodes);Assert.Equal(("chrome.exe",AlertKind.FirstNetworkUse),(e.Application,e.Kind));
    Assert.Empty(s.Open(M));var k=Assert.Single(s.ReadFirstUse(M).Known);Assert.Equal(("CHROME.EXE",t,t),(k.Key,k.First,k.Last));
    Assert.ThrowsAny<ArgumentException>(()=>s.Append(M,[Fu("b.exe",t)],t,Ch([Kn("bad",t,t)],[],Empty)));Assert.DoesNotContain(s.ReadFirstUse(M).Known,x=>x.Key=="B.EXE");}
[Fact] public void Known_rows_upsert_min_first_and_max_last_deletes_apply_first_and_metadata_is_written_once(){ var s=Store();var t=Minute0;
    s.Append(M,[],t,Ch([Kn("A.EXE",t,t.AddMinutes(5))],[],new(null,null,t,t.AddDays(3))));s.Append(M,[],t,Ch([Kn("A.EXE",t.AddMinutes(-2),t.AddMinutes(1))],[],new(t,null,t.AddDays(1),t.AddDays(4))));
    var st=s.ReadFirstUse(M);Assert.Equal((t.AddMinutes(-2),t.AddMinutes(5)),(st.Known[0].First,st.Known[0].Last));Assert.Equal((t,t.AddDays(3),t),(st.LearningStart,st.LearningEnd,st.SeededUsage));
    s.Append(M,[],t,Ch([Kn("A.EXE",t,t)],["A.EXE"],Empty));Assert.Single(s.ReadFirstUse(M).Known);}   // deletes apply before upserts
[Fact] public void Retention_and_eviction_remove_links_and_known_rows_unseen_for_400_days_and_initialize_sweeps_orphans(){
    // Append at now: links of episodes started before now − 400 d and known rows last seen before it are gone with their episodes. A SQLITE_FULL eviction removes the evicted episodes' links
    // and no known or metadata row. Rows inserted into episode_apps without an episode (as 0.16's retention leaves them) are gone after a reopen's Initialize.
}
[Fact] public void Invalid_first_use_rows_are_rejected_before_any_write(){
    // Each rejected with ArgumentException and nothing written: upsert keys "chrome.exe" (not AppKey.Of of itself), "PID 4", "A\u0001.EXE"; first after last; a time not on a whole minute;
    // kind 4 without Application, with Application "PID 4", with End ≠ Start; an Application " chrome.exe"; 1,025 upserts. A kind-2 episode with Application "PID 4" is accepted.
}
[Fact] public void Eight_thousand_known_apps_of_256_cjk_characters_stay_under_8_MiB(){ // 8,192 keys of 256 CJK characters (distinct), chunks of 1,024, machine M, default cap:
    // after the last Append, Assert.True(store.StorageBytes < 8L*1024*1024).
}
[Fact] public void Eight_thousand_alerted_apps_of_256_cjk_characters_stay_under_32_MiB(){ // 8,192 kind-4 episodes with links and known rows, chunks of 1,024: StorageBytes < 32 MiB.
}
[Fact] public void Alert_store_creates_new_tables_at_the_cap(){
    // A V016 file filled to Cap's page count with an empty freelist (closed episodes) initializes: the oldest tenth of the closed episodes is evicted, and pending_clear and the three
    // first-use tables appear together. The eviction on that 0.16 file, which has no episode_apps, deletes episodes only.
}
// Retention_drops_old_episodes_and_the_cap_evicts_the_oldest_closed_ones_first (:68): as the user decides (Before this task, below).
```

**Before this task (controller).** `Retention_drops_old_episodes_and_the_cap_evicts_the_oldest_closed_ones_first` (:68) cannot pass unchanged. In its 64 KiB file the four new root pages leave room for about 394 of its episodes in one Append, against 577 with 0.16's schema (measured on SQLite 3.53.1), and it appends 400 (:75-77): the first batch throws "full of open episodes". With the spec's three tables it still passes (spec :2613), but `pending_clear` is created in the same transaction (spec, Alert log → Table). This conflicts with the spec's "Kept and named" rule, so the controller takes it to the user before Task 5 starts. Proposal: batches of 300 (`Enumerable.Range(0, 300)`, minutes `batch * 300 + i`, the last `Contains` `c7-299`), every other assertion unchanged, listed in the PR and `docs/validation.md` as a changed test.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~AlertStoreTests"`. Expected: the build fails with `error CS1501` (no overload for `Append` takes 4 arguments) and `error CS1061` (`ReadFirstUse`).
- [ ] **Step 3: Implement the tables, the retry, the sweep, the links, the known rows, the metadata, `ReadFirstUse` and the validation**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, with :68 passing as the user decided, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/AlertStoreTests.cs
git commit -m "test(alerts): first-use tables, links, known rows and the cap retry"
git add src/DeadWare.Core/AlertStore.cs
git commit -m "feat(alerts): known apps, episode links and pending_clear in alerts.db"
```

Run (CI). Expected: green.

---

## 0.17.0 — Don't record and Clear history: Core

### Task 6: Exclusions in the minute folders and in budgets (Core)

Spec: *Don't record these apps and Clear history…* → Where recording stops (Usage history, Application resources, Application traffic budgets), How each store clears → Shared rules (Boundary rule), Usage history and Application resources (Order step 2), Alert log (first step 3); its Core tests `Usage_folder_skips_an_excluded_app_and_keeps_its_baseline`, `Process_folder_counts_an_excluded_app_in_Everything_else`, `Excluded_budget_stops_ends_its_episode_and_reseeds_only_itself`, `EndScope_detaches_open_budget_episodes`.

**Files:**
- Modify: `src/DeadWare.Core/UsageHistory.cs`: `UsageMinuteFolder` :42-180 (`FoldApplications` :138-163).
- Modify: `src/DeadWare.Core/ProcessHistory.cs`: `ProcessMinuteFolder` :39-186 (`Fold` :110-152).
- Modify: `src/DeadWare.Core/AlertEvaluator.cs`: `SeedDay` :65-97, `ObserveApplication` :165-185, new members.
- Test: `tests/DeadWare.Tests/UsageHistoryTests.cs`, `tests/DeadWare.Tests/ProcessMinuteFolderTests.cs`, `tests/DeadWare.Tests/AlertEvaluatorTests.cs`

**Interfaces:**
- Consumes: Task 1 `AppKey`, `HistoryClear.Covers`; 0.15.0's `ProcessGrouping.GpuUse` (the App rows rule).
- Produces:
  - `UsageMinuteFolder`:
    - `public IReadOnlySet<string> Excluded { get; set; } = FrozenSet<string>.Empty;`
    - `public HistoryClear? Boundary { get; set; }`
    - `public void Forget(string? appKey)`: removes the matching names (every name for null) from the open minute; baselines unchanged.
    - In `FoldApplications`, the baseline is stored in `next` (:147) and the deltas computed as today; then a name whose key is in `Excluded`, or that `Boundary.Covers` for the open minute, adds nothing (`continue` after :158).
  - `ProcessMinuteFolder`:
    - `Excluded` and `Boundary`, as above;
    - `public DateTimeOffset? OpenMinute { get; }`;
    - `public void Forget(string appKey)`: moves the app's open-minute totals, GPU-seconds and GPU memory byte-seconds included, into `other`;
    - `public void Discard()`: drops the open minute with no batch.
    - In `Fold`, the processes of an excluded key are grouped by key as today; their app-level values for that activity (CPU, I/O, memory sums, the busiest-engine GPU-seconds and the GPU memory byte-seconds) then go into `other`, never into `apps`. A `Boundary` with a key treats that key as excluded for minutes before `Before`. A `Boundary` for all apps drops the activity whole when its minute is before `Before`, adding no covered seconds.
  - `AlertEvaluator`:
    - `public sealed record AlertExclusionChange(IReadOnlyList<AlertEpisode> Ended, IReadOnlyList<string> Reseed);`
    - `public AlertExclusionChange SetExcluded(IReadOnlySet<string> keys, DateTimeOffset now)`: each ApplicationDaily rule whose `AppKey.Of(Target)` is newly in `keys` ends its open episode at `now` (forgotten, as `Replace` does) and gets day total 0. `Reseed` lists the targets of the rules whose key left `keys`. While excluded, `ObserveApplication` and both `SeedDay` overloads skip the rule. `FiredToday` is never cleared.
    - `public void SeedDay(DateTimeOffset day, IReadOnlyList<UsageApplicationMinute> rows, IReadOnlySet<string> only)`: the rows overload, for the rules whose key is in `only`; other rules' totals are untouched.
    - `public IReadOnlyList<AlertEpisode> EndScope(string? appKey, DateTimeOffset at, bool resetDay)`: ends and forgets each open ApplicationDaily episode in scope (every one for null) at `at`; `resetDay` sets those rules' day totals to 0; `FiredToday` is kept.

- [ ] **Step 1: Write the failing tests**

```csharp
// UsageHistoryTests. Minute0 = the class's Start (:8). P(f, seconds, bytes) calls f.Observe with Sample(seconds) (:10) and one ProcessTraffic (pid 10, gen 1, "chrome.exe",
//   cumulative ReceivedBytes = bytes).
[Fact] public void Usage_folder_skips_an_excluded_app_and_keeps_its_baseline(){ var f=new UsageMinuteFolder();var key=AppExclusions.Keys(["chrome.exe"]);
    P(f,1,100);f.Excluded=key;P(f,2,200);P(f,3,300);f.Excluded=FrozenSet<string>.Empty;P(f,4,400);P(f,5,500);
    Assert.Equal(200,Assert.Single(f.Flush()!.Applications).Received);
    // Added mid-minute: bytes folded before Excluded is set are in the closed batch, none after. Forget("CHROME.EXE") empties the app's open-minute bytes, and the next
    // publication counts only its own delta (the baseline was kept). Boundary = HistoryClear(null, Minute0 + 1 min, …): the open minute's bytes are dropped, baselines still move.
}
// ProcessMinuteFolderTests, with the class's helpers: P(...) samples, A(0, ps, gpuInterval: 1) (:13-14) for the activity and CloseGpu(...) (:19) to close the minute;
//   ex.exe runs two processes on different engines (40 % and 30 %), with GPU memory.
[Fact] public void Process_folder_counts_an_excluded_app_in_Everything_else(){
    // With Excluded = {EX.EXE}: Everything else's CPU, I/O and memory = the unknown-name processes' plus ex.exe's app-level sums; its GPU-seconds add 0.4 × 1 (the busiest
    // engine of the members' summed per-engine use), not 0.7, and its GPU memory byte-seconds add ex.exe's.
    // Every minute total and RankTotal for every rank, GPU included, equal the same minute folded without the list (ProcessHistoryStore.ReadApplications on both).
    // ex.exe is never a listed row, even when it is the busiest by every measure.
}
// AlertEvaluatorTests. B = an ApplicationDaily "chrome.exe" budget of 1,000 bytes; O = another budget "steam.exe".
[Fact] public void Excluded_budget_stops_ends_its_episode_and_reseeds_only_itself(){
    // 1,500 chrome.exe bytes open B's episode. SetExcluded({CHROME.EXE}, t): Ended holds it with End t; OpenEpisodes is empty; later chrome.exe minutes add nothing; SeedDay(rows) skips B.
    // SetExcluded({}, t2): Reseed == ["chrome.exe"]; SeedDay(day, rows, only:{CHROME.EXE}) restores B's total from rows, and O's total is unchanged. FiredToday is kept: B does not fire again today.
    // A failed re-seed read (rows empty) leaves B's total at 0.
}
[Fact] public void EndScope_detaches_open_budget_episodes(){
    // B open; EndScope("CHROME.EXE", t, resetDay:true): returns it with End t; OpenEpisodes lacks it; the next ObserveMinute emits nothing for its id; the day total is 0.
}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~UsageHistoryTests|FullyQualifiedName~ProcessMinuteFolderTests|FullyQualifiedName~AlertEvaluatorTests"`. Expected: the build fails with `error CS1061` for `Excluded`, `SetExcluded` and `EndScope`.
- [ ] **Step 3: Implement the folder members and the evaluator members**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/UsageHistoryTests.cs tests/DeadWare.Tests/ProcessMinuteFolderTests.cs tests/DeadWare.Tests/AlertEvaluatorTests.cs
git commit -m "test(history): excluded apps in the folders and budgets, EndScope"
git add src/DeadWare.Core/UsageHistory.cs src/DeadWare.Core/ProcessHistory.cs src/DeadWare.Core/AlertEvaluator.cs
git commit -m "feat(history): Don't record filters in the minute folders and budgets"
```

Run (CI). Expected: green.

---

### Task 7: Connection writer hooks in Core, bounded maintenance and purge before evict

Spec: *Connections storage* → Writer hooks for 0.17.0 (`Forget`) and Tests (0.17.0, Core); *Don't record these apps and Clear history…* → How each store clears → Connections history (Routine cleanup becomes bounded, Full file); its Core tests `Forget_removes_one_app_or_every_entry_and_keeps_coverage`, `Bounded_maintenance_drains_a_raised_expiry_over_several_appends`, `Full_file_purges_hidden_rows_before_evicting_a_bucket`.

**Files:**
- Modify: `src/DeadWare.Core/ConnectionMinuteFolder.cs` (0.16.0)
- Modify: `src/DeadWare.Core/ConnectionHistoryStore.cs`: `Append` :131-185, `WriteChunk` :200-255 (its `SQLITE_FULL` branch :231-235), `Cleanup` :286-332.
- Test: `tests/DeadWare.Tests/ConnectionMinuteFolderTests.cs`, `tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs`

**Interfaces:**
- Consumes: 0.16.0's folder and its test helpers (`F`, `Pub`, `D`, `Merge`, `Units`); Task 1 `AppKey`.
- Produces:
  - `public int Forget(string? appKey)` on `ConnectionMinuteFolder`: removes from every open unit each entry whose `AppKey.Of(Name)` equals `appKey` (every entry for null). Runs stay; a unit left with no entry and no run is removed; `Held` falls by the entries removed, which it returns. Frozen units are untouched.
  - `public void Append(IReadOnlyList<ConnectionHistoryRecord> records, IReadOnlyList<ConnectionCoverage> coverage, DateTimeOffset now, int maintenanceTransactions = int.MaxValue)`. A negative value throws `ArgumentOutOfRangeException`. `Cleanup` runs at most that many transactions of at most 64 rows each.
  - On `SQLITE_FULL`, `WriteChunk` first purges one maintenance transaction of rows already hidden by `expiry` (`Cleanup`'s statements) and retries the chunk after each. It calls `EvictOldestBucket` only when no hidden row is left.
  - `private Action? maintenanceCommitted;`, called after each maintenance transaction commits; tests set it by reflection.

- [ ] **Step 1: Write the failing tests**

```csharp
// ConnectionMinuteFolderTests (0.16.0 helpers).
[Fact] public void Forget_removes_one_app_or_every_entry_and_keeps_coverage(){ var f=F();
    Merge(f,Pub(10,9,[D(9.5,name:"Chrome.exe"),D(9.6,name:"other.exe",remote:"9.9.9.9")]));Merge(f,Pub(11,10,[D(10.5,name:"CHROME.EXE",localPort:200)]));int held=f.Held;
    var frozen=F();Merge(frozen,Pub(30,29,[D(29.5,name:"chrome.exe")]));Merge(frozen,Pub(63,62,[]));var before=Units(frozen)[0];Assert.Equal(0,frozen.Forget("CHROME.EXE"));Assert.Same(before,Units(frozen)[0]);
    Assert.Equal(2,f.Forget("CHROME.EXE"));Assert.Equal(held-2,f.Held);   // "Chrome.exe" and "CHROME.EXE" are two entries of one key
    f.FreezeAll();var u=Assert.Single(Units(f));Assert.Equal(["other.exe"],u.Records.Select(r=>r.Delta.Peer.Process.Name));
    Assert.Equal((M0.AddSeconds(9),M0.AddSeconds(11)),(u.Coverage[0].From,u.Coverage[^1].To));
    var all=F();Merge(all,Pub(10,null,[D(9.5)]));Assert.Equal(1,all.Forget(null));Assert.Equal(0,all.Held);all.FreezeAll();Assert.Empty(Units(all));}   // the unit had no run: removed
// ConnectionHistoryStoreTests. Hidden(db) = traffic rows with minute < expiry.
[Fact] public void Bounded_maintenance_drains_a_raised_expiry_over_several_appends(){
    // 1,000 rows at Epoch; store.Append([],[],Epoch.AddDays(31),maintenanceTransactions:2): 2 maintenance commits (seam), Hidden(db) == 1,000 − 128, and Read/ExportCsv/the strip show none of them.
    // store.Append([],[],Epoch.AddDays(31),maintenanceTransactions:0): no commit, Hidden unchanged. Further Appends with 64 reach Hidden 0.
    // The existing unbounded callers (Near_capacity_expiry_… :273-303) pass unchanged.
}
[Fact] public void Full_file_purges_hidden_rows_before_evicting_a_bucket(){
    // On the fixture cap with an empty freelist and a hidden backlog (expiry raised with maintenanceTransactions:0), an Append that hits SQLITE_FULL succeeds; floor is unchanged;
    // the seam counts one maintenance commit before each retry.
}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ConnectionMinuteFolderTests|FullyQualifiedName~ConnectionHistoryStoreTests"`. Expected: the build fails with `error CS1061` (`Forget`) and `error CS1739` (`maintenanceTransactions`).
- [ ] **Step 3: Implement `Forget`, the bound, the seam and the purge-first order**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ConnectionMinuteFolderTests.cs tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs
git commit -m "test(history): folder Forget, bounded maintenance and purge before eviction"
git add src/DeadWare.Core/ConnectionMinuteFolder.cs src/DeadWare.Core/ConnectionHistoryStore.cs
git commit -m "feat(history): bounded connection maintenance and folder Forget"
```

Run (CI). Expected: green.

---

### Task 8: Clearing `connections.db` (Core)

Spec: *Don't record these apps and Clear history…* → How each store clears → Shared rules (marker, clear in progress, secure_delete, yield to readers, chunks, failure) and Connections history (Marker, `BeginClear`, One app hidden at once, One-app step, All-apps step, End); its Core tests `One_app_clear_hides_at_once_then_deletes_rows_and_receipts`, `All_apps_clear_raises_expiry_and_purges_below_it`, `Clear_marker_survives_reopen_and_resumes`, `Clear_steps_stay_inside_the_transaction_and_journal_budgets`.

**Files:**
- Modify: `src/DeadWare.Core/HistoryClear.cs` (`HistoryClearProgress`, `PendingConnectionClear`)
- Modify: `src/DeadWare.Core/ConnectionHistoryStore.cs`:
  - `Initialize` :66-92 reads the marker;
  - `GroupedWhere` (0.16.0) and `Arguments` :400;
  - the earliest-retained query in `Read` :371-379;
  - `Open` :468-489 (`secure_delete`);
  - new `BeginClear` and `ClearStep`.
- Test: `tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 `HistoryClear`; Task 7's bounded `Cleanup`; 0.16.0's `GroupedWhere`, `temp.geo`, `temp.grouped`, the strip and `ExportDestinationsCsv`.
- Produces:
  - `public sealed record HistoryClearProgress(long Done, long? Total, bool Finished) { public int? Percent { get; } }`: null when `Total` is null; 100 when finished; otherwise ⌊100 × Done ÷ Total⌋ capped at 99 (0 for a zero total).
  - `public sealed record PendingConnectionClear(string? AppKey, DateTimeOffset Before);`, which is what this store keeps of a request.
  - On `ConnectionHistoryStore`:
    - `public PendingConnectionClear? PendingClear { get; }`, read under the lock. It is set by `Initialize` from the one `metadata` row whose key starts with `clear-` (`clear-app:<KEY>` or `clear-all`, value `Before` ticks), set by `BeginClear` and cleared by the last step.
    - `public void BeginClear(HistoryClear clear)`: one `CommitBounded` transaction writes the marker, for all apps `expiry = max(expiry, Before)`, and the cleared mark `cleared-all` or `cleared-one` = max(its value, `Before`). An equal pending marker re-commits nothing; a different one throws `InvalidOperationException`.
    - `public HistoryClearProgress ClearStep(TimeSpan budget)`: delete transactions until `budget` has passed on the monotonic clock, always at least one unless it yields.
      - One app: the spec's (minute, id) cursor, 2,048 rows a round through `traffic_time`; matches deleted in transactions of at most 512 rows with their `dedup` receipts and any manifest left without receipts. `Total` = traffic rows with `minute < Before` at the first step; `Done` = rows scanned.
      - All apps: traffic rows with `minute < Before`, then the coverage `Cleanup` now selects, at most 512 a transaction. `Total` at the first step; `Done` = `Total` minus the rows still selected, so the routine purge's deletes count.
      - The transaction size halves on `TransactionBudgetException` for the rest of the clear; at 1 row it throws `IOException("Connection history clear exceeds the bounded transaction budget.")`.
      - `GuardJournal` and the yield check run before each transaction. Yield: when `-wal` is over 4 MiB after a TRUNCATE checkpoint, the step ends early; after 60 s of yielding without a transaction it throws `IOException`.
      - The last transaction, which finds nothing left, deletes the marker; the cleared mark stays. Each transaction is followed by `PRAGMA wal_checkpoint(TRUNCATE)`. The cursor lives in memory; a resumed clear restarts from the first row.
    - `GroupedWhere` gains `AND NOT ($clearKey<>'' AND minute<$clearBefore AND app_key(name)=$clearKey)`, and the earliest-retained query's traffic branch the same predicate. `Arguments` adds both values from `PendingClear` (`''` and 0 when there is no one-app marker), so `Read`, the strip, the Destinations steps, `ExportCsv` and `ExportDestinationsCsv` all hide the rows.
    - While `PendingClear` is non-null, every write connection runs `PRAGMA secure_delete=ON`.
    - Seams set by reflection: `private Func<TimeSpan> clearClock` (monotonic elapsed), `private Action<int>? clearCommitted` (rows deleted per clear transaction, invoked after the commit and before its TRUNCATE checkpoint, so a reader of the `-wal` there sees the transaction's frames) and `private Action<SqliteConnection>? clearConnectionOpened`.

- [ ] **Step 1: Write the failing tests**

```csharp
// C(clear) runs store.ClearStep(TimeSpan.FromMilliseconds(250)) until Finished and returns the step count. Before = Epoch.AddMinutes(10).
[Fact] public void One_app_clear_hides_at_once_then_deletes_rows_and_receipts(){
    // Rows before Before: "Chrome.exe" in batches b1 and b2, "CHROME.EXE" in b3, "other.exe" in b3; and a Chrome row at Before (b4).
    store.BeginClear(new("CHROME.EXE",Before,Before));
    // Read (Rows, Summary, Strip), ExportCsv, ExportDestinationsCsv and EarliestRetainedTime omit the Chrome rows before Before and keep other.exe and the row at Before.
    C(store);
    // SELECT count(*) of traffic and dedup for the Chrome rows before Before: 0; manifests: b3's remains (other.exe keeps its receipt), b1's and b2's are gone; b4 untouched;
    // coverage count unchanged; PendingClear null; no metadata key starts with 'clear-'; 'cleared-one' == Before ticks.
}
[Fact] public void All_apps_clear_raises_expiry_and_purges_below_it(){
    store.BeginClear(new(null,Before,Before));Assert.Equal(Before.UtcTicks,Meta("expiry"));store.Append([],[],Epoch.AddMinutes(20));Assert.Equal(Before.UtcTicks,Meta("expiry"));
    // Read over [Epoch, Before) is empty; after C(store): traffic, coverage, dedup and manifests below Before are gone; rows at or after Before stay; 'cleared-all' == Before ticks.
}
[Fact] public void Clear_marker_survives_reopen_and_resumes(){
    // BeginClear(one app) and one step with a 1 ms budget; PendingClear non-null; a new ConnectionHistoryStore on the file: Initialize passes (user_version 1), PendingClear equals
    // (AppKey, Before) of the request, and C(new store) finishes; PendingClear is null after the last step and non-null before it.
}
[Fact] public void Clear_steps_stay_inside_the_transaction_and_journal_budgets(){
    // 20,000 rows alternating "a.exe" and "b.exe" before Before. clearCommitted (seam) never sees more than 512. With no reader, the -wal is ≤ 8 MiB after each step and when read
    // inside clearCommitted.
    // A pinned reader (a second Pooling=false connection holding a read transaction): the step yields once the -wal passes 4 MiB and the -wal never exceeds 10 MiB; with clearClock
    // advanced past 60 s of yielding, ClearStep throws IOException, and not before. ClearStep(TimeSpan.FromMilliseconds(1)) commits exactly one transaction.
}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ConnectionHistoryStoreTests"`. Expected: the build fails with `error CS1061` for `BeginClear`, `ClearStep` and `PendingClear`.
- [ ] **Step 3: Implement the marker, the hide predicate, `BeginClear`, `ClearStep`, `secure_delete` and the seams**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs
git commit -m "test(history): one-app and all-apps connection clears, marker and budgets"
git add src/DeadWare.Core/HistoryClear.cs src/DeadWare.Core/ConnectionHistoryStore.cs
git commit -m "feat(history): clear connections.db behind a durable marker"
```

Run (CI). Expected: green.

---

### Task 9: Clearing `usage-history.db`, cleared time on both strips, and the clear timings

Spec: *Don't record these apps and Clear history…* → How each store clears → Shared rules and Usage history; The traffic strip after a clear or with an excluded app (Cleared marks, `TrafficStrip` fields); Tests → `Usage_one_app_clear_removes_every_spelling_on_every_machine`, `Usage_all_apps_clear_keeps_device_minutes`, `Usage_clear_marker_is_atomic_and_resumes`, `Strip_marks_time_a_clear_removed`, and Real Windows `History_clear_timing_on_large_files`.

**Files:**
- Modify: `src/DeadWare.Core/HistoryClear.cs` (`HistoryClearMarker`, `UsageClearedMarks`)
- Modify: `src/DeadWare.Core/UsageHistoryStore.cs`: `Initialize` :42-70, `Configure` :356-367 (`secure_delete`), new `BeginClear`, `ClearStep` and `ClearedMarks`, and 0.16.0's `ReadApplicationSeries`.
- Modify: `src/DeadWare.Core/TrafficStrip.cs` (the record) and `src/DeadWare.Core/ConnectionHistoryStore.cs` (`Read`'s strip step 4)
- Modify: `tests/DeadWare.App.Tests/RealWindows/HistoryClearMeasurementTests.cs`
- Test: `tests/DeadWare.Tests/UsageHistoryStoreTests.cs`, `tests/DeadWare.Tests/HistoryClearTests.cs`

**Interfaces:**
- Consumes: Task 1 `HistoryClear`, `AppKey`; Task 8 `HistoryClearProgress` and the `cleared-*` marks; 0.16.0's strip steps.
- Produces:
  - `public static class HistoryClearMarker`:
    - `public static void Write(string path, HistoryClear clear)`: `{"Version":1,"AppKey":<string or null>,"Before":<ISO 8601 UTC>,"Requested":<ISO 8601 UTC>}` to `path + ".tmp"`, `FileStream.Flush(true)`, then `File.Move(tmp, path, overwrite: true)`.
    - `public static HistoryClear? Read(string path)`: null when absent; `InvalidDataException` for bad JSON, another `Version` or invalid fields; an `IOException` passes through.
    - `public static void DeleteLeftover(string path)`: deletes `path + ".tmp"`.
  - `public sealed record UsageClearedMarks(DateTimeOffset? AllAppsBefore, DateTimeOffset? OneAppBefore)` with:
    - `public static UsageClearedMarks None { get; }`;
    - `public static UsageClearedMarks Read(string path)`: missing, invalid or unreadable gives `None`;
    - `public UsageClearedMarks With(HistoryClear clear)`: the larger of old and new per kind;
    - `public void Write(string path)`: `{"Version":1,"AllAppsBefore":…,"OneAppBefore":…}` with the marker's `.tmp`, `Flush(true)` and move.
  - On `UsageHistoryStore` (marker `usage-history.clear.json`, marks `usage-history.cleared.json`, both `Path.ChangeExtension` of the database path):
    - `public bool ClearPending { get; }` and `public HistoryClear? PendingClear { get; }`, from the marker's write until its removal, and from `Initialize` while a valid marker exists;
    - `public bool DiscardedInvalidMarker { get; }`: `Initialize` deleted an invalid marker. A leftover `.tmp` is deleted; an unreadable marker is kept (`ClearPending` true, `PendingClear` null) and `Initialize` still passes;
    - `public UsageClearedMarks ClearedMarks { get; }`, read by `Initialize`;
    - `public void BeginClear(HistoryClear clear)`: writes the marker and throws on failure, deleting nothing;
    - `public HistoryClearProgress ClearStep(TimeSpan budget)`:
      - one app: per machine, the distinct names read once and matched by `AppKey.Of`; per name, the spec's 512-row `DELETE … minute IN (SELECT … LIMIT 512)`;
      - all apps: per machine, the spec's 4,096-row statement through `application_minutes_time`;
      - a checkpoint and the yield check after each transaction; `Total` = the scope's rows at the first step;
      - the last step writes `ClearedMarks.With(clear)` and then deletes the marker;
    - `secure_delete=ON` on write connections while a marker exists;
    - the seams `clearClock`, `clearCommitted` (invoked after each commit and before its checkpoint) and `clearConnectionOpened`, as in Task 8.
  - `TrafficStrip(…, DateTimeOffset? RetainedFrom = null, bool CapacityShortened = false, bool RetainedByClear = false, DateTimeOffset? AppClearedBefore = null)`.
  - Connections `Read`: step 4 also reads `cleared-all` and `cleared-one` in the same transaction. `RetainedByClear` = `RetainedFrom` set, `CapacityShortened` false and `cleared-all` == `expiry`. `AppClearedBefore` = `cleared-one` when after the origin and after `RetainedFrom`.
  - `ReadApplicationSeries`: no minute before `AllAppsBefore` counts in any slot. `RetainedFrom` = the first whole minute at or after `AllAppsBefore` when that is after `from`, with `RetainedByClear` true. `AppClearedBefore` = `OneAppBefore` when after the origin and after `RetainedFrom`.

- [ ] **Step 1: Write the failing tests and add the clear timings**

```csharp
// UsageHistoryStoreTests. Fill(store, machines ["A","B"], apps a minute 64 incl. "Chrome.exe" (A) and "chrome.exe" (B), minutes, metrics and capture gaps).
[Fact] public void Usage_one_app_clear_removes_every_spelling_on_every_machine(){
    // BeginClear(HistoryClear("CHROME.EXE", Before, Before)) and steps: no Chrome row of either spelling or machine before Before; Chrome rows at or after Before stay;
    // metric_minutes and capture_gaps counts unchanged; clearCommitted never sees more than 512; with no reader the -wal read inside clearCommitted is ≤ 8 MiB.
}
[Fact] public void Usage_all_apps_clear_keeps_device_minutes(){ // clearCommitted ≤ 4,096 each; metric_minutes and capture_gaps unchanged; no application row before Before
}
[Fact] public void Usage_clear_marker_is_atomic_and_resumes(){
    // After BeginClear: the JSON's fields equal the request; no .tmp remains; ClearPending is true; a leftover .tmp written by hand is deleted by a new instance's Initialize.
    // One step, then a new instance: ClearPending true until its steps delete the marker; it finishes the clear. Invalid JSON in the marker: Initialize deletes it,
    // DiscardedInvalidMarker is true, ClearPending false, and no row is deleted.
}
// HistoryClearTests
[Fact] public void Strip_marks_time_a_clear_removed(){
    // Connections: an all-apps Older than 7 days clear → RetainedFrom == Before and RetainedByClear; a 30-day Append later (expiry passes Before) → RetainedByClear false;
    //   with CapacityShortened (floor raised above) → false. Two clears of each kind: cleared-all and cleared-one hold the later Before; a reopen still passes Initialize.
    //   A one-app clear sets AppClearedBefore when its Before is after the origin and after RetainedFrom, and null otherwise.
    // Usage history: after an all-apps clear, ReadApplicationSeries counts no minute before AllAppsBefore, RetainedFrom is the first whole minute at or after it, RetainedByClear true;
    //   a one-app clear sets AppClearedBefore; usage-history.cleared.json written as "{" or deleted reads as no clear.
}
```

In `History_clear_timing_on_large_files`, after each fill (Task 2), time two clears on that file: one app (`HistoryClear.Create("app0.exe", allTime: true, UtcNow)`, the app holding about 10 % of the rows), then all apps for all time. Each runs `BeginClear` and then `ClearStep(TimeSpan.FromMilliseconds(250))` until `Finished`, timing each step, with no reader. `largestWalBytes` is the largest `-wal` length read inside the store's `clearCommitted` seam (set by reflection), which runs after each clear transaction commits and before its TRUNCATE checkpoint. `clears` gets four entries `{ store, scope, totalMs, steps, p95StepMs, largestWalBytes, finished, error }`: `store` is "connections" or "usage", `scope` is "oneApp" or "allApps", and `p95StepMs` = `ProcessMeasurementTests.Percentile(stepMs.Order().ToArray(), 0.95)` (the helper indexes a sorted array). The measurement then asserts, for each entry, `largestWalBytes ≤ 8 MiB` and `finished`.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~UsageHistoryStoreTests|FullyQualifiedName~HistoryClearTests"`. Expected: the build fails with `error CS1061` for `BeginClear`, `ClearPending` and `RetainedByClear`.
- [ ] **Step 3: Implement the marker codec, the cleared marks, the Usage clear and both strips' cleared time**
- [ ] **Step 4: Run the Step 2 command, the build, then the Core baseline.** Expected: PASS, 0 errors (the measurement compiles), then no output.
- [ ] **Step 5: Commit, push and record the timings**

```bash
git add tests/DeadWare.Tests/UsageHistoryStoreTests.cs tests/DeadWare.Tests/HistoryClearTests.cs tests/DeadWare.App.Tests/RealWindows/HistoryClearMeasurementTests.cs
git commit -m "test(history): usage clears, cleared marks on both strips and clear timings"
git add src/DeadWare.Core/HistoryClear.cs src/DeadWare.Core/UsageHistoryStore.cs src/DeadWare.Core/TrafficStrip.cs src/DeadWare.Core/ConnectionHistoryStore.cs
git commit -m "feat(history): clear usage-history.db and mark cleared time"
```

Run (CI). Expected: green, and `history-clear.json` holds the four clears. The controller adds them and the build job's duration to the PR description. If the job is cancelled at its 20-minute limit, stop and report to the user (spec, CI time).

---

### Task 10: Clearing `process-history.db` and the retention wait (Core)

Spec: *Don't record these apps and Clear history…* → How each store clears → Shared rules (secure_delete and the retention wait) and Application resources; Tests → `Process_one_app_clear_moves_numbers_into_Everything_else`, `Process_all_apps_clear_deletes_whole_minutes_in_six_hour_windows`, `Retention_expiry_waits_while_a_marker_exists`.

**Files:**
- Modify: `src/DeadWare.Core/ProcessHistoryStore.cs`:
  - `Initialize` :79-98;
  - `Append`'s `ExpireByDay` (:153) and `Expire` :213-218 skip while a marker exists;
  - `Configure` :457-469 (`secure_delete`);
  - new `BeginClear` and `ClearStep`.
- Modify: `src/DeadWare.Core/UsageHistoryStore.cs`: `Append`'s `Expire` (:113) skipped while a marker exists.
- Test: `tests/DeadWare.Tests/ProcessHistoryStoreTests.cs`, `tests/DeadWare.Tests/HistoryClearTests.cs`

**Interfaces:**
- Consumes: Task 9's `HistoryClearMarker` and the Usage marker; Task 8 `HistoryClearProgress`; `KeyOf`'s `names.upper` (= `AppKey.Of` of a saved name).
- Produces, on `ProcessHistoryStore` (marker `process-history.clear.json`, the Usage format):
  - `ClearPending`, `PendingClear`, `DiscardedInvalidMarker` and `public void BeginClear(HistoryClear clear)`, as Usage's, and `public HistoryClearProgress ClearStep(TimeSpan budget, DateTimeOffset now)`, whose `now` is used only for the `Expire` after the last step (the store has no clock). `ProcessHistoryService` passes `time.GetUtcNow()`; its `ClearStepOverride` keeps the `Func<TimeSpan, HistoryClearProgress>` shape.
  - Windows: 6-hour UTC windows from the oldest saved minute up to `Before`, one transaction per window and machine (the store loops over machines itself). `Total` = windows × machines at the first step.
  - One app, found by `names.upper` = key (none: nothing to do), in each window:
    1. the spec's `UPDATE app_minutes AS e SET … FROM app_minutes AS a …` into Everything else, each GPU `<sum>` null when both are null, else their sum with null as 0;
    2. `INSERT OR IGNORE` of an Everything else row under the reserved key for a minute without one;
    3. delete the app's rows.

    After the last window, its `names` row goes when no `app_minutes` row refers to it.
  - All apps: per window and machine, `DeleteMinutes`' two statements for that machine with `minute < min(window end, Before)`; `DeleteOrphanedNames` after the last window.
  - The last step deletes the marker and then calls `Expire(now)` with the step's `now`.
  - `secure_delete=ON` on write connections while a marker exists. Seams: `clearClock`, `clearConnectionOpened`, and `private Action<string, long, long>? clearWindowCommitted` (machine, from, to in Unix ms).
- Both stores' expiry (`Append`'s retention delete; process `Expire` and `ExpireByDay`) deletes nothing while `ClearPending`. Eviction on `SQLITE_FULL` still runs.

- [ ] **Step 1: Write the failing tests**

```csharp
// ProcessHistoryStoreTests. Minutes(store, machine "m", 720 minutes before Before, rows chrome.exe, code.exe and Everything else with GPU values; ten minutes with null GPU values).
[Fact] public void Process_one_app_clear_moves_numbers_into_Everything_else(){
    // Before and after BeginClear("CHROME.EXE") + steps, ReadApplications for every ProcessRank gives the same RankTotal; Everything else grows by chrome's CPU, I/O, memory,
    // GPU-seconds and GPU memory byte-seconds; chrome.exe is in no row and not in names; a minute saved without an Everything else row (raw SQL) gains one holding chrome's values;
    // a minute whose GPU values were null stays null; gpu_covered is unchanged; PRAGMA user_version is 2.
}
[Fact] public void Process_all_apps_clear_deletes_whole_minutes_in_six_hour_windows(){
    // minutes, app_minutes (GPU columns included) before Before are gone; later minutes stay; orphaned names are gone; every clearWindowCommitted (machine, from, to) spans at most 360 minutes
    // of one machine; two machines are cleared.
}
// HistoryClearTests
[Fact] public void Retention_expiry_waits_while_a_marker_exists(){
    // With a marker on each store: Usage's Append at a time 401 days after its oldest day, and Application resources' Expire and Append 31 days after theirs, delete no expired day.
    // After each clear finishes (the marker gone), the next Append (Usage) and the store's own Expire after the last step (Application resources, whose steps pass that
    // simulated now: oldest day + 31 d) delete them.
}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ProcessHistoryStoreTests|FullyQualifiedName~HistoryClearTests"`. Expected: the build fails with `error CS1061` for `ProcessHistoryStore.BeginClear`.
- [ ] **Step 3: Implement the Application resources clear and both retention waits**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ProcessHistoryStoreTests.cs tests/DeadWare.Tests/HistoryClearTests.cs
git commit -m "test(history): Application resources clears and the retention wait"
git add src/DeadWare.Core/ProcessHistoryStore.cs src/DeadWare.Core/UsageHistoryStore.cs
git commit -m "feat(history): clear process-history.db in six-hour windows"
```

Run (CI). Expected: green.

---

### Task 11: Clearing the alert log, and the checks across all four stores (Core)

Spec: *Don't record these apps and Clear history…* → How each store clears → Alert log (the command, in scope, first step 2, later steps, last transaction, known apps); *First network use alerts* → Interactions (Clear history table); Tests → `Alert_clear_removes_app_episodes_and_keeps_device_episodes`, `Clear_marker_that_cannot_be_saved_clears_nothing`, `Cleared_names_leave_no_bytes_in_any_store_file`; Review Focus 1 and 2.

**Files:**
- Modify: `src/DeadWare.Core/AlertStore.cs`: `Initialize` (reads `pending_clear`), `Open`/`Configure` :162-188 (`secure_delete`), new `BeginClear` and `ClearStep`.
- Test: `tests/DeadWare.Tests/AlertStoreTests.cs`, `tests/DeadWare.Tests/HistoryClearTests.cs`

**Interfaces:**
- Consumes: Task 5's tables; Tasks 8–10's store clears; Task 1 `app_key`.
- Produces, on `AlertStore`:
  - `public HistoryClear? PendingClear { get; }`, from `pending_clear`, read by `Initialize`.
  - `public void BeginClear(string machine, HistoryClear clear)`: one transaction writes the `pending_clear` row and, for all time, deletes `machine`'s in-scope `known_apps` rows (`app_key=$key`, or every row). It throws on failure, writing nothing. An existing equal `pending_clear` row commits nothing and deletes no known row; a different one throws `InvalidOperationException`.
  - `public HistoryClearProgress ClearStep(string machine, TimeSpan budget)` (`Total` null: the alert log shows no percent).
    - In scope: closed episodes with `start < clear.AlertCutoff` on every machine key, of two kinds. First-use episodes are found through `episode_apps` (`app_key(name)=$key`, or any link). ApplicationDaily episodes are matched in C#: the text before the last " ≥ " of a summary that ends " in a day", by `AppKey.Of`.
    - Transactions of at most 256 episodes with their links; then, for all time, other machine keys' in-scope known rows, at most 1,024 a transaction. The last transaction deletes the marker.
    - The shared rules, as Task 8's: delete transactions run until `budget` has passed on `clearClock` (always at least one unless it yields), and each is followed by `PRAGMA wal_checkpoint(TRUNCATE)`. Before each, when `-wal` is over 4 MiB after a TRUNCATE checkpoint attempt, the step ends early; after 60 s of yielding without a transaction it throws `IOException`.
  - `secure_delete=ON` on write connections while the marker exists, and the seams `clearClock`, `clearCommitted` (`Action<int>`, after each commit and before its checkpoint) and `clearConnectionOpened`.

- [ ] **Step 1: Write the failing tests**

```csharp
// AlertStoreTests
[Fact] public void Alert_clear_removes_app_episodes_and_keeps_device_episodes(){
    // Episodes: a pre-0.17 budget "chrome.exe ≥ 2.0 GB in a day" without a link; a budget for "Chrome.EXE" (with its link); a first-use episode linked to CHROME.EXE; one linked to other.exe;
    // a CPU metric episode; a battery episode; a first-use episode for CHROME.EXE starting after Requested in the same minute; known rows for CHROME.EXE and OTHER.EXE on M and on "OTHER-PC".
    // Each of one app/all apps × Older than 7 days/All time deletes exactly the in-scope episodes and keeps the one after Requested.
    // Older than 7 days keeps open episodes and every known row. One app, All time deletes CHROME.EXE's row on M inside BeginClear's transaction, and an upsert of CHROME.EXE appended after it
    // is kept; the steps delete CHROME.EXE on OTHER-PC. All apps, All time empties known_apps on every machine; first_use_learning_start is unchanged.
    // pending_clear is empty afterwards; user_version is 1. A 0.16 file without the new tables gains them in place, then clears.
    // BeginClear again with the equal request writes nothing (a CHROME.EXE row upserted after the first BeginClear stays); a different request throws InvalidOperationException.
    // With a pinned reader (a second Pooling=false connection holding a read transaction) and enough in-scope budget episodes with 500-character summaries that their deletes
    // write more than 4 MiB of -wal, a step yields once the -wal passes 4 MiB, and throws
    // IOException only after clearClock passes 60 s of yielding; clearCommitted never sees more than 256.
}
// HistoryClearTests
[Fact] public void Clear_marker_that_cannot_be_saved_clears_nothing(){
    // connections.db at its fixture cap (65,536 bytes): after Initialize, metadata rows ('pad-' + i:D5, 0), keys no build reads, are inserted one at a time until SQLITE_FULL,
    // so the metadata table's last leaf is full and the freelist is empty (a file full of traffic alone still takes the marker in the metadata pages' free space).
    // BeginClear (one app and all apps) then needs a new page and throws; PendingClear is null, no metadata key starts with 'clear-', and no row is deleted.
    // Usage history with its sidecar unwritable (a directory created at the marker's .tmp path after Initialize, which blocks the write on Linux and Windows alike): BeginClear throws,
    // ClearPending is false, no row is deleted.
}
[Fact] public void Cleared_names_leave_no_bytes_in_any_store_file(){
    // For each store: rows for "ZQXSECRET.exe" (a budget and a first-use episode with its known row for alerts.db), a one-app All time clear run to the end, a TRUNCATE checkpoint.
    // The UTF-8 bytes of "ZQXSECRET" and "zqxsecret" appear nowhere in the database, its -wal or usage-history.cleared.json; no *.clear.json or *.tmp remains;
    // clearConnectionOpened saw PRAGMA secure_delete return 1 on every store's clearing connection.
}
[Fact] public void One_app_clear_matches_non_ascii_case_in_every_store(){   // Review Focus 1
    // Rows for "ärger.exe" and "Ärger.exe" in all four stores (alerts.db: a budget for each spelling and a first-use episode); HistoryClear.Create("ÄRGER.EXE", allTime: true, now).
    // In process-history.db the two spellings are saved in different minutes (one batch cannot hold two apps of one key) and share one names row (upper "ÄRGER.EXE",
    // display the first spelling). After every store's steps, no row of either spelling remains in any store (no app_minutes or names row for that key), and rows of
    // "other.exe" are untouched.
}
[Fact] public void Clearing_an_app_with_no_saved_history_finishes_at_once(){   // Review Focus 2
    // Each store holds other apps only; HistoryClear.Create("chrom.exe", allTime: false, now): each store's first ClearStep returns Finished (Percent 100 where Total is set),
    // every marker is gone, and every row count is unchanged.
}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~AlertStoreTests|FullyQualifiedName~HistoryClearTests"`. Expected: the build fails with `error CS1061` for `AlertStore.BeginClear`.
- [ ] **Step 3: Implement the alert-log clear and its `secure_delete`**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/AlertStoreTests.cs tests/DeadWare.Tests/HistoryClearTests.cs
git commit -m "test(history): alert-log clears and checks across the four stores"
git add src/DeadWare.Core/AlertStore.cs
git commit -m "feat(alerts): clear app episodes and known apps behind pending_clear"
```

Run (CI). Expected: green.

---

### Task 12: The first-use seed reads

Spec: *First network use alerts* → Known apps and seeding → Sources; *Don't record these apps and Clear history…* → Shared rules → Clear in progress; Tests → `UsageHistoryStoreTests.Application_names_return_each_name_with_its_first_and_last_minute_for_this_machine_only`, `ConnectionHistoryStoreTests.Application_names_honour_expiry_and_floor_convert_ticks_and_report_a_clear_in_progress`.

**Files:**
- Modify: `src/DeadWare.Core/UsageHistoryStore.cs`, `src/DeadWare.Core/ConnectionHistoryStore.cs` (new reads)
- Modify: `src/DeadWare.App/UsageHistoryService.cs` (after `ReadApplicationsAsync` :167-179), `src/DeadWare.App/ConnectionService.cs` (after `ReadViewAsync` :32-36)
- Test: `tests/DeadWare.Tests/UsageHistoryStoreTests.cs`, `tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 3 `ApplicationSeen`; Tasks 8–9 `PendingClear` and `ClearPending`.
- Produces:
  - `public IReadOnlyList<ApplicationSeen>? ReadApplicationNames(string machine)` on `UsageHistoryStore`: the spec's SQL verbatim, on its own read connection, minutes converted from Unix ms; null while `ClearPending`.
  - `public IReadOnlyList<ApplicationSeen>? ReadApplicationNames()` on `ConnectionHistoryStore`: the spec's SQL with `Grouped`'s expiry and floor filters, read-only, deferred, under the store's lock, ticks converted; null while `PendingClear` is non-null.
  - `public async Task<IReadOnlyList<ApplicationSeen>?> ReadApplicationNamesAsync()` on `UsageHistoryService` (on failure: `Fail`, then null) and on `ConnectionService` (on any exception: null). Both run off the UI thread like the other reads.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Application_names_return_each_name_with_its_first_and_last_minute_for_this_machine_only(){
    // Machine "a": chrome.exe at Day+1 min and Day+9 min, code.exe at Day+5 min; machine "b": steam.exe. ReadApplicationNames("a") == [("chrome.exe",Day+1min,Day+9min),("code.exe",Day+5min,Day+5min)]
    // in max-minute-descending order; with a usage-history.clear.json marker (BeginClear), it returns null.
}
[Fact] public void Application_names_honour_expiry_and_floor_convert_ticks_and_report_a_clear_in_progress(){
    // 60 per-poll rows of "app.exe" (batch "s:i:1") and one 0.16.0 per-minute unit of it: one row with the earliest and the latest minute, as DateTimeOffsets at offset zero;
    // rows below a raised expiry and at or below floor are absent; with metadata row ('clear-all', ticks) inserted by SQL and the store reopened, ReadApplicationNames() returns null.
}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~UsageHistoryStoreTests|FullyQualifiedName~ConnectionHistoryStoreTests"`. Expected: the build fails with `error CS1061` for `ReadApplicationNames`.
- [ ] **Step 3: Implement both reads and both service wrappers**
- [ ] **Step 4: Run the Step 2 command, the build, then the Core baseline.** Expected: PASS, 0 errors, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/UsageHistoryStoreTests.cs tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs
git commit -m "test(history): application names for the first-use seed"
git add src/DeadWare.Core/UsageHistoryStore.cs src/DeadWare.Core/ConnectionHistoryStore.cs src/DeadWare.App/UsageHistoryService.cs src/DeadWare.App/ConnectionService.cs
git commit -m "feat(history): read saved application names for the first-use seed"
```

Run (CI). Expected: green.

---

## 0.17.0 — Services

### Task 13: First-use detection and the tracker on the alert worker; budgets of excluded apps (App)

Spec: *First network use alerts* → Detection on the collector thread, The alert worker (Constructor, Configure, Exclusions, Startup 1–2, Loop, Flush, Status, No one-hour floor); *Don't record these apps and Clear history…* → Where recording stops (Application traffic budgets: `SetExcluded`, removal and re-seed, startup order), Settings model (Startup: the `Alerts.Configure` calls); Tests → `Network_use_is_posted_from_the_collector_thread_once_per_app_per_minute`, `A_work_with_1024_new_apps_saves_in_batches_of_at_most_1024_and_never_fails_the_log`, `Excluded_budget_reseeds_when_the_app_is_removed`.

**Files:**
- Modify: `src/DeadWare.App/AlertService.cs`: the work records :15-18, constructor :41-47, `Configure` :56-62, new `TryRecordNetworkUse`, `RunAsync` :115-182.
- Modify: `src/DeadWare.App/UiSession.cs`: the `Alerts` construction and `Configure` (:75-77), `SetAlertRules` :231, `SetAlertNotifications` :236, the unsubscribe beside :317.
- Test: `tests/DeadWare.App.Tests/AlertServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 3–6 (`FirstNetworkUse`, `FirstUseTracker`, the store's `Append(…, firstUse)` and `ReadFirstUse`, `AlertEvaluator.SetExcluded` and `SeedDay(…, only)`); `Reading.Connections` (`Collector.cs:14`), `ConnectionSample.Deltas`.
- Produces:
  - Constructor: `AlertService(string path, string machineKey, UsageHistoryService usage, Func<string?> gpuKey, TimeProvider? timeProvider = null, TimeSpan? flushInterval = null, TimeZoneInfo? localZone = null, Func<Task<IReadOnlyList<ApplicationSeen>?>>? connectionNames = null, TimeSpan? firstUseGroupWindow = null)`. The window defaults to 60 s; it must be above 0 and at most 60 s, else `ArgumentOutOfRangeException`. `UiSession.cs:75` and `AlertsUiChecks.cs:35` compile unchanged.
  - `public void Configure(IReadOnlyList<AlertRule> alertRules, bool notificationsEnabled, IReadOnlyList<string>? ignoredApps = null, IReadOnlyList<string>? excludedApps = null)`. It stores the excluded key set (`AppExclusions.Keys`) and the two lists with volatile writes, publishes the collector-side rule-exists flag, and posts `RulesWork(Rules, At, Ignored, Excluded)`.
  - `public void TryRecordNetworkUse(Reading reading)`: the spec's detection steps 1–4, on the collector thread.
    - Its state: the posted set; the UTC minute; `private long knownGeneration` (read volatile; a clear increments it, and tests do by reflection); the 4,096-entry name-to-key cache; the learning-started flag.
    - A work holds at most 1,024 uses; `private sealed record NetworkUseWork(DateTimeOffset At, IReadOnlyList<NetworkUse> Uses) : Work;`.
  - `public FirstUseStatus FirstUse { get; }` (volatile; `new(false, false, null, null, null)` until `Restore`).
  - Worker:
    - startup reads `ReadFirstUse` into `tracker.Restore`, configures the tracker from the stored snapshot, then applies the stored excluded set to the evaluator after `Replace` and `Restore` and before the budget seed;
    - `NetworkUseWork` calls `tracker.Observe`, and the worker flushes at 64 pending episodes;
    - `Flush` calls `tracker.Expire(now)`, then repeats `Append(machine, ≤1,024 pending, now, tracker.TakeChanges(1024))` until nothing is pending;
    - a `RulesWork` calls `tracker.Configure` and `evaluator.SetExcluded`; for its `Reseed` targets it awaits `usage.ReadApplicationMinutesAsync(target, StartOfLocalDay(now), now + 1 min)` inside the loop, then calls `SeedDay(now, rows, only)`.
  - `UiSession`:
    - constructs `Alerts` with `connectionNames: () => Connections.ReadApplicationNamesAsync()`;
    - every `Alerts.Configure` call passes `Settings.FirstUseIgnoredApps, Settings.ExcludedApps`;
    - `Collector.SampleAvailable += Alerts.TryRecordNetworkUse` beside `TryRecordBattery` (:77), unsubscribed beside :317.

- [ ] **Step 1: Write the failing tests**

```csharp
// Store(path) holds both seed markers and learning dates in the past (appended through AlertStore with FirstUseChanges metadata). Use(name, remote="8.8.8.8") = a ConnectionDelta;
// R(t, available=true, params deltas) = a Reading at t whose Connections is a ConnectionSample with those Deltas. Saved(service) = the saved kind-4 episodes.
[Fact] public async Task Network_use_is_posted_from_the_collector_thread_once_per_app_per_minute(){
    // Start with the rule. Each is asserted through PendingObservations and Saved after a flush:
    // R(..., available:false) and a poll after Configure([]) post nothing; loopback-only deltas (127.0.0.2, ::1) post nothing; an excluded app's deltas post nothing;
    // a second poll in the same minute posts nothing new; the next minute posts again; an incremented knownGeneration posts again within the minute;
    // one poll with 1,100 new apps posts one work of 1,024 uses, and the next poll posts the other 76.
}
[Fact] public async Task A_work_with_1024_new_apps_saves_in_batches_of_at_most_1024_and_never_fails_the_log(){
    // Ten polls of 1,024 new apps each, faster than the worker reads: every app is saved as a kind-4 episode with its known row, Failed stays false, Status is SavedStatus.
}
[Fact] public async Task Excluded_budget_reseeds_when_the_app_is_removed(){
    // A budget "chrome.exe" of 1,000 bytes; the usage store holds 600 chrome.exe bytes today. Configure(excludedApps:["chrome.exe"]): later MinuteClosed batches with 600 chrome bytes start nothing.
    // Configure(excludedApps:[]): the rule re-seeds from today's 600 saved bytes and the next 500-byte minute starts its episode; another rule's total is unchanged.
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `TryRecordNetworkUse`, and `error CS1739` for `excludedApps`.
- [ ] **Step 3: Implement detection, the tracker on the worker, the exclusions and the session wiring**
- [ ] **Step 4: Build and run the Core baseline.** Expected: 0 errors, then no output.
- [ ] **Step 5: Commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/AlertServiceTests.cs
git commit -m "test(alerts): first-use detection, batches and excluded budgets"
git add src/DeadWare.App/AlertService.cs src/DeadWare.App/UiSession.cs
git commit -m "feat(alerts): detect first network use on the collector thread"
```

Run (CI). Expected: green; every existing `AlertServiceTests` and `AlertsUiChecks` check passes.

---

### Task 14: The seed, the grouped balloon and restarts; the real-Windows check (App)

Spec: *First network use alerts* → Known apps and seeding (Seed timing, Merge, Wiring), The alert worker (Startup 3, the third wake-up, Quit and sign-out), Balloon; Tests → `A_new_app_after_learning_is_logged_closed_and_one_balloon_groups_apps_within_the_window`, `First_use_groups_skip_the_hour_floor_and_muted_notifications_still_log`, `Seeding_reads_each_store_once_retries_a_failed_store_and_skips_a_store_with_a_clear_in_progress`, `Restart_keeps_known_apps_and_learning_dates_and_a_group_open_at_quit_shows_no_balloon`, and Real Windows `First_network_use_sees_this_process_send_one_udp_datagram`.

**Files:**
- Modify: `src/DeadWare.App/AlertService.cs` (`RunAsync`), `src/DeadWare.App/App.xaml.cs` (beside :46-56)
- Create: `tests/DeadWare.App.Tests/RealWindows/FirstNetworkUseChecks.cs`
- Test: `tests/DeadWare.App.Tests/AlertServiceTests.cs`

**Interfaces:**
- Consumes: Task 13's worker; Task 12's `ReadApplicationNamesAsync`; `FirstNetworkUse.BalloonText`; `TrayIcon.ShowAlert` (`TrayIcon.cs:63-69`).
- Produces:
  - `public event Action<FirstUseGroup>? FirstUseGroupClosed;`, raised on the worker inside try/catch when `TakeDueGroup` returns a group and notifications are on.
  - The seed: for each store with `tracker.NeedsSeed`, at worker start after Restore and when a `RulesWork` adds the first FirstNetworkUse rule, never without one. It checks the stop flag before each read and before its first write. A throwing or missing delegate counts as null.
  - The third wake-up: `Task.Delay(due - now, time)` joins the `WhenAny` (:156) while `tracker.GroupDue` is set. The final flush on Quit shows no open group.
  - `App.xaml.cs`: `session.Alerts.FirstUseGroupClosed += group => …`. It marshals to the dispatcher, returns when `lifetime.IsEnding` or `!Settings.AlertNotifications`, then calls `tray?.ShowAlert("DeadWare alert", FirstNetworkUse.BalloonText(group, TimeZoneInfo.Local, CultureInfo.CurrentCulture), ShowAlerts)`.
  - `[Collection("RealWindows")] public sealed class FirstNetworkUseChecks` with `[ElevatedGitHubFact] public async Task First_network_use_sees_this_process_send_one_udp_datagram()`.
    - Setup: a real `Collector` with ETW, its `SampleAvailable` feeding an `AlertService` on a temporary `alerts.db` that has both seed markers and learning dates in the past, configured with the default rule.
    - It sends one UDP datagram to 192.0.2.1:9 and waits up to 30 s for a kind-4 episode whose `Application` is `Path.GetFileName(Environment.ProcessPath)`.
    - It writes `first-network-use.json` `{ capture, seconds }`: "available" with the seconds until the episode, or "unavailable" when `Reading.Connections` was never `Available` (then it passes).

- [ ] **Step 1: Write the failing tests and the check**

```csharp
// Window 100 ms. Fixture alerts.db with both seed markers and learning dates in the past unless stated.
[Fact] public async Task A_new_app_after_learning_is_logged_closed_and_one_balloon_groups_apps_within_the_window(){
    // Two new apps in one poll: one FirstUseGroupClosed with Count 2; two saved kind-4 episodes, each with its link and known row; EpisodeStarted never raised.
}
[Fact] public async Task First_use_groups_skip_the_hour_floor_and_muted_notifications_still_log(){
    // Two groups 1 s apart both raise. With Configure(…, notificationsEnabled:false) neither raises and both episodes are logged.
}
[Fact] public async Task Seeding_reads_each_store_once_retries_a_failed_store_and_skips_a_store_with_a_clear_in_progress(){
    // No markers: names from a usage fixture and a fake connectionNames are known with no episode; both markers set. A throwing connectionNames leaves its marker unset and the next start reads it.
    // With usage-history.clear.json present (BeginClear), the usage read returns null and its marker stays unset. After both markers, a restart reads neither store.
    // StopAsync while connectionNames is pending: nothing is written.
}
[Fact] public async Task Restart_keeps_known_apps_and_learning_dates_and_a_group_open_at_quit_shows_no_balloon(){
    // Window 60 s: a new app opens a group, StopAsync: no FirstUseGroupClosed; a new service on the file knows the app and keeps the learning dates (FirstUse.LearningStart equal).
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `FirstUseGroupClosed`.
- [ ] **Step 3: Implement the seed, the group wake-up, the event, the balloon and the check**
- [ ] **Step 4: Build.** Expected: 0 errors.
- [ ] **Step 5: Commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/AlertServiceTests.cs tests/DeadWare.App.Tests/RealWindows/FirstNetworkUseChecks.cs
git commit -m "test(alerts): first-use seed, grouped balloons, restarts and a real UDP datagram"
git add src/DeadWare.App/AlertService.cs src/DeadWare.App/App.xaml.cs
git commit -m "feat(alerts): seed known apps and group first-use balloons"
```

Run (CI). Expected: green; `first-network-use.json` is in the artifact. The controller adds its result to the PR description.

---

### Task 15: The Don't record filter in the connection writer (App)

Spec: *Connections storage* → Writer hooks for 0.17.0 (Filter before the merge) and Tests (0.17.0, App `Excluded_deltas_leave_buffered_balanced`); *Don't record these apps and Clear history…* → Where recording stops (Connections history); Tests → `Excluded_app_shows_live_and_is_never_appended`, `Retried_batch_keeps_its_payload_when_the_list_changes`; Review Focus 3.

**Files:**
- Modify: `src/DeadWare.App/ConnectionService.cs` (0.16.0's merge step in `WriteLoopAsync`; new `SetExclusions`)
- Test: `tests/DeadWare.App.Tests/ConnectionServiceTests.cs`

**Interfaces:**
- Consumes: 0.16.0's merge step and accounting; Task 1 `AppKey`.
- Produces: `public void SetExclusions(IReadOnlySet<string> keys)` (volatile write). The merge step reads the set once per publication, at merge time only. It leaves out of `accepted` each delta, already past the three checks, whose `AppKey.Of(delta.Peer.Process.Name)` is in it. A skipped delta is not dropped, does not clear `complete` or coverage, and still counts in the `sample.Deltas.Count` the merge subtracts from `buffered`. The live view and lookups are unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Excluded_deltas_leave_buffered_balanced(){ // SetExclusions({"A.EXE"}); readings with a.exe and b.exe deltas through a minute close and StopAsync:
    // no appended record names a.exe; DroppedObservations 0; the b.exe record and the coverage are Complete; BufferedDeltas 0 after StopAsync.
}
[Fact] public async Task Excluded_app_shows_live_and_is_never_appended(){ // Live.Connections holds a.exe; appended records never do; DroppedObservations and Live.Complete unchanged.
}
[Fact] public async Task Retried_batch_keeps_its_payload_when_the_list_changes(){ // the append throws once on the R1 write; SetExclusions({"B.EXE"}); the retried BatchId carries equal records
    // (b.exe included) and coverage.
}
[Fact] public async Task Excluding_an_app_mid_minute_keeps_what_was_merged_before(){   // Review Focus 3
    // a.exe at +1 … +30 s, then SetExclusions({"A.EXE"}), a.exe at +31 … +62 s: minute 0's saved a.exe row holds the +1 … +30 s bytes only (300); minute 1 has no a.exe row.
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `SetExclusions`.
- [ ] **Step 3: Implement the filter**
- [ ] **Step 4: Build.** Expected: 0 errors.
- [ ] **Step 5: Commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionServiceTests.cs
git commit -m "test(connections): excluded apps are never saved and stay live"
git add src/DeadWare.App/ConnectionService.cs
git commit -m "feat(connections): Don't record filter before the per-minute merge"
```

Run (CI). Expected: green.

---

### Task 16: The Connections clear command (App)

Spec: *Connections storage* → Writer hooks for 0.17.0 (`Forget`, Write result, Held units in a clear's scope, Final write); *Don't record these apps and Clear history…* → Clear history… (Service state, Status suffixes), How each store clears → Shared rules (queued command, boundary rule, failure, unavailable) and Connections history (Routine cleanup becomes bounded, Writer order); Tests → App `Clear_retires_held_in_scope_units_whole`, `Connection_clear_flushes_first_and_drops_the_rest_of_the_minute`.

**Files:**
- Modify: `src/DeadWare.Core/HistoryClear.cs`: the shared service-state types.
- Modify: `src/DeadWare.Core/ConnectionMinuteFolder.cs`: `RetireWaiting`.
- Modify: `src/DeadWare.App/ConnectionService.cs`:
  - the `append` delegate gains `int maintenanceTransactions`;
  - `InitializeAsync` :24-31;
  - the write attempt, the tick and the R6 final write (0.16.0);
  - the clear command, `StorageStatus` :101.
- Test: `tests/DeadWare.App.Tests/ConnectionServiceTests.cs`

**Interfaces:**
- Consumes: Task 7 `Forget` and `Append(…, maintenanceTransactions)`; Task 8 `BeginClear`, `ClearStep`, `PendingClear`; Task 15's merge filter.
- Produces:
  - In `HistoryClear.cs`:
    - `public enum HistoryClearPhase { None, Queued, Clearing, Done, Failed, Unavailable }` and `public enum HistoryClearStore { Connections, Usage, ProcessHistory, Alerts }`;
    - `public sealed record HistoryClearState(HistoryClearPhase Phase, int? Percent, string? Error, bool MarkerSaved, bool Resumed) { public static HistoryClearState None { get; } = new(HistoryClearPhase.None, null, null, false, false); }`;
    - on `HistoryClear`, `public static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(250), BoundaryLimit = TimeSpan.FromSeconds(120);`;
    - `public static class HistoryClearCopy { public static string StatusSuffix(HistoryClearState state, bool alertLog); }`: " · Clearing history (37 %).", " · Clearing history." for the alert log, " · Clearing history failed (IOException); it resumes when DeadWare next starts.", " · Clearing history failed (IOException); not cleared.", and "" otherwise.
  - `public IReadOnlyList<ConnectionMinuteUnit> RetireWaiting(Func<ConnectionHistoryRecord, bool> inScope)` on the folder: removes and returns every waiting unit holding a record in scope.
  - `internal sealed record ConnectionFlushResult(bool Saved, string? FailureType);`, returned by the write attempt. `Saved` is false exactly when an `Append` it called threw; held units do not make it false.
  - `internal const int MaintenanceTransactions = 64;`. `InitializeAsync`'s empty `Append`, every write attempt and the tick pass 64; every `Append` of the R6 final write passes 0. A seam, `internal Action<int>? maintenanceObserved`, receives each value, `InitializeAsync`'s direct `store.Append` included.
  - `internal delegate void ConnectionAppend(IReadOnlyList<ConnectionHistoryRecord> records, IReadOnlyList<ConnectionCoverage> coverage, DateTimeOffset now, int maintenanceTransactions);`, the type of the `append` field. The fake-append constructor (:77-80) keeps its signature.
  - `internal ConnectionService(string path, IConnectionGeolocation resolver, Func<DateTimeOffset> clock, Func<ConnectionAppend, ConnectionAppend> wrapAppend, ConnectionServiceOptions? options = null, long maximumDatabaseBytes = ConnectionHistoryStore.MaximumDatabaseBytes)`: a real store at that cap whose write attempts go through `wrapAppend(store.Append)`, for the clear tests (the path constructor at :19-22 has no wrap, the fake one no store).
  - Members shared by all four services (Tasks 17 and 18 repeat them):
    - `public HistoryClearState ClearState { get; internal set; }`; the internal setter is a test seam for Task 22's checks, never called by the app;
    - `public void Clear(HistoryClear request)` puts the request in a command channel of capacity 1 and sets Queued;
    - `public void RetryClear()` re-posts the kept request while Failed and the worker runs. With `MarkerSaved` false it runs the full order. With `MarkerSaved` true it runs as a resume and repeats nothing that already ran: Connections' `BeginClear` re-commits nothing (equal marker), `Forget` is skipped and the boundary rule is installed only while `Before` is still ahead of now, then the write attempts, `RetireWaiting` and the steps; Usage and Application resources skip `BeginClear`, `Forget` and `Discard`, then `Flush()` and the steps; the alert log runs only its first-step (3) and (6), then the later steps (Task 18). An All time clear so loses at most the 60 s its first run dropped;
    - `public bool CanRetryClear { get; }`;
    - `internal Func<TimeSpan, HistoryClearProgress>? ClearStepOverride { get; set; }`, a test seam that replaces the store's step.
  - The writer takes the command before its next queue item and alternates data with one `ClearStep(HistoryClear.StepBudget)`. A step never starts after `StopAsync`.
  - Order: `BeginClear`; for all time, `folder.Forget(AppKey)` and the boundary rule; write attempts until nothing sendable is left; `RetireWaiting(r => clear.Covers(r.Delta.Peer.Process.Name, r.Delta.Minute))`, with `Dropped += max(1, Observations)` for each unit; then the steps.
    - The boundary rule: `clear` held beside the exclusion set, which the merge filter applies, until the writer merges a minute at or after `Before` or `BoundaryLimit` has passed on a `Stopwatch` since the command.
    - A clear resumed at start from `PendingClear` runs only the steps. It installs the rule again when `Before` is still ahead of now.
  - Phases:
    - `BeginClear` throws: Failed, `MarkerSaved` false.
    - A write attempt with `Saved` false: Failed with its `FailureType`, `MarkerSaved` true; the unsent units keep their normal retry.
    - A step throws: Failed with the type, `MarkerSaved` true, and the writer keeps recording.
    - Initialization failed: Unavailable with that failure's type, writing nothing.
  - `StorageStatus` ends with `HistoryClearCopy.StatusSuffix(ClearState, false)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Clear_retires_held_in_scope_units_whole(){
    // Clock-check hold (0.16.0's rollback fixture): a held unit with a.exe and one with b.exe only. Clear(one app "A.EXE", all time): the a.exe unit is retired before the first delete
    // (DroppedObservations rises by its Observations) and never appended; the b.exe unit is appended later unchanged; StopAsync's appends all pass maintenanceTransactions 0 (maintenanceObserved).
}
[Fact] public async Task Connection_clear_flushes_first_and_drops_the_rest_of_the_minute(){
    // a.exe frozen units are appended before the delete (a fake append records order against the store's PendingClear). After an All time clear, a.exe deltas of minutes before Before
    // are not appended and those at or after it are. Older than 7 days leaves the open units alone (Held unchanged).
    // An append that throws during the clear's write: ClearState is Failed, Error "IOException", MarkerSaved true, and PendingClear is set.
    // With that clear an All time "A.EXE" one: RetryClear() then reaches Done, and an a.exe delta merged after the first run (its minute at or after Before) is appended
    // (Forget is not repeated).
    // maintenanceObserved: InitializeAsync passes 64, write attempts pass 64, the final write passes 0.
    // A service at maximumDatabaseBytes 65,536 whose metadata table was padded after InitializeAsync as in Task 11's Clear_marker_that_cannot_be_saved_clears_nothing:
    // Clear(...) gives ClearState Failed, MarkerSaved false, Error the exception's type name, and no row is deleted.
    // (Every service built through the wrapAppend constructor.)
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `ClearState` and `Clear`.
- [ ] **Step 3: Implement the types, `RetireWaiting`, the flush result, the maintenance values and the command**
- [ ] **Step 4: Build and run the Core baseline.** Expected: 0 errors, then no output.
- [ ] **Step 5: Commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionServiceTests.cs
git commit -m "test(connections): the clear command flushes, retires held units and steps"
git add src/DeadWare.Core/HistoryClear.cs src/DeadWare.Core/ConnectionMinuteFolder.cs src/DeadWare.App/ConnectionService.cs
git commit -m "feat(connections): clear connection history as a writer command"
```

Run (CI). Expected: green, every existing `ConnectionServiceTests` and `CoolingShutdownTests` test included.

---

### Task 17: Usage history and Application resources: exclusions and clears (App)

Spec: *Don't record these apps and Clear history…* → Settings model (`SetExclusions`), Where recording stops (Usage history, Application resources), Settings → History and alerts (the `limit` parameter), How each store clears → Shared rules, Usage history (Order), Application resources (Order, the boundary window), Errors and limits (paused recording); Tests → `Usage_clear_interleaves_with_recording_and_resumes_after_quit`, `Process_clear_moves_the_open_minute_into_Everything_else`; Review Focus 4 and 5.

**Files:**
- Modify: `src/DeadWare.App/UsageHistoryService.cs` (`RunAsync` :82-140, `Status` :36-38)
- Modify: `src/DeadWare.App/ProcessHistoryService.cs` (`RunAsync` :80-148, `Status` :38-40, `TryReadApplicationsAsync` :156-168)
- Test: `tests/DeadWare.App.Tests/UsageHistoryServiceTests.cs`, `tests/DeadWare.App.Tests/ProcessHistoryServiceTests.cs`

**Interfaces:**
- Consumes: Task 6's folder members; Tasks 9–10's store clears; Task 16's shared members and types.
- Produces, on both services:
  - `public void SetExclusions(IReadOnlySet<string> keys)`; the worker sets `folder.Excluded` before each item.
  - Task 16's clear members, with the worker alternating one data pass as today (up to 120 queued items per wake, flush timer unchanged) and one step.
  - The order: `BeginClear` (a throw: Failed, `MarkerSaved` false, nothing cleared); for all time, `Forget` and `Boundary`; `Flush()`; then the steps.
    - Usage: `folder.Forget(AppKey)`.
    - Application resources: `folder.Forget(AppKey)` for one app, or `folder.Discard()` when `OpenMinute < Before` for all apps. During the boundary window, closed batches with `Minute < Before` are discarded for all apps.
    - The boundary lasts until the worker folds a minute at or after `Before`, or `HistoryClear.BoundaryLimit`.
  - A flush failure stops the worker as today; the clear then shows Failed and resumes at the next start.
  - Resume after `Initialize` (Application resources: after the start-up expiry) from `store.PendingClear`, with `Resumed` true. A resumed clear whose `Before` is still ahead of now sets `folder.Boundary` again, with the same end (a minute at or after `Before` folded, or `HistoryClear.BoundaryLimit`), and for all apps Application resources again discards closed batches with `Minute < Before`. `DiscardedInvalidMarker` gives Failed (InvalidDataException), `MarkerSaved` false, `Resumed` true. A clear posted after the worker stopped on a failure gives Unavailable with that failure's type.
  - `Status` ends with `HistoryClearCopy.StatusSuffix(ClearState, false)`.
  - `ProcessHistoryService.TryReadApplicationsAsync(DateTimeOffset from, DateTimeOffset to, ProcessRank rank, int limit = 200)`, passed to the store's `limit`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Usage_clear_interleaves_with_recording_and_resumes_after_quit(){
    // A store with 200,000 app rows; Clear(all apps, all time) while publications arrive each 10 ms: DroppedSamples stays 0. StopAsync during the clear completes within 1 s and the marker remains.
    // A new service on the file: ClearState.Resumed true, it finishes (Done), and no application row before Before remains.
    // A new service on a file whose usage-history.clear.json holds "{": ClearState is Failed, Error "InvalidDataException", MarkerSaved false, Resumed true, and nothing is deleted.
    // A directory created at the marker's .tmp path before Clear: Failed, MarkerSaved false, nothing deleted.
}
[Fact] public async Task Process_clear_moves_the_open_minute_into_Everything_else(){
    // A one-app All time clear mid-minute: the closed minute's Everything else holds the app's CPU, I/O, memory, GPU-seconds and GPU memory byte-seconds folded before the clear, and no row names it.
    // An all-apps All time clear: the open minute is discarded and no batch with Minute < Before is saved during the boundary window.
}
[Fact] public async Task Clear_runs_while_recording_is_paused(){   // Review Focus 4
    // usage.Configure(false) and process.Configure(false, false): Clear(all apps, all time) on each reaches Done, and no row is written meanwhile.
}
[Fact] public async Task Try_again_after_a_failed_step_finishes_the_clear(){   // Review Focus 5
    // Usage: ClearStepOverride throws IOException once. ClearState is Failed, Error "IOException", MarkerSaved true, CanRetryClear true, and HistoryClearCopy.StatusSuffix reads
    // " · Clearing history failed (IOException); it resumes when DeadWare next starts."; publications are still saved; RetryClear() reaches Done and the marker is gone.
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `SetExclusions`, `Clear` and `ClearStepOverride` on both services.
- [ ] **Step 3: Implement both services' exclusions and clear commands, and the read limit**
- [ ] **Step 4: Build.** Expected: 0 errors.
- [ ] **Step 5: Commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/UsageHistoryServiceTests.cs tests/DeadWare.App.Tests/ProcessHistoryServiceTests.cs
git commit -m "test(history): usage and Application resources clears, pause and retry"
git add src/DeadWare.App/UsageHistoryService.cs src/DeadWare.App/ProcessHistoryService.cs
git commit -m "feat(history): exclusions and clear commands on the usage and process workers"
```

Run (CI). Expected: green.

---

### Task 18: The alert-log clear (App)

Spec: *Don't record these apps and Clear history…* → How each store clears → Alert log (first step 1–6, later steps, resume); *First network use alerts* → Interactions (Clear history); Tests → `AlertServiceTests.Clearing_an_app_forgets_it_so_its_next_poll_alerts_again_and_keeps_the_dont_alert_list`, `Alert_clear_runs_in_steps_and_deletes_an_open_budget_episode_for_good`.

**Files:**
- Modify: `src/DeadWare.App/AlertService.cs` (`RunAsync`, `Status` :53)
- Test: `tests/DeadWare.App.Tests/AlertServiceTests.cs`

**Interfaces:**
- Consumes: Task 11 `BeginClear(machine, clear)`, `ClearStep(machine, budget)`, `PendingClear`; Task 6 `EndScope`; Task 4 `Forget`; Task 16's shared members.
- Produces: Task 16's clear members on `AlertService`.
  - The first step, with no other work between: (1) `Flush()`; (2) `store.BeginClear(machine, clear)`, where a throw gives Failed, `MarkerSaved` false, with nothing cleared and the tracker unchanged; (3) all time: `evaluator.EndScope(AppKey, Requested, resetDay: true)`, whose episodes join pending; (4) all time: `tracker.Forget(AppKey, Requested)`; (5) all time: `Interlocked.Increment(ref knownGeneration)`; (6) `Flush()`.
  - The later steps alternate with one data pass as today (up to 128 queued works per wake, flush timer unchanged).
  - Resume after `Restore` and the seed runs (3) and (6), then the steps. `RetryClear` with `MarkerSaved` true runs the same (Task 16).
  - Phases: a `ClearStep` exception is caught on the worker and gives Failed with its type and `MarkerSaved` true; it never reaches the catch at :177-181, so the log keeps working and `RetryClear` re-posts. A clear posted after the alert worker stopped on a failure gives Unavailable with that failure's type and writes nothing.
  - `Status` ends with `HistoryClearCopy.StatusSuffix(ClearState, true)`. The alert log has no percent.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Clearing_an_app_forgets_it_so_its_next_poll_alerts_again_and_keeps_the_dont_alert_list(){
    // After learning, a.exe is known. Clear(one app "A.EXE", all time): its known row goes in BeginClear's transaction; a.exe's next poll gives one episode.
    // An a.exe poll between BeginClear and the last episode chunk keeps its new row and episode, also when its FirstSeen is before Requested.
    // All apps, all time: known_apps is empty on every machine key; learning dates and seed markers unchanged. Older than 7 days: every known row and FirstUse.KnownApps unchanged.
    // BeginClear failing (a read-only file): no known row deleted, the app still known. After a restart mid-clear: only episodes are deleted, known rows written after BeginClear stay.
    // Configure's ignored list is unchanged throughout. ClearStepOverride throws once after a.exe has been learned again: after RetryClear() reaches Done, a.exe's new known row
    // and episode are kept and its next poll gives no second episode.
}
[Fact] public async Task Alert_clear_runs_in_steps_and_deletes_an_open_budget_episode_for_good(){
    // An open "chrome.exe" budget episode; Clear(one app, all time): later 600-byte minutes do not recreate the row; the known app is removed; the Don't alert list is unchanged.
    // StopAsync mid-clear (ClearStepOverride slow) completes within 1 s and pending_clear remains; a new service runs EndScope and Flush again and finishes.
    // ClearStepOverride throws IOException once: ClearState is Failed, MarkerSaved true, the service's Failed stays false, and a later NetworkUseWork is still saved.
    // A service whose worker stopped on a store failure: Clear(...) gives Unavailable with that failure's type, and pending_clear stays empty.
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `AlertService.Clear` and `ClearState`.
- [ ] **Step 3: Implement the command, its order and the resume**
- [ ] **Step 4: Build.** Expected: 0 errors.
- [ ] **Step 5: Commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/AlertServiceTests.cs
git commit -m "test(alerts): the alert-log clear forgets apps and ends budget episodes"
git add src/DeadWare.App/AlertService.cs
git commit -m "feat(alerts): clear the alert log on its worker"
```

Run (CI). Expected: green.

---

### Task 19: The session: the list, the clear request and page re-reads (App)

Spec: *Don't record these apps and Clear history…* → Settings model (`SetExcludedApps`, Startup, `IsExcluded`), Clear history… (Progress: re-reading pages, the last result), How each store clears → Shared rules (one at a time, unavailable); section 7 → Quit and sign-out; Tests → `ClearHistory_refuses_a_second_request_and_reports_a_stopped_worker_as_unavailable`, `HistoryClearChanged_rereads_the_open_pages`, `Quit_during_a_clear_meets_the_history_deadline`.

**Files:**
- Modify: `src/DeadWare.Core/HistoryClear.cs` (`HistoryClearLine`)
- Modify: `src/DeadWare.App/UiSession.cs`: the constructor (:69-89), the timer tick (:84-89), new members.
- Modify: `src/DeadWare.App/ConnectionsPage.cs`, `src/DeadWare.App/UsageHistoryPage.cs`, `src/DeadWare.App/AlertsPage.cs`: subscribe while loaded (their `Loaded`/`Unloaded` handlers); `AlertsPage`'s and `UsageHistoryPage`'s read seams.
- Test: `tests/DeadWare.App.Tests/HistoryClearSessionTests.cs` (new); the page half in the STA fact: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`, `UsageHistoryUiChecks.cs`, `AlertsUiChecks.cs`

**Interfaces:**
- Consumes: Tasks 13 and 15–18 (`SetExclusions`, `Configure`'s lists, the clear members); 0.16.0's `ReloadHistory`.
- Produces:
  - `public sealed record HistoryClearLine(HistoryClearStore Store, HistoryClearState State, bool CanRetry);` (Core).
  - On `UiSession`:
    - `public FrozenSet<string> ExcludedKeys { get; }` and `public bool IsExcluded(string? name)`.
    - `public void SetExcludedApps(IEnumerable<string> names)`, the spec's steps 1–3: normalize and return if unchanged (ordinal, in order); `SetExclusions(keys)` on `Connections`, `Usage` and `ProcessHistory`; `Alerts.Configure(Settings.Alerts, Settings.AlertNotifications, Settings.FirstUseIgnoredApps, Settings.ExcludedApps)`; save and raise `Changed`.
    - The constructor passes the loaded set to each history service right after its `Configure`.
    - `public bool ClearHistory(HistoryClear request)`: false while any store's state is Queued or Clearing, or Failed with `MarkerSaved`; otherwise posts the request to all four services and returns true.
    - `public void RetryClear(HistoryClearStore store)`.
    - `public IReadOnlyList<HistoryClearLine> ClearLines()`: Connections, Usage, ProcessHistory, Alerts, the stores whose phase is not None, in that order.
    - `public bool ClearActive { get; }` (the refusal condition) and `public bool ClearWaitingForRestart { get; }` (a store is Failed with `MarkerSaved` and cannot retry, so its worker has stopped).
    - `public event Action<HistoryClearStore>? HistoryClearChanged;`, raised on the UI thread by `internal void CheckClearStates()`, which the 1-second timer calls. It fires when a store's phase becomes Clearing and when it leaves Clearing.
    - **Fixture sessions.** The UI checks build `UiSession` with `GetUninitializedObject` and set only some members (`ConnectionsPageUiChecks.cs:30-31` sets only `Settings`; the dashboard session at `BatteryUiTests.cs:101-106` has no `Connections` or `ProcessHistory`). So `ExcludedKeys` returns its cached set or, when unset, `AppExclusions.Keys(Settings.ExcludedApps)`, and `IsExcluded` never throws. `SetExcludedApps` calls `Connections?.SetExclusions`, `Usage?.SetExclusions`, `ProcessHistory?.SetExclusions` and `Alerts?.Configure`, as `SetUsageRecording` does for `ProcessHistory` (`UiSession.cs:228`). `ClearHistory`, `RetryClear`, `ClearLines`, `ClearActive`, `ClearWaitingForRestart` and `CheckClearStates` skip a null service.
  - Pages, while loaded: `ConnectionsPage` calls `ReloadHistory()` on Connections; `UsageHistoryPage` refreshes (`RequestRefresh()`) on Usage or ProcessHistory; `AlertsPage` re-reads its log (`RequestRefresh()`) on Alerts.
  - Read seams for the page checks, replaced by reflection: `ConnectionsPage`'s `readHistory` (:80); `UsageHistoryPage`'s `readStrip` and `showing` (0.16.0), with every `IsLoaded && IsVisible` test in `RefreshAsync` (:208-224) reading `showing()`; on `AlertsPage`, new `private Func<bool> showing` (`() => IsLoaded && IsVisible`, read by every such test in `RefreshAsync` :186-202) and `private Func<DateTimeOffset, DateTimeOffset, Task<AlertLogView>> readLog` (`(from, to) => session.Alerts.ReadEpisodesAsync(from, to)`, at :194), both assigned in the constructor.

- [ ] **Step 1: Write the failing tests**

```csharp
// Session(...) builds an uninitialized UiSession with real services on temp files, as CoolingShutdownTests does, and Settings with ExcludedApps.
[Fact] public async Task ClearHistory_refuses_a_second_request_and_reports_a_stopped_worker_as_unavailable(){
    // ClearHistory(all apps, all time) → true; a second call while any store is Queued or Clearing → false; after all are Done → true again.
    // With Usage Failed and MarkerSaved true (its ClearStepOverride throws once) and its worker running: ClearActive is true, ClearWaitingForRestart false, ClearHistory returns false;
    // after RetryClear(HistoryClearStore.Usage) reaches Done, it returns true.
    // The Usage worker stopped by a failure: its ClearState is Unavailable with that failure's type and nothing is written. A failed Usage read with its worker running (Failed true) is not Unavailable.
}
[Fact] public void HistoryClearChanged_rereads_the_open_pages(){
    // The session half only (page checks run in the STA fact, below): states driven through ClearStepOverride, CheckClearStates raises HistoryClearChanged(store) once when each store
    // reaches Clearing and once when it leaves Clearing.
}
[Fact] public async Task Quit_during_a_clear_meets_the_history_deadline(){
    // A session built like CoolingShutdownTests.StuckSession (:81-98), whose collector worker completes, with ProcessHistory set and diagnostics as at :95; ClearStepOverride sleeps
    // 200 ms per step on every service.
    // StopCollectorAsync, invoked by reflection with Quit's deadlines (5 s, 5 s, 20 s) as CoolingShutdownTests.Stop (:68) does, finishes within 20 s, and the trace has no
    // collector_stop_timeout. (ShutdownAsync itself needs layout, timer and saveTimer, which such a session lacks.)
    // StopCollectorForSessionEnding during a clear, with a hidden Connections backlog (expiry raised, maintenanceTransactions 0 so far), returns within 3 s with no stage outstanding.
}
// The page half, in the STA fact, each raising the session's HistoryClearChanged backing delegate by reflection on a page loaded by raising FrameworkElement.LoadedEvent,
// with the read seams above replaced by counting fakes and showing() true:
// ConnectionsPageUiChecks: a page in History repeats its whole-interval read on Connections, and not on Usage.
// UsageHistoryUiChecks: a new strip read on Usage and on ProcessHistory.
// AlertsUiChecks: readLog is called again on Alerts.
// Each: after raising FrameworkElement.UnloadedEvent, the event causes no read.
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `ClearHistory`, `CheckClearStates` and `HistoryClearChanged`.
- [ ] **Step 3: Implement the session members, the startup wiring and the page subscriptions**
- [ ] **Step 4: Build.** Expected: 0 errors.
- [ ] **Step 5: Commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/HistoryClearSessionTests.cs tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs tests/DeadWare.App.Tests/AlertsUiChecks.cs
git commit -m "test(session): one clear at a time, page re-reads and the quit deadline"
git add src/DeadWare.Core/HistoryClear.cs src/DeadWare.App/UiSession.cs src/DeadWare.App/ConnectionsPage.cs src/DeadWare.App/UsageHistoryPage.cs src/DeadWare.App/AlertsPage.cs
git commit -m "feat(session): Don't record list, clear requests and HistoryClearChanged"
```

Run (CI). Expected: green; the UI fact passes "Connections page (all themes)", "usage history" and "alerts".

---

## 0.17.0 — Pages

### Task 20: The Alerts page for first network use (App)

Spec: *First network use alerts* → The rule (Don't alert list: `IgnoreFirstUse`, `UnignoreFirstUse`), Alerts page (Editor, Rule row, Don't alert list, Log row), Copy; *Don't record these apps and Clear history…* → Copy (Alerts rule line: Not counted); Tests → the `AlertsUiChecks` additions and `ShellUiChecks` (the Settings help line), and `AlertsUiChecks`' Not counted note.

**Files:**
- Modify: `src/DeadWare.Core/FirstNetworkUse.cs` (`RuleLine`)
- Modify: `src/DeadWare.App/AlertsPage.cs`:
  - `AlertsPageData` :12 and `Kinds` :17-21;
  - the intro :51, the footer :65 and the log note :75;
  - `UpdateEditor` :98-112, `Draft` :114-131, `AddRule` :132-139;
  - `RenderRules` :165-179, `RequestSuggestion` :204-212, `Apply` :234-261.
- Modify: `src/DeadWare.App/UiSession.cs` (`IgnoreFirstUse`, `UnignoreFirstUse`), `src/DeadWare.App/Windows.cs:176` (the Settings alert help)
- Test: `tests/DeadWare.App.Tests/AlertsUiChecks.cs`, `tests/DeadWare.App.Tests/ShellUiChecks.cs`

**Interfaces:**
- Consumes: Tasks 3, 13 (`FirstUse`, `Failed`), 19 (`IsExcluded`); 0.16.0's `RequestConnectionsHistory`.
- Produces:
  - `public static string RuleLine(bool logFailed, FirstUseStatus status, bool enabled, bool elevated, bool captureAvailable, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)`: the spec's nine states in order, "1 app known" for one, the learning date `(end − 1 tick)` local `dddd d MMMM`.
  - `AlertsPageData(…, string Status, FirstUseStatus? FirstUse = null, bool Elevated = false, bool CaptureAvailable = false, IReadOnlyList<string>? IgnoredApps = null)`; the positional fixture at `AlertsUiChecks.cs:50` compiles unchanged.
  - `public void IgnoreFirstUse(string name)` and `public void UnignoreFirstUse(string key)` on `UiSession`: each normalizes `FirstUseIgnoredApps`, calls `Alerts.Configure` with all four arguments, raises `Changed` and saves.
  - The page:
    - **Editor:** `new("First network use by an app", AlertKind.FirstNetworkUse)` appended to `Kinds`; for it, `UpdateEditor` hides Reading, Executable, At or above and For, and `Draft` parses no threshold. `AddRule` refuses a second one with "A first network use rule already exists.", and the suggestion reads "New apps are learned quietly for 3 calendar days after per-app capture first runs; after that each app's first network use is logged once."
    - **Rule row:** the switch and **Remove**, no **Edit**, and the 11 pt Muted `RuleLine` below.
    - **Don't alert list:** "Not alerted, even when new again:" then one line per app sorted `OrdinalIgnoreCase`, each with **Alert again** (automation name "Alert again for {name}"). It shows whenever the list is non-empty.
    - **Log rows** for kind 4:
      - the Summary in Ink, and the second line "{Start local:g} · first network use", plus " · acknowledged" and " · on the Don't alert list";
      - **View in Connections** when Start is at most 30 days old, calling `RequestConnectionsHistory(minute(Start) − 5 min, minute(Start) + 10 min, Application)`; otherwise **View in Usage history**;
      - **Don't alert for this app** (automation name "Don't alert for {name}") when the key is not listed: it adds and acknowledges, and with 256 listed it stays enabled and a click sets the action status (Warning) to "The Don't alert list holds 256 apps; remove one first.";
      - **Acknowledge**, as today.
    - **Copy:** the spec's intro ending, footer, log note and Settings help (`Windows.cs:176`), exactly; the `ShellUiChecks.cs:89` prefix is unchanged.
    - **Not counted:** "Not counted: this app is in Don't record these apps." as a Muted note under an ApplicationDaily rule whose target `session.IsExcluded`, and in the editor while the Executable text is excluded.

- [ ] **Step 1: Write the checks**

```csharp
// AlertsUiChecks, after the existing theme loop, in Midnight, Daylight and Blackout. Fu = DefaultFirstNetworkUse; St(...) = a FirstUseStatus.
// The nine rule lines, each through Apply(new([Fu], true, log, "status", FirstUse:…, Elevated:…, CaptureAvailable:…)) (the first four positional, as :50) and session.Alerts' Failed set
//   by reflection for the first:
//   "Paused: the alert log is unavailable." · "Starting: reading the known-app list." · "Switched off: apps are learned without alerts · 1 app known." ·
//   "Waiting for per-app capture, which runs while DeadWare runs as administrator. Learning starts then." · "Waiting for per-app capture to start. Learning starts then." ·
//   "Learning quietly through Wednesday 4 November · 12 apps known." (en-GB culture, end Thursday 00:00) · "Watching for new apps · 12 apps known." ·
//   "Not watching: per-app capture needs DeadWare to run as administrator · 12 apps known." · "Not watching: per-app capture is not running · 12 apps known.".
// The first-use rule row has Remove and no Edit. Selecting the kind hides Reading, Executable, At or above and For; AddRule with one present shows "A first network use rule already exists.".
// Log rows: a kind-4 episode 29 days old shows View in Connections, which raises ConnectionsHistoryRequested with [minute(Start) − 5 min, minute(Start) + 10 min) and "chrome.exe";
//   one 31 days old shows View in Usage history. Second lines "… · first network use", " · acknowledged", " · on the Don't alert list".
// Don't alert for this app is absent for a listed app; with 256 listed, a click leaves the list unchanged and actionStatus reads the exact text. Automation names "Alert again for chrome.exe"
//   and "Don't alert for chrome.exe". The Not alerted list with Alert again shows also with no first-use rule.
// Not counted: with "chrome.exe" excluded, the app-rule row and the editor (Executable "chrome.exe") show "Not counted: this app is in Don't record these apps.".
// The existing counts at :72-73 hold for the fixture without first-use episodes.
// ShellUiChecks: the alert help found by its prefix (:89) ends with "Cooling Health keeps its own notification setting." and contains "The first network use rule learns your apps for 3 days".
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1739` for `FirstUse` in `AlertsPageData`, and `error CS0117` for `FirstNetworkUse.RuleLine`.
- [ ] **Step 3: Implement the rule line, the page changes, the copy and the session methods**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/AlertsUiChecks.cs tests/DeadWare.App.Tests/ShellUiChecks.cs
git commit -m "test(ui): first network use on the Alerts page"
git add src/DeadWare.Core/FirstNetworkUse.cs src/DeadWare.App/AlertsPage.cs src/DeadWare.App/UiSession.cs src/DeadWare.App/Windows.cs
git commit -m "feat(alerts): first network use rule line, Don't alert list and log rows"
```

Run (CI). Expected: green; the UI fact passes "alerts" and "shell chrome".

---

### Task 21: Settings → Don't record these apps (App)

Spec: *Don't record these apps and Clear history…* → Settings → History and alerts (heading to keyboard and theme, without the Clear history part), Copy (Settings); Tests → `ShellUiChecks` for the Settings block.

**Files:**
- Create: `src/DeadWare.App/ExcludedAppsBlock.cs`
- Modify: `src/DeadWare.App/Windows.cs`: the block after the Connections block (:178-180), in the History and alerts card.
- Test: `tests/DeadWare.App.Tests/ShellUiChecks.cs`

**Interfaces:**
- Consumes: Task 19 `SetExcludedApps` and its null-service guards; Task 1 `AppExclusions`; Task 17's `TryReadApplicationsAsync(…, limit)`; `UsageHistoryService.ReadApplicationsAsync`.
- Produces:
  - `internal sealed class AppSuggestions(UiSession session, Func<DateTimeOffset> clock)`:
    - `internal Task RefreshAsync()`: at most once a minute, `Usage.ReadApplicationsAsync(now − 400 d, now + 1 min, 1000)` and `ProcessHistory.TryReadApplicationsAsync(now − 30 d, now, ProcessRank.CpuTime, limit: 1000)`, off the UI thread; a failed read, or a null service (fixture sessions), leaves its names out.
    - `internal IReadOnlyList<string> Names(bool includeListed)`: the union by key of the current reading's apps (`Reading.Processes` names, then the `ProcessActivity` app names; none while `session.Current` is null), the Usage names and the Application resources names, in that order. Each key keeps its first source's spelling. Names `Accepts` rejects are dropped, and listed ones too unless `includeListed`. Sorted `StringComparer.OrdinalIgnoreCase`.
  - `internal sealed class ExcludedAppsBlock : StackPanel` with `internal ExcludedAppsBlock(UiSession session, AppSuggestions suggestions)`:
    - the heading `Don't record these apps` (16) and the help text;
    - `internal ComboBox Box` (editable, automation name `App to stop recording`, at most 260 characters), `internal Button Add` (Enter in the box adds too);
    - `internal TextBlock Error` (Warning) and `internal TextBlock Note` (Muted);
    - `internal StackPanel List`, one row per entry sorted `OrdinalIgnoreCase`, each with `Remove` (automation name `Remove <name> from Don't record these apps`), or `No apps are excluded.`.
    - Add errors: `Enter an app's executable name, such as chrome.exe.`, `That app is already in the list.`, `At most 64 apps can be listed.`. After a successful Add the note reads `Saved history for this app is kept. Use Clear history… to remove it.`; the next add or remove hides it, and a successful add shows it again.
    - Tab order: box, `Add`, each `Remove`. After a remove, focus moves to the next row's `Remove`, or to the box when the list empties, with `if (!next.Focus()) FocusManager.SetFocusedElement(scope, next)`, as `ApplicationResourcesCard.ClearSlot` does (:193-195). Every brush is a `DynamicResource` token. `Loaded` and showing the Settings page call `suggestions.RefreshAsync()`.

- [ ] **Step 1: Write the checks**

```csharp
// ShellUiChecks, a new Settings check on the dashboard's Settings page: Block = the ExcludedAppsBlock found in the History and alerts card, after the Connections block. The dashboard
// session (BatteryUiTests.cs:101-106) has no Connections or ProcessHistory and collector.latest is null (:241), so the check first sets collector.latest to a Reading whose Processes
// hold a ProcessTraffic named "fixture.exe" (back to null at the end) and awaits the block's suggestions.RefreshAsync(); SetExcludedApps' null guards (Task 19) cover the rest.
// Exact strings: the heading, the help text, "Add", "No apps are excluded.". Choosing the suggestion "fixture.exe" and Add: session.Settings.ExcludedApps == ["fixture.exe"], the box
// cleared, the note shown. Typing "Steam.exe" + Enter adds it. Errors: "C:\\x.exe" → the first error; "STEAM.EXE" → the second; with 64 listed → the third. The list's first Remove has the automation
// name "Remove fixture.exe from Don't record these apps". Focus (logical; offscreen nothing takes keyboard focus): scope = FocusManager.GetFocusScope(Block);
// FocusManager.SetFocusedElement(scope, the first Remove); clicking it leaves FocusManager.GetFocusedElement(scope) on the next row's Remove; removing the last row leaves Box focused.
// After a Remove, Note is collapsed. Suggestions exclude listed apps. The list is emptied again at the end.
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS0246` for `ExcludedAppsBlock`.
- [ ] **Step 3: Implement the block and `AppSuggestions`, and place the block**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ShellUiChecks.cs
git commit -m "test(ui): Settings Don't record these apps"
git add src/DeadWare.App/ExcludedAppsBlock.cs src/DeadWare.App/Windows.cs
git commit -m "feat(settings): Don't record these apps"
```

Run (CI). Expected: green; "shell chrome" passes.

---

### Task 22: Clear history…: the window, progress and the three entry points (App)

Spec: *Don't record these apps and Clear history…* → Settings → History and alerts (Clear history), Clear history… (Entry points, Window, Choices, Boundary, What it clears and keeps, Confirmation, Progress), Copy (Clear history window, Confirmation, Progress, Usage history line); Tests → `HistoryClearUiChecks` (new), `ShellUiChecks` (`Clear history…` disabled with each tooltip), `ApplicationResourcesUiChecks` and `UsageHistoryUiChecks` (the entry points and copy).

**Files:**
- Modify: `src/DeadWare.Core/HistoryClear.cs` (the rest of `HistoryClearCopy`)
- Create: `src/DeadWare.App/ClearHistoryWindow.cs`
- Modify:
  - `src/DeadWare.App/Windows.cs` (after Task 21's block);
  - `src/DeadWare.App/ConnectionsPage.cs` (the Recording, origin and storage expander, :122-124);
  - `src/DeadWare.App/UsageHistoryPage.cs` (Keep a longer record, :116-121);
  - `src/DeadWare.App/UiSession.cs` (`Ending`).
- Create: `tests/DeadWare.App.Tests/HistoryClearUiChecks.cs`
- Modify: `tests/DeadWare.App.Tests/BatteryUiTests.cs`: `Phase("history clear"); HistoryClearUiChecks.Run(app);` right after `AlertsUiChecks.Run(app);` (:87). Also `ShellUiChecks.cs`, `ConnectionsPageUiChecks.cs`, `UsageHistoryUiChecks.cs` and `ApplicationResourcesUiChecks.cs`.

**Interfaces:**
- Consumes: Task 19 (`ClearHistory`, `RetryClear`, `ClearLines`, `ClearActive`, `ClearWaitingForRestart`, `Changed`, the null-service guards); Task 16's `ClearState` internal setter and `CanRetryClear`; Task 21 `AppSuggestions`; Task 1 `HistoryClear.Create`, `AppExclusions.Accepts`.
- Produces:
  - `HistoryClearCopy`:
    - `Confirmation(HistoryClear clear, string appName, TimeZoneInfo zone, CultureInfo culture)` (the four texts, `<before>` in local "g");
    - `Line(HistoryClearLine line)` (`<store> · waiting`, `· clearing (<n> %)` or `· clearing` for the alert log, `· done`, both failed forms, `· unavailable (<Type>); not cleared`);
    - `Header(IReadOnlyList<HistoryClearLine> lines)` and `Summary(IReadOnlyList<HistoryClearLine> lines)`: `<n> of 4 finished.`, with the number of lines for a resumed clear, then the two optional sentences;
    - `DisabledTip(bool waiting)`;
    - constants for every window, Settings and Usage history string in the spec's Copy.
  - `internal static class ClearHistoryEntry { internal static Button Create(UiSession session, Func<Window?> owner); }`: a `Clear history…` button, disabled while `ClearActive` or `ClearWaitingForRestart`, with `ToolTipService.ShowOnDisabled` and the two tooltips. It refreshes on `Changed` and opens a `ClearHistoryWindow` owned by `owner()`.
  - `internal sealed class ClearHistoryWindow : Window` (Title `Clear history`, Width 480, `SizeToContent.Height`, every brush a `DynamicResource` token):
    - the controls `OneApp`, `AllApps`, `OlderThan7Days`, `AllTime` (RadioButtons; defaults One app and Older than 7 days), `AppBox` (editable ComboBox, automation name `App to clear`, `suggestions.Names(includeListed: true)`), `Validation`, `ClearButton` (not default), `CancelButton` (`IsCancel`) and `Progress`;
    - `internal Func<Window, string, bool> Confirm`, by default `MessageBox.Show(owner, text, "DeadWare", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes`, with this window as `owner`, so the confirmation closes with it;
    - `internal bool Submit()`: T = `DateTimeOffset.UtcNow`, `HistoryClear.Create`, the confirmation naming `SavedName(text)`; No sends nothing; Yes calls `ClearHistory` and shows the progress view with focus on `Close`, which then takes Esc. After `Confirm` returns, it sends nothing when the window has closed meanwhile or the session has begun ending (a new `internal bool Ending => layout?.Ending ?? false` on `UiSession`; Quit and sign-out call `layout.BeginShutdown()` first, `UiSession.cs:421`, `:292`).
    - It closes when its owner closes or the session ends.
  - `internal sealed class HistoryClearProgressView : StackPanel` with `internal TextBlock Header` (`AutomationProperties.LiveSetting` Polite), `internal IReadOnlyList<TextBlock> Lines`, `internal TextBlock Summary`, `internal Button TryAgain` (shown when a line `CanRetry`, calling `RetryClear`), `internal Button? Close` (the window's only) and `internal void Apply(IReadOnlyList<HistoryClearLine> lines)`. It refreshes on `Changed`.
  - Placement:
    - **Settings:** heading `Clear history` (16), its help, the entry button and a progress view shown whenever `ClearLines()` is non-empty.
    - **Connections:** the entry button in the expander.
    - **Usage history:** the line `Apps in Don't record these apps are not saved here; the Device charts still include their traffic.` and then the entry button in Keep a longer record.

- [ ] **Step 1: Write the checks**

```csharp
// HistoryClearUiChecks.Run(app), in Midnight, Daylight and Blackout, on an uninitialized session with Settings, a Collector whose latest reading has no processes (Current is read
// by AppSuggestions), a fixture DetachedWindowState as layout, and unstarted Usage and Alerts services. Progress lines, headers and summaries are driven by calling HistoryClearProgressView.Apply and HistoryClearCopy with constructed HistoryClearLine values; the session-level
// states the entry buttons read are set through the services' internal ClearState setter (Task 16).
// Defaults: OneApp, OlderThan7Days checked, ClearButton disabled. Typing "a*" shows Validation "Enter an app's executable name, such as chrome.exe."; "chrome.exe" enables ClearButton; AllApps enables it.
// Confirm hook: the four texts for (one app, all apps) × (Older than 7 days, All time) with <before> in local "g"; answering No sends nothing (every ClearState None). A Confirm hook that
//   calls layout.BeginShutdown() and returns Yes also leaves every ClearState None.
// Progress: each line form ("Connections history · waiting", "Usage history · clearing (37 %)", "Application resources · done", "Alert log · failed (IOException); it resumes when DeadWare next starts",
//   "… · failed (IOException); not cleared", "… · unavailable (IOException); not cleared"); 99 % shown for Done 999 of 1,000; the headers "Clearing history…", "Finishing a clear that was
//   interrupted…", "History cleared." with "Freed space is reused for new history; the files don't shrink. Exported CSV files and Windows notification history are not cleared.", and
//   "History partly cleared." with "2 of 4 finished. Failed histories resume when DeadWare next starts. Histories marked not cleared need Clear history again.".
// TryAgain visible only while a line CanRetry; Close closes the window and the clear keeps running (states unchanged). Keyboard (offscreen nothing takes keyboard focus): the window's
// focusable controls, in tree order, are the Apps radios, AppBox, the Range radios, ClearButton and CancelButton, with no TabIndex override; ClearButton.IsDefault is false and
// CancelButton.IsCancel is true; Enter pressed in AppBox through the Press helper (ApplicationResourcesUiChecks.cs:1267-1272, with its KeySource) changes no ClearState; after
// Submit, FocusManager.GetFocusedElement(scope) is Close. Header's LiveSetting is Polite. A theme change while open restyles the window (its Background and text brushes equal the new theme's resources).
// ShellUiChecks: with session.Usage's ClearState set to Clearing, the Settings Clear history… button is disabled and its ToolTip is "A clear is in progress." with ShowOnDisabled true;
//   set to Failed with MarkerSaved true (its worker not started, so CanRetryClear is false), "A clear is waiting to finish; it resumes when DeadWare next starts."; the result stays in
//   Settings until a new clear starts. The state is set back to None at the end.
// UsageHistoryUiChecks and ApplicationResourcesUiChecks: Keep a longer record shows the new line and then Clear history….
// ConnectionsPageUiChecks: the Recording, origin and storage expander holds Clear history….
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS0246` for `ClearHistoryWindow` and `HistoryClearProgressView`.
- [ ] **Step 3: Implement the copy, the window, the progress view, the entry button and its three placements**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/HistoryClearUiChecks.cs tests/DeadWare.App.Tests/BatteryUiTests.cs tests/DeadWare.App.Tests/ShellUiChecks.cs tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs tests/DeadWare.App.Tests/ApplicationResourcesUiChecks.cs
git commit -m "test(ui): Clear history window, progress and entry points"
git add src/DeadWare.Core/HistoryClear.cs src/DeadWare.App/ClearHistoryWindow.cs src/DeadWare.App/Windows.cs src/DeadWare.App/ConnectionsPage.cs src/DeadWare.App/UsageHistoryPage.cs src/DeadWare.App/UiSession.cs
git commit -m "feat(ui): Clear history… from Settings, Connections and Usage history"
```

Run (CI). Expected: green; the UI fact passes "Connections page (all themes)", "usage history", "history clear" and "shell chrome".

---

### Task 23: " · not recorded" marks and footers on the live pages (App)

Spec: *Don't record these apps and Clear history…* → "not recorded" marks, Copy (Marks and notes, except the Alerts and Usage history lines); *The Destinations table* → With 0.17.0 ("not recorded" mark); Tests → `ConnectionsPageUiChecks.CheckDestinationsNotRecordedMark` and the mark additions to `ConnectionsPageUiChecks`, `ProcessesPageUiChecks`, the Network page block (`BatteryUiTests.cs:464-480`) and `ApplicationResourcesUiChecks`.

**Files:**
- Modify:
  - `src/DeadWare.App/Windows.cs`: the Network footer :100 and the Application column :99;
  - `src/DeadWare.App/ConnectionsPage.cs`: `ApplyLive`'s `PeerRow` (:237), the map labels (:241-242) and the expander note (:124);
  - `src/DeadWare.App/ConnectionsWidget.cs:36`;
  - `src/DeadWare.App/DestinationsCard.cs`;
  - `src/DeadWare.App/ProcessesPage.cs`: `ProcessRowView` :23, the app rows :383, `Spoken` :495;
  - `src/DeadWare.App/ApplicationResourcesCard.cs:129`.
- Modify: `src/DeadWare.Core/ProcessTable.cs` (`ProcessCopy.Footer` :21)
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`, `ProcessesPageUiChecks.cs`, `BatteryUiTests.cs`, `ApplicationResourcesUiChecks.cs`; `tests/DeadWare.Tests/ProcessTableTests.cs` (:69, :79)

**Interfaces:**
- Consumes: Task 19 `IsExcluded`, `SetExcludedApps` and its null-service guards (the check sessions); 0.16.0's `DestinationRow` and `DestinationsCard.ShowLive`.
- Produces:
  - The suffix ` · not recorded` after an excluded app's name: the Network page's Application cell; each Live `PeerRow`'s application text (so the node list and the detail box show it); map hover labels on the page and the widget.
  - The Network page's Application column (:99) becomes a `DataGridTemplateColumn` (header "Application", `SortMemberPath` "Name"). Its cell is a TextBlock built from the row's `Name`: the Name run and, when `session.IsExcluded(Name)`, a Muted run ` · not recorded`; cells are rebuilt on `session.Changed`. `RefreshTables` (:305-313) keeps projecting the bare `Name`, so its search and the sort are unchanged; the mark does not depend on `RefreshTables`, which returns early while the page is not visible (:307).
  - `DestinationRowContext(…, Func<string, bool>? IsExcluded = null)`. In Live, when the busiest app is excluded, the APPS cell gets the suffix after its name (before " +{n:N0}"). That app's tooltip line reads "{name} · not recorded · ↓ {received} · ↑ {sent}", and the spoken text gains ", not recorded" after its name. Copy row copies the shown cell. Grouping, the busiest choice, sorting and search use the bare name; History rows pass no `IsExcluded`.
  - `public string Mark { get; }` on `ProcessRowView` ("not recorded" or ""), shown Muted after the name on app rows only. The spoken summary becomes `<name>, not recorded, <lead>, …`. `Name`, sorting and search are unchanged.
  - The footers, exactly:
    - Network, appended: `Apps marked not recorded are in Settings → Don't record these apps; their traffic isn't saved.`;
    - Connections expander, appended: `Apps in Don't record these apps show live, marked not recorded, and are never saved.`;
    - `ProcessCopy.Footer`, a new line just before the GPU line: `Apps marked not recorded are in Don't record these apps; Application resources counts them under Everything else.`.
  - The Application resources note's sentence ends `…; Everything else holds the rest, including apps in Don't record these apps.`
  - The new footer line changes two existing Core tests: `The_footer_ends_with_the_gpu_line` (:69) expects `Count` 6 and still ends with the GPU line; `Footer_omits_disk_without_the_disk_build` (:79) expects 5 and 6.

- [ ] **Step 1: Write and update the checks**

```csharp
// ConnectionsPageUiChecks, called after the Destinations checks: session.SetExcludedApps(["Browser"]) on the Live fixture, whose 8.8.8.8 peer is "Browser" (:34; AppExclusions.Accepts
// it, key "BROWSER"). The list is cleared again at the end.
static void CheckDestinationsNotRecordedMark(UiSession session, ConnectionLiveView live){ // the 8.8.8.8 row's busiest app is Browser
    // Apps starts "Browser · not recorded", the tooltip has "Browser · not recorded · ↓ ", Spoken has "Browser, not recorded"; Copy row (SetClipboard fake) includes " · not recorded";
    // search "Browser" still finds the row; a History row whose busiest app is "Browser" (ShowHistory) carries no mark.
}
// Also: the node list label and the detail box show "Browser · not recorded"; map.DisplayedPoints' Label for its node ends " · not recorded"; the expander text ends with the new
// sentence. The page search "Browser" still keeps its node, and "not recorded" matches no node.
// ProcessesPageUiChecks: an excluded app row's Mark is "not recorded" and its Spoken starts "<name>, not recorded, "; its process rows have Mark ""; sorting by Name and search are unchanged.
// BatteryUiTests network block (:464-480): with "Fixture application" excluded (set before the fixture assigns ItemsSource, cleared after the block), the Application cell reads
// "Fixture application · not recorded", the column's SortMemberPath is "Name" and the row's Name is the bare name, and the footer ends with the new sentence.
// ApplicationResourcesUiChecks.The_note_names_gpu: Contains "…; Everything else holds the rest, including apps in Don't record these apps. Usage after".
// ProcessTableTests :69 Count 6, :79 Count 5 and 6.
```

- [ ] **Step 2: Build and run to verify failure.** Build. Expected: `error CS1061` for `ProcessRowView.Mark`, and `error CS1739` for `IsExcluded` in `DestinationRowContext`. Then run `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ProcessTableTests"` (the Core project builds alone). Expected: `The_footer_ends_with_the_gpu_line` fails at `Assert.Equal(6, …)` (actual 5), and `Footer_omits_disk_without_the_disk_build` fails at `Assert.Equal(5, …)` (actual 4).
- [ ] **Step 3: Implement the marks, the context member and the copy**
- [ ] **Step 4: Run the Processes Core tests (`--filter "FullyQualifiedName~ProcessTableTests"`), build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs tests/DeadWare.App.Tests/ProcessesPageUiChecks.cs tests/DeadWare.App.Tests/BatteryUiTests.cs tests/DeadWare.App.Tests/ApplicationResourcesUiChecks.cs tests/DeadWare.Tests/ProcessTableTests.cs
git commit -m "test(ui): not recorded marks and footers on live pages"
git add src/DeadWare.App/Windows.cs src/DeadWare.App/ConnectionsPage.cs src/DeadWare.App/ConnectionsWidget.cs src/DeadWare.App/DestinationsCard.cs src/DeadWare.App/ProcessesPage.cs src/DeadWare.App/ApplicationResourcesCard.cs src/DeadWare.Core/ProcessTable.cs
git commit -m "feat(ui): mark apps in Don't record these apps on live pages"
```

Run (CI). Expected: green; "Connections page (all themes)", "processes page", "usage history" and "dashboard theme text" pass.

---

### Task 24: Cleared time and an excluded application filter on both strips (App)

Spec: *Don't record these apps and Clear history…* → The traffic strip after a clear or with an excluded app (Drawing, Not saved, An excluded application filter), Copy (Traffic strip); section 7 → Errors (the two 0.17.0 cases); Tests → `ConnectionsPageUiChecks.CheckStripNotRecordedAndClearedTime` and the `UsageHistoryUiChecks` additions.

**Files:**
- Modify:
  - `src/DeadWare.App/TrafficStripChart.cs` (0.16.0): the Not saved state, the band label and `NoteLines`;
  - `src/DeadWare.App/ConnectionsPage.cs`: the application filter button and the strip apply;
  - `src/DeadWare.App/UsageHistoryPage.cs` (`ApplyStrip`).
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`, `tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs`

**Interfaces:**
- Consumes: Task 9's `RetainedByClear` and `AppClearedBefore`; Task 19 `IsExcluded`; 0.16.0's chart, view and filter button.
- Produces:
  - `TrafficStripChart.SetData(TrafficStrip strip, DateTimeOffset dueUntil, DateTimeOffset now, bool filterNotRecorded = false)` and `TrafficStripView.Apply(TrafficStrip? strip, DateTimeOffset dueUntil, DateTimeOffset now, bool filterNotRecorded = false)`.
  - `internal string? BandLabel { get; }` on the chart: "Not kept" or `Cleared` (with `RetainedByClear`). The retained start is `RetainedFrom` when set on both pages, so nothing inside the band is hatched.
  - **Not saved**, checked just before No traffic: `RecordedSeconds` > 0, measure bytes 0, and the slot starts before `AppClearedBefore` or `filterNotRecorded` is set.
    - It is hatched over the full height and stays a hover and keyboard target.
    - Its chip is the span then `No saved traffic`, then `Some application history before {g} was cleared` and/or `Not recorded: in Don't record these apps`, with no value line.
    - While it is hovered or selected, the legend values are "—" with the chip's reason line as the tooltip.
  - `internal IReadOnlyList<string> NoteLines { get; }` on the view: every line under the legend in order. That is `NotKeptLine` (`Cleared before {RetainedFrom local "g"}` with `RetainedByClear`), then `Some application history before {AppClearedBefore local "g"} was cleared.`, then, with `filterNotRecorded`, `Not recorded: this app is in Don't record these apps.`.
  - Connections, while `IsExcluded(applicationName)` (checked again on `Changed`): the filter button reads `Application: {applicationName} · not recorded ✕`, its tooltip adds ` It is in Don't record these apps, so its traffic isn't saved.`, and the strip is applied with `filterNotRecorded: true`.

- [ ] **Step 1: Write the checks**

```csharp
// ConnectionsPageUiChecks, after CheckApplicationFilter:
static void CheckStripNotRecordedAndClearedTime(UiSession session, DateTimeOffset now){
    // Excluded filter (session.SetExcludedApps(["chrome.exe"]), legend click on chrome.exe): the button found by automation name "Clear application filter" reads
    //   "Application: chrome.exe · not recorded ✕" and its ToolTip ends " It is in Don't record these apps, so its traffic isn't saved."; NoteLines contains
    //   "Not recorded: this app is in Don't record these apps."; a recorded zero-byte slot is hatched (chart.Hatch contains its centre), HoverAt gives
    //   [span, "No saved traffic", "Not recorded: in Don't record these apps"], HandleKey reaches it, and the legend values are "—" with that tooltip; slots with bytes keep their bands.
    // RetainedByClear: BandLabel "Cleared"; NoteLines[0] "Cleared before {g}". AppClearedBefore with the filter: its line, and Not saved slots before it under the filter;
    //   with AppClearedBefore and no filter, a zero-byte slot before it is Not saved, not No traffic.
}
// UsageHistoryUiChecks: ApplyStrip with RetainedByClear draws the band labelled "Cleared" and NoteLines[0] "Cleared before {g}", with no hatching inside the band;
//   with AppClearedBefore, its line, and a zero-byte slot before it is Not saved, not No traffic.
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `NoteLines` and `BandLabel`, and `error CS1739` for `filterNotRecorded`.
- [ ] **Step 3: Implement the state, the label, the lines and the filter marks**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs
git commit -m "test(ui): cleared time and the not recorded filter on both strips"
git add src/DeadWare.App/TrafficStripChart.cs src/DeadWare.App/ConnectionsPage.cs src/DeadWare.App/UsageHistoryPage.cs
git commit -m "feat(ui): Cleared band, Not saved slots and the not recorded filter"
```

Run (CI). Expected: green; "traffic strip", "Connections page (all themes)" and "usage history" pass.

---

## 0.17.0 — Release

### Task 25: 0.17.0 release docs and version

Spec: section 7 → Docs and release (0.17.0), Rollback (0.17 to 0.16 or older), Pending by eye (0.17.0); *First network use alerts* → Docs and release; *Don't record these apps and Clear history…* → Docs and release, Errors and limits (Clock).

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.17.0</Version>`)
- Modify: `CHANGELOG.md`: a new top section, `## 0.17.0 — First network use alerts and history controls`.
- Create: `docs/releases/v0.17.0.md`, in the shape of `docs/releases/v0.15.0.md`: bullets, then `## Verification`.
- Modify: `docs/validation.md`: a new top section, `## First network use alerts and history controls (0.17.0, YYYY-MM-DD)`, dated with the UTC date this step runs.
- Modify: `docs/user-guide.md`. Line numbers are at 75d6d65 and 0.16.0 adds paragraphs, so anchor on the quoted text:
  - **Alerts.** In the paragraph that starts "Open **Alerts** in the sidebar" (:183), "Four kinds are available." becomes "Five kinds are available." and gains "A **first network use** rule logs each app the first time DeadWare sees it use the network (below)." In the paragraph that starts "Every crossing is one **episode**" (:187), "it never notifies more than once an hour" becomes "it never notifies more than once an hour (first network use is the exception, below)". After the paragraph that starts "Device and network rules evaluate" (:189) comes the spec's first network use paragraph, verbatim. Then one sentence points to Don't record these apps and Clear history: budgets of excluded apps stop counting; clearing an app for all time makes its next network use alert again; clearing older than 7 days keeps it known.
  - **Usage history** (:165-179): a new paragraph covering each point of the spec's list:
    - the list, where it is and what it stops (Application resources counts excluded apps under Everything else, GPU included, so device totals stay true; budgets stop counting);
    - that saved history is kept until cleared, and "PID n";
    - Clear history's choices, what it clears and keeps, and that "PID n" and "Unknown application" rows are cleared only with All apps;
    - the 60-second rule for all time, resuming after Quit or sign-out, and "not cleared";
    - that freed space is reused and the files don't shrink, and that exported CSVs and Windows notification history are not cleared;
    - the strips' Cleared band and lines, and the Connections strip's "not recorded" filter.
  - **Connections** (:129-145): one sentence pointing to Don't record and Clear history, with the clock limitation: after an all-apps clear made while the clock was ahead, setting the clock back hides new Connections history until the clock catches up.
  - **Network measurements and privacy**: the paragraph that starts "Interface totals work independently" (:207) gains the spec's `alerts.db` sentence verbatim, and one sentence pointing to Don't record and Clear history.
  - **Build** (:230): `-Version 0.16.0` becomes `-Version 0.17.0`.
- Modify: `README.md`, one line only: the **Look back** bullet (:23) adds "get an alert the first time an app uses the network, and keep chosen apps out of history or clear it".

**Interfaces:**
- Consumes: Tasks 1–24 as merged on the branch. Re-read the touched code before writing: the docs describe the code, not this plan.

- [ ] **Step 1: Bump the version and write the CHANGELOG section.** One bullet each:
  - **First network use alerts:** added once and switched on (not when 64 rules already exist); 3 days of learning after per-app capture first runs; one balloon per 60 s; the Don't alert list; View in Connections.
  - **Don't record these apps:** the live " · not recorded" marks, and what stops being saved.
  - **Clear history…:** what it clears and keeps, and that it resumes after Quit or a crash.
  - **Returning to 0.16 or older:** the rollback paragraph below.
- [ ] **Step 2: Write `docs/releases/v0.17.0.md`.** It holds the same points as user-facing bullets and one rollback paragraph shared by both sections.
  - The first-use sentences (spec, *First network use alerts* → Docs and release): that build's first save drops the rule and the Don't alert list; alerts.db keeps the known apps and the learning date, and older builds show first-use episodes with "peak 0"; reinstalling 0.17 adds the rule again once, even if it had been removed, with no new learning period; apps first used meanwhile alert at their next use, and Don't alert entries are gone.
  - Then the spec's Don't record paragraph verbatim ("Going back to 0.16 or earlier drops the Don't record these apps and Don't alert lists from settings.json on that version's first save, …").
  - Then `## Verification`: automated coverage by area, the CI measurements as measured, and the checks by eye as pending.
- [ ] **Step 3: Write the `docs/validation.md` section:**
  - `### CI measurements (as measured, no threshold)`, with run links:
    - Task 2's two fill times and the build job's duration against its 20-minute limit;
    - Task 9's four clears (total ms, steps, p95 step ms, largest WAL);
    - Task 14's `First_network_use_sees_this_process_send_one_udp_datagram` result as recorded, or `capture: unavailable`.
  - `### Findings`: the accepted clock-ahead limitation of an all-apps clear, beside the monotonic `expiry` watermark finding of the 0.12.2 connections review (:832), which stays open.
  - `### Automated coverage`: Core by class (`AppKeyTests`, `HistoryClearTests`, `FirstNetworkUseTests`, `FirstUseTrackerTests`, and the alert, folder, evaluator and store classes), then App and UI checks on Windows CI by name.
  - `### Pending by eye`, never claimed as verified, every item of the spec's 0.17.0 list:
    - First network use alerts: the balloon's appearance and click-through; one balloon for three apps started within a minute; a real three-day learning run on an administrator install; the rule line for a standard user.
    - Don't record these apps and Clear history…: all apps, all time on the user's full `connections.db` and `usage-history.db` (the time until done, dashboard responsiveness, and Quit and sign-out during the clear, followed by the resume); an excluded app with a day of traffic absent from Connections History, from Usage history's Applications and from the Application resources rows while device totals still match; the Cleared band and Not saved slots on both strips, and the excluded application filter, in a light, a dark and a high-contrast theme.
  - `### Results`: the build line, the Core suite on Linux (passed, skipped, the 26 baseline failures), and the id and head SHA of the green Windows CI run. The controller updates this line before merging.
- [ ] **Step 4: Check the docs**

```bash
grep -c '<Version>0.17.0</Version>' Directory.Build.props                                          # 1
grep -c '^## 0.17.0 — First network use alerts and history controls$' CHANGELOG.md                # 1
grep -c '^## First network use alerts and history controls (0.17.0, ' docs/validation.md          # 1
grep -c 'publish.ps1 -Version 0.17.0' docs/user-guide.md                                           # 1
grep -c 'Four kinds are available' docs/user-guide.md                                              # 0
grep -c 'Five kinds are available' docs/user-guide.md                                              # 1
grep -c 'first network use is the exception, below' docs/user-guide.md                             # 1
grep -c 'Going back to 0.16 or earlier drops the Don' docs/releases/v0.17.0.md                     # 1
grep -c 'a real three-day learning run on an administrator install' docs/validation.md             # 1
grep -c 'even if it had been removed' docs/releases/v0.17.0.md                                     # 1
git diff --numstat origin/main -- README.md                                                        # 1	1	README.md (one line changed)
```

Then run the build and the Core baseline. Expected: as in Global Constraints.
- [ ] **Step 5: Commit, push, verify and mark the PR ready**

```bash
git add Directory.Build.props CHANGELOG.md docs/releases/v0.17.0.md docs/validation.md docs/user-guide.md README.md
git commit -m "docs: 0.17.0 release notes, validation and version"
```

Run (CI). Expected: green. Mark the PR ready, confirm through the GitHub tools that GitHub has the commit, and report the PR link. Merging and publishing then follow Global Constraints → Merging and publishing, which needs the user's go-ahead.
