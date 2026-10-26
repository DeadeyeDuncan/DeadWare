# History and cooling health — proposal

Status: researched proposal, not implemented. This describes a possible next feature; it does not certify the current computer's cooling health.

Review: an independent builder check found no important unanswered questions for planning this first release. The user approved this scope and explicitly resumed implementation on 2026-10-25 after a temporary pause. The unknown AIO model limits later pump/coolant integration, not the thermal-history scope below.

## User intent and established context

The user wants to retain usage/temperature data, establish normal operation under load, and notice changes that could precede hardware trouble, such as an AIO pump problem or internal blockage. The current PC uses an AIO liquid cooler; its model and sensor connections are unknown. DeadWare must remain useful on other hardware too.

DeadWare currently keeps at most 900 samples/15 minutes in memory. Earlier elevated CPU/GPU diagnostics captured CPU/GPU temperatures and package power. They did not capture CPU cooler, pump, coolant or flow readings. Motherboard/controller collection was disabled, so that absence does not establish lack of support. A read-only search of present Windows device names did not identify a cooler by common cooler vendor/product names.

## Proposed first release

1. **Local thermal history.** Persist CPU/GPU temperature, utilization and usable package power, plus supported clock/fan readings. Provide day/week/month views and CSV export. Show recording status and gaps. Preserve the existing live graphs and compact windows.
2. **Explicit reference period.** Let the user select a period when the cooling setup was believed to be operating normally. Keep that reference fixed, separate from rolling recent history. Call it a reference, not proof that the hardware was healthy.
3. **Comparable-condition trends.** Compare sustained windows at similar package power and utilization, using fan/coolant/ambient context only when actually available. Show reference range, current range, difference, matched coverage and the missing context that limits interpretation.
4. **One explainable warning.** An optional tray notification flags a sustained positive temperature deviation from the reference, with a supporting chart. It identifies a change worth investigating, not a failed part or remaining lifespan. Record, acknowledge and mute warnings; use persistence and a cooldown to avoid alerting on every sample.
5. **Separate references per device/configuration.** Changing the selected GPU must not mix devices. Provide a new reference after hardware replacement, cleaning, remounting, fan-curve or power-limit changes. Retain the previous reference for historical comparison.

Pump-specific alarms, flow monitoring and cooler integrations are a follow-on to confirmed sensor discovery/mapping. They are not a prerequisite for basic thermal history and comparison. The AIO model would help determine that follow-on scope, but is not a blocker for the first release.

## Proposed defaults and builder choices

These are design recommendations, not preferences already chosen by the user:

- Local storage only, with recording visibly controlled in the app. No cloud upload and no application names or packet information in the thermal-history store.
- A bounded local database, initially 30 days of detailed thermal windows and 90 days of compact summaries, with a configurable retention limit and visible storage usage. The builder should select aggregation intervals and a hard storage cap from measured data volume; do not store an unbounded per-second dump of every sensor.
- The builder should select a transactional embedded store compatible with the existing .NET 10 Windows application. Use one background writer, batched transactions and bounded query results. Keep storage I/O and analysis off the collector and UI threads.
- Calibrate and document conservative matching/deviation defaults through replay fixtures. Expose their meaning in the UI. Do not present statistical sensitivity as a validated probability of failure. Specific thresholds are engineering defaults to test, not manufacturer guarantees.
- Monitoring should continue if recording fails, is disabled, or falls behind. Surface the recording error or dropped-history interval rather than quietly hiding it.

## Interpretation and data-quality requirements

- Prefer comparable package power plus load over utilization alone. Workloads with the same utilization can have different thermal behavior. Do not silently replace missing power with zero or compare dissimilar conditions.
- Require enough valid reference coverage and sustained comparable operation. Until then display **Learning** or **Not enough comparable data**. A missing comparison is not a reassuring health result.
- Treat startup/warm-up, workload transitions, sleep/resume, stale readings and collection gaps separately. Do not train a reference on clearly invalid or throttled samples. Where a device normally operates against a temperature target, temperature alone may conceal declining performance; explain this limitation rather than report it as healthy.
- Keep raw sensor type, hardware identity/topology and data validity alongside values. A percentage may be load or control duty; a fan-named RPM sensor may not identify a pump. Never infer pump failure from an arbitrary fan channel.
- Zero-RPM fan operation may be intentional. Missing telemetry is different from a confirmed zero reading. Direct pump/flow alerts require explicit supported sensor mapping and expectations for when that device should be operating.
- Use a local machine/device/configuration identity plus sensor mapping version. A provider path that changes with enumeration is not sufficient proof that two samples belong to the same physical device. Do not put serial numbers in exported reports by default.
- Record UTC wall time and session-local ordering/duration. Preserve gaps and valid counts through aggregation. A wall-clock change or process restart must not bridge unrelated samples into one stable-load window.
- Room temperature, cooling settings, dust, mounting and workload differences can explain changes. If context is unavailable, state that uncertainty. Do not claim a reservoir/cold-plate blockage, pump failure date or remaining useful life from temperature trends.

## Fit with existing architecture

Subscribe to `Collector.SampleAvailable` before collection starts. That callback should only enqueue bounded work and must not throw into the collection loop. Keep `SampleHistory` as the fast live cache. A separate storage/analysis component owns durable history, rollups and queries.

Preserve CPU/GPU-only collection as the default and keep extended motherboard/controller sensors opt-in. Do not create a second hardware poller, automatically enable broad probes, change fan/pump controls, run stress tests, or add automatic shutdown actions.

Integrate storage shutdown with the existing explicit Quit and bounded Windows session-ending paths: stop production, finish or explicitly record incomplete queued work, and close the store without requiring a UI continuation during sign-out. Recovery must tolerate process termination, a locked store and disk-full conditions.

## Acceptance evidence before delivery

- Replay independent fixtures for comparable healthy-reference data, sustained thermal drift, changing load/power, warm-up, missing sensors, zero-RPM fan stop, gaps, sleep/restart, clock changes and changed device identity.
- Verify no warning during insufficient reference coverage or unsupported sensor conditions. Verify sustained deviations generate one useful warning with cooldown, and that recent degraded readings do not overwrite the reference.
- Verify history survives a normal quit/restart and that partial/corrupt/locked storage produces an explicit recording state while live monitoring continues.
- Measure storage growth, bounded queue behavior and live sampling/UI responsiveness while recording and querying. Include the prior tray, startup, pop-out and settings checks appropriate to touched paths.
- Inspect the new views offscreen and use a user-run native smoke check with Computer Use off. Do not simulate a hardware fault by stopping the user's pump or obstructing cooling.

## Sources and limits

- [Intel: Information about processor temperature](https://www.intel.com/content/www/us/en/support/articles/000005597/processors.html) — operating temperatures depend on configuration/workload; temperature by itself is not a reliable cooling diagnosis. Intel-specific limits must not be applied to AMD hardware.
- [NZXT: Is my Kraken cooler failing?](https://support.nzxt.com/hc/en-us/articles/43715888947611-Is-my-Kraken-cooler-failing) — AIO symptoms can involve pump power, mounting, fan/airflow or internal blockage; sensor/header availability depends on the model. This is evidence about diagnostic ambiguity, not identification of the user's AIO as an NZXT product.
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — the existing sensor provider exposes supported temperatures, fan speeds, loads and other telemetry, with hardware-specific coverage.
- Local code and `I:/DeadWare/.superpowers/sdd/2026-10-25-deadware/elevated-hardware.json` supplied the bounded existing-host evidence. No new live hardware polling or extended-sensor enablement was performed for this investigation.

This proposal concerns early warning of cooling changes. Accurate failure forecasting and universal AIO support are not established by the available data.
