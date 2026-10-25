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
- Shared 1-, 5- and 15-minute history; all windows use one collector.
- Five saved themes: **Midnight**, **Ember**, **Glacier**, **Amethyst**, and **Daylight**.

## Download and run

Get the Windows x64 ZIP from [Releases](https://github.com/DeadeyeDuncan/DeadWare/releases). Extract the **entire** archive and run `DeadWare.exe` inside the `DeadWare` folder. The .NET runtime is bundled. The initial preview is portable and unsigned; no installer or automatic updater is included.

Windows 10/11 x64 is the initial target. ARM64 and other operating systems are not validated. Releases remain previews while hardware coverage expands.

Start as a normal user. If a capability needs additional permissions, the app shows its status and provides an explicit administrator restart action. Administrator access does not guarantee every sensor is available. DeadWare does not disable Windows protections or install a custom monitoring driver.

## Hardware compatibility

DeadWare discovers hardware rather than assuming a particular PC. [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) supplies supported sensor data; Windows counters provide basic fallbacks. Support varies by CPU, GPU, motherboard, laptop firmware, drivers and permissions. Missing sensors are shown as unavailable, and missing history remains a graph gap.

The default hardware scan focuses on CPU and GPU sensors. Extended motherboard, controller, storage and memory-device sensors are optional and off by default, since low-level polling can affect responsiveness on some machines. Changing this setting requires restarting DeadWare. Windows memory usage remains available without extended scanning.

This first release is **not complete HWiNFO sensor parity**. A successful launch on one PC is not evidence that every sensor works on every device. Please report compatibility findings through the hardware issue template with device models and the capability message, without serial numbers.

## Network measurements and privacy

Interface totals work independently of per-process attribution. Application traffic uses Windows ETW metadata, including TCP/UDP IPv4/IPv6 byte events. The app does not record packet payloads or send telemetry to a cloud service. No firewall management is included.

Rates reflect observed traffic, not ISP billing totals. Adapter totals and attributed application traffic may differ due to virtual adapters, local traffic, protocol overhead or lost events. Session history is held in memory; long-term usage storage is not included. Settings are kept in the user's local application-data directory.

Diagnostic output is generated only when explicitly requested. It can include hardware names and running application information; redact it before attaching it to a public issue.

For local troubleshooting, `DeadWare.exe --diagnose output.json --samples 3` collects a short sample and exits. Add `--no-hardware` to isolate Windows counters and network tracking, or `--no-etw` to isolate hardware collection from per-app tracing. Diagnostics include provider timings. Avoid enabling extended sensors when investigating a responsiveness problem.

## Build

Install the stable [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then:

```powershell
dotnet build DeadWare.slnx -c Release
dotnet test tests/DeadWare.Tests/DeadWare.Tests.csproj -c Release
dotnet run --project src/DeadWare.App/DeadWare.App.csproj
```

Create a self-contained portable ZIP and SHA-256 checksum:

```powershell
pwsh -File scripts/publish.ps1 -Version 0.1.0
```

The packaging script runs tests before publishing and writes output under `artifacts/`. GitHub Actions runs the same script. Pushing a version tag such as `v0.1.0` builds and publishes a **prerelease** after tests pass. Workflows use pinned action commits.

## Project direction

The first release focuses on everyday monitoring, hardware adaptability and usable detached windows. Future work can expand compatibility validation, long-term usage history, signed installers and additional telemetry sources. Development plans are in `docs/superpowers/`.

Original code is MIT licensed. Dependencies retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md). The icon was created for DeadWare to match the owner's Dead app branding; its generation details are in [assets](assets/README.md).
