# Changelog

## 0.6.0 — Manual updates and rollback

- Show battery saver directly on the charge trace with a different color and dashed line, a graph legend and distinct unknown/mixed states; remove saver text from the current-status card.
- Add Updates with on-demand checks for normal GitHub releases, release notes and explicit Update & restart / Roll back & restart actions.
- Verify release ZIP digests/checksums, safe archive paths and every managed file before preparing a replacement.
- Apply through an independent helper after normal Quit, preserve settings/history and install paths, and retain a verified previous version for rollback.
- Recover interrupted or failed replacements without force-terminating monitoring. Show unavailable releases/backups and clear preparation/operation status.
- Publish future tagged versions as normal releases. Historical previews are ignored by the updater; no automatic checks or preview-channel controls are added.

## 0.5.0 — Lifetime battery history

- Record battery percentage and charging/power and Windows battery-saver changes into independent local history with no automatic expiry.
- Add Day/Week/Month/Year/All charts, original-observation CSV export, current status, storage/error reporting and an opt-out recording control.
- Open Battery history in its own remembered, resizable, always-on-top window. Preserve unavailable readings and restart/sleep/paused-monitoring gaps.
- Keep long-range queries and writes off the UI/collector threads, and drain pending battery observations during normal Quit and bounded sign-out.


## 0.4.2 — Remember graph history

- Automatically save and restore the last six elapsed hours of CPU/GPU usage and temperature plus combined network upload/download graphs across quits and restarts.
- Keep downtime and session boundaries visible as gaps, without substituting saved values for current readings or mixing identifiable different hardware.
- Save on a background timer and flush on normal shutdown. Bounded local storage and visible errors leave live monitoring running if history cannot be saved.

## 0.4.1 — Longer live graphs

- Add 1-, 3- and 6-hour ranges alongside the existing minute ranges for CPU, GPU and combined network graphs. The shared range selection survives restart.
- Retain up to six hours of live samples in memory. Longer charts preserve spikes and gaps while reducing drawing work to the graph's width.
- Keep the compact options menu narrow with two rows of range choices, and avoid history statistics work for hidden dashboards and closed graph menus.

## 0.4.0 — Cooling Health

- Cooling Health records local CPU/GPU temperature, load, package power and available clock/fan history. Day/week/month charts and CSV export preserve unavailable readings and recording gaps.
- Save an explicit fixed reference period and compare ten sustained minutes at similar power, load and available fan speed. Optional tray warnings have acknowledgment and a persistent 24-hour cooldown.
- Separate cooling setups and sensor mappings keep references from silently mixing. Learning, stale and insufficient-data states explain when comparison is unavailable.
- Keep 30 days of minute detail and 90 days of hourly summaries by default, with retention controls and bounded local SQLite storage. Recording errors leave live monitoring running.
- Show the GPU selector only on Overview; remove it from Network, Sensors and Desktop layout without changing the selected adapter or history controls.

## 0.3.0 — Keep monitoring from the tray

- Closing the dashboard now hides it in the system tray while pop-out graphs, the widget and monitoring keep running.
- Reopen the same dashboard from the tray icon; use Quit DeadWare in the tray menu or dashboard to fully exit.
- Optional Start in the system tray setting hides the dashboard at Windows sign-in while restoring your chosen graphs and widget.
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
