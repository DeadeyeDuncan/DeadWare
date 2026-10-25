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
