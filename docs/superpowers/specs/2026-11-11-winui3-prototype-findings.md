# WinUI 3 measured prototype — findings

Date: 2026-11-12. Spec: `2026-11-11-winui3-migration-prototype-design.md`. Plan: `../plans/2026-11-11-winui3-prototype.md`. Code: `tools/WinUiPrototype*`, `scripts/measure-ui.ps1`, `.github/workflows/prototype.yml`.

## Recommendation

**Go, with one condition.** Every feasibility risk was settled in favour of WinUI 3, the user confirmed the window, backdrop and tray by eye on their own Windows 11 machine, and nothing measured counts against the migration.

The condition is about the performance goal. **Performance was measured only on a GitHub runner with no GPU.** The user chose to skip the run on their own hardware. The figures below favour WinUI 3 by a wide margin, but software rendering inflates the WPF side and the two apps are not equal in complexity, so they show direction and not size. The measuring script should be run on real hardware against the real shell at the end of sub-project 2, before the twelve pages are ported. That is one command and costs nothing until then.

Goals 1 (native Windows 11 look) and 3 (current Microsoft stack) do not depend on these numbers.

## What was measured, and where

All figures are from the `WinUI prototype` workflow, run 43295670372, commit f1f0925.

- **Machine:** Microsoft Windows Server 2025 Datacenter 10.0.26100.0, AMD EPYC 7763, video adapter "Microsoft Hyper-V Video" (no hardware GPU, so all drawing was software-rendered), display scale 100%.
- **Method:** `scripts/measure-ui.ps1` launches the executable and samples the process from outside. Start-up is the time from process start to a visible main window. Memory and CPU are taken over 60 seconds with the window shown, then over 60 seconds after `WM_CLOSE`. Three launches per configuration; all three values are shown.
- **MB** below means 1,048,576 bytes.

### Configurations

| Label | What it is |
|---|---|
| WinUI 1 Hz | The prototype as specified: six Win2D charts, one redraw per chart per second, Mica backdrop. |
| WinUI solid | The same with `--no-mica`, the fallback used where Mica is unavailable. |
| WinUI smooth | `--smooth`: draws once a second and has the compositor slide each drawn chart between samples. |
| WinUI every-frame | `--every-frame`: redraws every chart on every frame. |
| WPF preview | `tools/OverviewPreview`: the production WPF Overview on synthetic readings. |

`--smooth` and `--every-frame` were added during the work and are not in the spec. The WPF charts animate a transform on a drawn chart between samples (`src/DeadWare.App/Charts.cs:380`), so a prototype that only redraws once a second was not a like-for-like CPU comparison. **`WinUI smooth` is the row to compare with `WPF preview`.**

### Start-up (ms)

| Configuration | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| WinUI 1 Hz | 853 | 522 | 509 |
| WinUI solid | 492 | 478 | 481 |
| WinUI smooth | 485 | 475 | 481 |
| WinUI every-frame | 463 | 464 | 465 |
| WPF preview | 3372 | 1175 | 1164 |

The first launch of each executable on the runner is slower than the rest (853 and 3372), as expected for a cold file cache.

### Window shown, 60 seconds

| Configuration | Private memory (MB) | Working set (MB) | CPU (seconds) |
|---|---|---|---|
| WinUI 1 Hz | 56.7 / 59.8 / 60.7 | 139.5 / 140.1 / 140.7 | 1.234 / 1.141 / 1.219 |
| WinUI solid | 59.6 / 59.1 / 57.6 | 139.5 / 139.5 / 139.2 | 1.016 / 0.938 / 0.922 |
| WinUI smooth | 59.0 / 58.1 / 56.9 | 143.3 / 143.2 / 142.5 | 14.391 / 14.359 / 14.297 |
| WinUI every-frame | 46.6 / 47.9 / 46.5 | 134.3 / 134.3 / 134.2 | 15.141 / 15.641 / 15.312 |
| WPF preview | 214.9 / 213.8 / 213.7 | 295.7 / 292.9 / 289.4 | 204.891 / 205.5 / 203.062 |

### Window hidden to the tray, 60 seconds

| Configuration | Private memory (MB) | Working set (MB) | CPU (seconds) |
|---|---|---|---|
| WinUI 1 Hz | 58.5 / 59.8 / 57.2 | 123.8 / 123.4 / 131.5 | 0.0 / 0.0 / 0.016 |
| WinUI solid | 59.6 / 59.0 / 57.6 | 123.3 / 126.3 / 122.7 | 0.0 / 0.0 / 0.016 |
| WinUI smooth | 58.7 / 58.8 / 57.6 | 127.9 / 127.2 / 124.9 | 0.016 / 0.016 / 0.0 |
| WinUI every-frame | 47.5 / 47.3 / 48.0 | 119.9 / 118.8 / 118.1 | 0.859 / 0.531 / 0.703 |
| WPF preview | not measured | not measured | not measured |

The WPF preview has no tray and exits on close, so it has no hidden figures. The installed 0.17.13 app, which would have supplied a reference, was not measured.

### Package size

| Package | Folder | ZIP |
|---|---|---|
| WinUI prototype (self-contained, Windows App SDK 2.6.5, Win2D 1.4.0) | 247,108,454 bytes (235.7 MB) | 98,539,649 bytes (94.0 MB) |
| DeadWare 0.17.13 release | not measured | 121,881,999 bytes (116.2 MB) |

The prototype contains none of the app's own code, maps, fonts or dependencies. Before Win2D was added the shell alone was 244,162,999 bytes unpacked and 97,557,800 zipped, so nearly all of the size is the .NET runtime and the Windows App SDK. The real WinUI package will be larger than 94 MB; by how much is not known from this work.

## How far the comparison can be trusted

**The tables above predate two corrections and flatter WinUI.** An independent review of the branch found that the prototype's charts started empty (at most 60 of 600 points drawn during a measurement, against a full window in WPF) and that sampling began the instant the window appeared. Both are fixed in the code: charts now start full, and the script waits 10 seconds before sampling. The user then chose to stop the prototype work, so the tables were not regenerated. Expect the prototype's CPU and memory to be higher than shown. The `WinUI prototype` workflow's latest run holds figures taken after the fixes.

1. **No GPU.** WPF software-renders whole frames on the CPU, and its 204 CPU-seconds in 60 (more than three cores) is that. On a machine with a GPU the WPF figure will be far lower. The WinUI figures would also change. The ratio between them on real hardware is unknown.
2. **Unequal complexity.** The WPF preview is the production Overview: nine charts with several traces each (per-core lines, drives, glow copies, splines, time ticks) against six single-trace charts, six hours of history held in memory, glass styling, theme controls, and the whole `DeadWare.App` assembly loaded. The prototype is a plain shell with six cards. Memory and CPU are comparable in order of magnitude only.
3. **Different scrolling distances.** The prototype keeps 600 samples per chart; the preview shows 300 seconds. The per-second slide is under two pixels in both, so this should not matter much, but it was not equalised.
4. **In-process cost only.** The script reads the measured process. Work done by the desktop compositor on behalf of either app is not attributed to it.
5. **Start-up means first visible window, not first drawn content.** WinUI shows its window before laying it out; the WPF preview builds its view first. This flatters WinUI by an unknown amount.
6. **Other differences.** The preview is a framework-dependent build output, the prototype a self-contained publish; the windows differ in size (1060 x 970 against 1220 x 900); the preview was always measured last; `--every-frame` redraws six separate canvases and is an upper bound, not a design.
7. **Three launches, one machine.** Enough to show the figures are stable run to run; not a benchmark.

What the numbers do support: nothing here argues against WinUI 3 on performance, and its cost when hidden to the tray was close to zero. They do not establish how much faster or lighter a real WinUI DeadWare would be than the WPF one.

## The four risks

| Risk | Outcome |
|---|---|
| 1. A self-contained, unpackaged Windows App SDK app builds and publishes from the `dotnet` CLI on a CI runner. | **Worked, with a fix.** `dotnet publish` left out the app's own `App.xbf`, `MainWindow.xbf` and `<assembly>.pri`, and the published app failed at start-up with `XamlParseException`; the plain build output ran. A target in `WinUiPrototype.csproj` copies them after publish. `scripts/publish.ps1` will need the same at cutover. |
| 2. Win2D runs in that unpackaged app. | **Worked.** Six `CanvasControl` charts drew 60 times in 10 seconds at one sample a second, and 1,866 times in 10 seconds with `--every-frame` (about 31 frames a second each, software-rendered). |
| 3. A tray icon by direct Win32 calls coexists with a WinUI 3 window. | **Worked.** On CI the icon was added, close hid the window and the process stayed alive. The user confirmed on Windows 11: icon present, hide and restore, the Open/Quit menu and its dismissal on an outside click, and Quit ending the process. The helper window had to be a hidden top-level window; a message-only window cannot take the foreground, which the menu needs in order to dismiss. |
| 4. The app starts without Mica and shows the solid fallback. | **Worked with the fallback forced** (`--no-mica`). The runner itself supports Mica, and the user confirmed Mica by eye. No Windows 10 machine was used, in line with Windows 10 being best-effort. |

## Open items

- **One unexplained failure.** An early version of the per-frame mode, whose exit timer ran on the UI thread and called `Environment.Exit`, never exited on the runner within 40 seconds. A starved UI thread was suspected and then ruled out: with per-frame redraw the one-second sample timer still fired 9 times in 10 seconds. The failure stopped when the exit timer moved to the thread pool. The cause was not identified. The tray's Quit also exits from the UI thread; the user reported that Quit worked, but did not say whether it was tried with `--every-frame` running. Sub-project 2 should shut the app down through the normal WinUI exit path and test quitting while charts animate.
- **The performance measurement on real hardware** is still owed, as the condition above says.
- **Review items left as they are:** `CompositionTarget.Rendering` stays subscribed while the window is hidden (which may explain the 0.5 to 0.9 CPU-seconds `--every-frame` used when hidden); the script's writability probe leaves a zero-byte file if a run is interrupted; exiting from the UI thread is the likely cause of the unexplained hang above.
- **Tables and the connection map** were out of scope and remain the largest unknowns for sub-project 3.

## What happens to the prototype

It stays in `tools/` until sub-project 2 replaces it, so the measuring script has something to run against. Nothing in it is carried forward without being redesigned. `scripts/measure-ui.ps1` is general and worth keeping.
