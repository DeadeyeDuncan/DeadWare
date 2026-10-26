<p align="center"><img src="assets/deadware.png" width="160" alt="DeadWare skull processor emblem"></p>

# DeadWare

**Your hardware and network, in one clear view.**

[![Windows build](https://github.com/DeadeyeDuncan/DeadWare/actions/workflows/build.yml/badge.svg)](https://github.com/DeadeyeDuncan/DeadWare/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

DeadWare is a Windows desktop monitor for watching CPU/GPU usage and temperatures alongside application network activity. Keep the dashboard open, detach a graph, or use the compact desktop widget.

## Features

- CPU and GPU utilization and temperature graphs with detected device names.
- A combined download/upload graph with a shared scale and distinct line styles.
- Per-application network rates when Windows permits ETW capture.
- Searchable sensor readings with minimum, maximum and average values.
- Selectable graphics adapters on multi-GPU systems.
- Separate graph windows and a compact widget with always-on-top controls.
- Compact pop-out graphs prioritize the current reading and chart; their options menu holds history range, Always on top and sensor statistics.
- Close the dashboard to the system tray and keep graphs, the widget and monitoring running independently.
- Restore the graphs and widget left open, including their saved positions and sizes.
- Optional administrator startup at your Windows sign-in.
- Shared 1-, 5- and 15-minute history; all windows use one collector.
- Local Cooling Health history, fixed reference comparisons, CSV export and optional sustained-change warnings.
- Five saved themes: **Midnight**, **Ember**, **Glacier**, **Amethyst**, and **Daylight**.
- Scrollbars follow the selected theme across all windows, with matching native title bars on Windows 11.

## Download and run

Get the Windows x64 ZIP from [Releases](https://github.com/DeadeyeDuncan/DeadWare/releases). Extract the **entire** archive and run `DeadWare.exe` inside the `DeadWare` folder. The .NET runtime is bundled. The initial preview is portable and unsigned; no installer or automatic updater is included.

Windows 10/11 x64 is the initial target. ARM64 and other operating systems are not validated. Releases remain previews while hardware coverage expands.

Start as a normal user. If a capability needs additional permissions, the app shows its status and provides an explicit administrator restart action. Administrator access does not guarantee every sensor is available. DeadWare does not disable Windows protections or install a custom monitoring driver.

## Startup and saved workspace

In **Desktop layout**, enable **Start with Windows** to launch DeadWare as administrator when you sign in. Windows approval is needed to configure this option. It creates a task for your Windows account, runs only in your signed-in session, and stores no password. You can turn it off from the same page. Only one monitoring instance runs at a time.

For a quiet sign-in, also enable **Start in the system tray**. This hides the dashboard only for Windows startup launches. Remembered graphs and the widget still reopen when restoration is enabled; turn restoration off too if you want only the tray icon. A fresh manual launch still shows the dashboard. If the tray icon cannot be created, the dashboard stays accessible.

Closing the dashboard with its X or Alt+F4 hides it in the system tray. Pop-out graphs, the widget and monitoring keep running, even with no dashboard open. Double-click the DeadWare tray icon or right-click it and choose **Show dashboard** to reopen it. Windows may place the icon in the hidden-icons area next to the clock.

To fully stop monitoring, choose **Quit DeadWare** from the tray menu or dashboard. Keep window restoration enabled and leave your desired graphs and widget open when you quit: they will reopen with their saved positions, sizes and always-on-top choices. Closing a graph or widget individually keeps it closed next time. Positions are kept within available monitors if your display setup changes. Live graph history starts a new session after a full quit; saved Cooling Health history remains available. Hiding the dashboard resets neither.

Version 0.1.0 saved window positions but did not record which windows were open. After upgrading, arrange your desired windows once to establish the saved workspace.

Startup points to the executable's current location. Keep the extracted folder in a permanent location. If you move it, enable startup again from the new location; disable startup before removing the app. Choose **Quit DeadWare** before replacing files during an upgrade; closing the dashboard alone leaves it running.

## Hardware compatibility

DeadWare discovers hardware rather than assuming a particular PC. [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) supplies supported sensor data; Windows counters provide basic fallbacks. Support varies by CPU, GPU, motherboard, laptop firmware, drivers and permissions. Missing sensors are shown as unavailable, and missing history remains a graph gap.

The default hardware scan focuses on CPU and GPU sensors. Extended motherboard, controller, storage and memory-device sensors are optional and off by default, since low-level polling can affect responsiveness on some machines. Changing this setting requires restarting DeadWare. Windows memory usage remains available without extended scanning.

This first release is **not complete HWiNFO sensor parity**. A successful launch on one PC is not evidence that every sensor works on every device. Please report compatibility findings through the hardware issue template with device models and the capability message, without serial numbers.

## Cooling Health

The **Cooling Health** page saves local CPU/GPU temperature, usage and package power, plus clock and fan readings when supported. Recording starts by default; optional tray notifications start off. Choose a device and day/week/month range, switch metrics or export a new CSV file. Settings and the SQLite history database live under `%LOCALAPPDATA%\DeadWare`. No thermal data is uploaded. Per-application network data is not stored there.

The first minute produces a history window. To compare cooling, select a past period of up to seven days when you believe the setup was operating normally and save it as a named reference. At least 30 eligible minute windows are needed. The first five minutes, gaps, unstable workloads, missing required readings and known throttling are excluded; learning can therefore take longer than 35 minutes. Saved reference readings stay fixed rather than learning a developing problem as normal.

Comparisons need ten consecutive stable recent minutes, each matched to at least ten reference windows within 10% or 5 W of package power (whichever is larger), ten percentage points of load, and comparable fan speed when both sides provide it. The initial warning rule requires every recent minute to exceed the reference mean by at least 5°C and its 95th percentile by at least 3°C. These conservative engineering defaults are not validated probabilities of hardware failure. Coolant and room temperature are not measured by this first implementation; fan context may also be unavailable. Temperature-target boosting can conceal cooling deterioration, so an ordinary comparison is not a health certificate.

Warnings are logged even when tray notifications are muted. Enable notifications if desired; warnings can be acknowledged and are limited to one per device/reference per 24 hours. An unacknowledged warning suppresses repeats. Changing the active reference does not modify its saved readings. Choose **New cooling setup** after hardware replacement (including a replacement with the same model), cleaning, remounting, fan-curve or power-limit changes. Local identity uses machine, hardware metadata and the selected sensor mapping; it cannot identify every physical replacement automatically. When multiple devices have identical model names, their histories are separated by session and comparison is disabled because enumeration cannot establish physical identity. Older setups remain available for history viewing.

By default, minute detail is retained for 30 days and hourly summaries for 90 days. Controls offer 7/30 days of detail and 30/90 days of summaries. The database is capped at 256 MiB with a bounded journal; the page shows storage usage. Up to ten references per device/setup are retained. If storage is locked, full or corrupt, recording reports an error and live monitoring continues; resolve the storage issue and restart to retry. Saved references can consume space independently of ordinary history retention. Database files are local to this Windows profile; cloud synchronization of an open database is not supported.

Cooling Health flags changes worth investigating. It cannot diagnose a blocked cooler, failing pump, or remaining lifespan. Pump/flow/coolant-specific monitoring needs confirmed supported sensors and is not included yet.

## Network measurements and privacy

Interface totals work independently of per-process attribution. Application traffic uses Windows ETW metadata, including TCP/UDP IPv4/IPv6 byte events. The app does not record packet payloads or send telemetry to a cloud service. No firewall management is included.

Rates reflect observed traffic, not ISP billing totals. Adapter totals and attributed application traffic may differ due to virtual adapters, local traffic, protocol overhead or lost events. Network session history is held in memory; long-term network usage totals are not included. Thermal history is stored separately as described above. Settings are kept in the user's local application-data directory.

Diagnostic output is generated only when explicitly requested. It can include hardware names and running application information; redact it before attaching it to a public issue.

For local troubleshooting, `DeadWare.exe --diagnose output.json --samples 3` collects a short sample and exits. Add `--no-hardware` to isolate Windows counters and network tracking, or `--no-etw` to isolate hardware collection from per-app tracing. Diagnostics include provider timings. Avoid enabling extended sensors when investigating a responsiveness problem.

For workspace-saving problems, start the app with `--layout-diagnostics "C:\path\layout.jsonl"` using an absolute local output path. This opt-in, size-limited trace records settings paths, open-window IDs and save results; it follows an administrator restart and contains no sensor readings or network traffic. Use a new file or a previous DeadWare layout trace. Treat it as private diagnostic data.

## Build

Install the stable [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then:

```powershell
dotnet build DeadWare.slnx -c Release
dotnet test DeadWare.slnx -c Release
dotnet run --project src/DeadWare.App/DeadWare.App.csproj
```

Create a self-contained portable ZIP and SHA-256 checksum:

```powershell
pwsh -File scripts/publish.ps1 -Version 0.4.0
```

The packaging script runs tests before publishing and writes output under `artifacts/`. GitHub Actions runs the same script. Pushing a version tag such as `v0.2.0` builds and publishes a **prerelease** after tests pass, using the matching `docs/releases/v0.2.0.md` release notes. Workflows use pinned action commits.

## Project direction

The first release focuses on everyday monitoring, hardware adaptability and usable detached windows. Future work can expand compatibility validation, long-term usage history, signed installers and additional telemetry sources. Development plans are in `docs/superpowers/`.

Original code is MIT licensed. Dependencies retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md). The icon was created for DeadWare to match the owner's Dead app branding; its generation details are in [assets](assets/README.md).
