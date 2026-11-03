# Changelog

## 0.10.0 — Per-core CPU usage

- Add **Settings → Monitoring → Show per-core CPU usage on usage graphs**. One thin solid line per core, tinted from the CPU graph colour, draws under the bold total on the dashboard CPU usage card, the separate CPU usage pop-out and the widget Usage graph; hover a graph to see the eight busiest cores. Core lines have no fill, glow or min/max band and follow the smoothing setting on 1–6 hour ranges. Core traces are saved in the six-hour graph history (up to 48 cores, once a second). Off by default; the joined CPU/GPU pop-out, Usage history, Alerts and Cooling Health are unchanged.
- A line is one LibreHardwareMonitor load sensor (`CPU Core #k`, `P-Core #k`, `E-Core #k`, or each thread where the chip exposes them), labelled `Core 1…N`, up to 64 live. With no per-core loads from LibreHardwareMonitor, Windows per-processor counters are used instead; those persist only when a single CPU identity exists and are otherwise live-only. The CPU card subtitle shows `· N cores`, and the dashboard status says `Per-core CPU usage unavailable.` when the option is on but no cores are reported.

## 0.9.1 — Thermal history and overview sizing

- Fix Cooling Health opening an empty thermal history. Adding pump speed to the CPU sensor map changed the CPU's history identity, and devices were listed by identity hash, so the page defaulted to the stale entry. Devices are now ordered by their most recent recorded window (or hourly summary once minute detail has expired), and a repeated device label gains **· older sensor map** and a short identifier.
- Let the overview card grid fill the window height above the footer, enlarging each graph; 810 px remains the minimum so short windows scroll.

## 0.9.0 — Refined glass

- Restyle the dashboard, pop-outs, widget and Connections as refined glass: cards with hairline borders, a navigation rail with vector icons and an accent bar on the current page, a one-row page header with a hardware subtitle (CPU, GPU, RAM, last sample age), Settings grouped into Monitoring, Appearance, Widget, Pop-outs, Startup and History and alerts cards, and a segmented history-range control in place of the dropdown. Every theme keeps its palette and fonts; derived hairline, accent, surface and glow tones come from each palette, and Blackout keeps pure-black surfaces.
- Draw live graphs as monotone splines through the recorded readings (raw 1–6 hour traces stay exact polylines), on a crisp 1 px hairline grid with value labels inside the plot, wall-clock time ticks (`HH:mm`, seconds on the 1-minute range) and a right-aligned **Now**. A faint under-glow follows each trace. Move the pointer over a graph for a crosshair and a chip with the sample time and each series' value; nothing is shown over a gap. Cooling Health, Battery history and Usage history charts share the grid and the hover chip; thermal windows and usage buckets that connect are splines, battery percent stays a polyline.
- Paint the Connections map as a filled globe: gradient ocean, filled continents, hairline coast and graticule, limb shading and an atmosphere glow (a vertical gradient and seam copies on the 2D map). Traffic routes are static arcs with glow passes and a core line per active direction, moving dots on the forty busiest routes, pulsing nodes with halos, a radar-style origin and legend chips. Hover a node for its process, location and rates; the widget map keeps a larger hit target and no labels. High Contrast and disabled transparency stay flat.
- Give pages their card chrome: Connections controls in a card strip above the map card, Network and Sensors toolbar strips, compact pop-outs with one-row chrome so the trace stays tall, and card-styled widget graphs. No control moves to another window and no setting, history or window behaviour changes.
- The Windows build workflow renders every theme and route with the preview tool and uploads the frames as `DeadWare-previews`.

## 0.8.0 — Alerts

- Add **Alerts** to the sidebar: user-set rules that notify from the tray when observed data crosses a threshold. Four kinds: a device metric (CPU usage, CPU temperature, the selected GPU's usage or temperature, RAM usage) at or above a value for 1, 5, 15 or 60 consecutive recorded minutes; download or upload at or above a rate for the same durations; an application's download plus upload at or above a byte budget within one local day; and battery charge at or below a percentage while on battery.
- While editing a threshold, the page shows what Usage history recorded in the last 7 and 30 days: the highest minute, the average and the share of recorded minutes at or above the value being typed. Nothing is set automatically.
- Each crossing is one episode. A tray balloon names the rule and the observed value, clicking it opens Alerts, and **Recent alerts** lists the episode with **Acknowledge** and **View in Usage history**, which opens the Day view around its start. A rule notifies once per episode, re-arms after the metric stays below the threshold minus a small margin (3°C, 5 points or 10% of a byte value) for the rule's duration, never notifies more than once an hour, and application budgets notify once per day. A **Show tray notifications for alerts** switch, also in Settings, mutes balloons without stopping the log.
- Rules are saved in `settings.json`; the log lives in `%LOCALAPPDATA%\DeadWare\alerts.db`, keeps 400 days under a 64 MiB cap, and is never uploaded. Device and network rules read the same per-minute averages Usage history shows, so recording must be on for them to fire; a gap in recording resets a rule's count, and a restart mid-day keeps the morning's application traffic. Cooling Health's own warnings and notification setting are unchanged.

## 0.7.1 — Reliable tests and manual releases

- Make the automated test suite reliable on busy build machines. Five timing-sensitive tests failed intermittently on CI and blocked two release attempts; the test host now runs those service and shutdown tests one at a time with a larger thread pool, and a settings-save race in a UI check is drained before cleanup. No app behavior changes.
- The Release workflow can be run manually from the Actions tab with a version tag; it creates the tag on the chosen commit and publishes as a tag push does. A repeated run after a failed one moves an unreleased tag to the commit it builds.
- Update Microsoft.Data.Sqlite to 10.0.14 and the test SDK and xUnit runner to their current releases.

## 0.7.0 — Usage history

- Add **Usage history** beside Battery history, with an **Open in window** action and a remembered window. Browse **Day**, **Week** and **Month** views of CPU and GPU usage and temperature, RAM usage and network download/upload, step to earlier periods, and read minimum, average and maximum for the period from the stored minute values.
- Each chart draws the average of the recorded minutes in each slot over a shaded minimum–maximum band. Minutes without readings stay empty, and partial slots are not connected, so sleep, restarts and stopped monitoring remain visible as gaps.
- See per-application download and upload totals for the period, summed by executable name across its processes, with search, a share bar, an **Other applications** row beyond the first 200 names, and a notice when recorded minutes had no per-application capture. Open Connections to see where the bytes went.
- Recording is on by default and saved as **Record usage history on this device** on the page and in Settings. Pausing keeps what was saved. History lives in `%LOCALAPPDATA%\DeadWare\usage-history.db`, keeps up to 400 days or 256 MiB (the oldest whole days are dropped first, never the current day), and is never uploaded. GPU rows are keyed by model name, so a change in enumeration order does not split them.
- Export the period's device minutes or application totals to a new CSV file. Names that look like spreadsheet formulas are neutralized in exports.
- History begins when 0.7.0 first runs; the six-hour live graphs and their cache are unchanged and are not copied into the new store.

## 0.6.39 — Safer updates and startup

- The Updates page no longer closes DeadWare when an installed app file or the saved rollback copy has been changed or damaged. It shows a message and disables installing until the complete package is reinstalled.
- Opening Updates now cleans up old update downloads. Only small records of the latest update are kept, so its downloaded ZIP and unpacked copy are removed, and abandoned update folders older than a day are deleted. For an installation in a protected folder, this cleanup happens when DeadWare runs as administrator. Update data that an earlier version left for such an installation in `%LOCALAPPDATA%\DeadWare\updates` isn't cleaned up automatically; you can delete it while DeadWare is quit.
- Quit no longer hangs when a hardware sensor driver stops responding. DeadWare waits at most 5 seconds for hardware providers, while saving graph, Cooling Health, battery and connection history gets up to 20 seconds to finish. Connection history is now saved even if a hardware provider is stuck, including when Windows signs out.
- Starting as administrator at sign-in now requires DeadWare to be in a folder standard users can't change, such as `C:\Program Files\DeadWare`. A copy in a user folder such as `AppData\Local\Programs` or Downloads can't turn it on, and Settings explains why. If startup was already on from such a folder, Settings now shows a warning; copy or extract DeadWare into `C:\Program Files\DeadWare` with administrator approval, start DeadWare from that copy, then choose **Enable / repair startup**, or turn startup off. Startup is never turned off automatically.
- A DeadWare installed in a protected folder such as Program Files now updates through an administrator-only update folder, `%ProgramData%\DeadWare\updates`, so programs without administrator rights can't change a downloaded update. Updating such an installation needs **Restart as administrator**, and rolling back to a version older than 0.6.39 isn't available for it. Copies in user folders keep using `%LOCALAPPDATA%\DeadWare\updates`.
- DeadWare and its updater ignore .NET startup hooks set through environment variables.

## 0.6.38 — CPU pump speed on graphs

- Add **Settings → Show CPU pump speed on CPU temperature graphs**. It draws the CPU cooler's pump RPM as a dotted line on its own left RPM scale on the dashboard CPU temperature graph, the CPU temperature pop-out and the widget temperature graph, matching the GPU fan overlay. The joined CPU / GPU temperature pop-out keeps only the GPU fan.
- When the widget shows both, the GPU fan and CPU pump share the left RPM scale. The pump is blue by default; change its color and line style from the new CPU pump color button on the overview's CPU temperature card.
- Pump RPM is saved in the six-hour graph history. Like Cooling Health, it uses a header labeled as a pump, otherwise the `CPU Fan` header, and needs **Extended sensors** and a single CPU. The option is off by default.

## 0.6.37 — CPU pump speed in Cooling Health

- Record the CPU cooler's pump speed as the CPU's **Fan speed** in Cooling Health, matching how GPU fan speed is shown. DeadWare uses a motherboard or AIO header labeled as a pump (for example `Pump Fan` or `AIO Pump`), otherwise the `CPU Fan` header. Unlabeled headers such as `Fan #2` are not used.
- Requires **Extended sensors** and a single CPU. Reference comparisons also match comparable pump speed.
- The new sensor mapping starts CPU Cooling Health history and references under a new setup; save a fresh CPU reference after updating. GPU history is unchanged.

## 0.6.36 — See-through glass windows

- Add **Settings → Window glass** with **Off**, **Subtle**, **Balanced** (default) and **Strong**. Glass levels let the blurred desktop and windows behind show through the dashboard, graph pop-outs, battery history and widget using the Windows 11 acrylic backdrop, with thinner tinted panels and brighter highlighted edges.
- Windows shows acrylic only in the focused window, so unfocused DeadWare windows switch to the theme's solid surface until selected again. Pop-up menus stay solid.
- Off keeps the previous solid glass-panel look. Windows 10 and Windows 11 21H2, disabled Windows Transparency effects, Blackout and High Contrast stay solid.

## 0.6.35 — Elder Scrolls themes

- Add **Skyrim** and **Mage** beneath the hover-open **Elder Scrolls Themes** submenu in Color theme. Skyrim uses charcoal, silver and frost blue; Mage uses deep violet, arcane cyan and antique gold.
- Add readable Nordic-style lettering, an original subdued stone background, and decorative runes along live graph traces across the dashboard, graph pop-outs and widget. Keep numeric readings clear and preserve data gaps.
- Remember the selected theme across launches while preserving custom graph colors/styles and keyboard menu navigation. Windows High Contrast uses system fonts and undecorated traces; disabled transparency keeps solid backgrounds.

## 0.6.34 — Readable long-range network graphs

- Scale smoothed 1-, 3- and 6-hour network graphs to the highest download or upload average, with headroom, so brief traffic spikes no longer flatten the trend.
- Keep the min/max band, clipped at the plot ceiling for larger spikes, and retain full-data statistics. Raw mode continues to scale to the original peaks.

## 0.6.33 — Glass-panel refresh

- Give dashboard panels, controls, tables, graph pop-outs and the desktop widget tinted glass surfaces with subtle highlights and soft borders.
- Refine the Overview with consistent metric cards, larger readings, compact pop-out actions and separate download/upload statistics.
- Give graphs recessed plotting surfaces and soft trace shadows, with subtle live-chart fills; retain battery state markers, cooling ranges and missing-data gaps.
- Carry the appearance through all seven palettes, with solid surfaces for Blackout, Windows High Contrast and disabled Windows transparency.

## 0.6.32 — GPU fan graph appearance

- Add a dedicated GPU fan color and style button beside the GPU temperature color button on the overview.
- Choose the fan trace's color, solid/dashed/dotted pattern, thickness, neon glow and glow strength independently of the temperature trace.
- Save the fan appearance across launches and share it with temperature pop-outs and the widget. Fan labels and legend samples follow the selected appearance; the default remains dotted in the theme's fan color.

## 0.6.31 — Graph smoothing toggle

- Add **Smooth long-range graphs (1, 3 and 6 hours)** in Settings. Switch between the smoothed average/range view and raw traces across the dashboard, pop-outs and widget without restarting.
- Remember the choice across launches. Smoothing stays on by default; shorter ranges remain detailed in either mode.
- Preserve recorded readings, peaks, gaps and full-data statistics in both modes, with large raw histories prepared in the background too.

## 0.6.30 — Blackout theme and stable chart scrolling

- Add **Blackout** to the Color theme menu: pure-black backgrounds and panels, restrained charcoal controls and borders, and clear neutral text.
- Apply Blackout across the dashboard, widget, detached graphs, connection maps and supported native window frames. Graph colors remain distinct, and custom graph colors/styles continue to apply.
- Fix long-range smoothing regrouping old readings on each refresh. Historical averages retain their shape and move left with time.

## 0.6.29 — Graph thickness and glow controls

- Add **Line thickness** (50–300%) and **Glow strength** (0–300%) sliders to graph color popouts. 100% keeps the original look; glow strength becomes available when Neon glow is enabled.
- Fix the color popout closing when choosing a line pattern or using its controls. Default, Solid, Dashed and Dotted are now directly selectable within the popout.
- Save all style choices per trace and share them with the widget, detached graphs and reopened windows. Widget legend widths follow the selected thickness.

## 0.6.28 — Smoother graphs and custom line styles

- Add **Default, Solid, Dashed and Dotted** line patterns and optional **Neon glow** to each graph's color popout. Choices are saved per trace and shared with the dashboard, graph windows and widget; legends follow the selected pattern.
- Make 1-, 3- and 6-hour graphs easier to read with an average trend line and a subtle min/max band. Real peaks, gaps, current readings and summary statistics remain represented.
- Prepare large chart histories in the background and scroll traces smoothly between refreshes, reducing the polling interruptions to map dragging and animation.
- Reuse connection-map paths when only traffic changes and reduce repeated projection work while panning.

## 0.6.27 — Gentler transitions and interactive widget maps

- Ease active connection waves into idle: brightness, wave size and motion settle gradually instead of snapping. Returning traffic resumes smoothly from the current wave phase.
- Enable drag-to-pan and mouse-wheel zoom in the widget's World and North America views. Arrow keys and +/− also work when the map has focus; Home resets the current region.
- Keep the widget's custom view during live refreshes and Home-city changes, independently of the main map.

## 0.6.26

- Replace straight dashed connection overlays with DeadEye-style breathing oscilloscope waves. Download and upload keep their colors and travel in opposite directions; Animate pauses wave motion.
- Halve connection-dot sizes in the widget and reduce its home marker and traveling highlights.
- Fade in state and province borders as you zoom into either map, including US states, Canadian provinces and Mexican states. The widget's NA view shows these details automatically.
- Bundle regional border data for offline use and cache projected geometry between camera changes.

## 0.6.25 — Fine connection lines and country borders

- Restyle connection arcs with fine layered filaments, restrained glow and a brighter selected connection, inspired by DeadEye's node links.
- Add subtle country borders to the main globe/2D map and the widget, using bundled offline geography.
- Cache border geometry and strand shapes, and keep quiet/stopped traffic static.

## 0.6.24 — Connections map details

- Replace the all-connections table with a compact list that follows visible map nodes in Live and History.
- Open connection details beside a selected node, following map movement and clearing stale details across view changes.
- Inspect saved map representatives directly, without table paging; distinguish connection traffic from grouped-location totals.

## 0.6.23 — North America widget view

- Add a small **NA** button on the widget map to switch between World and a closer North America view.
- Save the view choice and keep it during live updates, origin changes and widget reopening.

## 0.6.22 — Map-only Connections widget

- Show only the map in the Connections widget, without headings, legends, status text or transfer-rate readouts.
- Fit the full world across the widget panel and use the available height when the map is the only selected widget item.
- Place the map below the selected graphs and keep it fully reachable by scrolling on small displays.

## 0.6.21 — Connections map and history

- Add Connections with a rotatable globe, 2D map, approximate country/city locations and a searchable application/peer table with traffic direction, bytes and rates.
- Save optional device-local endpoint history, enabled by default, with pause/resume, up to 30 days of retention, minute playback, filters and CSV export.
- Add **Connections map (live)** under **Settings → Widget graphs**, off by default. It can appear alone or with other graphs and stays live while Connections displays history.
- Resolve public endpoints with an optional explicitly downloaded local DB-IP City Lite database and choose an approximate home city from an offline list.
- Show local and unknown peers, capture gaps, incomplete intervals and omitted map locations without inventing routes or traffic.

## 0.6.20 — GPU fan speed on temperature graphs

- Add the saved **Show GPU fan speed on temperature graphs** Settings option, off by default and applied immediately to the dashboard GPU temperature graph, GPU/joined temperature pop-outs and widget temperature graph.
- Plot the selected GPU's first fan RPM as a dotted warning-color line on a separate left RPM scale, with temperature on the right °C scale.
- Keep missing fan readings unavailable with gaps, and preserve valid 0 RPM readings.
- Save the last six hours of GPU fan history from this version onward; earlier samples remain unavailable.

## 0.6.19 — Graph colors and themed text

- Add filled-circle color pickers to Overview graphs, with presets, custom colors and a theme-default reset.
- Save independent CPU/GPU usage and temperature, RAM, download and upload colors across graph windows, widget traces, readings and legends.
- Match headings, body text, controls, tables, tooltips and graph labels to the selected theme's colors and fonts.
- Remove page-header subtitles and their spacing, and simplify the Network page title to Network.

## 0.6.18 — Neon color theme

- Add Neon to the sidebar's Color theme selector, with dark surfaces, a cyan accent, green CPU readings, magenta GPU readings and blue downloads.
- Apply Neon across shared app windows and save the selection for the next session.

## 0.6.17 — Remove widget footer

- Remove the text footer beneath the widget graphs.
- Give the graphs the space previously reserved for that row.

## 0.6.16 — Themed graph range dropdown

- Replace the six graph range buttons with a compact dropdown on the dashboard and in pop-out options.
- Keep the same 1-, 5- and 15-minute plus 1-, 3- and 6-hour choices, with one saved selection shared across windows.
- Match the dropdown to the selected app theme.

## 0.6.15 — Browse release note history

- Add Show release note history in Updates to browse all normal published versions, including current and older releases, newest first.
- Refresh history on each explicit opening and return to current or missed-version notes with Hide release note history.
- Support cancellation and readable retry states without changing update, download or rollback behavior.

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
