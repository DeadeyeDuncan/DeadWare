# Changelog

## 0.2.0 — Startup and saved workspace

- Optional administrator startup at Windows sign-in, with per-user registration and an in-app disable control.
- Restore previously open pop-out graphs and the desktop widget, including saved positions, sizes and topmost choices.
- Checkpoint workspace changes and preserve open windows during application shutdown and administrator restart.
- Prevent duplicate monitoring instances when starting from Windows or opening the app again.

## 0.1.0 — First preview

- Real CPU/GPU usage and temperature charts with detected hardware and explicit sensor availability.
- Combined download/upload graph and per-application network monitoring when Windows permits ETW capture.
- Detached graph windows and compact desktop widget.
- Sensor details and shared 1/5/15-minute history.
- Midnight, Ember, Glacier, Amethyst and Daylight theme presets.
- CPU/GPU-focused polling by default, optional extended sensors, and local provider-isolation diagnostics.
- Portable Windows x64 packaging, SHA-256 checksums, build checks and GitHub prerelease automation.

Initial hardware compatibility validation is limited; please report missing sensors with hardware model and the displayed status message. This is not a claim of complete HWiNFO or GlassWire feature parity.
