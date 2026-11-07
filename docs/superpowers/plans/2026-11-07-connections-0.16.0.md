# Connections history, traffic strip and Destinations (0.16.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship 0.16.0, "See it": per-minute Connections history with a grouped TEMP read and the History Unlocated fix, the traffic strip on Connections History and Usage history with **Show in Connections**, and the Destinations table.

**Architecture:** `ConnectionMinuteFolder` (Core, pure) merges each publication into open minute units. `ConnectionService`'s writer freezes them and saves them as ordinary rows through the unchanged `Append`. `ConnectionHistoryStore.Read` builds `temp.geo` and `temp.grouped` once per call under `temp_store=MEMORY` and a 64 MiB TEMP limit. The page, the summary, the markers, the strip (`TrafficStrip`, Core) and the Destinations list (`ConnectionDestinationList`, Core) all read those tables in one snapshot. `UsageHistoryStore.ReadApplicationSeries` gives the Usage strip on `ReadMetric`'s grid. In App, `TrafficStripChart` and `TrafficStripView` draw both strips. `ConnectionsPage` gains the strip card, the exact-application filter, `ShowHistory`, `ReloadHistory` and the Destinations card, and `UiSession.RequestConnectionsHistory` carries Show in Connections.

**Tech Stack:** .NET 10, WPF, Microsoft.Data.Sqlite (existing; bundled SQLite 3.53.4 from `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5), xUnit 2.9.3. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-07-connections-design.md`. The user approved it section by section and then as a written spec on 2026-11-07, choosing to trim the Destinations steps to one shared pass (decision 15, `temp.picked`). It is binding. This plan implements its front matter, Decisions 1–30, Architecture and Release split, its three 0.16.0 sections and its "Errors, testing, docs and release (both releases)" as they apply to 0.16.0. Executors read both documents; where they differ, the spec wins. The spec's anchors are at 181eb54 (0.14.1). **Line numbers are at 75d6d65** (v0.15.0, `main`) throughout this plan.

## Builds on 0.15.0

`main` is 75d6d65: v0.15.0 (per-app GPU, PR #84) has merged, so the spec's "0.15.0 ships first" holds.

**Anchors that 0.15.0 left in place.** These files are byte-identical at 181eb54 and 75d6d65, so the spec's line numbers hold for them:
- Core: `ConnectionHistoryStore.cs`, `ConnectionLedger.cs`, `ConnectionModels.cs`, `ConnectionProjection.cs`, `NetworkAddress.cs`, `UsageHistory.cs`, `UsageHistoryStore.cs` and `ProcessRowOrder.cs`.
- App: `ConnectionService.cs`, `ConnectionsPage.cs`, `ConnectionMap.cs`, `UiSession.cs`, `UsageHistoryService.cs`, `UsageHistoryChart.cs`, `Theme.cs`, `ThemeTokens.cs`, `Charts.cs`, `App.xaml.cs` and `ProcessHistoryService.cs` (`TryReadApplicationsAsync` :156, summary :154; `DefaultFlushInterval` :13), plus `tools/OverviewPreview/Program.cs`.
- Tests: `ConnectionServiceTests.cs`, `ConnectionHistoryStoreTests.cs`, `ConnectionProjectionTests.cs`, `UsageHistoryStoreTests.cs`, `ConnectionsPageUiChecks.cs`, `ConnectionMapUiChecks.cs` and `BatteryUiTests.cs`.

**Anchors 0.15.0 moved**, re-anchored here. Find each by the quoted member or text, not only by its number:
- `Core/ProcessTable.cs`: `ProcessGrouping` :75, `Key` :77 (spec :50), `Group` :78 with its displayed `NormalizeName` at :85 (spec :57).
- `Core/Monitoring.cs`: `UserSettings` :374-414 and `SettingsCodec.Read` :418 (spec :374-413, :417).
- `App/ApplicationResourcesCard.cs`:
  - `ResourceRow` and its `Update` :26-60 (spec :25-50);
  - `SaveDelay` :302 (spec :278);
  - `ShowSlot` :175-183;
  - `ClearSlot` :187-199, which hands focus on at :190-194 (spec :165-177, :168-173);
  - `SlotHeading` :203-208 (spec :181-187) and `CeilingMinute` :210-211;
  - the `syncing` reconcile in `RefreshRows` :350-364 (spec :114, :311-325);
  - `ScrollToTop` :287 (spec :263).
- `App/UsageHistoryPage.cs`:
  - `ApplicationRow` :22, `Moments` :32-33 (CPU, RAM and GPU);
  - `shareBar` :41 and :301-313 (spec :38, :298-310);
  - `marked` :51 (spec :48);
  - the Applications card :89-98, with its note at :97 (spec :86-95, :94);
  - the `SlotCleared` handler :106 (spec :103) and the Esc guard :107-114 (spec :104-111);
  - the range change :125, `Step` :153 and `ShowDay` :155-160 (spec :122, :150-157);
  - `MoveMoment` :175-179 and `MarkMoment` :184-194 (spec :172-176, :181-191);
  - `RefreshAsync` :208-224 and `LoadAsync` :225-250, with `points` at :228 and `resources.Load` at :231 (spec :205-221, :222-247);
  - `RefreshApplications` :283-314, with `ItemsSource` at :295 and the gap notice at :299 (spec :292, :296).
- `App/ProcessesPage.cs`: the row style :145, the pixel scroll unit :147, `Unloaded` :162, `ShowColumns` :228-240, `OnSorting` :258-265, `Show` :415-424, and the `ValueText` style :509-517 (spec :493-500, :258-265, :415-424).
- `App/Windows.cs`: unchanged numbers. `MinWidth=900` is at :53, the `UsageDayRequested` wiring at :78-79, `PageKey` at :84-90, the `IsEnding` guard at :252, `ShowUsageDay` at :249, and the Usage history window at :358-369.
- `docs/user-guide.md`:
  - Connections :129-145 is unchanged: :133 ("Open **Connections** beside Network"), :135 ("Download/upload colors"), :137 (**History**), :143 and :145 ("History normally retains 30 days").
  - Usage history :165-179: Applications :169, Application resources :171, click a moment :173 (as 0.15.0 left it: "the Device **CPU**, **RAM** or **GPU** chart"), and `process-history.db` :177.
  - Features: Connections :36. Build sample :230 (`-Version 0.15.0`).
- `docs/validation.md`: the spec's Unlocated finding (:785) is now :832, in the 0.12.2 connections review. 0.15.0's section is the top section (:3).
- `tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs`: the gap-notice assertion is at :84, the share bar at :92-93 and `CheckChart` at :107-116 (unchanged numbers). `Run` ends with `ApplicationResourcesUiChecks.Run(app)`.
- `BatteryUiTests.cs`: the STA fact is at :38. Its phases include "Connections map (all themes)" :61-62, "Connections page (all themes)" :63-64, "usage history" :84-85, and "dashboard and settings" :233, which calls `ConnectionsPageUiChecks.CheckNavigation(dashboard)` at :244.

**Access between assemblies.** `DeadWare.Core` has no `InternalsVisibleTo`. So Core tests use public members only, and App tests reach Core's two internal measurement members by reflection, as `ProcessHistoryStoreTests` sets `beforeUpgradeCommit`. `DeadWare.App`'s internals are visible to `DeadWare.App.Tests` (`App/ConnectionGeolocation.cs:7`).

## Global Constraints

From the spec, verbatim:

- "null, never zero, for an unknown value; every "—" has a tooltip naming its reason; status text holds fixed copy, counts and error codes, never names or paths; native and storage work runs off the UI thread; colours come from theme resources only."
- "No store changes its format version: `connections.db`, `usage-history.db` and `process-history.db` keep theirs." And: "`connections.db` stays at `user_version` 1 with the same schema; 0.15 and 0.16 open it alike."
- "Stores | No format change; one new setting". That setting is "`ConnectionsDestinationScope` ("All", "Internet" or "Local")".
- "**The per-minute merge, as drafted.** One saved row per app and destination per minute, with today's row identity. Each minute is saved 2 s plus one to two polling intervals after it ends."
- "The grace is a 2 s constant, `CloseGrace`, and is not configurable." And: "The 60 s hold is a constant and is not configurable."
- "**Backward clock steps.** A step of up to 60 s holds the frozen minute until the clock passes it. A larger step retires it."
- "`Read` builds `temp.geo` and `temp.grouped` once per call, with `temp_store=MEMORY` and a 64 MiB TEMP limit. Over that limit it throws `ConnectionHistoryLimitException` and returns no partial view."
- "**Unreadable is "unavailable", never an empty period.** A read that fails returns no partial view". And: "The summary then shows "—" for every count, with the tooltip "History unavailable", never the zeros the read-error view gives today (`Empty`, `src/DeadWare.App/ConnectionsPage.cs:150`). The Destinations count line follows the same rule."
- "**Existing caps and journal limits are unchanged**": `connections.db` 256 MiB and 30 days; `usage-history.db` 256 MiB and 400 days; `process-history.db` 192 MiB and 30 days; `alerts.db` 64 MiB and 400 days. Also: "Each store's 8 MiB journal bound, the transaction budgets, the queue capacities and `MaximumBufferedDeltas` are unchanged too."
- The new limits: "64 MiB of TEMP pages per History read", "1,500 strip slots", "1,000 rows per scope and 3,000 in all".
- "**0.16.0:** the strip and the Destinations table key apps with `ProcessGrouping.Key`, registered on `connections.db` connections as `app_key`." And: "New matching never uses SQLite's ASCII-only `upper()` or `COLLATE NOCASE`".
- "**What 0.16.0 ships without:** the writer hooks, the not-recorded mark and `AppKey.Of`, which arrive in 0.17.0."
- "**No new requirements.** Nothing needs administrator rights beyond per-app capture's existing need. Nothing makes a network request."
- "Page checks run in Windows CI inside the existing STA fact `BatteryUiTests.Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows` … No new STA fact is added."
- "A measurement records and decides nothing; `docs/validation.md` keeps its numbers as measured, with no threshold."
- "**Checks by eye** are done by the user on their PC. They are listed in `docs/validation.md` as pending and never claimed as verified until the user reports them".
- "**CI time.** CI's job limit is 20 minutes (`.github/workflows/build.yml:15`). The 0.16.0 plan's first measurement task times its 256 MiB per-minute fill on the Windows runner … Each records the time in its PR description and in its release's `docs/validation.md` section. If the fills do not fit, that goes back to the user before either the file sizes or the limit changes."
- "**Docs.** Each release updates the user guide, CHANGELOG, its release notes and `docs/validation.md`. README gets at most a one-line mention per release."
- Release headings: "CHANGELOG: `## 0.16.0 — Traffic strip and Destinations table`" and "`docs/validation.md`: `## Traffic strip and Destinations table (0.16.0, <date>)`". The release notes have no heading.
- "Each is one PR from `main`, opened after the previous release has merged, on a feature branch, with CI as the gate (`docs/development.md`)."
- Out of scope for 0.16.0, as the spec's "Out of scope (both releases)" lists, including: hostnames, reverse DNS and port service labels; blocking; cloud lookups; per-app speed limits; rewriting per-poll rows; VACUUM; new tables or indexes in `connections.db`; caching TEMP tables between reads; changing CSV output or `ExportCsv`'s temp store; releasing the store lock during a read or an export; a configurable close grace or hold window; the monotonic `expiry` watermark; a strip in Live mode, on the widget or on the Network page; a Live destinations export; saving the sort.

For this plan:

- Every task's requirements include the spec section it cites.
- **0.17.0 stays out.** These parts of the spec are not built here: the "Writer hooks for 0.17.0" (the filter before the merge, `Forget`, `ConnectionFlushResult`, retiring held in-scope units, and `maintenanceTransactions: 0` on the final write); the Destinations "With 0.17.0" items (the " · not recorded" mark and the clear behaviour); `AppKey.Of`; and every 0.17.0 test. Everything else the spec marks "Defined here for …" ships in 0.16.0. That includes `GroupedWhere`, `ReloadHistory`, `ShowHistory`, `RequestConnectionsHistory`, `app_key`, the "no longer kept" strip rules, the "Not saved yet" target and the bare-name Show in Connections request.
- **Local checks** run on Linux with .NET SDK 10.0.114.
  - Build: `dotnet build DeadWare.slnx -c Release -p:EnableWindowsTargeting=true --nologo`. Expected: 0 errors, and only the warnings `main` had at the branch point (Task 1 Step 0 records the count; 14 at 0.15.0), none in a file the task touched.
  - Count warnings with Step 0's command, `--no-incremental` included, since an incremental build re-reports only the projects it rebuilt.
  - Core tests: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "<filter>"`.
  - **Core baseline:** the full Core run fails exactly the 26 Windows-only tests in `/home/user/DeadWare-processes/.superpowers/sdd/2026-11-05-processes/linux-baseline-failures.txt`. None of them is a Connections or Usage test. This prints nothing:
    `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo 2>&1 | grep -E '^  Failed ' | sed -E 's/ \[[^]]*\]$//' | sort | diff - <(sort /home/user/DeadWare-processes/.superpowers/sdd/2026-11-05-processes/linux-baseline-failures.txt)`
- **Windows-only tests.** App tests (`tests/DeadWare.App.Tests`) and UI checks run only in Windows CI; locally they only need to compile. UI checks run inside the single STA fact, which **stops at its first failing check**. A UI check "passes in CI" when that fact passes, and "fails" when the fact fails at the named check in the named phase.
- **Red checks.**
  - A test that needs a new member is red when the local build fails with the named `error CS…`:
    - CS0103 for a missing static type used only in member access;
    - CS0246 for a missing type in a declaration, a `new`, a type argument or a `typeof`;
    - CS1061 for a missing instance member;
    - CS0117 for a missing static member of an existing type;
    - CS1739 for a missing named argument.
  - A test that compiles without the change is red only in CI (**CI-red first**). Push its `test(...)` commit alone and stop. The controller confirms the named failure (the first check expected to fail, for the STA fact) before the change is pushed. CI's `dotnet test DeadWare.slnx` runs both test projects, so a Core failure in the same commit does not hide the UI fact's.
- **Run (CI)** is the controller's. Implementers push and return; they never use `gh`. The controller watches checks, reads job logs and fetches the `DeadWare-measurements` artifact through the GitHub tools. `build.yml` cancels an in-progress run when a newer push arrives, so push once per step and let the previous run finish first.
- **Workflow.**
  - One branch, `claude/connections-0.16.0`, from `origin/main` (75d6d65, v0.15.0), with one pull request to `main`, opened as a draft with Task 1's push. CI runs on `claude/*` branches only through a pull request.
  - Fetch and inspect `origin` before each task. Keep unrelated local work. Never force-push or rewrite pushed commits.
  - Each task ends with its commits pushed, `git fetch origin && git status -sb` showing nothing ahead or behind, and CI green, as `AGENTS.md` requires.
  - Tasks 4–20 start only after Task 3's gate has passed.
  - Mark the PR ready after Task 20.
- **Merging and publishing.** The user gave the go-ahead on 2026-11-07 when approving this plan ("same as 0.15.0"): when every task has been reviewed, the final review and its fix wave are done and CI is green, the controller updates the validation Results line to the final run, marks the PR ready, merges it with a merge commit, runs `release.yml` with the tag `v0.16.0` on `main`, and verifies the published release (body equals `docs/releases/v0.16.0.md`, ZIP and `.sha256` attached), without asking again.
  1. Updates the `docs/validation.md` Results line with the final run id and SHA, and lets that run finish green.
  2. Merges the PR (merge commit).
  3. Runs `release.yml` for tag `v0.16.0` on `main` (`actions_run_trigger` `run_workflow`, input `tag`).
  4. Checks the published release: its body equals `docs/releases/v0.16.0.md`, and the ZIP and its `.sha256` are attached.
- **Commits** use `test(...)` for tests, `feat(...)` for features, `fix(...)` for fixes and `docs:` for docs.
  - Every commit message ends with two lines: the committing session's own `Co-Authored-By:` line (`Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` for the controller; a subagent on another model uses its own) and `Claude-Session: https://claude.ai/code/session_017AZ6FXcD7T9s63fqUnw9Js`. The `git commit -m` commands below show only the subject; add the two lines as a final `-m` paragraph.
  - The PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`, a blank line, then the same session link.
  - Never commit anything under `.superpowers/`.
- **Real-Windows measurements** are `[GitHubWindowsFact]` facts in classes marked `[Collection("RealWindows")]` under `tests/DeadWare.App.Tests/RealWindows/`. They write through `MeasurementRecorder.Write`, never record an app name, address or path, and always pass unless they throw. A read error inside a measurement is recorded as the exception's type name in that entry's `error` field, and the measurement continues.
- **Copy** is exact. Every user-visible string quoted from the spec is used as written: same characters, "—", "…", "·" and "✕" included.

## Review Focus

These are the five failure modes that the spec implies but that its listed tests do not exercise, most likely first. Each is pinned by a test in its owning task.

1. **The polling interval changes in the middle of a minute** (1 s to 10 s). Expected: still one row per pair for that minute, and one contiguous complete coverage run. The minute closes on the first 10 s publication whose `CoverageFrom` is at least M + 62 s (R1 adapts through `CoverageFrom`). Test: Task 1, `Polling_interval_change_mid_minute_keeps_one_row_and_contiguous_coverage`.
2. **App names with non-ASCII letters or SQL pattern characters in the exact application filter** (`Ärger.exe`, `a_b.exe`, `50%.exe`). Expected: case folds by `ToUpperInvariant`, as `ProcessGrouping.Key` does, never by SQLite's ASCII `upper()`, and nothing ever pattern-matches. Test: Task 7, `Application_filter_folds_non_ascii_case_and_never_pattern_matches`.
3. **A minute read fails while the minute cursor is on and a strip is shown.** Expected:
   - the strip keeps the whole interval;
   - the summary shows "—" counts with the tooltip "History unavailable";
   - the action status gives the type name;
   - the next cursor move reads again.

   Test: Task 11, `CheckMinuteReadFailureKeepsTheStrip`.
4. **Export destinations on an interval over the 64 MiB TEMP limit.** Expected: `ConnectionHistoryLimitException` before any text is written, so the new file stays empty, and the page shows the export limit text. Test: Task 15, `Destinations_csv_over_the_temp_limit_writes_nothing`.
5. **The scope hides the selected Destinations row** (8.8.8.8 selected, then **Show** Local). Expected: the selection, the map highlight and the status line clear, and choosing All again selects nothing. Test: Task 18, `CheckScopeChangeClearsTheSelection`.

## File structure

| File | Change | Responsibility |
|---|---|---|
| `src/DeadWare.Core/ConnectionMinuteFolder.cs` | new | Open minute units, the close rules R1–R6, freezing, sendable units and the clock check. Pure. |
| `src/DeadWare.Core/ConnectionHistoryStore.cs` | modify | `GroupedWhere`; the TEMP read; the limit; the Unlocated fix; `app_key`; the strip; the destination steps; `ExportDestinationsCsv`. |
| `src/DeadWare.Core/ConnectionModels.cs` | modify | `ConnectionHistoryQuery.Application`; `ConnectionHistoryView.Strip` and `.Destinations`; the destination records. |
| `src/DeadWare.Core/TrafficStrip.cs` | new | Strip records and `TrafficStrips` (grid, fullness, rates, targets, text). |
| `src/DeadWare.Core/UsageHistoryStore.cs` | modify | `ReadApplicationSeries`. |
| `src/DeadWare.Core/NetworkAddress.cs` | modify | `Describe`. |
| `src/DeadWare.Core/ConnectionDestinations.cs` | new | The Live list, scopes, caps, `ConnectionDestinationSort` and `DestinationCopy`. |
| `src/DeadWare.Core/ConnectionProjection.cs` | modify | `LocationKey`; `Group` with highlighted keys. |
| `src/DeadWare.Core/Monitoring.cs` | modify | `UserSettings.ConnectionsDestinationScope`, normalized in `SettingsCodec.Read`. |
| `src/DeadWare.App/ConnectionService.cs` | modify | The writer merges into the folder and writes closed minutes; `DefaultFlushInterval`; `ExportDestinationsCsvAsync`. |
| `src/DeadWare.App/UsageHistoryService.cs` | modify | `DefaultFlushInterval`; `TryReadApplicationSeriesAsync`; `TryReadApplicationsAsync`. |
| `src/DeadWare.App/UsageHistoryChart.cs` | modify | Whole-minute `IsFull`. |
| `src/DeadWare.App/TrafficStripChart.cs` | new | `TrafficStripChart` (drawing, hover, keys) and `TrafficStripView` (caption, chart, legend, not-kept line). |
| `src/DeadWare.App/Theme.cs` | modify | `StripBrush` and `StripColor`. |
| `src/DeadWare.App/ConnectionsPage.cs` | modify | Body rows 0–6; the strip card and its reads; the cursor; the application filter; `ShowHistory`; `ReloadHistory`; the limit text; "—" counts; wiring the Destinations card. |
| `src/DeadWare.App/DestinationsCard.cs` | new | The Destinations card: grid, columns, `DestinationRow`, scope, count line, notes, empty states, sorting, holds, copy. |
| `src/DeadWare.App/ConnectionMap.cs` | modify | `Highlight`, `HighlightedKeys`, the ring and the automation name. |
| `src/DeadWare.App/UsageHistoryPage.cs` | modify | The strip in place of the share bar; the slot listing; one cursor; Show in Connections. |
| `src/DeadWare.App/ApplicationResourcesCard.cs` | modify | `SlotSpan`. |
| `src/DeadWare.App/UiSession.cs` | modify | `ConnectionsHistoryRequest` and its event; `SetConnectionsDestinationScope`. |
| `src/DeadWare.App/Windows.cs` | modify | `MainWindow` handles `ConnectionsHistoryRequested`. |
| `src/DeadWare.App/Charts.cs` | modify | `Ui.ValueText`, moved from `ProcessesPage`. |
| `src/DeadWare.App/ProcessesPage.cs` | modify | Uses `Ui.ValueText` in place of its own `ValueText` style. |
| `tools/OverviewPreview/Program.cs` | modify | Checks the Destinations rows of the Connections preview and saves an image of the card. |
| `tests/DeadWare.Tests/…` | new / modify | `ConnectionMinuteFolderTests`, `TrafficStripTests`, `ConnectionDestinationsTests` (new); `ConnectionHistoryStoreTests`, `UsageHistoryStoreTests`, `NetworkAddressTests`, `ConnectionProjectionTests`, `ConnectionSettingsTests`. |
| `tests/DeadWare.App.Tests/…` | new / modify | `TrafficStripUiChecks` (new); `RealWindows/ConnectionHistoryMeasurementTests` and `RealWindows/UsageApplicationSeriesMeasurementTests` (new); `ConnectionServiceTests`, `ConnectionsPageUiChecks`, `UsageHistoryUiChecks`, `ConnectionMapUiChecks`, `BatteryUiTests`. |

---

## 0.16.0 — Connections storage

### Task 1: `ConnectionMinuteFolder` (Core)

Spec: *Connections storage* → What a row is, The open minute, When a minute closes, Writing closed minutes (payload, clock check), and its Tests list.

**Files:**
- Create: `src/DeadWare.Core/ConnectionMinuteFolder.cs`
- Test: `tests/DeadWare.Tests/ConnectionMinuteFolderTests.cs`

**Interfaces:**
- Consumes: `ConnectionSample`, `ConnectionDelta`, `ConnectionHistoryRecord`, `ConnectionCoverage`, `ConnectionLocation` and `GeoPoint` (`ConnectionModels.cs`).
- Produces:
  - `public delegate bool ConnectionLocationLookup(string address, out ConnectionLocation? location);`. A hit with a null location is a cached null. The service passes `locations.TryGetValue`.
  - `public sealed record ConnectionMinuteUnit(string BatchId, string Segment, long Generation, DateTimeOffset Minute, IReadOnlyList<ConnectionHistoryRecord> Records, IReadOnlyList<ConnectionCoverage> Coverage, long Observations) { public int Size => Records.Count + Coverage.Count; }`
  - `public sealed record ConnectionSendable(IReadOnlyList<ConnectionMinuteUnit> Send, IReadOnlyList<ConnectionMinuteUnit> Retired);`
  - `public sealed class ConnectionMinuteFolder(ConnectionLocationLookup lookup, int pressureThreshold)`. `pressureThreshold` below 1 throws `ArgumentOutOfRangeException`. Its members:
    - `public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2), StallLimit = TimeSpan.FromSeconds(90), ClockHold = TimeSpan.FromSeconds(60), FutureGuard = TimeSpan.FromSeconds(2);`
    - `public const int MaximumRecords = 32768, MaximumCoverage = 32768;`
    - `public static string BatchId(string segment, DateTimeOffset minute, long generation, long serial)` returns `$"{segment}:m{minute.UtcTicks}:{generation}:{serial}"`, formatted with the invariant culture.
    - `public int Held { get; }`: open entries plus open coverage runs.
    - `public int Waiting { get; }`: the sum of `Size` over frozen units not yet sent or retired.
    - `public int Merge(ConnectionSample sample, long generation, GeoPoint? origin, bool complete, IReadOnlyList<ConnectionDelta> accepted)` returns the number of units R1, R2 and R5 froze in this step.
    - `public int Tick(DateTimeOffset now, long currentGeneration)` applies R3, then R4.
    - `public int FreezeAll()` applies R6.
    - `public ConnectionSendable Sendable(DateTimeOffset now, bool final, int maximumRecords = MaximumRecords, int maximumCoverage = MaximumCoverage)`
    - `public void Sent(IReadOnlyList<ConnectionMinuteUnit> units)`
    - `public IReadOnlyList<ConnectionMinuteUnit> DropWaiting()`

The plan's decisions, where the spec leaves a choice:
- **Merge.** `Merge` merges `accepted` (the deltas that passed the service's three checks) and the publication's coverage pieces, computed as `ConnectionService.cs:291-300` computes them today, with `complete` as each piece's flag. Then, in this order:
  - R1: freeze each open unit of `sample.Segment` and `generation` with `Minute + 1 min + CloseGrace ≤ sample.CoverageFrom`.
  - R2: freeze every open unit whose Segment or generation differs. R2 applies to every publication, including one with no deltas and no coverage.
  - R5: when `Held ≥ pressureThreshold`, freeze every open unit.
- **Keys.** Units are keyed by (Segment, generation, Minute). Entries are keyed by (Pid, process Generation, Name, RemoteAddress, RemotePort, Protocol, Origin). Strings compare ordinally, and Origin compares by record equality.
- **Freezing.** A unit takes the folder's next serial (0, 1, 2, …) when it freezes. Waiting units are kept in (Minute, serial) order.
- **Location lookups.** At every merge an entry's last known location is updated from `lookup` when it hits, a hit with null included. At freeze, `lookup` hits first, then the last known location, then null.
- **`Sendable`** walks the waiting units in order. For each unit, `excess` = (the latest record `LastSeen` or coverage `To`) − (now + `FutureGuard`).
  - `excess ≤ 0`: the unit is sendable.
  - `0 < excess ≤ ClockHold` and not final: the unit stays waiting, unchanged, and the walk goes on.
  - Otherwise: the unit is retired. It is removed from waiting and listed in `Retired`.
  - Sendable units are taken whole while the totals stay within both limits. The walk stops at the first sendable unit that does not fit, and the first sendable unit is always taken (with the real limits one unit always fits; spec, Payload).
  - `Sent` removes the listed units from waiting. `DropWaiting` removes and returns every waiting unit.

- [ ] **Step 0: Fetch, check that 0.15.0 is on main, create the branch and record the warning count**

```bash
git fetch origin
git status --short --branch                                               # unrelated local work stays where it is
git show origin/main:Directory.Build.props | grep -c '<Version>0.15.0</Version>'      # 1; if 0, stop: 0.15.0 has not merged
git ls-remote --heads origin claude/connections-0.16.0                   # empty before the first push; otherwise continue that branch without force-pushing
git switch -c claude/connections-0.16.0 origin/main
git checkout claude/determined-keller-uggb29 -- docs/superpowers/specs/2026-11-07-connections-design.md
# The plan: check it out the same way if it is committed on claude/determined-keller-uggb29; otherwise copy it from that worktree.
git add docs/superpowers/specs/2026-11-07-connections-design.md docs/superpowers/plans/2026-11-07-connections-0.16.0.md
git commit -m "docs: Connections spec and the 0.16.0 plan"
dotnet build DeadWare.slnx -c Release -p:EnableWindowsTargeting=true --nologo --no-incremental 2>&1 | grep -E 'warning [A-Za-z]+[0-9]+' | sed -E 's/^ +//' | sort -u | wc -l   # record in the PR body (14 at 0.15.0)
```

- [ ] **Step 1: Write the failing tests**

```csharp
// M0 = 2026-11-07 10:00:00Z. Pub(t, from, deltas, available=true, segment="s") is a ConnectionSample at M0+t s (Live empty, CoverageFrom M0+from s or null).
// D(t, received=10, localPort=100, remote="8.8.8.8", port=443, name="app.exe", pid=10) is a delta at M0+t s in its UTC minute (FirstSeen=LastSeen).
// Cache is a Dictionary<string,ConnectionLocation?>; F(threshold=16384) => new ConnectionMinuteFolder(Cache.TryGetValue, threshold).
// Merge(f, p, generation=1, complete=true, origin=null) => f.Merge(p, generation, origin, complete, p.Deltas). Units(f) => f.Sendable(M0.AddHours(1), false).Send.
[Fact] public void One_row_per_pair_per_minute_with_saturating_totals(){ var f=F();
    Merge(f,Pub(1,null,[D(0.5)]));for(int t=2;t<=60;t++)Merge(f,Pub(t,t-1,[D(t-0.5),..(t==30?[D(29.7,localPort:200)]:[])]));f.FreezeAll();
    var u=Assert.Single(Units(f));var r=Assert.Single(u.Records);
    Assert.Equal((610L,M0.AddSeconds(0.5),M0.AddSeconds(59.5),true),(r.Delta.ReceivedBytes,r.Delta.FirstSeen,r.Delta.LastSeen,r.Complete));Assert.Equal(61,u.Observations);
    var c=Assert.Single(u.Coverage);Assert.Equal((M0.AddSeconds(1),M0.AddMinutes(1),true),(c.From,c.To,c.Complete));
    var big=F();Merge(big,Pub(10,9,[D(9.2,received:long.MaxValue/2+1),D(9.4,received:long.MaxValue/2+1)]));big.FreezeAll();Assert.Equal(long.MaxValue,Units(big)[0].Records[0].Delta.ReceivedBytes);}
[Fact] public void Completeness_is_the_and_of_contributing_publications(){ var f=F();for(int t=1;t<=10;t++)Merge(f,Pub(t,t-1,[D(t-0.5)]),complete:t!=5);f.FreezeAll();var u=Units(f)[0];
    Assert.False(u.Records[0].Complete);Assert.Equal([(0d,4d,true),(4d,5d,false),(5d,10d,true)],u.Coverage.Select(c=>((c.From-M0).TotalSeconds,(c.To-M0).TotalSeconds,c.Complete)));}
[Fact] public void Minute_closes_two_seconds_and_one_poll_after_it_ends(){
    var open=F();Merge(open,Pub(30,29,[D(29.5)]));Assert.Equal(0,Merge(open,Pub(62.9,61.9,[])));Assert.Equal(0,open.Waiting);
    var closed=F();Merge(closed,Pub(30,29,[D(29.5)]));Assert.Equal(1,Merge(closed,Pub(63,62,[])));Assert.True(closed.Waiting>0);
    var slow=F();Merge(slow,Pub(50,40,[D(45)]));Merge(slow,Pub(60,50,[D(55)]));Assert.Equal(0,Merge(slow,Pub(70,60,[D(59.5)])));
    Assert.Equal(1,Merge(slow,Pub(80,70,[D(59.8)])));var r=Units(slow)[0].Records[0];Assert.Equal((40L,M0.AddSeconds(59.8)),(r.Delta.ReceivedBytes,r.Delta.LastSeen));   // the late delta is in
    var none=F();Merge(none,Pub(30,29,[D(29.5)]));Assert.Equal(0,Merge(none,Pub(65,null,[])));Assert.Equal(0,none.Waiting);}
[Fact] public void Late_delta_becomes_a_new_unit_with_a_new_batch_id(){ var f=F();Merge(f,Pub(30,29,[D(29.5)]));Merge(f,Pub(63,62,[]));
    Assert.Equal(1,Merge(f,Pub(64,63,[D(59.9)])));var minute0=Units(f).Where(u=>u.Minute==M0).ToArray();Assert.Equal(2,minute0.Length);Assert.NotEqual(minute0[0].BatchId,minute0[1].BatchId);
    var stalled=F();Merge(stalled,Pub(30,29,[D(29.5)]));Assert.Equal(1,stalled.Tick(M0.AddSeconds(90),1));
    Assert.Equal(0,Merge(stalled,Pub(40,30,[D(35)])));Assert.Equal(0,Merge(stalled,Pub(61,40,[])));Assert.Equal(1,Merge(stalled,Pub(70,62.5,[])));
    // Both minute-0 units appended to a real ConnectionHistoryStore (temp file) read back as one row of 20 bytes.
    Assert.Equal(20,AppendAndRead(Units(stalled)).Rows.Single(r=>r.Minute==M0).ReceivedBytes);}
[Fact] public void Segment_or_generation_change_closes_old_units(){ var f=F();Merge(f,Pub(30,29,[D(29.5)],segment:"a"));
    Assert.Equal(1,Merge(f,Pub(31,null,[],available:false,segment:"b")));Assert.Equal(0,f.Held);int waiting=f.Waiting;
    foreach(var s in new[]{"c","d","e"})Assert.Equal(0,Merge(f,Pub(32,null,[],available:false,segment:s)));Assert.Equal((0,waiting),(f.Held,f.Waiting));
    var g=F();Merge(g,Pub(40,39,[D(39.5)]),generation:1);Assert.Equal(1,Merge(g,Pub(41,40,[D(40.5)]),generation:2));Assert.True(g.Held>0);}
[Fact] public void Tick_closes_old_generation_and_stalled_minutes(){ var f=F();Merge(f,Pub(30,29,[D(29.5)]),generation:1);Assert.Equal(1,f.Tick(M0.AddSeconds(31),2));
    var s=F();Merge(s,Pub(30,29,[D(29.5)]));Assert.Equal(0,s.Tick(M0.AddSeconds(89),1));Assert.Equal(1,s.Tick(M0.AddSeconds(90),1));}
[Fact] public void Pressure_counts_open_entries_and_runs_only(){ var f=F(4);Merge(f,Pub(10,null,[D(9.1,remote:"1.1.1.1"),D(9.2,remote:"1.1.1.2"),D(9.3,remote:"1.1.1.3")]));f.FreezeAll();
    Assert.Equal(0,Merge(f,Pub(20,null,[D(19.1,remote:"2.2.2.1"),D(19.2,remote:"2.2.2.2"),D(19.3,remote:"2.2.2.3")])));Assert.Equal(3,f.Held);
    Assert.Equal(1,Merge(f,Pub(21,null,[D(20.1,remote:"2.2.2.4")])));Assert.Equal(0,f.Held);}
[Fact] public void Sendable_units_stay_within_append_limits(){ // minute 0: 2 records + 1 run; minute 1: 2 records + 1 run; minute 2: 1 record; each frozen by FreezeAll after its minute.
    var first=f.Sendable(M0.AddHours(1),false,3,3);Assert.Equal([M0],first.Send.Select(u=>u.Minute));f.Sent(first.Send);
    var rest=f.Sendable(M0.AddHours(1),false,3,3);Assert.Equal([M0.AddMinutes(1),M0.AddMinutes(2)],rest.Send.Select(u=>u.Minute));Assert.Empty(rest.Retired);}
[Fact] public void Origin_change_keeps_two_entries_in_one_minute(){ var f=F();Merge(f,Pub(10,9,[D(9.5)]),origin:new(1,2));Merge(f,Pub(11,10,[D(10.5)]),origin:new(3,4));f.FreezeAll();
    Assert.Equal([new GeoPoint(1,2),new GeoPoint(3,4)],Units(f).SelectMany(u=>u.Records).Select(r=>r.Origin).OrderBy(o=>o!.Latitude));}
[Fact] public void Location_is_the_cache_value_at_freeze_else_the_last_known(){ // L1 = a located ConnectionLocation
    var hit=F();Merge(hit,Pub(10,9,[D(9.5)]));Cache["8.8.8.8"]=L1;hit.FreezeAll();Assert.Equal(L1,Units(hit)[0].Records[0].Location);Cache.Clear();
    var nul=F();Cache["8.8.8.8"]=L1;Merge(nul,Pub(10,9,[D(9.5)]));Cache["8.8.8.8"]=null;nul.FreezeAll();Assert.Null(Units(nul)[0].Records[0].Location);Cache.Clear();
    var miss=F();Cache["8.8.8.8"]=L1;Merge(miss,Pub(10,9,[D(9.5)]));Cache.Clear();miss.FreezeAll();Assert.Equal(L1,Units(miss)[0].Records[0].Location);}
[Fact] public void Frozen_unit_never_changes(){ var f=F();Merge(f,Pub(30,29,[D(29.5)]));Merge(f,Pub(63,62,[]));var u=Units(f)[0];var (records,coverage,obs)=(u.Records.ToArray(),u.Coverage.ToArray(),u.Observations);
    Merge(f,Pub(64,63,[D(59.9),D(63.5)]));Merge(f,Pub(126,125,[D(125.5)]));var again=Units(f).Single(x=>x.BatchId==u.BatchId);
    Assert.Same(u,again);Assert.Equal(records,again.Records);Assert.Equal(coverage,again.Coverage);Assert.Equal(obs,again.Observations);}
[Fact] public void Batch_ids_are_unique_and_fit_the_store_bound(){
    var last=new DateTimeOffset(DateTimeOffset.MaxValue.UtcTicks-DateTimeOffset.MaxValue.UtcTicks%TimeSpan.TicksPerMinute,TimeSpan.Zero);
    string id=ConnectionMinuteFolder.BatchId(new string('s',128),last,long.MaxValue,long.MaxValue);Assert.True(id.Length<=192,$"{id.Length} characters");
    // A record and a coverage run under that id, segment 128 characters, at a current minute: ConnectionHistoryStore.Append does not throw.
    var f=F();for(int i=0;i<10_000;i++){Merge(f,Pub(i*60+10,i*60+9,[D(i*60+9.5)]));f.FreezeAll();}
    var ids=f.Sendable(M0.AddDays(8),false).Send.Select(u=>u.BatchId).ToArray();Assert.Equal(10_000,ids.Distinct().Count());
    Assert.DoesNotContain(ids,x=>System.Text.RegularExpressions.Regex.IsMatch(x,@"^s:\d+:\d+$"));}
[Fact] public void FreezeAll_and_Held_count_entries_and_runs(){ var f=F();Merge(f,Pub(10,9,[D(9.5),D(9.6,remote:"9.9.9.9")]));Merge(f,Pub(11,10,[]),complete:false);
    Assert.Equal(4,f.Held);Assert.Equal(1,f.FreezeAll());Assert.Equal((0,4),(f.Held,f.Waiting));}
[Fact] public void Polling_interval_change_mid_minute_keeps_one_row_and_contiguous_coverage(){   // Review Focus 1
    var f=F();Merge(f,Pub(1,null,[D(0.5)]));for(int t=2;t<=30;t++)Merge(f,Pub(t,t-1,[D(t-0.5)]));
    foreach(int t in new[]{40,50,60,70})Assert.Equal(0,Merge(f,Pub(t,t-10,[D(Math.Min(t-0.5,59.5))])));
    Assert.Equal(1,Merge(f,Pub(80,70,[])));var u=Units(f).Single(x=>x.Minute==M0);Assert.Single(u.Records);
    var c=Assert.Single(u.Coverage);Assert.Equal((M0.AddSeconds(1),M0.AddMinutes(1),true),(c.From,c.To,c.Complete));}
```

`AppendAndRead(units)` initializes a `ConnectionHistoryStore` on a temp file, calls `Append([..units.SelectMany(u=>u.Records)], [..units.SelectMany(u=>u.Coverage)], M0.AddMinutes(10))` and returns `Read(new(M0, M0.AddMinutes(5)))`.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ConnectionMinuteFolderTests"`. Expected: the build fails with `error CS0246` for `ConnectionMinuteFolder` (the `new` in `F`; also `ConnectionMinuteUnit` where a helper declares it) and `error CS0103` for `ConnectionMinuteFolder.BatchId`.
- [ ] **Step 3: Implement `ConnectionMinuteFolder.cs`** with the signatures and rules above. Byte sums saturate as the store's `Add` does (`ConnectionHistoryStore.cs:128`).
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit, push and open the draft PR**

```bash
git add tests/DeadWare.Tests/ConnectionMinuteFolderTests.cs
git commit -m "test(core): per-minute connection units, close rules and batch ids"
git add src/DeadWare.Core/ConnectionMinuteFolder.cs
git commit -m "feat(core): merge connection deltas into per-minute units"
git push -u origin HEAD
```

Open the draft PR through the GitHub tools: base `main`, head `claude/connections-0.16.0`, title "Connections history, traffic strip and Destinations (0.16.0)". The body names the spec and this plan and records Step 0's warning count. It says that Task 3 records the CI fill time, and it ends with the attribution in Global Constraints. Run (CI). Expected: green.

---

### Task 2: The grouped TEMP read, its limit and the Unlocated fix (Core)

Spec: *Connections storage* → The grouped read (steps 1–8, Limit, Equivalence) and The Unlocated fix.

**Files:**
- Modify: `src/DeadWare.Core/ConnectionHistoryStore.cs`:
  - `RowOrder` (:23) is removed;
  - `Grouped` (:24-35) loses its WHERE body to `GroupedWhere`;
  - the constructor (:59-63);
  - `Read` (:334-386).
- Test: `tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 `ConnectionMinuteFolder.BatchId` (test fixtures only).
- Produces:
  - `public const int MaximumGroupedTempPages = 16384;`
  - `public ConnectionHistoryStore(string path, long maximumDatabaseBytes = MaximumDatabaseBytes, int maximumGroupedTempPages = MaximumGroupedTempPages)`. A page count outside 16–16,384 throws `ArgumentOutOfRangeException(nameof(maximumGroupedTempPages))`.
  - `public sealed class ConnectionHistoryLimitException(Exception inner) : IOException("Connection history interval holds too many saved rows to group.", inner)`, in `ConnectionHistoryStore.cs`.
  - `private const string GroupedWhere`: the WHERE body of :29-32. `Grouped`, the geo insert and the grouped insert all use it. `Grouped` stays for `ExportCsv`.
  - `internal bool defaultTempStore;`: a measurement-only switch that Task 3 sets by reflection. While it is true, `Read` skips `PRAGMA temp_store=MEMORY` and still sets `temp.max_page_count`.
  - A private `T Grouping<T>(Func<T> statement)` that turns a `SqliteException` with `SqliteErrorCode == 13` into `ConnectionHistoryLimitException`. Every statement of steps 2–6 runs through it, and so does every later statement over the TEMP tables (Tasks 7 and 15).

`Read` follows the spec's steps 1–8 exactly:
- the pragmas right after `Open` and before `BeginTransaction(deferred:true)`;
- `temp.geo` with explicit ids in BINARY (location, origin) order;
- `temp.grouped` with `row_index = row_number() OVER (ORDER BY minute DESC,pid,generation,name,remote,port,protocol,geo)-1`;
- the page from `temp.grouped JOIN temp.geo`;
- one summary statement, with Unlocated `count(DISTINCT CASE WHEN kind=0 AND x.located=0 THEN remote END)` and Local `kind=1`;
- markers over `temp.grouped` grouped by `geo` where `located=1`;
- coverage, the earliest retained time, the floor and the status unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
// Store(cap, pages=16384) => new ConnectionHistoryStore(Database, cap, maximumGroupedTempPages: pages), initialized. Existing helpers: Row, Query, Connect, Sql, Epoch, Place.
// Traffic(db) => SELECT count(*) FROM traffic. Poll/Minute fixtures: three pairs (8.8.8.8 with Place; 192.168.1.5 local; 9.9.9.9 unlocated), 10 bytes a second.
[Fact] public void Per_minute_rows_read_the_same_as_per_poll_rows(){
    // A: 60 per-poll batches "s:{i}:1", each one record per pair with FirstSeen=LastSeen=Epoch+i s and coverage [i, i+1) s.
    // B: one unit under ConnectionMinuteFolder.BatchId("s",Epoch,1,0): one summed record per pair (FirstSeen Epoch, LastSeen Epoch+59 s) and coverage [0, 60) s.
    var a=A.Read(Query(2));var b=B.Read(Query(2));Assert.Equal(a.Rows,b.Rows);Assert.Equal(a.TotalRows,b.TotalRows);Assert.Equal(a.Summary,b.Summary);Assert.Equal(a.Markers,b.Markers);
    Assert.Equal(a.Coverage,b.Coverage);Assert.Equal(a.Gaps,b.Gaps);Assert.Equal(a.EarliestRetainedTime,b.EarliestRetainedTime);Assert.Equal(Csv(A),Csv(B));
    Assert.Equal((180L,3L),(Traffic(A),Traffic(B)));}
[Fact] public void Grouped_read_matches_the_three_scan_reference(){
    // Fixture: per-poll and per-minute rows; 192.168.3.5, 100.64.0.1, 239.255.255.250, 9.9.9.9 (no location), 1.0.0.1 (country, no point); two origins at one location;
    // 300 located groups (more than 256 markers); then raise floor and expiry with Sql(db,"UPDATE metadata …"). Reference(store, query) runs the 0.15 statements copied
    // verbatim from 75d6d65 (Grouped :24-35, RowOrder :23, page :342, summary :345 with kind=0 added inside Unlocated's CASE, markers :351-369) in one deferred transaction
    // on a Pooling=false read-only connection with sat_sum registered.
    foreach(var q in new[]{Query(600),Query(600) with{Search="app2"},Query(600) with{Search="4242"},Query(600) with{CountryCode="US"},
        Query(600) with{Direction=ConnectionDirection.Received},Query(600) with{Direction=ConnectionDirection.Sent}})
    foreach(int offset in new[]{0,200,(int)Reference(store,q).TotalRows+5}){var mine=store.Read(q with{Offset=offset});var theirs=Reference(store,q with{Offset=offset});
        Assert.Equal(theirs.Rows,mine.Rows);Assert.Equal(theirs.TotalRows,mine.TotalRows);Assert.Equal(theirs.Summary,mine.Summary);Assert.Equal(theirs.Markers,mine.Markers);Assert.Equal(theirs.OmittedMarkers,mine.OmittedMarkers);}}
[Fact] public void Grouped_read_writes_nothing_to_the_database(){ // main and -wal bytes and lengths, -shm length and StorageBytes are equal before and after Read,
    // and again while a second Pooling=false connection holds an open read transaction (a pinned reader).
}
[Fact] public void Grouped_read_over_the_temp_limit_fails_without_a_partial_view(){ var store=Store(pages:16);
    store.Append([..Enumerable.Range(0,2000).Select(i=>Row(i%9,$"b{i}",address:$"20.{i/250}.{i%250}.1"))],[],Epoch.AddMinutes(10));store.Append([Row(9,"last")],[],Epoch.AddMinutes(10));
    var e=Assert.Throws<ConnectionHistoryLimitException>(()=>store.Read(Query(10)));Assert.Equal("Connection history interval holds too many saved rows to group.",e.Message);
    Assert.Single(store.Read(new(Epoch.AddMinutes(9),Epoch.AddMinutes(10))).Rows);
    Assert.Throws<ArgumentOutOfRangeException>(()=>new ConnectionHistoryStore(Database,maximumGroupedTempPages:15));Assert.Throws<ArgumentOutOfRangeException>(()=>new ConnectionHistoryStore(Database,maximumGroupedTempPages:16385));}
[Fact] public void Unlocated_counts_public_peers_without_a_point_only(){ // 192.168.3.5 and 9.9.9.9 with no location, 1.0.0.1 with a country and no point,
    // 100.64.0.1, 239.255.255.250, and 8.8.8.8 with Place, all in minute 0
    var s=store.Read(Query()).Summary;Assert.Equal((2L,1L,6L),(s.Unlocated,s.Local,s.Destinations));}
// Markers_are_bounded_while_full_summaries_include_local_unlocated_and_every_country (:183-190): Assert.Equal(1,view.Summary.Unlocated) at :188 instead of 2.
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ConnectionHistoryStoreTests"`. Expected: the build fails with `error CS1739` (`maximumGroupedTempPages`) and `error CS0246` (`ConnectionHistoryLimitException`).
- [ ] **Step 3: Implement the pragmas, `GroupedWhere`, the two TEMP tables, the page, summary and marker statements, the limit and the constructor argument**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output. Every existing `ConnectionHistoryStoreTests` test passes unchanged except :188.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs
git commit -m "test(history): grouped TEMP read, its limit and the unlocated rule"
git add src/DeadWare.Core/ConnectionHistoryStore.cs
git commit -m "feat(history): group connection history once per read in TEMP tables"
```

Run (CI). Expected: green.

---

### Task 3: CI measurement: per-minute rates, the 256 MiB fill and Read timings (gate)

Spec: *Connections storage* → Tests → Windows CI measurement; section 7 → New CI measurements and CI time.

**Files:**
- Create: `tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs`

**Interfaces:**
- Consumes: Task 1 `ConnectionMinuteFolder.BatchId`; Task 2's constructor, `Read` and the `defaultTempStore` field (set by reflection); `MeasurementRecorder.Write`; `GitHubWindowsFactAttribute`; `ProcessMeasurementTests.Percentile`.
- Produces: `[Collection("RealWindows")] public sealed class ConnectionHistoryMeasurementTests` with `[GitHubWindowsFact] public void Connection_history_rates_and_read_timings()`, which writes `connection-history.json`. Tasks 7 and 15 add fields to the same file.

The fields:
- `rates`: `{ idle, onePair, twentyPairs, twentyPairsChurn }`. Each is MiB per simulated hour: `StorageBytes` after the last `Append`, divided by 12 hours.
- `fill`: `{ seconds, hours, trafficRows, fileBytes, evicted }`.
- `reads`: eight entries `{ range, tempStore, ms, heapHighwaterBytes, totalRows, error }`. `range` is "31d", "7d", "1d" or "1min", and `tempStore` is "memory" or "default".

The loads (the folder's output, written directly; one segment `Guid.NewGuid().ToString("N")` per store; BatchIds `ConnectionMinuteFolder.BatchId(segment, minute, 1, serial++)`):
- **idle:** one complete coverage run [M, M + 60 s) per minute, and no records.
- **onePair:** idle, plus one record per minute: PID 100, "app0.exe", 20.0.0.1:443 Tcp, 60,000 bytes received and 6,000 sent, FirstSeen M + 0.5 s, LastSeen M + 59.5 s, a located DB-IP-shaped `ConnectionLocation`, Origin (41.88, −87.63), Complete.
- **twentyPairs:** 20 such pairs (PIDs 100–119, `app{i}.exe`, 20.0.0.1–20.0.0.20, 20 distinct locations).
- **twentyPairsChurn:** twentyPairs, plus a replacement step every simulated second. With probability 0.05 (`new Random(20)`), one pair chosen uniformly is replaced by a new process instance (the next PID) to a new remote (the next address). The replaced pair and its replacement each save a row for that minute.

The steps:
1. **Rates.** Write each load for 12 simulated hours into its own store, starting at `floor(UtcNow) − 12 h`. Use one `Append` per simulated minute, with `now` = the minute's end + 3 s.
2. **Fill.** Start a fresh store at `floor(UtcNow) − 10 days` and write the twentyPairs load with one `Append` per simulated hour (`now` = the hour's end + 3 s). Stop when the main file is at least `MaximumDatabaseBytes − 4 MiB` and `EarliestRetainedTime` is later than the start, or after 240 hours. Record the fill's `Stopwatch` seconds and hours, `SELECT count(*) FROM traffic`, the main file length, and whether eviction happened.
3. **Reads.** On the filled file, with E = the last simulated minute's end, read [E − 31 d, E), [E − 7 d, E), [E − 1 d, E) and [E − 1 min, E), first with `defaultTempStore` false and then true.
   - Do one warm-up 1-day read first and do not record it.
   - Before each read, call `SQLitePCL.raw.sqlite3_memory_highwater(1)`. Time the read with a `Stopwatch`, then record `sqlite3_memory_highwater(0)` and `TotalRows`.

- [ ] **Step 1: Write the measurement**
- [ ] **Step 2: Build.** Expected: 0 errors. A measurement has no red step.
- [ ] **Step 3: Commit, push and run in CI**

```bash
git add tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs
git commit -m "test(history): measure per-minute rates, the full-file fill and grouped read timings"
```

Run (CI). Expected: green, and the run's `DeadWare-measurements` artifact holds `connection-history.json`.
- [ ] **Step 4: Record the numbers and check the gate (controller, GitHub tools)**

Add to the PR description: the run link; `fill.seconds` and `fill.hours`; the build job's total duration against its 20-minute limit; the four rates; and the eight reads.

**Gate.** Stop before Task 4 and report to the user, with these numbers, when either holds: the build job was cancelled at its 20-minute limit, or this measurement threw. Do not shrink the file, the loads or the read set, and do not raise the job limit. The spec sends both choices to the user.

---

### Task 4: The writer saves closed minutes (App)

Spec: *Connections storage* → The open minute (what is merged), When a minute closes, Writing closed minutes, Crash, storage failure, pause, settings, Quit and sign-out.

**Files:**
- Modify: `src/DeadWare.App/ConnectionService.cs`:
  - add `public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(15);`;
  - `WriteLoopAsync` (:231-322): `Process` (:277-303) merges into the folder in place of :290; `Flush` (:237-276) becomes the write attempt; the per-poll batch ID (:280) and the `attempted` set (:235, :255-262) go; the timer (:304) uses `options.FlushInterval ?? DefaultFlushInterval`.
- Test: `tests/DeadWare.App.Tests/ConnectionServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's folder (all members).
- Produces: `ConnectionService.DefaultFlushInterval` (Task 11 uses it in the Connections SaveDelay). Nothing else public changes: `Live`, `StorageStatus` and every status string stay as they are.

The writer's steps. All folder calls run under `gate`.
- **Setup.** `WriteLoopAsync` creates `new ConnectionMinuteFolder(locations.TryGetValue, Math.Max(1, options.MaximumBufferedDeltas / 2))`.
- **Process.** After `EnrichAsync`, compute `complete` and the accepted deltas exactly as :281-289 do. Then call `folder.Merge(sample, item.Generation, item.Origin, complete, accepted)`, and make a write attempt when it froze any unit.
- **Tick.** Call `folder.Tick(clock(), generation)`. Then make a write attempt that calls `Append` even with nothing to send, so expiry and Cleanup run as today.
- **A write attempt** takes `folder.Sendable(clock(), final)`. Each retired unit adds `max(1, Observations)` to the dropped count (`DropLocked`).
  - Not final: one `Append` of the sendable units. On success, call `Sent`. On failure, set today's unavailable status and leave the units waiting unchanged.
  - Final: repeat `Sendable` and `Append` until nothing is left. On failure, drop `max(1, Σ Observations)` over the unsent units and `DropWaiting()`.
- **Stop.** After the loop ends (the queue is complete and drained), call `folder.FreezeAll()`, then make the final attempt. A final attempt with nothing to send makes no `Append`, as today.
- **Accounting.** The invariant is `buffered` = the queued deltas + `folder.Held` + `folder.Waiting`. After every folder call, adjust `buffered` by the change in `Held + Waiting`. A merge also subtracts `sample.Deltas.Count` and decrements `bufferedPublications`. Admission (:144) is unchanged. `BufferedDeltas` is 0 after `StopAsync`.

- [ ] **Step 1: Write the new tests and change the eight**

```csharp
// New. The clock follows the readings unless stated. Reading(seq, seconds, bytes=10, complete=true, coverage=null, segment="segment") gains the segment parameter.
// Calls records each append's (records, coverage); WaitFor(n) waits up to 15 s for n appends with records.
[Fact] public async Task Open_minute_is_written_once_when_it_closes_and_live_is_unchanged(){ // readings at Epoch+1 … +63 s; +1 has no CoverageFrom, the rest cover from the previous
    for(int s=1;s<=62;s++){service.TryRecord(Reading(s,s,coverage:s==1?null:Epoch.AddSeconds(s-1)));Assert.Equal(10,Assert.Single(service.Live.Connections).Traffic.ReceivedBytes);}
    await Task.Delay(200);Assert.DoesNotContain(calls,c=>c.Records.Count>0);
    service.TryRecord(Reading(63,63,coverage:Epoch.AddSeconds(62)));var append=await WaitFor(1);var r=Assert.Single(append.Records);
    Assert.Equal((Epoch,590L),(r.Delta.Minute,r.Delta.ReceivedBytes));var c=Assert.Single(append.Coverage.Where(x=>x.From<Epoch.AddMinutes(1)));Assert.Equal((Epoch.AddSeconds(1),Epoch.AddMinutes(1),true),(c.From,c.To,c.Complete));}
[Fact] public async Task Stop_writes_the_open_minute(){ /* readings +1 … +30, StopAsync: one append, a 300-byte record for minute 0 and coverage [+1 s, +30 s) */ }
[Fact] public async Task Session_ending_stop_writes_the_open_minute(){ /* as Session_normal_quit_keeps_final_subscription_… (:229) builds its session; readings +1 … +30;
    StopCollectorAsync by reflection with 3 s, 1.5 s and 3 s: the store holds minute 0 with 300 bytes */ }
[Fact] public async Task Pause_writes_the_open_minute_at_the_next_tick_and_resume_uses_a_new_batch(){ // FlushInterval 20 ms; readings +1 … +10, Configure(false,…):
    // within 1 s one append holds minute 0 (100 bytes); Configure(true,…), readings +11 … +20, StopAsync: a second minute-0 record under a different BatchId with the second generation.
}
[Fact] public async Task Failed_write_retries_identical_units_and_new_data_uses_new_batches(){ // the append throws once on the R1 write; the next attempt sends the same BatchIds
    // with equal records and coverage; readings after the failure go to units with BatchIds not in the first set.
}
[Fact] public async Task Pressure_freezes_open_units_at_half_the_delta_budget(){ // MaximumBufferedDeltas 8: four distinct peers in one reading freeze the open unit (Held reaches 4)
    // and write it without waiting for R1 or a tick.
}
[Theory][InlineData(false)][InlineData(true)]
public async Task Small_clock_rollback_holds_a_frozen_minute_until_the_clock_passes_it(bool stopAt95){ // a reading at +100 s covering from +99 s (minute 1), then the clock steps back
    // to +50 s with a reading on segment "new": minute 1 is frozen by R2, held, DroppedObservations 0. Clock +95 s, FlushInterval 20 ms: R4 writes minute 0 while minute 1 is held.
    // !stopAt95: clock +100 s, minute 1 is written unchanged (its original record). stopAt95: StopAsync at +95 s retires it, DroppedObservations 1.
}
// Changed (spec: App, changed):
//   Pause_resume_acceptance_preserves_prior_writes_and_records_a_gap_with_saved_origin (:30): saved bytes [10, 70]; the 30 and 40 merge in generation 2.
//   Normal_flush_records_unknown_when_lookup_is_stuck (:90) and Failed_storage_retries_keep_the_original_payload_when_lookup_later_changes (:99): clock at Epoch + 100 s; R4 writes.
//   Local_database_drains_final_data_and_history_reads_do_not_replace_live (:133): built with the internal clock constructor at Epoch + 100 s.
//   Later_database_and_origin_updates_do_not_rewrite_saved_geography (:208): clock at Epoch + 100 s before the first wait; the second reading is a late unit with the
//     Replacement location and origin (3, 4), written by R4.
//   Slow_enrichment_cannot_starve_a_due_flush_behind_all_queued_publications (:280): clock at Epoch + 100 s; the first appended record holds 10–40 bytes.
//   Clock_rollback_retires_unflushed_future_records_and_coverage_without_blocking_fresh_storage (:338): the spec's steps and assertions exactly (Reading(2,30,30,false,Epoch+29 s) on
//     "new"; Dropped 1; then clock Epoch + 95 s with FlushInterval 20 ms, or StopAsync with stopImmediately; one 30-byte row at Epoch, FirstSeen = LastSeen = Epoch + 30 s,
//     incomplete; a gap over [+99 s, +100 s]; no complete coverage over [+29 s, +30 s]; BufferedDeltas 0; both status strings as today).
//   Clock_rollback_discards_whole_attempted_batch_without_changing_saved_receipts_or_retrying_its_safe_subset (:359): the spec's steps and assertions exactly
//     (45 bytes; minute-1 rows of 10 and 5; replaying original adds nothing; original keeps LastSeen +100 s and +70 s and Complete; a gap over +30 s to +31 s; BufferedDeltas 0).
// Every other test in the file passes unchanged.
```

- [ ] **Step 2: Build, commit the tests alone, push and verify failure (CI-red first)**

```bash
git add tests/DeadWare.App.Tests/ConnectionServiceTests.cs
git commit -m "test(connections): the writer saves closed minutes"
```

Run (CI). Expected: the build compiles. The test run fails with at least `Open_minute_is_written_once_when_it_closes_and_live_is_unchanged` (no append until R1) and both `Small_clock_rollback_holds_a_frozen_minute_until_the_clock_passes_it` cases (today's code retires the batch, so `DroppedObservations` is 1). Stop for the controller.
- [ ] **Step 3: Implement the writer**
- [ ] **Step 4: Commit, push and verify pass**

```bash
git add src/DeadWare.App/ConnectionService.cs
git commit -m "feat(connections): save one row per app and destination per minute"
```

Run (CI). Expected: green, with every `ConnectionServiceTests` test passing, and `connection-history.json` still recorded.

---

### Task 5: The page shows the limit text (App)

Spec: *Connections storage* → The grouped read → Limit; Tests → Page check.

**Files:**
- Modify: `src/DeadWare.App/ConnectionsPage.cs`, `PumpHistoryAsync` (:217-230)
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs` (new `CheckHistoryLimitError`, called from `RunCore` right after `CheckHistoryQueries`)

**Interfaces:**
- Consumes: Task 2 `ConnectionHistoryLimitException`.
- Produces: a `catch (ConnectionHistoryLimitException)` before the general catch at :226. It sets `actionStatus.Text = "This interval holds too many saved rows to show. Choose a shorter interval."`, applies `Empty(query, "History unavailable; no saved observations shown.")` and calls `StopPlayback()`. Task 11 adds the strip's and the summary's unavailable states on top of it.

- [ ] **Step 1: Write the check**

```csharp
private static void CheckHistoryLimitError(UiSession session){ var page=new ConnectionsPage(session);   // as CheckIntervalWarning (:240) does
    PageType.GetField("readHistory",All)!.SetValue(page,(Func<ConnectionHistoryQuery,Task<ConnectionHistoryView>>)(_=>Task.FromException<ConnectionHistoryView>(new ConnectionHistoryLimitException(new IOException()))));
    Field<ComboBox>(page,"mode").SelectedIndex=1;Pump();
    Assert.Equal("This interval holds too many saved rows to show. Choose a shorter interval.",Field<TextBlock>(page,"actionStatus").Text);
    Assert.Contains("History unavailable; no saved observations shown.",Field<TextBlock>(page,"capture").Text);Assert.Empty(Field<ListBox>(page,"mappedConnections").Items);}
```

- [ ] **Step 2: Build, commit, push and verify failure (CI-red first)**

```bash
git add tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs
git commit -m "test(connections): history limit text"
```

Run (CI). Expected: the UI fact fails in phase "Connections page (all themes)" at `CheckHistoryLimitError`: the action status reads "History could not be read (ConnectionHistoryLimitException).".
- [ ] **Step 3: Add the catch**
- [ ] **Step 4: Commit, push and verify pass**

```bash
git add src/DeadWare.App/ConnectionsPage.cs
git commit -m "feat(connections): say when an interval holds too many rows to show"
```

Run (CI). Expected: green.

---

## 0.16.0 — The traffic strip

### Task 6: The strip's Core model (`TrafficStrip.cs`)

Spec: *The traffic strip* → What the strip plots, Slot states (the `IsFull` rule), Core model, Show in Connections (target); its Core `TrafficStripTests`.

**Files:**
- Create: `src/DeadWare.Core/TrafficStrip.cs`
- Test: `tests/DeadWare.Tests/TrafficStripTests.cs`

**Interfaces:**
- Consumes: `ProcessGrouping.Key` (`ProcessTable.cs:77`, unchanged).
- Produces: the spec's records verbatim:
  - `public enum TrafficMeasure { Total, Received, Sent }`
  - `public sealed record TrafficStripApplication(string Name, string Key, long Received, long Sent);`
  - `public sealed record TrafficStripSlot(DateTimeOffset Start, DateTimeOffset End, long Minutes, long RecordedSeconds, bool Incomplete, IReadOnlyList<long> Received, IReadOnlyList<long> Sent);`
  - `public sealed record TrafficStrip(DateTimeOffset From, DateTimeOffset To, long BucketMilliseconds, TrafficMeasure Measure, IReadOnlyList<TrafficStripApplication> Applications, long OtherReceived, long OtherSent, long OtherApplications, IReadOnlyList<TrafficStripSlot> Slots, DateTimeOffset? RetainedFrom = null, bool CapacityShortened = false);`
- Also `public static class TrafficStrips`:
  - `public const int MaximumApplications = 6, MaximumSlots = 1500, ConnectionsRetentionDays = 30;`
  - `public static DateTimeOffset CeilingMinute(DateTimeOffset time)`: the first whole UTC minute at or after `time`. A plan helper; the strip, `ShowHistory`, `ConnectionsTarget` and the Usage heading share it.
  - `public static long ConnectionsBucketMinutes(DateTimeOffset from, DateTimeOffset to)`: the first of 1, 2, 5, 10, 15 or 30 that gives at most 1,500 slots, otherwise ⌈minutes ÷ 1,500⌉, where minutes = ⌈(to − CeilingMinute(from)) ÷ 1 min⌉.
  - `public static int ConnectionsSlotCount(DateTimeOffset from, DateTimeOffset to)` and `public static (DateTimeOffset Start, DateTimeOffset End) ConnectionsSlot(DateTimeOffset from, DateTimeOffset to, long index)`. Slot k is [origin + k·b, min(origin + (k + 1)·b, to)), where origin = `CeilingMinute(from)`. The count is 0 when origin ≥ to. Plan helpers; Task 7 builds the slots with them.
  - `public static long MinuteStarts(DateTimeOffset start, DateTimeOffset end)` = (CeilingMinute(end) − CeilingMinute(start)) ÷ 1 min.
  - `public static bool IsFull(TrafficStripSlot slot)`: not `Incomplete`, and `RecordedSeconds ≥ 60 × max(1, n − 0.5)` with n = `MinuteStarts(Start, End)`.
  - `public static DateTimeOffset DueUntil(DateTimeOffset to, DateTimeOffset now, TimeSpan saveDelay)` = min(to, now − saveDelay).
  - `public static long Bytes(TrafficStripSlot slot, int series, TrafficMeasure measure)`: the last index is Other, and Total saturates at `long.MaxValue`.
  - `public static double? Rate(TrafficStripSlot slot, int series, TrafficMeasure measure)`: null when `RecordedSeconds` is 0.
  - `public static int? SlotAt(TrafficStrip strip, DateTimeOffset time)`
  - `public static string SlotLengthText(long bucketMilliseconds)`: under one hour, minutes `0.#` + "-minute"; otherwise hours `0.#` + "-hour"; in the current culture.
  - `public static (DateTimeOffset From, DateTimeOffset To)? ConnectionsTarget(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)`: [max(CeilingMinute(from), CeilingMinute(now − 30 d)), CeilingMinute(to)). It is null when that is empty. The empty case includes a span in which no whole minute starts (a plan decision; the button is then disabled).

- [ ] **Step 1: Write the failing tests**

```csharp
// T(h,m,s=0) = 2026-11-07 h:m:s Z. S(start,end,seconds,incomplete=false,received=null,sent=null) builds a TrafficStripSlot (Minutes = MinuteStarts; lists default to [0]).
[Fact] public void Connections_bucket_minutes_keep_at_most_1500_whole_minute_slots(){ var a=T(0,0);
    foreach(var (span,b,count) in new[]{(TimeSpan.FromMinutes(5),1L,5),(TimeSpan.FromDays(1),1L,1440),(TimeSpan.FromHours(25),1L,1500),(TimeSpan.FromDays(2),2L,1440),
        (TimeSpan.FromDays(7),10L,1008),(TimeSpan.FromDays(30),30L,1440),(TimeSpan.FromDays(31),30L,1488),(TimeSpan.FromDays(40),39L,1477)})
    {Assert.Equal(b,TrafficStrips.ConnectionsBucketMinutes(a,a+span));Assert.Equal(count,TrafficStrips.ConnectionsSlotCount(a,a+span));}
    var from=T(10,0,20);var to=T(12,0,30);Assert.Equal(T(10,1),TrafficStrips.ConnectionsSlot(from,to,0).Start);
    Assert.Equal(to,TrafficStrips.ConnectionsSlot(from,to,TrafficStrips.ConnectionsSlotCount(from,to)-1).End);}
[Fact] public void Is_full_counts_the_whole_minutes_that_start_in_the_slot(){
    Assert.True(TrafficStrips.IsFull(S(T(10,0),T(10,1),60)));Assert.False(TrafficStrips.IsFull(S(T(10,0),T(10,1),59)));
    Assert.True(TrafficStrips.IsFull(S(T(10,0),T(10,10),570)));Assert.False(TrafficStrips.IsFull(S(T(10,0),T(10,10),569)));
    var six=(T(10,0,10),T(10,0,10).AddMilliseconds(403_200));Assert.Equal(6,TrafficStrips.MinuteStarts(six.Item1,six.Item2));
    Assert.True(TrafficStrips.IsFull(S(six.Item1,six.Item2,360)));Assert.False(TrafficStrips.IsFull(S(six.Item1,six.Item2,300)));
    var seven=(T(10,0),T(10,0).AddMilliseconds(403_200));Assert.Equal(7,TrafficStrips.MinuteStarts(seven.Item1,seven.Item2));
    Assert.True(TrafficStrips.IsFull(S(seven.Item1,seven.Item2,420)));Assert.False(TrafficStrips.IsFull(S(seven.Item1,seven.Item2,360)));
    Assert.False(TrafficStrips.IsFull(S(T(10,0),T(10,1),60,incomplete:true)));}
[Fact] public void Due_until_keeps_unsaved_minutes_out_of_the_gaps(){ var delay=TimeSpan.FromSeconds(16);
    Assert.True(TrafficStrips.DueUntil(T(11,0),T(10,0,10),delay)<T(10,0));Assert.Equal(T(10,0),TrafficStrips.DueUntil(T(11,0),T(10,0,16),delay));Assert.Equal(T(11,0),TrafficStrips.DueUntil(T(11,0),T(12,0),delay));}
[Fact] public void Rate_divides_measure_bytes_by_recorded_seconds_and_is_null_without_recorded_time(){ var s=S(T(10,0),T(10,1),10,received:[100,0,50],sent:[20,30,0]);
    Assert.Equal(12,TrafficStrips.Rate(s,0,TrafficMeasure.Total));Assert.Equal(10,TrafficStrips.Rate(s,0,TrafficMeasure.Received));Assert.Equal(2,TrafficStrips.Rate(s,0,TrafficMeasure.Sent));
    Assert.Equal(5,TrafficStrips.Rate(s,2,TrafficMeasure.Total));Assert.Null(TrafficStrips.Rate(s with{RecordedSeconds=0},0,TrafficMeasure.Total));
    Assert.Equal(long.MaxValue,TrafficStrips.Bytes(S(T(10,0),T(10,1),60,received:[long.MaxValue],sent:[1]),0,TrafficMeasure.Total));}
[Fact] public void Slot_at_finds_the_listed_slot_holding_a_time_and_null_in_gaps(){ // slots [10:00,10:01) and [10:02,10:03)
    Assert.Equal(0,TrafficStrips.SlotAt(strip,T(10,0,30)));Assert.Null(TrafficStrips.SlotAt(strip,T(10,1,30)));Assert.Equal(1,TrafficStrips.SlotAt(strip,T(10,2)));Assert.Null(TrafficStrips.SlotAt(strip,T(10,3)));}
[Fact] public void Connections_target_uses_whole_minutes_and_clips_to_thirty_days(){ var now=T(12,0,30);
    Assert.Equal((T(11,1),T(11,5)),TrafficStrips.ConnectionsTarget(T(11,0,20),T(11,5),now));
    Assert.Equal((TrafficStrips.CeilingMinute(now.AddDays(-30)),TrafficStrips.CeilingMinute(now.AddDays(-29))),TrafficStrips.ConnectionsTarget(now.AddDays(-31),now.AddDays(-29),now));
    Assert.Null(TrafficStrips.ConnectionsTarget(now.AddDays(-40),now.AddDays(-31),now));
    Assert.Equal((T(10,1),T(10,3)),TrafficStrips.ConnectionsTarget(T(10,0,30),T(10,2,54),now));Assert.Equal((T(10,0),T(10,3)),TrafficStrips.ConnectionsTarget(T(10,0),T(10,2,24),now));
    Assert.Null(TrafficStrips.ConnectionsTarget(T(10,0,10),T(10,0,50),now));}
[Fact] public void Slot_length_text_uses_minutes_under_an_hour(){ // CultureInfo.CurrentCulture = InvariantCulture in try/finally
    Assert.Equal(["1-minute","3.2-minute","30-minute","1.6-hour"],new long[]{60_000,192_000,1_800_000,5_760_000}.Select(TrafficStrips.SlotLengthText));}
[Fact] public void Application_key_normalizes_then_upper_cases_and_is_idempotent(){ var longName=new string('a',300);
    Assert.Equal(ProcessGrouping.Key(" chrome.exe"),ProcessGrouping.Key("Chrome.EXE"));Assert.Equal(ProcessGrouping.Key("Chrome.EXE"),ProcessGrouping.Key("chr\u0001ome.exe"));
    Assert.Equal("UNKNOWN APPLICATION",ProcessGrouping.Key(""));Assert.Equal("UNKNOWN APPLICATION",ProcessGrouping.Key(null!));Assert.Equal(new string('A',256),ProcessGrouping.Key(longName));
    foreach(var x in new[]{" chrome.exe","Chrome.EXE","chr\u0001ome.exe","",longName})Assert.Equal(ProcessGrouping.Key(x),ProcessGrouping.Key(ProcessGrouping.Key(x)));}
```

The last test pins `ProcessGrouping.Key`'s existing behaviour, which the strip relies on. It passes before and after the change.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~TrafficStripTests"`. Expected: the build fails with `error CS0103` for `TrafficStrips` and `error CS0246` for `TrafficStripSlot`.
- [ ] **Step 3: Implement `TrafficStrip.cs`**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/TrafficStripTests.cs
git commit -m "test(core): traffic strip grid, fullness, rates and targets"
git add src/DeadWare.Core/TrafficStrip.cs
git commit -m "feat(core): traffic strip model"
```

Run (CI). Expected: green.

---

### Task 7: The Connections strip, the exact-application filter and `app_key` (Core)

Spec: *The traffic strip* → Connections: data; *The Destinations table* → History store step 0 (`app_key`, registered here because the filter needs it first).

**Files:**
- Modify: `src/DeadWare.Core/ConnectionModels.cs`: `ConnectionHistoryQuery` (:29-31) and `ConnectionHistoryView` (:45-49).
- Modify: `src/DeadWare.Core/ConnectionHistoryStore.cs`:
  - `Open` (:474) registers `app_key`;
  - `Normalize` (:395-399) and `Arguments` (:400);
  - `GroupedWhere` gains the application predicate;
  - `Read` fills `Strip` and narrows the status condition (:380-381).
- Modify: `tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs` (the strip's timings).
- Test: `tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 2's TEMP tables and `Grouping`; Task 6's model and helpers.
- Produces:
  - `ConnectionHistoryQuery(…, int Offset = 0, int Limit = 200, string Application = "")`. `Normalize` rejects null or more than 260 characters with the store's `Invalid()`. It trims the value, keeps "" (every application), and stores anything else as `ProcessGrouping.Key(value)`.
  - `ConnectionHistoryView(…, long OmittedMarkers = 0, TrafficStrip? Strip = null)`. `Read` passes `Strip:` by name.
  - In `Open`: `db.CreateFunction<string, string>("app_key", ProcessGrouping.Key, isDeterministic: true)`, beside `sat_sum`.
  - `GroupedWhere` adds `AND ($application='' OR app_key(name)=$application)`, and `Arguments` adds `$application`.
  - `Read` builds the strip from `temp.grouped` and `coverage`, after step 6, through `Grouping`. It follows the spec's steps 1–8:
    - the grid comes from `ConnectionsBucketMinutes`, `ConnectionsSlot` and `ConnectionsSlotCount`;
    - totals: `SELECT name, sat_sum(received), sat_sum(sent) FROM temp.grouped GROUP BY name`, folded by key in C# with saturation; the measure comes from `Direction`; ties by key, ordinal; a zero measure never ranks;
    - slot bytes: `SELECT (minute-$origin)/$bucket, name, sat_sum(received), sat_sum(sent), min(complete) FROM temp.grouped GROUP BY 1, name`;
    - `floor` and `expiry` are read once;
    - retained = max(CeilingMinute(expiry), floor + 1 min);
    - each minute's covered time comes from the coverage pieces (`stamp > floor`, `stamp ≥ retained`, `finish > From`, `start < To`), clipped to [From, To), summed per `stamp` and capped at 60 s;
    - a minute with rows and no coverage counts as 60 s and incomplete;
    - `Minutes`, `RecordedSeconds` (whole seconds, rounded down) and `Incomplete`; slots with `Minutes` 0 are left out;
    - `RetainedFrom`, and `CapacityShortened` = floor > 0 && floor + 1 min > CeilingMinute(expiry).
  - The status prefix "Capacity shortened retention. " uses `CapacityShortened` in place of `floor>0`. Its copy is unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
// A(name, received, sent, minute=0) => Row(minute, $"b-{name}-{minute}", received, sent, name:name). Strip(q) => store.Read(q).Strip!.
[Fact] public void Strip_ranks_six_applications_by_the_direction_measure_and_folds_the_rest_into_other(){
    store.Append([A("b.exe",70,11),A("a.exe",80,1),A("c.exe",60,3),A("d.exe",50,4),A("e.exe",40,5),A("f.exe",30,6),A("g.exe",20,7),A("h.exe",10,0),A("z.exe",0,0)],[],Epoch.AddMinutes(2));
    var both=Strip(Query());Assert.Equal(["a.exe","b.exe","c.exe","d.exe","e.exe","f.exe"],both.Applications.Select(a=>a.Name));Assert.Equal((30L,7L,2L),(both.OtherReceived,both.OtherSent,both.OtherApplications));
    Assert.Equal(["a.exe","b.exe","c.exe","d.exe","e.exe","f.exe"],Strip(Query() with{Direction=ConnectionDirection.Received}).Applications.Select(a=>a.Name));
    var sent=Strip(Query() with{Direction=ConnectionDirection.Sent});Assert.Equal(["b.exe","g.exe","f.exe","e.exe","d.exe","c.exe"],sent.Applications.Select(a=>a.Name));
    Assert.Equal((80L,1L,1L),(sent.OtherReceived,sent.OtherSent,sent.OtherApplications));
    foreach(var d in Enum.GetValues<ConnectionDirection>()){var v=store.Read(Query() with{Direction=d});
        Assert.Equal(v.Summary.ReceivedBytes,v.Strip!.Applications.Sum(a=>a.Received)+v.Strip.OtherReceived);Assert.Equal(v.Summary.SentBytes,v.Strip.Applications.Sum(a=>a.Sent)+v.Strip.OtherSent);}}
[Fact] public void Strip_groups_names_by_application_key_and_shows_the_ordinal_smallest_name(){ store.Append([A("chrome.exe",10,0),A("Chrome.exe",20,0),A("PID 4",5,0)],[],Epoch.AddMinutes(2));
    Assert.Equal([("Chrome.exe","CHROME.EXE",30L),("PID 4","PID 4",5L)],Strip(Query()).Applications.Select(a=>(a.Name,a.Key,a.Received)));}
[Fact] public void Strip_slots_match_a_reference_fold_of_every_read_row(){ // Random(7): three days, 9 names on 4 remotes in 2 countries, coverage on most minutes.
    // For no filter, Search, CountryCode, Direction Received and Sent, and Application: read every page (Limit 500, Offset 0, 500, …) and fold the rows in C#
    // into slot (minute - origin) / b and series (the strip's key, else Other). Every slot's Received and Sent lists equal the fold.
}
[Fact] public void Strip_recorded_seconds_come_from_coverage_and_rows_without_coverage_are_incomplete(){
    // Minute k after Epoch: 0 [0,30) s; 1 [0,60); 2 [0,40) and [20,60) in two batches; 3 [0,60) incomplete; 4 traffic and no coverage; 5 [0,60) and no traffic;
    // 6 [0,0.7) s; 7 nothing. Minutes 0–4 carry "a.exe" traffic.
    var s=Strip(Query(8));Assert.Equal([0,1,2,3,4,5,6],s.Slots.Select(x=>(int)(x.Start-Epoch).TotalMinutes));
    Assert.Equal([30L,60L,60L,60L,60L,60L,0L],s.Slots.Select(x=>x.RecordedSeconds));Assert.Equal([false,false,false,true,true,false,false],s.Slots.Select(x=>x.Incomplete));
    Assert.Equal(0,TrafficStrips.Bytes(s.Slots[5],s.Applications.Count,TrafficMeasure.Total)+TrafficStrips.Bytes(s.Slots[5],0,TrafficMeasure.Total));
    var other=Strip(Query(8) with{Application="other.exe"});Assert.DoesNotContain(other.Slots,x=>x.Start==Epoch.AddMinutes(4));Assert.Contains(other.Slots,x=>x.Start==Epoch.AddMinutes(5));}
[Fact] public void Strip_reports_retained_from_after_expiry_and_capacity_eviction(){ // metadata set with Sql(Connect(),"UPDATE metadata …"); E = Epoch.AddHours(5)
    // expiry E, floor 0: [E−1h,E+1h) → RetainedFrom E, CapacityShortened false.   floor = E+10 min (stamp), expiry E: RetainedFrom E+11 min, CapacityShortened true.
    // floor = E−10 min, expiry E: RetainedFrom E, false.   [E+1h,E+2h): RetainedFrom null.   [E−3h,E−2h): RetainedFrom ≥ To and Slots empty.
}
[Fact] public void Strip_leaves_out_the_minute_that_holds_the_expiry_instant(){ // coverage [10:00,10:01) and traffic at 10:00 (minute M), interval [M−10 min, M+10 min)
    // expiry M+30 s: no slot at M, RetainedFrom M+1 min. expiry exactly M: one full listed slot at M (TrafficStrips.IsFull) and RetainedFrom M.
}
[Fact] public void Capacity_shortened_status_matches_the_strip(){ // floor newer than expiry: Assert.Contains("Capacity shortened retention.",view.Status);
    // floor older than expiry: Assert.DoesNotContain("Capacity shortened",view.Status).
}
[Fact] public void Application_filter_matches_the_key_exactly_everywhere(){ // rows chrome.exe, Chrome.exe, chrome and other.exe, with locations
    // "chrome": only the "chrome" rows. "CHROME.EXE": chrome.exe and Chrome.exe, in Rows, TotalRows, Summary, Markers, Strip (one application, OtherApplications 0) and every ExportCsv line.
    // "" and "   ": every row. new string('a',261): Assert.Throws<ArgumentException>.
    // The schema v1 file of History_pid_search_matches_existing_schema_v1_rows_without_rewriting_them (:147) keeps its bytes after a Read with Application set.
}
[Fact] public void Strip_is_the_same_for_per_sample_and_merged_minute_rows(){ // Task 2's two stores: A (60 per-poll batches) and B (one per-minute unit) of the same traffic
    Assert.Equal(storeA.Read(Query(2)).Strip,storeB.Read(Query(2)).Strip,StripComparer);}
[Fact] public void Application_filter_folds_non_ascii_case_and_never_pattern_matches(){   // Review Focus 2
    store.Append([A("ärger.exe",1,0),A("ÄRGER.EXE",2,0),A("a_b.exe",4,0),A("aXb.exe",8,0),A("50%.exe",16,0),A("500.exe",32,0)],[],Epoch.AddMinutes(2));
    long R(string app)=>store.Read(Query() with{Application=app}).Summary.ReceivedBytes;Assert.Equal((3L,4L,16L),(R("Ärger.exe"),R("a_b.exe"),R("50%.exe")));}
```

`StripComparer` compares the records field by field, with lists by sequence (records compare lists by reference). Then, in `ConnectionHistoryMeasurementTests`, after the reads, the measurement adds `strip30d: { allApplications: { p50Ms, p95Ms }, busiestApplication: { p50Ms, p95Ms } }`. These are 5 runs each of the 30-day read [E − 30 d, E), with temp_store MEMORY. The busiest application is `Strip.Applications[0].Key` from the first run, and its name is never recorded.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ConnectionHistoryStoreTests"`. Expected: the build fails with `error CS0117` (`Application` in `with`) and `error CS1061` (`Strip`).
- [ ] **Step 3: Implement the field, the predicate, `app_key`, the strip and the status condition**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output. The only existing test that pins "Capacity shortened", `Impossible_capacity_write_reports_failure_and_exposes_intentional_old_retention_eviction` (:217-223), passes unchanged: its floor (Epoch − 1 min) + 1 min is later than CeilingMinute(expiry).
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs
git commit -m "test(history): connections strip, exact application filter and the narrower capacity status"
git add src/DeadWare.Core/ConnectionModels.cs src/DeadWare.Core/ConnectionHistoryStore.cs
git commit -m "feat(history): traffic strip and exact application filter in the grouped read"
```

Run (CI). Expected: green, and `connection-history.json` holds `strip30d`. The controller adds the strip timings to the PR description.

---

### Task 8: The Usage history application series

Spec: *The traffic strip* → Usage history: data; Core model → Save delays (Usage); Tests → `UsageHistoryStoreTests` and `Usage_application_series_timing_for_a_month`.

**Files:**
- Modify: `src/DeadWare.Core/UsageHistoryStore.cs`: `ReadApplicationSeries`, after `ReadApplications` (:191-216).
- Modify: `src/DeadWare.App/UsageHistoryService.cs`:
  - `public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(15);`, which replaces the literal in the constructor (:28);
  - two wrappers after `ReadApplicationsAsync` (:167-179).
- Create: `tests/DeadWare.App.Tests/RealWindows/UsageApplicationSeriesMeasurementTests.cs`
- Test: `tests/DeadWare.Tests/UsageHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 6's model.
- Produces:
  - `public TrafficStrip ReadApplicationSeries(string machine, DateTimeOffset from, DateTimeOffset to, int maxPoints = 1500)`: `Measure` Total, no `RetainedFrom`, one read-only connection and one deferred transaction.
    - It validates as `ReadMetric` does (:158-161), and `maxPoints` outside 1–4,000 throws `ArgumentOutOfRangeException`.
    - It uses `ReadMetric`'s bucket.
    - Ranking: the two `ReadApplications` statements with limit 6.
    - Minutes: a minute is recorded when `metric_minutes` or `application_minutes` holds it, and captured when it is recorded and not in `capture_gaps`. `RecordedSeconds` = 60 × captured minutes, and `Incomplete` = captured < recorded.
    - Slot bytes sum only captured minutes. Group natively by slot and name, and subtract the few capture-gap minutes' rows read separately.
    - On Usage history, `TrafficStripApplication.Key` is the stored name: nothing on that page uses keys.
  - `UsageHistoryService.DefaultFlushInterval`.
  - `public async Task<TrafficStrip?> TryReadApplicationSeriesAsync(DateTimeOffset from, DateTimeOffset to, int maxPoints)` and `public async Task<UsageApplicationView?> TryReadApplicationsAsync(DateTimeOffset from, DateTimeOffset to)`. Both are written like `ReadApplicationsAsync`. On any failure other than an `ArgumentException` they call `Fail` and return null.

- [ ] **Step 1: Write the failing tests and the measurement**

```csharp
[Theory][InlineData(1,60)][InlineData(1,450)][InlineData(1,1500)][InlineData(7,60)][InlineData(7,450)][InlineData(7,1500)][InlineData(30,60)][InlineData(30,450)][InlineData(30,1500)][InlineData(30,4000)]
public void Application_series_uses_the_metric_bucket_grid(int days,int points){ // a CPU minute and an a.exe row every 7 minutes over the period
    var s=store.ReadApplicationSeries("m",Day,Day.AddDays(days),points);var cpu=store.ReadMetric("m",UsageMetrics.Cpu,null,Day,Day.AddDays(days),points);
    Assert.Equal(cpu.BucketMilliseconds,s.BucketMilliseconds);Assert.All(cpu.Buckets,b=>Assert.Contains(s.Slots,x=>x.Start==b.Start&&x.End==b.End));}
[Fact] public void Application_series_ranks_like_the_applications_read(){ // nine names, two pairs tied on received + sent
    var s=store.ReadApplicationSeries("m",Day,Day.AddDays(1));var a=store.ReadApplications("m",Day,Day.AddDays(1));
    Assert.Equal(a.Applications.Take(6).Select(x=>(x.Name,x.Received,x.Sent)),s.Applications.Select(x=>(x.Name,x.Received,x.Sent)));
    Assert.Equal((a.Applications.Skip(6).Sum(x=>x.Received)+a.OtherReceived,a.Applications.Skip(6).Sum(x=>x.Sent)+a.OtherSent,a.Applications.Count-6+a.OtherApplications),(s.OtherReceived,s.OtherSent,s.OtherApplications));}
[Fact] public void Application_series_keeps_capture_gap_minutes_out_of_slot_bytes(){ // 5-minute slots (points chosen so); slot 0: 4 captured minutes and 1 gap minute, all with a.exe 100 B;
    // slot 1: only gap minutes. Totals: a.exe 500 + slot 1's bytes; slot 0 bytes 400, Incomplete, RecordedSeconds 240; slot 1 listed with RecordedSeconds 0.
}
[Fact] public void Application_series_lists_recorded_minutes_without_traffic_as_zero_slots(){ // metric rows only in slot 0 → listed with zero bytes; slot 1 unrecorded → absent
}
[Fact] public void Application_series_rejects_bad_arguments(){ Assert.Throws<ArgumentOutOfRangeException>(()=>store.ReadApplicationSeries("m",Day,Day.AddDays(1),0));
    Assert.Throws<ArgumentOutOfRangeException>(()=>store.ReadApplicationSeries("m",Day,Day.AddDays(1),4001));Assert.ThrowsAny<ArgumentException>(()=>store.ReadApplicationSeries("m",Day,Day.AddDays(-1)));
    Assert.ThrowsAny<ArgumentException>(()=>store.ReadApplicationSeries("m",Day,Day.AddDays(402)));Assert.ThrowsAny<ArgumentException>(()=>store.ReadApplicationSeries("",Day,Day.AddDays(1)));}
```

`[Collection("RealWindows")] public sealed class UsageApplicationSeriesMeasurementTests` holds `[GitHubWindowsFact] public void Usage_application_series_timing_for_a_month()`. It fills a `UsageHistoryStore` with 43,200 minutes ending at the current minute, each with a CPU metric row and 15 application rows (`app0.exe`–`app14.exe`), in batches within `MaximumBatchRows`. Then it writes `usage-application-series.json`: `{ minutes, applicationsPerMinute, fileBytes, points450: { p50Ms, p95Ms }, points1500: { p50Ms, p95Ms } }`, from 5 runs each after one warm-up.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~UsageHistoryStoreTests"`. Expected: the build fails with `error CS1061` for `ReadApplicationSeries`.
- [ ] **Step 3: Implement the read, the constant and the two wrappers**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/UsageHistoryStoreTests.cs tests/DeadWare.App.Tests/RealWindows/UsageApplicationSeriesMeasurementTests.cs
git commit -m "test(usage): application series on the metric grid and its month timing"
git add src/DeadWare.Core/UsageHistoryStore.cs src/DeadWare.App/UsageHistoryService.cs
git commit -m "feat(usage): read per-application traffic per slot for the strip"
```

Run (CI). Expected: green, and the artifact holds `usage-application-series.json`. The controller adds its numbers to the PR description.

---

### Task 9: Whole-minute fullness on the Usage line charts

Spec: *The traffic strip* → Usage history line charts: whole-minute fullness (decision 20); Tests → `CheckFractionalBucketsConnect` and the unchanged `CheckChart`.

**Files:**
- Modify: `src/DeadWare.App/UsageHistoryChart.cs:28-30` (`IsFull` and its comment)
- Test: `tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs` (new `CheckFractionalBucketsConnect(DateTimeOffset now)`, called in `Run`'s theme loop right after `CheckChart`)

**Interfaces:**
- Consumes: Task 6 `TrafficStrips.MinuteStarts`.
- Produces: `internal static bool IsFull(UsageHistoryBucket bucket, long bucketMilliseconds) => bucket.Minutes >= Math.Max(1, TrafficStrips.MinuteStarts(bucket.Start, bucket.End) - 0.5);`. The signature is unchanged and `CanConnect` (:31-32) is unchanged.

- [ ] **Step 1: Write the check**

```csharp
private static void CheckFractionalBucketsConnect(DateTimeOffset now){ // ReadMetric's Week grid at 1,500 points: 403,200 ms (6.72 min) from now−7 d (a whole minute)
    var from=now.AddDays(-7);UsageHistoryBucket B(int k,long minutes)=>new(from.AddMilliseconds(403_200L*k),from.AddMilliseconds(403_200L*(k+1)),50,40,60,minutes,minutes,minutes*60);
    Assert.Equal(6,TrafficStrips.MinuteStarts(B(3,0).Start,B(3,0).End));Assert.Equal(7,TrafficStrips.MinuteStarts(B(2,0).Start,B(2,0).End));
    Assert.True(UsageHistoryChart.IsFull(B(3,6),403_200));Assert.True(UsageHistoryChart.CanConnect(B(2,7),B(3,6),403_200));
    Assert.False(UsageHistoryChart.IsFull(B(3,5),403_200));Assert.False(UsageHistoryChart.CanConnect(B(2,7),B(3,5),403_200));
    Assert.False(UsageHistoryChart.IsFull(B(2,6),403_200));Assert.True(UsageHistoryChart.IsFull(B(2,7),403_200));}
```

- [ ] **Step 2: Build, commit, push and verify failure (CI-red first)**

```bash
git add tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs
git commit -m "test(usage): fully recorded fractional buckets connect"
```

Run (CI). Expected: the UI fact fails in phase "usage history" at `CheckFractionalBucketsConnect`'s first `IsFull(B(3,6), …)` assertion. `CheckChart` passes unchanged before it.
- [ ] **Step 3: Change `IsFull`**
- [ ] **Step 4: Commit, push and verify pass**

```bash
git add src/DeadWare.App/UsageHistoryChart.cs
git commit -m "fix(usage): line charts count a bucket's whole-minute starts"
```

Run (CI). Expected: green.

---

### Task 10: `TrafficStripChart` and `TrafficStripView` (App)

Spec: *The traffic strip* → What the strip plots (series, colours), Slot states, Drawing, Errors and limits; Tests → `TrafficStripUiChecks`.

**Files:**
- Create: `src/DeadWare.App/TrafficStripChart.cs`
- Modify: `src/DeadWare.App/Theme.cs`: `StripBrush` and `StripColor` beside `CoreBrush` (:25-39) and `DriveBrush` (:40-53). The strip cache is cleared in `ApplyGraphColors` (:54).
- Create: `tests/DeadWare.App.Tests/TrafficStripUiChecks.cs`
- Modify: `tests/DeadWare.App.Tests/BatteryUiTests.cs`: insert `Phase("traffic strip"); TrafficStripUiChecks.Run(app);` right before `Phase("usage history");` (:84).

**Interfaces:**
- Consumes: Task 6's model and `TrafficStrips` (`IsFull`, `MinuteStarts`, `Bytes`, `Rate`, `SlotAt`, `SlotLengthText`); `ChartFrame.Grid`, `ChartFrame.Nearest`, `ChartFrame.Chip` (`ChartFrame.cs`), `GraphDepth.Surface` (`GraphDepth.cs`), `Ui.Bytes` (`Charts.cs`); `ThemeTokens.CoreTint`.
- Produces:
  - `Theme.StripBrush(int index)` (`public static SolidColorBrush`), cached like `CoreBrush`.
  - `internal static Color Theme.StripColor(int index, bool highContrast)`:
    - outside high contrast: indexes 0–2 are `Graph/download`, `Graph/gpu` and `Graph/cpu`, and 3–5 are `ThemeTokens.CoreTint(base, 1, 2, false)` of the same three, with the user's graph colours applied;
    - in high contrast: `ThemeTokens.CoreTint(SystemColors.WindowTextColor, index, 6, true)`. In high contrast, `Graph/download` holds WindowText (`Theme.cs:103`), so this is the spec's `CoreTint(Graph/download, i, 6, true)`, and it lets the checks compute the greys without switching Windows to high contrast.
    - `StripBrush` passes `SystemParameters.HighContrast`.
  - `internal enum TrafficStripPage { Connections, UsageHistory }`
  - `internal sealed class TrafficStripChart : FrameworkElement` (Height 130, `MinHeight` 120, automation name "Traffic by application"):
    - `internal TrafficStripChart(TrafficStripPage page)`
    - `internal void SetData(TrafficStrip strip, DateTimeOffset dueUntil, DateTimeOffset now)` and `internal void SetText(string text)`. `SetText` clears the data and draws the text the way "No history in this range yet" is drawn.
    - `internal TrafficStrip? Strip { get; }`
    - `internal bool Selectable { get; set; }`, `internal int? SelectedIndex { get; set; }` and `internal DateTimeOffset? MarkTime { get; set; }`
    - `internal event Action<int, DateTimeOffset>? SlotActivated`, `internal event Action<int>? SelectionMoved` and `internal event Action? HoverChanged`
    - `internal int? HoverIndex { get; }` and `internal Rect PlotRect { get; }`
    - `internal int? SlotAt(Point point)`, `internal void HandleClick(Point point)`, `internal bool HandleKey(Key key)` and `internal void HoverAt(Point? point)`. Mouse moves call `HoverAt`, and the checks call it directly.
    - Inspection members for the checks:
      - `internal IReadOnlyList<string> ChipLines { get; }`;
      - `internal IReadOnlyList<(int Series, bool Full, StreamGeometry Geometry)> Bands { get; }`, in stack order (bottom first), non-empty geometries only;
      - `internal IReadOnlyList<StreamGeometry> Separators { get; }`;
      - `internal Geometry? Hatch { get; }`, `internal Geometry? Baseline { get; }` and `internal Rect? NotKept { get; }`.
  - `internal sealed class TrafficStripView : StackPanel`:
    - `internal TrafficStripView(UiSession session, TrafficStripPage page)`
    - `internal TrafficStripChart Chart { get; }`
    - `internal void Apply(TrafficStrip? strip, DateTimeOffset dueUntil, DateTimeOffset now)`. A null strip shows "History unavailable".
    - `internal void ShowText(string text)`
    - `internal IReadOnlyList<(string Text, string? Tip, Brush? Swatch, bool IsButton)> Legend { get; }`, `internal string Caption { get; }` and `internal string NotKeptLine { get; }`
    - `internal event Action<string, string>? ApplicationChosen` (key, name). On Connections each application legend entry is a `Button` with `AutomationProperties.Name` "{name}: show only this application", whose `Click` raises it; Other is plain text.
    - `internal void RefreshSwatches()`. Both the `session.Changed` handler and the `SystemParameters.StaticPropertyChanged` handler (for `HighContrast`, at `DispatcherPriority.Background`) call it, and the checks call it directly.

The spec's Drawing subsection fixes the sizes, margins, scale, colours, bands, separators, gaps, hover, legend, theme changes, keys and empty texts. The plan decides these:
- **Bands** draw as `DrawGeometry(brush, null, geometry)`, where `brush` is a frozen clone of `StripBrush(series)` (or `Muted` for Other) with `Opacity` 0.85 for full and 0.45 for partial. A run is broken by any change of slot state, the No traffic state included.
- **"Recorded m of n min"** rounds with `MidpointRounding.AwayFromZero`.
- **The legend header** for a hovered or selected slot is that slot's span, as the chip's first line shows it. For the whole span it is "Totals for this interval" (Connections) or "Totals for this period" (Usage history).
- **Empty texts the chart decides from the data:**
  - Connections: "No saved history in this interval" when no slot is listed and the interval is not wholly before `RetainedFrom`. When it is wholly before, the plot is the "Not kept" band.
  - Usage history: "No history in this range yet" when no slot is listed, and "No per-application capture in this range" when every listed slot has `RecordedSeconds` 0.
  - The pages pass "Reading saved history…" and "Choose a valid interval" through `ShowText`.
- **Caption:** "Traffic by application · {SlotLengthText} slots", plus " · received" or " · sent" when `Strip.Measure` is Received or Sent (Connections). **Not-kept line:** "Not kept before {RetainedFrom local, "g"}", plus " · Capacity shortened retention" when `CapacityShortened`. It is empty while `RetainedFrom` is null.

- [ ] **Step 1: Write the checks**

```csharp
// T0 = 2026-11-07 03:00Z. Strip(n, apps=6, page, build) gives n one-minute full slots from T0 with every series non-zero; Draw(chart, w=600, h=130) lays out and
// returns the GeometryDrawings of VisualTreeHelper.GetDrawing(chart), flattened. Themes = the nine names. DeltaE76(a,b) converts sRGB to CIE Lab (D65).
internal static void Run(Application app){ CheckStackOrderAndColours(app);CheckGapsZerosAndPartials();CheckBandsAndSeparators();CheckChipAndLegend();CheckKeysAndClicks();
    CheckNotKeptAndEmptyTexts();CheckHoverDoesNotRebuildGeometry();CheckThemeRefresh(app);}
static void CheckStackOrderAndColours(Application app){ foreach(var theme in Themes){ ApplyTheme(theme);var chart=new TrafficStripChart(TrafficStripPage.Connections);chart.SetData(Strip(1),T0.AddHours(1),T0.AddHours(1));
    var drawings=Draw(chart);Assert.Equal([0,1,2,3,4,5,6],chart.Bands.Select(b=>b.Series));
    for(int k=1;k<7;k++)Assert.True(chart.Bands[k].Geometry.Bounds.Top<chart.Bands[k-1].Geometry.Bounds.Top);   // bottom series first
    for(int k=0;k<7;k++){var d=drawings.Single(x=>ReferenceEquals(x.Geometry,chart.Bands[k].Geometry));Assert.Null(d.Pen);
        Assert.Equal(k<6?Theme.StripBrush(k).Color:((SolidColorBrush)app.Resources["Muted"]).Color,((SolidColorBrush)d.Brush).Color);}
    for(int i=0;i<6;i++){Assert.True(DeltaE76(Theme.StripColor(i,false),PanelColor(app))>=30);for(int j=i+1;j<6;j++)Assert.True(DeltaE76(Theme.StripColor(i,false),Theme.StripColor(j,false))>=20,$"{theme} {i}/{j}");}
    var greys=Enumerable.Range(0,6).Select(i=>Theme.StripColor(i,true)).ToArray();Assert.Equal(6,greys.Distinct().Count());Assert.All(greys,c=>Assert.True(c.R==c.G&&c.G==c.B));}
    Theme.SetGraphColors(new Dictionary<string,string>{["download"]="#123456"});Assert.Equal(Hex("#123456"),Theme.StripBrush(0).Color);
    Assert.Equal(ThemeTokens.CoreTint(Hex("#123456"),1,2,false),Theme.StripBrush(3).Color);Theme.SetGraphColors(new Dictionary<string,string>());}
static void CheckGapsZerosAndPartials(){ // 36 one-minute slots: 5 missing; 10 zero (60 s, no bytes); 15 partial (45 s); 20 incomplete (60 s); 25 no-capture (RecordedSeconds 0, Usage page);
    // To T0+36 min; listed slots are 0–33 except 5, so 34 and 35 are unlisted. dueUntil T0+34 min, now T0+34.5 min. Centre(k) = the plot point of minute k's middle.
    Assert.True(chart.Hatch!.FillContains(Centre(5)));Assert.True(chart.Hatch.FillContains(Centre(25)));
    Assert.All(new[]{0,10,15,20,30,34,35},k=>Assert.False(chart.Hatch.FillContains(Centre(k))));
    chart.HoverAt(Centre(34));Assert.Equal(["03:34–03:35","Not saved yet","Minutes are saved after they end"],chart.ChipLines);   // local-time span in the check's culture
    chart.HoverAt(Centre(35));Assert.Empty(chart.ChipLines);   // after the end of now's minute: no hover target
    Assert.True(chart.Baseline!.Bounds.Left<=Centre(10).X&&Centre(10).X<=chart.Baseline.Bounds.Right);
    Assert.Equal(0.45,BandBrush(chart,15).Opacity,3);Assert.Equal(0.45,BandBrush(chart,20).Opacity,3);Assert.Equal(0.85,BandBrush(chart,0).Opacity,3);
    // Full runs [0–4],[6–9],[11–14],[16–19],[21–24],[26–33]: each series' full geometry has 6 figures and its partial geometry 2.
    Assert.All(chart.Bands.Where(b=>b.Full),b=>Assert.Equal(6,Figures(b.Geometry)));Assert.All(chart.Bands.Where(b=>!b.Full),b=>Assert.Equal(2,Figures(b.Geometry)));
    // A Usage slot of 403,200 ms with 6 minute starts and RecordedSeconds 360 draws at 0.85.
}
static void CheckBandsAndSeparators(){ // fills have no pen (as above); 36 slots at 600 px: Separators.Count == 6 (every band but the top), every figure one horizontal
    // LineSegment (start.Y == end.Y); 1,440 slots at 600 px: Separators empty.
}
static void CheckChipAndLegend(){ // full slot: ChipLines == [span, "{Ui.Bytes(total)} · {Ui.Bytes(rate,true)} average"]; partial 45 s: + "Recorded 1 of 1 min"; incomplete: + "Incomplete capture";
    // no-capture slot: [span, "No per-application capture"] (no value line); Connections slot of 0.7 s: [span, "Recorded 0 of 1 min"] (no value line).
    // Nothing hovered: Legend[0].Text "Totals for this interval"; entries "{name} · {bytes} · {0.#}%". Hover or select a slot: Legend[0].Text is its span and values are the slot's, without shares.
    // A no-capture slot (Usage): every value "—" with Tip "No per-application capture in this slot"; a Connections slot under one second: Tip "Under a second recorded in this slot".
    // 15 applications: the last entry starts "Other (9 more) · "; Other 0: no Other entry. No application bytes: "No application traffic recorded in this interval." / "… in this period."
    // A 256-character name: the entry's ActualWidth ≤ 240, TextTrimming CharacterEllipsis, Tip "{name} · ↓ {received} · ↑ {sent}".
}
static void CheckKeysAndClicks(){ // MarkTime unset: Left with nothing selected selects the last slot and raises SelectionMoved; Right the first; Enter raises SlotActivated(index, slot.Start).
    // MarkTime in the gap at minute 5: Left selects slot 4, Right slot 6. HandleClick inside slot 3 raises SlotActivated(3, t) with t in [Start, End).
    // A click on minute 5 (gap) or on the not-yet target raises nothing. Keyboard focus draws an Accent-pen frame around PlotRect.
}
static void CheckNotKeptAndEmptyTexts(){ // RetainedFrom at minute 10: NotKept spans origin..minute 10, labelled "Not kept" (≥ 60 px). RetainedFrom ≥ To: NotKept equals PlotRect.
    // NotKeptLine "Not kept before {g}" and, with CapacityShortened, "… · Capacity shortened retention".
    // Texts drawn: ShowText("Reading saved history…"), Apply(null) → "History unavailable", ShowText("Choose a valid interval"), no listed slot → "No saved history in this interval";
    // Usage: no listed slot → "No history in this range yet", every slot RecordedSeconds 0 → "No per-application capture in this range", null → "History unavailable".
}
static void CheckHoverDoesNotRebuildGeometry(){ // 1,500 slots: keep the Bands geometry references, call HoverAt at 100 positions: every reference is the same;
    // Draw at 700 px: every reference changes.
}
static void CheckThemeRefresh(Application app){ // a view whose LoadedEvent is raised (as ConnectionMapUiChecks.cs:64 does): Theme.SetGraphColors(download #123456), then raise
    // session.Changed (its backing delegate, by reflection): Legend swatches 0 and 3 show StripBrush(0) and StripBrush(3).
    // Theme.SetGraphColors(download #654321) without Changed; invoke SystemParameters.StaticPropertyChanged's backing delegate the same way with new PropertyChangedEventArgs("HighContrast")
    // and pump the dispatcher at DispatcherPriority.Background: all six swatches show the new brushes. After UnloadedEvent is raised, the same raise leaves the swatches unchanged.
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS0246` for `TrafficStripChart`, `TrafficStripPage` and `TrafficStripView`, and `error CS0117` for `Theme.StripBrush`.
- [ ] **Step 3: Implement `TrafficStripChart.cs` and `Theme.StripBrush`/`StripColor`**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/TrafficStripUiChecks.cs tests/DeadWare.App.Tests/BatteryUiTests.cs
git commit -m "test(ui): traffic strip drawing, states, legend, keys and themes"
git add src/DeadWare.App/TrafficStripChart.cs src/DeadWare.App/Theme.cs
git commit -m "feat(ui): traffic strip chart and view"
```

Run (CI). Expected: green, with the UI fact passing "traffic strip".

---

### Task 11: The strip on Connections History: placement, reads, cursor and read errors (App)

Spec: *The traffic strip* → Connections: page (Placement, Queries, Strip reads, Time cursor, Copy caption), Errors and limits; section 7 → Errors (the "—" counts); *The Destinations table* → Page layout (rows 0–6, `MeasureOverride`).

**Files:**
- Modify: `src/DeadWare.App/ConnectionsPage.cs`:
  - body rows (:83-84) become seven: Auto, Star (`MinHeight` 300), Auto ×5;
  - `trafficCard` goes after the map card (:97), in row 2;
  - the node list moves to row 3 (:102), the footer to row 4 (:121) and the settings to row 6 (:122-124). Row 5 waits for Task 17;
  - `MeasureOverride` (:137-146) and the `SizeChanged` wiring (:127);
  - `BuildQuery` (:195-200), `RequestHistory` and `PumpHistoryAsync` (:206-230), `ChangeMode` (:175), `Loaded`/`Unloaded`/`IsVisibleChanged` (:135), the `cursor.ValueChanged` and minute handlers (:130), `ApplyHistory` (:244-257) and `ExportAsync` (:331).
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`

**Interfaces:**
- Consumes: Task 7's `ConnectionHistoryView.Strip`; Task 10's `TrafficStripView` (Connections) and its chart events; Task 6's `DueUntil`, `SlotAt` and `CeilingMinute`; Task 4's `ConnectionService.DefaultFlushInterval`.
- Produces (page-private unless stated; the checks read them by name):
  - `trafficCard` (`Border`, `Ui.Panel` around `trafficStrip`, collapsed in Live) and `trafficStrip` (`TrafficStripView`).
  - The strip state: `stripContext`, `stripNow`, `pendingStrip`, `stripReading` and `stripFailed`.
  - `internal Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;`, the "now" the page passes when it applies a strip.
  - `private ConnectionHistoryQuery? BuildQuery(out ConnectionHistoryQuery? whole)`. One `TryInterval` call; it returns the minute query while the cursor is on and the whole query otherwise. `whole` is always the whole interval. `cursorTime` is set only from the returned query, and `ExportAsync` uses `whole`.
  - `internal void ReloadHistory()`: clears `stripContext` and `stripFailed`, then calls `RequestHistory()` when the page is in History.
  - **Unavailable views.** A view with `Strip` null (`Empty`: a failed read, the TEMP limit or no service) shows the summary "History · minute-bucket average rates · — destinations · — countries · — local · — unlocated · ↓ — received / ↑ — sent", with the tooltip "History unavailable". Any other view clears that tooltip.
  - **Minute reads** never touch the strip: with the cursor on, `ApplyHistory` ignores `view.Strip`.

The spec's Strip reads, Time cursor and Empty texts fix everything else: the pump order, the context comparison, cancelling, showing again (on `Loaded` as well as `IsVisibleChanged`), playback, the hairline, the click, the keys, the stale boxes and "History unavailable" with `stripFailed`. Due until = `TrafficStrips.DueUntil(To, clock(), ConnectionService.DefaultFlushInterval + TimeSpan.FromSeconds(session.Settings.PollingIntervalSeconds))`.

- [ ] **Step 1: Write and update the checks**

```csharp
// A fake readHistory queues each request and its TaskCompletionSource, as CheckHistoryQueries does; WithStrip(view) adds a strip of one-minute slots for its query.
// Call these from RunCore after CheckHistoryLimitError: CheckStripReads(session, now), CheckStripCursor(session, now), CheckMinuteReadFailureKeepsTheStrip(session, now).
static void CheckStripReads(UiSession session, DateTimeOffset now){ var page=new ConnectionsPage(session);   // readHistory replaced by the fake
    // Cursor off: History mode makes exactly one request per context, and the reply's Strip is shown (trafficStrip.Chart.Strip is the reply's).
    // Cursor on, 10:00–10:03: a cursor move requests only its minute; changing the search requests the minute, then the whole interval, and the strip reply applies only its Strip
    //   (the minute's rows stay in the summary).
    // Ten cursor moves while the strip read is pending: no new whole-interval request; after the strip reply the next request is the last minute.
    // A strip reply for an old context (search changed meanwhile) is dropped.
    // A thrown strip read: trafficStrip shows "History unavailable", stripFailed is set, actionStatus "History could not be read (IOException).", and no further whole request for that context.
    // A thrown read with the cursor off: Field<TextBlock>(page,"summary").Text contains "— destinations" and its ToolTip is "History unavailable".
    // Showing again (raise UnloadedEvent, then LoadedEvent; Loaded applies the stripNow rule as showing does), with the minute cursor on: a strip reply applied while
    //   clock() is before stripContext.To → the requests are the minute, then the whole interval; applied while clock() is after To → only the minute is requested.
    page.ReloadHistory();   // with the cursor on: the minute and then the whole interval are requested again
}
static void CheckStripCursor(UiSession session, DateTimeOffset now){
    // HandleClick on the chart at 10:01:40 turns the minute cursor on with cursor.Value 1, and the next request is minute 10:01 (FiltersChanged did not reset it to 0).
    // HandleKey(Right) then goes to the first minute of the next listed slot, Left to the previous; with the cursor in a gap, to the neighbours.
    // Moving the slider and playback move chart.MarkTime (From + value min + 30 s) and SelectedIndex at once, before the minute reply.
    // cursorTime keeps the cursor minute's text while a strip read is queued.
    // After the from box is edited to another interval and not applied, a click and a key change nothing.
}
static void CheckMinuteReadFailureKeepsTheStrip(UiSession session, DateTimeOffset now){   // Review Focus 3
    // Cursor off, apply a strip; cursor on; the minute read throws IOException: chart.Strip is unchanged, the summary shows "— destinations" with ToolTip "History unavailable",
    // actionStatus "History could not be read (IOException).". The next cursor move requests that minute and a successful reply restores the counts.
}
// Updated CheckHistoryQueries: in playback, after the first minute's reply the next request is the whole interval (10:00–10:03); AdvancePlayback requests nothing until that reply
//   arrives, then the next minute.
// Updated CheckWholePageLayout: in History, Grid.GetRow(trafficCard)==2 and Grid.GetRow(node list)==3, trafficCard above the node list; map.ActualHeight ≥ 300 at 671 × 440;
//   in Live trafficCard.Visibility==Collapsed.
// Updated CheckThemes: at 671 × 440 and 1100 × 700 in History the strip shows and the map stays ≥ 300 high and ≥ 500 wide.
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `ReloadHistory`.
- [ ] **Step 3: Implement the card, the queries, the strip reads, the cursor and the unavailable summary**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs
git commit -m "test(connections): strip placement, reads, cursor and unavailable counts"
git add src/DeadWare.App/ConnectionsPage.cs
git commit -m "feat(connections): traffic strip under the map in History"
```

Run (CI). Expected: green, with the UI fact passing "Connections page (all themes)".

---

### Task 12: The application filter, `ShowHistory` and Show in Connections navigation (App)

Spec: *The traffic strip* → Connections: page (Application filter, ShowHistory, the expander sentence), Navigation; Connections: data (`SameHistoryContext`, `Matches`).

**Files:**
- Modify: `src/DeadWare.App/ConnectionsPage.cs`:
  - fields `applicationFilter` and `applicationName`;
  - the filter button after Direction (:91-92);
  - `BuildQuery` passes `Application`;
  - `Matches` (:315) and `SameHistoryContext` (:203-205);
  - the legend's `ApplicationChosen` handler;
  - `ShowHistory` and `LocalBoxes`;
  - the expander note (:124).
- Modify: `src/DeadWare.App/UiSession.cs` (:34-36)
- Modify: `src/DeadWare.App/Windows.cs`: subscribe and unsubscribe at :78-79, and add the handler beside `ShowUsageDay` (:249).
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`

**Interfaces:**
- Consumes: Task 7's `ConnectionHistoryQuery.Application`; Task 11's `BuildQuery(out …)` and `RequestHistory`; Task 10's `ApplicationChosen`.
- Produces:
  - `public sealed record ConnectionsHistoryRequest(DateTimeOffset From, DateTimeOffset To, string? Application);` (`UiSession.cs`).
  - `public event Action<ConnectionsHistoryRequest>? ConnectionsHistoryRequested;` and `public void RequestConnectionsHistory(DateTimeOffset from, DateTimeOffset to, string? application)` on `UiSession`.
  - `internal void ShowHistory(DateTimeOffset from, DateTimeOffset to, string? application)` on `ConnectionsPage`, following the spec's steps 1–4.
  - `internal static (string From, string To) LocalBoxes(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)`: step 2's text, with the 180-step widening. It uses `TryParseLocalTime`'s rule with `zone`, and `ShowHistory` passes `TimeZoneInfo.Local`.
  - `internal Action<Window> present = window => window.Show();` on `MainWindow`, a seam the check replaces so that no native window is created.
  - `private void ShowConnectionsHistory(ConnectionsHistoryRequest request)` on `MainWindow`. It returns when `lifetime.IsEnding || Dispatcher.HasShutdownStarted`. Otherwise it runs `present(this)`, un-minimizes, calls `Activate()` (which does nothing without a native window) and `Navigate("connections")`, then `connections.ShowHistory(...)` (`pages["connections"] is ConnectionsPage`).
  - **The filter button:** "Application: {applicationName} ✕". It trims at 240 px, has the automation name "Clear application filter" and the tooltip "Only {applicationName} is shown. Click to show every application.". It shows in Live and History and is collapsed while the filter is clear.
  - **The legend handler** sets `applicationFilter` = key and `applicationName` = name, then calls `FiltersChanged()`.
  - **`SameHistoryContext`** also compares `Application` (ordinal). `Matches` adds `(applicationFilter.Length == 0 || ProcessGrouping.Key(process.Name) == applicationFilter)`.
  - **The expander note** gains, right after "…Local / special and unlocated peers remain in totals and history CSV.": "The strip under the map stacks the six applications with the most traffic in the interval, plus Other; its colours identify applications, not direction. Click it to move the minute cursor, or click an application to show only it." Task 17 replaces the sentence before it, so this sentence ends up right after the Destinations sentence, as the spec places it.

- [ ] **Step 1: Write and update the checks**

```csharp
// Called from RunCore after the Task 11 checks.
static void CheckApplicationFilter(UiSession session, DateTimeOffset now){
    // A legend click (invoke, through ButtonAutomationPeer, the legend Button named "chrome.exe: show only this application"; the Other entry is not a Button): the next request
    //   has Application "CHROME.EXE"; the button shows "Application: chrome.exe ✕"
    //   with AutomationProperties.Name "Clear application filter" and ToolTip "Only chrome.exe is shown. Click to show every application."; clicking it clears Application.
    // In Live with the filter set, a view with chrome.exe and Chrome.exe and other.exe lists only the first two (summary "2 destinations" when they reach two addresses).
    // SameHistoryContext(q, q with{Application="X"}) is false; BuildQuery(out whole), invoked by reflection, gives whole.Application == "CHROME.EXE" (ExportAsync exports whole).
}
static void CheckShowHistory(UiSession session, DateTimeOffset now){ var page=new ConnectionsPage(session);
    page.ShowHistory(now.AddMinutes(-90).AddSeconds(20),now.AddMinutes(-30),"Chrome.exe");   // from Live
    // mode is History; from/to boxes hold the whole minutes in local "yyyy-MM-dd HH:mm"; search, country "" and Direction Both; the minute cursor off, value 0;
    // the next request has Application "CHROME.EXE"; range.SelectedIndex unchanged. Again from History: a request follows at once.
    // A view whose strip has RetainedFrom ≥ To: the chart's NotKept equals its PlotRect, NotKeptLine starts "Not kept before ", and the storage line still names "Earliest …".
    var zone=TimeZoneInfo.CreateCustomTimeZone("dst-test",TimeSpan.Zero,"dst-test","dst-test","dst-test-summer",[DstRule]);   // a rule that springs forward and falls back in 2026
    var (from,to)=ConnectionsPage.LocalBoxes(AmbiguousStartUtc,InvalidEndUtc,zone);   // the ambiguous start widens earlier and the invalid end later, each to the first valid minute
}
// Updated CheckNavigation(MainWindow dashboard): replace `present` by reflection with a counter; WindowState Minimized; session.RequestConnectionsHistory(from, to, "chrome.exe")
//   → the counter is 1, WindowState Normal, the Connections page shown with its boxes and filter set as ShowHistory sets them, and
//   new WindowInteropHelper(dashboard).Handle == IntPtr.Zero. Then Navigate("overview"), read MainWindow's `lifetime` field by reflection and call BeginSessionEnding():
//   a request leaves the counter at 1 and Overview shown. Last, set the lifetime's <IsEnding>k__BackingField back to false: the later phases reuse this dashboard,
//   and VerifyUpdateNotice (BatteryUiTests.cs:1143-1144) needs RefreshUpdateNotice (Windows.cs:252) to run.
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `RequestConnectionsHistory` and `ShowHistory`, and `error CS0117` for `ConnectionsPage.LocalBoxes`.
- [ ] **Step 3: Implement the filter, `ShowHistory`, the request and the handler**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs
git commit -m "test(connections): application filter, ShowHistory and navigation"
git add src/DeadWare.App/ConnectionsPage.cs src/DeadWare.App/UiSession.cs src/DeadWare.App/Windows.cs
git commit -m "feat(connections): exact application filter and Show in Connections entry point"
```

Run (CI). Expected: green, with "Connections page (all themes)" and "dashboard and settings" passing.

---

### Task 13: The strip on Usage history and Show in Connections (App)

Spec: *The traffic strip* → Usage history: page (Placement, Reads, One cursor, Show in Connections, Copy).

**Files:**
- Modify: `src/DeadWare.App/UsageHistoryPage.cs`:
  - `ApplicationRow` (:22) gains `IsOther`;
  - `shareBar` is removed (:41, :93, :301-313);
  - the Applications card (:89-98): heading row with **Clear** and **Show in Connections**, then the strip between the search row and the summary, then the note;
  - the `SlotCleared` handler (:106) and the Esc guard (:107-114);
  - `MarkMoment` (:184-194), `LoadAsync` (:225-250) and `RefreshApplications` (:283-314);
  - the gap notice (:299).
- Modify: `src/DeadWare.App/ApplicationResourcesCard.cs`: `SlotHeading` (:203-208) builds on the new `SlotSpan`. Delete the private `CeilingMinute` (:210-211) and call `TrafficStrips.CeilingMinute` at :179 and :275, so the heading, the strip and `ConnectionsTarget` share one helper.
- Test: `tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs`

**Interfaces:**
- Consumes: Task 8's `TryReadApplicationSeriesAsync`, `TryReadApplicationsAsync` and `UsageHistoryService.DefaultFlushInterval`; Task 10's `TrafficStripView` (UsageHistory); Task 12's `RequestConnectionsHistory`; Task 6's `ConnectionsTarget`, `SlotAt`, `DueUntil` and `CeilingMinute`.
- Produces:
  - `internal static string ApplicationResourcesCard.SlotSpan(DateTimeOffset first, DateTimeOffset lastEnd, bool withDate)`, the "03:13–03:16" part. `SlotHeading` returns "Busiest apps · " + `SlotSpan(...)`, unchanged in output.
  - `internal void ApplyStrip(TrafficStrip? strip, DateTimeOffset now)`, with due until = `DueUntil(period end, now, UsageHistoryService.DefaultFlushInterval + polling)`. It then sets the strip's `SelectedIndex` from `marked`.
  - `internal void ApplySlotApplications(UsageApplicationView? view)`. Null shows "History unavailable for this slot." with no rows and no gap notice.
  - Page fields the checks read: `strip` (`TrafficStripView`), `applicationSlot` (`(DateTimeOffset From, DateTimeOffset To)?`), `selectedApplication` (`string?`), `applicationsClear` and `showInConnections` (`Button`), and `applicationsHeading` (`TextBlock`).
  - `private void ClearMoment()`: clears the Applications slot, then calls `resources.ClearSlot()`.
  - The Esc guard becomes `resources.Slot is not null || applicationSlot is not null || marked is not null`.
  - Seams the checks replace by reflection, assigned in the constructor: `private Func<DateTimeOffset, DateTimeOffset, int, Task<TrafficStrip?>> readStrip` and `private Func<DateTimeOffset, DateTimeOffset, Task<UsageApplicationView?>> readSlotApplications`, which call `session.Usage.TryReadApplicationSeriesAsync` and `TryReadApplicationsAsync`, and `private Func<bool> showing`, set to `() => IsLoaded && IsVisible` (a field initializer cannot read `this`). Also `internal Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;`.
  - `LoadAsync` starts the strip read through `readStrip` the way `resources.Load` starts the card's (same `from`, `to` and `points`), without awaiting it, under its own request counter. While a slot is listed, each refresh also starts the slot read through `readSlotApplications` under a second counter. Each continuation checks its counter and `showing()` before `ApplyStrip(strip, clock())` or `ApplySlotApplications` runs.
  - The table becomes single-select. The rebuild runs under `syncing` and re-selects `selectedApplication` by ordinal name. A row no longer shown sets it to null.
  - **Show in Connections** targets `TrafficStrips.ConnectionsTarget(slot or period, clock())`, where the period is the applied data's `From` and `To`, and is disabled when that is null. It carries `selectedApplication`, or no application for an `IsOther` row or "Unknown application". Its tooltip is the spec's four texts, with `ToolTipService.ShowOnDisabled` true.
  - **Copy:**
    - card note (:97), exactly: "Totals come from observed per-application traffic (ETW) during the period, summed by executable name across its processes. Time without capture is a gap, not zero traffic. The strip stacks the six applications with the most traffic, plus Other, as average rates over each slot's captured minutes; its colours identify applications, not direction. Click a slot, or press Enter on it, to list that slot's applications. Show in Connections opens where the bytes went, for up to the last 30 days.";
    - gap notice (:299): "{n:N0} recorded minutes had no or incomplete per-application capture; the totals may miss some of their traffic, and the strip leaves them out.";
    - an empty listed slot: "No application traffic recorded in this slot.";
    - while a slot read runs: "Loading this slot…".

- [ ] **Step 1: Write and update the checks**

```csharp
// Run, month branch: :84 asserts "3 recorded minutes had no or incomplete per-application capture; the totals may miss some of their traffic, and the strip leaves them out.";
//   :92-93 become: after page.ApplyStrip(StripFor(now), now) (Data's 1,500-slot grid; six listed applications and OtherApplications 9: the spec's 15-app fixture), the strip's Legend has eight entries:
//   Legend[0] is the header "Totals for this period", Legend[1..6] are the six applications and Legend[7] starts "Other (9 more) · ". The seven swatches Legend[1..7].Swatch
//   are Theme.StripBrush(0..5) and Muted (the spec's seven entries).
// CheckChart: unchanged (its 5-minute buckets start on the minute).
// Call the five checks below from Run after the theme loop, before ApplicationResourcesUiChecks.Run(app). Each builds the page as Run does, applies Data(now,false),
//   then page.ApplyStrip(StripFor(now), now). Pages here are never Loaded, so a check that reads drives LoadAsync by reflection, with showing() replaced to return true.
static void CheckStripListsSlot(...){ // HandleClick on slot 3 and HandleKey(Enter) on the selected slot each list it: applicationSlot == slot, applicationsHeading.Text == "Applications · " + SlotSpan(...),
    // applicationsClear Visible, summary "Loading this slot…", then page.ApplySlotApplications(slotFixture) shows its rows; ApplySlotApplications(null) shows "History unavailable for this slot." and no rows;
    // with the focus scope's element null, activation leaves FocusManager.GetFocusedElement(scope) null (offscreen nothing takes keyboard focus; as ApplicationResourcesUiChecks.cs:603-610);
    // at 1050 × 800 with the page at the top, the Applications grid starts below the fold; after HandleClick on slot 3, VerticalOffset > 0 and the grid's top is inside the viewport
    //   (as Clicking_a_point_brings_the_card_into_view, ApplicationResourcesUiChecks.cs:476-494); ScrollToTop, then Apply(Data(now,false)) and ApplyStrip: VerticalOffset stays 0;
    // CPU, RAM and GPU SelectedIndex mark the slot by time; resources.Slot stays null, and a slot it already listed stays listed.
}
static void CheckStripSharesTheCursor(...){ // HandleKey(Left) on the strip moves the Moments charts' SelectedIndex by time and lists nothing; Left on the CPU chart moves the strip's SelectedIndex;
    // Apply(Data(now,false)) again keeps the strip's hairline at its time.
}
static void CheckStripClearPaths(...){ // each of Esc, applicationsClear, the card's Clear, a range change, ◀ Earlier and ShowDay clears applicationSlot, resources.Slot, marked and every SelectedIndex;
    // applicationsClear holding the scope's focus (FocusManager, as ApplicationResourcesUiChecks.cs:521-524) hands it to the search box.
}
static void CheckStripReadsOnItsOwn(...){ // readStrip replaced by a fake that queues TaskCompletionSources: with the strip read pending, Apply shows the Device charts and the table;
    // two queued strip reads completed newest first: the newest is shown and the older reply is dropped; ApplyStrip(null, now) shows "History unavailable";
    // a Day period at offset 0: chart.Hatch does not contain the plot's right edge; each LoadAsync starts a new strip read.
}
static void CheckShowInConnections(...){ // capture session.ConnectionsHistoryRequested; clock set by reflection for each case.
    // clock = now: the period gives whole minutes ([CeilingMinute(from), CeilingMinute(to))); a listed slot gives its whole minutes;
    // selecting chrome.exe carries "chrome.exe"; "Other applications (…)" and "Unknown application" carry null;
    // the selection survives Apply (ItemsSource replaced) and a search hiding the row clears selectedApplication;
    // clock = now + 40 d: IsEnabled false, ShowOnDisabled true, ToolTip "Connections keeps up to 30 days; this period is older.";
    // clock = now + 1 d (a day clipped from the 30-day period): the tooltip ends " Connections keeps up to 30 days, so earlier minutes are left out.".
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `ApplyStrip` and `ApplySlotApplications`, and `error CS0117` for `ApplicationResourcesCard.SlotSpan`.
- [ ] **Step 3: Implement the strip, the slot listing, one cursor, Show in Connections and the copy**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/UsageHistoryUiChecks.cs
git commit -m "test(usage): strip, slot listing, shared cursor and Show in Connections"
git add src/DeadWare.App/UsageHistoryPage.cs src/DeadWare.App/ApplicationResourcesCard.cs
git commit -m "feat(usage): traffic strip in place of the share bar, with Show in Connections"
```

Run (CI). Expected: green, with "usage history" passing, including `ApplicationResourcesUiChecks`.

---

## 0.16.0 — The Destinations table

### Task 14: The Destinations Core model: class, Live list, caps, scope, sort, copy and setting

Spec: *The Destinations table* → Rows, Class, Columns (cell rules), Scope, Caps, Count line and notes, Sorting, Empty states, Selection (status lines), Copy and export (texts), Core model.

**Files:**
- Modify: `src/DeadWare.Core/NetworkAddress.cs`: `Describe`, after `Classify` (:10-41).
- Modify: `src/DeadWare.Core/ConnectionModels.cs`: the spec's records and enum.
- Create: `src/DeadWare.Core/ConnectionDestinations.cs`
- Modify: `src/DeadWare.Core/Monitoring.cs`: `UserSettings` (:374-414) and `SettingsCodec.Read` (:418).
- Test: `tests/DeadWare.Tests/NetworkAddressTests.cs`, `tests/DeadWare.Tests/ConnectionDestinationsTests.cs` (new) and `tests/DeadWare.Tests/ConnectionSettingsTests.cs`

**Interfaces:**
- Consumes: `NetworkAddress.Classify`/`Normalize`, `ProcessGrouping.Key`, `UsageMetrics.NormalizeName`, `LocatedConnection`.
- Produces:
  - The spec's records, verbatim: `DestinationScope`, `NetworkAddressClass`, `ConnectionDestinationApp`, `ConnectionDestinationPort`, `ConnectionDestination`, `ConnectionDestinationCounts` and `ConnectionDestinationList`.
  - `public static NetworkAddressClass Describe(IPAddress address)`, which normalizes first; its `Kind` always equals `Classify`'s, and its labels follow the spec's class table, first match wins. `public static NetworkAddressClass Describe(string address)` gives "Special · unrecognized" (Special) for unparseable text.
  - `public static class ConnectionDestinations`:
    - `public const int MaximumRowsPerScope = 1000, MaximumApps = 10, MaximumPorts = 10, MaximumPoints = 8, ExportMaximumApps = 100, ExportMaximumPorts = 1000;`
    - `public static ConnectionDestinationList FromLive(IReadOnlyList<LocatedConnection> connections, bool available)`:
      - one row per `RemoteAddress`;
      - apps by key, shown as the `NormalizeName` of the first connection's name in view order; ports by (port, protocol);
      - busiest by received + sent (saturating) descending; ties by key for apps, and by port then Tcp before Udp for ports;
      - rates summed, and null when `!available`; `Connections` is the peer count; `Minutes` and `Complete` are null;
      - Location: the first non-null location in view order; Points: its point when valid, whatever the class; `Unlocated` = Public without a valid point;
      - `Rows` holds each kind's top 1,000 by `(double)received + sent` descending, then address ordinal, so at most 3,000; `Counts` is exact.
    - `public static IReadOnlyList<ConnectionDestination> InScope(ConnectionDestinationList list, DestinationScope scope)`: All is the top 1,000 of `Rows` by the same rank, Internet is Public and Local is Local.
    - `public static DestinationScope ParseScope(string? value)`: "Internet" and "Local" (ordinal), else All.
  - `public enum ConnectionDestinationColumn { Destination, Class, Location, Apps, Ports, Download, Upload, Received, Sent, Connections, Minutes, FirstSeen, LastSeen }`
  - `public static class ConnectionDestinationSort`:
    - `public static int Compare(ConnectionDestination a, ConnectionDestination b, ConnectionDestinationColumn key, bool descending)` implements the spec's sort keys, with null and "—" last in both directions and ties by address, ordinal;
    - `public static bool DescendingFirst(ConnectionDestinationColumn key)` is false for Destination, Class, Location, Apps and Ports;
    - `public static IReadOnlyList<ConnectionDestinationColumn> Columns(bool history)` lists the spec's column order per mode;
    - `public static ConnectionDestinationColumn InMode(ConnectionDestinationColumn key, bool history)` returns `key` when the mode has it, else `Received`.
  - `public static class DestinationCopy` holds every fixed string the spec gives for the count line, the notes, the empty states, the CLASS and LOCATION tooltips, the status line, copying and export. Each is exact copy from the spec:
    - `CountLine(ConnectionDestinationCounts counts, DestinationScope scope, bool history)`: the count sentence, "1 destination" singular, and the over-1,000 continuation for the scope's count in the mode's wording.
    - `UnavailableCountLine` = "— destinations: — Internet (— unlocated), — local, — special-purpose.", which section 7's "—" rule implies (a plan decision on the exact text).
    - `Footnote(bool history)`.
    - `LiveEmpty(bool available, bool anyConnections, bool filtersSet, DestinationScope scope)` and `HistoryEmpty(bool minute, bool filtersSet, DestinationScope scope)`; the History constants `InvalidInterval`, `Reading` and `Unavailable`.
    - `ClassTip(NetworkAddressKind kind)`, and the LOCATION tips `CountryOnly`, `SomeMinutesUnlocated`, `NoDatabase`, `NotInDatabase`, `NoSavedLocation`, `LocalNotLocated` and `SpecialNotLocated`.
    - `HeaderTip(ConnectionDestinationColumn column, bool history)` (the RECEIVED, SENT, CONNECTIONS and MINUTES header tooltips; null for the other columns), and the DESTINATION tooltip lines `ObservedSince(DateTimeOffset first)` (local time, "g"), `LiveIncomplete` and `HistoryIncomplete`, and the APPS and PORTS tooltip's `MoreLine(int n)` ("and {n:N0} more").
    - `RateUnavailable`.
    - `Highlighted(int nodes)`, `NotAmongDrawn` and `NotOnMap(NetworkAddressKind kind)`.
    - `Copied`, `CopyFailed(string exceptionType)`, `ExportLiveOnly` and `ExportLimit`.
  - `public string ConnectionsDestinationScope { get; set; } = "All";` on `UserSettings`. `SettingsCodec.Read` sets it to `ConnectionDestinations.ParseScope(s.ConnectionsDestinationScope).ToString()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// NetworkAddressTests
[Theory][MemberData(nameof(ClassTable))]   // one or more addresses per class-table row: both ends of each IPv4 range, 0.0.0.0, 0.1.2.3, 255.255.255.255, ::, ::1, ::ffff:10.0.0.1 …
public void Describe_labels_every_classified_range(string address,string label){ var ip=IPAddress.Parse(address);var c=NetworkAddress.Describe(ip);Assert.Equal(label,c.Label);Assert.Equal(NetworkAddress.Classify(ip),c.Kind);}
[Fact] public void Unparseable_destination_text_is_special_unrecognized()=>Assert.Equal(new NetworkAddressClass(NetworkAddressKind.Special,"Special · unrecognized"),NetworkAddress.Describe("not-an-ip"));
// ConnectionDestinationsTests. P(remote, name="a.exe", pid=10, localPort=100, port=443, protocol=Tcp, received=10, sent=5, down=1, up=2, first=T0, last=T0, location=null) is a LocatedConnection.
[Fact] public void Live_destinations_group_by_remote_address(){ var l=ConnectionDestinations.FromLive([P("8.8.8.8",received:long.MaxValue,first:T0.AddSeconds(5)),
        P("8.8.8.8",name:"b.exe",pid:11,localPort:101,received:1,last:T0.AddSeconds(9)),P("8.8.8.8",localPort:102,port:53,protocol:ConnectionProtocol.Udp)],true);
    var r=Assert.Single(l.Rows);Assert.Equal((2,2,3),(r.AppCount,r.PortCount,r.Connections));Assert.Equal(long.MaxValue,r.ReceivedBytes);Assert.Equal((3d,6d),(r.DownloadBytesPerSecond,r.UploadBytesPerSecond));
    Assert.Equal((T0,T0.AddSeconds(9)),(r.FirstSeen,r.LastSeen));Assert.Null(r.Minutes);Assert.Null(r.Complete);}
[Fact] public void Live_rates_are_null_when_capture_is_unavailable(){ var r=ConnectionDestinations.FromLive([P("8.8.8.8")],false).Rows[0];Assert.Null(r.DownloadBytesPerSecond);Assert.Null(r.UploadBytesPerSecond);}
[Fact] public void Live_app_names_group_case_insensitively_and_keep_the_first_casing(){ var r=ConnectionDestinations.FromLive([P("8.8.8.8",name:"\tChrome.exe  "),P("8.8.8.8",name:"chrome.exe",pid:11)],true).Rows[0];
    Assert.Equal(("Chrome.exe",1),(r.Apps[0].Name,r.AppCount));}
[Fact] public void Live_unlocated_and_counts_follow_the_summary_rules(){ var l=ConnectionDestinations.FromLive([P("8.8.8.8",location:Located),P("1.0.0.1",location:CountryOnly),P("9.9.9.9"),
        P("192.168.1.1"),P("100.64.0.1",location:Located2)],true);
    Assert.Equal(new ConnectionDestinationCounts(5,3,1,1,2),l.Counts);Assert.Equal([Located2.Point],l.Rows.Single(r=>r.Address=="100.64.0.1").Points);
    Assert.False(l.Rows.Single(r=>r.Address=="192.168.1.1").Unlocated);Assert.False(l.Rows.Single(r=>r.Address=="100.64.0.1").Unlocated);}
[Fact] public void Apps_and_ports_keep_the_ten_busiest_with_exact_counts(){ // to 8.8.8.8: app{i}.exe receives i+1 bytes (sent 0) on port 1000+i Tcp, except app10.exe,
    // which receives 12 bytes on port 1011 Udp: 12 apps and 12 ports, with app10/app11 tied and 1011 Tcp/1011 Udp tied
    var r=ConnectionDestinations.FromLive(conns,true).Rows[0];Assert.Equal((10,12,10,12),(r.Apps.Count,r.AppCount,r.Ports.Count,r.PortCount));
    Assert.Equal(("app10.exe","app11.exe"),(r.Apps[0].Name,r.Apps[1].Name));   // ties by key, ordinal
    Assert.Equal((1011,ConnectionProtocol.Tcp,1011,ConnectionProtocol.Udp),(r.Ports[0].Port,r.Ports[0].Protocol,r.Ports[1].Port,r.Ports[1].Protocol));   // by port, Tcp before Udp
}
[Fact] public void Scopes_partition_kinds_and_special_is_all_only(){ // 8.8.8.8, 192.168.1.1, 239.255.255.250
    Assert.Equal(3,ConnectionDestinations.InScope(l,DestinationScope.All).Count);Assert.Equal(["8.8.8.8"],ConnectionDestinations.InScope(l,DestinationScope.Internet).Select(r=>r.Address));
    Assert.Equal(["192.168.1.1"],ConnectionDestinations.InScope(l,DestinationScope.Local).Select(r=>r.Address));}
[Fact] public void Each_scope_keeps_its_own_top_1000(){ // 1,200 public (20.x.y.z, bytes 2·i) and 1,100 local (10.x.y.z, bytes 2·i+1), two public addresses tied
    Assert.Equal(1000,ConnectionDestinations.InScope(l,DestinationScope.Internet).Count);Assert.Equal(1000,ConnectionDestinations.InScope(l,DestinationScope.Local).Count);
    Assert.Equal(OverallTop1000,ConnectionDestinations.InScope(l,DestinationScope.All).Select(r=>r.Address));Assert.Equal(new ConnectionDestinationCounts(2300,1200,1100,0,1200),l.Counts);}
[Fact] public void Destination_sort_rules(){ // each column's ascending order on a fixture; DescendingFirst false exactly for Destination, Class, Location, Apps, Ports;
    // null rates last ascending and descending; 9.9.9.9 before 2001:4860::8888 and "bad" last; equal Received ties by address; InMode(Download, true) == Received, InMode(Minutes, false) == Received.
}
[Fact] public void Destination_copy_texts(){ // CultureInfo.CurrentCulture = InvariantCulture
    Assert.Equal("3 destinations: 2 Internet (1 unlocated), 1 local, 0 special-purpose.",DestinationCopy.CountLine(new(3,2,1,0,1),DestinationScope.All,false));
    Assert.Equal("1 destination: 1 Internet (0 unlocated), 0 local, 0 special-purpose.",DestinationCopy.CountLine(new(1,1,0,0,0),DestinationScope.All,true));
    Assert.EndsWith(" Showing the top 1,000 by traffic (received + sent); search or narrow the interval to see the rest.",DestinationCopy.CountLine(new(1234,1234,0,0,0),DestinationScope.Internet,true));
    Assert.EndsWith(" Showing the top 1,000 by traffic (received + sent); search to see the rest.",DestinationCopy.CountLine(new(1234,1234,0,0,0),DestinationScope.All,false));
    Assert.DoesNotContain("Showing",DestinationCopy.CountLine(new(1234,1234,0,0,0),DestinationScope.Local,false));
    Assert.Equal(("Since each flow was first observed.","Saved in the shown interval."),(DestinationCopy.HeaderTip(ConnectionDestinationColumn.Received,false),DestinationCopy.HeaderTip(ConnectionDestinationColumn.Received,true)));
    Assert.Equal("Observed flows to this address: one per app process, local address and port, remote port and protocol. A flow idle for 60 s drops out.",DestinationCopy.HeaderTip(ConnectionDestinationColumn.Connections,false));
    Assert.Equal("Saved minutes with traffic to this address in the shown interval.",DestinationCopy.HeaderTip(ConnectionDestinationColumn.Minutes,true));Assert.Null(DestinationCopy.HeaderTip(ConnectionDestinationColumn.Apps,true));
    Assert.Equal($"Observed since {T0.ToLocalTime():g}.",DestinationCopy.ObservedSince(T0));Assert.Equal("and 1,234 more",DestinationCopy.MoreLine(1234));
    Assert.Equal(("Capture is incomplete; totals may be low.","Some minutes were captured incompletely; totals may be low."),(DestinationCopy.LiveIncomplete,DestinationCopy.HistoryIncomplete));
    // …and every other string as the spec words it: both footnotes, the four Live and six History empty texts (span "interval" and "minute"), the class and location tips,
    // "Highlighted 1 map node." and "Highlighted 1,234 map nodes.", the three "Not on the map" texts, "Copied to the clipboard.", "Copy failed (ExternalException); try again.",
    // "Export is available in History." and the export limit text.
}
// ConnectionSettingsTests
[Fact] public void Destination_scope_setting_normalizes_and_round_trips(){
    foreach(var json in new[]{"{}","{\"ConnectionsDestinationScope\":null}","{\"ConnectionsDestinationScope\":\"Bogus\"}","{\"ConnectionsDestinationScope\":\"local\"}"})
        Assert.Equal("All",SettingsCodec.Read(json).Settings.ConnectionsDestinationScope);
    var s=new UserSettings{ConnectionsDestinationScope="Local"};Assert.Equal("Local",SettingsCodec.Read(SettingsCodec.Write(s)).Settings.ConnectionsDestinationScope);}
```

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~NetworkAddressTests|FullyQualifiedName~ConnectionDestinationsTests|FullyQualifiedName~ConnectionSettingsTests"`. Expected: the build fails with `error CS0117` (`NetworkAddress.Describe`), `error CS0103` (`ConnectionDestinations`, `DestinationCopy`), `error CS0246` (`NetworkAddressClass`) and `error CS0117` (`ConnectionsDestinationScope` in the `UserSettings` initializer).
- [ ] **Step 3: Implement `Describe`, the records, `ConnectionDestinations.cs` and the setting**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/NetworkAddressTests.cs tests/DeadWare.Tests/ConnectionDestinationsTests.cs tests/DeadWare.Tests/ConnectionSettingsTests.cs
git commit -m "test(core): destination classes, live list, caps, scopes, sorting and copy"
git add src/DeadWare.Core/NetworkAddress.cs src/DeadWare.Core/ConnectionModels.cs src/DeadWare.Core/ConnectionDestinations.cs src/DeadWare.Core/Monitoring.cs
git commit -m "feat(core): destinations model and saved scope"
```

Run (CI). Expected: green.

---

### Task 15: History destinations and Export destinations in the store (Core)

Spec: *The Destinations table* → History store (steps 1–7 with the shared pass `temp.picked`, decision 15), Copy and export (Store, Header, Rows), Core model (`Destinations` on the view); Tests → the History and CSV tests and the Windows CI measurement.

**Files:**
- Modify: `src/DeadWare.Core/ConnectionModels.cs`: `ConnectionHistoryView` gains `Destinations`.
- Modify: `src/DeadWare.Core/ConnectionHistoryStore.cs`: `Read` (the steps after the strip), plus `ExportDestinationsCsv` and `destinationStepsTimed`.
- Modify: `tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs`
- Test: `tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 2's TEMP tables, `Grouping` and `Csv`; Task 7's `app_key`; Task 14's records, `Describe` and caps.
- Produces:
  - `ConnectionHistoryView(…, TrafficStrip? Strip = null, ConnectionDestinationList? Destinations = null)`. `Read` passes both by name.
  - `Read` runs the spec's steps 1–7, verbatim SQL for steps 1 and 4, through `Grouping`:
    1. `temp.destinations`;
    2. the uncapped counts;
    3. the rows with rank ≤ 1,000;
    4. `temp.picked` in one pass over `temp.grouped`, where `app_key` runs once per picked row;
    5. apps, at most 10 per remote, with the latest minute's name (ties go to the lower BINARY name);
    6. ports, at most 10, ties by port then protocol;
    7. locations from `temp.picked JOIN temp.geo` where `location <> 'null'`, at most 8, latest first.
  - Each row: `Class = new((NetworkAddressKind)kind, NetworkAddress.Describe(remote).Label)`; `Location` = the first location of step 7; `Points` = its distinct valid points, in order; app `Name` = `UsageMetrics.NormalizeName(name)`; `Complete = complete == 1`; rates and `Connections` null.
  - `public void ExportDestinationsCsv(TextWriter writer, ConnectionHistoryQuery query, DestinationScope scope)`:
    - it takes the gate, opens its own read-only connection and runs the pragmas, steps 2–3 of the grouped read and destination step 1 in one deferred transaction, all before writing anything;
    - then the header `Address,Class,CountryCode,Country,Region,City,Latitude,Longitude,Applications,ApplicationNames,Ports,PortList,ReceivedBytes,SentBytes,Minutes,FirstSeenUtc,LastSeenUtc,Complete`;
    - then every in-scope destination, uncapped, ordered by `cast(received AS REAL)+cast(sent AS REAL)` descending, then address. Pages of 1,000 take their apps, ports and locations from steps 4–7 restricted to the page's addresses, with the export caps (100 apps, 1,000 ports);
    - `ApplicationNames` and `PortList` hold the busiest first, joined by "; ", ending "; +{n} more" (invariant digits) when capped. Each port reads "{port} {protocol}" as the PORTS cell does;
    - the location and coordinates are the shown location's (empty without one); times use "O"; every value goes through `Csv`.
  - `internal Action<TimeSpan>? destinationStepsTimed;`: when set (by reflection, measurement only), `Read` reports the elapsed time of steps 1–7.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void History_destinations_match_a_brute_force_grouping(){ // Random(11): 60 addresses (public, local and special), 6 ASCII app names, 5 ports in both protocols, 30 minutes,
    // locations per address and minute (some null), and some incomplete batches. For every row of Read(Query(30)).Destinations: ReceivedBytes, SentBytes, Minutes, FirstSeen,
    // LastSeen, AppCount, PortCount, Apps and Ports (names, totals, order), Complete, Class.Kind, Unlocated and Location equal a LINQ grouping of the appended records.
}
[Fact] public void History_destination_counts_match_the_summary(){ // the fixture of Markers_are_bounded_… (:183): Counts (302,301,1,0,1) and All == Summary.Destinations;
    // the Unlocated_counts_public_peers_without_a_point_only fixture: Counts (6,3,1,2,2).
}
[Fact] public void History_destination_shows_the_latest_saved_location_and_every_point(){ // 8.8.8.8 saved with Place in minute 0, Elsewhere (another point) in minute 2, null in minutes 1 and 3
    var d=store.Read(Query()).Destinations!.Rows.Single();Assert.Equal(Elsewhere,d.Location);Assert.Equal([Elsewhere.Point,Place.Point],d.Points);Assert.True(d.Unlocated);
    Assert.Equal(store.Read(Query()).Summary.Unlocated,store.Read(Query()).Destinations!.Counts.Unlocated);}
[Fact] public void History_destinations_follow_search_country_direction_and_the_minute(){ // each filter keeps exactly the matching rows' addresses and totals;
    // Application "B.EXE" keeps only b.exe's traffic; a one-minute query lists only that minute's addresses.
}
[Fact] public void History_destination_lists_are_bounded(){ // 1,100 public, 1,100 local and 1,100 special addresses with one row each; 5 public addresses each reached by 15 apps on 15 ports
    // (app i on port i); one address saved with 12 distinct located locations in 12 minutes.
    var l=store.Read(Query(20)).Destinations!;Assert.True(l.Rows.Count<=3000);Assert.All(Enum.GetValues<NetworkAddressKind>(),k=>Assert.True(l.Rows.Count(r=>r.Class.Kind==k)<=1000));
    Assert.Equal(5,l.Rows.Count(r=>r.AppCount==15));Assert.All(l.Rows.Where(r=>r.AppCount==15),r=>Assert.Equal((10,15,10),(r.Apps.Count,r.PortCount,r.Ports.Count)));
    var many=l.Rows.Single(r=>r.Points.Count>1);Assert.Equal(8,many.Points.Count);Assert.Equal(Latest,many.Location);}
[Fact] public void Destinations_csv_lists_every_destination_in_scope(){ // 1,200 addresses; one reached by 120 apps; one app named "=formula"
    // header line equals the spec's; 1,200 data lines in received + sent order, then address; the Local scope lists only local addresses;
    // the "=formula" name is written as "'=formula"; Latitude and Longitude are numbers written as Csv writes them: quoted, with no formula apostrophe (for example "-87"),
    // as Csv_keeps_negative_coordinates_and_rates_numeric (:175-181) pins; the 120-app address has Applications 120 and
    // ApplicationNames of 100 names ending "; +20 more".
}
[Fact] public void Destinations_csv_over_the_temp_limit_writes_nothing(){   // Review Focus 4
    var store=Store(pages:16);store.Append([..Enumerable.Range(0,2000).Select(i=>Row(i%9,$"b{i}",address:$"20.{i/250}.{i%250}.1"))],[],Epoch.AddMinutes(10));var writer=new StringWriter();
    Assert.Throws<ConnectionHistoryLimitException>(()=>store.ExportDestinationsCsv(writer,Query(10),DestinationScope.All));Assert.Equal("",writer.ToString());}
```

In `ConnectionHistoryMeasurementTests`, set `destinationStepsTimed` by reflection before each read, and add `destinationStepsMs` to every `reads` entry. The controller adds the step times to the PR description.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ConnectionHistoryStoreTests"`. Expected: the build fails with `error CS1061` for `Destinations` and `ExportDestinationsCsv`.
- [ ] **Step 3: Implement the steps, the view member, the export and the timing hook**
- [ ] **Step 4: Run the Step 2 command, then the Core baseline.** Expected: PASS, then no output.
- [ ] **Step 5: Commit and push**

```bash
git add tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs
git commit -m "test(history): saved destinations, their bounds, the destinations CSV and step timings"
git add src/DeadWare.Core/ConnectionModels.cs src/DeadWare.Core/ConnectionHistoryStore.cs
git commit -m "feat(history): destinations from the grouped read in one shared pass, and their CSV"
```

Run (CI). Expected: green, and every `connection-history.json` read entry has `destinationStepsMs`.

---

### Task 16: The map highlight

Spec: *The Destinations table* → Selection and map highlight (one rounding rule, cap order, `ConnectionMap.Highlight`); Tests → `Location_key_matches_map_grouping`, `Grouping_keeps_highlighted_locations_within_the_cap` and `ConnectionMapUiChecks.CheckHighlight`.

**Files:**
- Modify: `src/DeadWare.Core/ConnectionProjection.cs`: `Group` (:152-163).
- Modify: `src/DeadWare.App/ConnectionMap.cs`:
  - the stored `SetData` input and `SelectPoint` key (:79-83, :105-109);
  - the node drawing (:333, :456-462);
  - the `Accent` ring, a resource reference like `Download` (:64-66);
  - the automation name.
- Test: `tests/DeadWare.Tests/ConnectionProjectionTests.cs` and `tests/DeadWare.App.Tests/ConnectionMapUiChecks.cs` (`CheckHighlight`, called from `Run`).

**Interfaces:**
- Produces:
  - `public static (double Latitude, double Longitude) LocationKey(GeoPoint point) => (Math.Round(point.Latitude, 3), Math.Round(Wrap(point.Longitude), 3));`. `Group` keys by it.
  - `public static ConnectionMapGroups Group(IEnumerable<ConnectionMapPoint> points, IReadOnlySet<(double Latitude, double Longitude)>? highlighted = null)` orders groups by Selected, then highlighted, then key. Passing null gives today's order.
  - `public void Highlight(IReadOnlyCollection<GeoPoint>? locations)` and `public IReadOnlyList<string> HighlightedKeys { get; }` on `ConnectionMap`, as the spec defines them (regroup only when the key set changes; `SetData` keeps the highlight; selected-node look plus a 1 px `Accent` ring at radius + 4 px; " · {n} highlighted" in the automation name).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Location_key_matches_map_grouping(){ Assert.Equal((41.881,-87.6),ConnectionProjection.LocationKey(new(41.8806,-87.6)));
    Assert.Equal((41.88,-87.6),ConnectionProjection.LocationKey(new(41.8804,-87.6)));   // 41.8805 is a binary midpoint; Math.Round's ToEven gives 41.88, as Group does
    Assert.Equal(ConnectionProjection.LocationKey(new(10,180)),ConnectionProjection.LocationKey(new(10,-180)));
    // Group over 40 points with keys that collide at the third decimal: every group's points share one LocationKey and no two groups share one.
}
[Fact] public void Grouping_keeps_highlighted_locations_within_the_cap(){ // 300 points at distinct keys; one Selected; highlight the key that sorts 280th
    var g=ConnectionProjection.Group(points,new HashSet<(double,double)>{key280});Assert.Contains(g.Points,p=>ConnectionProjection.LocationKey(p.Point)==key280);
    Assert.True(g.Points[0].Selected);Assert.Equal(44,g.Overflow);}
// Grouping_preserves_selection_keys_saturates_totals_and_bounds_markers (:67) passes unchanged.
// ConnectionMapUiChecks.CheckHighlight: in Midnight and Daylight, map.Highlight([point]) puts that node's key in HighlightedKeys; its rendered ellipse radius is the selected radius
//   and an Accent-brush ellipse of radius + 4 px surrounds it; Highlight(null) empties HighlightedKeys; SetData with the same points keeps it; after SelectPoint(k) a highlight change keeps
//   the selected key; AutomationProperties.GetName(map) ends " · 1 highlighted".
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS0117` for `ConnectionProjection.LocationKey` and `error CS1061` for `ConnectionMap.Highlight` and `HighlightedKeys`.
- [ ] **Step 3: Implement `LocationKey`, the ordering and `Highlight`**
- [ ] **Step 4: Run `dotnet test tests/DeadWare.Tests -c Release -p:EnableWindowsTargeting=true --nologo --filter "FullyQualifiedName~ConnectionProjectionTests"`, then the Core baseline, then commit and push.** Expected: PASS, then no output.

```bash
git add tests/DeadWare.Tests/ConnectionProjectionTests.cs tests/DeadWare.App.Tests/ConnectionMapUiChecks.cs
git commit -m "test(map): one location key and highlighted nodes"
git add src/DeadWare.Core/ConnectionProjection.cs src/DeadWare.App/ConnectionMap.cs
git commit -m "feat(map): highlight a destination's nodes"
```

Run (CI). Expected: green, with "Connections map (all themes)" passing.

---

### Task 17: The Destinations card: grid, columns, scope, rows and layout (App)

Spec: *The Destinations table* → What it is, Rows (Where rows come from), Columns, Scope, Count line and notes, Sorting (default, header clicks, mode change), Empty states, Page layout, Accessibility; section 7 → the count line's "—".

**Files:**
- Create: `src/DeadWare.App/DestinationsCard.cs`
- Modify: `src/DeadWare.App/ConnectionsPage.cs`:
  - the card in row 5;
  - `ApplyLive` (:234-243) builds the Live list from its `filtered` array;
  - `ApplyHistory` passes `view.Destinations`;
  - `RequestHistory`'s new-context branch shows "Reading saved destinations…";
  - `RefreshSession` (:165-174) sets the scope under `syncing`;
  - `MeasureOverride` and its `SizeChanged` wiring;
  - the settings help text (:124).
- Modify: `src/DeadWare.App/UiSession.cs`: `SetConnectionsDestinationScope`, beside `SetConnectionsAnimation` (:250).
- Modify: `src/DeadWare.App/Charts.cs` and `src/DeadWare.App/ProcessesPage.cs` (:508-517): the value style moves to `Ui.ValueText`, and `ProcessesPage` uses it.
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`

**Interfaces:**
- Consumes: Task 14's `FromLive`, `InScope`, `ParseScope`, `ConnectionDestinationSort` and `DestinationCopy`; Task 15's `ConnectionHistoryView.Destinations`; Task 11's layout and summary rule; Task 12's application filter (a set filter).
- Produces:
  - `internal sealed record DestinationRowContext(bool History, bool Available, bool Complete, bool HasLocationDatabase);`
  - `internal sealed class DestinationRow : INotifyPropertyChanged`:
    - `Key` (the address);
    - text properties `Destination`, `Class`, `Location`, `Apps`, `Ports`, `Download`, `Upload`, `Received`, `Sent`, `Connections`, `Minutes`, `FirstSeen` and `LastSeen`;
    - tip properties `DestinationTip`, `ClassTip`, `LocationTip`, `AppsTip`, `PortsTip`, `DownloadTip` and `UploadTip`;
    - `Spoken`, worded as the spec's Accessibility subsection gives it;
    - `internal ConnectionDestination Value { get; }`;
    - `internal DestinationRow Update(ConnectionDestination value, DestinationRowContext context)`, which raises one `PropertyChanged(string.Empty)` only when a text or tip changed, and returns `this`.
  - `internal sealed class DestinationsCard : Border` (`Ui.Panel`, Margin (0, 8, 0, 8)):
    - `internal DestinationsCard(UiSession session)`;
    - `internal DataGrid Grid` (Height 315, automation name "Destinations", the spec's grid settings, `Ui.ValueText` on the number cells);
    - `internal ComboBox Scope` (Width 120, automation name "Destinations shown");
    - `internal TextBlock CountLine`, `Status` and `Footnote`, and `internal Border Empty`;
    - `internal ObservableCollection<DestinationRow> Rows`;
    - `internal void ShowLive(ConnectionLiveView view, IReadOnlyList<LocatedConnection> filtered, bool filtersSet, bool hasLocationDatabase)`;
    - `internal void ShowHistory(ConnectionDestinationList? list, bool minute, bool filtersSet)`, where null shows the "Destinations unavailable" state and `DestinationCopy.UnavailableCountLine` with the tooltip "History unavailable";
    - `internal void ShowText(string text)`.
  - **Columns** are kept per key and swapped per mode, with the spec's star sizes. Each `MinWidth` = max(listed, measured header + 18, and for FIRST/LAST SEEN the measured "2026-12-28 23:59" in "g" + 14). Each header's `ToolTip` is `DestinationCopy.HeaderTip(key, history)`. IPv6 addresses show without brackets.
  - **Sorting:** RECEIVED descending by default; `Grid.Sorting` handles header clicks with `e.Handled`, and a mode change keeps the key through `InMode`. This task always re-sorts on new data (holds come in Task 18).
  - **The wheel:** `PreviewMouseWheel` passes the wheel to the page's `ScrollViewer` when the grid cannot scroll further that way.
  - `internal Func<bool> hasLocationDatabase;` on the page, assigned in the constructor beside `readHistory` (:80): `hasLocationDatabase = () => session.Connections?.DatabaseDate is not null;` (a field initializer cannot read `session`). Also the page's `destinations` field.
  - `public void SetConnectionsDestinationScope(string scope)` on `UiSession`, which normalizes with `ParseScope`, raises `Changed` and saves.
  - **The settings help text** "The list follows the nodes visible on the map. Local / special and unlocated peers remain in totals and history CSV." becomes "The node list follows the nodes visible on the map; the Destinations table lists every destination, including local, special-purpose and unlocated ones." Task 12's strip sentence follows it.

- [ ] **Step 1: Write and update the checks**

```csharp
// RunCore :41 and CheckWholePageLayout :189: Assert.Equal("Destinations",AutomationProperties.GetName(Assert.Single(Elements(page).OfType<DataGrid>()))); ConnectionsWidgetUiChecks.cs:476 stays.
// Card(page) => (DestinationsCard)PageType.GetField("destinations",All)!.GetValue(page)!. Call these from RunCore after the Task 12 checks.
static void CheckDestinationsLive(FrameworkElement page, ConnectionLiveView live){ var card=Card(page);   // RunCore's fixture: 8.8.8.8 located, 192.168.1.1, 9.9.9.9
    Assert.Equal(["DESTINATION","CLASS","LOCATION","APPS","PORTS","↓ DOWNLOAD","↑ UPLOAD","RECEIVED","SENT","CONNECTIONS"],card.Grid.Columns.Select(c=>(string)c.Header));
    Assert.Equal(3,card.Rows.Count);card.Scope.SelectedIndex=1;Assert.Equal(2,card.Rows.Count);card.Scope.SelectedIndex=2;Assert.Single(card.Rows);card.Scope.SelectedIndex=0;
    Assert.Equal(["Local · private","Internet","Internet"],card.Rows.OrderBy(r=>r.Key,StringComparer.Ordinal).Select(r=>r.Class));
    // headers' ToolTips: RECEIVED "Since each flow was first observed.", CONNECTIONS "Observed flows to this address: …"; 8.8.8.8's DestinationTip contains
    //   "Capture is incomplete; totals may be low." (the fixture view is not Complete).
    Assert.Equal(("—","Local addresses are not located."),Cell(card,"192.168.1.1",r=>(r.Location,r.LocationTip)));
    // hasLocationDatabase false: 9.9.9.9 LocationTip "No location database. Use Download / update locations at the bottom of the page."; true: "Not in the location database, or its lookup has not finished."
    Assert.Equal("3 destinations: 2 Internet (1 unlocated), 1 local, 0 special-purpose.",card.CountLine.Text);
    // a stale view (Available false): Download "—" with DownloadTip "Endpoint capture is stale or unavailable; no current rate."; each Live empty text through views and filters.
}
static void CheckDestinationsHistory(UiSession session, DateTimeOffset now){ // a real ConnectionService store with the two-location fixture (as CheckSavedMapDetails, :137-138)
    // History headers ["DESTINATION","CLASS","LOCATION","APPS","PORTS","RECEIVED","SENT","MINUTES","FIRST SEEN","LAST SEEN"], with RECEIVED's ToolTip "Saved in the shown interval."
    //   and MINUTES' "Saved minutes with traffic to this address in the shown interval."; one row for the address;
    // an invalid interval shows "Choose a valid interval to list saved destinations."; a pending read "Reading saved destinations…"; a thrown read "Destinations unavailable; no saved
    // observations shown." with CountLine "— destinations: — Internet (— unlocated), — local, — special-purpose."; with the minute cursor on and no rows: "No saved destinations in this minute.";
    // sorted by ↓ DOWNLOAD in Live, switching to History sorts by RECEIVED descending.
}
static void CheckDestinationScopeSaved(UiSession session){ // choose Local: session.Settings.ConnectionsDestinationScope == "Local"; a new page opens with Scope on Local;
    // the summary text and the node list are the same with All and with Local. It ends by choosing All again, so the setting is "All" for the checks that follow.
}
// CheckWholePageLayout (671 × 440): card.Grid.ActualHeight == 315; scrolling the page brings the card into view; a MouseWheel down over the grid at its last row scrolls the page's ScrollViewer.
// CheckThemes (671 × 440 and 1100 × 700, nine themes): Grid height 315; the map ≥ 300 high and ≥ 500 wide; the map's height is equal with 0 and with 50 Local destinations;
//   in en-US no column header and no FIRST SEEN or LAST SEEN cell is trimmed at the minimum widths (each TextBlock's DesiredSize.Width ≤ its cell's width).
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS0246` for `DestinationsCard`.
- [ ] **Step 3: Implement the card, the rows, the scope, the layout, `Ui.ValueText` and the session setter**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs
git commit -m "test(connections): destinations card, columns, scope, history rows and layout"
git add src/DeadWare.App/DestinationsCard.cs src/DeadWare.App/ConnectionsPage.cs src/DeadWare.App/UiSession.cs src/DeadWare.App/Charts.cs src/DeadWare.App/ProcessesPage.cs
git commit -m "feat(connections): Destinations table below the map"
```

Run (CI). Expected: green; "Connections page (all themes)" and "processes page" pass.

---

### Task 18: Destinations rows update in place, holds, selection and the map highlight (App)

Spec: *The Destinations table* → Updates in place, Selection and map highlight (page side: selection rules, a row's points, turning the map, the status line), Accessibility (keys).

**Files:**
- Modify: `src/DeadWare.App/DestinationsCard.cs`: the in-place reconcile, `OrderHold`, the immediate re-sorts, scrolling to the top and the selection.
- Modify: `src/DeadWare.App/ConnectionsPage.cs`:
  - `SelectMapPeer` (:267-274) clears the table selection and calls `Highlight(null)` before `SelectPoint`, and only when it really selects;
  - the Esc handler (:134) and `ChangeMode` (:175) also call `ClearDestinationSelection`;
  - F5 goes in the page's `PreviewKeyDown`;
  - `Unloaded` and hiding the page clear the holds;
  - after every update the page passes the selected row's points to `map.Highlight`, calls `RefreshMappedConnections` when the key set changed, turns the map (`SetView`) once per selection, and sets the status line.
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`

**Interfaces:**
- Consumes: Task 16's `Highlight`, `HighlightedKeys` and `LocationKey`; `CalmOrderPlanner.Plan` and `Held`, and `OrderHold` (`ProcessRowOrder.cs:13-84`); Task 14's `DestinationCopy.Highlighted`, `NotAmongDrawn` and `NotOnMap`.
- Produces:
  - On the card: `internal OrderHold Hold { get; }`; `internal Func<double> clockSeconds` (the page's monotonic `Stopwatch` in seconds; the checks replace it); `internal ConnectionDestination? Selected { get; }`; `internal event Action? SelectionChanged`; `internal void ClearSelection()`; `internal void Resort()`.
  - On the page: `private void ClearDestinationSelection()`.
  - **New data:** a Live view whose `Timestamp` differs from the last applied one, or a newly applied History view.
    - A Live view equal to the last applied one (record equality) changes nothing. One with the same `Timestamp` but other values updates values only.
    - New data re-sorts unless the order is held. While held, the order is `CalmOrderPlanner.Held(current, sorted, _ => null)`.
    - Header clicks, a scope change, F5, a mode change and any filter or History context change re-sort at once. A new History context, a scope change and a mode change also scroll the grid to its top.

- [ ] **Step 1: Write the checks and extend `CheckDestinationsHistory`**

```csharp
static void CheckDestinationsInPlace(FrameworkElement page, ConnectionLiveView live){ var card=Card(page);
    // a second view with a new Timestamp keeps the same DestinationRow instances, row containers, SelectedItem and the grid's VerticalOffset;
    // Hold.PointerOver=true: a view adding 1.1.1.1 puts it last and moves nothing; after PointerOver=false the next new Timestamp re-sorts;
    // applying an equal view twice (also a stale view read twice from a ConnectionService) raises no PropertyChanged and no collection change;
    // during a hold, F5, a header click and a Live search change each re-sort; a row that leaves clears SelectedItem;
    // Press(card.Grid, Key.Down) (PreviewKeyDown then KeyDown with a KeySource, as ApplicationResourcesUiChecks' Press does), then clockSeconds +2.9 s: still held; +3.1 s: not held
    // (fake clock); raising the page's UnloadedEvent clears PointerOver; a scope change scrolls the grid to its top.
}
static void CheckDestinationHighlight(FrameworkElement page, ConnectionLiveView live){ var card=Card(page);var map=Field<ConnectionMap>(page,"map");
    // select the 8.8.8.8 row: map.HighlightedKeys has its node, nodeDetails Collapsed, card.Status.Text "Highlighted 1 map node.";
    // with the globe turned away (map.SetView far from the node), selecting the row makes map.PointPosition(node) non-null;
    // the local row: "Not on the map: local addresses are not located."; a located special fixture peer: "Highlighted 1 map node.";
    // SelectMapPeer(node) after a row: card.Selected null and the node stays selected after the regroup; selecting a row clears the node selection;
    // Esc clears both; the detail box's Close leaves the table selection; a cursor move keeps the row selected;
    // Live: the selected 9.9.9.9 row gains a location in a later view with the same Timestamp → its node is highlighted and the camera does not move;
    // Live with 300 locations: a highlighted location beyond the first 256 keys is drawn.
}
static void CheckScopeChangeClearsTheSelection(FrameworkElement page){   // Review Focus 5
    // select 8.8.8.8, choose Local: card.Selected null, map.HighlightedKeys empty, card.Status collapsed; choose All: card.Grid.SelectedItem null.
}
// CheckDestinationsHistory gains: the two-location row highlights two nodes; a destination whose location is outside the 256 markers shows "Not on the map: its location is not among the 256 drawn.".
```

Call the three new checks from `RunCore` right after `CheckDestinationScopeSaved`. They read the card's new members directly (`card.Hold`, `card.clockSeconds`, `card.Selected`).

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `DestinationsCard.Hold`, `clockSeconds` and `Selected`.
- [ ] **Step 3: Implement the reconcile, the holds, the re-sorts, the selection, the highlight and the status line**
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs
git commit -m "test(connections): destinations stay calm, select and highlight"
git add src/DeadWare.App/DestinationsCard.cs src/DeadWare.App/ConnectionsPage.cs
git commit -m "feat(connections): Destinations rows update in place and highlight their nodes"
```

Run (CI). Expected: green, with "Connections page (all themes)" passing, `CheckDestinationsHistory`'s added highlight assertions included.

---

### Task 19: Copy and Export destinations (App)

Spec: *The Destinations table* → Copy and export, Accessibility (row menu, Ctrl+C, Menu key, Shift+F10), Docs and release → Preview.

**Files:**
- Modify: `src/DeadWare.App/DestinationsCard.cs`:
  - the row menu (**Copy row**, **Copy address**), opened on the row under the pointer, and by the Menu key and Shift+F10 for the selected row; while it is open, `Hold.MenuOpen` is set;
  - a `CommandBinding` for `ApplicationCommands.Copy` that replaces the grid's own copy;
  - **Export destinations…** in the toolbar.
- Modify: `src/DeadWare.App/ConnectionsPage.cs`:
  - the hooks `exportDestinations` and `SetClipboard`;
  - `ExportDestinationsAsync`, which shares `exportInProgress` with Export CSV, and `UpdateExportEnabled` (:259), which sets both buttons;
  - the results in `actionStatus`.
- Modify: `src/DeadWare.App/ConnectionService.cs`: `ExportDestinationsCsvAsync`, beside `ExportCsvAsync` (:37-46).
- Modify: `tools/OverviewPreview/Program.cs` (`RenderShell`, :246-328).
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`

**Interfaces:**
- Consumes: Task 15's `ExportDestinationsCsv`; Task 11's `BuildQuery(out whole)`; Task 14's `DestinationCopy`.
- Produces:
  - `public async Task ExportDestinationsCsvAsync(string path, ConnectionHistoryQuery query, DestinationScope scope)`: off the UI thread, `FileMode.CreateNew`, UTF-8 without BOM.
  - On the card: `internal Button Export`, the toolbar's **Export destinations…** after the scope combo, with the automation name "Export destinations…".
  - On the page: `internal Func<string, ConnectionHistoryQuery, DestinationScope, Task> exportDestinations`, assigned in the constructor beside `exportHistory` (:81) to the service, or `Task.FromException(new InvalidOperationException("Connection export unavailable."))` without one; and `internal Action<string> SetClipboard = Clipboard.SetText;`.
  - `private async Task ExportDestinationsToPathAsync(string path)`, which `ExportDestinationsAsync` calls after the dialog. It builds `whole` with `BuildQuery(out whole)`, reads the card's scope, refuses an existing file with Export CSV's text, shares `exportInProgress` and calls `exportDestinations(path, whole, scope)`.
  - **Copy row:** the selected row's shown cells in column order, tab-separated, on one line. **Copy address:** the address only. Results go to `actionStatus`: `DestinationCopy.Copied`, or `CopyFailed(type)`. Ctrl+C with no selection does nothing.
  - **Export destinations…** is enabled only in History while no export runs. In Live its tooltip is `DestinationCopy.ExportLiveOnly`, with `ShowOnDisabled` true.
    - The dialog follows Export CSV's rules, with the default name `destinations-{yyyyMMdd-HHmmss}.csv`, and it exports the whole-interval query with every filter, the application filter included.
    - It refuses an existing file with Export CSV's text and reports success with Export CSV's text.
    - `ConnectionHistoryLimitException` gives `DestinationCopy.ExportLimit`; any other failure gives Export CSV's failure text.

- [ ] **Step 1: Write the checks**

```csharp
// Call both from RunCore right after the Task 18 checks.
static void CheckDestinationsCopy(UiSession session, ConnectionLiveView live){ var page=new ConnectionsPage(session);Call(page,"ApplyLive",live);
    string? copied=null;PageType.GetField("SetClipboard",All)!.SetValue(page,(Action<string>)(t=>copied=t));
    // select 8.8.8.8, Ctrl+C (ApplicationCommands.Copy on the grid): copied is the row's cells joined by '\t' in column order, actionStatus "Copied to the clipboard.";
    // Copy address: copied "8.8.8.8"; a hook throwing ExternalException: actionStatus "Copy failed (ExternalException); try again."; no selection: Ctrl+C leaves copied null.
}
static void CheckDestinationsExport(UiSession session, DateTimeOffset now){ var page=new ConnectionsPage(session);var button=Card(page).Export;   // exportDestinations replaced by a fake recording (path, query, scope)
    // Live: button.IsEnabled false, ToolTip "Export is available in History.", ToolTipService.GetShowOnDisabled(button) true;
    // History: ExportDestinationsToPathAsync invoked by reflection (as CheckExportBusy, :270, invokes ExportToPathAsync) on a new path calls the fake once with the whole-interval
    //   query and the card's scope, then the success text; an existing file is refused with Export CSV's text;
    // while Export CSV's fake is pending the button is disabled, and Export CSV is disabled while this one's is pending;
    // a fake throwing ConnectionHistoryLimitException: "This interval holds too many saved rows to export destinations. Choose a shorter interval.".
}
```

- [ ] **Step 2: Build to verify failure.** Expected: `error CS1061` for `DestinationsCard.Export`, the toolbar button the checks read (`internal Button Export`).
- [ ] **Step 3: Implement copy, the row menu, export and the service method, and the preview check.** In `RenderShell`'s route loop, for "connections" (rendered from the existing Live fixture, `tools/OverviewPreview/Program.cs:236-244`, `:374`): read the page's `destinations` field and its `Rows` by reflection (no InternalsVisibleTo, as for `ProcessesPage.Grid`); add to `failures` unless there are 4 rows, each with Class "Special · documentation" and Location "—"; then bring the card into view and save `glass-shell-{theme}-connections-destinations.png`.
- [ ] **Step 4: Build, commit, push and verify pass**

```bash
git add tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs
git commit -m "test(connections): copy a destination and export destinations"
git add src/DeadWare.App/DestinationsCard.cs src/DeadWare.App/ConnectionsPage.cs src/DeadWare.App/ConnectionService.cs tools/OverviewPreview/Program.cs
git commit -m "feat(connections): copy destinations and Export destinations…"
```

Run (CI). Expected: green, the preview step included. The controller opens the run's `DeadWare-previews` artifact, checks `glass-shell-{theme}-connections-destinations.png` (the 203.0.113.x rows as "Special · documentation" with "—" locations) and notes in the PR what it shows.

---

## 0.16.0 — Release

### Task 20: 0.16.0 release docs and version

Spec: section 7 → Docs and release (0.16.0), Rollback (0.16 to 0.15), Pending by eye (0.16.0); each 0.16.0 section's Copy and docs / Docs and release.

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.16.0</Version>`)
- Modify: `CHANGELOG.md`: a new top section, `## 0.16.0 — Traffic strip and Destinations table`.
- Create: `docs/releases/v0.16.0.md`, in the shape of `docs/releases/v0.15.0.md`: bullets, then `## Verification`.
- Modify: `docs/validation.md`: a new top section, `## Traffic strip and Destinations table (0.16.0, YYYY-MM-DD)`, dated with the UTC date this step runs.
- Modify: `docs/user-guide.md`. Line numbers are at 75d6d65; anchor on the quoted text.
  - :36 (Features, Connections): add the traffic strip and the Destinations table.
  - :133: "Private/LAN, special and unlocated addresses remain in summary totals and saved-history CSV exports." becomes "Private/LAN, special and unlocated addresses are listed in the Destinations table (special-purpose ones under All only) and in saved-history CSV exports."
  - A new paragraph after :133 describes the table, the scope, the caps, the highlight, the hold and F5, copy and export, and that History lists saved minutes.
  - :135: after "IPv6 endpoints use `[address]:port` notation." add "The Destinations table lists bare addresses, since no port follows."
  - :137 (**History**): the strip, its cursor and the application filter.
  - :145: replace "Background writes target a 15-second flush interval and drain on normal **Quit DeadWare**. A crash or power loss can lose the unflushed batch; delayed or failed storage can extend that interval and produces incomplete/unavailable recording status." with the spec's paragraph exactly ("Each minute is saved as one row per application and destination shortly after it ends: …"). After "High traffic can evict older whole minutes earlier", add "(a steady 20 application–destination pairs a minute fill the cap in about 8–9 days; idle and light use keep the full 30 days)". Then add "Not kept before …" and when "Capacity shortened retention" now shows.
  - :169 (Applications): the strip replaces the share bar; Show in Connections; strip colours identify applications, not direction.
  - :173 (click a moment): the strip shares the cursor, and a click lists its slot in the Applications table.
  - :230 (Build): `-Version 0.15.0` becomes `-Version 0.16.0`.
- Modify: `README.md`, one line only: the "**See your connections:**" bullet (:22) adds "see when each app used the network and list every destination".

**Interfaces:**
- Consumes: Tasks 1–19 as merged on the branch. Re-read the touched code before writing: the docs describe the code, not this plan.

- [ ] **Step 1: Bump the version and write the CHANGELOG section.** One bullet each:
  - **Storage:** per-minute rows, about 60 times fewer than 0.15 at 1-second polling, and 30 days for idle and light use. Earlier rows stay until they expire, and the file does not shrink. A minute appears a few seconds after it ends. A crash can lose about a minute, instead of about 15 seconds. A small backward clock correction no longer loses history.
  - **The Unlocated fix:** History's unlocated count no longer includes local or special peers, matching Live.
  - **The strip** on both pages, Show in Connections and the application filter.
  - **The reworded Usage gap notice** and the narrower "Capacity shortened retention".
  - **Line charts** connect fully recorded slots that hold one minute start fewer than their length.
  - **The Destinations table.**
  - **Returning to 0.15:** it keeps reading the file and records per poll again; it goes back to its own Unlocated count; it ignores the saved scope, and returning to 0.16 opens the table on All.
- [ ] **Step 2: Write `docs/releases/v0.16.0.md`.** The same points as user-facing bullets, then `## Verification`. That section lists the automated coverage by area and the checks by eye as pending, and cites the CI measurements as measured.
- [ ] **Step 3: Write the `docs/validation.md` section:**
  - `### CI measurements (as measured, no threshold)`, with run links:
    - Task 3's rates, `fill.seconds` and the build job's duration against its 20-minute limit, and the eight reads with heap peaks;
    - Task 7's `strip30d` p50 and p95;
    - Task 15's `destinationStepsMs`;
    - Task 8's `usage-application-series.json`.
  - `### Linux harness`: the spec's retention table and timing table, attributed to the Linux container as measured there.
  - `### Findings`: the Unlocated finding of the 0.12.2 connections review (:832, "the history summary counting local peers as unlocated") is resolved; the monotonic `expiry` watermark finding stays open.
  - `### Automated coverage`: Core by class (`ConnectionMinuteFolderTests`, `TrafficStripTests`, `ConnectionDestinationsTests`, and the store, settings, address and projection tests), and App and UI checks on Windows CI by name.
  - `### Pending by eye`, never claimed as verified: the spec's 0.16.0 list, every item:
    - Connections storage: a 7-day History view after a week of per-minute recording shows "Reading saved history…" and then the view; the newest minute appears a few seconds after it ends.
    - The traffic strip: readable in all nine themes and in high contrast, including Mage's and Skyrim's closest pairs; the bottom band agrees with the top row of the Applications table; a large download shows as one tall band; the current minute at the right edge of Today is empty, not hatched, and its hover says "Not saved yet"; Show in Connections from the separate Usage history window brings the dashboard forward on Connections, with the slot and the app; on a file whose cap has shortened retention, a 30-day Connections view shows "Not kept before … · Capacity shortened retention"; Week and Month line charts connect across fully recorded fractional buckets.
    - The Destinations table: Live with a busy browser stays calm while the pointer rests on it; History over 30 days: a selected destination's node lights up; a LAN device appears under Local.
  - `### Results`: the build line, the Core suite on Linux (passed, skipped, the 26 baseline failures), and the id and head SHA of the green Windows CI run. The controller updates this line before merging.
- [ ] **Step 4: Check the docs**

```bash
grep -c '<Version>0.16.0</Version>' Directory.Build.props                              # 1
grep -c '^## 0.16.0 — Traffic strip and Destinations table$' CHANGELOG.md              # 1
grep -c '^## Traffic strip and Destinations table (0.16.0, ' docs/validation.md         # 1
grep -c 'publish.ps1 -Version 0.16.0' docs/user-guide.md                              # 1
grep -c 'Background writes target a 15-second flush interval' docs/user-guide.md       # 0
grep -c 'with a search box, a share bar and an \*\*Other applications\*\*' docs/user-guide.md   # 0 (the Applications paragraph; Application resources keeps its share bar)
grep -c 'Destinations table' docs/user-guide.md docs/releases/v0.16.0.md               # at least 1 each
grep -c 'Returning to 0.15' docs/releases/v0.16.0.md                                   # at least 1
grep -c 'Week and Month line charts connect across fully recorded fractional buckets' docs/validation.md   # 1
git diff --numstat origin/main -- README.md                                            # 1	1	README.md (one line changed)
```

Then run the build and the Core baseline. Expected: as in Global Constraints.
- [ ] **Step 5: Commit, push, verify and mark the PR ready**

```bash
git add Directory.Build.props CHANGELOG.md docs/releases/v0.16.0.md docs/validation.md docs/user-guide.md README.md
git commit -m "docs: 0.16.0 release notes, validation and version"
```

Run (CI). Expected: green. Mark the PR ready, confirm through the GitHub tools that GitHub has the commit, and report the PR link. Merging and publishing then follow Global Constraints → Merging and publishing (the user's standing go-ahead).
