# Per-core CPU usage (0.10.0)

Status: proposed on 2026-11-02; approved by the user on 2026-11-02 after 0.9.1 shipped. First of three planned releases (per-core CPU, then Storage, then Processes).

## Intent

DeadWare's CPU usage graph shows one machine-wide line. Modern desktop chips have 8–32 cores and the interesting question is often not "how busy is the CPU" but "is one core pinned while the rest idle". The per-core load sensors already arrive every poll from LibreHardwareMonitor and are dumped into `Snapshot.Values`; nothing reads them. This release draws them, as one thin line per core under the existing bold total, on the graphs the user already watches, behind a Settings switch that is off by default.

This is a minor-version milestone, 0.10.0, because it adds a saved setting, new persisted graph-history keys and a visible change to three graphs.

## Proposed experience

- **Settings → Monitoring** gains **Show per-core CPU usage on usage graphs**, a saved switch, off by default. Turning it on or off applies immediately to open windows, like **Show GPU fan speed on temperature graphs**.
- When on, the **dashboard CPU usage card**, the **separate CPU usage pop-out** and the **widget Usage graph** draw one thin line per core beneath the bold total. The joined CPU/GPU pop-out, the joined usage trace in the widget's GPU line, the Usage history page, Cooling Health and Alerts are unchanged.
- Core lines are thin (half the user's line thickness), solid, with no area fill and no neon glow, in a ramp of tints derived from the theme's CPU colour so they read as one family. The total keeps its full thickness, pattern, fill and glow and is drawn last, on top. Long-range smoothing (1, 3 and 6 hours) applies to core lines exactly as to any trace, including the raw-trace setting.
- Hovering a graph keeps the existing chip and adds, after the total, the eight highest cores at that sample as `Core 3 · 41%` lines, then `+N more` when there are more than eight. A gap in the total shows nothing, as today.
- The CPU card's subtitle, where the device name shows, appends `· N cores` while the switch is on and cores are available. Legends do not change: there is no per-core legend.
- On a machine where no per-core reading is available the switch stays visible and saveable, the graphs draw only the total, and the hardware status line appends `Per-core CPU usage unavailable.`
- Cores are labelled `Core 1`…`Core N` in sensor order. Performance/efficiency core naming on hybrid chips is not in this release.

## Data and storage

- The collector publishes `cpu/core/{i}/usage` for `i` = 0…N−1 alongside `cpu/usage`, and the `Reading` carries `CpuCoreCount` (0 when unavailable). Sources, in order:
  1. LibreHardwareMonitor CPU load sensors named `CPU Core #k` (1-based `k`; ordered by `k`). Where a chip reports `CPU Core #k Thread #t` variants as well, only the per-core sensor is used; where only thread sensors exist, each thread is a line.
  2. Otherwise Windows: `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` per logical processor, turned into a percentage by the same idle/kernel/user delta that the existing machine-wide fallback uses. This runs only when LibreHardwareMonitor gave no per-core loads, so the common path adds no Windows calls.
  3. Neither: no `cpu/core/*` keys, `CpuCoreCount` 0.
- At most 64 cores are published and persisted; further cores are dropped with a one-time entry in the hardware status.
- `GraphHistoryProjection.Mappings` adds `core/{i}/usage` to the CPU identity's metrics, for the cores present in the reading. The CPU identity hash is unchanged, so a user's existing six-hour CPU history still matches, and the core keys restore through the same new-mapping path as any key seen for the first time. The 64 MiB cap and 15-second batch writes are unchanged; at 32 cores and 0.5-second polling the added rows stay well inside the cap over six hours.
- `cpu/core/*` is never folded into Usage history minute rows, never evaluated by Alerts, and never read by Cooling Health. Sensor-table statistics already list the raw per-core sensors and are unchanged.

## Implementation choices

These are the builder's proposed defaults, not additional user requirements: thin solid family-tinted lines; eight-core hover cap; `· N cores` subtitle; a 64-core cap; LibreHardwareMonitor first with a Windows fallback; no legend entries, no per-core colour pickers, no per-core statistics, no heat-map or small-multiple layouts, no P/E naming, no widget legend change, no joined pop-out change.

Reuse `TraceSpec` and the existing per-trace path cache in `Charts.cs`, `CompactGraph.cs` and `WidgetGraph.cs`; the `ShowGpuFanSpeed` settings plumbing in `UiSession`, `Windows.cs` and the Settings Monitoring card; `GraphHistoryProjection` for persistence; the palette-derived token code in `Theme.cs` for the tint ramp. Keep the chart's hover tolerance, gap handling and smoothing untouched.

## Verification and delivery

- Test core-key selection from synthetic sensor lists: `CPU Core #k` only, core plus thread variants, thread-only, none, out-of-order `k`, more than 64 cores.
- Test the Windows per-processor fallback's delta arithmetic, including the first sample (no result), counter wrap and a processor count change.
- Test `GraphHistoryProjection.Mappings` includes core metrics with the unchanged CPU identity, that a restore round-trip returns core values, and that a reading with fewer cores than the saved history restores only the cores present.
- Test the setting's default, persistence and `Changed` propagation.
- Test the trace-spec builder returns core specs only when the switch is on and cores exist, that the tint ramp yields N distinct colours for 1, 8 and 64 cores in every theme, and that hover text lists the total, at most eight cores in descending order and the correct `+N more`.
- Render the CPU card, the CPU pop-out and the widget offscreen in Midnight, Blackout and Daylight with the switch on at 8 and 32 cores through the existing preview tool; the CI preview job covers all themes.
- Manual on a Windows machine: 0.5-second polling, six-hour restore after Quit and relaunch, High Contrast, and toggling the switch with every affected window open.
- Run the full suite and package 0.10.0 with `scripts/publish.ps1`. Commit the design, plan, implementation, tests and validation to a feature branch, open a pull request, verify CI, and prepare the 0.10.0 release notes, README section and changelog.

## Inline builder check

Every integration point exists: the collector already carries the sensors, the projection already handles new keys, the fan-speed switch gives the settings pattern, and the chart renderers already draw multiple `TraceSpec`s per graph. No new store, window or page is needed. Implementation starts once the user approves this spec and chooses the development mode.
