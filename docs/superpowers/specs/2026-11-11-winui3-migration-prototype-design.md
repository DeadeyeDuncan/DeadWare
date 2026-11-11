# WinUI 3 migration: roadmap and measured prototype — design

Status: migration approach, look, Windows versions and the prototype design approved in conversation by the user on 2026-11-11. Awaiting review as a written spec. One implementation plan follows, for the prototype only.

Date: 2026-11-11. Anchor: `origin/main` at 92be2bb (0.17.13).

## Intent

**What the user said.** "I'd like to work on converting DeadWare to WinUI 3", for three reasons given together:

1. a native Windows 11 look and feel (Mica, Fluent controls, system animations);
2. performance (smoother charts and map, lower CPU or memory);
3. future-proofing (WPF seen as a dead end; move to Microsoft's current stack).

**Decisions the user made.**

- **Look: go native.** Mica backdrop, standard Fluent controls, system light/dark and accent colour. The seven palettes and the hand-built glass style are not recreated; palettes survive only as chart and accent colour sets.
- **Windows versions: Windows 11 first, Windows 10 best-effort.** The user runs no Windows 10 machine. The app targets Windows 10 19041 and later and falls back to a solid background where Mica is missing. No separate Windows 10 test path is built, and a Windows 10 visual flaw does not block a release.
- **Migration approach: a new WinUI app beside the WPF one** (approach A below).
- **Start with a measured prototype** before committing to the port.

**Assumptions (mine, not stated by the user).**

- Distribution stays a self-contained ZIP with the in-app updater; no MSIX or Store package.
- `DeadWare.Core`, `DeadWare.UpdateCore`, `DeadWare.Updater` and the data under `%LOCALAPPDATA%\DeadWare` keep their formats and behaviour.
- Success for the whole migration is feature parity with 0.17.x, with CI still building, testing and packaging.

**A caution recorded with the user.** WinUI 3 is not automatically faster than WPF. GPU-drawn charts should be smoother, but start-up time and baseline memory are often worse. DeadWare runs all day and 0.17.13 was a memory-growth fix, so goal 2 is checked by measurement first. That is what the prototype is for.

## What the migration touches

Measured on the anchor commit:

| Part | Size | Effect |
|---|---|---|
| `DeadWare.Core`, `DeadWare.UpdateCore`, `DeadWare.Updater` | 9.2k + 0.8k + 0.1k lines | No UI framework dependency. Unchanged. |
| `DeadWare.App` | 16.5k lines | WPF, built almost entirely in C#; `App.xaml` (338 lines of styles) is the only XAML. Rewritten. |
| `DeadWare.App.Tests` | 19.9k lines | Includes offscreen WPF UI checks. UI checks rewritten; service tests move with the services. |
| `tools/OverviewPreview` | — | WPF offscreen renders in CI. Replaced or retired at cutover. |

Parts of the app with no drop-in WinUI 3 equivalent:

- **Custom drawing.** Ten files draw through `OnRender`/`DrawingContext` (charts, traffic strip, connection map). WinUI 3 has no `OnRender`.
- **Tables.** About 50 `DataGrid`/`ListView`/`GridView` uses in six files. WinUI 3 has no built-in `DataGrid`.
- **Tray icon.** `TrayIcon.cs` uses the WinForms `NotifyIcon`. WinUI 3 has none.
- **Layout.** Custom panels with `MeasureOverride`/`ArrangeOverride` in seven files.
- **Packaging.** The ZIP must carry the Windows App SDK unpackaged and self-contained.
- **UI checks.** They rely on WPF laying out and rendering without a window.

## Migration approach

**A. New WinUI app beside the WPF one (chosen).** A new project is built in CI and not shipped. The WPF app keeps releasing 0.17.x fixes. Pages are ported one at a time. At parity, `scripts/publish.ps1` switches to the new app and the WPF project is deleted. Each step is a small pull request with a green build.

**B. Convert `DeadWare.App` in place on a long-lived branch (rejected).** Nothing compiles for weeks, CI gives no signal, and every WPF fix made meanwhile is a merge conflict.

**C. Host WinUI content inside the WPF window (rejected).** The hosting is fragile, and the Mica backdrop and native shell, which are the main point, would arrive last.

## Roadmap

Each sub-project gets its own spec, plan and pull requests. Only sub-project 0 is designed here; the others are listed so the order and boundaries are on record.

| # | Sub-project | Delivers |
|---|---|---|
| 0 | Measured prototype | Throwaway WinUI app; numbers against the WPF app; a go/no-go recommendation. |
| 1 | Service extraction | The non-UI parts of `DeadWare.App` (collector, ETW, history, alert and update services) move to a shared library both apps reference. Ships in a normal WPF release. |
| 2 | Shell | Window, navigation, tray, settings, light/dark/accent, Windows 10 fallback, self-contained packaging, CI, UI check harness. |
| 3 | Drawing kit | Charts, traffic strip and connection map on Win2D; the table control. |
| 4 | Pages | The twelve pages in batches, simplest first. |
| 5 | Pop-outs and widget | Detached graph windows and the desktop widget. |
| 6 | Cutover | Updater path from WPF to WinUI, the release, removal of the WPF project. |

Sub-projects 1 to 6 start only after the user reads the prototype's findings and says go.

## Sub-project 0: the measured prototype

### The question

Does a WinUI 3 + Win2D DeadWare start, idle and draw at a cost the user accepts, shipped the way DeadWare ships today (self-contained ZIP, no installer)?

The output is numbers and a recommendation. The code is throwaway: nothing in it is carried into sub-project 2 without being redesigned there.

### What is built

`tools/WinUiPrototype/`, a single WinUI 3 project. It is **not** added to `DeadWare.slnx`, so `dotnet test DeadWare.slnx` and `scripts/publish.ps1` neither build nor ship it.

- **Project settings.** `net10.0-windows10.0.19041.0`, minimum platform version 10.0.19041.0, `WindowsPackageType=None` (unpackaged), `WindowsAppSDKSelfContained=true`, x64. Package versions are the current stable Windows App SDK and Win2D for WinUI, pinned in the project file.
- **Window.** Mica backdrop when `MicaController.IsSupported()` is true, a solid theme background otherwise. Content extends into the title bar.
- **Navigation.** A `NavigationView` with two entries, **Overview** and **Empty**, so navigation cost and an idle page can both be observed. System light/dark and accent are followed; there is no theme picker.
- **Overview page.** Six cards in two columns, each holding one live chart drawn in a Win2D `CanvasControl`. Each chart draws what `HistoryChart` (`src/DeadWare.App/Charts.cs:148`) draws today: a grid, axis labels, one filled line trace, and gaps for missing samples. Each card also shows a current reading as text.
- **Data.** A synthetic generator produces one sample per chart per second and keeps the last 600. No hardware monitoring, ETW, storage or network. Charts redraw once per sample and not otherwise.
- **Tray.** An icon through `Shell_NotifyIcon` by P/Invoke, with no library. Left click opens the window; the menu has **Open** and **Quit**. Closing the window hides it to the tray. While hidden, the generator keeps running and nothing is drawn.
- **Errors.** An unhandled exception writes its text to a file beside the measurement output and exits non-zero. There is no other error handling; this is not a product.

### How it is measured

`scripts/measure-ui.ps1` launches an executable and samples the process from outside, so the same script measures both apps and neither needs measurement code.

Parameters: the executable path, its arguments, a label, and the output file.

It records, as one JSON object under `artifacts/measurements/`:

- **Start-up:** milliseconds from process start to a visible main window.
- **Visible steady state:** working set and private bytes after 60 seconds with the window shown, and CPU seconds used during those 60 seconds.
- **Hidden steady state:** the same three figures after a further 60 seconds with the window hidden. The script hides the window by posting a close message; if the process exits instead of hiding, the figures are recorded as null with the reason.
- **Machine:** Windows build, processor name, whether a hardware GPU adapter is present, and the display scale.

Each figure is taken three times in separate launches and all three values are kept, not an average alone.

Package size is recorded separately: bytes of the published folder and of its ZIP.

### What it is compared against

- **Like-for-like:** `tools/OverviewPreview` run interactively. It is the production WPF Overview with six cards on synthetic readings, so the two sides differ in UI framework and not in sensor work. It has no tray, so its hidden figures are null.
- **Reference only:** the real 0.17.13 app, for the hidden-to-tray figures and for package size. It does real sensor work, so the findings label this row as not like-for-like.

### Where it runs

- **CI.** A new `prototype` job in `.github/workflows/build.yml` on `windows-latest`: publish the prototype, run the script against it and against `tools/OverviewPreview`, record package size, upload the JSON. It runs only when files under `tools/WinUiPrototype/` or `scripts/measure-ui.ps1` change, and does not gate the existing `build` job.
- **The user's hardware.** CI runners have no GPU, so Win2D falls back to software rendering there and the chart CPU figures are pessimistic. The figures that decide goal 2 come from the user running one documented command on their own machine and returning the JSON. `tools/WinUiPrototype/README.md` holds that command.

### Risks the prototype also settles

Each is reported as worked, or failed with what was tried:

1. A self-contained, unpackaged Windows App SDK app builds and publishes from the `dotnet` CLI on a CI runner.
2. Win2D runs in that unpackaged app.
3. A tray icon by direct Win32 calls coexists with a WinUI 3 window, including hide-to-tray and quit.
4. The app starts on a machine without Mica and shows the solid fallback. On CI this is checked by forcing the fallback path, since no Windows 10 runner is used.

### Deliverable

`docs/superpowers/specs/2026-11-11-winui3-prototype-findings.md`, holding:

- both result sets (CI and the user's hardware) in one table per measure;
- the outcome of the four risks;
- a go/no-go recommendation with the reasoning.

No pass/fail thresholds are fixed in advance. The user decides from the numbers.

### Not in the prototype

Real sensor data; tables; the connection map; settings; pop-outs and the widget; palettes; localisation; UI tests beyond the measurement run; any change to the shipped app, its version or its release notes.

### Verification limits

This design was written in a Linux session that cannot build or run Windows UI. Everything in the prototype is verified through the `windows-latest` CI job and the user's machines, and the findings say which figures came from which.
