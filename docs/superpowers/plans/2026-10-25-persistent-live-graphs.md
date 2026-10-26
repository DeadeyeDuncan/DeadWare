# Persistent live graphs implementation plan

**Goal:** Automatically restore the CPU/GPU usage and temperature graphs and combined network graph across normal quits and Windows restarts.

**Architecture:** Reuse SQLite in a separate `live-graph-history.db`; a bounded background worker saves graph-only samples every 15 seconds and on shutdown. Match saved metrics to the current hardware, translate UTC timestamps into the current monotonic timeline, and merge restored samples without replacing live readings. Retain six elapsed hours, including gaps while the program was stopped. Cooling Health storage and per-process network accounting remain separate.

**Scope:** User's direct persistence request extends the existing six-hour graph feature. No additional collector, packet data, application names, imports, archive browser, or retention controls. Existing sensor-table history remains session-only. Old versions ignore the new database, which makes rollback compatible.

## Tasks

- [x] Core store: `GraphHistoryStore.cs` and tests. Schema 1; six-hour / 21,601-row retention, 64 MiB database cap, bounded payloads and journal, transactional/idempotent writes, preserve damaged or newer files. Verify real SQLite round trips, boundaries, locks, and full/corrupt storage.
- [x] Restore merge: `Monitoring.cs` and tests. Snapshot session metadata; explicit plot discontinuities; merge old metrics into matching restored snapshots, let live snapshots win collisions, exclude expired/nonfinite samples, preserve retention and monotonic ordering. Verify fast restarts and late device restoration.
- [x] Runtime: `GraphHistoryService.cs`, optional projection helper, and app tests. Nonblocking 120-item queue, periodic flush independent of arriving samples, immutable graph-only projection, stable hardware mapping and retry after an empty first publication, visible storage failures, bounded background startup.
- [x] Integration: `UiSession.cs`, dashboard status and shutdown tests. Subscribe/start once; cancel providers and drain both history workers concurrently. Preserve the existing three-second Windows shutdown budget and leave live monitoring usable on storage failure.
- [x] Delivery: update usage/release notes, run the complete suite and focused offscreen/fixture verification, review, package, safely install after tray Quit, verify restoration, and publish the preview.

## Acceptance and limits

- Old trace points reappear in the selected range after restart without becoming current readings. Offline periods, collection failures, and session boundaries stay visibly disconnected.
- Unknown or changed hardware does not inherit another model's history. Identical ambiguous GPUs are not restored by enumeration order. A same-model physical replacement cannot always be detected from available metadata.
- Disk access never occurs in collector callbacks or on the UI thread. A stalled provider cannot prevent the periodic flush or the separate history shutdown drain.
- Corrupt, locked, full, or newer-schema storage reports a status error and preserves live monitoring and the original file.
- Retention means the last six wall-clock hours, not six accumulated hours of uptime. A sudden termination may lose the latest unflushed batch (normally up to 15 seconds); a normal quit drains it.
- No Computer Use, new hardware probes, stress tests, or unrequested settings changes. Use synthetic readings and temporary databases for automated tests.
