# WinUI 3 Measured Prototype Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a throwaway WinUI 3 + Win2D app and a measuring script, and produce start-up, memory, CPU and package-size numbers against the WPF Overview, ending in a findings note with a go/no-go recommendation.

**Architecture:** Pure logic (sample buffer, synthetic data, chart geometry) lives in a plain `net10.0` library with unit tests, so it can be tested on any OS. The WinUI app is a thin shell over it: Mica window, `NavigationView`, six Win2D charts, a Win32 tray icon. A PowerShell script measures any executable from outside the process; a dedicated workflow publishes the prototype and runs the script on `windows-latest`.

**Tech Stack:** .NET 10, Windows App SDK 2.6.5 (unpackaged, self-contained), Microsoft.Graphics.Win2D 1.4.0, xunit 2.9.3, PowerShell 7, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-11-winui3-migration-prototype-design.md`

## Global Constraints

- Nothing under `src/`, `tests/`, `DeadWare.slnx`, `scripts/publish.ps1`, `Directory.Build.props` or `.github/workflows/build.yml` changes. The shipped app, its version and its release notes are untouched.
- The prototype projects are **not** added to `DeadWare.slnx`.
- App project: `net10.0-windows10.0.19041.0`, `TargetPlatformMinVersion` 10.0.19041.0, `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, x64 only.
- Package versions pinned exactly: `Microsoft.WindowsAppSDK` 2.6.5, `Microsoft.Graphics.Win2D` 1.4.0.
- No hardware monitoring, ETW, storage or network in the prototype. Data is synthetic: one sample per chart per second, last 600 kept.
- Tray icon by `Shell_NotifyIcon` P/Invoke; no tray library.
- No pass/fail performance thresholds anywhere. Checks assert that measurements were *taken*, never that they are good.
- Measurement JSON goes under `artifacts/measurements/` (already git-ignored).
- The code is throwaway. Do not polish, generalise or add settings.
- This plan was written on Linux. Tasks 2 to 5 can only be verified by the `WinUI prototype` workflow run on the pull request; expect to iterate on CI output, and record each fix in the commit message.
- Docs-only commits carry `[skip ci]`; commits that touch `tools/WinUiPrototype*`, `scripts/measure-ui.ps1` or the workflow must not.

**One deviation from the spec, for the user to confirm:** the spec puts a `prototype` job in `build.yml` that runs only when prototype files change. GitHub applies path filters per workflow, not per job, so this plan uses a separate file, `.github/workflows/prototype.yml`. The effect the spec asks for is the same: it runs only on prototype changes and does not gate the `build` job.

## Review Focus

1. **A launch that never shows a window** (the WPF preview shows a modal error box on failure): the script must give up after its start-up timeout, kill the process, record `startupMs: null` with a reason, and exit, not hang the job. Test in Task 5.
2. **A process that exits on close instead of hiding** (the WPF preview has no tray): hidden figures are `null` with `hiddenReason`, and the run still counts. Test in Task 5.
3. **A leftover process from an earlier run** (a hidden-to-tray prototype stays alive): the script ends every run by killing the process it started, so run 2 never measures beside run 1. Test in Task 5.
4. **Degenerate chart input** (empty series, all samples missing, a flat series where min equals max, zero-sized canvas): geometry returns no `NaN` or infinite coordinate and never throws. Tests in Task 1.
5. **Paths with spaces** in the executable or output path (the user's profile folder may contain one): the script quotes correctly. Test in Task 5.

---

## File Structure

| File | Responsibility |
|---|---|
| `tools/WinUiPrototype.Logic/WinUiPrototype.Logic.csproj` | `net10.0` class library, no dependencies. |
| `tools/WinUiPrototype.Logic/SampleSeries.cs` | Fixed-capacity buffer of nullable samples. |
| `tools/WinUiPrototype.Logic/SyntheticSource.cs` | Deterministic sample generator with periodic gaps. |
| `tools/WinUiPrototype.Logic/ChartGeometry.cs` | Turns a series into point runs split at gaps. |
| `tools/WinUiPrototype.Tests/` | xunit tests for the three logic files. |
| `tools/WinUiPrototype/WinUiPrototype.csproj` | The WinUI 3 app project. |
| `tools/WinUiPrototype/App.xaml(.cs)` | Start-up, argument parsing, error file. |
| `tools/WinUiPrototype/MainWindow.xaml(.cs)` | Backdrop, title bar, navigation, hide-to-tray. |
| `tools/WinUiPrototype/OverviewPage.cs` | Six cards, the one-second timer. |
| `tools/WinUiPrototype/ChartCard.cs` | One card: title, reading, Win2D chart. |
| `tools/WinUiPrototype/Tray.cs` | `Shell_NotifyIcon` wrapper and its message window. |
| `tools/WinUiPrototype/README.md` | What it is, that it is throwaway, the command the user runs. |
| `scripts/measure-ui.ps1` | Launch, sample, write JSON. |
| `.github/workflows/prototype.yml` | Publish, measure, upload. |
| `docs/superpowers/specs/2026-11-11-winui3-prototype-findings.md` | The deliverable. |

---

### Task 1: Logic library and its tests

**Files:**
- Create: `tools/WinUiPrototype.Logic/WinUiPrototype.Logic.csproj`, `SampleSeries.cs`, `SyntheticSource.cs`, `ChartGeometry.cs`
- Create: `tools/WinUiPrototype.Tests/WinUiPrototype.Tests.csproj`, `LogicTests.cs`

**Interfaces:**
- Produces (namespace `WinUiPrototype.Logic`):
  - `public sealed class SampleSeries(int capacity)` with `void Add(double? value)`, `int Count`, `int Capacity`, `double? this[int index]` (0 is the oldest kept sample), `double? Latest` (null when empty or when the newest sample is missing).
  - `public sealed class SyntheticSource(int seed)` with `double? Next()`. Values are in 0 to 100. Every 97th call returns null. The same seed gives the same sequence.
  - `public static class ChartGeometry` with `IReadOnlyList<IReadOnlyList<(float X, float Y)>> Runs(SampleSeries series, float width, float height, double min, double max)`.

Test project settings: `net10.0`, `Microsoft.NET.Test.Sdk` 18.11.0, `xunit` 2.9.3, `xunit.runner.visualstudio` 4.0.1 (the versions `tests/DeadWare.Tests` uses), one `ProjectReference` to the logic project.

- [ ] **Step 1: Write the failing tests** in `LogicTests.cs`:

```csharp
[Fact] public void Series_keeps_only_the_newest_capacity_samples()
{
    var s = new SampleSeries(3);
    foreach (var v in new double?[] { 1, 2, 3, 4 }) s.Add(v);
    Assert.Equal(3, s.Count); Assert.Equal(2, s[0]); Assert.Equal(4, s[2]); Assert.Equal(4, s.Latest);
}
[Fact] public void Series_latest_is_null_when_empty_or_missing()
{
    var s = new SampleSeries(3); Assert.Null(s.Latest);
    s.Add(5); s.Add(null); Assert.Null(s.Latest); Assert.Equal(2, s.Count);
}
[Fact] public void Source_is_deterministic_in_range_and_gaps_every_97th()
{
    var a = new SyntheticSource(7); var b = new SyntheticSource(7);
    for (int call = 1; call <= 300; call++)
    {
        var x = a.Next(); Assert.Equal(x, b.Next());
        if (call % 97 == 0) Assert.Null(x); else Assert.InRange(x!.Value, 0, 100);
    }
}
[Fact] public void Runs_split_at_gaps_and_newest_sits_at_the_right_edge()
{
    var s = new SampleSeries(5);
    foreach (var v in new double?[] { 0, 100, null, 50, 50 }) s.Add(v);
    var runs = ChartGeometry.Runs(s, 400, 100, 0, 100);
    Assert.Equal(2, runs.Count); Assert.Equal(2, runs[0].Count); Assert.Equal(2, runs[1].Count);
    Assert.Equal((0f, 100f), runs[0][0]);    // oldest, value 0, bottom-left
    Assert.Equal((100f, 0f), runs[0][1]);    // value 100, top
    Assert.Equal((400f, 50f), runs[1][1]);   // newest at the right edge
}
[Fact] public void Runs_of_a_part_filled_series_stay_right_aligned()
{
    var s = new SampleSeries(5); s.Add(10); s.Add(10);
    var runs = ChartGeometry.Runs(s, 400, 100, 0, 100);
    Assert.Equal(300f, runs[0][0].X); Assert.Equal(400f, runs[0][1].X);
}
[Theory]
[InlineData(0f, 100f)] [InlineData(400f, 0f)] [InlineData(-1f, 100f)]
public void Runs_are_empty_for_a_canvas_with_no_area(float w, float h)
{
    var s = new SampleSeries(5); s.Add(1); s.Add(2);
    Assert.Empty(ChartGeometry.Runs(s, w, h, 0, 100));
}
[Fact] public void Runs_are_empty_for_an_empty_or_all_missing_series()
{
    var s = new SampleSeries(5); Assert.Empty(ChartGeometry.Runs(s, 400, 100, 0, 100));
    s.Add(null); s.Add(null); Assert.Empty(ChartGeometry.Runs(s, 400, 100, 0, 100));
}
[Fact] public void Runs_of_a_flat_range_sit_on_the_middle_line_and_are_finite()
{
    var s = new SampleSeries(5); s.Add(5); s.Add(5);
    var runs = ChartGeometry.Runs(s, 400, 100, 5, 5);
    Assert.All(runs[0], p => { Assert.Equal(50f, p.Y); Assert.True(float.IsFinite(p.X)); });
}
[Fact] public void Runs_clamp_values_outside_the_range()
{
    var s = new SampleSeries(2); s.Add(-50); s.Add(500);
    var runs = ChartGeometry.Runs(s, 400, 100, 0, 100);
    Assert.Equal(100f, runs[0][0].Y); Assert.Equal(0f, runs[0][1].Y);
}
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test tools/WinUiPrototype.Tests` — expected: build errors naming the three missing types.

- [ ] **Step 3: Implement the three types.** Geometry rules the tests pin: horizontal spacing is `width / (Capacity - 1)`; the newest sample is at `X = width` and older samples step left; `Y = height - (value - min) / (max - min) * height`, clamped to 0..height; when `max <= min`, `Y = height / 2`; a run needs no minimum length (a single isolated sample is a run of one). A capacity of 1 places its only sample at `X = width`.

- [ ] **Step 4: Run to verify they pass.** `dotnet test tools/WinUiPrototype.Tests` — expected: all tests pass. This runs on Linux.

- [ ] **Step 5: Commit** — `feat(prototype): sample buffer, synthetic source and chart geometry`.

---

### Task 2: App shell and the workflow that publishes it

**Files:**
- Create: `tools/WinUiPrototype/WinUiPrototype.csproj`, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `app.manifest`
- Create: `.github/workflows/prototype.yml`

**Interfaces:**
- Consumes: the logic project by `ProjectReference`.
- Produces:
  - Executable `DeadWare.WinUiPrototype.exe` (`AssemblyName`).
  - Command line: `--no-mica` forces the solid fallback; `--error-file <path>` names where an unhandled exception's text is written before exiting with code 1. Unknown arguments are ignored.
  - `MainWindow` exposes `void ShowFromTray()` and `void QuitFromTray()` (bodies arrive in Task 4; in this task `ShowFromTray` activates the window and `QuitFromTray` exits the app) and a `Frame ContentFrame` that pages are placed in.
  - Workflow artifact `WinUiPrototype-win-x64` (the published folder) and a job-summary line `Published size: <bytes> bytes folder, <bytes> bytes ZIP`.

Decisions:
- Project properties beyond Global Constraints: `OutputType=WinExe`, `UseWinUI=true`, `Platforms=x64`, `RuntimeIdentifier=win-x64`, `EnableMsixTooling=false`, `PublishTrimmed=false`, `PublishSingleFile=false`, `ApplicationManifest=app.manifest` (per-monitor-v2 DPI aware), icon `../../assets/deadware.ico`.
- Backdrop: if `--no-mica` is absent and `MicaController.IsSupported()` is true, set `SystemBackdrop = new MicaBackdrop()`. Otherwise leave the backdrop unset and give the root grid the `ApplicationPageBackgroundThemeBrush` background. The window title states which: `DeadWare prototype (Mica)` or `DeadWare prototype (solid)`. The measuring script and a person can both read that.
- `ExtendsContentIntoTitleBar = true`.
- `NavigationView` in left mode, settings entry hidden, two items: **Overview** (selected at start) and **Empty**. In this task both show an empty page; Overview gets content in Task 3.
- Initial window size 1220 x 900 DIPs, the WPF shell's capture size.
- Workflow `WinUI prototype`: triggers `pull_request` and `push` to `main`, both filtered to `tools/WinUiPrototype/**`, `tools/WinUiPrototype.Logic/**`, `tools/WinUiPrototype.Tests/**`, `scripts/measure-ui.ps1` and `.github/workflows/prototype.yml`; plus `workflow_dispatch`. `permissions: contents: read`. One job on `windows-latest`, 30-minute timeout, using the same pinned `actions/checkout`, `actions/setup-dotnet` (10.0.402) and `actions/upload-artifact` SHAs as `build.yml`. Steps: test the logic project; `dotnet publish tools/WinUiPrototype -c Release -r win-x64 --self-contained true -o artifacts/prototype/DeadWare.WinUiPrototype`; zip it; write the size line to `$GITHUB_STEP_SUMMARY`; upload the folder.

- [ ] **Step 1: Write the smoke check first, as a workflow step** after publish: start the exe with `--error-file artifacts/prototype/error.txt`, wait up to 60 seconds for a process main window whose title starts with `DeadWare prototype (`, fail the step if none appears or if the error file exists, then stop the process. Repeat with `--no-mica` and require the title `DeadWare prototype (solid)`.

- [ ] **Step 2: Push with the app project still absent and confirm the workflow fails** at publish with a missing-project error. (This proves the workflow triggers and the check can fail.)

- [ ] **Step 3: Create the project and the five source files** per the decisions above.

- [ ] **Step 4: Push and read the run.** Expected: logic tests pass, publish succeeds, both smoke checks pass, the summary shows the two sizes. If publish fails, fix from the error text; this step is where spec risks 1 and 4 are settled, so record in the commit message what had to change.

- [ ] **Step 5: Commit** — `feat(prototype): WinUI shell with Mica fallback and its workflow`.

---

### Task 3: Overview page with six Win2D charts

**Files:**
- Create: `tools/WinUiPrototype/OverviewPage.cs`, `tools/WinUiPrototype/ChartCard.cs`
- Modify: `tools/WinUiPrototype/WinUiPrototype.csproj` (add Win2D), `MainWindow.xaml.cs` (navigate Overview to `OverviewPage`)

**Interfaces:**
- Consumes: `SampleSeries`, `SyntheticSource`, `ChartGeometry.Runs` from Task 1; `ContentFrame` from Task 2.
- Produces:
  - `internal sealed class ChartCard : UserControl` with constructor `(string title, string unit, int seed)` and `void Tick()` (take one sample, update the reading text, invalidate the canvas).
  - `internal sealed class OverviewPage : Page` with `void SetDrawing(bool enabled)`; while disabled, `Tick` still samples but does not invalidate. Task 4 calls it on hide and show.
  - `OverviewPage.FramesDrawn` (`static long`, incremented in every canvas draw handler), used by the smoke check.

Decisions:
- Six cards in a 2-column, 3-row grid, 12 DIP gaps, titles and units: CPU `%`, GPU `%`, Memory `%`, Disk `%`, Download `Mb/s`, Upload `Mb/s`; seeds 1 to 6. Each card uses `CardBackgroundFillColorDefaultBrush`, 8 DIP corner radius, 16 DIP padding.
- Each card: title, the reading (`Latest` to one decimal with the unit, or `—` when null) in the `TitleTextBlockStyle`, and a `CanvasControl` filling the rest.
- Each chart draws, in order: four horizontal grid lines; axis labels `0` and `100` at the left; for each run, a closed fill down to the baseline at 25% opacity, then the line at 2 DIP width. A run of one point draws a 2 DIP dot. Range is fixed at 0 to 100. Line colour is the system accent colour, re-read on each draw.
- One `DispatcherQueueTimer` on the page at 1 second, started on `Loaded` and stopped on `Unloaded`, calls `Tick` on all six cards. There is no other redraw trigger besides resize.
- Capacity 600 per series.

- [ ] **Step 1: Extend the workflow smoke check first:** add a hidden argument `--exit-after <seconds>` to the app that, when present, writes `OverviewPage.FramesDrawn` to `<error-file>.frames` and exits 0 after that many seconds. The check runs `--exit-after 10` and requires the frames file to hold a number of at least 30 (six charts, several ticks). Add the same run with `--no-mica`.

- [ ] **Step 2: Push and confirm the check fails** because the frames file is absent.

- [ ] **Step 3: Implement** `ChartCard`, `OverviewPage`, the `--exit-after` argument, and the navigation wiring.

- [ ] **Step 4: Push and read the run.** Expected: frames check passes in both modes. This settles spec risk 2 (Win2D in an unpackaged app).

- [ ] **Step 5: Commit** — `feat(prototype): Overview page with six live Win2D charts`.

---

### Task 4: Tray icon and hide-to-tray

**Files:**
- Create: `tools/WinUiPrototype/Tray.cs`
- Modify: `tools/WinUiPrototype/MainWindow.xaml.cs`, `App.xaml.cs`

**Interfaces:**
- Consumes: `MainWindow.ShowFromTray()`, `MainWindow.QuitFromTray()`, `OverviewPage.SetDrawing(bool)`.
- Produces: `internal sealed class Tray : IDisposable` with constructor `(Action open, Action quit)`; it owns a message-only window, adds the icon with tooltip `DeadWare prototype`, and removes it on `Dispose`.

Decisions:
- Left click and double click call `open`. Right click shows a native popup menu (`CreatePopupMenu`/`TrackPopupMenuEx`) with **Open** and **Quit**; call `SetForegroundWindow` on the message window first so the menu dismisses on an outside click.
- The icon is `assets/deadware.ico`, copied to the output and loaded with `LoadImage`.
- Closing the main window (`AppWindow.Closing`) is cancelled and replaced by `AppWindow.Hide()` plus `SetDrawing(false)`. `ShowFromTray` shows, activates and calls `SetDrawing(true)`. `QuitFromTray` disposes the tray, then exits the process with code 0. Nothing else may keep the process alive after Quit.
- If adding the icon fails, write the Win32 error to the error file and continue without a tray; closing the window then exits. (A machine with no shell must still be measurable.)

- [ ] **Step 1: Extend the workflow smoke check first:** start the app, wait for the main window, post `WM_CLOSE` to it, wait 5 seconds, and require that the process is still running and `IsWindowVisible` is false for that handle. Then stop the process and require that no `DeadWare.WinUiPrototype` process remains.

- [ ] **Step 2: Push and confirm the check fails** because the process exits on close.

- [ ] **Step 3: Implement** `Tray` and the close, show and quit wiring.

- [ ] **Step 4: Push and read the run.** Expected: the check passes. This settles spec risk 3 as far as CI can see it; the menu itself needs the user's eyes and is listed in the README (Task 6).

- [ ] **Step 5: Commit** — `feat(prototype): Win32 tray icon with hide-to-tray`.

---

### Task 5: The measuring script and the measurement runs

**Files:**
- Create: `scripts/measure-ui.ps1`
- Modify: `.github/workflows/prototype.yml`

**Interfaces:**
- Produces: `scripts/measure-ui.ps1` with parameters
  `-Exe <path>` (required), `-Arguments <string[]>` (default none), `-Label <string>` (required), `-Out <path>` (required), `-Runs <int>` (default 3), `-SteadySeconds <int>` (default 60), `-StartupTimeoutSeconds <int>` (default 60).
  Exit code 0 when the file was written, even if a run recorded nulls; non-zero only for bad parameters or an unwritable output path.
- Output JSON shape:

```json
{
  "label": "winui-prototype",
  "exe": "DeadWare.WinUiPrototype.exe",
  "steadySeconds": 60,
  "machine": { "osBuild": "…", "processor": "…", "videoAdapters": ["…"], "hardwareGpu": false, "scalePercent": 100 },
  "runs": [
    { "startupMs": 812, "startupReason": null,
      "visible": { "workingSetBytes": 0, "privateBytes": 0, "cpuSeconds": 0.0 },
      "hidden": null, "hiddenReason": "process exited on close" }
  ]
}
```

Decisions:
- `exe` records the file name only, never the full path (a path can hold a user name).
- **Start-up:** start the process, poll every 25 ms for a main window handle for which `IsWindowVisible` is true; `startupMs` is the elapsed time from `Start()`. On timeout: `startupMs` null, `startupReason` `"no visible window within <n> s"`, `visible` and `hidden` null, kill, next run.
- **Visible:** record `TotalProcessorTime`, wait `SteadySeconds`, then record working set, private bytes, and the processor-time difference.
- **Hidden:** post `WM_CLOSE` to the main window and wait 5 seconds. If the process exited: `hidden` null, `hiddenReason` `"process exited on close"`. If still running with the window still visible: `hidden` null, `hiddenReason` `"window did not hide"`. Otherwise sample as for Visible over another `SteadySeconds`.
- **End of every run:** kill the started process and its children and wait for exit, in a `finally`.
- `hardwareGpu` is true when any `Win32_VideoController` name does not match `Basic|Hyper-V|Remote|Virtual`. All adapter names are recorded so the reader can judge.
- Refuse to start (non-zero exit, clear message) if a process with the executable's name is already running, so a stray instance cannot share the measurement.
- When `GITHUB_STEP_SUMMARY` is set, append a table with one row per run.

- [ ] **Step 1: Write the script's checks first, as workflow steps** using `-Runs 1 -SteadySeconds 5` so they take seconds:
  1. **No window** (Review Focus 1): `-Exe` is `ping.exe`, `-Arguments -n 30 127.0.0.1`, `-StartupTimeoutSeconds 5`. Require exit code 0, `runs[0].startupMs` null, `startupReason` present, and no `ping` process left.
  2. **Exits on close** (Review Focus 2): `-Exe` is `notepad.exe`. Require `startupMs` a positive number, `visible.privateBytes` positive, `hidden` null, `hiddenReason` `"process exited on close"`.
  3. **Hides, and leaves nothing behind** (Review Focus 3): the prototype with `-Runs 2`. Require both runs to have non-null `hidden`, and no `DeadWare.WinUiPrototype` process afterwards.
  4. **Spaces** (Review Focus 5): copy the published prototype to `artifacts/prototype/with space/` and write to `artifacts/measurements/with space/out.json`. Require the file to exist and parse.
  5. **Already running:** start the prototype, then run the script against it; require a non-zero exit and no output file.

- [ ] **Step 2: Push and confirm the five checks fail** because the script is absent.

- [ ] **Step 3: Write `scripts/measure-ui.ps1`.**

- [ ] **Step 4: Push and read the run.** Expected: all five checks pass.

- [ ] **Step 5: Add the real measurement steps** to the workflow, at the defaults (3 runs, 60 seconds):
  - prototype → `artifacts/measurements/ui-winui-prototype.json`
  - prototype with `--no-mica` → `ui-winui-prototype-solid.json`
  - `tools/OverviewPreview`, built with `dotnet build -c Release` and run with no arguments → `ui-wpf-overview-preview.json`

  Upload `artifacts/measurements/` as `WinUiPrototype-measurements` with `if: always()`. Raise the job timeout to 45 minutes.

- [ ] **Step 6: Push and read the run.** Expected: three JSON files, each with three runs; the two prototype files have non-null `hidden`; the WPF file has `hiddenReason` `"process exited on close"`. If the WPF preview records a start-up timeout, open its window title in the log to see whether it showed its error box, and report that in the findings instead of working around it.

- [ ] **Step 7: Commit** — `feat(prototype): external UI measuring script and CI measurement runs`.

---

### Task 6: README, the user's run, and the findings

**Files:**
- Create: `tools/WinUiPrototype/README.md`
- Create: `docs/superpowers/specs/2026-11-11-winui3-prototype-findings.md`

**Interfaces:**
- Consumes: the workflow artifacts from Tasks 2 and 5; the JSON the user returns.

- [ ] **Step 1: Write the README.** It states that this is a throwaway prototype for the spec above and is not shipped, then gives the user's steps in order:
  1. Download the `WinUiPrototype-win-x64` artifact from the latest `WinUI prototype` run and extract it.
  2. Quit DeadWare (the measurement should not share the machine with it).
  3. Run, from the repository root, the three commands that measure the prototype, the WPF preview (`dotnet build tools/OverviewPreview -c Release` first), and the installed 0.17.13 `DeadWare.exe` with `-Label wpf-installed`.
  4. Send back the three files from `artifacts/measurements/`.
  5. By eye, on the prototype: the Mica backdrop shows; the tray icon appears; left click opens; the right-click menu shows Open and Quit and dismisses on an outside click; Quit ends the process. Report each as yes or no.

  It also says the installed-app row is a reference and not like-for-like, because that app does real sensor work.

- [ ] **Step 2: Commit and push the README** (`docs(prototype): how to run the measurements [skip ci]`), then ask the user to do the run. **Stop here until the user returns the files.**

- [ ] **Step 3: Write the findings** with these sections, in this order: the recommendation (go, no-go, or go with named conditions) and its reasoning in one short paragraph; a table per measure (start-up, visible memory, visible CPU, hidden memory, hidden CPU, package size) with a row per app and columns for CI and the user's hardware, all three run values shown; the four risks, each as worked or failed with what was tried; what the numbers cannot show (no real sensor work, no tables or map, software rendering on CI). The WPF package size is the size of the published 0.17.13 release ZIP.

- [ ] **Step 4: Commit and push** (`docs: WinUI 3 prototype findings [skip ci]`), verify GitHub has the commit, and present the recommendation to the user. Sub-projects 1 to 6 do not start without the user's go.
