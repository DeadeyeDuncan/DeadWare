# Preview validation

DeadWare targets Windows 10/11 x64. Hardware discovery is dynamic, but compatibility has only been exercised on one development PC so far. This document records observed coverage, not a certification of other hardware.

## Observed hardware and UI

- AMD Ryzen 7 7700X CPU usage and Windows memory readings were available without elevation.
- AMD Radeon integrated graphics and an RX 6800 XT were discovered separately. The discrete GPU supplied a real core temperature. The integrated GPU's VR SoC sensor is not substituted for core temperature.
- Inaccessible CPU temperature readings remained unavailable rather than showing a misleading zero.
- Aggregate network rates worked without elevation; per-app tracing correctly displayed the administrator requirement.
- Published WPF dashboard, GPU selection, all five themes, live theme changes in the widget, CPU and combined network graph pop-outs, history-range changes and always-on-top controls were exercised. Chart labels are no longer clipped. Closing the main window stopped the collector and pop-out process cleanly.
- A three-sample elevated CPU/GPU-only diagnostic completed and stopped normally: CPU temperature was approximately 66.9°C, hardware discovery took approximately 517 ms and the last complete poll took approximately 56 ms. This is a short observation, not a hardware performance guarantee.
- After the TraceEvent startup-order fix, an elevated network-only diagnostic completed with six attributed applications and no diagnostic error. ETW initialization took approximately 206 ms; the final polling pass took approximately 27 ms. Process and trace-session checks confirmed clean termination. Computer Use was off for this check.

## Automated and code review checks

The Release build completed without warnings or errors, and all 39 tests passed. Tests cover bounded history, missing readings, rates and counter resets, stable selection, settings recovery, physical-pixel placement, lost process lifecycle events, polling cadence and diagnostic error capture. Independent review accepted the corrected DPI placement, attribution recovery and bounded diagnostics.

## Manual responsiveness check

Severe whole-system lag was reported during elevated testing. The corrected build narrows default hardware polling, guarantees idle time between slow passes, and adds provider-isolation diagnostics. Later, lag persisted after all DeadWare processes and trace sessions had stopped; the user reported that exiting Computer Use restored responsiveness. Final checks therefore ran without screen automation.

The first elevated network-only diagnostic exposed a separate TraceEvent initialization-order bug. Kernel provider configuration now occurs before accessing the session source, as required by TraceEvent 4.0.0. The fix passed scoped review and the bounded elevated traffic check.

On 2026-10-25 the user tested the corrected published build as administrator with Computer Use off, with hardware monitoring and application traffic together. The user confirmed that the app and PC stayed responsive throughout the requested approximately one-minute check, network applications were visible, and the app closed cleanly. The reported lag did not recur in that check. This supports the preview on the tested host; it does not establish the original lag's exact cause or guarantee performance on other PCs.

## Coverage limits

Real mixed-DPI multi-monitor restoration, sustained IPv4/IPv6 TCP/UDP attribution under trace loss, additional CPU/GPU vendors, motherboard sensors, laptops and ARM64 have not been validated. Automated tests cover the pure accounting, history and restoration logic; they cannot establish hardware compatibility or whole-system performance.

Raw diagnostics remain local and are excluded from source control. Reports should redact personal details before upload.

## Startup and saved workspace (0.2.0)

On 2026-10-25, the Release build and self-contained Windows package passed all 56 tests. New coverage includes the remembered open-window set, shutdown preservation, revision-ordered saves, per-user task policy, unexpected task repetition, and Windows account-name/SID equivalence. Independent review accepted the feature after the policy corrections.

On the development PC, the installed executable successfully enabled, disabled and re-enabled its owned startup task. Native Task Scheduler inspection confirmed the current user, interactive logon, highest privileges, the stable executable path with `--startup`, a ten-second sign-in delay, unlimited trigger/task execution time, IgnoreNew, battery operation, and no idle or network prerequisite. The app's read-only query confirmed the final registration is ready with no repair needed.

Actual registration exposed a Windows normalization detail that the unregistered XML roundtrip did not: Windows stores the logon trigger account as a name while retaining the principal SID. The app now resolves only the trigger name to its SID; task ownership still requires the exact principal SID and ownership marker. Unknown or different identities are rejected.

Offscreen WPF checks exercised themed ScrollViewer, DataGrid and dropdown scrollbars, both orientations, line/page commands, thumb movement, and live Midnight/Daylight changes. Rendered-pixel checks cover the scrollbar corner during theme changes. These checks created no visible windows. On the installed Windows 11 build, DWM reported dark mode enabled for the dashboard, both GPU graph windows and the widget. The user confirmed the styling looked correct and the app stayed responsive. Older Windows keeps the standard native frame; live High Contrast transitions were not exercised.

The opt-in layout diagnostic passed 27 isolated checks covering disabled behavior, path validation, existing-file protection, readback results, error isolation and its size cap. Tests used disposable files. A file-symlink case was not exercised because creating that link required unavailable privileges.

The installed app recorded successful settings writes and matching readbacks, preserved open graphs while closing the main window, and completed shutdown cleanly. A normal scheduled run with diagnostics off saved the widget and two GPU graphs. A subsequent task launch read that saved set; native physical-pixel inspection confirmed the dashboard, widget and both graphs matched the previously captured positions, sizes and topmost flags exactly. A quiet duplicate launch exited without another collector. The startup definition was restored to its ordinary `--startup` action after the diagnostic check.

The command runner's settings-file readbacks remained stale relative to the scheduled app's own readings at the same reported path. Per-process diagnostics and native window comparisons resolved that verification discrepancy; no persistence change was made to address it. The underlying execution-context difference was not identified. No reboot or sign-out was performed, and Computer Use remained off throughout these checks.

## Compact graph windows (0.2.1)

The Release build completed without warnings or errors and all 56 tests passed. A focused review found no actionable defects in the compact view, shared range controls, topmost binding, event subscriptions or existing placement behavior.

Isolated offscreen WPF checks covered all five graph types at the new 300×180 minimum and 420×230 default window dimensions, with estimated native-frame space removed. Synthetic fixtures exercised missing/stale readings, long device names and rates, both network directions, Midnight/Daylight palettes and live theme changes. Image exports at 96/144/192 DPI are rendering checks, not a real mixed-DPI monitor test. The harness also exercised range buttons and the topmost binding using disposable settings files. It did not start a collector, create a native window, or read/write the user's settings.

The graph options popup uses WPF outside-click dismissal and explicit Escape/focus handling. These native interactions were reviewed but not exercised by the offscreen harness. Existing saved sizes are retained; users can resize pop-outs to the smaller minimum. Dashboard cards, the widget, monitoring and persistence were not changed.

## Tray lifetime (0.3.0)

The Release build completed without warnings or errors and all 60 tests passed. Four lifecycle tests cover hide versus unavailable-tray fallback, session-ending close behavior, reentrant/repeated quit, and cleanup after teardown failure. Independent review accepted the implementation after a session-ending cleanup gap was corrected.

A separate native-free WPF probe passed 79 checks against the actual dashboard close handler, session shutdown, App session-ending/exit hooks and isolated saved settings. Hiding did not cancel the collector, close detached windows or change the remembered set. Explicit quit closed fixture windows and persisted their open set. Failure paths still reached cleanup, and normal quit/session ending reused one collector-stop task. A never-completing hardware-worker fixture returned from the session-ending stop after approximately 3.01 seconds. This bounded path runs without a dispatcher continuation; it requests ETW disposal before awaiting hardware completion. The three-second deadline is a best-effort limit, not a guarantee that every stuck provider completes.

These checks used no native tray, displayed windows, live collector, UAC, real Windows sign-out or user settings. Native tray clicks and visibility require the installed-app smoke check. Explorer restart and actual Windows sign-out are not covered by the fixture checks.

## Published release records

The public [0.2.1 preview](https://github.com/DeadeyeDuncan/DeadWare/releases/tag/v0.2.1) was published on 2026-10-25 from commit `60f4e9c`. The [Windows build](https://github.com/DeadeyeDuncan/DeadWare/actions/runs/40241998790) and [release workflow](https://github.com/DeadeyeDuncan/DeadWare/actions/runs/40242000182) passed. Its downloaded Windows x64 ZIP matched both the checksum file and GitHub asset digest: `180d9b48e86f5ab7e162da24bf18c38bc3ec95542d184c915b61809d648bc804`. The archive contained required app/runtime/license files and no local settings or NuGet restore file. After the user closed the prior version, the compact update was installed at the existing path and launched through the administrator startup task. Native inspection found one app process with the dashboard and GPU graph windows open; a GPU graph was sized 406×204 pixels, below the former minimum. Computer Use remained off.

The public [0.2.0 preview](https://github.com/DeadeyeDuncan/DeadWare/releases/tag/v0.2.0) was published on 2026-10-25 from commit `7023b68`. Its [Windows build](https://github.com/DeadeyeDuncan/DeadWare/actions/runs/40240862148) and [release workflow](https://github.com/DeadeyeDuncan/DeadWare/actions/runs/40241033973) passed. The downloaded Windows x64 ZIP matched both its checksum file and GitHub's asset digest: `b91382259a2f5413c04ab7de04748a86ec522f93ff01ee88775aaec69fafcb51`. Required app/runtime/license files were present, with no local settings or NuGet restore file included.

The public [0.1.0 preview](https://github.com/DeadeyeDuncan/DeadWare/releases/tag/v0.1.0) was published on 2026-10-25 from commit `1a036e6`. The [Windows build](https://github.com/DeadeyeDuncan/DeadWare/actions/runs/40228886325) and [release workflow](https://github.com/DeadeyeDuncan/DeadWare/actions/runs/40229056889) both passed. CI uses the verified .NET SDK 10.0.402.

The release contains a self-contained Windows x64 ZIP (approximately 69.5 MiB) and a SHA-256 file. The checksum file was checked against GitHub's uploaded-asset digest: `9a14b73592cecb17c1bbb443610b5826427284a43cb18dc9a16fc3fbcd4a5c3f`.
