# Changelog

## 0.6.14 — Joined CPU/GPU pop-outs

- Add a saved Settings option to join CPU/GPU usage on one pop-out and both temperatures on another, using consistent device colors and line styles.
- Show both devices' current readings and statistics, with shared usage and temperature scales.
- Apply the option to new pop-outs while retaining existing open windows and remembered layouts.
- Remove the redundant graph, Battery history and widget launch buttons from Settings; keep the overview and dashboard launch controls.

## 0.6.13 — Widget pin control in Settings

- Move the widget's Always on top checkbox into Settings and use the freed space for graphs.
- Apply pin changes immediately and save them even when the widget is closed.
- Carry over the previous widget pin choice while keeping other windows' pin controls unchanged.

## 0.6.12 — Choose widget graphs

- Add saved Settings checkboxes for CPU/GPU usage, CPU/GPU temperature, RAM and Network, applying changes to an open widget.
- Collapse hidden graph rows and show a helpful empty state when all graphs are off; collection and history continue.
- Adjust minimum/default widget heights to the selected count while retaining current size and pin when hiding graphs.

## 0.6.11 — RAM usage graphs

- Add a RAM usage graph beside Network on the overview and as the widget's fourth graph, showing percent and current used/total memory.
- Save RAM's six-hour percentage history using the shared polling interval, range and gap handling; earlier versions have no RAM history to restore.
- Support a remembered RAM pop-out and grow older saved widget sizes to fit four graphs.

## 0.6.10 — Network rate colors

- Match the current upload rate color to the upload graph line while keeping download in its own graph color.
- Keep the rate colors in sync when switching themes.

## 0.6.9 — Adjustable data polling

- Choose 0.5, 1, 2, 5 or 10 seconds in Settings; keep the one-second default and save the choice across launches.
- Apply changes after the current wait or reading finishes, without restarting or overlapping hardware scans.
- Adjust display freshness, graph gaps and battery/Cooling Health history for the selected cadence, preserving six hours of graphs at the fastest setting.
- Keep Cooling Health's reference comparison quality requirements; 5- and 10-second polling still records history but has too few samples for comparisons.

## 0.6.8 — Cooling Health navigation order

- Move Cooling Health immediately after Sensors in the dashboard sidebar.

## 0.6.7 — Network graph in the widget

- Add a third widget graph for download and upload, sharing a bytes-per-second scale and the existing history range.
- Show current rates in the network legend while retaining CPU/GPU usage and temperature graphs, memory and status.
- Make room for all three charts and grow older saved sizes to the window minimum before clamping their on-screen position.

## 0.6.6 — Release notes for every missed version

- Show notes for every normal release newer than the installed version through the offered update, with separate version sections ordered newest first.
- Exclude installed/older versions and previews, and retrieve intervening notes across release-history pages.
- Keep the latest notes and update available if earlier notes cannot be loaded; explain the fallback and support cancellation.

## 0.6.5 — Group widget graphs by metric

- Put CPU and selected-GPU usage together on the first widget graph, and both temperatures together on the second.
- Keep CPU's color and solid line consistent across both graphs, and GPU's color and dashed line consistent across both.
- Use one shared percent scale for usage and one shared °C scale for temperatures, expanding the latter for either device above 100°C.

## 0.6.4 — Hide the update banner on Updates

- Hide the update-available banner while the Updates page is open, including during background status changes.
- Restore the banner on other pages when a newer release remains available.

## 0.6.3 — Widget graphs

- Show stacked CPU and selected-GPU history graphs in the compact widget, each combining solid usage and dashed temperature lines in different colors.
- Label percent and °C scales separately, expand the temperature scale above 100°C, and retain small current readings plus memory/network summaries.
- Reuse the shared history range, GPU selection and saved graph history, preserving missing-data gaps and stale-reading status.

## 0.6.2 — Desktop-aware navigation

- Hide Battery history in the sidebar and its Settings window button when Windows reports no battery; restore them when a battery is detected or its status is unknown.
- Return to Overview if the open Battery history page becomes unavailable, while retaining saved history and existing detached windows.
- Rename Desktop layout to Settings.

## 0.6.1 — Startup update notices

- Check for the latest normal GitHub release once per app launch, after monitoring starts.
- Show a persistent dashboard notice and system-tray notification when a newer version is available; Review update opens Updates.
- Keep downloads, installation, restart and rollback explicit. Handle offline checks quietly and cancel pending checks on shutdown.

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
