# Preview validation

DeadWare targets Windows 10/11 x64. Hardware discovery is dynamic, but compatibility has only been exercised on one development PC so far. This document records observed coverage, not a certification of other hardware.

## Observed hardware and UI

- AMD Ryzen 7 7700X CPU usage and Windows memory readings were available without elevation.
- AMD Radeon integrated graphics and an RX 6800 XT were discovered separately. The discrete GPU supplied a real core temperature. The integrated GPU's VR SoC sensor is not substituted for core temperature.
- Inaccessible CPU temperature readings remained unavailable rather than showing a misleading zero.
- Aggregate network rates worked without elevation; per-app tracing correctly displayed the administrator requirement.
- Published WPF dashboard, theme persistence, GPU selection, widget opening and closing all windows with the main window were exercised.

## Release gates still pending

An early elevated build caused severe whole-system lag on the development PC; closing it restored responsiveness. The next build narrows default hardware polling, guarantees idle time between slow passes, and adds provider-isolation diagnostics. The exact cause and responsiveness of the corrected elevated build still require verification. No release should claim this issue resolved before those checks pass.

The independent review also requested fixes for mixed-DPI window restoration and network process identity recovery after lost ETW lifecycle events. Regression results and final review belong in the completion record once the corrected build is ready.

## Coverage limits

Real mixed-DPI multi-monitor restoration, sustained IPv4/IPv6 TCP/UDP attribution under trace loss, additional CPU/GPU vendors, motherboard sensors, laptops and ARM64 have not been validated. Automated tests cover the pure accounting, history and restoration logic; they cannot establish hardware compatibility or whole-system performance.

Raw diagnostics remain local and are excluded from source control. Reports should redact personal details before upload.
