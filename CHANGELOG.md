# Changelog

## 0.3.0 — Keep monitoring from the tray

- Closing the dashboard now hides it in the system tray while pop-out graphs, the widget and monitoring keep running.
- Reopen the same dashboard from the tray icon; use Quit DeadWare in the tray menu or dashboard to fully exit.
- Full quit saves the open workspace for next launch. Administrator restart and Windows sign-out keep their shutdown behavior.

## 0.2.1 — Compact graph windows

- Smaller pop-out graphs with a compact header and prominent current readings.
- Move history range, Always on top, hardware details and statistics into a graph options menu.
- Keep both download and upload readings visible in the compact network graph.
- Preserve saved window positions, sizes and topmost choices; existing windows can now be resized smaller.

## 0.2.0 — Startup and saved workspace

- Optional administrator startup at Windows sign-in, with per-user registration and an in-app disable control.
- Restore previously open pop-out graphs and the desktop widget, including saved positions, sizes and topmost choices.
- Checkpoint workspace changes and preserve open windows during application shutdown and administrator restart.
- Prevent duplicate monitoring instances when starting from Windows or opening the app again.
- Theme-aware scrollbars and Windows 11 native title bars across the main app, pop-out graphs and widget.

## 0.1.0 — First preview

- Real CPU/GPU usage and temperature charts with detected hardware and explicit sensor availability.
- Combined download/upload graph and per-application network monitoring when Windows permits ETW capture.
- Detached graph windows and compact desktop widget.
- Sensor details and shared 1/5/15-minute history.
- Midnight, Ember, Glacier, Amethyst and Daylight theme presets.
- CPU/GPU-focused polling by default, optional extended sensors, and local provider-isolation diagnostics.
- Portable Windows x64 packaging, SHA-256 checksums, build checks and GitHub prerelease automation.

Initial hardware compatibility validation is limited; please report missing sensors with hardware model and the displayed status message. This is not a claim of complete HWiNFO or GlassWire feature parity.
