# DeadWare first working release

Approved direction: user's CPU/GPU dashboard and combined network graph mockup, with authorization on 2026-10-25 to build the actual app and a public GitHub repository named DeadWare under DeadeyeDuncan. Brand should belong with the other Dead apps. This document records implementation choices made by Codex; it does not imply the user selected a framework or license.

## Product
Windows desktop monitor centered on four charts: CPU usage, CPU temperature, GPU usage, GPU temperature. Network activity has one shared-scale download/upload chart and a searchable application bandwidth table. Each chart, including network, can open as a separate resizable window. A compact desktop widget shows CPU/GPU usage, temperatures, memory and network rates. Detached windows support topmost and remember usable positions; all windows share one collector and history.

Clean dark UI follows the accepted concept: readable typography, generous grouping, mint CPU, amber GPU, blue download and dashed mint upload. Use a red/silver/black hardware emblem to fit the Dead app brand without changing semantic chart colors. Include sensor details with current/minimum/maximum/average and detected hardware names.

User added preset themes during implementation: Midnight, Ember, Glacier, Amethyst and Daylight. Persist the selection and apply it live across all open windows. Keep semantic colors distinguishable and use readable light-mode equivalents. Unknown saved themes fall back safely. A custom theme editor is not in this release.

## Compatibility and trustworthy readings
Target supported Windows 10/11 x64 computers first using .NET 10 LTS, WPF, and a self-contained distribution. Do not claim ARM64 support without validation. Discover devices, allow GPU selection on multi-GPU systems, and use stable sensor IDs. LibreHardwareMonitor provides supported hardware readings; Windows CPU/memory/network counters provide fallbacks. Show unavailable or stale readings explicitly, never fabricate data or convert unknown to zero. Missing sensors must not prevent other monitoring. Capability status explains normal-user vs elevated monitoring and unsupported/blocked sensors. Never disable Windows protections or install a custom kernel driver.

One-second samples with bounded 15-minute memory history; 1/5/15-minute graph windows and gaps for missing values. Collect off the UI thread without overlapping polls. Historic missing readings must remain gaps. Display min/max/average over the selected history. Use actual elapsed monotonic time for byte rates.

After the first elevated smoke test caused severe system lag, the default sensor scope is narrowed to CPU/GPU. Extended motherboard, controller, storage and memory-device scanning is opt-in and requires restart. Polling must retain an idle interval even when a hardware pass exceeds its nominal cadence. Local diagnostics can disable hardware or ETW independently and report provider timings. These changes reduce polling scope; they do not by themselves establish the cause or resolution of the reported lag. Confirm responsiveness with bounded isolated elevated checks before releasing.

## Network
Interface totals work without elevation. Per-process TCP/UDP IPv4/IPv6 byte counts use Microsoft ETW when permitted. Present permission/error state with an explicit restart-as-administrator action; run without elevation by default. Capture traffic metadata and byte counts only, not payloads. Do not claim traffic attribution is exact billing, or silently substitute interface totals for per-process data. Handle PID reuse, exiting processes, event loss, network adapter resets, and no active interface. Keep data locally in memory; user settings only persist in local app data. No cloud telemetry, firewall control, or packet payload recording.

## Delivery
Public GitHub repository, README, license, third-party notices, issue templates, Windows build/test workflow and tag-triggered prerelease packaging. Initial version 0.1.0; portable self-contained ZIP is sufficient for the first release and avoids a machine-wide install. Provide a local executable and launch it for verification. An installer and signing can be later work. No guarantee of complete HWiNFO sensor parity.

Verify core history/rate/selection/settings edge cases automatically and launch the real published WPF binary. Record actual host sensor/network coverage and any untested elevated path. A release must clearly identify first-release hardware coverage limits.
