# Alerts and thresholds (0.8.0)

Status: proposed on 2026-11-02; awaiting the user's approval before implementation. A 0.7.1 settling release (listed at the top of the plan) comes first.

## Intent

Usage history (0.7.0) gave DeadWare a year of per-minute CPU, GPU, RAM, network and per-application data. Alerts are the first feature that pays that data back: a user who leaves DeadWare in the tray should learn when the GPU has run hot for ten minutes, when RAM has been pinned, when an application has used more bandwidth today than they expect, or when the battery is about to run out, without opening the dashboard. Cooling Health already has the only alert in the app, with tray notifications, acknowledgement, a once-per-24-hours limit and warnings logged even when muted. This release generalizes that plumbing into user-set rules and uses usage history both to suggest sensible thresholds and to show the evidence behind each alert.

This is a minor-version milestone, 0.8.0, because it adds a page, a settings structure and a new local database.

## Proposed experience

- Add **Alerts** to the sidebar. The page has **Rules** at the top and **Recent alerts** below, and Settings gains a short summary with an **Open Alerts** button.
- **Rules** are a list the user adds to, edits and switches on and off. Four kinds in this release:
  - **Device metric**: CPU usage, CPU temperature, selected GPU usage or temperature, or RAM usage at or above a value for 1, 5, 15 or 60 consecutive recorded minutes.
  - **Network rate**: download or upload at or above a bytes-per-second value for the same durations.
  - **Application traffic**: a named executable's download plus upload at or above a byte budget within one local calendar day.
  - **Battery**: charge at or below a percentage while on battery. This one evaluates immediately rather than per minute.
- Beside each threshold input the page shows what usage history recorded for the last 7 and 30 days: the highest minute average, the average, and the share of recorded minutes at or above the current input. This is the suggestion mechanism: the user sees "your CPU temperature was at or above 85°C in 2.1% of the last 30 days' minutes" and chooses accordingly. No threshold is set automatically.
- When a rule fires, DeadWare shows a tray notification with the rule name, the observed value and duration, and adds an entry to **Recent alerts** with **Acknowledge** and **View in Usage history**. The second button opens Usage history on the Day view covering the alert so the user sees the surrounding minutes, not a bare number. Clicking the notification opens the Alerts page.
- A rule notifies once per episode. It re-arms when the metric stays below the threshold (minus a small hysteresis: 3°C, 5 percentage points or 10% of a byte value) for the rule's duration, and never notifies more than once per hour even if it re-arms sooner. Application-budget rules notify once per local day. An unacknowledged alert does not block later episodes; acknowledgement is a bookkeeping mark, as in Cooling Health. Every episode is logged whether or not notifications are enabled.
- A **Notifications** checkbox on the page mutes tray balloons for all rules without deleting them or stopping the log. Cooling Health keeps its own notification setting.
- Alerts state plainly what they are: a threshold the user chose was crossed in observed data. They are not a hardware diagnosis, a security finding or a billing figure.

## Evaluation and storage

- Device and network rules evaluate the per-minute averages that the usage history folder already produces, so they see exactly what Usage history later shows. The usage service exposes each closed minute to an alert evaluator on the same background worker; nothing is added to the collector's publication path. A minute with no reading for the metric does not count toward or against a duration; a sample gap (sleep, restart) resets the consecutive count.
- Application rules sum the per-minute application rows for the current local day in memory, seeded from the store at startup so a restart mid-day does not forget the morning's traffic. Capture-gap minutes neither add bytes nor reset the day.
- Battery rules evaluate each publication's battery reading in the alert service, with the same hysteresis idea (re-arm at threshold plus 5 points or when charging).
- Rules live in `settings.json` as a list under `Alerts`, each with an id, kind, metric or application name, threshold, duration, enabled flag and creation time, normalized on load like other settings. The alert log lives in `%LOCALAPPDATA%\DeadWare\alerts.db` (id, rule id, rule summary at the time, start, end, peak value, acknowledged) with 400-day retention and a 64 MiB cap, written by the same bounded background pattern as the other stores and drained on Quit and sign-out.
- Tray notifications reuse the existing `TrayIcon` balloon with a generic title and click action. The rule summary and the observed value are the only text shown; application names are the executable names DeadWare already displays.
- Nothing is uploaded. Rules and the log stay in this Windows profile.

## Implementation choices

These are the builder's proposed defaults, not additional user requirements: four rule kinds; 1/5/15/60-minute durations; per-episode notification with hysteresis and a one-hour floor; a single mute switch; suggestions as recorded statistics rather than auto-set thresholds; a separate `alerts.db`. No actions on alert (scripts, shutdown, email, webhooks), no GPU fan or CPU pump rules, no disk or motherboard sensors, no anomaly detection, no per-rule quiet hours, and no Year/All usage ranges in this change.

Reuse `UsageMinuteFolder` output, `UsageHistoryStore.ReadMetric` statistics for suggestions, `CoolingAlertPolicy`'s shape for the notification rules, `TrayIcon` balloons, `BatteryReading`, the theme and panel primitives, and the window and settings codecs. Keep Cooling Health's own warnings, references and notification setting unchanged.

## Verification and delivery

- Test rule normalization from settings (bounds, unknown kinds dropped, duplicates kept by id), the evaluator's consecutive-minute counting across gaps, unavailable readings, hysteresis re-arm, the one-hour floor, application day boundaries in local time including a DST change, battery immediate firing and re-arm, and that disabled rules never fire.
- Test the store's initialize, reopen, retention, cap, acknowledgement and bounded reads, and that a corrupt file reports without stopping monitoring.
- Test the service's wiring: a closed usage minute reaches the evaluator, notifications respect the mute switch, episodes are logged when muted, the day accumulator seeds from the store, and Quit drains the log within the history deadline.
- Render the Alerts page offscreen in three themes with no rules, several rules with suggestions, and a log with acknowledged and open entries; verify the Usage history hand-off selects the right day.
- Run the full suite and package 0.8.0 with `scripts/publish.ps1`. Record in the validation notes what ran only on CI and what needs a Windows machine (tray balloon appearance, click-through, a real multi-day accumulation).
- Commit the design, plan, implementation, tests and validation to a feature branch, open a pull request, verify CI, and prepare the 0.8.0 release notes, README section and changelog.

## Inline builder check

The usage folder and store, the cooling warning policy and tray balloon, the battery reading and the settings codec supply every integration point. A separate store is needed because alert episodes are user-facing records with acknowledgement state, not metric minutes, and must survive usage-history eviction. Implementation starts once the user approves this scope and 0.7.1 has shipped.
