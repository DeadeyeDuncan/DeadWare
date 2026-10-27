# Long-term battery history

Status: approved by the user on 2026-10-27, including battery-saver history.

## Intent

The user wants to see how battery percentage falls over time and when the computer recharges. History should last for years, potentially the lifetime of the device. It should have a dedicated view/window like Cooling Health, separate from the six-hour live graph cache. GitHub must contain the work so development can continue on other computers.

## Proposed experience

- Add **Battery history** beside Cooling Health and an **Open battery history window** action. The same view works in the dashboard and its own resizable, remembered window.
- Show current battery percentage, plain charging/power status and Windows battery-saver status. Chart charge from 0 to 100 percent, distinguish charging and battery-saver periods, and expose Day, Week, Month, Year and All history ranges. Unknown saver status stays unknown rather than appearing disabled.
- Preserve real date/time spacing. Missing readings, sleep, stopped monitoring and process restarts are gaps. A long-range chart summarizes readings to fit the screen; it must not suggest that a charging state lasted throughout an unobserved interval.
- Start recording automatically when DeadWare runs on a battery-equipped machine. Provide a recording checkbox, storage/status text and CSV export. A desktop with no system battery shows that clearly while older saved history stays viewable.
- Recording starts after this upgrade. Existing device lifetime history cannot be reconstructed. Stored readings survive normal app upgrades and restarts.

## Storage and collection

- Use native Windows power status in the existing collector, without elevation, drivers or additional hardware scans. Record the system battery total, which also supports machines with multiple batteries; this is device charge history rather than per-battery wear analysis.
- Retain one observation per minute plus observed changes in charging/power or battery-saver state. Keep percentage, power state, saver state, UTC time and collection segment; segment boundaries preserve restart, queue loss, clock changes and collection gaps. Include saver state in CSV. Do not store every one-second publication.
- Use a separate local SQLite file, `%LOCALAPPDATA%\DeadWare\battery-history.db`. Keep saved observations indefinitely: no automatic expiration or deletion to meet a storage cap. Show actual storage use. A full/locked/corrupt store reports an error while the other monitors continue.
- Keep writes on a bounded background worker, flush periodically and on normal Quit, and join the existing bounded Windows sign-out shutdown path. An abrupt power loss can lose the unsaved batch.
- Keep historical reads and CSV streaming off the UI thread. Bound chart results with SQL time buckets; preserve minimum/maximum percentage and charging-state coverage. Long-range summaries are a display operation and do not delete the original observations. Do not draw lines across ambiguous aggregated gaps.
- Partition records by the existing stable local machine key. Source and version history are synchronized through GitHub; battery readings and settings remain local to each device. Export supports an independent backup of recorded data.

## Implementation choices

These are the builder's proposed defaults, not additional user requirements: minute observations; Day/Week/Month/Year/All ranges; built-in Windows aggregate battery percentage; an opt-out recording control; CSV export; an independent database with no retention expiry. No wattage chart, health score, battery replacement detection, cloud data sync or retroactive Windows battery-report import in this change.

Reuse existing WPF theme/layout primitives, SQLite dependency, collector publications and window lifecycle. Keep the existing CPU/GPU/network and Cooling Health behavior intact. Work inline as requested by the user's standing preference. Review the design and implementation inline rather than dispatching agents.

## Verification and delivery

- Test native value conversion for valid, unknown and absent battery states, including charging versus plugged in but not charging and battery-saver on/off/unknown.
- Test observation cadence, immediate state changes, unknown values, restart/gap/clock boundaries, bounded queue loss and quit/sign-out draining.
- Test SQLite close/reopen, machine isolation, oldest-history retention across years, bounded whole-history queries, chart range summaries and CSV correctness.
- Build and run the full existing test suite. Render the real WPF view offscreen in dark/light themes with empty, charging, discharging and multi-year fixtures.
- Run a short diagnostic sample on this Surface and verify current percentage/state. Do not infer physical charging-transition proof from a simulated fixture.
- Commit the design, plan, implementation, tests and validation to a GitHub feature branch, open a reviewable pull request and verify its build. Produce a versioned portable build for testing on this tablet; keep the installed app's data intact during any upgrade.

## API references

- [SYSTEM_POWER_STATUS](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status): percentage uses 255 for unknown; BatteryFlag distinguishes charging, unknown and no system battery.
- [GetSystemPowerStatus](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus): native system power snapshot, available to desktop apps without an added dependency.

## Inline builder check

The existing collector, UI session, SQLite history workers and Cooling Health page supply the integration points. A distinct store is necessary because the six-hour graph cache and Cooling Health retention would discard lifetime battery history. This lifetime design supersedes the original six-hour proposal. The user approved the complete design, including saver history, and authorized implementation on 2026-10-27.
