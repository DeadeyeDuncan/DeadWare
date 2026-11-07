# Connections history, alerts and controls (0.16.0 and 0.17.0) — design

Status: approved section by section by the user on 2026-11-07, in seven sections (scope and shape, storage, the traffic strip, the Destinations table, first network use, privacy controls, and errors, testing, docs and release). This written spec awaits the user's review. An implementation plan follows only after that review.

Date: 2026-11-07.

**Relation to other specs.**
- **Builds on:**
  - the Processes spec (`docs/superpowers/specs/2026-11-05-processes-design.md`, 0.13.0 and 0.14.0);
  - the per-app GPU spec (`docs/superpowers/specs/2026-11-06-per-app-gpu-design.md`, 0.15.0);
  - the earlier Connections (`2026-10-29-connections-design.md`), Usage history (`2026-11-02-usage-history-design.md`) and Alerts (`2026-11-02-alerts-design.md`) specs.
- **Where this spec is silent, the earlier specs apply.** Their shared rules hold throughout:
  - null, never zero, for an unknown value;
  - every "—" has a tooltip naming its reason;
  - status text holds fixed copy, counts and error codes, never names or paths;
  - native and storage work runs off the UI thread;
  - colours come from theme resources only.
- **This spec changes no rule of theirs except these, each stated in its section:**
  - **Connections spec (:70):** Connections history saves about one minute behind instead of the "target 15 seconds", and a crash can lose about the last minute (*Connections storage*).
  - **Connections spec (:20):** History's Unlocated count drops local and special peers, as Live already does (*Connections storage*).
  - **Connections page:** "Capacity shortened retention." shows under a narrower condition (*The traffic strip*).
  - **Usage history:** line charts count a bucket as full by the whole minutes that start in it (*The traffic strip*).
  - **Usage history:** the Applications card's share bar gives way to the strip (*The traffic strip*).
  - **Alerts spec (:14):** a fifth rule kind.
  - **Alerts spec (:21):** the one-hour notification floor does not apply to first network use (*First network use alerts*).
  - **Alerts spec:** an application traffic budget whose app is on Don't record these apps stops counting, its open episode ends, and it is re-seeded from saved minutes when the app is removed (*Don't record these apps and Clear history…*).
  - **Processes and per-app GPU specs:** Application resources' Everything else also holds apps on the Don't record list, and a one-app clear moves that app's minutes into it (*Don't record these apps and Clear history…*).
  - **Usage history and Processes specs:** retention deletes wait while a clear marker exists (*Don't record these apps and Clear history…*).
- **0.15.0 ships first.** It is specified in the per-app GPU spec: `process-history.db` version 2 with the GPU columns, up to 40 listed apps a minute plus Everything else, and a 192 MiB cap. Everything below that touches Application resources or `process-history.db` is written against that schema and its GPU values.
- **Anchors.** Code anchors are `origin/main` at 181eb54 (0.14.1). 0.15.0, and for 0.17.0 also 0.16.0, move some of them, so each section quotes doc text beside its line and says which anchors to recheck.

## Intent

**The outcome.** Connections shows where traffic went but not *when* each app used the network, and lists only the destinations the map can draw. Nothing tells the user when an app goes online for the first time. They also cannot keep an app out of history or remove what was saved. A public-source feature study of GlassWire found these to be the largest gaps on DeadWare's Connections side. DeadWare did not decompile or reverse-engineer GlassWire, and the study's GlassWire claims are unverified against its live pages.

These two releases close those gaps locally, with no new privilege, account or network traffic:
- **0.16.0 lets the user see it.**
  - A per-app traffic timeline under the map, and the same strip on Usage history with a way across to Connections.
  - A table of every destination, including local, special-purpose and unlocated ones.
  - Connections history that lasts.
- **0.17.0 lets the user watch and control it.**
  - An alert the first time an app uses the network.
  - A list of apps whose history is never saved.
  - A way to clear saved history.

**What the user said.** Every item under Decisions is the user's choice or an approval of a drafted default. In particular:
- two releases in one spec, with the 0.16.0 and 0.17.0 contents listed below;
- Approach 1 of three approaches offered;
- the billing-period data plan dropped from the release;
- hostnames, blocking, cloud lookups and per-app speed limits out of scope;
- "defaults are fine" for the open design questions and for the storage, strip, first-use and privacy defaults;
- the Destinations table approved as drafted;
- section 7 (errors, testing, docs and release) "looks right".

**Assumptions** (not stated by the user; each section relies on them):
- **Who it is for.** The person who runs DeadWare on their own Windows PC and wants to know which apps talk to the network, without sending anything off the PC. Per-app traffic needs DeadWare to run as administrator, as today. A standard user gets the device-wide parts and states that say what is missing.
- **Real retention varies.** The retention and timing figures come from a harness driving the real stores on Linux. Windows times will differ, though the ratios should hold. Browsers with many processes and addresses save more rows than the benchmark.
- **The bundled SQLite.** It is 3.53.4 (`SQLitePCLRaw.bundle_e_sqlite3` 3.0.5) on both builds. The win-x64 build's TEMP default is not known, which is why the read sets it explicitly.
- **Release order.** 0.15.0 merges before 0.16.0 starts, and its spec holds as written.

**What success looks like**, as the approved sections define it:
- **0.16.0:**
  - **Retention.** Idle and light use keep the full 30 days of Connections history. Today the 256 MiB cap is reached in about 4 days idle, or about 4 hours with 20 busy app–destination pairs.
  - **History speed.** A 7- or 30-day History view on a full per-minute file opens in about 2–2.5 s (decision 15; see For this review), Today in about 0.25 s, and the minute cursor stays under 5 ms.
  - **The strip.** It shows which of the top six apps used the network in each slot. Unrecorded time is hatched, never drawn as zero.
  - **Destinations.** The table's Internet, Local and Unlocated counts equal the summary's, and History's Unlocated count follows the Live rule.
- **0.17.0:**
  - **First network use.** After a quiet 3-day learning period, an app's first network use gives one log row. Apps seen within a minute share one balloon, so there is at most one balloon a minute.
  - **Don't record.** A listed app still shows on live pages, marked "not recorded", and none of its history is saved. Device totals stay true.
  - **Clear history.** One app's or all apps' history is cleared from all four histories together. Quit and sign-out wait for at most one step, and an interrupted clear finishes at the next start.
- **Both releases.** CI is green, the new CI measurements are recorded, and the checks by eye are listed as pending until the user has done them.

## Decisions

Each rule is tagged with the section in which the user approved it: §1 scope and shape, with the design questions answered "defaults are fine"; §2 storage; §3 the traffic strip; §4 the Destinations table; §5 first network use; §6 privacy controls; §7 errors, testing, docs and release. They are binding; the sections below detail them and never reopen them.

**Scope and shape**
1. Two releases, one spec. (§1)
   - **0.16.0, "See it":**
     - per-minute merge compaction of `connections.db`;
     - the History summary's Unlocated fix: local and special peers are not unlocated, as in Live;
     - the traffic strip on Connections History and on Usage history, with **Show in Connections**;
     - the Destinations table below the map and the strip.
   - **0.17.0, "Watch and control":**
     - first network use alerts;
     - **Don't record these apps**;
     - **Clear history…**.
2. **Approach 1, "Query what's stored".** (§1)
   - No store changes its format version: `connections.db`, `usage-history.db` and `process-history.db` keep theirs.
   - New tables go only into `alerts.db`, which also keeps its version.
3. **Connections History builds its grouped rows once per read** into TEMP tables, and every view on the page reads them. (§1)
4. **A clear runs on each store's own worker**, in bounded steps, behind a durable "clear in progress" marker. An interrupted clear resumes at the next start. (§1)
5. **Out of scope:** hostnames and reverse DNS, blocking or firewall rules, the data plan (dropped), cloud lookups and per-app speed limits. (§1)
6. **0.15.0 ships before 0.16.0.** Anything touching Application resources or `process-history.db` is written against 0.15.0's version 2 schema and its GPU values. (§1)

**Design questions** ("defaults are fine")
7. **Special peers** (shared CGNAT, multicast, documentation and other reserved ranges) are listed under All only, with a class column. They count as neither Local nor Unlocated. (§1)
8. **Excluded apps in Application resources** fold into Everything else, so totals stay true. The user guide says so. (§1)
9. **Learning period.** It lasts 3 calendar days and starts only when per-app capture has first run. Editing the rule keeps the start. (§1)
10. **Clearing an app.** Clearing an app for all time forgets it, so its next use alerts again. Don't alert entries stay. Excluded apps and "PID n" names are never learned or alerted on. (§1)
11. **Rollback from 0.17 to 0.16** drops the new settings lists, so excluded apps are recorded again. The release notes say so. (§1)
12. **Space after a clear.** There is no VACUUM; freed space is reused. The UI says that exported CSVs and Windows notification history are not cleared. (§1)
13. **The Usage strip** joins 0.14.1's shared cursor, and a click narrows the Applications table to that slot. (§1)

**Storage**
14. **The per-minute merge, as drafted.** One saved row per app and destination per minute, with today's row identity. Each minute is saved 2 s plus one to two polling intervals after it ends. (§2)
15. **History speed.** 0.16.0 accepts about 2–2.5 s for a 7- or 30-day History view on a full per-minute file, with no cache. (§2)
16. **The grouped read** uses `temp_store=MEMORY`: about 50 MiB of transient memory during a full-file read, and nothing in `%TEMP%`. (§2)
17. **Backward clock steps.** A step of up to 60 s holds the frozen minute until the clock passes it. A larger step retires it. (§2)

**Traffic strip**
18. **Series.** Six apps plus Other. (§3)
19. **Placement.** The strip goes directly under the map. The compact node list stays under the map, as today, now below the strip. (§3)
20. **Line charts.** 0.16.0 fixes the existing Usage history `IsFull` defect with the strip's whole-minute rule. (§3)

**Destinations table**
21. **Approved as drafted.** (§4)
    - One row per remote address, in Live and in History.
    - A saved All / Internet / Local choice that narrows only the table.
    - Counts that equal the summary's.
    - Selecting a row highlights its map nodes.

**First network use**
22. **The rule is added for the user.** It is added automatically, switched on, once, for new and upgrading users. A removed rule stays removed. (§5)
23. **Off and removed differ.** A switched-off rule keeps learning quietly. A removed rule stops learning, so adding it back alerts once for each app first used in between; the guide warns. (§5)

**Privacy controls**
24. **An all-apps, all-time clear** empties the known set but keeps the learning start, so there is no relearning. (§6)
25. **An "older than 7 days" clear** does not forget apps for first-use alerts. (§6)
26. **The clock-ahead limitation is accepted.** After an all-apps clear, Connections' expiry never moves back, so a clock that was ahead and is later set back hides new Connections history until the clock catches up. This is documented, not fixed. (§6)

**Errors, testing, docs and release**
27. **Errors.** (§7)
    - An unknown value is "—" with a reason tooltip, never 0.
    - An unreadable store shows "unavailable", never an empty period.
    - Status text holds fixed copy, counts and error codes, never names or paths.
    - All storage and native work runs off the UI thread.
    - Existing caps and journal limits are unchanged.
    - Interrupted work resumes.
28. **Testing.** (§7)
    - Core tests run on every run.
    - Page checks run in Windows CI inside the existing STA fact.
    - New CI measurements: the History read on a full per-minute file, and full Connections and Usage clear durations.
    - Checks by eye stay pending and are never claimed as verified.
29. **Docs.** Each release updates the user guide, CHANGELOG, its release notes and `docs/validation.md`. README gets at most a one-line mention per release. The 0.17.0 notes state the rollback effect. (§7)
30. **Release order** is 0.15.0, then 0.16.0, then 0.17.0, with one PR each from main. (§7)

**For this review.** These were settled while the sections were joined, after the user approved them, so the user has not yet seen them:
- **Cleared time on the strips.** The cleared marks (`cleared-all`, `cleared-one` and `usage-history.cleared.json`, times only), the Cleared band and the Not saved slot state were added after §6 was approved, to meet §3's requirement that the clear section say how cleared time is drawn (*Don't record these apps and Clear history…*).
- **An app added to Don't record mid-minute.** Connections keeps the entries merged before the change, as §6 states for every store. This replaces §2's note that the writer removes them from open units at its next step (*Connections storage*, Writer hooks).
- **Read-error counts.** A failed Connections History read now shows "—" for the summary counts, not today's zeros (*Errors, testing, docs and release*), applying §7's rule.
- **Open question: History speed.** Decision 15's 2–2.5 s came from a benchmark with one simple destinations grouping. *The Destinations table*'s steps as drafted measure about 0.45 s more on Linux, so a 30-day view on a full per-minute file takes about 2.8–3.0 s (*Connections storage*, Timings). The user chooses between accepting that for 0.16.0 and trimming those steps (for example, steps 4–6 reading one shared pass over `temp.grouped`, which measured about 0.25 s faster on Linux) to stay near 2.5 s.

## Architecture

**Approach 1, "Query what's stored".**
- **Reads.** Every new view reads what the stores already hold; nothing is precomputed at write time.
  - The one write-side change is that the connection writer merges each minute in memory and saves it as ordinary rows.
  - Connections History groups those rows once per read into TEMP tables. The page, summary, markers, strip and Destinations table all read them in one snapshot.
- **New state** lives in `alerts.db` tables and `settings.json`, plus, from 0.17.0, rows in `connections.db`'s existing `metadata` table (the clear marker and the cleared marks) and sidecar files beside `usage-history.db` and `process-history.db` (the clear markers and `usage-history.cleared.json`).
- **Don't record** filters sit where each worker folds data, after the live view is built.
- **Clears** are queued commands on each store's own worker. Each saves pending data first and then deletes in bounded steps behind a durable marker.
- **The other two approaches.**
  - **Rollups at write time (2)** would need version bumps of `connections.db` and `alerts.db`. Returning to 0.15 would then leave Connections history and the alert log unavailable, and every delete path would have to keep the rollups in step.
  - **An app registry with clear tombstones (3)** would add a contract across all four stores, and older builds would show cleared data again.
  - One idea was taken from 3: the durable "clear in progress" marker.

**What changes in each store:**
- **`connections.db`** stays at `user_version` 1 with the same schema; 0.15 and 0.16 open it alike.
  - **0.16.0, writing.** The writer (`ConnectionMinuteFolder`) saves one row per app and destination per minute. These are ordinary records through the existing write path, caps and budgets. Older per-poll rows stay until they expire.
  - **0.16.0, reading.** `Read` builds `temp.geo` and `temp.grouped` once per call, with `temp_store=MEMORY` and a 64 MiB TEMP limit. Over that limit it throws `ConnectionHistoryLimitException` and returns no partial view.
  - **0.16.0, new pieces.** Every connection registers `app_key` beside `sat_sum`. `ConnectionHistoryQuery` gains the exact `Application` field.
  - **0.17.0, the marker.** A `metadata` row marks a clear in progress: `clear-app:<KEY>` or `clear-all`, holding the boundary in ticks. 0.15 and 0.16 ignore it.
  - **0.17.0, the cleared marks.** `metadata` rows `cleared-all` and `cleared-one` hold the latest `Before` of each kind of clear, times only. They stay after the clear so the strip can label cleared time, and 0.15 and 0.16 ignore them.
  - **0.17.0, how a clear hides rows.** An all-apps clear raises the existing `expiry`. A one-app clear adds a hide predicate to `GroupedWhere`.
  - **0.17.0, the write path.** Routine purges are bounded per `Append`, `secure_delete` is on while a marker exists, and excluded apps are skipped before the merge.
- **`usage-history.db`** keeps its format.
  - **0.16.0** adds `ReadApplicationSeries` for the strip.
  - **0.17.0** adds:
    - `ReadApplicationNames` for the first-use seed;
    - the exclusion filter in `UsageMinuteFolder.FoldApplications`, which still moves the app's baseline;
    - a sidecar `usage-history.clear.json` marker while a clear runs;
    - `usage-history.cleared.json`, the times of the latest clears (times only), kept after the clear for the strip.
- **`process-history.db`** stays at 0.15.0's version 2, and 0.16.0 does not touch it. **0.17.0:**
  - **Excluded apps** fold into Everything else in `ProcessMinuteFolder.Fold`, GPU by 0.15.0's App rows rule.
  - **A one-app clear** moves the app's numbers into each minute's Everything else row.
  - **An all-apps clear** deletes whole minutes in 6-hour windows.
  - **The marker** is a sidecar file, `process-history.clear.json`.
- **`alerts.db`** stays at `user_version` 1, and every build since 0.8.0 still opens it. **0.17.0** adds four tables with `IF NOT EXISTS`, with one evict-and-retry on SQLITE_FULL at the 64 MiB cap:
  - `known_apps`: app key, first and last minute, per machine;
  - `episode_apps`: links an episode to its app by name;
  - `alert_metadata`: the seed markers and the learning start and end;
  - `pending_clear`: the alert log's clear marker.

  First-use episodes are kind 4 and are always saved closed.
- **`settings.json`** (`UserSettings`). Older builds drop all of these on their first save:
  - **0.16.0:** `ConnectionsDestinationScope` ("All", "Internet" or "Local");
  - **0.17.0:**
    - `ExcludedApps` (Don't record these apps, at most 64);
    - `FirstUseIgnoredApps` (Don't alert, at most 256);
    - `FirstUseRuleAdded`;
    - the first-network-use rule (`AlertKind` value 4) in `Alerts`.
- **In memory only:**
  - the connection writer's open-minute units;
  - the Show in Connections request;
  - the first-use detector's per-minute set of posted apps and its 60-second balloon group.

**One app key.**
- **0.16.0:** the strip and the Destinations table key apps with `ProcessGrouping.Key`, registered on `connections.db` connections as `app_key`.
- **0.17.0:**
  - It adds `AppKey.Of`, which also replaces unpaired surrogates, so it equals `process-history.db`'s `names.upper`.
  - It registers that function as `app_key` on `connections.db` and `alerts.db` connections. The strip's and the Destinations table's keys switch to it.
  - For every name of at most 256 characters without unpaired surrogates, the two functions agree, and executable names are at most 255 characters.
  - New matching never uses SQLite's ASCII-only `upper()` or `COLLATE NOCASE`, except the budget re-seed when an app leaves the Don't record list, which reuses `ReadApplicationMinutes` (`COLLATE NOCASE`, `UsageHistoryStore.cs:226`) as the startup seed does today.

## Release split

| | 0.16.0 — see it | 0.17.0 — watch and control |
|---|---|---|
| Storage | Per-minute merge, grouped TEMP read, Unlocated fix, `app_key` | Writer hooks: filter before the merge, `Forget`, the flush result, a final write that purges nothing (`maintenanceTransactions: 0`) |
| Pages | Traffic strip (Connections History and Usage history), Show in Connections, exact-application filter, whole-minute `IsFull` on the Usage line charts, Destinations table with scope, highlight, copy and **Export destinations…** | " · not recorded" marks on live pages, including the Destinations Apps cell; Settings → **Don't record these apps**; **Clear history…** from Settings, Connections and Usage history |
| Alerts | — | First network use rule (kind 4), learning, seed, grouped balloon, Don't alert list, **View in Connections** |
| Stores | No format change; one new setting | No format change; four `alerts.db` tables, clear markers, cleared-time marks (`connections.db` `metadata` rows and `usage-history.cleared.json`), three new settings and the added rule |

- **Order:** 0.15.0 (per-app GPU), then 0.16.0, then 0.17.0. Each is one PR from `main`, opened after the previous release has merged, on a feature branch, with CI as the gate (`docs/development.md`).
- **What 0.16.0 already provides for 0.17.0:**
  - `ConnectionsPage.ReloadHistory()`;
  - `UiSession.RequestConnectionsHistory` and `ConnectionsPage.ShowHistory`, which View in Connections uses;
  - `GroupedWhere`, which carries the clear's hide predicate;
  - the strip's rules for an interval Connections no longer keeps.
- **What 0.16.0 ships without:** the writer hooks, the not-recorded mark and `AppKey.Of`, which arrive in 0.17.0.

## Section map

1. **0.16.0 — Connections storage: per-minute merge, the grouped read and the Unlocated fix.** How the writer merges and saves each minute, the expected retention, the TEMP-table History read and its limit, the Unlocated rule, and the writer hooks 0.17.0 adds.
2. **0.16.0 — The traffic strip on Connections History and Usage history.** What the strip plots and its slot states, the whole-minute `IsFull` fix, the exact-application filter, the shared cursors, Show in Connections, and drawing.
3. **0.16.0 — The Destinations table.** Rows per remote address, the address class, scope, caps, in-place updates, the map highlight, copy and export, page layout, and its History store steps.
4. **0.17.0 — First network use alerts.** The rule, who counts, detection on the collector thread, `FirstUseTracker`, seeding, the `alerts.db` tables, the balloon, the Alerts page, and interactions with clears and exclusions.
5. **0.17.0 — Don't record these apps and Clear history….** The one app key, the settings list and where recording stops, the not-recorded marks, the Clear history dialog, and how each of the four stores clears and resumes.

Then **Errors, testing, docs and release (both releases)**, which holds the cross-cutting rules, the test counts, docs per release, rollback, the consolidated out-of-scope list and the checks pending by eye.


## 0.16.0 — Connections storage: per-minute merge, the grouped read and the Unlocated fix

Anchors are at `origin/main` 181eb54 (the 0.14.1 merge, PR #82). The anchored code and test files are byte-identical at 5dde079 and on `claude/cleanup-0.14.1`:
- `ConnectionService.cs`, `ConnectionHistoryStore.cs`, `ConnectionLedger.cs` and `ConnectionModels.cs`;
- `UiSession.cs` and `ConnectionsPage.cs`;
- `ConnectionServiceTests.cs` and `ConnectionHistoryStoreTests.cs`.

0.15.0 merges first. Its spec and plan change none of these files. They do change `src/DeadWare.Core/ProcessHistory.cs` (`ProcessMinuteFolder.Fold` and `Close`), `docs/user-guide.md` and `docs/validation.md` (a new top section). Doc anchors below therefore quote their text as well as the line. Recheck every anchor after 0.15.0 merges.

This section changes only how `connections.db` is written and read.
- `connections.db` stays at `user_version` 1 with the same schema (`src/DeadWare.Core/ConnectionHistoryStore.cs:36-57`). Nothing is added to the file. 0.15 therefore reads it, and `Initialize`'s schema equality check (`ConnectionHistoryStore.cs:76-79`) passes in both builds.
- `usage-history.db` and `process-history.db` are untouched by this section. `process-history.db` stays in 0.15.0's format: `user_version` 2 with the GPU columns, up to 40 apps plus Everything else a minute, and a 192 MiB cap. A rollback from 0.16 to 0.15 therefore finds both files in 0.15's format.

### What changes

- **New:** `src/DeadWare.Core/ConnectionMinuteFolder.cs` holds `ConnectionMinuteFolder` and `ConnectionMinuteUnit`.
  - It is a pure policy object like `ProcessMinuteFolder` (`src/DeadWare.Core/ProcessHistory.cs:29`; 0.15.0 changes its `Fold` and `Close`, not its shape) and `UsageMinuteFolder` (`src/DeadWare.Core/UsageHistory.cs:42`): no timers, threads or storage.
  - It reads the location cache only through a lookup delegate that the service passes in.
- **`ConnectionService.WriteLoopAsync`** (`src/DeadWare.App/ConnectionService.cs:231-322`) merges each publication into the folder instead of appending per-poll records, and writes closed minutes.
- **`ConnectionHistoryStore.Read`** (`ConnectionHistoryStore.cs:334-386`) builds the grouped rows once into TEMP tables. The summary uses the Live Unlocated rule.
- **Unchanged:**
  - the store's write path, `ConnectionHistoryStore.cs:131-332`: `Append`, `WriteChunk`, `Cleanup`, `EvictOldestBucket`, the expiry watermark, the 256 MiB cap, the 30-day retention, the 8 MiB journal bound (`:13`) and the transaction budgets;
  - `ExportCsv` (`:437-454`), including its temp store;
  - the live view, the service's status strings (`StorageStatus`, `ConnectionService.cs:101`) and the settings.
- A per-minute record is an ordinary `ConnectionHistoryRecord`. It passes `Prepare` (`:97-113`) as it is, because its FirstSeen and LastSeen stay inside its minute.

### What a row is

- "App" and "destination" keep today's History row identity:
  - the app is the process instance: PID, start generation and name;
  - the destination is the remote address, port and protocol, under the origin in force.
- The local address and port are never part of it (`src/DeadWare.Core/ConnectionModels.cs:33`).
- History rows, summaries and CSV therefore read exactly as today. Only the number of stored rows changes.
- Two processes of one browser to one address, or one process to two ports, stay separate rows, as today.

### The open minute

**Unit.** The writer holds open units keyed by (Segment, recording generation, Minute):
- Segment is the ledger's segment (`src/DeadWare.Core/ConnectionLedger.cs:27,140`). It is a new GUID after every break.
- Recording generation is `Pending.Generation`: the service's `generation` at admission, which `Configure` bumps on every recording toggle (`ConnectionService.cs:109`).
- Minute is the delta's UTC minute, or the start minute of the coverage piece.
- A unit is created only when a delta or a coverage piece is merged into it. No unit is ever empty.

**Entry.** Inside a unit, one entry is kept per (Pid, process Generation, Name, RemoteAddress, RemotePort, Protocol, Origin):
- The ledger already normalizes RemoteAddress (`ConnectionLedger.cs:64`). The folder compares the strings ordinally.
- This is the store's item identity (`ConnectionHistoryStore.cs:110`) without:
  - Segment and Minute, which the unit fixes;
  - Location, which is fixed at freeze.
- Two local ports to one remote merge, exactly as `Append`'s (BatchId, Item) grouping merges them today (`:138-143`).
- Only deltas with the same stored Name merge, and the merged entry keeps the AND of their completeness, which reads as `min(complete)`. Defined here for the traffic strip.

**Entry values:**
- `ReceivedBytes` and `SentBytes`: saturating sums, capped at `long.MaxValue` like the store's `Add` (`:128`).
- `FirstSeen`: the minimum. `LastSeen`: the maximum.
- `Complete`: the AND of the `complete` flags of the publications that contributed (`ConnectionService.cs:281,287`).
- `LastLocation`: the value the location cache held for the address at the latest merge in which the cache held it. A cached null counts.
- `Observations`: the number of accepted deltas merged in.
- `Peer`: from the first contribution. Its local endpoint is never stored.

**Coverage.** Coverage pieces are computed exactly as today (`ConnectionService.cs:291-300`): `CoverageFrom < Timestamp`, at most 60 s, split at UTC minutes, with the publication's `complete` flag.
- Each piece goes to its minute's unit as a run.
- A piece extends the unit's last run only when `last.To == piece.From` and both have the same Complete flag. Otherwise it starts a new run.
- A run stays inside one minute, so it never exceeds 60 s and passes the store's interval check (`:145`).
- Pieces never overlap, because each starts at the previous Take of its segment and a new segment starts with a null `CoverageFrom`. Each minute's stored coverage therefore stays inside that minute, and its Σ(finish − start) equals the time covered. Defined here for the traffic strip.
- `ReadCoverage` output is unchanged, because it already merges equal adjacent intervals (`:428`).

**What is merged.** Only deltas that pass today's three checks (`ConnectionService.cs:286-287`): the pause boundary, the 60 s window and the +2 s future guard.
- This replaces today's only `records.Add` (`:290`). The live view is built earlier, at `:139`.
- 0.17.0's filter goes immediately before this merge (Writer hooks for 0.17.0).

`Held` is the number of open entries plus open coverage runs.

**Freezing.** Freezing a unit produces an immutable `ConnectionMinuteUnit(BatchId, Segment, Generation, Minute, Records, Coverage, Observations)`.

`BatchId` is `{Segment}:m{Minute.UtcTicks}:{generation}:{serial}`:
- `serial` is the folder's own counter. It starts at 0 and increases by 1 for every frozen unit.
- There is one `ConnectionService` per process (`UiSession.cs:78`), and segments are new GUIDs, so IDs never repeat across runs.
- It never collides with today's ID `{segment}:{sequence}:{generation}` (`ConnectionService.cs:280`), whose second part has no `m`.
- It is at most 128 + 61 = 189 characters: the segment bound plus `:m`, three 19-digit numbers and two colons. The store allows 192 (`ConnectionHistoryStore.cs:99`).

`Records`: one per entry.
- Location is the cache value when `locations` holds the address at freeze, including a cached null. Otherwise it is `LastLocation`, else null.
- This is today's rule ("freeze geography at the first persistence attempt", `ConnectionService.cs:255-262`), applied at freeze.
- Record order does not affect the manifest, which sorts by item (`ConnectionHistoryStore.cs:156`).

`Coverage`: one `ConnectionCoverage` per run, under the same BatchId.

The service calls folder methods under its `gate`, as today's `records.Add` does (`:290`), because the lookup worker changes `locations` under that lock.

### When a minute closes

- **R1, closed by capture.** After merging a publication with a non-null `CoverageFrom`, freeze each open unit of that publication's Segment and generation whose `Minute + 62 s ≤ CoverageFrom`.
  - `CoverageFrom` is the previous Take (`ConnectionLedger.cs:112`). Every event stamped in the minute that reached the ledger before this publication's Take is therefore merged, and that Take is later than 2 s after the minute ended. An event that reaches the ledger later is a late delta (below).
  - The close comes 2 s plus one to two polling intervals after the minute ends (`src/DeadWare.Core/PollingPolicy.cs:9`): 2.5–3 s at 0.5 s polling, 3–4 s at 1 s and 12–22 s at 10 s.
  - The grace is a 2 s constant, `CloseGrace`, and is not configurable. A null `CoverageFrom` closes nothing.
- **R2, segment or generation ended.** After merging a publication, freeze every open unit whose Segment or generation differs from that publication's.
  - The ledger never adds to a segment after a break (`ConnectionLedger.cs:133-143`). The queue is FIFO and generations only grow.
  - R2 covers three cases:
    - ETW loss, which breaks the segment mid-minute (`src/DeadWare.App/EtwNetwork.cs:90`);
    - sleep or a clock discontinuity (`ConnectionLedger.cs:89-91`);
    - per-app capture unusable (not administrator, capture off or failed). Then every poll breaks the ledger and publishes a new segment with no deltas and no coverage (`EtwNetwork.cs:99-108`). Those publications close the open minutes and create no unit.
  - A PC that loses ETW events every few seconds therefore saves about one row per pair per break, not per minute. History stays exact.
- **R3, recording paused.** At every tick, freeze every open unit whose generation is not the service's current one.
  - The tick is `FlushInterval`, 15 s by default (`ConnectionService.cs:304`).
  - A pause is therefore saved within about 15 s. `Configure` itself does no storage work on the UI thread.
- **R4, capture stalled.** At every tick, freeze every open unit with `Minute + 90 s ≤ clock()`.
  - This covers a collector that stops publishing.
  - At 10 s polling, R1 normally fires first, at M+72 to M+82 s. If the writer lags more than about 8 s, R4 can close the minute first, and its last publication then opens a late unit. Totals stay exact.
- **R5, pressure.** After a merge, if `Held ≥ max(1, MaximumBufferedDeltas / 2)` (16,384 by default), freeze all open units.
  - Waiting units do not count. During a storage failure, freezing more cannot help.
  - This replaces both of today's count triggers (`ConnectionService.cs:302`).
- **R6, stop.** When `StopAsync` (`:323-333`) has completed the queue and the writer has drained it, freeze all open units (`FreezeAll`), then make the final write (`:321`).

**A late delta.** A delta for a minute that has no open unit opens a new unit, with a new serial and therefore a new BatchId.
- If R1 closed the earlier unit, R1 on the same publication freezes the new one at once.
- After R3, R4 or R5 the new unit stays open until a rule closes it.
- Totals stay exact. The minute simply has one extra stored row, which reads merge (`ConnectionHistoryStore.cs:33`).

### Writing closed minutes

- **Write attempts** happen at the end of every writer step that froze a unit, and at every tick. The tick is the existing maintenance call (`Flush(maintenance:true)`, `:309`), which still calls `Append` with an empty list so that expiry and Cleanup run.
- **When a minute is in the file.** While capture publishes and storage succeeds, a closed minute is in the file within 2 s plus two polling intervals after it ends, plus the `Append` itself, which waits for a running `Read` (up to about 2.5 s on a full file; see The grouped read). That is at most about 24.5 s at 10 s polling, so at every polling interval it falls inside the strip's save delay (the 15 s flush interval plus one polling interval). A minute is saved later only when R4 closes it (up to M + 90 s plus one tick), when the clock check holds it (up to 60 s), or when storage fails (until a retry succeeds). Defined here for the traffic strip.
- **Payload.** Units wait in (Minute, serial) order. A write takes whole sendable units in that order while the payload stays within `Append`'s limits of 32,768 records and 32,768 coverage (`ConnectionHistoryStore.cs:136`). The rest wait for the next attempt.
  - One unit always fits. Entries grow only from admitted deltas, so all entries together never exceed `MaximumBufferedDeltas`. One unit's runs are bounded by the publications of one minute.
- **Clock check** (replaces `ConnectionService.cs:240-254`). For each waiting unit, `excess = (latest record LastSeen or coverage To) − (now + 2 s)`:
  - `excess ≤ 0`: the unit is sent.
  - `0 < excess ≤ 60 s`: the unit is held back unchanged and checked again at later attempts. The wall clock passes it within a minute.
  - `excess > 60 s`, or the final write: the unit is retired whole, with `Dropped += max(1, Observations)`.
  - The 60 s hold is a constant and is not configurable.
  - A unit is never trimmed, never resent as a subset and never resent with a changed payload.
  - A backward clock step of up to about a minute (Windows steps standalone PCs for offsets over 1 s) therefore loses nothing.
- **Retry after a non-final failure** (`:271`): every unsent unit stays unchanged and is resent at the next attempt. New traffic goes into new units with new BatchIds.
  - `ValidateManifests` (`ConnectionHistoryStore.cs:186-193`) and the per-item dedup receipts (`:213,224`) make a retry exact. That includes a retry after a later chunk failed, or after a commit whose acknowledgement failed.
  - No BatchId is ever sent with a changed payload ("A connection history BatchId was reused with a changed payload", `:191`).
- **Final failure** (`:272`): `Dropped += max(1, Σ Observations)` over the unsent units.
- The `attempted` set (`:235,255-262`) is deleted, because geography is fixed at freeze.
- **Accounting.** `buffered` is the queued deltas plus `Held` plus the sizes of waiting units (records plus coverage).
  - A merge adds the change in `Held`, subtracts `sample.Deltas.Count` and decrements `bufferedPublications`.
  - A successful write subtracts the sizes of the sent units. A retirement subtracts the size of its unit.
  - Admission (`:144`) is unchanged. A merge can add up to two runs without deltas, so `buffered` can exceed `MaximumBufferedDeltas` by at most 2. Admission then drops every publication until a write frees space.
  - The `QueueCapacity * 2` publication bound no longer binds, because the channel's capacity binds first.
  - `BufferedDeltas` is 0 after `StopAsync`.

### Crash, storage failure, pause, settings, Quit and sign-out

- **Crash or power loss** loses:
  - the open minute;
  - the previous minute until R1 writes it;
  - any units still unsent after a failed write or held by the clock check.
  - That is up to about 64 s at 1 s polling and about 82 s at 10 s polling. Today it is up to about 15 s.
  - The restarted run has a new segment, so it never merges into the lost minute, and the lost time reads as a coverage gap.
- **Unreadable or unsupported file.**
  - `Initialize` fails and the status is unchanged (`ConnectionService.cs:233-234`).
  - Every write fails cheaply, and closed units wait until admission fills `buffered` and drops new publications. Live is unaffected.
  - On stop the units are dropped and counted, as today.
- **Pause:** saved at the next tick by R3, or by R2 if recording resumes first.
- **Resume:** a new generation, so a new unit and BatchId. The first publication is incomplete, as today (`:143`).
- **Home city change** (`src/DeadWare.App/UiSession.cs:244-248`): Origin is in the entry key, so that minute gets a second row per pair, exactly as today's grouping does. Nothing is written early.
- **Location database update or reload:** it clears the cache (`ConnectionService.cs:183`). At freeze a missing address falls back to `LastLocation`.
- **Polling interval change:** nothing changes, because R1 adapts through `CoverageFrom`.
- **Restart as administrator:** goes through Quit (`UiSession.cs:426-433`).
- **Quit:** `StopCollectorAsync` (`UiSession.cs:302-326`; 5 s for the final drain, 20 s for history), then R6.
  - The final write carries the open units: the current minute, plus the previous minute in the first seconds of a minute.
  - It also carries any units still waiting, in as many `Append` calls as the limits need. Units still held by the clock check are retired.
- **Sign-out:** `StopCollectorForSessionEnding` (`UiSession.cs:339-351`), then the same R6 write.
  - The whole stop has 3 s. The connections stop starts after network capture stops, or after 1.5 s at the latest.
  - If the 3 s pass first, the open minute is lost, up to about 65 s; today it is up to about 15 s.
  - `Read` holds the store gate for its whole run (`ConnectionHistoryStore.cs:336`), about 2.5 s on a full per-minute file. A History read in progress at sign-out can therefore make the final write miss the deadline.

### Live and History

- **Live is unaffected.**
  - `live` is built at `ConnectionService.cs:139`, before the recording gate (`:141`).
  - The folder is private to the writer task.
  - `Live`, `StorageStatus` (`:101`) and every status string are unchanged.
- **History and CSV export** include saved minutes only. The newest minute appears once R1 writes it.
- **A saved minute reads the same as before.** History rows, summaries and CSV keep today's per-minute grouping.
  - One difference: a peer whose location was resolved during the minute is stored with the location known at freeze.
  - Today it could be two rows when a 15 s flush came before the lookup.

### Expected retention

A harness drove the real `ConnectionHistoryStore.Append` (same schema, pragmas, chunking and checkpoints) with the folder's output simulated. It used 1 s polling, real-format DB-IP locations and an origin, for 12–24 simulated hours per load:

| Load | 0.15 (one row per poll) | 0.16 (one row per minute) | 0.16 retention |
|---|---|---|---|
| Idle (coverage only) | 2.53 MiB/h; cap in 4.2 days | 0.052 MiB/h; 37 MiB in 30 days | 30 days |
| 1 steady app–destination pair | 6.15 MiB/h; cap in 41.6 h | 0.120 MiB/h; 86 MiB in 30 days | 30 days |
| 20 steady pairs | 62.8 MiB/h; cap in 4.1 h | 1.175 MiB/h; cap in 218 h | about 9 days |
| 20 pairs, 5 % replaced each second | 63.1 MiB/h; cap in 4.1 h | 1.336 MiB/h; cap in 192 h | about 8 days |

- **Rows per hour at 20 pairs:**
  - traffic: 72,000 under 0.15, 1,220 measured under 0.16;
  - coverage: 3,600 under 0.15, 61 under 0.16;
  - manifests: 7,200 under 0.15, 122 under 0.16.
  - The harness started each simulated hour 3 s past a minute, which adds one partial minute per hour. Steady running gives 1,200 traffic rows and 60 coverage rows, about 60 times fewer than 0.15.
- **Rule of thumb:** about 0.87 KiB per recorded minute, plus about 0.94 KiB per saved row (one per pair per minute). 30 days fit while the average stays at or below about 5 pairs a minute.
- "Pairs" are process instances times endpoints. Browsers with many processes and addresses, and PCs with frequent ETW loss (R2), save more rows than the benchmark.
- **Polling:** 0.15's rows scale with polls: 0.5 s doubles them and 10 s gives about a tenth. 0.16's rows do not depend on the polling interval.
- **Cap and eviction** work as before: the oldest whole minutes go first (`ConnectionHistoryStore.cs:266-284`). The page's earliest retained time and "Capacity shortened retention." (`:381`) stay.

**Upgrade.** There is no migration.
- Per-poll rows stay until they are 30 days old or evicted, and reads group both kinds.
- A file that is full under 0.15 gives up its oldest per-poll minutes one at a time as per-minute rows arrive.
- The file never shrinks (no VACUUM); freed pages are reused.

**Rollback to 0.15.** 0.15 reads the 0.16 rows unchanged and writes per-poll rows again. Both builds accept the file.

### The grouped read

**What it replaces.** `Read` stops running `Grouped` (`ConnectionHistoryStore.cs:24-35`) three times: for the page (`:342`), the summary (`:345`) and the markers (`:351-369`). Instead, each call does the following on its existing read-only connection, with `Arguments(query)` (`:340`) bound to every statement.

`Grouped`'s WHERE clause (`:29-32`) moves into its own constant, `GroupedWhere`. `Grouped`, the geo insert (step 2) and the grouped insert (step 3) all use that one constant. A predicate added to it therefore reaches every read path, `ExportCsv` included, with no change here: the traffic strip's exact-app field in 0.16.0, and Clear history's one-app hide predicate in 0.17.0. Defined here for the traffic strip and for Don't record these apps and Clear history….

1. **Pragmas.** Right after `Open` and before `BeginTransaction(deferred:true)` (`:339`), run `PRAGMA temp_store=MEMORY` and then `PRAGMA temp.max_page_count=<maximumGroupedTempPages>`.
   - temp_store goes first, because changing it discards the temp database.
   - The default `MaximumGroupedTempPages` is 16,384 pages, which is 64 MiB at the temp database's 4,096-byte pages.
   - The Linux build of the bundled SQLite (3.53.4 in `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5) reports `TEMP_STORE=1`, which is also SQLite's default. The win-x64 binary's build setting is not known. The explicit pragma makes the build setting irrelevant.
   - With that setting, SQLite writes TEMP pages and large sorts to files in Windows `%TEMP%`, and today's `Read` already spills its sorts there.
   - MEMORY keeps grouped history out of `%TEMP%`. `max_page_count` bounds the TEMP tables: exceeding it fails with SQLITE_FULL, code 13 (checked on the bundled SQLite).
   - The limit does not bound SQLite's sorter (see Timings).
   - `ExportCsv` and the write connections (`:483`) are unchanged.
2. **The geo dictionary.**
   - `CREATE TEMP TABLE geo(id INTEGER PRIMARY KEY, location TEXT NOT NULL, origin TEXT NOT NULL, located INTEGER NOT NULL, UNIQUE(location, origin))`
   - `INSERT INTO temp.geo(id, location, origin, located) SELECT row_number() OVER (ORDER BY location, origin), location, origin, location<>'null' AND json_extract(location,'$.Point') IS NOT NULL FROM traffic WHERE <GroupedWhere: range, expiry, floor, search, country, and any predicate added later> GROUP BY location, origin`
   - Ids are assigned explicitly in BINARY (location, origin) order.
   - `located` is 1 exactly when the location has a point.
3. **The grouped table.**
   - `CREATE TEMP TABLE grouped(row_index INTEGER PRIMARY KEY, minute INTEGER NOT NULL, pid INTEGER NOT NULL, generation INTEGER NOT NULL, name TEXT NOT NULL, remote TEXT NOT NULL, port INTEGER NOT NULL, protocol INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, received INTEGER NOT NULL, sent INTEGER NOT NULL, geo INTEGER NOT NULL, complete INTEGER NOT NULL, country TEXT NOT NULL, kind INTEGER NOT NULL)`
   - The insert is `Grouped` joined to `temp.geo` on (location, origin):
     - `g.id` replaces location and origin in the select and in GROUP BY;
     - the HAVING for direction stays the same;
     - `row_index` is `row_number() OVER (ORDER BY minute DESC,pid,generation,name,remote,port,protocol,geo)-1`.
   - That order equals `RowOrder` (`:23`), because geo ids follow (location, origin) order.
   - `RowOrder` is a total order over grouped rows, because country follows from location (`:112`) and kind follows from remote.
   - `kind` is the stored class: 0 Public, 1 Local, 2 Special.
   - `temp.geo` and `temp.grouped`, with these columns, ids, order and meanings, are what later readers inside `Read` use. Defined here for the Destinations table and the traffic strip.
4. **Page.**
   - The statement is `SELECT <Columns' order, with x.location and x.origin> FROM temp.grouped g JOIN temp.geo x ON x.id=g.geo WHERE row_index >= $offset ORDER BY row_index LIMIT $limit`.
   - Rows are read with `ReadRow` (`:389-394`).
5. **Summary.** One statement over `temp.grouped` joined to `temp.geo` computes:
   - `count(*)`, `sat_sum(received)` and `sat_sum(sent)`;
   - `count(DISTINCT nullif(country,''))` and `count(DISTINCT remote)`;
   - `count(DISTINCT CASE WHEN kind=0 AND x.located=0 THEN remote END)` for Unlocated (The Unlocated fix);
   - `count(DISTINCT CASE WHEN kind=1 THEN remote END)` for Local.
6. **Markers.** This is today's rule (`:351-369`) applied to the TEMP table:
   - Group `temp.grouped` by geo where `located=1`, with `sat_sum` totals and `min(row_index)` as the representative.
   - Take the top 256 by `cast(total_received AS REAL)+cast(total_sent AS REAL)` DESC, then by geo.
   - Count every group with `count(*) OVER ()`.
   - Join the representative row by `row_index`.
7. **Unchanged statements:** coverage (`ReadCoverage`, `:401-435`), the earliest retained time (`:371-379`), and floor and status (`:380-383`). The traffic strip and the Destinations table read `temp.grouped` and `temp.geo` in the same snapshot, under the same gate, before the connection closes.
8. **Cleanup.** Disposing the connection drops the TEMP tables (connections are not pooled, `:471`).
   - `Read` writes no page to `connections.db` or `-wal`, and nothing to `%TEMP%`.
   - Like today's `Read`, it may update SQLite's own read marks in `-shm`.
   - `Read` keeps nothing between calls, so the next `Read` always sees the file as it is then, including after a clear. Defined here for Don't record these apps and Clear history….
   - `Grouped` stays for `ExportCsv` (`:445`), which spells `RowOrder`'s order inline. Nothing else reads `RowOrder` after this change, so it is removed; step 3 states its order in full.

**Limit.** Defined here for the Destinations table.
- SQLITE_FULL (13) from any statement in steps 2–6, or from any statement a later section runs over TEMP tables on this connection, throws `ConnectionHistoryLimitException : IOException`, with the message "Connection history interval holds too many saved rows to group." No partial view is returned.
- `ConnectionsPage` (`src/DeadWare.App/ConnectionsPage.cs:226`) catches that type before its general catch. It shows the fixed copy "This interval holds too many saved rows to show. Choose a shorter interval." in its action status and applies the same view as other read errors, `Empty(query, "History unavailable; no saved observations shown.")` (`:150`, `:226`), and stops playback. The interval therefore reads as unavailable, never as an empty period.
- A full 256 MiB per-minute file measured 23.8 MiB of TEMP pages. By estimate, only names near the 260-character limit can reach 64 MiB.
- The store's public constructor gains `int maximumGroupedTempPages = MaximumGroupedTempPages`. It accepts 16–16,384 and throws `ArgumentOutOfRangeException` otherwise, as `maximumDatabaseBytes` does (`:59-63`).
- `CommandTimeout=1` (`:503`) bounds only busy waits, not execution, so a 1.5 s build runs under it.

**Equivalence.** On both benchmark files over 7 days, the TEMP read returned exactly 0.15's results:
- pages at offsets 0, 1,000 and 5,000;
- the summary, with the Unlocated rule applied to both;
- the 7 markers.

**Timings.** Measured twice in a Linux container (.NET 10, SQLite 3.53.4); ranges cover both runs. Windows times will differ, but the ratios should hold. The Windows CI measurement (Tests) records them on Windows.

| File | Range | 0.15 `Read` | 0.16 build | 0.16 consumers¹ | 0.16 total | SQLite heap peak |
|---|---|---|---|---|---|---|
| Per-poll, full: 5 h, 293k rows, 5,679 grouped | 31 d | 1.71 s | 0.81–0.83 s | 0.02 s | 0.85 s | 52 MiB |
| Per-minute, full: 7.8 days, 262k rows, all grouped | 31 d | 4.34 s | 1.38–1.47 s | 0.94–1.01 s | 2.3–2.5 s | 49 MiB |
| Per-minute, full | 7 d | not measured | 1.09–1.13 s | 0.78–0.82 s | 1.9–2.0 s | 44 MiB |
| Per-minute, full | 1 d | not measured | 0.15 s | 0.10 s | 0.25 s | 8 MiB |
| Either | 1 minute (cursor) | 4–10 ms | < 1 ms | < 1 ms | < 5 ms | < 1 MiB |

¹ Summary, page, a per-app hourly strip, a destinations grouping and markers, as benchmarked. The traffic strip and the Destinations table own their exact queries.

- **Accepted for 0.16.0.** A 7- or 30-day view on a full per-minute file takes about 2–2.5 s as benchmarked (the Destinations steps add more, next item), the Today (1-day) preset about 0.25 s, and the minute cursor under 5 ms. 0.16.0 ships these timings with no cache. The page keeps its existing busy handling: "Reading saved history…" (`ConnectionsPage.cs:224`) while a read runs, and one pending request that later requests replace (`:214-229`).
- **The Destinations steps.** The consumers column benchmarked one simple destinations grouping. *The Destinations table*'s History store steps 1–6 as specified, run on the same 31 d file (SQLite 3.53.1, `upper()` standing in for `app_key`), took about 0.72–0.82 s against 0.26–0.39 s for that grouping in the same runs: about 0.45 s more, before the managed `app_key`'s own cost (about +75 ms per per-row call, *The traffic strip*). A 30-day view is then about 2.8–3.0 s and a 7-day view about 2.3–2.4 s.
  - The 30-day figure is over decision 15's 2–2.5 s. This is an open question for the user (front matter, For this review); the figures in this section that cite about 2.5 s for a full-file `Read` (When a minute is in the file, Sign-out) rise with it.
  - The Windows CI measurement records the steps' times (*The Destinations table*, Tests).
- **Default temp_store on the full per-minute file (31 d):**
  - build 1.24 s and consumers 0.81 s, 2.05 s in total;
  - an 8.3 MiB heap;
  - 23.8 MiB of TEMP pages, plus sort spill, written to `%TEMP%` files on each read.
  - MEMORY trades about 40 MiB of transient heap and about 0.4 s for keeping grouped history out of `%TEMP%`. 0.16.0 uses MEMORY: memory rises by about 50 MiB for the length of a full-file read.
- **A wide single-pass TEMP table** (location and origin text in every row) measures 0.50 s with a 75 MiB peak on the per-poll file.
  - On the full per-minute file its table is 66.1 MiB, over the 64 MiB limit, and the read takes about 2.6 s.
  - The compact layout (a geo dictionary plus integer rows) is therefore used.
- **The gate.** `Read` still holds the store `gate` for its whole run (`:336`), so a writer `Append` waits for it (see Sign-out).

### The Unlocated fix

- The summary's Unlocated term becomes `kind=0 AND located=0`. That is the old `:345` condition (`location='null' OR json_extract(location,'$.Point') IS NULL`) restricted to Public remotes.
- This is the Live rule (`ConnectionsPage.cs:239`): a Public remote with no point.
- Local remotes (`kind` 1) and Special remotes (`kind` 2, for example CGNAT, multicast and documentation ranges; `src/DeadWare.Core/NetworkAddress.cs:10-39`) are never unlocated.
  - Local stays `kind=1`.
  - Special remotes count only in the summary's destination count and, from the Destinations table, in its All filter. They count as neither Local nor Unlocated.
- The summary copy (`ConnectionsPage.cs:249`) is unchanged.
- This closes the Unlocated finding at `docs/validation.md:785` ("the history summary counting local peers as unlocated", in the 0.12.2 connections review). The other finding recorded there, the monotonic `expiry` watermark, stays open.
- On the per-minute benchmark file, the 7-day Unlocated count goes from 2,111 to 833 (measured).

### Errors, threads and limits

- **Unreadable or too large:** every read failure, the TEMP limit included, shows History as unavailable with no partial view, never as an empty period (Limit).
- **Status text** is fixed copy only. The only new text is the limit copy and the exception message above. Neither names an app, a peer or a path.
- **Threads:** all storage work stays off the UI thread. Merging, freezing and writing run on the writer task; `Read` runs through `ReadViewAsync`'s `Task.Run` (`ConnectionService.cs:35`); `Configure` does no storage work.
- **Limits:** the 256 MiB cap, 30-day retention, journal bound, transaction budgets, queue capacity (120) and `MaximumBufferedDeltas` (32,768) are unchanged. The new limit is the 64 MiB TEMP bound per read.
- **Interruptions:** a failed write is retried with identical units; a crash loses at most the time stated under Crash, which reads as a coverage gap. This section has no multi-step operation to resume.

### Writer hooks for 0.17.0

0.17.0 adds these to the folder and writer, with Don't record these apps and Clear history…, which uses them. 0.16.0 ships without them. Defined here for Don't record these apps and Clear history….

- **Filter before the merge.** The merge skips a delta that passes today's three checks but that section's Don't record set, or during a clear its boundary rule, excludes. Both tests and `AppKey.Of` are that section's. A skipped delta:
  - is not merged, so it adds nothing to `Held` or to any entry's `Observations`;
  - is not counted as dropped, and does not clear the publication's `complete` flag or its coverage;
  - still counts in the `sample.Deltas.Count` that the merge subtracts from `buffered`, so `Held` and `buffered` stay balanced.
  - The set is read at merge time only, never at a write. A list change never alters an entry already merged or a frozen unit: the part of the open minute merged before the change is saved when its minute closes, as that section states. The approved storage section had the writer remove the app's entries from open units at its next step instead; this follows the later privacy section, which keeps in every store what was folded before the change (front matter, For this review).
- **`Forget(string? appKey)`** on the folder removes from every open unit each entry whose `AppKey.Of(Name)` equals `appKey` (ordinal), or every entry when `appKey` is null.
  - Coverage runs stay, because coverage records capture, not apps.
  - A unit left with no entry and no run is removed.
  - `Held` and `buffered` fall by the number of entries removed.
  - Frozen units are untouched.
- **Write result.** `Flush` returns `ConnectionFlushResult(bool Saved, string? FailureType)`.
  - Saved is false exactly when an `Append` it called threw. FailureType is then that exception's type name, and the unsent units keep their normal retry.
  - Units held back by the clock check do not make it false.
- **Held units in a clear's scope.** After a Flush that saved, and before the clear's first delete, every unit still held by the clock check that holds a record in the clear's scope is retired whole, with `Dropped += max(1, Observations)`. A record is in scope when its minute is before the clear's `Before` and, for a one-app clear, `AppKey.Of(Name)` equals the clear's key. An all-apps clear matches every app, still only for minutes before `Before`. Such a unit is never trimmed: sent after the delete, it would add its rows back.
  - So a clear needs no `FreezeAll` before its write. After `Forget` for All time, no open unit holds an in-scope record (an all-apps clear leaves coverage runs only, which the raised `expiry` hides and the routine purge removes), and for Older than 7 days every open unit is newer than `Before`. So the write sends every in-scope frozen unit without freezing open ones.
- **Final write.** Every `Append` call of the R6 final write passes `maintenanceTransactions: 0`, so Quit and sign-out never wait for a purge. The writer's other `Append` calls pass the bound that section sets.

### Copy and docs

- **`docs/user-guide.md:145`** (the paragraph that starts "History normally retains 30 days"). Replace "Background writes target a 15-second flush interval and drain on normal **Quit DeadWare**. A crash or power loss can lose the unflushed batch; delayed or failed storage can extend that interval and produces incomplete/unavailable recording status." with:

  "Each minute is saved as one row per application and destination shortly after it ends: 2 seconds plus one to two polling intervals, which is 3–4 seconds at the default 1-second polling. The minute in progress is saved on normal **Quit DeadWare**, within 15 seconds of pausing recording, and at Windows sign-out or shutdown when its 3-second limit allows. History and CSV export show a minute once it is saved. A crash or power loss can lose about the last minute (up to about 65 seconds at the default polling and about 80 seconds at 10-second polling); delayed or failed storage can extend that and produces incomplete/unavailable recording status."
- **Same paragraph, after "High traffic can evict older whole minutes earlier":** add "(a steady 20 application–destination pairs a minute fill the cap in about 8–9 days; idle and light use keep the full 30 days)".
- **`docs/releases/v0.16.0.md`:**
  - Connections history stores one row per application and destination per minute, about 60 times fewer rows than 0.15 at 1-second polling. Idle and light use keep 30 days.
  - Rows from earlier versions stay until they expire, and the file does not shrink.
  - History and CSV export show a minute a few seconds after it ends.
  - A crash can lose about a minute, instead of about 15 seconds.
  - A small backward clock correction no longer loses connection history.
  - History's unlocated count no longer includes local or special peers, matching Live.
  - Returning to 0.15 keeps reading the file and records per poll again.
- **`CHANGELOG.md`**, 0.16.0 section (shared with the other 0.16.0 sections): the same points as the release notes.
- **README:** nothing from this section. The 0.16.0 release has at most a one-line mention there.
- **`docs/validation.md`**, 0.16.0 section:
  - the retention and timing runs above, and the CI measurement's numbers from the Windows runner, as measured, with no threshold;
  - the resolution of the Unlocated finding at `:785`, and that the expiry-watermark finding stays open;
  - pending until checked by eye on a physical machine: a 7-day History view after a week of per-minute recording shows "Reading saved history…" and then the view, and the newest minute appears a few seconds after it ends.
- **DPI, theme and keyboard:** this section adds no layout or controls. Its only UI change is fixed copy in the existing action-status line. The traffic strip and the Destinations table own layout, theme, DPI and keyboard.

### Out of scope

- Rewriting existing per-poll rows.
- VACUUM.
- New indexes or tables in `connections.db`.
- Caching TEMP tables between reads.
- Changing CSV output or `ExportCsv`'s temp store.
- Releasing the store gate during reads, or interrupting a read at sign-out.
- A configurable close grace or hold window.
- The monotonic expiry-watermark finding (`docs/validation.md:785`).
- Any change to `usage-history.db` or `process-history.db`.

### Tests

Core tests (`tests/DeadWare.Tests`) run on every run. App tests and the page checks run in Windows CI; the page checks run inside the existing STA fact `Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows` (`tests/DeadWare.App.Tests/BatteryUiTests.cs:38`).

**Core, new `ConnectionMinuteFolderTests`:**

- `One_row_per_pair_per_minute_with_saturating_totals`:
  - 60 one-second publications for one pair give one record with summed bytes, minimum `FirstSeen`, maximum `LastSeen` and `Complete` true.
  - A second local port to the same remote merges in.
  - Sums saturate at `long.MaxValue`.
- `Completeness_is_the_and_of_contributing_publications`: one incomplete publication makes the record and its coverage run incomplete. Complete runs before and after it stay separate runs.
- `Minute_closes_two_seconds_and_one_poll_after_it_ends`:
  - At 1 s polling the unit is open after `CoverageFrom` = M + 61.9 s and frozen after M + 62.0 s.
  - At 10 s polling it freezes on the first publication with `CoverageFrom` ≥ M + 62 s, with that publication's late delta included.
  - A null `CoverageFrom` closes nothing.
- `Late_delta_becomes_a_new_unit_with_a_new_batch_id`:
  - A delta after an R1 freeze gives a second unit for the same minute, frozen on the same publication, with a different BatchId.
  - After an R4 freeze, the second unit stays open until R1.
  - Both units append to a real store, and `Read` returns the sum.
- `Segment_or_generation_change_closes_old_units`: R2. A run of capture-unusable publications (a new segment each, no deltas or coverage) closes the open units and creates no unit.
- `Tick_closes_old_generation_and_stalled_minutes`: R3; R4 at M + 90 s, not at M + 89 s.
- `Pressure_counts_open_entries_and_runs_only`: with frozen units waiting, R5 fires only when `Held` reaches the threshold.
- `Sendable_units_stay_within_append_limits`: with limits of 3 records and 3 coverage, units are taken whole in (Minute, serial) order, the rest wait, and no unit splits.
- `Origin_change_keeps_two_entries_in_one_minute`.
- `Location_is_the_cache_value_at_freeze_else_the_last_known`, in three cases:
  - a cache hit with a value;
  - a hit with null;
  - a miss after a reload cleared the cache, which falls back to the last known value.
- `Frozen_unit_never_changes`: later merges leave a frozen unit's records, coverage and `Observations` unchanged.
- `Batch_ids_are_unique_and_fit_the_store_bound`:
  - A 128-character segment, the minute of `DateTimeOffset.MaxValue`, and `long.MaxValue` for generation and serial give an ID of at most 192 characters (asserted on the string). That same ID string, on a record and a coverage run at a current minute, is accepted by `Append`.
  - 10,000 frozen units have distinct IDs, and none matches the `{segment}:{n}:{n}` pattern.
- `FreezeAll_and_Held_count_entries_and_runs`.

**Core, `ConnectionHistoryStoreTests`:**

- `Per_minute_rows_read_the_same_as_per_poll_rows`:
  - The same traffic is written as 60 per-poll batches into one store and as one per-minute unit into another.
  - The `Read` views (Rows, TotalRows, Summary, Markers, Coverage, Gaps, EarliestRetainedTime) and the CSV text are equal.
  - The per-minute file has one sixtieth of the traffic rows.
- `Grouped_read_matches_the_three_scan_reference`:
  - The fixture mixes per-poll and per-minute rows; local, special, unlocated and country-only peers; two origins at one location; more than 256 marker groups; and raised `expiry` and `floor`.
  - It is read with each filter: search text, search PID, country, Received and Sent.
  - Pages are read at offset 0, offset 200 and past the end.
  - Every result equals a test-local reference that runs 0.15's three statements (`ConnectionHistoryStore.cs:342,345,351-369`, with `kind=0` added to Unlocated) in one snapshot.
- `Grouped_read_writes_nothing_to_the_database`: `Read` leaves unchanged the bytes and lengths of the main file and `-wal`, the length of `-shm`, and `StorageBytes`, including with a pinned reader.
- `Grouped_read_over_the_temp_limit_fails_without_a_partial_view`:
  - With `maximumGroupedTempPages: 16` and 2,000 grouped rows, `Read` throws `ConnectionHistoryLimitException`, and a one-minute `Read` then succeeds.
  - The constructor rejects 15 and 16,385.
- `Unlocated_counts_public_peers_without_a_point_only`:
  - The fixture has 192.168.3.5 (no location), 9.9.9.9 (no location), a public peer with a country and no point, 100.64.0.1, 239.255.255.250, and a public peer with a point.
  - It asserts Unlocated 2, Local 1 and Destinations 6.
- **Changed:** `Markers_are_bounded_while_full_summaries_include_local_unlocated_and_every_country` (`tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs:183-190`) asserts `Unlocated` 1 instead of 2 at `:188`. The local 192.168.3.5 no longer counts; 9.9.9.9 still does.

**App (`tests/DeadWare.App.Tests/ConnectionServiceTests.cs`), new.** In every new test the clock follows the readings unless stated otherwise.

- `Open_minute_is_written_once_when_it_closes_and_live_is_unchanged`:
  - 63 one-second readings at Epoch + 1 … + 63 s.
  - No traffic record is appended before the reading at + 63 s, whose `CoverageFrom` is + 62 s.
  - Then one append holds one summed record for minute 0 and one complete coverage run [+1 s, +60 s).
  - `Live` after each reading equals that reading.
- `Stop_writes_the_open_minute`.
- `Session_ending_stop_writes_the_open_minute`: through `StopCollectorAsync` with 3 s, 1.5 s and 3 s.
- `Pause_writes_the_open_minute_at_the_next_tick_and_resume_uses_a_new_batch`: `FlushInterval` 20 ms.
- `Failed_write_retries_identical_units_and_new_data_uses_new_batches`.
- `Pressure_freezes_open_units_at_half_the_delta_budget`.
- `Small_clock_rollback_holds_a_frozen_minute_until_the_clock_passes_it`:
  - A reading at + 100 s with coverage from + 99 s, then a step back to + 50 s with a reading on segment "new".
  - The minute-1 unit is held and `Dropped` stays 0.
  - With the clock at + 95 s, R4 writes minute 0 while minute 1 is still held. At + 100 s, minute 1 is written unchanged.
  - A Theory case stops at + 95 s instead: the held unit is retired and `Dropped` is 1.

**App, changed.** These assumed per-poll batches, a write within 15 s while running, or a two-minute batch:

- `Pause_resume_acceptance_preserves_prior_writes_and_records_a_gap_with_saved_origin` (`:30`): bytes [10, 70]; the 30 and 40 merge in generation 2.
- `Normal_flush_records_unknown_when_lookup_is_stuck` (`:90`): clock at Epoch + 100 s; the record is written by R4.
- `Failed_storage_retries_keep_the_original_payload_when_lookup_later_changes` (`:99`): clock at Epoch + 100 s; the unit is written by R4.
- `Local_database_drains_final_data_and_history_reads_do_not_replace_live` (`:133`):
  - Built with the internal clock constructor at Epoch + 100 s.
  - With the real clock, its 2026-10-29 rows would expire from 2026-11-28 and its 81-byte assertion would fail.
- `Later_database_and_origin_updates_do_not_rewrite_saved_geography` (`:208`):
  - Clock at Epoch + 100 s before the first wait.
  - The second reading becomes a late unit with the Replacement location and origin (3, 4), written by R4.
- `Slow_enrichment_cannot_starve_a_due_flush_behind_all_queued_publications` (`:280`):
  - Clock at Epoch + 100 s.
  - It asserts that the first appended record holds 10–40 bytes, fewer than all five 10-byte publications. That shows the tick ran while publications were queued.
- `Clock_rollback_retires_unflushed_future_records_and_coverage_without_blocking_fresh_storage` (`:338`):
  - The step back goes to Epoch + 30 s, with `Reading(2,30,30,false,Epoch+29 s)` on segment "new".
  - That is 68 s past the minute-1 unit's end, beyond the hold, so R2 freezes the unit and the same step retires it (`Dropped` 1).
  - Without `stopImmediately`: wait for `Dropped` 1, then set the clock to Epoch + 95 s with `FlushInterval` 20 ms, so that R4 writes minute 0. With it: `StopAsync` writes minute 0.
  - Assertions:
    - a single row of 30 bytes at Epoch, with FirstSeen = LastSeen = Epoch + 30 s, incomplete;
    - a gap over [+99 s, +100 s];
    - no complete coverage over [+29 s, +30 s];
    - `BufferedDeltas` 0;
    - both status strings as today.
- `Clock_rollback_discards_whole_attempted_batch_without_changing_saved_receipts_or_retrying_its_safe_subset` (`:359`):
  - Clock at Epoch + 160 s, `FlushInterval` 100 ms. One publication at + 100 s (coverage from + 99 s) carries two minute-1 deltas: `Peer` at + 100 s (10 bytes) and `Peer` with RemotePort 80 at + 70 s (5 bytes).
  - R4 freezes the unit at a tick. The first append commits to the real store, then blocks.
  - The test steps the clock back to + 30 s, records an incomplete reading on segment "new" (30 bytes at + 30 s), and releases. The acknowledgement then throws.
  - The next attempt finds the unit 68 s ahead and retires it whole: `Dropped` 2.
  - `StopAsync` writes the new unit by R6.
  - Assertions:
    - the store shows 45 bytes;
    - the minute-1 rows hold 10 and 5 bytes from the committed attempt;
    - replaying `original` adds nothing;
    - `original` keeps LastSeen + 100 s and + 70 s and `Complete` true;
    - a gap covers + 30 s to + 31 s;
    - `BufferedDeltas` 0.
- Every other test in the file passes unchanged under these rules. Each was checked against them: `:21`, `:44`, `:53`, `:61`, `:67`, `:80`, `:109`, `:119`, `:152`, `:171`, `:195`, `:228`, `:262`, `:288`, `:294`, `:304`, `:315` and `:323`.

**Page check (`tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`), new:**

- `CheckHistoryLimitError`, called from `RunCore`: a `readHistory` that throws `ConnectionHistoryLimitException` shows "This interval holds too many saved rows to show. Choose a shorter interval." in the action status, and the History view shows no rows with "History unavailable; no saved observations shown.".

**Windows CI measurement, new `tests/DeadWare.App.Tests/RealWindows/ConnectionHistoryMeasurementTests.cs`:**

- `Connection_history_rates_and_read_timings`, a `[GitHubWindowsFact]` in `[Collection("RealWindows")]` (`tests/DeadWare.App.Tests/RealWindows/RealWindowsFacts.cs`). It writes `connection-history.json` through `MeasurementRecorder.Write`. It records only and decides nothing.
  - It records MiB/h of per-minute rows for the idle, 1-pair, 20-pair and 20-pair-with-churn loads, 12 simulated hours each, with one real `Append` per simulated minute, as the writer makes.
  - It fills a store to its 256 MiB cap with the 20-pair per-minute load, one `Append` per simulated hour.
  - On that file it records the 31 d, 7 d, 1 d and 1-minute `Read` times, and SQLite's heap high-water mark (`SQLitePCL.raw.sqlite3_memory_highwater`, reset before each read), with temp_store MEMORY and with SQLite's default. The default is reached through an internal store switch that only this measurement sets.

**0.17.0, with the writer hooks:**

- Core `Forget_removes_one_app_or_every_entry_and_keeps_coverage`: `Forget(key)` removes that app's entries from open units and keeps other apps and every run; `Forget(null)` removes every entry; a unit left empty is removed; `Held` falls by the entries removed; frozen units are unchanged.
- App `Excluded_deltas_leave_buffered_balanced`: excluded deltas are not stored, `Dropped` stays 0, the record and coverage stay complete, and `BufferedDeltas` is 0 after `StopAsync`.
- App `Clear_retires_held_in_scope_units_whole`: a unit held by the clock check that holds the cleared app is retired whole before the delete; a held unit without in-scope records is written later unchanged; the final write passes `maintenanceTransactions: 0`.

### Depends on

- **0.16.0 — the traffic strip on Connections History and Usage history:** defines its fields in `ConnectionHistoryView` and its query over `temp.grouped` and `temp.geo` inside `Read`.
- **0.16.0 — the traffic strip on Connections History and Usage history:** takes the strip from the last whole-interval read, which stays unchanged while the minute cursor moves, because a minute-cursor read (`ConnectionsPage.cs:197`) builds the TEMP tables for that minute only.
- **0.16.0 — the traffic strip on Connections History and Usage history:** marks the minute not yet saved as such in its last slot, with a tooltip naming that reason, never as a gap or a zero.
- **0.16.0 — the Destinations table:** reads History rows from `temp.grouped` and `temp.geo` in the same snapshot, using the stored `kind` and the `located` rule so that its Internet, Local and Unlocated figures agree with the summary, and states that it follows the minute cursor.
- **0.16.0 — the Destinations table:** adds its steps' timings to `Connection_history_rates_and_read_timings`, which runs in Windows CI as a measurement, not behind an environment variable.
- **0.16.0 — the traffic strip on Connections History and Usage history:** adds its 30-day `Read` timings, with the application filter empty and set, to `Connection_history_rates_and_read_timings`.
- **0.17.0 — Don't record these apps and Clear history…:** puts its Don't record filter immediately before the merge, through the hook above, and keeps what was merged before a list change: frozen units never change, so an app excluded mid-minute still appears in the part of that minute merged before the change.
- **0.17.0 — Don't record these apps and Clear history…:** runs a clear as a writer command in its stated order (marker, `Forget` for All time, `Flush`, then the delete steps), using the hooks above and retiring held in-scope units before the first delete.
- **0.17.0 — Don't record these apps and Clear history…:** keeps its "clear in progress" marker as a `metadata` row, which needs no schema change, since 0.15 and 0.16 read only `floor` and `expiry` by key and ignore it; a clear interrupted by a downgrade resumes when 0.17 starts again.
- **0.17.0 — Don't record these apps and Clear history…:** adds its one-app hide predicate to `GroupedWhere`, and has the page re-read the shown History view after a clear.
- **0.17.0 — First network use alerts:** reads `ConnectionSample.Deltas` on the collector thread, independent of this writer, and allows for a balloon naming an app whose minute is not saved yet.


## 0.16.0 — The traffic strip on Connections History and Usage history

A short stacked chart of the six applications with the most traffic, plus Other, as average rates per slot. It sits directly under the map on Connections History and replaces the share bar in Usage history's Applications card. Unrecorded time is hatched and never drawn as zero; the minute not yet saved stays empty and says why. On Connections it drives the minute cursor and sets an exact-application filter; on Usage history it joins the shared cursor, lists a slot's applications and offers **Show in Connections**.

- **Anchors** are `origin/main` at 181eb54 (0.14.1).
- **0.15.0 merges first.** It moves lines in `UsageHistoryPage.cs`, `ApplicationResourcesCard.cs`, `ProcessTable.cs` (`ProcessGrouping.Key`, `:50`) and `docs/user-guide.md`, and it makes the GPU chart a Moments chart (`docs/superpowers/specs/2026-11-06-per-app-gpu-design.md:123-127`). Below, "the Moments charts" means CPU, RAM and GPU. Re-anchor those files after 0.15.0 merges.
- **Stores.** The strip reads `connections.db` and `usage-history.db` only. It never reads or writes `process-history.db`, so 0.15.0's version 2 schema (GPU columns, 40 listed apps a minute plus Everything else, the 192 MiB cap) is untouched. It meets Application resources only through the shared cursor and `SlotSpan`.
- **No schema change** in either store, and no new setting.
- **The Unlocated fix** does not affect the strip.
- **Other sections are named by title:** *Per-minute merge, the grouped read and the Unlocated fix* (0.16.0), *The Destinations table* (0.16.0), *First network use alerts* (0.17.0), and *Don't record these apps and Clear history…* (0.17.0). What each must supply is under Depends on.

### What the strip plots

- **Series.**
  - The six applications with the most traffic over the whole interval or period (`TrafficStrips.MaximumApplications` = 6), stacked with the largest at the bottom, then **Other** on top, holding every other application.
  - Other is left out of the chart and the legend when its measure total is 0.
  - Why six: the graph keys resolve to five theme roles (`Theme.cs:59`): Download, Gpu, Cpu, Accent and Warning. Accent is the cursor hairline and focus frame, and Warning marks warnings, so three hues remain, and each gets one `CoreTint` shade (Drawing).
  - Measured from the nine palettes (`Theme.cs:70-78`) and `ThemeTokens.CoreTint`, the closest pair among the six is ΔE 21.3 (CIE76; Mage, series 0 and 1), then ΔE 24.1 (Skyrim, series 0 and 2). Every other theme's closest pair is at least ΔE 32.
  - The 0.14 share bar showed eight apps in about four colours (`UsageHistoryPage.cs:303`).
- **Measure.**
  - Usage history: received + sent.
  - Connections: set by the Direction filter (`ConnectionsPage.cs:92`). Both → received + sent, Received → received, Sent → sent.
  - Ranking uses the same measure. Overflow rules are under Errors and limits.
- **Band height.** A series' average rate in the slot: its measure bytes ÷ the slot's `RecordedSeconds`, in B/s, the unit of the Network chart. A slot whose `RecordedSeconds` is 0 has no value (null), never 0.
- **Ranking.**
  - By measure total over the whole interval or period, descending.
  - Ties: by app key ordinal on Connections; by name on Usage history (the `ReadApplications` order, `UsageHistoryStore.cs:198-201`).
  - An application whose measure total is 0 never ranks. (Usage history never stores a zero-byte row, `UsageHistory.cs:173`.)
  - The six are fixed for the interval, so one colour means one application across the whole strip.
- **Identity.**
  - Connections groups stored names by app key and shows the ordinal-smallest stored name of each key in the interval.
  - Usage history keeps stored names exactly as the Applications table does, so the strip and the table agree name for name.
  - "PID n" and "Unknown application" are ordinary names here.
- **Connections slots.**
  - Whole-minute slots, counted from the first whole minute at or after `From` (the origin). The page's `From` is always a whole local minute, so the origin is `From`.
  - Slot length: the first of 1, 2, 5, 10, 15 or 30 minutes that gives at most 1,500 slots (`TrafficStrips.MaximumSlots`). If none does, ⌈minutes ÷ 1,500⌉; only an interval the page does not offer gets that far.
  - Examples: Today → 1 min (1,440 slots; a 25-hour DST day gives 1,500), 7 days → 10 min (1,008), 30 days → 30 min (1,440), 31 days → 30 min (1,488).
  - The last slot ends at `To`.
- **Usage history slots.** `ReadMetric`'s grid, with the page's point count (`UsageHistoryStore.cs:164,169`; `UsageHistoryPage.cs:225`):
  - bucket = max(60,000 ms, ⌈(to − from) ÷ points⌉);
  - slot k = [from + k × bucket, min(from + (k + 1) × bucket, to));
  - a minute belongs to slot ⌊(minute − from) ÷ bucket⌋.
  - These are exactly the Moments charts' slots (0.15.0 reads the GPU chart with the CPU chart's points). They are not whole minutes: a slot holds ⌊length⌋ or ⌈length⌉ whole-minute starts.

### Slot states

Each slot is a `TrafficStripSlot`, and only slots with `Minutes` > 0 are listed.

*Due until* = min(`To`, now − SaveDelay). SaveDelay = the owning service's flush interval (15 s) + the polling interval, as in `ApplicationResourcesCard.SaveDelay` (`:278`). A whole minute is *due* when its end ≤ due until. The open minute, and a closed minute that may not be saved yet, are therefore never gaps. The *retained start* is `RetainedFrom` on Connections when set, otherwise the origin.

| State | Rule | Drawn | Hover chip status lines |
|---|---|---|---|
| Not recorded | a due minute in no listed slot, at or after the retained start | hatched | none (no hover target) |
| No per-application capture (Usage only) | listed, `RecordedSeconds` 0 | hatched; still a hover and keyboard target | "No per-application capture" |
| No traffic | `RecordedSeconds` > 0, measure bytes 0 | baseline only | "No traffic" |
| Full | `TrafficStrips.IsFull` | bands at 0.85 opacity | none |
| Partial | listed, not full | bands at 0.45 opacity | "Recorded m of n min"; also "Incomplete capture" when `Incomplete` |
| Not kept (Connections only) | before `RetainedFrom` | solid band labelled "Not kept" | none |
| Not yet | not due | empty | "Not saved yet" and "Minutes are saved after they end", over begun minutes only (below) |

- **Order.** States are checked top to bottom. A No traffic slot that is not full also shows Partial's status lines.
- **`IsFull(slot)`.** `Incomplete` is false and `RecordedSeconds` ≥ 60 × max(1, n − 0.5), where n is the number of whole minutes that start in [Start, End).
  - On Connections, n is the slot's length in minutes.
  - On Usage history, a fully recorded 6.72-minute slot with 6 minute starts is full. 0.16.0 applies the same count to the line charts (next subsection).
- **"Recorded m of n min".** n is as above, and m is `RecordedSeconds` ÷ 60 rounded to the nearest minute.
- **A Connections slot under one second.** On Connections a listed slot has `RecordedSeconds` 0 only when its coverage totals under one second (a piece cut at a minute boundary when capture starts or stops). It is Partial: no band, no value line, the chip reads "Recorded 0 of n min" (plus "Incomplete capture" when `Incomplete`), and it is not hatched. Its legend values show "—" with the tooltip "Under a second recorded in this slot".
- **Not yet with data.** A listed slot inside the Not-yet range is drawn by its own state.
- **Not saved yet.** Defined here for *Per-minute merge, the grouped read and the Unlocated fix*, whose open minute is not in the file yet.
  - The Not-yet minutes that have begun, from the start of the first minute that is not due to min(`To`, the end of the minute that holds now), form one hover target wherever no listed slot is drawn.
  - Its chip shows the span ("10:41–10:42", with the date as for slots) and the two status lines in the table.
  - Time after now (the rest of Today, for example) has no hover target. The target is not a keyboard target and is never selected or activated.

### Usage history line charts: whole-minute fullness

- `UsageHistoryChart.IsFull` (`UsageHistoryChart.cs:29-30`) compares a bucket's `Minutes` with its length. A bucket on `ReadMetric`'s grid holds ⌊length⌋ or ⌈length⌉ whole-minute starts, so when the fraction is above 0.5, a fully recorded bucket with the lower count never connects: 420 of 1,500 Week buckets at 1,500 points, and 200 of 700 Month buckets at 700 points.
- 0.16.0 changes it to the strip's count: a bucket is full when `Minutes` ≥ max(1, n − 0.5), with n = `TrafficStrips.MinuteStarts(Start, End)`. `CanConnect` (`:31-32`) is otherwise unchanged.
- This applies to every `UsageHistoryChart`: the Device charts and the Application resources chart, including 0.15.0's GPU series, whose `Minutes` counts only the minutes the app was listed with GPU data (`docs/superpowers/specs/2026-11-06-per-app-gpu-design.md:121`), so a bucket missing any of them stays partial.
- Only which full buckets connect changes. No value, band or statistic changes.

### Core model

New file `src/DeadWare.Core/TrafficStrip.cs`:

```csharp
public enum TrafficMeasure { Total, Received, Sent }
public sealed record TrafficStripApplication(string Name, string Key, long Received, long Sent);
// Received and Sent hold one entry per application in TrafficStrip.Applications, then Other: Applications.Count + 1 entries.
public sealed record TrafficStripSlot(DateTimeOffset Start, DateTimeOffset End, long Minutes, long RecordedSeconds, bool Incomplete,
    IReadOnlyList<long> Received, IReadOnlyList<long> Sent);
public sealed record TrafficStrip(DateTimeOffset From, DateTimeOffset To, long BucketMilliseconds, TrafficMeasure Measure,
    IReadOnlyList<TrafficStripApplication> Applications, long OtherReceived, long OtherSent, long OtherApplications,
    IReadOnlyList<TrafficStripSlot> Slots, DateTimeOffset? RetainedFrom = null, bool CapacityShortened = false);
```

**Record fields.**
- The applications' `Received`/`Sent` and `OtherReceived`/`OtherSent` are totals for the whole interval or period, used by the legend.
- `OtherApplications` counts the other keys (Connections) or names (Usage history) that have any bytes.
- `Slots` are in ascending `Start` order.
- `Minutes`: the slot's recorded minutes.
- `RecordedSeconds`: its recorded time in whole seconds, rounded down.
- `Incomplete`: set by each page's rules below.

**`TrafficStrips` (static).**
- Constants: `MaximumApplications` (6), `MaximumSlots` (1,500), and `ConnectionsRetentionDays` (30, the cutoff in `ConnectionHistoryStore.cs:146`).
- `ConnectionsBucketMinutes(DateTimeOffset from, DateTimeOffset to)`: the slot length rule above.
- `MinuteStarts(DateTimeOffset start, DateTimeOffset end)` (long): the number of whole minutes that start in [start, end).
- `IsFull(TrafficStripSlot)`: the rule under Slot states, with n = `MinuteStarts(slot.Start, slot.End)`.
- `DueUntil(DateTimeOffset to, DateTimeOffset now, TimeSpan saveDelay)`: min(to, now − saveDelay).
- `Bytes(TrafficStripSlot, int series, TrafficMeasure)`: series `Applications.Count` is Other.
- `Rate(TrafficStripSlot, int series, TrafficMeasure)` (double?): null when `RecordedSeconds` is 0.
- `SlotAt(TrafficStrip, DateTimeOffset)` (int?): the listed slot whose [Start, End) holds the time.
- `SlotLengthText(long bucketMilliseconds)`: "1-minute", "3.2-minute", "1.6-hour". Under one hour it gives minutes with `0.#`, otherwise hours with `0.#`, in the current culture.
- `ConnectionsTarget(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)`: see Show in Connections.

**App key.** The strip's app key is the existing `ProcessGrouping.Key(name)` (`ProcessTable.cs:50`): `UsageMetrics.NormalizeName(name).ToUpperInvariant()` (`UsageHistory.cs:33-38`).
- It is the function *The Destinations table* registers as the SQLite `app_key`, so the strip's C# keys and `app_key` always agree.
- Stored Connections names can be 260 characters (`ConnectionHistoryStore.cs:101`). The key keeps the first 256 after control characters are removed and the text is trimmed, so two names that differ only after character 256 share a key.
- The key is idempotent (Key(Key(x)) == Key(x)) for every name whose 256-character cut does not end in white space, which includes every name of at most 256 characters. Executable names are at most 255 characters. The page relies on this, because it passes a key back through `Normalize`.

**Save delays.** Two new `public static readonly TimeSpan` fields, `UsageHistoryService.DefaultFlushInterval` and `ConnectionService.DefaultFlushInterval`, both 15 s, like `ProcessHistoryService.DefaultFlushInterval` (`ProcessHistoryService.cs:13`). They replace the literal 15 s defaults in `UsageHistoryService`'s constructor (`:28`) and in `ConnectionService.WriteLoopAsync`'s timer (`:304`). Each page's SaveDelay is its service's constant + `session.Settings.PollingIntervalSeconds`.

### Connections: data

**Exact application field.** Defined here for *The Destinations table*.
- `ConnectionHistoryQuery` (`ConnectionModels.cs:29-31`) gains `string Application = ""` as its last parameter, so existing calls still compile.
- `Normalize` (`ConnectionHistoryStore.cs:395-399`) rejects null or more than 260 characters, the `Search` bound. It trims the value, keeps "" (every application), and stores anything else as `ProcessGrouping.Key` of it.
- `Arguments` (:400) adds `$application`.
- `Grouped`'s WHERE (:29-32) adds `AND ($application='' OR app_key(name)=$application)`.
- **Where it applies.** The predicate is part of `Grouped`'s WHERE, so the `temp.grouped` build of *Per-minute merge, the grouped read and the Unlocated fix* (which is `Grouped` joined to `temp.geo`) carries it. The rows, summary, markers, strip, Destinations table and `ExportCsv` (:445) therefore all honour it. An application-filtered CSV holds only that application.
- **A filter like the others.** `Application` joins `SameHistoryContext` (`ConnectionsPage.cs:203-205`) and `BuildQuery` (`:195-200`), so the Destinations table and Export destinations inherit it, and a non-empty `Application` counts as a set filter for the History empty texts.
- **The strip's use of `app_key`.** Defined here for *The Destinations table*: the strip calls `app_key` only in this predicate, and only when the field is set. Its own grouping folds stored names to keys in C# (step 2 below), never per row in SQL.
- Measured on 300,000 rows with Microsoft.Data.Sqlite 10.0.14: about 9 ms with the field empty (the constant left side of the OR short-circuits), and about 67 ms warm (165 ms cold) with it set.

**The strip.** Defined here for *Per-minute merge, the grouped read and the Unlocated fix*: the view member and its queries over the TEMP tables.
- `ConnectionHistoryView` (`ConnectionModels.cs:45-49`) gains `TrafficStrip? Strip = null`, trailing and optional. Defined here for *The Destinations table*: `Read` (:384) passes it by name, as it passes `Destinations`, so the order of the two members does not matter.
- `Read` (:334-386) fills it in the same read transaction and snapshot as the summary and markers, after `temp.grouped` is built, from `temp.grouped` and `coverage`, for the query's [From, To) and filters. It does not use `temp.geo`, because the strip ignores location. A minute read therefore gets a one-slot strip, which the page ignores.
- Steps:
  1. **Grid.** origin = the first whole minute ≥ `From`. Slot length b = `ConnectionsBucketMinutes(From, To)`, and `$bucket` = b × `TimeSpan.TicksPerMinute`, because minutes are stored in ticks. When origin ≥ `To`, `Slots` is empty.
  2. **Totals.**
     - `SELECT name, sat_sum(received), sat_sum(sent) FROM temp.grouped GROUP BY name`, folded in C# by `ProcessGrouping.Key` with saturation. `Direction` sets the measure.
     - The top six keys, ranked as above, are `Applications`. Each `Name` is the ordinal-smallest stored name of its key, and all other keys go into Other.
  3. **Slot bytes.**
     - `SELECT (minute − $origin) / $bucket, name, sat_sum(received), sat_sum(sent), min(complete) FROM temp.grouped GROUP BY 1, name`.
     - C# folds each row into its slot's series: its key's series if it is in the top six, otherwise Other.
     - SQL groups by stored name and C# maps the distinct names to keys, so `app_key` is never called per row here. Measured: grouping by a managed `app_key(name)` takes about 175 ms against about 100 ms by name.
  4. **Retained start.**
     - Read `floor` and `expiry` from `metadata` once, in the read transaction. Today `Read` reads only `floor` into C# (:380); `expiry` appears only inside its SQL.
     - retained = max(the first whole minute ≥ `expiry`, `floor` + 1 min). Traffic is kept only for minutes > `floor` and minutes ≥ `expiry` (:29-30), so this is the first minute that can hold rows.
     - It comes from `floor` and `expiry` only, never from the earliest-retained query (:371-379).
  5. **Recorded time per minute.**
     - Take the coverage pieces with `stamp` > floor, `stamp` ≥ retained, `finish` > `From` and `start` < `To`, grouped by `stamp`.
     - Clip each piece to [max(`start`, `From`), min(`finish`, `To`)).
     - When `expiry` is not a whole minute, the minute that holds it has its traffic excluded (:29) and its coverage clipped to the cutoff when written (:221); `stamp` ≥ retained already leaves its pieces out, so no listed slot falls inside "Not kept". When `expiry` is a whole minute (0.17.0's all-apps clear sets it to `Before`), that minute is retained and its coverage counts.
     - A minute's covered time is the sum, capped at 60 s. The minute is incomplete when any of its pieces has `complete` 0.
     - A minute with rows in `temp.grouped` but no coverage piece counts as 60 s and incomplete, so traffic is never dropped for missing coverage. Under a filter this applies only where the filter matches a row. A minute without coverage whose rows the filter excludes stays not recorded: unknown, never zero.
  6. **Slot fields.** A slot's `Minutes` counts its minutes that have coverage or rows. `RecordedSeconds` = Σ covered time. `Incomplete` = any incomplete minute, or any row with `complete` 0. Slots with `Minutes` 0 are left out.
  7. **`RetainedFrom`.** retained, when it is after origin; otherwise null. It can be at or after `To` when the whole interval is older than what is kept.
  8. **`CapacityShortened`.** floor > 0 and floor + 1 min > the first whole minute ≥ `expiry`. `Read`'s status (:381) uses the same condition instead of floor > 0; its copy is unchanged. The capture line and the strip then agree, and once the 30-day expiry passes the evicted minutes, neither says "Capacity shortened retention".
- **Coverage and filters.** Coverage belongs to the whole capture, so filters never remove it; the only filter-dependent case is the rows-without-coverage rule in step 5. Where a filter matches nothing during covered time, the strip shows a known zero, not a gap.

### Connections: page

**Placement.** Defined here for *The Destinations table*, which owns the body-row list and `MeasureOverride`.
- **The card.** In History mode only, a card `trafficCard` holding a `TrafficStripView` sits directly under the map card (`ConnectionsPage.cs:96-97`), full width. The controls card is already called `strip` in code (:94), hence the new name.
- **Row.** `trafficCard` is body row 2 (Auto) in that section's rows 0–6: header, map card, traffic strip, node list, footer, Destinations card, settings. The compact node list stays under the map as today (`:102`), now below the strip.
- **Live.** The card is collapsed in Live mode, so its desired height is 0.
- **Measure.** That section's `MeasureOverride` (`:137-146`) measures `trafficCard` with the header, footer, Destinations card and settings, and adds its desired height to `minimum`. The 300 for the map and 170 for the list stay, so the map keeps at least 300 px and the page scrolls instead.
- **Size changes.** `trafficCard.SizeChanged` invalidates measure, like the others (`:127`).

**Queries.** `BuildQuery` (`:195-200`) builds both queries from one `TryInterval` call:
- It returns the query it returns today (one minute while the cursor is on) and gives the whole-interval query through an `out` parameter.
- `cursorTime` is set only from the returned query. Today a `BuildQuery(true)` call while the cursor is on writes the interval start into `cursorTime` (`:198`). `ExportAsync` (`:331`) and Export destinations use the `out` query for the same reason.

**Strip reads.**
- **Fields.**
  - `stripContext`: the whole-interval query of the strip on screen, or null.
  - `stripNow`: when that strip was applied.
  - `pendingStrip`: a whole-interval query waiting to be read.
  - `stripReading`: the one in flight.
  - `stripFailed`: the whole-interval query of the last failed strip read.
- **Context.** `SameHistoryContext` (`:203-205`) also compares `Application` (ordinal).
- **Minute cursor off.** The one read per request (`RequestHistory`, `:206-216`) carries the strip. `ApplyHistory` (`:244-257`) applies it and sets `stripContext` and `stripNow`. A view whose `Strip` is null (a failed read, the TEMP limit, or no service: `Empty`, `:150`) shows "History unavailable".
- **Minute cursor on.** Defined here for *Per-minute merge, the grouped read and the Unlocated fix*: a minute read builds TEMP tables for its minute only, so the strip comes from the last whole-interval read and stays unchanged while the cursor moves.
  - A cursor move reads only its minute, as today, and the strip stays.
  - If the whole-interval query is not `SameHistoryContext` with `stripContext`, `stripReading`, `pendingStrip` or `stripFailed`, `RequestHistory` sets `pendingStrip` to it.
  - When it differs from `stripContext`, the strip clears at once and shows "Reading saved history…". The map clears on every cursor move (`:209-212`); the strip clears only when the whole-interval context changes.
- **Pump.**
  - `PumpHistoryAsync` (`:217-230`) loops while the page is in History and `pending` or `pendingStrip` is set.
  - It reads `pending` (the minute) first, and `pendingStrip` only when no minute is pending.
  - The strip read takes no cancellation token and is not invalidated by `generation`. When it returns, its `Strip` is applied only if the page is still in History and its query is `SameHistoryContext` with the current whole-interval query; otherwise it is dropped. Its rows, summary, markers and Destinations are never applied; the Destinations table follows the minute read (*The Destinations table*, Where rows come from).
  - A minute queued during the strip read is read right after it.
- **Cancelling.**
  - `CancelHistory` (`:202`) leaves `pendingStrip` and `stripReading` alone, so moving the cursor never drops or restarts the strip read.
  - `ChangeMode`, `Unloaded` and hiding the page clear `pendingStrip`. A change to Live also clears `stripContext`.
- **Showing the page again.** When `stripContext.To` is after `stripNow`, `stripContext` is cleared, so the next `RequestHistory` re-reads the strip. Otherwise the minutes saved since would stay "Not yet".
- **Re-reading saved data.** Defined here for *Don't record these apps and Clear history…*: `internal void ReloadHistory()` clears `stripContext` and `stripFailed` and, when the page is in History, calls `RequestHistory()`. The strip is therefore re-read with the minute cursor on as well as off. The page's re-read after a clear uses it; Live is unaffected.
- **Playback.** Playback waits for the strip read, as it waits for any read (`AdvancePlayback`, `:233`), because the pump stays busy.
- **Cost.** With the cursor on, a context change reads its minute first (under 5 ms), then the whole interval for the strip: about 1.9–2.5 s for 7 or 30 days as benchmarked, plus about 0.45 s for *The Destinations table*'s steps (an open question), and 0.25 s for one day on a full per-minute file (the timings in *Per-minute merge, the grouped read and the Unlocated fix*). A cursor move during that read waits for it, and moving the cursor never starts another strip read.
- **Empty texts.**
  - A failed strip read whose context is still current shows "History unavailable" and records `stripFailed`. actionStatus also shows the existing "History could not be read (TypeName)." (`:226`), or that section's limit text for `ConnectionHistoryLimitException`. The same context is not read again until it changes, the page is shown again, or `ReloadHistory` runs.
  - An interval that does not parse shows "Choose a valid interval".
  - A read with no listed slot and the interval not wholly before `RetainedFrom` shows "No saved history in this interval" over its hatching.
  - An interval wholly before `RetainedFrom` shows the "Not kept" band over the whole plot (next item).
- **An interval Connections no longer keeps.** Defined here for *First network use alerts*, whose View in Connections can open one.
  - The page opens the interval as asked; nothing is clipped or moved.
  - Wholly before `RetainedFrom` (the 30-day expiry or the 256 MiB cap removed it): the plot is one "Not kept" band, the legend reads "No application traffic recorded in this interval.", and the line under it reads "Not kept before {RetainedFrom}" (with " · Capacity shortened retention" when `CapacityShortened`). The storage line keeps naming the earliest retained time ("Earliest {g}", `:249`). The summary, map and Destinations table show what the file holds for the interval, which is nothing.
  - Kept but not recorded (recording off, or DeadWare not running): its due minutes are hatched, and the legend reads the same no-traffic line.
  - Partly kept: [origin, `RetainedFrom`) is the "Not kept" band, and the rest is drawn by its slot states.
- **Time of the read.** The page passes `DateTimeOffset.UtcNow` at the moment it applies the strip as "now". Due until = `DueUntil(To, now, Connections SaveDelay)`. The Today preset runs to tomorrow's midnight (`:177`); without "now" the rest of today would be hatched as gaps.

**Time cursor.** The strip shares the minute cursor (`minute`, `cursor`, `:49-50`).
- **Hairline.**
  - While the minute cursor is on, the page sets the chart's `MarkTime` to the middle of the cursor minute (From + `cursor.Value` minutes + 30 s), and `SelectedIndex` to `SlotAt` of that time (null in a gap). The chart draws an Accent hairline at `MarkTime`.
  - The page updates both whenever the cursor value or the minute checkbox changes, including changes made under `syncing`. `cursor.ValueChanged`'s handler updates the hairline before its `syncing` check (`:130`), so the hairline moves at once, before the minute's read returns, and also during playback, which steps the cursor under `syncing` (`:233`).
  - With the cursor off, both are null.
- **Click.** A click on a listed slot turns the minute cursor on at the minute under the pointer: index = ⌊(time − From) ÷ 1 min⌋, clamped to [0, `cursor.Maximum`].
  - It sets `minute` and `cursor` under `syncing`, so `FiltersChanged` does not reset the cursor to 0 (`:201`).
  - It then calls `StopPlayback`, `ClearSelection` and `RequestHistory`.
  - A click on a gap does nothing.
- **Keys** (with the strip focused). The chart raises `SelectionMoved` and `SlotActivated`, and the page acts.
  - Left/Right select the previous or next listed slot, counted from `SelectedIndex`.
  - When `SelectedIndex` is null but `MarkTime` is set (the cursor is in a gap), Left takes the last listed slot that ends at or before `MarkTime`, and Right the first that starts after it.
  - With the cursor off, Left takes the last listed slot and Right the first.
  - At the ends the selection stays and nothing is read.
  - The page then turns the minute cursor on at the selected slot's first minute, as a click does: one minute read per key press.
  - Enter raises `SlotActivated` at the selected slot's Start, and the page treats it as a click there.
- **Stale boxes.** Clicks and keys act only while the from/to boxes parse to `stripContext`'s From and To (`TryInterval`, `:178-185`). After an edit that has not been applied they do nothing; otherwise `RequestHistory` would apply the edit with a minute index computed for the old interval.
- **Esc** keeps its meaning on this page: it closes the node details (`:134`).

**Application filter.**
- **Fields.** The page gains `applicationFilter` (the key; "" means all) and `applicationName` (as given, for display).
- **Queries.** `BuildQuery` passes `applicationFilter` as `Application`. `Matches` (`:315`) adds `(applicationFilter.Length == 0 || ProcessGrouping.Key(process.Name) == applicationFilter)`, so Live and History filter alike.
- **Button.** In the filters row, after Direction (`:91-92`), a button shows "Application: {applicationName} ✕" while the filter is set; otherwise it is collapsed. It shows in Live and History.
  - Its text trims at 240 px with an ellipsis.
  - Automation name: "Clear application filter".
  - Tooltip: "Only {applicationName} is shown. Click to show every application."
  - A click clears both fields and calls `FiltersChanged()`.
- **Legend.**
  - On the strip, each application's legend entry is a button that sets the filter to its key and name, then calls `FiltersChanged()`. Like any filter change, this resets the cursor to its first minute.
  - Other is not a button.
  - While the filter is set, the strip holds that one application and no Other.

**ShowHistory.** `internal void ShowHistory(DateTimeOffset from, DateTimeOffset to, string? application)` opens History for whole minutes. It is the entry point for Show in Connections and for 0.17.0's View in Connections.
1. `StopPlayback(); ClearSelection();`. start = the first whole minute ≥ from; end = max(the first whole minute ≥ to, start + 1 min). Callers pass at most 30 days and 1 minute.
2. **Write the boxes.**
   - Write both times as local `yyyy-MM-dd HH:mm` text.
   - While the local start is invalid or ambiguous (`TryParseLocalTime`'s rule, `:186-194`), move it one minute earlier. While the local end is, move it one minute later. At most 180 steps each, so the interval always contains the one that was asked for.
   - The widening adds at most 6 hours, so `TryInterval`'s 31-day limit (`:181`) is never reached.
3. **Under `syncing`, set:**
   - the `from`/`to` text; `search` and `country` to ""; Direction to Both;
   - the minute cursor off and the cursor to 0;
   - `applicationFilter` to `ProcessGrouping.Key(application)` ("" when null or blank), and `applicationName` to `application`. The button follows.
   - `range` is not touched, because changing it would apply a preset (`:129`).
4. **Read.** From Live: `mode.SelectedIndex = 1` (`ChangeMode` then reads, `:175`). From History: `RequestHistory()`.

**Copy.**
- Caption above the chart: "Traffic by application · {SlotLengthText} slots", plus " · received" or " · sent" when Direction is set to one of them.
- Under the legend, when `RetainedFrom` is set: "Not kept before {RetainedFrom in local time, "g"}", plus " · Capacity shortened retention" when `CapacityShortened`.
- The expander note (`:124`) gains, right after *The Destinations table*'s sentence "The node list follows the nodes visible on the map; the Destinations table lists every destination, including local, special-purpose and unlocated ones.": "The strip under the map stacks the six applications with the most traffic in the interval, plus Other; its colours identify applications, not direction. Click it to move the minute cursor, or click an application to show only it."

### Usage history: data

`UsageHistoryStore.ReadApplicationSeries(string machine, DateTimeOffset from, DateTimeOffset to, int maxPoints = 1500)` returns a `TrafficStrip` with `Measure` Total and no `RetainedFrom`. It uses one read-only connection and one deferred transaction, so its statements share one snapshot.
- **Validation:** as `ReadMetric` (`:158-161`), it checks the machine and the range. `maxPoints` must be 1–4,000 (`ArgumentOutOfRangeException`).
- **Grid:** `ReadMetric`'s, as above.
- **Ranking and totals:**
  - The two `ReadApplications` statements (`:198-210`), with limit 6. So `Applications` equals `ReadApplications(...).Applications.Take(6)` for the same snapshot, with the same totals.
  - `OtherReceived`, `OtherSent` and `OtherApplications` hold the rest.
  - Totals include capture-gap minutes' rows, as the table's do.
- **Recorded and captured minutes:** a minute is recorded when `metric_minutes` or `application_minutes` holds a row for it. It is captured when it is recorded and not in `capture_gaps` (`:64`).
- **Slot fields:** `Minutes` counts the slot's recorded minutes; `RecordedSeconds` = 60 × its captured minutes; `Incomplete` = captured < recorded.
- **Slot bytes:**
  - Sums of the `application_minutes` rows of captured minutes only, per name (one of the six, otherwise Other).
  - A capture-gap minute's rows stay in the totals and the table but stay out of the strip. The folder saves rows for those minutes, which are "not trustworthy" (`UsageHistory.cs:10`, :111, :173-175).
- **Listing:** slots with `Minutes` 0 are left out. Minutes before the earliest retained minute draw as not recorded, as on the Device charts.
- **Implementation note:** group natively by slot and name (`application_minutes_time`, `:63`). Read the few capture-gap minutes' rows separately and subtract them, rather than probing `capture_gaps` for every row.

`UsageHistoryService` gains two wrappers, written like `ReadApplicationsAsync` (`UsageHistoryService.cs:167-179`). On a failure other than an argument error each calls `Fail` and returns null, as `ProcessHistoryService.TryReadApplicationsAsync` does (`ProcessHistoryService.cs:154-168`). An unreadable file is therefore never shown as an empty period or slot.
- `TryReadApplicationSeriesAsync(from, to, maxPoints)` for the strip.
- `TryReadApplicationsAsync(from, to)` for the slot read. The period read keeps `ReadApplicationsAsync` unchanged.

### Usage history: page

**Placement.** In the Applications card (`UsageHistoryPage.cs:86-95`), the strip view takes the share bar's place, between the search row and the summary. The share bar (`shareBar`, `:38`, and `:298-310`) is removed. The legend gives each application's share, and the Share column stays.

**Reads.**
- **The strip reads on its own.** `LoadAsync` (`:222-247`) starts it the way `resources.Load` starts the card (`:228`), with the same `from`, `to` and `points`, and does not await it, so the Device charts and the table never wait for a second scan of `application_minutes`.
  - The strip has its own request counter. A result is shown only if it is still the latest request and the page is loaded and visible.
  - `internal void ApplyStrip(TrafficStrip? strip, DateTimeOffset now)` shows it, with due until = `DueUntil(period end, now, Usage SaveDelay)`; null shows "History unavailable". It then sets the strip's `SelectedIndex` from `marked`, as `MarkMoment` does.
  - Defined here for *Don't record these apps and Clear history…*: the strip keeps nothing between refreshes, and every refresh (`RefreshAsync`, `:205-221`) re-reads it with the table. The page's re-read after a clear therefore refreshes the strip.
- **The slot read.** While an Applications slot is listed, each refresh also re-reads it with `TryReadApplicationsAsync(slot.Start, slot.End)`. It has its own request counter, and `internal void ApplySlotApplications(UsageApplicationView? view)` shows it. Null shows the summary "History unavailable for this slot." with no rows and no gap notice.
- **Consistency.** The strip, the table and the slot list come from separate reads. Until the next minute refresh they can differ by the minutes saved between the reads.

**One cursor.** The strip joins 0.14.1's cursor (`marked`, `:48`) but is not a Moments chart (`:30`): it never lists the Application resources card.
- **Following the Moments charts.** `MarkMoment` (`:181-191`) also sets the strip's `SelectedIndex` to `SlotAt(strip, marked)`. Left, Right and clicks on the CPU, RAM and GPU charts therefore move the strip's hairline, matched by time. The page never sets the strip's `MarkTime`, so its keys follow `UsageHistoryChart.HandleKey`'s rules (`UsageHistoryChart.cs:83-94`), like the charts beside it.
- **Moving on the strip.** The strip's `SelectionMoved` sets `marked` to the slot's middle and calls `MarkMoment`, as `MoveMoment` does (`:172-176`). The Moments charts' hairlines follow, and nothing is listed.
- **Listing a slot.** A click on a listed slot, or Enter on the selected one, lists that slot in the Applications table (`ShowApplicationsSlot`):
  - `applicationSlot` = the slot's [Start, End); `marked` = its middle; `MarkMoment` runs.
  - The heading becomes, for example, "Applications · 03:13–03:16". It uses `ApplicationResourcesCard.SlotSpan(first, lastEnd, withDate)`, the time span that `SlotHeading` (`ApplicationResourcesCard.cs:181-187`) now builds on ("Busiest apps · " + the span, unchanged), with first = `CeilingMinute(Start)` and lastEnd = `CeilingMinute(End)`, and the date on Week and Month.
  - **Clear** shows, the summary reads "Loading this slot…", and a slot read starts at once. A stale result is dropped.
  - Keyboard focus stays on the strip, and the table is brought into view. Only activation does this, never the refresh.
  - A strip activation never lists or clears the Application resources card's slot, and a Moments-chart activation (CPU, RAM or GPU) never lists or clears the Applications slot. Each card's heading names its own slot, and one hairline marks `marked`.
- **While a slot is listed:**
  - The table, summary and gap notice describe the slot; an empty slot reads "No application traffic recorded in this slot.".
  - Search still filters the rows, and the minute refresh re-reads the slot.
- **Clearing.** **Clear** on either card, or Esc, clears the moment: both cards return to the period, and `marked` and every hairline clear.
  - The Esc guard (`:104-111`) becomes `resources.Slot is not null || applicationSlot is not null || marked is not null`.
  - Esc and the Applications **Clear** call `ClearMoment()`: clear the Applications slot, then call `resources.ClearSlot()`. `ClearSlot` always raises `SlotCleared` (`ApplicationResourcesCard.cs:165-177`).
  - The `SlotCleared` handler (`:103`) also clears the strip's `SelectedIndex` and the Applications slot. A range change, ◀ Earlier, Later ▶ and `ShowDay` (`:122`, `:150-157`) therefore clear it too.
  - If the Applications **Clear** has focus when it collapses, focus moves to the Applications search box, the control below it (as the card moves it to Rank by, `ApplicationResourcesCard.cs:168-173`).

**Show in Connections.** A **Show in Connections** button sits in the Applications heading row, after **Clear**.
- **Target.** The Applications table's listed slot (never the Application resources card's), otherwise the period, passed through `TrafficStrips.ConnectionsTarget(from, to, now)`:
  - it covers the whole minutes that start in [from, to);
  - the retention start is the first whole minute ≥ now − 30 days;
  - it returns null (the button is disabled) when the target ends at or before the retention start, and otherwise [max(start, retention start), end).
- **Application.** Defined here for *Don't record these apps and Clear history…*: the request carries the selected row's name exactly as the table shows it, the bare stored name, never a key or a mark; `ShowHistory` keys it. The "Other applications" row and "Unknown application" (`UsageMetrics.UnknownName`) carry no application.
  - The table becomes single-select, and `ApplicationRow` (`:22`) gains `IsOther`.
  - The page records the selected row's name as `selectedApplication` on `SelectionChanged`, except while it rebuilds the rows. Replacing `ItemsSource` (`:292`) clears the selection and raises `SelectionChanged` first, so the rebuild runs under a `syncing` guard, as the card's does (`ApplicationResourcesCard.cs:114`, `:311-325`). After each rebuild it re-selects that row by name (ordinal).
  - When the row is no longer shown (a new period, a slot without it, or a search that hides it), `selectedApplication` becomes null. The resources card keeps its chart's app while a search hides its row (`:124`); this button acts only on what the table shows.
- **Click:** `session.RequestConnectionsHistory(target.From, target.To, application)`.
- **Tooltip** (with `ToolTipService.ShowOnDisabled` true):
  - "Opens Connections History for this period." or "Opens Connections History for the listed slot.";
  - plus " Only the selected application is shown." when an application is carried;
  - plus " Connections keeps up to 30 days, so earlier minutes are left out." when clipping removes more than one hour;
  - when disabled: "Connections keeps up to 30 days; this period is older."
- **The cap.** The button does not know Connections' 256 MiB cap. When the cap has shortened retention, Connections keeps less and shows "Not kept before …" for the rest, as the user guide's retention paragraph says (`docs/user-guide.md:145`).
- The state is updated on each `Apply`, each slot change and each selection change.

**Copy.**
- **Card note.** The note (`:94`) becomes: "Totals come from observed per-application traffic (ETW) during the period, summed by executable name across its processes. Time without capture is a gap, not zero traffic. The strip stacks the six applications with the most traffic, plus Other, as average rates over each slot's captured minutes; its colours identify applications, not direction. Click a slot, or press Enter on it, to list that slot's applications. Show in Connections opens where the bytes went, for up to the last 30 days."
- **Gap notice.** The notice (`:296`) becomes "{n:N0} recorded minutes had no or incomplete per-application capture; the totals may miss some of their traffic, and the strip leaves them out."
  - The old "their traffic is not counted here" was not true of the table, which sums every stored row (`UsageHistoryStore.cs:198-210`).
- **Caption.** "Traffic by application · {SlotLengthText} slots".

### Navigation

Defined here for *First network use alerts*: the Show in Connections request (from, to, exact app).
- **Request.** `UiSession` (`UiSession.cs:34-36`) gains:
  - `public event Action<ConnectionsHistoryRequest>? ConnectionsHistoryRequested`;
  - `RequestConnectionsHistory(DateTimeOffset from, DateTimeOffset to, string? application)`;
  - `public sealed record ConnectionsHistoryRequest(DateTimeOffset From, DateTimeOffset To, string? Application)`, public because the event on the public `UiSession` (`UiSession.cs:7`) exposes it.
  - `Application` is the bare application name, or null for every application. Callers pass at most 30 days and 1 minute.
- **Handler.** `MainWindow` subscribes and unsubscribes next to `UsageDayRequested` (`Windows.cs:78-79`). Its handler:
  1. returns at once when `lifetime.IsEnding` or `Dispatcher.HasShutdownStarted`, as `RefreshUpdateNotice` does (`Windows.cs:252`; `App.ShowDashboard`, `App.xaml.cs:75`, checks `lifetime.IsEnding` only), so a click during Quit or sign-out does nothing;
  2. shows the dashboard as `App.ShowDashboard` does (`App.xaml.cs:73-77`: `Show()`, un-minimize, `Activate()`), because the request can come from the separate Usage history window (`Windows.cs:358-369`);
  3. calls `Navigate("connections")`;
  4. calls `ShowHistory`.
- The request lives only in memory; nothing is saved.

### Drawing

New file `src/DeadWare.App/TrafficStripChart.cs`. It holds `TrafficStripChart`, a `FrameworkElement` drawn the way `UsageHistoryChart` is, and `TrafficStripView`, a `StackPanel` holding the caption, the chart, the legend and the not-kept line.

- **Size.**
  - Height 130, `MinHeight` 120. Below 160 × 100 nothing is drawn.
  - Plot margins: left 12, right 8, top 10, bottom 24.
  - `GraphDepth.Surface` paints behind the plot.
- **Scale.**
  - Value lines at 0, ½ and the maximum, labelled with `Ui.Bytes(value, true)` via `ChartFrame.Grid` with `nowLabel: null`, as `UsageHistoryChart` passes it (`:135`). No log scale.
  - Maximum = 1.15 × the highest stacked rate of any drawable slot, and at least 1 B/s.
  - Start and end date labels as on `UsageHistoryChart` (`UsageHistoryChart.cs:168-173`).
- **Colours.**
  - `Theme.StripBrush(int index)` is cached like `Theme.CoreBrush` (`Theme.cs:27-39`) and cleared with it in `ApplyGraphColors` (`:56`).
  - Outside high contrast: indexes 0, 1 and 2 are `Graph/download`, `Graph/gpu` and `Graph/cpu`. Indexes 3, 4 and 5 are `ThemeTokens.CoreTint(base, 1, 2, false)` of the same three (`ThemeTokens.cs:23-39`). The user's own graph colours apply.
  - In high contrast, those three keys are all WindowText (`Theme.cs:103`), and user colours are ignored (`Theme.cs:60`). Index i is therefore `CoreTint(Graph/download, i, 6, true)`, six grey levels from lightness 0.35 to 0.75, as `CoreBrush` does for cores.
  - Other is `Muted`, and the legend swatches use the same brushes.
- **Bands.**
  - One frozen `StreamGeometry` per series and fill state (full, partial).
  - Each geometry has one closed, stepped figure (no curves) per run of adjacent drawable slots: a slot whose Start equals the previous slot's End and that has the same state.
  - Fills have no stroke. Fill opacity: 0.85 for full, 0.45 for partial.
  - **Separators**, only when slots are at least 3 px wide: each band except the top one gets a 1 px `Panel` line along its top edge. The line is drawn as one horizontal segment per slot, with no vertical risers, and only where that band is at least 3 px tall.
  - With narrower slots there are no separators. At the 671 px layout, Today's 1,440 slots are under 0.5 px wide, and a stroked outline would paint over the fills.
  - Geometries are rebuilt when the data or the size changes, never on hover.
  - A 1 px `Hairline` baseline runs under every slot that has recorded time.
  - Lines are snapped with a `GuidelineSet`, as in `ChartFrame.Grid` (`:28-34`).
- **Gaps and limits.**
  - Hatching fills the full plot height over the due minutes in [the retained start, due until), except where slots are drawn. The brush is a frozen 6 × 6 DIP `DrawingBrush` with one diagonal 1 px `Hairline` line.
  - Not kept: [origin, min(`RetainedFrom`, `To`)) as a `Hairline` band at 0.35 opacity, labelled "Not kept" when it is at least 60 px wide.
  - Not yet: empty.
- **Hover.**
  - As on `UsageHistoryChart`, listed slots are the hover targets (`ChartFrame.Nearest`), and a `Hairline` line marks the hovered slot. The begun Not-yet span is one more target after them (Slot states).
  - `ChartFrame.Chip` shows at most four lines: the span ("03:13–03:16", with the date as above), "{bytes} · {rate} average" (`Ui.Bytes`), and the status lines from the table.
  - The value line appears only when `RecordedSeconds` > 0. A chip has no tooltip, so it never shows "—".
  - The chip never names an application; the legend does.
- **Legend.**
  - A `WrapPanel` below the chart, in stack order, bottom series first.
  - Entries: a header; one swatch with "{name} · {bytes} · {share}" per application; then "Other ({OtherApplications} more) · …".
  - Each entry is at most 240 px wide and trims with an ellipsis. Its tooltip is "{name} · ↓ {received} · ↑ {sent}".
  - The header and values describe the hovered slot, otherwise the selected slot, otherwise the whole interval or period. The whole-span header reads "Totals for this interval" (Connections) or "Totals for this period" (Usage history).
  - Shares (`0.#` %) appear only for totals. A share's denominator is the applications' totals plus Other, which on Usage history is the table's total.
  - With no application bytes, the legend shows "No application traffic recorded in this interval." (or "… in this period.").
  - For a slot with no recorded time, every value shows "—" with the tooltip "No per-application capture in this slot" (Usage history) or "Under a second recorded in this slot" (Connections).
- **Theme changes.**
  - While loaded, `TrafficStripView` subscribes to `session.Changed`, which theme, glass and graph-colour changes raise (`UiSession.cs:99, 103, 116`). It then invalidates the chart and re-reads the swatch brushes, as `CoolingHealthPage` does (`:102-114`).
  - A Windows high-contrast switch re-applies the theme without `Changed` (`Theme.cs:82-95`). The view therefore also listens to `SystemParameters.StaticPropertyChanged` for `HighContrast` and refreshes at `DispatcherPriority.Background`, after Theme's own `Apply`, which is queued at normal priority (`Theme.cs:94`).
  - It unsubscribes on `Unloaded`.
- **Selection and keys.**
  - `Selectable` works as on `UsageHistoryChart` (`:42-46`): the chart is focusable, and draws an Accent focus frame and an Accent hairline at `MarkTime` when that is set, otherwise at the middle of `SelectedIndex`.
  - `SlotActivated(int index, DateTimeOffset at)` is raised on a click (`at` = the time under the pointer, clamped into the slot) and on Enter (`at` = the slot's Start).
  - `SelectionMoved(int)` is raised on Left/Right with the rules under Keys (the `MarkTime` gap rule applies only when `MarkTime` is set). `HoverChanged` is raised on hover changes.
  - `internal` `SlotAt(Point)`, `HandleClick(Point)` and `HandleKey(Key)` exist for the checks, as on `UsageHistoryChart`.
- **Empty texts**, drawn like "No history in this range yet":
  - Connections: "Reading saved history…", "History unavailable", "Choose a valid interval", "No saved history in this interval", "Not kept" (each as under Connections: page).
  - Usage history: "No history in this range yet" (no listed slot, as on `UsageHistoryChart`, `:174`), "No per-application capture in this range" (every listed slot has `RecordedSeconds` 0), "History unavailable" (a null strip).
- **Accessibility.** The chart's automation name is "Traffic by application". Legend entries are text; on Connections they are buttons named "{name}: show only this application". Keyboard use is under Time cursor and One cursor. Sizes are in DIPs and colours are theme resources only.

### Errors and limits

- **Unknown is never zero.** No value is ever drawn for unrecorded or not-yet-due time. A missing value is null and shows "—" with a tooltip naming its reason.
- **Unreadable is "unavailable".** An unreadable file shows "History unavailable" (strip) or "History unavailable for this slot." (Usage slot list), never an empty period or slot.
- **Data passed to the UI:** at most 1,500 slots × 7 series × 2 longs (about 168 KB).
- **Overflow:** Connections sums saturate at `long.MaxValue`, as `sat_sum` does (`ConnectionHistoryStore.cs:474`), and every C# fold saturates, never wraps. Usage history's SQL sums are SQLite `SUM`, as in `ReadApplications` (`UsageHistoryStore.cs:199,206`), which fails the read on overflow instead of wrapping; it cannot happen below 2^63 bytes.
- **Bad arguments:** `ConnectionHistoryQuery.Application` over 260 characters throws the store's `ArgumentException` ("Connection history contains an invalid or oversized value."). `ReadApplicationSeries` with `maxPoints` 0 or 4,001 throws `ArgumentOutOfRangeException`.
- **Failures:**
  - A Usage history read failure goes through `Fail`, so the status names only the exception type.
  - A Connections failure shows "History unavailable" and the existing typed actionStatus message. A file over its cap fails the read (`ConnectionHistoryStore.cs:470`) the same way.
  - The strip's statements run under the TEMP limit of *Per-minute merge, the grouped read and the Unlocated fix*. SQLITE_FULL from them throws `ConnectionHistoryLimitException` like the rest of `Read`, no partial view is returned, and the page shows that section's limit text and "History unavailable".
- **Threading:** all reads and folding run off the UI thread, inside the services' `Task.Run`. Only geometry building runs on the UI thread.
- **Quit and sign-out:**
  - A strip read is a whole-interval `Read`, which holds the store's lock (`ConnectionHistoryStore.cs:336`) for its whole run, about 2–2.5 s on a full per-minute file.
  - At Quit it delays the final write by at most that one read, inside the 20 s history deadline (`UiSession.cs:300`).
  - At sign-out (3 s) a read in flight can make the final write miss its limit, as *Per-minute merge, the grouped read and the Unlocated fix* states for every History read. With the cursor off the strip adds no read; with it on it adds one whole-interval read per context change.
  - `Unloaded` and hiding the page drop any strip read not yet started.
  - Usage history reads use their own read-only connections and never block its writer.
- **Interruptions:** a strip read dropped by hiding or unloading the page is made again when the page is shown, so nothing stays stale.
- **Unchanged:** every store's caps, retention, journal limits and write path.
- **No new requirements:** nothing needs administrator rights beyond per-application capture's existing need, and there is no new network traffic.

### Privacy

- Nothing new is stored, and `--diagnose` gains no fields.
- Application names appear only in the legend, its tooltips and the application filter button, never in status text. Status text holds fixed copy, counts and exception type names.
- The Show in Connections request lives in memory.
- CSV export changes only in that it honours the application filter.

### Tests

**Core (Linux and CI, every run), new `tests/DeadWare.Tests/TrafficStripTests.cs`:**
- `Connections_bucket_minutes_keep_at_most_1500_whole_minute_slots`:
  - 5 min → 1, 1 day → 1 (1,440 slots), 25 hours → 1 (1,500), 2 days → 2, 7 days → 10 (1,008), 30 days → 30 (1,440), 31 days → 30 (1,488), and 40 days → 39.
  - A From 20 s past a minute starts the grid at the next minute.
  - The last slot ends at To.
- `Is_full_counts_the_whole_minutes_that_start_in_the_slot`:
  - a 1-minute slot needs 60 s; a 10-minute slot needs 570 s;
  - a 6.72-minute slot with 6 minute starts is full with 6 captured minutes and partial with 5; one with 7 starts needs 7;
  - an `Incomplete` slot is never full.
- `Due_until_keeps_unsaved_minutes_out_of_the_gaps`: with a 16 s save delay, the 09:59 minute is not due at 10:00:10 and is due at 10:00:16; due until never passes To.
- `Rate_divides_measure_bytes_by_recorded_seconds_and_is_null_without_recorded_time`: covers Total, Received and Sent, the Other index, and saturation.
- `Slot_at_finds_the_listed_slot_holding_a_time_and_null_in_gaps`.
- `Connections_target_uses_whole_minutes_and_clips_to_thirty_days`:
  - a target inside 30 days gives its whole minutes;
  - a partly older one is clipped to the first whole minute ≥ now − 30 days;
  - a wholly older one gives null;
  - a 2.4-minute slot gives the 2 or 3 whole minutes that start in it.
- `Slot_length_text_uses_minutes_under_an_hour`: 60,000 → "1-minute", 192,000 → "3.2-minute", 1,800,000 → "30-minute", 5,760,000 → "1.6-hour" (invariant culture).
- `Application_key_normalizes_then_upper_cases_and_is_idempotent` (`ProcessGrouping.Key`):
  - " chrome.exe", "Chrome.EXE" and "chr\u0001ome.exe" share one key;
  - "" and null give "UNKNOWN APPLICATION";
  - a 300-character name without white space is cut to 256 characters before upper-casing;
  - Key(Key(x)) == Key(x) for each of these names.

**Core, `ConnectionHistoryStoreTests`:**
- `Strip_ranks_six_applications_by_the_direction_measure_and_folds_the_rest_into_other`:
  - eight applications give the expected order for Both, Received and Sent, with ties broken by key;
  - Other holds the rest, and the application totals plus Other equal the summary totals;
  - an application whose measure is 0 never ranks.
- `Strip_groups_names_by_application_key_and_shows_the_ordinal_smallest_name`: "chrome.exe" and "Chrome.exe" form one series named "Chrome.exe"; "PID 4" is its own series.
- `Strip_slots_match_a_reference_fold_of_every_read_row`: on a random three-day fixture, with every page read through Offset, the strip equals a C# fold of the rows. Run with no filter and with each of the search, country, Direction and application filters.
- `Strip_recorded_seconds_come_from_coverage_and_rows_without_coverage_are_incomplete`:
  - 30 s of coverage → 30 s; a fully covered minute → 60 s; overlapping pieces are capped at 60 s;
  - an incomplete piece → `Incomplete`;
  - traffic without coverage → 60 s and `Incomplete`; under a filter that excludes that traffic, the minute is absent;
  - coverage without traffic → a listed zero slot;
  - 0.7 s of coverage → a listed slot with `RecordedSeconds` 0;
  - neither → no slot.
- `Strip_reports_retained_from_after_expiry_and_capacity_eviction`: expiry only; a floor newer than the expiry (`CapacityShortened`); a floor older than the expiry (not `CapacityShortened`); an interval after both (null); an interval wholly before both (`RetainedFrom` at or after To, no slots).
- `Strip_leaves_out_the_minute_that_holds_the_expiry_instant`: with expiry at 10:00:30, coverage over 10:00–10:01 and traffic at 10:00, no slot is listed at 10:00, and `RetainedFrom` is 10:01. With expiry at exactly 10:00:00, full coverage and traffic at 10:00 give a full listed 10:00 slot and `RetainedFrom` 10:00.
- `Capacity_shortened_status_matches_the_strip`: a floor newer than the expiry puts "Capacity shortened retention." in the status; a floor older than the expiry does not. Existing tests that pin the status with floor > 0 are updated.
- `Application_filter_matches_the_key_exactly_everywhere`:
  - "chrome" does not match "chrome.exe"; "CHROME.EXE" matches "chrome.exe" and "Chrome.exe" in the rows, the total, the summary, the markers, the strip and the CSV;
  - "" and "   " match every application; 261 characters are rejected;
  - an existing version 1 file is read without being rewritten, as `History_pid_search_matches_existing_schema_v1_rows_without_rewriting_them` (:147) checks.
- `Strip_is_the_same_for_per_sample_and_merged_minute_rows`: the same traffic, saved per sample and saved merged per minute, gives equal strips.

**Core, `UsageHistoryStoreTests`:**
- `Application_series_uses_the_metric_bucket_grid`: for Day, Week and Month at 60, 450 and 1,500 points, and with 4,000 points, `BucketMilliseconds` equals `ReadMetric`'s, and every slot that holds a CPU minute has that CPU bucket's Start and End.
- `Application_series_ranks_like_the_applications_read`: nine names, with ties. `Applications` equals `ReadApplications(...).Applications.Take(6)` in order, with equal totals; Other's totals and count are the rest.
- `Application_series_keeps_capture_gap_minutes_out_of_slot_bytes`:
  - a capture-gap minute's rows count in the totals but not in the slot bytes;
  - that slot is `Incomplete`, with `RecordedSeconds` = 60 × its captured minutes;
  - a slot made only of capture-gap minutes is listed with `RecordedSeconds` 0.
- `Application_series_lists_recorded_minutes_without_traffic_as_zero_slots`: minutes with only metric rows give a listed zero slot; an unrecorded slot is absent.
- `Application_series_rejects_bad_arguments`: `maxPoints` 0 and 4,001; a reversed range and a range over 401 days; a bad machine key.

**App/UI checks (Windows CI), new `TrafficStripUiChecks`,** run inside the existing STA fact, in the UI test's phase list beside `UsageHistoryUiChecks` (`BatteryUiTests.cs:85`):
- `CheckStackOrderAndColours`:
  - six applications plus Other draw seven fills, bottom series first;
  - the fills equal `Theme.StripBrush(0..5)` and `Muted` in all nine themes;
  - outside high contrast, every pair of the six is at least ΔE 20 (CIE76) apart, and every fill is at least ΔE 30 from `Panel`;
  - in high contrast, the six fills are six different greys;
  - a user colour for download changes series 0 and 3.
- `CheckGapsZerosAndPartials`, on a 36-slot fixture with a missing slot, a zero slot, a partial slot, an incomplete slot and a no-capture slot:
  - hatching covers only the missing and no-capture slots, and never the not-yet-due minutes at the end;
  - hovering the begun not-yet minutes shows "Not saved yet" and "Minutes are saved after they end"; time after now has no hover target;
  - the zero slot shows its baseline;
  - fill opacity is 0.45 for partial slots and 0.85 for full ones; a fully recorded fractional slot is full;
  - runs split at the missing and partial slots (figure counts per geometry).
- `CheckBandsAndSeparators`:
  - fills have no pen;
  - with 36 slots in a 600 px plot, each band except the top one has horizontal separator segments only;
  - with 1,440 slots in a 600 px plot there are none.
- `CheckChipAndLegend`:
  - the hover chip's lines for each state; no value line for a no-capture slot or a Connections slot under one second;
  - the legend's totals and shares with nothing hovered, and slot values on hover and on selection;
  - "—" with its tooltip for a no-capture slot and for a Connections slot under one second;
  - "Other (9 more)" for 15 applications, and Other hidden when it is 0;
  - the no-traffic line;
  - a 256-character name trimmed, with the full name in the tooltip.
- `CheckKeysAndClicks`:
  - Left, Right and Enter as on `UsageHistoryChart` when `MarkTime` is unset;
  - with `MarkTime` in a gap, Left and Right go to the neighbouring listed slots;
  - a click's time lies inside its slot; a click on a gap or on the not-yet target raises nothing;
  - the focus frame.
- `CheckNotKeptAndEmptyTexts`: the band and its "Not kept" label; a wholly not-kept interval fills the plot; the caption with and without "Capacity shortened retention"; every empty text.
- `CheckHoverDoesNotRebuildGeometry`: with 1,500 slots, 100 hover positions keep the series geometries' reference identity; a resize rebuilds them.
- `CheckThemeRefresh`: after `session.Changed` for a graph-colour change, swatches 0 and 3 show the new colours; a high-contrast change refreshes every swatch.

**App/UI checks, `UsageHistoryUiChecks`:**
- **Updated `Run`.**
  - The share bar assertion (`UsageHistoryUiChecks.cs:92-93`) becomes a legend check: seven entries for the 15-app fixture, colours from `StripBrush`.
  - The gap notice assertion (`:84`) takes the new text.
- **Updated `CheckChart`** (`:107-116`): its 5-minute buckets start on the minute, so its `IsFull` and `CanConnect` assertions stand unchanged.
- `CheckFractionalBucketsConnect`: on `ReadMetric`'s grid, a fully recorded 6.72-minute bucket with 6 minute starts is full and connects to a full neighbour; with 5 recorded minutes it is partial and breaks the line; a bucket with 7 starts needs 7 minutes.
- `CheckStripListsSlot`:
  - a click and Enter both list the slot in the table, with the heading "Applications · …", **Clear** showing, and rows from a slot fixture;
  - a null slot result shows "History unavailable for this slot." with no rows;
  - focus stays on the strip, and the table is brought into view;
  - the Moments charts' hairlines mark the slot by time;
  - Application resources lists nothing, and a slot it already listed stays listed.
- `CheckStripSharesTheCursor`: Left on the strip moves the Moments charts' hairlines by time and lists nothing; Left on the CPU chart moves the strip's hairline; a refresh keeps the hairline at its time.
- `CheckStripClearPaths`: Esc, the Applications **Clear**, the card's **Clear**, a range change, ◀ Earlier and `ShowDay` each clear both narrowings and every hairline; the Applications **Clear** hands focus to the search box.
- `CheckStripReadsOnItsOwn`:
  - the Device charts and the table apply while the strip read is still pending;
  - a stale strip result is dropped;
  - a null result shows "History unavailable";
  - the right edge of a Day period at offset 0 is not hatched;
  - each refresh reads the strip again.
- `CheckShowInConnections`:
  - the button raises `ConnectionsHistoryRequested` with whole minutes, for the period and for an Applications slot;
  - it carries the selected row's bare name, and no application for "Other applications" or "Unknown application";
  - the selection survives a refresh that replaces `ItemsSource`, and a search that hides the row clears it;
  - for a period older than 30 days it is disabled, with its tooltip; a Month period gets the clipping tooltip.

**App/UI checks, `ConnectionsPageUiChecks`:**
- **Updated `CheckHistoryQueries`** (:156-184): in playback, the first minute read is followed by one whole-interval strip read before the next minute.
- **Updated `CheckWholePageLayout` and `CheckThemes`** (:185-196, :285-): History shows the strip in body row 2, above the node list; the map keeps at least 300 px at 671 × 440 and 1100 × 700; Live hides the strip.
- `CheckStripCursor`:
  - a click turns the cursor on at the minute under the pointer without resetting it;
  - Left and Right go to the first minutes of adjacent listed slots, and from a gap to the neighbours;
  - the hairline follows the slider and playback;
  - `cursorTime` keeps the cursor minute while a strip read is queued;
  - after an unapplied edit to the from/to boxes, clicks do nothing.
- `CheckStripReads`:
  - with the minute cursor off, one read per context;
  - with it on, cursor moves read only minutes, and a context change reads the minute and then the whole interval, applying only its strip;
  - ten cursor moves during the strip read neither drop nor restart it, and the last minute is read right after it;
  - a strip reply for an old context is dropped;
  - a failure shows "History unavailable" and is not read again for the same context;
  - showing the page after its strip's "now" re-reads the strip;
  - `ReloadHistory` with the cursor on reads the minute and the whole interval again.
- `CheckApplicationFilter`:
  - a legend click sets `Application` to the key; the button's text and tooltip; clearing the filter;
  - the Live list is filtered by key;
  - `SameHistoryContext` compares the field, and the export query carries it.
- `CheckShowHistory`:
  - from Live and from History: the mode, the boxes, the cleared filters, the cursor off, the application filter set from a bare name, and `range` untouched;
  - an interval wholly before `RetainedFrom` shows the "Not kept" band, the not-kept line and the storage line's earliest time;
  - with a test time zone that changes for DST, passed to the parse helper, an ambiguous local start widens earlier and an invalid local end widens later.
- **Updated `CheckNavigation`** (:333): `RequestConnectionsHistory` on a minimized dashboard restores it, shows Connections and calls `ShowHistory`; while the lifetime is ending it does nothing.

**Measurements (Real Windows CI):** recorded through `MeasurementRecorder` as floors; they decide nothing.
- Defined here for *Per-minute merge, the grouped read and the Unlocated fix*: `Connection_history_rates_and_read_timings` also times, on its full per-minute file, the 30-day `Read`, strip included, with the application filter empty and set to the busiest app (p50 and p95 of 5 runs). This is the strip's part of the release's History read measurement on a full per-minute file, and needs no second file.
- `Usage_application_series_timing_for_a_month`: 30 days with 15 applications per minute, read with `ReadApplicationSeries` at 450 and 1,500 points.

**Manual checks** (user's PC; recorded as pending in `docs/validation.md`, never claimed as verified):
- The strip is readable in all nine themes and in high contrast, including Mage's and Skyrim's closest pairs.
- The bottom band agrees with the top row of the Applications table.
- A large download shows up as one tall band.
- The current minute at the right edge of Today is empty, not hatched, and its hover says "Not saved yet".
- Show in Connections from the separate Usage history window brings the dashboard forward on Connections, with the slot and the application.
- On a file whose cap has shortened retention, a 30-day Connections view shows "Not kept before … · Capacity shortened retention".
- Week and Month line charts connect across fully recorded fractional buckets.

### Docs and release

- **User guide, Connections:** the History paragraph (`docs/user-guide.md:137`) describes the strip, its cursor and the application filter; the retention paragraph (:145) gains "Not kept before …" and the narrower "Capacity shortened retention".
- **User guide, Usage history:** the Applications paragraph (:169) replaces the share bar with the strip and adds Show in Connections; the click-a-moment paragraph (:173, as 0.15.0 leaves it) adds that the strip shares the cursor and that a click lists its slot in the Applications table. The guide says that strip colours identify applications, not direction.
- **CHANGELOG and `docs/releases/v0.16.0.md`:** the strip on both pages, Show in Connections and the application filter; the reworded Usage gap notice; the narrower "Capacity shortened retention" status; and that Usage history line charts now connect fully recorded slots that hold one minute start fewer than their length.
- **`docs/validation.md`:** the strip's timings and `Usage_application_series_timing_for_a_month` as floors, and the manual checks as pending.
- **README:** at most the release's one-line mention, shared with the other 0.16.0 sections.
- **Release:** 0.16.0 is one PR from main after 0.15.0 has merged.

### Out of scope

- A strip in Live mode.
- Restacking one application by country or destination.
- A 100 %-stacked or log-scale view.
- Separate download and upload bands.
- Exporting the strip as CSV.
- The strip on the widget or the Network page.
- Highlighting a Destinations row in the strip (a selected row highlights map nodes only).
- Any change to the Application resources card's ranking, or any read of `process-history.db`.

### Depends on

- ***Per-minute merge, the grouped read and the Unlocated fix* (0.16.0), grouped read:** `temp.grouped` with `minute`, `name`, `received`, `sent` and `complete`, built once per `Read` (minute reads included) in the read snapshot, under its TEMP limit and limit text.
- ***Per-minute merge, the grouped read and the Unlocated fix* (0.16.0), per-minute merge:** each closed minute saved within the flush interval plus one polling interval of its end (its close comes 2 s plus one to two polling intervals after the minute and is written at the end of that writer step), coverage runs that stay inside one minute, and one row per stored name per minute keeping the AND of `complete`.
- ***Per-minute merge, the grouped read and the Unlocated fix* (0.16.0), status:** it keeps `Read`'s floor and status statements (`:380-383`) and their copy, and this section narrows only the `:381` condition for "Capacity shortened retention.".
- ***The Destinations table* (0.16.0):** the body-row list (strip in row 2, node list in row 3), `MeasureOverride` measuring `trafficCard`, the `app_key` registration with `ProcessGrouping.Key` beside `sat_sum` in `Open`, and the expander sentence this section's sentence follows.
- ***First network use alerts* (0.17.0):** its View in Connections sends `RequestConnectionsHistory` with at most 30 days and the episode's bare application name, and relies on the "no longer kept" rules above.
- ***Don't record these apps and Clear history…* (0.17.0), Don't record these apps:** an excluded app's minutes keep their coverage (Connections) and metric minutes (Usage history), so with the application filter set to an excluded app the strip would draw known zeros over recorded time; that section marks such a filter "not recorded" and draws no zero for it.
- ***Don't record these apps and Clear history…* (0.17.0), Clear history…:** a one-app clear leaves coverage and metric minutes, so cleared minutes would read as known zeros, and an all-apps clear's raised `expiry` (either range) would read as "Not kept before …". That section draws and words cleared time from its persistent `cleared-all` and `cleared-one` `metadata` rows and `usage-history.cleared.json` (`RetainedByClear`, `AppClearedBefore`, the Cleared band and the Not saved state), never from the clear-in-progress marker, with its own copy.
- ***Don't record these apps and Clear history…* (0.17.0), Clear history…:** when a store's clear finishes it calls `ConnectionsPage.ReloadHistory()` and refreshes Usage history if open.
- ***Don't record these apps and Clear history…* (0.17.0), One app key:** if it registers `app_key` with `AppKey.Of` instead of `ProcessGrouping.Key`, the strip's C# key (folds, `Normalize`, `Matches`, `ShowHistory`) switches to the same function.


## 0.16.0 — The Destinations table

A full-width card on the Connections page that lists destinations in Live and in History. It sits below the map, the traffic strip, the node list and the Export CSV footer. Selecting a row lights up its map nodes; an All / Internet / Local choice narrows the list.

- **Anchors** are `origin/main` at 181eb54 (0.14.1). `App/` is `src/DeadWare.App/`, `Core/` is `src/DeadWare.Core/`, `Tests/` is `tests/DeadWare.Tests/`, `AppTests/` is `tests/DeadWare.App.Tests/`, and UG is `docs/user-guide.md`.
- **0.15.0 merges first.** It changes `App/ProcessesPage.cs` (GPU columns), `App/ApplicationResourcesCard.cs` (GPU rank and columns), `Core/ProcessTable.cs` (`ProcessCopy`, `ProcessAppRow`), `UserSettings` in `Core/Monitoring.cs` (`ProcessColumnsVersion`, per-app GPU spec :83) and `docs/user-guide.md`. Doc anchors below therefore quote their text as well as the line. Recheck those anchors, among them `Core/ProcessTable.cs:50` and `:57`, and the `App/UiSession.cs` ones, after 0.15.0 merges.
- **Stores.** The table reads the live view and `connections.db` only. It never reads or writes `usage-history.db` or `process-history.db`, so 0.15.0's version 2 schema (GPU columns, up to 40 listed apps plus Everything else a minute, the 192 MiB cap) is untouched. The Application resources card is only the precedent for rows that update in place; 0.15.0's GPU columns there do not change the rules borrowed from it.
- **No schema change** in any store. The table adds one setting, the scope.
- **Shared rules.** Null, never zero, for an unknown value; every "—" has a tooltip naming its reason; an unreadable file shows "unavailable", never an empty period; status text holds fixed copy, counts and exception type names, never app names, addresses or paths; storage and file work runs off the UI thread; colours come from theme resources only.
- **Other sections are named by title:** *Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0, below *Connections storage*), *The traffic strip on Connections History and Usage history* (0.16.0, below *The traffic strip*), *First network use alerts* (0.17.0) and *Don't record these apps and Clear history…* (0.17.0). What each must supply is under Depends on.

### What it is

- It lists every destination the page's filters allow, in Live and in History.
- That includes the local, special-purpose and unlocated destinations the map cannot draw. Today these appear only as counts (`App/ConnectionsPage.cs:239-240`).
- An All / Internet / Local choice narrows the table only.
- Selecting a row highlights its nodes on the map.
- **The node list stays in place.** The compact node list (`App/ConnectionsPage.cs:54`, `:275-313`) stays under the map as today (`:97`, `:102`), now directly below the traffic strip, which sits directly under the map card. It still follows the nodes visible on the map.

### Rows

- **One row per remote address.**
  - The key is the normalized address text: `ConnectionPeer.RemoteAddress` in Live and `traffic.remote` in History.
  - The ledger normalizes the Live text (`Core/ConnectionLedger.cs:64`) and `Prepare` normalizes the History text (`Core/ConnectionHistoryStore.cs:107`). Both use `NetworkAddress.Normalize` (`Core/NetworkAddress.cs:8`), so an IPv4-mapped IPv6 address lands in its IPv4 row.
- **A row adds every app, process, local address and port, remote port and protocol that reached the address under the page's current filters.**
  - The filters are search, country and direction (`App/ConnectionsPage.cs:315` in Live; `Grouped`, `Core/ConnectionHistoryStore.cs:24-35`, in History), plus the application filter of *The traffic strip* (set from its legend, Show in Connections or View in Connections), which `Matches` applies in Live and `ConnectionHistoryQuery.Application` in History.
  - So with a search for one app, a row's numbers are that app's traffic to the address. This is how to read per-app numbers for one destination; there are no per-app child rows.
- **The counts equal the summary's.** Defined here for *Connections storage*: History rows use the stored `kind` (0 Public, 1 Local, 2 Special) and its `located` rule, so the Internet, Local and Unlocated figures agree with the summary.
  - All equals the summary's destination count (`Summary.Destinations` in History): both count distinct remote addresses (Live `App/ConnectionsPage.cs:240`; History today `Core/ConnectionHistoryStore.cs:345`, in 0.16.0 *Connections storage*'s summary step).
  - Local equals the summary's local count (`Summary.Local`; kind Local, `kind` 1).
  - Unlocated equals the summary's unlocated count (`Summary.Unlocated`). Live: a Public address with no valid point (`App/ConnectionsPage.cs:239`). History: a Public address any of whose grouped rows has `located` 0 (`kind=0 AND located=0`), with this release's Unlocated fix.
  - Internet + Local + Special = All.
- **Where rows come from:**
  - **Live:** the shown view (`ConnectionService.Live`, `App/ConnectionService.cs:86-97`). That is the peers active within the last 60 s (`Core/ConnectionLedger.cs:103-104`), at most 8,192 (`:31`).
  - **History:** `temp.grouped` and `temp.geo` of *Connections storage*, which `Read` builds once, read in the same snapshot as the summary and markers. Defined here for *Connections storage*: **the table follows the minute cursor.** With the cursor on, `Read` builds those tables for the cursor's minute, so the table shows that minute and changes as the cursor moves or playback steps.
  - History holds saved minutes only. With per-minute saving, a minute appears a few seconds after it ends (*Connections storage*).

### Class

`NetworkAddress.Describe(IPAddress)` (new, Core) returns `NetworkAddressClass(NetworkAddressKind Kind, string Label)`.

- It normalizes first, as `Classify` does.
- Its `Kind` always equals `Classify`'s (`Core/NetworkAddress.cs:10-41`); the label refines it.
- The first matching row of this table wins:

| Addresses | Label | Kind |
|---|---|---|
| 127.0.0.0/8, ::1 | Local · loopback | Local |
| 169.254.0.0/16, fe80::/10 | Local · link-local | Local |
| 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, fc00::/7 | Local · private | Local |
| 0.0.0.0, :: | Special · unspecified | Special |
| the rest of 0.0.0.0/8 | Special · this network | Special |
| 100.64.0.0/10 | Special · shared (CGNAT) | Special |
| 224.0.0.0/4, ff00::/8 | Special · multicast | Special |
| 255.255.255.255 | Special · broadcast | Special |
| 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32, 3fff::/20 | Special · documentation | Special |
| 198.18.0.0/15 | Special · benchmarking | Special |
| 192.0.0.0/24, 2001::/23 | Special · IETF protocol | Special |
| 192.88.99.0/24, 2002::/16 | Special · 6to4 | Special |
| 192.31.196.0/24, 192.52.193.0/24, 192.175.48.0/24, the rest of 240.0.0.0/4, 2620:4f:8000::/48, any other IPv6 outside 2000::/3 | Special · reserved | Special |
| everything else | Internet | Public |

- **Unparseable text** is "Special · unrecognized" (Special), matching the page's `Kind` (`App/ConnectionsPage.cs:321`). The ledger (`Core/ConnectionLedger.cs:57`) and the store (`Core/ConnectionHistoryStore.cs:102`) reject such text, so only fixtures reach it.
- **History** takes the kind from the stored `kind` column (`Core/ConnectionHistoryStore.cs:112`) and the label from `Describe`. Both come from `Classify`, which has not changed since the column was added (`Core/NetworkAddress.cs` has one commit), so they agree; `Describe_labels_every_classified_range` pins that.
- **The CLASS cell** shows the label. Its tooltip:
  - Internet: "Public Internet address."
  - Local: "Local address: counted under Local."
  - Special: "Special-purpose address: listed under All only and counted as neither local nor unlocated."

### Columns

Live: **DESTINATION · CLASS · LOCATION · APPS · PORTS · ↓ DOWNLOAD · ↑ UPLOAD · RECEIVED · SENT · CONNECTIONS**

History: **DESTINATION · CLASS · LOCATION · APPS · PORTS · RECEIVED · SENT · MINUTES · FIRST SEEN · LAST SEEN**

Layout rules:

- Headers are upper-case.
- Every column is star-sized with a minimum width:

| Column | Star | Listed minimum |
|---|---|---|
| DESTINATION | 1.5 | 120 px |
| CLASS | 1.1 | 96 px |
| LOCATION | 1.6 | 120 px |
| APPS | 1.3 | 100 px |
| PORTS | 1 | 80 px |
| each number column | 0.8 | 72 px |
| FIRST SEEN, LAST SEEN | 1.1 | 110 px |

- **Each column's MinWidth is the largest of:**
  - its listed minimum;
  - its header's measured text width plus the header's 18 px side padding (`App/App.xaml:248-249`);
  - for FIRST SEEN and LAST SEEN, the measured width of 2026-12-28 23:59 in the current culture's "g" format plus the cell's 14 px padding (`App/App.xaml:260`).

  So no header and no time cell is ever trimmed.
- With the listed minimums, the columns add up to 876 px in Live and 952 px in History. A narrower table (as at the 900 px minimum window, `App/Windows.cs:53`) scrolls sideways inside the grid; the page never scrolls sideways (`App/ConnectionsPage.cs:41`).
- Number cells use the Processes value style (tabular numerals, ellipsis; `App/ProcessesPage.cs:493-500`). The style moves to `Ui` so both pages share it.

Cell rules:

- **DESTINATION:** the address. IPv6 has no brackets, since no port follows.
  - The tooltip is the address.
  - Live adds "Observed since {min FirstSeen, local time, format g}.", and "Capture is incomplete; totals may be low." when the view is not `Complete`.
  - History adds "Some minutes were captured incompletely; totals may be low." when the row is incomplete.
- **LOCATION:**
  - **Internet with a location:** City, Region and Country joined by ", ", skipping empty parts (as `App/ConnectionsPage.cs:322`). Its tooltip:
    - when the row has no valid points: "Country or region only: not drawn on the map; counted as unlocated.";
    - when it has points and `Unlocated` is true (History only: some of its saved minutes have no map position): "Some saved minutes have no map position; the address also counts as unlocated.";
    - otherwise no tooltip.
  - **Internet without a location:** "—" with a tooltip.
    - Live, when there is no connection service or its `DatabaseDate` is null (through the page's `hasLocationDatabase` hook, Core model): "No location database. Use Download / update locations at the bottom of the page."
    - Live otherwise: "Not in the location database, or its lookup has not finished."
    - History: "No location was saved with this address."
  - **Local:** "—", "Local addresses are not located."
  - **Special:** "—", "Special-purpose addresses are not located."
    - This holds even when a fixture gives the address a location.
    - Real Local and Special peers are never looked up (`App/ConnectionService.cs:151`).
  - **History location:** the address's latest non-null saved location. Latest means its maximum minute; ties go to the lower location text in BINARY order.
    - A later database update never relocates old minutes (UG:143, "A later location-database update does not relocate old records."), so one address can have several saved locations in an interval.
    - The row shows the latest one and highlights all of them, up to 8 (Selection and map highlight).
- **APPS:** the busiest app's name, plus " +{n:N0}" for the others.
  - Apps are grouped by `ProcessGrouping.Key` (`Core/ProcessTable.cs:50`).
  - The shown name is `UsageMetrics.NormalizeName` of a saved or live name (`Core/UsageHistory.cs:33-38`), as `ProcessGrouping.Group` shows it (`Core/ProcessTable.cs:57`). Control characters never reach a cell, the copy or the CSV.
  - Busiest means received + sent (saturating) descending, ties by key, ordinal.
  - The casing shown is that of the app's first connection in the live view's order (Live), or of its latest saved minute, ties lower BINARY (History).
  - The tooltip has one line per app, up to 10: "{name} · ↓ {received} · ↑ {sent}". A last line "and {n:N0} more" follows when there are more than 10.
- **PORTS:** the busiest remote port as "{port} {protocol}", with the protocol spelled as in the detail box (`App/ConnectionsPage.cs:317`, e.g. "443 Tcp"), plus " +{n:N0}". The tooltip works like APPS, up to 10 ports.
- **↓ DOWNLOAD, ↑ UPLOAD (Live):** the sum of the address's peers' current rates, through `Ui.Bytes(rate, true)` (`App/Charts.cs:39-45`).
  - When the view is not `Available` (stale or no capture), the cell is "—" with "Endpoint capture is stale or unavailable; no current rate." This is today's rule (`App/ConnectionsPage.cs:237`).
  - The ledger reports 0 B/s on the first sample after a capture break (`Core/ConnectionLedger.cs:106-109`). The table shows that as the map and the detail box do today, for that one poll (Out of scope).
- **RECEIVED, SENT:** byte sums saturating at `long.MaxValue`, through `Ui.Bytes`.
  - Live counts since each peer was first observed, as the summary says (`:240`). Header tooltip: "Since each flow was first observed."
  - History counts the shown interval or minute. Header tooltip: "Saved in the shown interval."
- **CONNECTIONS (Live):** the number of live peers to the address, N0. These are observed flows active within the last 60 s, not open sockets. Header tooltip: "Observed flows to this address: one per app process, local address and port, remote port and protocol. A flow idle for 60 s drops out."
- **MINUTES (History):** distinct saved minutes with traffic to the address, N0. Header tooltip: "Saved minutes with traffic to this address in the shown interval."
- **FIRST SEEN, LAST SEEN (History):** minimum first_seen and maximum last_seen, in local time, format "g" (as `:248`).
- **Known zeros:** 0 bytes shows "0.0 B" with no tooltip. Apart from the ledger's first-sample rates above, nothing unknown ever shows 0.

### Scope

- **The control:** a **Show** combo box (Width 120, automation name "Destinations shown") with All, Internet and Local.
  - All lists every row.
  - Internet lists kind Public.
  - Local lists kind Local.
  - Special rows appear under All only.
- **Saving:** the choice is saved as `UserSettings.ConnectionsDestinationScope` (string, initializer "All"; `Core/Monitoring.cs:374-413`).
  - `SettingsCodec.Read` (`:417`) turns any other value (compared ordinally), null or a missing property into "All".
  - `UiSession.SetConnectionsDestinationScope(string)` sits beside `SetConnectionsAnimation` (`App/UiSession.cs:250`). It normalizes, raises Changed and saves.
  - `RefreshSession` (`App/ConnectionsPage.cs:165-174`) sets the combo from the setting under `syncing`, as it does for Record history.
  - 0.15 drops the property. Coming back to 0.16 reads "All".
- **Reach:** the scope changes only the table and Export destinations. The map, the node list, the summary, the strip and the existing Export CSV keep every destination the filters allow.

### Caps

- **Constants (Core):**
  - `ConnectionDestinations.MaximumRowsPerScope` = 1,000;
  - `MaximumApps` = 10, `MaximumPorts` = 10, `MaximumPoints` = 8;
  - for the CSV, `ExportMaximumApps` = 100 and `ExportMaximumPorts` = 1,000.
- **Rank for the cap:** received + sent (as double) descending, ties by address text, ordinal. Addresses are ASCII, so ordinal and SQLite BINARY agree.
- **Per kind:** each kind keeps its own top 1,000. All is the top 1,000 of their union, which equals the top 1,000 overall. So Local never loses LAN rows to busy Internet ones.
- **Counts:** the counts per kind and the unlocated count are exact and uncapped: `ConnectionDestinationCounts(long All, long Internet, long Local, long Special, long Unlocated)`.

### Count line and notes

- **Count line**, above the grid (Muted 11): "{all:N0} destinations: {internet:N0} Internet ({unlocated:N0} unlocated), {local:N0} local, {special:N0} special-purpose."
  - "1 destination" is singular.
  - It shows every count whatever the scope.
- **When the scope's count is over 1,000**, the line continues:
  - History: " Showing the top 1,000 by traffic (received + sent); search or narrow the interval to see the rest."
  - Live: " Showing the top 1,000 by traffic (received + sent); search to see the rest."
- **Footnote** under the grid (Muted 11, wrapping): "A destination is one remote address; its numbers add every app and port that reached it under the current filters. Special-purpose addresses (shared CGNAT, multicast, broadcast, documentation and other reserved ranges) are listed under All only and count as neither local nor unlocated. Locations are approximate."
  - Live adds " Live rows cover peers active in the last 60 s."
  - History adds " History lists saved minutes; a minute appears a few seconds after it ends."

### Sorting

- **Default:** RECEIVED descending, in both modes. The sort is not saved.
- **Header clicks:**
  - Number and time columns sort descending on the first click.
  - DESTINATION, CLASS, LOCATION, APPS and PORTS sort ascending on the first click.
  - Clicking the sorted column flips it.
  - Clicks are handled in `DataGrid.Sorting` with `e.Handled`, as on Processes (`App/ProcessesPage.cs:258-265`).
- **Sort keys:**
  - DESTINATION: IPv4 before IPv6, then address bytes; unparseable text last.
  - CLASS: Internet, then Local, then Special, then the label, ordinal.
  - LOCATION: Country, then Region, then City, OrdinalIgnoreCase.
  - APPS: the busiest app's name (OrdinalIgnoreCase), then app count descending.
  - PORTS: the busiest port's number, then Tcp before Udp.
  - Every other column: its value.
- **"—" and null** sort last in both directions.
- **Ties** sort by address text, ordinal.
- **Mode change:** the sort stays when its column exists in the new mode. Otherwise it returns to RECEIVED descending: for ↓, ↑ and CONNECTIONS in History, and for MINUTES, FIRST SEEN and LAST SEEN in Live.
- **Where the rules live:** `ConnectionDestinationSort.Compare(a, b, key, descending)` in Core.

### Updates in place

Updates follow the Application resources card (`App/ApplicationResourcesCard.cs:25-50`, `:314-325`) and the Processes page (`App/ProcessesPage.cs:415-424`):

- **Rows:**
  - Each row is a `DestinationRow`, kept per key.
  - `Update` sets each cell's text and tooltip. It raises one `PropertyChanged(string.Empty)` only when one of them changed, then returns the row.
  - Reconciling removes gone rows, applies the `CalmOrderPlanner.Plan` moves (`Core/ProcessRowOrder.cs:13-34`), then inserts new rows at their place.
  - Row containers, the selection, keyboard focus and the scroll offset survive every update, except as below.
- **New data:**
  - In Live, new data is a view whose `Timestamp` differs from the last applied one. In History, it is a newly applied view.
  - On new data the table re-sorts unless its order is held.
  - During a hold it takes `CalmOrderPlanner.Held(current, sorted, _ => null)` (`:40-54`). Gone rows leave, new rows join the bottom in sorted order, and nothing moves.
- **Holds:** an `OrderHold` (`Core/ProcessRowOrder.cs:76-84`) on the page's monotonic `Stopwatch`.
  - The order is held:
    - while the pointer is over the grid;
    - while the row menu is open;
    - for 3 s after Up, Down, Page Up, Page Down, Home or End in the grid.
  - `Unloaded`, and `IsVisibleChanged` to hidden (`App/ConnectionsPage.cs:135`), clear PointerOver and MenuOpen, as Processes clears PointerOver on Unloaded (`App/ProcessesPage.cs:162`).
- **Immediate re-sort, even when held:**
  - a header click;
  - a scope change;
  - F5 with focus in the page (new, in the page's PreviewKeyDown, `App/ConnectionsPage.cs:134`). MainWindow's shell keys stay Processes-only (`App/Windows.cs:84-90`);
  - a mode change;
  - any filter change in either mode: search, country, direction or the application filter. In History, the interval too (`SameHistoryContext`, `:203-205`, which *The traffic strip* extends with `Application`).
- **Back to the top:** a new History context, a scope change and a mode change also scroll the grid to its top, as the card does for a new ranking (`App/ApplicationResourcesCard.cs:263`).
- **Same data:**
  - A Live view equal to the last applied one, by record equality, updates nothing and rebuilds nothing.
    - A stale view is a new copy on every read (`App/ConnectionService.cs:92-93`), so a reference check would never match. Record equality matches, because the copy shares the `Connections` list.
    - `Changed` fires every 0.5 s or 1 s (`App/UiSession.cs:59`, `:212`).
  - A view with the same `Timestamp` but other values updates values only. That happens when a lookup finishes (`App/ConnectionService.cs:198-199`) or when the view turns stale.
- **History reads:**
  - A new History context clears the rows, as the map does (`App/ConnectionsPage.cs:209-213`), and shows "Reading saved destinations…".
  - A cursor move or a playback step keeps the rows until the next view arrives, then updates them in place.

### Selection and map highlight

- **Selection** is single.
  - Selecting a row clears the node selection and its detail box (`ClearSelection`, `App/ConnectionsPage.cs:260-266`), then highlights the row's points.
  - When `SelectMapPeer` (`:267-274`) actually selects a node, by a map click or from the node list, it first clears the table selection and calls `Highlight(null)`, then calls `SelectPoint`. When it returns early (`:269-270`), the table selection stays.
  - `ClearSelection` stays node-only, because `FiltersChanged` (`:201`) and the detail box's Close (`:100`) call it. Esc's handler (`:134`) and `ChangeMode` (`:175`) also call the new `ClearDestinationSelection`, so Esc and a mode change clear both.
- **Filter and cursor changes:** `FiltersChanged` runs on every cursor move (`:130`) and still clears only the node selection.
  - The table selection stays while its address is in the new rows, and clears when it is not.
  - A selected row that leaves in any update clears the selection, the highlight and the status line.
- **A row's points**, whatever its class (the map draws every located peer: `App/ConnectionsPage.cs:241`, `Core/ConnectionHistoryStore.cs:356`):
  - Live: the address's location point, if it is valid.
  - History: the distinct valid points of the address's saved locations in the interval, latest first, at most 8.
  - After every update the page passes the selected row's current points to `Highlight`. A finished lookup can add a point, and a database reload clears the cache (`App/ConnectionService.cs:183`). This never turns the map.
- **One rounding rule:** `ConnectionProjection.LocationKey(GeoPoint)` = (`Math.Round(lat, 3)`, `Math.Round(Wrap(lon), 3)`), the rule `Group` uses today (`Core/ConnectionProjection.cs:155`). `Group` calls it, so node grouping and highlighting cannot disagree.
- **Cap order:** `ConnectionProjection.Group(points, IReadOnlySet<(double, double)>? highlighted = null)` orders groups by Selected, then highlighted, then key (`:162`).
  - With null, the order is today's.
  - So a highlighted Live node is never among the groups cut by the 256 cap (`:163`).
  - In History the store has already chosen the 256 markers by bytes (`Core/ConnectionHistoryStore.cs:351-363`), so a location outside them has no node. A point shared with a drawn marker is drawn and highlighted.
- **`ConnectionMap.Highlight(IReadOnlyCollection<GeoPoint>? locations)`** (new; null or empty clears the highlight):
  - The map gains two fields: the last `SetData` input (today it keeps only the grouped points, `App/ConnectionMap.cs:105-109`) and the last key passed to `SelectPoint` (`:79-83`).
  - `Highlight` stores the keys of the valid points. When the key set changes, it regroups the stored input, re-applies the stored `SelectPoint` key and redraws.
  - `SetData` keeps the stored highlight.
  - After a call that changes the key set, the page calls `RefreshMappedConnections`, because in Live with more than 256 groups the displayed nodes can change.
  - `HighlightedKeys` lists the displayed nodes whose location key is highlighted.
  - A highlighted node draws as a selected one does: radius + 1.5 px, a 1.2 px outline and the brightened arc (`:333`, `:456-462`).
  - It also gets a 1 px ring in the theme's `Accent` brush at radius + 4 px, scaled by `MapOnly`. The brush is a resource reference, like `Download` (`:64-66`).
  - The map's automation name gains " · {n} highlighted" when n > 0.
  - `Selected` stays the node-selection mechanism.
- **Turning the map:** after a selection, if no highlighted node has a `PointPosition` (`:85-92`) but at least one is displayed, the page calls `SetView` (`:130`) on the busiest highlighted node at the current zoom. That turns the globe or pans the 2D map. It happens once per selection, never on later updates.
- **Status line** under the grid (Muted 11, collapsed when empty). The first matching rule wins:
  1. Displayed highlighted nodes: "Highlighted 1 map node." / "Highlighted {n:N0} map nodes."
  2. Points but no displayed node (History only): "Not on the map: its location is not among the 256 drawn."
  3. No valid points:
     - Local: "Not on the map: local addresses are not located."
     - Special: "Not on the map: special-purpose addresses are not located."
     - Internet: "Not on the map: no map position for this address."
  - It is re-evaluated after every update.

### Copy and export

- **Copy:**
  - Ctrl+C and the row menu's **Copy row** put the selected row's shown cells on the clipboard: in column order, tab-separated, on one line, with no header.
  - **Copy address** puts only the address on the clipboard.
  - The row menu opens on the row under the pointer and selects it first. The Menu key and Shift+F10 open it for the selected row.
  - A `CommandBinding` replaces the grid's own Copy command, so that command never runs.
  - Copying goes through an internal `Action<string> SetClipboard` (default `Clipboard.SetText`).
  - Results go to the page's action status (`App/ConnectionsPage.cs:52`), which sits directly under the Destinations card. They read "Copied to the clipboard." on success, or "Copy failed ({ExceptionType}); try again." on failure.
  - With no selection, Ctrl+C does nothing.
- **Export destinations…** sits in the card's toolbar.
  - **Availability:** enabled only in History and while no export runs. It shares `exportInProgress` with Export CSV… (`App/ConnectionsPage.cs:259`, `:326-342`); `UpdateExportEnabled` sets both buttons.
  - **Live tooltip:** "Export is available in History.", with `ToolTipService.ShowOnDisabled` true. Without it WPF shows no tooltip on a disabled button.
  - **Dialog:** the same rules as Export CSV (`:328-331`): "Connection export unavailable." without a service, a CSV filter, `OverwritePrompt` false, and the default name `destinations-{yyyyMMdd-HHmmss}.csv`. It uses the same refusal and success texts (`:337-339`).
  - **Failures:** a `ConnectionHistoryLimitException` shows "This interval holds too many saved rows to export destinations. Choose a shorter interval." Any other failure uses Export CSV's text (`:340`). As with Export CSV, a failed export leaves the new file as far as it was written.
  - **Query:** the whole-interval query that `BuildQuery` returns through its `out` parameter (*The traffic strip*; today `BuildQuery(true)`, `:331`), with every filter, the application filter included. The minute cursor is ignored, as for Export CSV.
  - **Test hook:** the page calls an internal `exportDestinations` delegate, like `exportHistory` (`:44`, `:81`).
  - **Service:** `ConnectionService.ExportDestinationsCsvAsync(path, query, scope)` mirrors `ExportCsvAsync` (`App/ConnectionService.cs:37-46`): off the UI thread, `FileMode.CreateNew`, UTF-8 without BOM.
  - **Store:** the service calls `ConnectionHistoryStore.ExportDestinationsCsv(TextWriter, ConnectionHistoryQuery, DestinationScope)`. It works like this:
    - It takes the store gate and opens its own read-only connection.
    - It runs *Connections storage*'s pragmas and TEMP build (its steps 1–3) for the whole interval in one deferred transaction, then step 1 of History store below. Its TEMP pages count toward the same 64 MiB limit, and SQLITE_FULL from any of its statements throws `ConnectionHistoryLimitException`, as in `Read`.
    - It writes the header, then streams the scope's destinations in pages of 1,000. Each page's app lists, port lists and shown locations come from one query each (steps 4–6 restricted to the page's addresses, with the export caps).
    - Like Export CSV (`Core/ConnectionHistoryStore.cs:440`), it holds the gate for the whole export, so the writer's saves wait for it. A Quit or a sign-out during an export can miss the final save's deadline (`App/UiSession.cs:300`; *Connections storage*, sign-out).
  - **Header:** `Address,Class,CountryCode,Country,Region,City,Latitude,Longitude,Applications,ApplicationNames,Ports,PortList,ReceivedBytes,SentBytes,Minutes,FirstSeenUtc,LastSeenUtc,Complete`.
  - **Rows:** every destination in the scope, with no 1,000 cap, ordered by received + sent descending, then address, ordinal.
    - Applications and Ports hold the exact counts.
    - ApplicationNames and PortList hold the busiest first, joined by "; ", at most 100 apps and 1,000 ports. When there are more, they end with "; +{n} more" (invariant digits), which keeps each cell under Excel's 32,767-character limit.
    - The location and coordinates are the shown location's.
    - Times use the "O" format.
    - Values go through `Csv`, so formula neutralization applies to app names (`Core/ConnectionHistoryStore.cs:455-466`).
- **Live has no export.** It has nothing saved, as today.

### Empty states

The empty text replaces the grid, in a border of the same height (Muted 12, centred). The first matching rule wins.

**Live:**

- The view is not `Available` and has no connections: "No destinations: per-app capture is not running. The capture status above says why."
- The view is available with no connections: "No destinations yet. Apps that send or receive traffic appear here."
- The filters remove every connection: "No destinations match the filters."
- The scope removes every row: "No Internet destinations right now." or "No local destinations right now."

**History** (`{span}` is "interval", or "minute" with the minute cursor on):

- The interval is invalid (`BuildQuery` returns null): "Choose a valid interval to list saved destinations."
- Reading: "Reading saved destinations…"
- The read failed, hit the TEMP limit, or there is no service, so `Destinations` is null (`App/ConnectionsPage.cs:150`, `:226`): "Destinations unavailable; no saved observations shown." The action status carries the reason.
- No rows: "No saved destinations in this {span}." When search, country, direction or the application filter is set: "No saved destinations match the filters in this {span}."
  - An interval older than what the file keeps reads as no rows; the strip names the reason ("Not kept before …", *The traffic strip*).
- The scope removes every row: "No saved Internet destinations in this {span}." or "No saved local destinations in this {span}."

All of this copy lives in a Core `DestinationCopy` class, so the exact strings are tested on Linux.

### Page layout

- **Body rows** (today `App/ConnectionsPage.cs:83-84`, `:97`, `:102`, `:121`, `:124`), as Grid rows 0–6. Defined here for *The traffic strip*: this section owns this list and `MeasureOverride`, and *The traffic strip* supplies `trafficCard` for row 2.
  - Row 0: header (Auto).
  - Row 1: map card (Star, MinHeight 300).
  - Row 2: traffic strip, `trafficCard` (Auto; collapsed in Live).
  - Row 3: node list (Auto, unchanged).
  - Row 4: footer with Export CSV… and the node selection text (Auto, unchanged).
  - Row 5: **Destinations card** (Auto, new).
  - Row 6: settings, with the action status first (Auto).
- **Destinations card:** a `Ui.Panel` card like the map card, Margin (0, 8, 0, 8). From top to bottom:
  - the heading "Destinations" (`Ui.Text` 13, like "Connection details", `:100`);
  - a toolbar with the "Show" label, the scope combo and Export destinations…;
  - the count line;
  - the grid, or the empty border;
  - the status line;
  - the footnote.
- **Grid height:** fixed at 315 DIP. That shows about eight of the theme's 35 px rows (`App/App.xaml:245`) under the shorter column header, a little fewer when the horizontal scrollbar shows.
  - Table rows coming and going never resize the map. The node list still grows from 45 to 140 px (`:54`), as today.
  - Row virtualization (`App/App.xaml:245`) works inside the page's ScrollViewer.
  - The mouse wheel over the grid scrolls the grid. When the grid cannot scroll further in the wheel's direction, its PreviewMouseWheel passes the wheel to the page's ScrollViewer. This matters because the map takes the wheel for zoom (`App/ConnectionMap.cs:559`).
- **Grid settings:**
  - `AutoGenerateColumns` false, `IsReadOnly`, single selection.
  - `CanUserReorderColumns` and `CanUserResizeRows` false.
  - Pixel scroll unit (`App/ProcessesPage.cs:147`).
  - Columns are kept per key and swapped per mode, as in `ShowColumns` (`:228-240`).
  - Automation name "Destinations".
  - The row style binds `AutomationProperties.Name` to the row's spoken text (as `App/ProcessesPage.cs:145`).
- **`MeasureOverride`** (`App/ConnectionsPage.cs:137-146`). Defined here for *The traffic strip*:
  - The minimum height is the measured header, `trafficCard`, footer, Destinations card and settings, plus 300 for the map and 170 for the node list, as today. So the map keeps at least 300 px and the page scrolls instead.
  - Each part is measured at the available width with infinite height, as today; the fixed grid height keeps the card's height finite. A collapsed `trafficCard` (Live) measures 0.
  - The Destinations card's and `trafficCard`'s `SizeChanged` invalidate the measure, like the others (`:127`).
- **Settings help text** (`App/ConnectionsPage.cs:124`): "The list follows the nodes visible on the map. Local / special and unlocated peers remain in totals and history CSV." becomes "The node list follows the nodes visible on the map; the Destinations table lists every destination, including local, special-purpose and unlocated ones." Defined here for *The traffic strip*, whose sentence follows this one.

### Core model

`Core/ConnectionModels.cs` gains:

```csharp
public enum DestinationScope { All, Internet, Local }
public sealed record NetworkAddressClass(NetworkAddressKind Kind, string Label);
public sealed record ConnectionDestinationApp(string Name, long ReceivedBytes, long SentBytes);
public sealed record ConnectionDestinationPort(int Port, ConnectionProtocol Protocol, long ReceivedBytes, long SentBytes);
public sealed record ConnectionDestination(string Address, NetworkAddressClass Class, ConnectionLocation? Location,
    IReadOnlyList<GeoPoint> Points, bool Unlocated, IReadOnlyList<ConnectionDestinationApp> Apps, int AppCount,
    IReadOnlyList<ConnectionDestinationPort> Ports, int PortCount, long ReceivedBytes, long SentBytes,
    double? DownloadBytesPerSecond, double? UploadBytesPerSecond, int? Connections, long? Minutes,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool? Complete);
public sealed record ConnectionDestinationCounts(long All, long Internet, long Local, long Special, long Unlocated);
public sealed record ConnectionDestinationList(IReadOnlyList<ConnectionDestination> Rows, ConnectionDestinationCounts Counts);
```

- **`ConnectionHistoryView`** (`:45-49`) gains a trailing `ConnectionDestinationList? Destinations = null`.
  - Because it is trailing and optional, existing positional constructions keep compiling (`App/ConnectionsPage.cs:150`; `AppTests/ConnectionsPageUiChecks.cs:52`, `:166`, `:273`).
  - The store (`Core/ConnectionHistoryStore.cs:384`) passes it by name. It passes *The traffic strip*'s `Strip` by name too, so the order of the two members does not matter.
- **`Unlocated`:** the Unlocated rule under Rows, for this address. It is always false for Local and Special.
- **Nulls by mode:**
  - Live-only values (rates, Connections) are null in History rows.
  - History-only values (Minutes, Complete) are null in Live rows. Live completeness comes from the view.
  - Rates are null when the view is not `Available`.
- **`ConnectionDestinations`** (new `Core/ConnectionDestinations.cs`) holds:
  - `FromLive(IReadOnlyList<LocatedConnection> connections, bool available)`, which groups the filtered live connections and applies the caps;
  - `InScope(list, scope)`;
  - the constants under Caps.
- **Beside it:** `ConnectionDestinationSort` and `DestinationCopy`.
- **Building the Live list:** the page builds the list once per update, from the same filtered array it uses for the summary and the map (`App/ConnectionsPage.cs:236`).
- **Page hooks (internal, for the UI checks):**
  - `Func<bool> hasLocationDatabase`, default `() => session.Connections?.DatabaseDate is not null`;
  - `Func<string, ConnectionHistoryQuery, DestinationScope, Task> exportDestinations`;
  - `Action<string> SetClipboard`.

### History store

All of this runs within `Read`'s transaction, in the same snapshot as the summary and markers, after *Connections storage*'s steps 2–3 have built `temp.geo(id, location, origin, located)` and `temp.grouped`. `temp.grouped` holds a `geo` id in place of location and origin.

0. **`app_key`.** Defined here for *The traffic strip* and the 0.17.0 sections: `db.CreateFunction<string, string>("app_key", ProcessGrouping.Key, isDeterministic: true)` sits beside `sat_sum` in `Open` (`Core/ConnectionHistoryStore.cs:474`), so every `connections.db` connection has it. *The traffic strip* and the 0.17.0 sections use the same name and rule.
1. **Destinations table:**

   ```sql
   CREATE TEMP TABLE destinations AS
   SELECT g.remote, g.kind,
          sat_sum(g.received) received, sat_sum(g.sent) sent,
          min(g.first_seen) first_seen, max(g.last_seen) last_seen,
          count(DISTINCT g.minute) minutes, min(g.complete) complete,
          count(DISTINCT app_key(g.name)) apps,
          count(DISTINCT g.port*2+g.protocol) ports,
          max(g.kind=0 AND x.located=0) unlocated,
          row_number() OVER (PARTITION BY g.kind
            ORDER BY cast(sat_sum(g.received) AS REAL)+cast(sat_sum(g.sent) AS REAL) DESC, g.remote) rank
   FROM temp.grouped g JOIN temp.geo x ON x.id=g.geo
   GROUP BY g.remote, g.kind
   ```

   - `port*2+protocol` is unique because protocol is 0 (Tcp) or 1 (Udp) (`Core/ConnectionModels.cs:3`).
2. **Counts:** `SELECT count(*), sum(kind=0), sum(kind=1), sum(kind=2), sum(unlocated) FROM temp.destinations`, uncapped. These are the figures under Rows.
3. **Rows:** those with rank ≤ 1,000, at most 3,000.
4. **Apps:** in one pass over `temp.grouped` for those rows.
   - Group by (remote, `app_key(name)`) with `sat_sum` totals.
   - The name comes from `row_number() OVER (PARTITION BY remote, app_key(name) ORDER BY minute DESC, name) = 1`: the latest minute's casing, ties the lower BINARY name.
   - Keep `row_number() OVER (PARTITION BY remote ORDER BY total DESC, key) ≤ 10`, so at most 30,000 rows cross into .NET.
   - There is no per-app subquery, because `temp.grouped` has no index on remote.
5. **Ports:** the same as apps, grouped by (remote, port, protocol), ties by port then protocol. At most 30,000 rows.
6. **Locations:** for those rows, join `temp.geo` where `x.location <> 'null'`.
   - Group by (remote, x.location) with `max(minute)` as latest.
   - Keep `row_number() OVER (PARTITION BY remote ORDER BY latest DESC, location) ≤ 8`, so at most 24,000 rows.
   - The first one is the row's Location. Its distinct valid points, in that order, are Points.

Notes:

- **Statement shapes:** the per-kind rank over aggregates, the geo join, the windowed app name and the bounded locations ran on a fixture with SQLite 3.53.1, on a `mode=ro` connection inside `BEGIN DEFERRED`. On the fixture, Unlocated matched the summary's rule. *Connections storage*'s tests cover the bundled 3.53.4 TEMP build.
- **Limit.** The `destinations` table and these statements count toward *Connections storage*'s `temp.max_page_count` (64 MiB). The table holds one row per distinct remote, so it is small beside `temp.grouped`.
  - SQLITE_FULL here throws `ConnectionHistoryLimitException` like the rest of `Read`, and no partial view is returned.
  - The page shows *Connections storage*'s limit text in the action status and the "Destinations unavailable" state.
- **Lifetime:** the TEMP tables go away with the read-only connection.
- **Window functions:** the bundled `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 already runs them in `Read` (`:353`).
- **Filters:** every predicate in *Connections storage*'s `GroupedWhere` reaches these steps through `temp.grouped`: search, country, direction, *The traffic strip*'s `Application` and, in 0.17.0, the clear's one-app hide predicate.

### With 0.17.0

0.16.0 ships the table without these. They arrive with *Don't record these apps and Clear history…*, whose set, key and copy they use.

- **"not recorded" mark.** Defined here for *Don't record these apps and Clear history…*. In Live, an app whose key is in that section's Don't record set carries its ` · not recorded` suffix:
  - in the APPS cell, after the busiest app's name and before " +{n:N0}", when the busiest app is excluded. The cell is one plain text, so the suffix takes the cell's style;
  - on that app's line in the APPS tooltip: "{name} · not recorded · ↓ {received} · ↑ {sent}";
  - in the spoken text, as ", not recorded" after the busiest app's name.
  - The mark is part of the shown cell, so Copy row copies it. Grouping, the busiest choice, sorting and search use the bare name.
  - History rows, their tooltips and Export destinations carry no mark. They show what was saved: once the exclusion applies, an excluded app's later minutes are not saved, so History rows lack it for that time.
- **Clears.** A Connections clear hides its rows at once, as that section defines: a one-app clear through its hide predicate in `GroupedWhere`, an all-apps clear by raising `expiry`, which `GroupedWhere` already applies. History rows and Export destinations read `temp.grouped`, so they never show cleared rows, even while the clear's delete steps still run. When the clear finishes, the page re-reads the shown History view through `ConnectionsPage.ReloadHistory()` (*The traffic strip*), and the table updates in place without the cleared rows. A selected row that leaves clears the selection, as in any update. Live is unaffected.
- **One app key.** When that section registers `app_key` as `AppKey.Of`, the Live grouping here (`ProcessGrouping.Key`) switches to the same function, so Live and History group apps alike.

### Accessibility

- **Spoken text** for each row: "{address}, {class label}, {location, or 'location unknown, {reason}'}, {n} app(s): {busiest}{' and n more'}, port {busiest port}{' and n more'}, received {x}, sent {y}".
  - Live adds ", download {↓}, upload {↑}, {n} connections", and ", incomplete" when the view is not complete.
  - History adds ", {n} minutes, last seen {time}", and ", incomplete" when the row is incomplete.
- **Names:** the scope combo, the Export destinations… button, the grid and the row menu items are named.
- **Keyboard:**
  - Tab reaches the scope combo, the button and the grid.
  - The arrow keys move the selection and hold the order.
  - The Menu key and Shift+F10 open the row menu.
  - Ctrl+C copies, F5 re-sorts, and Esc clears both selections.
- **Colours** are theme resources only. The grid uses the app's DataGrid styles (`App/App.xaml:238-262`).
- **DPI:** sizes are in DIPs.

### Errors, threads and limits

- **Unknown is never zero.** An unknown value is null and shows "—" with a tooltip naming its reason: the LOCATION reasons and the stale-capture rates above. The one exception is the ledger's 0 B/s on the first sample after a capture break, which the map and detail box already show (Out of scope).
- **Unreadable is "unavailable".** A failed read, the TEMP limit or a missing service shows "Destinations unavailable; no saved observations shown.", never an empty period. The action status carries the reason: the existing "History could not be read ({TypeName})." (`App/ConnectionsPage.cs:226`) or *Connections storage*'s limit text.
- **Status text** is fixed copy, counts and exception type names: the highlight status, "Copied to the clipboard.", "Copy failed ({ExceptionType}); try again." and the export texts. It never names an app, an address or a path. App names and addresses appear only in cells, tooltips, the spoken row text, the clipboard and the CSV.
- **Threads.** The History steps run inside `Read`, through `ReadViewAsync`'s `Task.Run` (`App/ConnectionService.cs:35`). Export destinations runs in its service's `Task.Run`. The Live list is grouped in `ApplyLive` on the UI thread from the in-memory view, with no storage or native call, as today's summary and map are; it covers at most 8,192 peers. Clipboard writes run on the UI thread, which `Clipboard.SetText` requires.
- **Limits.** `connections.db`'s 256 MiB cap, 30-day retention, journal bound and transaction budgets are unchanged. The table's own bounds are under Caps and History store: 1,000 rows per scope and 3,000 in all, 10 apps, 10 ports and 8 points per row, at most 30,000 + 30,000 + 24,000 helper rows per read, and 100 apps and 1,000 ports per CSV cell.
- **Interruptions.** A History read cancelled by hiding or unloading the page is made again when the page is shown (`App/ConnectionsPage.cs:135`), so the table never stays stale. An export cut off by an error, Quit or a crash leaves the new file as far as it was written, as Export CSV does; it is never resumed or reported as finished, and exporting again writes a new file.

### Privacy

- The table shows only what the page already captures and saves.
- It stores one new setting, the scope.
- It makes no lookups and sends nothing.
- Export destinations writes only a new local file.

### Tests

Core tests (`Tests/`) run on every run, on Linux and in CI. App tests and the page checks run in Windows CI; `ConnectionsPageUiChecks` and `ConnectionMapUiChecks` run inside the existing STA fact `Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows` (`AppTests/BatteryUiTests.cs:38`, phases at `:62` and `:64`).

**Core (Linux and CI, every run), `Tests/`:**

- `Describe_labels_every_classified_range`: one or more addresses for each row of the class table, including:
  - both ends of each IPv4 range;
  - 0.0.0.0, 0.1.2.3, 255.255.255.255, ::, ::1;
  - an IPv4-mapped IPv6 address.

  Asserts each label, and that `Describe(a).Kind == Classify(a)` for every address.
- `Unparseable_destination_text_is_special_unrecognized`.
- `Live_destinations_group_by_remote_address`: three peers to one address from two apps, on different local ports, one of them Udp. Asserts one row with:
  - AppCount 2, PortCount 2 and Connections 3;
  - byte sums that saturate at `long.MaxValue`;
  - summed rates;
  - the minimum FirstSeen and the maximum LastSeen.
- `Live_rates_are_null_when_capture_is_unavailable`.
- `Live_app_names_group_case_insensitively_and_keep_the_first_casing`: a name with a tab and surrounding spaces shows its `NormalizeName` form.
- `Live_unlocated_and_counts_follow_the_summary_rules`: a located public peer, a country-only public peer, an unlocated public peer, a local peer and a located special peer give Unlocated 2, Local 1 and Special 1. The special row's Points hold its point.
- `Apps_and_ports_keep_the_ten_busiest_with_exact_counts`: 12 apps and 12 ports; ties by key, ordinal.
- `Scopes_partition_kinds_and_special_is_all_only`.
- `Each_scope_keeps_its_own_top_1000`: 1,200 public and 1,100 local destinations.
  - Internet and Local each give 1,000 rows.
  - All gives the overall top 1,000.
  - The counts are 2,300 (All), 1,200 (Internet), 1,100 (Local) and 0 (Special).
  - Ties go by address, ordinal.
- `Destination_sort_rules`:
  - each key's order and first-click direction;
  - nulls and "—" last in both directions;
  - IPv4 before IPv6;
  - ties by address;
  - the fallback to RECEIVED descending for a key the mode lacks.
- `Location_key_matches_map_grouping`: rounding at the third decimal; longitudes 180 and −180 wrapping to one key; `Group` grouping exactly the points that `LocationKey` treats as equal.
- `Grouping_keeps_highlighted_locations_within_the_cap`: 300 points, with one highlighted location that sorts after the first 256 by key.
  - Asserts that it is displayed, that Selected still comes first and that Overflow is 44.
  - The existing `Grouping_preserves_selection_keys_saturates_totals_and_bounds_markers` (`Tests/ConnectionProjectionTests.cs:67`) passes unchanged with no highlight.
- `History_destinations_match_a_brute_force_grouping`: a random fixture of public, local and special addresses, with several apps (ASCII names), ports, minutes, locations and incomplete batches.
  - It compares each row with LINQ over the appended records: totals, minutes, first and last seen, app and port counts and lists, the complete flag, the kind, Unlocated and Location.
- `History_destination_counts_match_the_summary`:
  - On the fixture of `Markers_are_bounded_while_full_summaries_include_local_unlocated_and_every_country` (`Tests/ConnectionHistoryStoreTests.cs:183`): All 302 = Destinations, Local 1 = Local, Unlocated 1 = Unlocated (after the Unlocated fix), Internet 301, Special 0.
  - On *Connections storage*'s `Unlocated_counts_public_peers_without_a_point_only` fixture: All 6, Internet 3, Local 1, Special 2, Unlocated 2.
- `History_destination_shows_the_latest_saved_location_and_every_point`: one address saved with two locations (as in `AppTests/ConnectionsPageUiChecks.cs:137-138`), plus minutes with no location.
  - The Location is the latest non-null one.
  - Points holds both, latest first.
  - Unlocated is true, and the count equals the summary's.
- `History_destinations_follow_search_country_direction_and_the_minute`: also covers a set `Application` field (*The traffic strip*), which keeps only that app's rows.
- `History_destination_lists_are_bounded`. The fixture:
  - 1,100 public, 1,100 local and 1,100 special addresses with one row each;
  - 5 public addresses each reached by 15 apps on 15 ports (app i on port i);
  - one address saved with 12 distinct located locations in 12 minutes.

  Asserts:
  - at most 1,000 rows per kind and 3,000 in all;
  - AppCount and PortCount 15, with 10 apps and 10 ports listed;
  - exactly 8 points, and the latest location.
- `Destinations_csv_lists_every_destination_in_scope`: 1,200 rows with no cap. Asserts:
  - the exact header and the row order;
  - the Local scope;
  - an app named "=formula" neutralized;
  - numeric coordinates;
  - an address with 120 apps listing 100 names, then "; +20 more", with Applications 120.
- `Destination_scope_setting_normalizes_and_round_trips`: a missing property, null, "Bogus" and "local" read as "All"; "Local" survives Write and Read.
- `Destination_copy_texts`: every empty-state, count, note and status string above, including N0 formatting, the singular "1 destination" and the plurals.

**App/UI checks (Windows CI), `AppTests/`:**

- **`ConnectionsPageUiChecks`:**
  - The two `Assert.Empty(Elements(page).OfType<DataGrid>())` assertions (`:41`, `:189`) become an assertion of a single DataGrid named "Destinations".
  - `ConnectionsWidgetUiChecks.cs:476` stays: the widget has no table.
- **`CheckDestinationsLive`**, on the existing fixture (8.8.8.8 located, 192.168.1.1 local, 9.9.9.9 unlocated):
  - the Live headers in order;
  - three rows under All, two under Internet and one under Local;
  - the class labels;
  - the "—" tooltips for the local row, and for the unlocated row with and without a database, through `hasLocationDatabase`;
  - the count line "3 destinations: 2 Internet (1 unlocated), 1 local, 0 special-purpose.";
  - a stale view showing "—" rates with their tooltip;
  - each Live empty text.
- **`CheckDestinationsInPlace`:**
  - A second view with a new Timestamp keeps the same row instances, containers, selection and scroll offset.
  - With the pointer over the grid, a new address joins the bottom and nothing moves. After the hold, the next new Timestamp re-sorts.
  - Applying an equal view again never rebuilds or re-sorts, including a stale view read twice from a service.
  - F5, a header click and a Live search change re-sort during a hold.
  - A gone row leaves and clears the selection.
  - The 3 s key hold is tested with a fake clock.
  - Hiding the page clears the pointer hold.
  - A scope change scrolls the grid to its top.
- **`CheckDestinationHighlight`:**
  - Selecting the 8.8.8.8 row puts its node in `HighlightedKeys`, keeps the detail box collapsed and shows "Highlighted 1 map node.".
  - With the globe turned away, selecting the row brings the node into view (`PointPosition` is not null).
  - Selecting the local row shows the local "Not on the map" text.
  - A located special fixture peer highlights its node and shows "Highlighted 1 map node.".
  - Selecting a node after a row clears the table selection and leaves that node selected after the regroup. Selecting a row clears the node selection.
  - Esc clears both. The detail box's Close leaves the table selection.
  - A cursor move keeps the row selected.
  - In Live, giving the selected unlocated row a location in a later view (same Timestamp) highlights the new node without moving the camera.
  - In Live with 300 locations, a highlighted location beyond the first 256 keys is drawn.
- **`CheckDestinationsHistory`**, with a real store:
  - the two-location fixture gives one row that highlights two nodes;
  - the invalid-interval, reading, unavailable and minute texts;
  - a destination outside the 256 markers shows "Not on the map: its location is not among the 256 drawn.";
  - the History headers;
  - the sort falling back from ↓ DOWNLOAD to RECEIVED.
- **`CheckDestinationScopeSaved`:** choosing Local saves the setting, and a new page opens on Local. The scope leaves the summary and the node list unchanged.
- **`CheckDestinationsCopy`**, through the `SetClipboard` hook:
  - Ctrl+C copies the tab-separated row, and Copy address copies the address. Both results show in the action status.
  - A throwing hook shows "Copy failed (ExternalException); try again.".
  - With no selection, nothing is copied.
- **`CheckDestinationsExport`**, through the `exportDestinations` hook:
  - Disabled in Live, with its tooltip and `ShowOnDisabled`.
  - In History it writes a new file through a fake export.
  - It refuses an existing file.
  - It is disabled while Export CSV runs, and Export CSV is disabled while it runs.
  - A fake `ConnectionHistoryLimitException` shows the limit text.
- **Layout:**
  - `CheckWholePageLayout` (`:185-196`, at 671 × 440) asserts:
    - the grid is 315 high;
    - the card can be reached by scrolling;
    - a wheel-down over the grid at its last row scrolls the page.
  - `CheckThemes` (`:285`), at 671 × 440 and 1100 × 700 in all nine themes, also asserts:
    - the grid is 315 high;
    - the map is at least 300 px high and 500 px wide (the existing check, `:303`);
    - the map's height is the same with 0 and with 50 Local destinations, which leave the node list unchanged;
    - in en-US, no header and no FIRST SEEN or LAST SEEN cell is trimmed at the minimum widths.
- **`ConnectionMapUiChecks.CheckHighlight`:**
  - In two themes, a highlighted node renders larger and with the `Accent` ring.
  - `Highlight(null)` clears the highlight.
  - The highlight survives `SetData`.
  - A regroup keeps the `SelectPoint` key.
  - The automation name gains " · 1 highlighted".

**0.17.0, with *Don't record these apps and Clear history…*:**

- **`CheckDestinationsNotRecordedMark`** (`ConnectionsPageUiChecks`): with one excluded app in the set, the Live APPS cell, that app's tooltip line and the row's spoken text carry the mark; Copy row includes it; search by the bare name still finds the row; a History row of the same app carries no mark.

**Windows CI measurement.** Defined here for *Connections storage*: `Connection_history_rates_and_read_timings` (`AppTests/RealWindows/ConnectionHistoryMeasurementTests.cs`) also records, for each of its 31 d, 7 d, 1 d and 1-minute reads on its full per-minute file, the elapsed time of History store steps 1–6, in the same `connection-history.json` through `MeasurementRecorder.Write`.
- The store reports that time to an internal `Action<TimeSpan>? destinationStepsTimed`, null by default and set only by this measurement, which reaches it the same way as its internal temp_store switch.
- It records only and decides nothing.

**Manual (user's PC, pending until checked by eye; never reported as verified before then):**

- Live with a busy browser: the table stays calm while the pointer rests on it.
- History over 30 days: a selected destination's node lights up.
- A LAN device appears under Local.

### Docs and release

- **User guide, Connections** (UG:129-145):
  - A new paragraph after UG:133 (the paragraph that starts "Open **Connections** beside Network") describes the table, the scope, the caps, the highlight, the hold and F5, copy and export, and that History lists saved minutes.
  - In UG:133, "Private/LAN, special and unlocated addresses remain in summary totals and saved-history CSV exports." becomes "Private/LAN, special and unlocated addresses are listed in the Destinations table (special-purpose ones under All only) and in saved-history CSV exports."
  - In UG:135 (the paragraph that starts "Download/upload colors"), after "IPv6 endpoints use `[address]:port` notation.", add: "The Destinations table lists bare addresses, since no port follows."
- **CHANGELOG and `docs/releases/v0.16.0.md`** (shared with the other 0.16.0 sections) name the table. The rollback note says that 0.15 ignores the saved scope, and that returning to 0.16 opens on All.
- **`docs/validation.md`**, 0.16.0 section: the destination steps' times from the Windows CI measurement, as measured, with no threshold; the manual checks above as pending until checked by eye.
- **README:** nothing from this section beyond the 0.16.0 release's shared one-line mention.
- **Preview** (`tools/OverviewPreview/Program.cs:236-244`, `:374`): the preview renders the table with its fixture.
  - Its reserved 203.0.113.x addresses list as "Special · documentation" under All, with "—" locations.
  - The map still draws their fixture points, and selecting one highlights its node (Selection and map highlight).
  - The preview's note already calls them reserved example IPs.
- **Release:** the table ships in the one 0.16.0 PR from main, after 0.15.0 has merged; the 0.17.0 mark and clear behaviour ship in the 0.17.0 PR.

### Out of scope

- Hostnames, reverse DNS and port service labels such as "443 → HTTPS".
- Grouping by app or by country; per-app child rows under a destination.
- Highlighting a destination in the traffic strip.
- Applying the scope to the map, the summary, the node list, the strip or Export CSV.
- A Live export; saving the sort.
- Destinations in the desktop widget.
- Synthetic map nodes for History locations outside the 256 markers.
- Changing the ledger's 0 B/s rates on the first sample after a capture break (`Core/ConnectionLedger.cs:106-109`). The map, the detail box and this table show them alike.
- Releasing the store gate during an export.
- Any schema change.

### Depends on

- ***Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0):** `temp.geo(id, location, origin, located)` and `temp.grouped` with its stored `kind`, built once per `Read` (minute reads included) in the read snapshot from `GroupedWhere`, as defined there.
- ***Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0):** the TEMP pragmas and the 64 MiB `temp.max_page_count`, `ConnectionHistoryLimitException` with no partial view, and the page's limit text, which this table and Export destinations reuse.
- ***Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0):** the summary's Unlocated rule `kind=0 AND located=0`, which this table's counts match.
- ***Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0):** `Connection_history_rates_and_read_timings` in Windows CI, its full per-minute file and its internal temp_store switch, which this section's step timings join.
- ***The traffic strip on Connections History and Usage history* (0.16.0):** `trafficCard` for body row 2, collapsed in Live, with its `SizeChanged` wired to the measure.
- ***The traffic strip on Connections History and Usage history* (0.16.0):** `TrafficStrip? Strip = null` on `ConnectionHistoryView`, passed by name.
- ***The traffic strip on Connections History and Usage history* (0.16.0):** `ConnectionHistoryQuery.Application` in `GroupedWhere`, `SameHistoryContext` (`App/ConnectionsPage.cs:203-205`), `BuildQuery` (with its `out` whole-interval query) and `Matches`, counting as a set filter for the empty texts.
- ***The traffic strip on Connections History and Usage history* (0.16.0):** its use of `app_key` only in the `Application` predicate, and its expander sentence placed right after this section's.
- ***The traffic strip on Connections History and Usage history* (0.16.0):** `ConnectionsPage.ReloadHistory()`, which re-reads the shown History view.
- ***Don't record these apps and Clear history…* (0.17.0):** the Don't record key set, `AppKey.Of` and the ` · not recorded` suffix that the Live APPS cell, tooltip and spoken text show.
- ***Don't record these apps and Clear history…* (0.17.0):** the one-app hide predicate in `GroupedWhere` and the all-apps `expiry` raise, which hide cleared rows at once, and a call to `ConnectionsPage.ReloadHistory()` when a Connections clear finishes.
- ***Don't record these apps and Clear history…* (0.17.0):** when it registers `app_key` as `AppKey.Of`, switching this table's Live grouping key to the same function.


## 0.17.0 — First network use alerts

Tell the user once, by tray balloon and in the alert log, when an app that DeadWare has not seen before first sends or receives network traffic, without a flood while DeadWare is new or while software is being installed.

- **Builds on** the 0.8.0 Alerts design (`docs/superpowers/specs/2026-11-02-alerts-design.md`), whose rules apply where this section is silent.
- **Anchors** are `origin/main` at 181eb54 (0.14.1). 0.15.0 and 0.16.0 merge first and move some of them:
  - 0.15.0 adds `ProcessColumnsVersion` to `UserSettings` and its one-time upgrade to `SettingsCodec.Read` (`src/DeadWare.Core/Monitoring.cs`), the same pattern `FirstUseRuleAdded` uses below, and changes `docs/user-guide.md` and `docs/validation.md`;
  - 0.16.0 changes `UiSession.cs`, `ConnectionsPage.cs` and `ConnectionHistoryStore.cs`;
  - *Don't record these apps and Clear history…* changes `AlertStore.cs` and `AlertService.cs` in the same release.

  Doc anchors quote their text as well as the line. Recheck every anchor after 0.15.0 and 0.16.0 merge.
- **Stores.** New tables go only in alerts.db, which stays at `user_version` 1. usage-history.db and connections.db are read once each for the seed and never written. process-history.db (0.15.0's version 2: GPU columns, up to 40 listed apps a minute plus Everything else, the 192 MiB cap) is neither read nor written.
- **House rules:** null, never zero, for an unknown value; every "—" has a tooltip naming its reason; status text holds fixed copy, counts and error codes, never names or paths; native and storage work runs off the UI thread; colours come from theme resources only; everything stays local; no new network traffic; no administrator requirement beyond per-app capture's existing one.
- **Other sections are named by title:** *Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0), *The traffic strip on Connections History and Usage history* (0.16.0) and *Don't record these apps and Clear history…* (0.17.0). What each must supply is under Depends on.

### Decisions

- One "first network use" rule is added automatically, once, switched on, for new and upgrading users. A removed rule stays removed. The rule has nothing to edit.
- Learning lasts 3 calendar days, the start day counting as day 1. It starts at the first poll in which per-app capture (administrator) has run. New apps seen during it are learned quietly. Editing, switching off, removing and re-adding the rule keep the start.
- After learning, each app's first network use is one closed log episode. Apps seen within 60 s of the first share one tray balloon, shown when the 60 s window closes.
- **Don't alert for this app** goes on the log row, because balloons have no buttons (`src/DeadWare.App/TrayIcon.cs:63-69`).
- Known apps are seeded once from the distinct names in Usage history and Connections history. A store whose seed read fails is seeded at a later start.
- Loopback traffic is not network use. Apps on the Don't record list and "PID n" names are never learned or alerted.
- A switched-off rule keeps learning quietly. A removed rule learns nothing, so adding it back alerts once for each app first used meanwhile; the guide warns.
- Clearing an app for all time forgets it, so its next use alerts again. Clearing all apps for all time empties the known set and keeps the learning start, with no new learning period. Clearing older than 7 days forgets no app. Don't alert entries stay.
- Limits: 8,192 known apps, the least recently seen forgotten to make room; a known app is forgotten after 400 days unseen; at most 256 Don't alert entries, each with **Alert again**.
- Log rows offer **View in Connections** for the last 30 days.
- Returning to 0.16 drops the rule and the Don't alert list from settings.json; the 0.17.0 release notes say so.

### The rule

- `AlertKind` (`src/DeadWare.Core/AlertRules.cs:5`) gains `FirstNetworkUse`, appended as value 4. settings.json and `episodes.kind` store kinds as numbers, so values 0–3 keep their meaning.
- `AlertRules.Normalize(AlertRule?)` (`AlertRules.cs:28-56`) gains a case for the new kind: Target "", Threshold 1, Minutes 1. Id, Enabled and Created are checked as today.
- `AlertRules.Normalize(IEnumerable<AlertRule?>)` (`AlertRules.cs:59-69`) keeps only the first FirstNetworkUse rule and drops later ones, the way it drops later duplicate ids. The rule counts toward the 64-rule cap (`AlertRules.cs:16`).
- `TargetName` (`AlertRules.cs:79-89`) returns "First network use".
- `Describe` (`AlertRules.cs:109-114`) returns "An app uses the network for the first time".
- `FormatThreshold`, `FormatValue` and `Hysteresis` are never called for this kind.
- `AlertRules.DefaultFirstNetworkUse(DateTimeOffset created)` returns `new("first-network-use", AlertKind.FirstNetworkUse, "", 1, 1, true, created)`.

**Added once, on.**
- `UserSettings` (`src/DeadWare.Core/Monitoring.cs:374-413`) gains `FirstUseRuleAdded` (bool, initializer true).
- The `Alerts` initializer (`Monitoring.cs:390`) becomes a list holding `DefaultFirstNetworkUse(DateTimeOffset.UtcNow)`, so a missing or unreadable settings file starts with the rule.
- `SettingsCodec.Read` (`Monitoring.cs:417-453`) runs a check after `AlertRules.Normalize` (`Monitoring.cs:444`). When the JSON has no `FirstUseRuleAdded` property (found with `document.RootElement.TryGetProperty`, as for `WidgetAlwaysOnTop` at `Monitoring.cs:437`) or the property is false, the default rule is appended, but only if no FirstNetworkUse rule is present and fewer than 64 rules are.
- In every case Read sets the flag to true, and the launch save (`src/DeadWare.App/UiSession.cs:95`) writes it.
- A user who removes the rule keeps it removed. A file holding 64 rules gets the flag but no rule.
- A JSON without an `Alerts` property keeps the initializer's rule, so the rule is not added twice.

**Don't alert list.**
- `UserSettings` gains `FirstUseIgnoredApps` (List<string>, empty by default).
- Read normalizes it with `FirstNetworkUse.NormalizeIgnored`:
  - each name goes through `UsageMetrics.NormalizeName` (`src/DeadWare.Core/UsageHistory.cs:33-38`);
  - names for which `FirstNetworkUse.IsLearnable` is false are dropped;
  - later duplicates by app key are dropped;
  - at most 256 are kept.
- A missing or null property reads as empty. As with `AlertRules.Normalize`, neither that nor a dropped entry sets `Error`.

**Learning date.** The rule has no parameters, so its row has no **Edit** button, and "editing keeps the start date" holds trivially. The learning date is stored per machine in alerts.db, not per rule id. Switching the rule off, removing it, or adding it again from the editor (**Add rule** builds a new id in `Draft`, `src/DeadWare.App/AlertsPage.cs:128`) therefore keeps the date.

### Who counts

- **App key:** `AppKey.Of(name)` (*Don't record these apps and Clear history…*, One app key). Defined here for that section: `known_apps.app_key`, the `episode_apps` match and the Don't alert entries are all keyed by `AppKey.Of`.
  - Keys are compared ordinally everywhere: in memory, in alerts.db and in the settings lists.
  - For a name of at most 256 characters without unpaired surrogates, the key equals `ProcessGrouping.Key(name)` (`src/DeadWare.Core/ProcessTable.cs:50`), which is `UsageMetrics.NormalizeName(name).ToUpperInvariant()`.
- **Display name:** `NormalizeName(name)` as it was first seen.
- **`FirstNetworkUse.IsLearnable(string? name)`** computes the key and is false when the key is:
  - "UNKNOWN APPLICATION", the key of `UsageMetrics.UnknownName` (`UsageHistory.cs:20`), which is also what an empty or control-only name becomes (`UsageHistory.cs:36`);
  - a "PID n" name, for which `AppKey.IsPidName` (*Don't record these apps and Clear history…*, One app key) is true: the key matches `^PID [0-9]{1,10}$`. These are the names DeadWare gives a process whose image path it cannot read (`src/DeadWare.App/NativeProcessIdentity.cs:15`). System (PID 4) is one: its image path cannot be read (`tests/DeadWare.App.Tests/RealWindows/ProcessScanChecks.cs:44`).
- **Excluded keys.** Defined here for *Don't record these apps and Clear history…*: the detector, the tracker and the seed skip every key in that section's Don't record set at each step below. The set is the `FrozenSet<string>` of `AppKey.Of` values that `AlertService.Configure` stores from its `excludedApps` list (The alert worker).
- **Network use** is any attributed TCP or UDP observation in a poll's `ConnectionSample.Deltas` (`src/DeadWare.Core/ConnectionModels.cs:11-12`, `:16-20`) whose remote address is not loopback (127.0.0.0/8 or ::1).
  - The ledger has already mapped IPv4-mapped addresses to IPv4 (`src/DeadWare.Core/ConnectionLedger.cs:64`), so "starts with `127.`" or "equals `::1`" is exact.
  - Traffic to this PC's own LAN or link-local address is not loopback, so it counts.
  - Zero-byte observations count.
  - Local, LAN, special and public addresses all count.
- **Identity** is the image file name only. A different program called chrome.exe counts as known. A program whose file name changes with each build or install (a hash or version in the name) counts as new each time.

### Detection on the collector thread

`AlertService.TryRecordNetworkUse(Reading reading)` is subscribed to `Collector.SampleAvailable` beside `TryRecordBattery` (`UiSession.cs:77`) and unsubscribed beside it (`UiSession.cs:317`). It runs on the collector thread (`src/DeadWare.App/Collector.cs:282`), like `TryRecordBattery` (`src/DeadWare.App/AlertService.cs:73-82`). It does no I/O, takes no locks of its own, and uses only collector-thread fields and volatile snapshots.

Defined here for *Connections storage: per-minute merge, the grouped read and the Unlocated fix*: detection reads `ConnectionSample.Deltas` on the collector thread and never touches 0.16.0's open-minute folder or the connection writer, so the per-minute merge neither delays nor changes what it sees.

Steps:

1. Return unless all of these hold:
   - the service is accepting;
   - a FirstNetworkUse rule exists, switched on or off (a volatile flag set by Configure);
   - `reading.Connections` (`Collector.cs:14`) is non-null with `Available` true. It is false for a standard user (`EtwNetwork.cs:37`, `:99-108`), after ETW fails, and on the collector's failure readings (`Collector.cs:290`, `:304`).
2. Clear the posted set when either of these changed since the previous poll:
   - `knownGeneration`, a volatile counter the worker increments after a clear forgets apps;
   - the UTC minute of `sample.Timestamp`.
3. For each delta:
   - skip it when the remote address is loopback;
   - compute the key through a name-to-key cache (ordinal `Dictionary<string,string>`, cleared when it reaches 4,096 entries);
   - skip keys that cannot be learned and excluded keys;
   - when the key is newly added to the posted set, record (display name, `delta.FirstSeen`);
   - when the key was already recorded in this poll, keep the earlier `FirstSeen`.
4. Post `NetworkUseWork(At: sample.Timestamp, Uses)` in either case:
   - `Uses` is non-empty;
   - the worker has not yet recorded a learning start (a volatile flag) and nothing was posted this minute. This posts the "capture has run" signal even when no app qualifies.

   A work holds at most 1,024 uses. Further keys are not added to the posted set, so the next poll posts them.

**Results:**
- Each app is posted at most once per UTC minute, and again after a clear forgets it.
- The 512-slot queue (`AlertService.cs:19-20`) sees at most about one work per poll. Drops are counted as today (`AlertService.cs:97`). A dropped work's apps are posted again in the next minute.
- The per-poll cost is one loop over the deltas: at most 32,768 (`ConnectionLedger.cs:31`), usually tens.

**What detection does not observe:**
- `FinalConnectionsAvailable` (Quit's final drain) is not observed. An app first seen only in that drain alerts at its next use.
- Detection does not depend on the recording switches. The deltas are on every Reading. Connections recording is checked only inside `ConnectionService.TryRecord` (`src/DeadWare.App/ConnectionService.cs:141`). Usage recording only stops `UsageHistoryService.TryRecord`, and with it `MinuteClosed` (`src/DeadWare.App/UsageHistoryService.cs:62`), which detection does not use.
- An app whose only traffic fell in an ETW loss window, or was dropped by the ledger's caps, is detected at its next use.

### FirstUseTracker (Core)

A pure policy object with one owner, the alert worker. It has no timers, threads or storage, so it is tested on Linux. It holds:
- the rule: its id and whether it is switched on, or none;
- the two seed markers (Usage history, Connections history);
- the learning start and end;
- the known keys with first and last seen (ordinal Dictionary);
- pending changes: known upserts, known deletes and metadata;
- the ignored and excluded keys;
- the open group;
- the episode floor, unset until a `Forget`.

Members:
- `Configure(rule?, ignored, excluded)`
- `Restore(FirstUseState)`
- `Seed(FirstUseSource source, IReadOnlyList<ApplicationSeen>? names, DateTimeOffset now)`; null means the read failed
- `Observe(DateTimeOffset at, IReadOnlyList<NetworkUse> uses, DateTimeOffset now)` returns `IReadOnlyList<AlertEpisode>`
- `TakeDueGroup(DateTimeOffset now)` returns `FirstUseGroup?`
- `Expire(DateTimeOffset now)`
- `Forget(string? key, DateTimeOffset notBefore)`
- `TakeChanges(int maximum)` returns `FirstUseChanges`
- `Status` returns `FirstUseStatus`

Records:
- `enum FirstUseSource { Usage, Connections }`
- `NetworkUse(string Name, DateTimeOffset FirstSeen)`
- `ApplicationSeen(string Name, DateTimeOffset First, DateTimeOffset Last)`
- `KnownApplication(string Key, DateTimeOffset First, DateTimeOffset Last)`
- `FirstUseMetadata(DateTimeOffset? SeededUsage, DateTimeOffset? SeededConnections, DateTimeOffset? LearningStart, DateTimeOffset? LearningEnd)`
- `FirstUseState(DateTimeOffset? SeededUsage, DateTimeOffset? SeededConnections, DateTimeOffset? LearningStart, DateTimeOffset? LearningEnd, IReadOnlyList<KnownApplication> Known)`
- `FirstUseChanges(IReadOnlyList<KnownApplication> Upserts, IReadOnlyList<string> Deletes, FirstUseMetadata Metadata)`
- `FirstUseGroup(DateTimeOffset First, IReadOnlyList<string> Names, int Count)`
- `FirstUseStatus(bool Restored, bool Seeded, DateTimeOffset? LearningStart, DateTimeOffset? LearningEnd, int? KnownApps)`: KnownApps is null until Restore.

**Observe**, in order:

1. With no rule, nothing is learned or alerted.
2. When the learning start is unset: start = `at`, and end = `FirstNetworkUse.LearningEnd(at, zone)`.
3. For each use, compute the key and skip keys that cannot be learned and excluded keys. Then:
   - **Known key:** last seen = max(last seen, minute of FirstSeen).
   - **Unknown key while 8,192 keys are known:** forget the known key with the oldest last seen (ties: the lowest key, ordinal), record its delete, then continue as for any other unknown key.
   - **Other unknown key:** add it with first seen = last seen = the minute of FirstSeen. It alerts when the rule is switched on, `at` is at or after the learning end, and the key is not ignored. Otherwise it is learned quietly, with no episode, and its upsert is recorded.
4. An alert is one closed episode:
   - Id: a new Guid "N";
   - RuleId: the current rule's id;
   - Kind: FirstNetworkUse;
   - Summary: "{name} used the network for the first time";
   - Start: min(FirstSeen, at), raised to the episode floor when one is set;
   - End: Start;
   - Peak: 0 (the column is NOT NULL, `src/DeadWare.Core/AlertStore.cs:41`, and the value is never shown);
   - Application: the display name.

   An alerted key gets no separate upsert. Append writes its known row from the episode (alerts.db), so an episode and its known row always commit together, however Flush splits the work. The episode joins the open group, or opens one with due time `now` + the group window (60 s).

**TakeChanges(maximum)** returns at most `maximum` upserts and `maximum` deletes, oldest change first, plus any metadata not yet taken. A key never has both an upsert and a delete in one result; the later change wins. Changes not taken stay for the next call.

**Expire(now)** forgets keys last seen before now − 400 days (`AlertStore.RetentionDays`, `AlertStore.cs:12`) and drops their pending upserts. The worker calls it at each Flush, and Append's retention deletes the same rows, so a forgotten app alerts at its next use in the same session.

**Forget(key, notBefore).** Defined here for *Don't record these apps and Clear history…*: the alert worker can remove keys from the in-memory known set and names from the waiting balloon group. `Forget`:
- removes `key` from the known set, or every key when `key` is null;
- drops the pending upserts of the removed keys (a pending delete stays, so a row already marked for deletion is still deleted);
- removes the removed keys' entries from the open group at once, so a key observed again in the same window is listed once;
- sets the episode floor to `notBefore`;
- changes nothing else: the learning start and end, the seed markers, the ignored keys and the rule stay.

**Learning end.** `FirstNetworkUse.LearningEnd(start, zone)` is the local midnight that begins the third calendar day after start's local date. The start day counts as day 1: a start on Monday at 09:00 or at 23:50 ends on Thursday at 00:00.
- An invalid local midnight moves to 01:00, the `StartOfDay` rule of `src/DeadWare.Core/AlertEvaluator.cs:203-208`.
- Start and end are stored in UTC, so a later time-zone change does not move them. Learning lasts between just over 2 days and 3 days, plus or minus a DST hour.
- The days count whether or not capture runs during them.

**Groups.**
- A group holds one entry (key, display name, Start) per episode, in detection order.
- A group opens with the first episode at worker time `now` and is due at `now` + 60 s.
- An episode created before the due time joins it; one at or after the due time opens the next group.
- `TakeDueGroup` at or after the due time always closes the group, then:
  - drops entries whose key is now ignored, excluded or no longer known;
  - returns null when no entry is left or no switched-on rule exists;
  - otherwise returns a `FirstUseGroup` whose First is the earliest Start left, whose Names are the first 64 entries' names, and whose Count counts every entry left.

### Known apps and seeding

**Seed timing.** Each history store is seeded once per machine. For each store whose seed marker (`first_use_seed_usage`, `first_use_seed_connections`) is missing, the seed runs the first time the alert worker runs with a FirstNetworkUse rule present: at worker start, or when a RulesWork adds the rule. It never runs while no rule exists. A store with its marker is never read for seeding again, including after clears.

**Sources:**
- `UsageHistoryService.ReadApplicationNamesAsync()` calls the new `UsageHistoryStore.ReadApplicationNames(machine)`:
  `SELECT name,min(minute),max(minute) FROM application_minutes WHERE machine=$machine GROUP BY name ORDER BY max(minute) DESC LIMIT 65536`
  - The query walks the primary key (`src/DeadWare.Core/UsageHistoryStore.cs:59-63`).
  - Minutes are Unix milliseconds.
  - It uses its own read connection, like the other reads (`UsageHistoryService.cs:154-165`), so the usage writer does not wait.
  - Like those reads it calls Fail on an error, but it returns null instead of an empty list.
- `ConnectionService.ReadApplicationNamesAsync()` calls the new `ConnectionHistoryStore.ReadApplicationNames()`:
  `SELECT name,min(minute),max(minute) FROM traffic WHERE <the expiry and floor filters of Grouped, ConnectionHistoryStore.cs:29-30> GROUP BY name ORDER BY max(minute) DESC LIMIT 65536`
  - Minutes are UTC ticks (`ConnectionHistoryStore.cs:217`).
  - The read is read-only, in a deferred transaction, under the store's gate like `Read` (`ConnectionHistoryStore.cs:334-339`).
  - There is no name index, so this is one full scan, and the writer waits for it once.
  - Per-poll rows from older versions and 0.16.0's per-minute rows hold the same `name` and `minute` columns, so the query reads both.
  - It returns null when it throws.
- Both return null while that store holds a clear marker of *Don't record these apps and Clear history…* (connections.db's `metadata` row; Usage history's `usage-history.clear.json`), so a seed never learns again an app that a resumed clear is deleting.
- Each read converts its minutes to `DateTimeOffset` before the merge.

**Merge.**
- A null result leaves that store's marker unset, and the store is read again at the next start. A result, even an empty one, sets the marker.
- Rows are merged by key: first = min, last = max.
- Names that cannot be learned, excluded names and names last seen before now − 400 days are skipped.
- Names are added in latest-last-seen order only while fewer than 8,192 keys are known. A seeded name never makes room by forgetting a known key.
- Seeded keys are written through Flush, in chunks of 1,024. Each store's marker goes in the transaction of its last chunk. A crash before the marker means that store is seeded again at the next start; the upserts are idempotent.
- The seed checks the stop flag before each read and before its first write. Once Quit or sign-out has begun it writes nothing, and it runs at the next start.
- The alert log's own clear runs only after startup's seed (that section resumes and takes commands after startup), so a name seeded just before an all-time clear began is forgotten by that clear.

**Wiring.** `AlertService` has no Connections reference and is built before `ConnectionService` (`UiSession.cs:75-78`). Its constructor gains `Func<Task<IReadOnlyList<ApplicationSeen>?>>? connectionNames = null`, passed as `()=>Connections.ReadApplicationNamesAsync()`; it is called only after `Start` (`UiSession.cs:95`), when Connections exists. A delegate that throws, or a missing delegate, counts as null.

**Forgetting.** Last seen is saved at minute precision in the next 15 s flush. A known app last seen more than 400 days ago is forgotten by `Expire` and its row deleted by Append's retention step; its next use alerts again. When 8,192 keys are known, a new app makes the least recently seen one forgotten (Observe step 3).

**Cost.** Last-seen updates make the alert worker write alerts.db about once a minute while capture runs: the first flush after each minute in which known apps were seen. Today it writes only when episodes change. Each such Append is one `synchronous=FULL` transaction plus a TRUNCATE checkpoint (`AlertStore.cs:187`, `:88`) and holds at most 1,024 known rows.

### alerts.db

Initialize's transaction (`AlertStore.cs:36-47`) adds the tables below with `IF NOT EXISTS`. `user_version` stays 1. The check at `AlertStore.cs:33` reads only the number, so every build since 0.8.0 still opens the file.

```sql
CREATE TABLE IF NOT EXISTS known_apps(
    machine TEXT NOT NULL, app_key TEXT NOT NULL,
    first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL CHECK(last_seen>=first_seen),
    PRIMARY KEY(machine,app_key)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS episode_apps(
    machine TEXT NOT NULL, episode TEXT NOT NULL, name TEXT NOT NULL,
    PRIMARY KEY(machine,episode)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS alert_metadata(
    machine TEXT NOT NULL, key TEXT NOT NULL, value INTEGER NOT NULL,
    PRIMARY KEY(machine,key)) WITHOUT ROWID;
```

**At the cap.** `Initialize` sets `max_page_count` in `Configure` (`AlertStore.cs:34`) before this transaction, and a new table needs a page, so at the 64 MiB cap a CREATE can fail with SQLITE_FULL. Defined here for *Don't record these apps and Clear history…*: these CREATEs get the same retry that section gives `pending_clear`. On SQLITE_FULL, `Initialize` evicts the oldest tenth of the closed episodes with `EvictOldestClosed` (`AlertStore.cs:134-143`), deleting their links once `episode_apps` exists, and retries once. With no closed episode to evict, or a second SQLITE_FULL, the alert log is unavailable for the session (Errors and limits).

**Tables and columns:**
- Times are UTC Unix milliseconds, as in `episodes`. `known_apps` times are whole minutes.
- `alert_metadata` keys: `first_use_seed_usage`, `first_use_seed_connections`, `first_use_learning_start`, `first_use_learning_end`. Values are integers. The clear marker of *Don't record these apps and Clear history…* lives in its own `pending_clear` table, because it holds an app key as text.
- `known_apps` stores no display name. Measured with this schema (SQLite 3.53.1, 4 KiB pages, machine key "DESKTOP-ABCDEFG"):
  - 8,192 keys of 256 CJK characters take 6.4 MiB;
  - 8,192 keys of 256 ASCII characters take 2.5 MiB;
  - 2,000 typical 20-character keys take 0.11 MiB;
  - keeping a 256-character name beside the key took 73 MiB for 16,384 rows, which is over the 64 MiB cap (`AlertStore.cs:11`).
- `episode_apps` links an episode to its app by display name. Clears match it with the shared `app_key` SQLite function, so it needs no key column or index.
  - Measured: 8,192 alerted apps with 256-character CJK names, each with its episode, link and known row, take 24.0 MiB.
  - Storing the key beside the name, with an index on the key, took 59.4 MiB, too close to the cap.
- `episode_apps` is written for every FirstNetworkUse episode and, from 0.17.0, for every ApplicationDaily episode: `AlertEvaluator.Start` (`AlertEvaluator.cs:189`) sets Application = rule.Target. Budget episodes from older builds have no link; *Don't record these apps and Clear history…* matches every budget episode by its summary.

**Code changes:**
- `AlertEpisode` (`AlertRules.cs:11-12`) gains `string? Application = null`. `Read` (`AlertStore.cs:92-106`) and `Open` (`AlertStore.cs:109-119`) fill it with a LEFT JOIN on `episode_apps`.
- `Append` (`AlertStore.cs:52-89`) gains `FirstUseChanges? firstUse = null`. One transaction holds:
  1. the retention deletes: the links of episodes with start before the cutoff, those episodes, and `known_apps` rows with last_seen before the cutoff;
  2. the episode upserts (`AlertStore.cs:69-77`), with these additions:
     - `INSERT INTO episode_apps … ON CONFLICT DO NOTHING` for each non-null Application;
     - for each FirstNetworkUse episode, the known upsert of (`AppKey.Of(Application)`, minute of Start, minute of Start);
  3. the known deletes, then the known upserts, which use `ON CONFLICT(machine,app_key) DO UPDATE SET first_seen=min(first_seen,excluded.first_seen), last_seen=max(last_seen,excluded.last_seen)`;
  4. the metadata, written with `ON CONFLICT DO NOTHING`, so nothing can move a learning date or a seed marker once it is set.

  The early return at `AlertStore.cs:57` becomes "no episodes and no changes".
- Validation runs before any write (`AlertStore.cs:145-152`, extended):
  - Application is null, or equals `UsageMetrics.NormalizeName(Application)` and is not "Unknown application". A budget episode may carry a PID name, because `AlertRules.Normalize` accepts any target except Unknown (`AlertRules.cs:45-48`).
  - A FirstNetworkUse episode needs an Application for which `IsLearnable` is true, End = Start and Peak = 0.
  - Every upsert and delete key equals `AppKey.Of(key)` and can be learned.
  - Upsert times are whole minutes, and first ≤ last.
  - A batch holds at most 1,024 upserts and 1,024 deletes.
  - A learning end is after its start.
- `SQLITE_FULL` handling (`AlertStore.cs:82-86`) and `EvictOldestClosed` (`AlertStore.cs:134-143`) delete the evicted episodes' links in the same transaction. `known_apps` and `alert_metadata` rows are never evicted for space.
- Initialize also deletes `episode_apps` rows whose episode no longer exists. Older builds' retention (`AlertStore.cs:65`) and eviction (`AlertStore.cs:140`) leave such rows behind.
- New `ReadFirstUse(machine)` returns `FirstUseState`, with at most 8,192 known rows by last_seen descending.

**Episodes are always saved closed,** so `Open` never returns a first-use episode. This keeps rollback safe:
- older builds reject kind 4 in `Validate` (`AlertStore.cs:149`);
- at startup they close and re-append every open episode their evaluator does not adopt (`AlertService.cs:126-127`), and a kind-4 episode is never adopted because those builds drop its rule;
- one open kind-4 episode would therefore fail an older build's alert log at every start;
- a closed episode is never re-appended, so an older build's alert log keeps working.

### The alert worker

- **Constructor** gains `connectionNames` and `TimeSpan? firstUseGroupWindow`. The window defaults to 60 s, must be above 0 and at most 60 s, and tests pass 100 ms. Both parameters are optional, so the existing calls (`UiSession.cs:75`, `tests/DeadWare.App.Tests/AlertsUiChecks.cs:35`) compile unchanged.
- **`Configure`** (`AlertService.cs:56-62`) gains `IReadOnlyList<string>? ignoredApps` and `IReadOnlyList<string>? excludedApps`. It stores the excluded key set (an ordinal `FrozenSet<string>` of `AppKey.Of` values) with a volatile write that detection reads, publishes the collector-side rule-exists and learning-started flags, and its RulesWork carries both lists. The existing calls at `UiSession.cs:76`, `:231` and `:236` pass them, the excluded list being `Settings.ExcludedApps` (*Don't record these apps and Clear history…*, Settings model).
- **Exclusions.** The Don't record set arrives through `Configure`'s `excludedApps` (*Don't record these apps and Clear history…*, Settings model). When the worker handles that RulesWork, it calls `tracker.Configure` with the new set, beside that section's `AlertEvaluator.SetExcluded`; nothing else changes for first use.
- **Startup** (`AlertService.cs:115-182`): after Restore (`AlertService.cs:124`), the worker:
  1. reads `ReadFirstUse` and passes it to `tracker.Restore`;
  2. configures the tracker from the current snapshot: the rules, the ignored list and the stored Don't record set (work posted before Start is not queued, `AlertService.cs:94-98`);
  3. seeds the stores whose marker is missing.
- **Loop:** `NetworkUseWork` calls `tracker.Observe`, and its episodes join the pending list.
  - The worker then flushes when pending holds 64 or more episodes, as Apply does (`AlertService.cs:149`). Without this, a work of 1,024 new apps, with up to 128 works read per wake (`AlertService.cs:165`), would exceed `AlertStore.MaximumBatch` (`AlertStore.cs:13`, `:55`) and fail the log.
  - A third wake-up, `Task.Delay(due - now, time)`, joins the `WhenAny` at `AlertService.cs:156` while a group is open.
  - When the group is due, `TakeDueGroup` runs. If notifications are on, `FirstUseGroupClosed` (Action<FirstUseGroup>) is raised inside try/catch, as at `AlertService.cs:148`.
- **Flush** (`AlertService.cs:137-142`) first calls `tracker.Expire(now)`. It then repeats `Append(up to 1,024 pending episodes, tracker.TakeChanges(1,024))` until no episodes and no changes remain.
- **Status:** the new `FirstUse` property is a volatile `FirstUseStatus`.
- **Quit and sign-out:** the final flush (`AlertService.cs:175`) writes every pending episode and change. A group still open shows no balloon. Sign-out gives every history drain 3 s (`UiSession.cs:300`, `:346`); anything not written by then is lost, like other history.
- **No one-hour floor.** First-use episodes never pass through `AlertEvaluator.Start` or `EpisodeStarted` (`AlertService.cs:39`).
  - The per-rule one-hour floor (`AlertEvaluator.cs:191-193`, `AlertRules.cs:21`) does not apply.
  - The per-episode balloon in `App.xaml.cs:46-56` never shows for them.
  - There is at most one first network use balloon per 60 s.
- **Evaluator:** `AlertEvaluator.ObserveMinute` skips the kind explicitly at `AlertEvaluator.cs:109`. `ObserveBattery` already skips it (`AlertEvaluator.cs:147`).

### Balloon

`App.xaml.cs` subscribes to `FirstUseGroupClosed` the way it subscribes at `:46-56`:
- it marshals to the dispatcher;
- it returns when `lifetime.IsEnding` or `Settings.AlertNotifications` is off;
- it calls `tray?.ShowAlert("DeadWare alert", FirstNetworkUse.BalloonText(group, TimeZoneInfo.Local, CultureInfo.CurrentCulture), ShowAlerts)` (`TrayIcon.cs:63-69`, `App.xaml.cs:79-83`).

Text. The time is group.First in local time, "t" format, as at `App.xaml.cs:54`:
- one app: "{n1} used the network for the first time at {t}. Open Alerts to review."
- two: "2 apps used the network for the first time from {t}: {n1} and {n2}. Open Alerts to review."
- three: "3 apps used the network for the first time from {t}: {n1}, {n2} and {n3}. Open Alerts to review."
- four or more: "{c:N0} apps used the network for the first time from {t}: {n1}, {n2}, {n3} and {c−3:N0} more. Open Alerts to review."

Rules for the text:
- In the balloon only, a name longer than 40 text elements (`StringInfo`) shows its first 39 and "…", so a surrogate pair is never split.
- The message is at most 225 characters with an 8-character time such as "11:59 PM" and 8,192 apps. It is at most 229 for any count below 1,000,000. A group gathers at most 1,024 apps a poll, so the cut at `TrayIcon.cs:67` never applies unless the time is over 29 characters.
- Balloons and log rows name apps, as application-budget balloons already do: the Summary shown at `App.xaml.cs:54` holds the executable name (`AlertRules.cs:111`), and the 0.8.0 design allows executable names in balloons (`2026-11-02-alerts-design.md:31`). Page and Settings status text never names an app.
- Windows may keep balloon text in its notification history, which Clear history does not clear; that section's dialog says so.
- Defined here for *Connections storage: per-minute merge, the grouped read and the Unlocated fix*: a balloon or log row can name an app whose minute Connections history has not saved yet, because detection runs on the poll and a minute is saved a few seconds after it ends. **View in Connections** opened then shows the minutes not yet saved as *The traffic strip on Connections History and Usage history* does, never as zero traffic.

### Alerts page

**Editor.**
- `Kinds` (`AlertsPage.cs:17-21`) gains `new("First network use by an app", AlertKind.FirstNetworkUse)`, appended, so the existing indexes keep their kinds.
- When it is selected, `UpdateEditor` (`AlertsPage.cs:98-112`) hides Reading, Executable, At or above and For.
- `Draft` (`AlertsPage.cs:114-131`) does not parse a threshold for this kind.
- `AddRule` (`AlertsPage.cs:132-139`) refuses a second one with "A first network use rule already exists."
- `RequestSuggestion` (`AlertsPage.cs:204-212`, as for battery at `:209`) shows "New apps are learned quietly for 3 calendar days after per-app capture first runs; after that each app's first network use is logged once."

**Rule row** (`RenderRules`, `AlertsPage.cs:165-179`). The row has the switch and **Remove**, and no **Edit**. Below it is one 11 pt Muted line, chosen in this order:
1. The alert log failed (`session.Alerts.Failed`): "Paused: the alert log is unavailable."
2. The status is not yet restored: "Starting: reading the known-app list."
3. The rule is switched off: "Switched off: apps are learned without alerts · {k:N0} apps known."
4. No learning start yet, and `Collector.IsElevated` (`Collector.cs:128`) is false: "Waiting for per-app capture, which runs while DeadWare runs as administrator. Learning starts then."
5. No learning start yet, and DeadWare is elevated: "Waiting for per-app capture to start. Learning starts then."
6. Now is before the learning end: "Learning quietly through {(end − 1 tick) local:dddd d MMMM} · {k:N0} apps known."
7. `session.Current?.Connections?.Available` is true: "Watching for new apps · {k:N0} apps known."
8. Not elevated: "Not watching: per-app capture needs DeadWare to run as administrator · {k:N0} apps known."
9. Otherwise: "Not watching: per-app capture is not running · {k:N0} apps known."

Further rules:
- "1 app known" is used for a count of one.
- The count is never shown before Restore: state 2 covers that time, so a known-app count is never shown as 0 while unknown.
- `AlertsPageData` (`AlertsPage.cs:12`) gains optional parameters with defaults: `FirstUseStatus? FirstUse = null`, `bool Elevated = false`, `bool CaptureAvailable = false` and `IReadOnlyList<string>? IgnoredApps = null`. Fixtures can drive every state, and the positional fixture at `tests/DeadWare.App.Tests/AlertsUiChecks.cs:50` compiles unchanged.

**Don't alert list.**
- When the list is non-empty, it shows below the rules, whether or not a first network use rule exists: the heading "Not alerted, even when new again:", then one line per app, sorted by `OrdinalIgnoreCase`.
- Each line has an **Alert again** button, with the automation name "Alert again for {name}", which calls `session.UnignoreFirstUse(key)`.
- `UiSession` gains `IgnoreFirstUse(string name)` and `UnignoreFirstUse(string key)`. Each one normalizes the list, calls Configure, raises Changed and saves.

**Log row** (`Apply`, `AlertsPage.cs:234-261`) for a FirstNetworkUse episode:
- **Title:** the Summary, in Ink (never open).
- **Second line:** "{Start local:g} · first network use", then " · acknowledged" when acknowledged and " · on the Don't alert list" when the key is on the list. No peak is shown; `AlertsPage.cs:247` is skipped for this kind.
- **Buttons, right to left:**
  - **View in Connections** when Start is at most 30 days old (the retention at `ConnectionHistoryStore.cs:146`). Defined here for *The traffic strip on Connections History and Usage history*: it calls `session.RequestConnectionsHistory(minute(Start) − 5 min, minute(Start) + 10 min, Application)`, a 15-minute interval well inside that section's 30-day limit, with the episode's bare Application, never a key. When Connections no longer holds that interval (its 30-day expiry, its 256 MiB cap, a clear, or recording was off), History opens the interval as asked. It shows it by that section's rules for an interval Connections no longer keeps (the "Not kept before {RetainedFrom}" band, or hatched unrecorded minutes) and, after a clear, by the rules of *Don't record these apps and Clear history…* (the "Cleared" band and "Cleared before …" line, or Not saved slots).
  - **View in Usage history** (`UiSession.cs:35-36`) for older episodes.
  - **Don't alert for this app** when Application is set and its key is not on the list. Its automation name is "Don't alert for {name}". It adds the app and acknowledges the episode.
    - When the list already holds 256 apps, the button stays enabled and focusable, and a click changes nothing.
    - Instead, the page's action status line (`AlertsPage.cs:33`, Warning) reads "The Don't alert list holds 256 apps; remove one first."
    - A disabled button would show no tooltip (nothing in the app sets `ToolTipService.ShowOnDisabled`) and could not take keyboard focus.
  - **Acknowledge**, as today.

### Copy

- **Alerts page intro** (`AlertsPage.cs:51`) ends with: " A first network use rule logs each app the first time DeadWare sees it use the network, after a quiet 3-day learning period; apps seen within a minute share one notification."
- **Alerts page footer** (`AlertsPage.cs:65`) becomes: "Every episode is logged below whether or not notifications are on. Alerts report what was observed against rules you chose; they are not a hardware diagnosis or a security finding."
- **Log note** (`AlertsPage.cs:75`) ends with: " The first network use rule also keeps the names of apps it has seen, with the first and last minute, for up to 400 days after each was last seen."
- **Settings help** (`src/DeadWare.App/Windows.cs:176`) becomes: "Rules you set on the Alerts page notify from the tray once per episode and are always logged there, whether or not notifications are on. The first network use rule learns your apps for 3 days once per-app capture has run as administrator, then logs each new app; apps seen within a minute share one notification. Cooling Health keeps its own notification setting." The prefix that `ShellUiChecks.cs:89` finds is unchanged.
- Rule-line, editor, list and balloon strings are under Alerts page and Balloon.

### Interactions

**Don't record these apps.**
- Excluded keys are filtered on the collector thread and again in the tracker. Seeding skips them. A group drops entries that are excluded when it closes.
- When an app is added to the list, its existing known row and episodes stay until a clear, as that section's "history saved before you add an app is kept until you clear it" says. Its last seen stops advancing, so it is forgotten 400 days after its last use before the add.
- **Removal.** Defined here for *Don't record these apps and Clear history…*: an app removed from the list is unknown unless it was known before it was added. An excluded key is never learned, seeded or advanced, so an app first used while on the list, or present only in history that was seeded while it was on the list, is unknown when removed: its next use alerts after the learning period, or is learned quietly during it. An app already known when it was added stays known until a clear or 400 days after its last use before the add.

**Clear history.** *Don't record these apps and Clear history…* defines the clear command `HistoryClear(AppKey, Before, Requested)`: Requested is the confirmation time T; Before is Floor(T) − 7 days for older than 7 days and Floor(T) + 1 minute for all time; the alert log deletes in-scope episodes with start < min(Before, Requested). Defined here for that section: the first-use part of the alert worker's clear.

- **First step.** In that section's first alert-log step, the worker runs these in order, with no other work in between:
  1. Flush the work that was pending before the clear. This saves nothing of the clear itself, and it puts every pending first-use episode and its known row on disk before the delete below, so a later Flush cannot write a deleted key's row back.
  2. One transaction writes that section's `pending_clear` row and, for all time only, deletes this machine's in-scope `known_apps` rows: the one key's row, or every row. That is at most 8,192 rows, about 6.4 MiB of pages at most, inside the 8 MiB journal bound (`AlertStore.cs:15`). If the transaction fails, nothing is cleared and the tracker is unchanged; that section reports the failure.
  3. All time only: that section's `AlertEvaluator.EndScope`.
  4. All time only: `tracker.Forget(AppKey, Requested)`. The keys leave memory and the waiting group, and every later episode starts at or after Requested, so the clear's later steps never delete an episode created after this step.
  5. All time only: `knownGeneration` is incremented, so the collector posts a running app again at its next poll.
  6. That section's Flush.
- **Later steps** are that section's: the in-scope first-use episodes and their `episode_apps` links are deleted in transactions of at most 256 episodes, between other work. For all time, in-scope `known_apps` rows under other machine keys, which this session never writes, are deleted in the same steps, at most 1,024 rows a transaction. The last transaction removes the marker only.
- **Resume.** After a crash or Quit, a marker found at the next start runs step 3 (all time only) and step 6 again, because a crash can lose them, then the later steps. Steps 1–2 had committed, so the tracker restored from the file already lacks the deleted known rows; `Forget` and the generation increment are not repeated. Episodes and known rows created after the first step are kept. After a restart, new episodes start after Requested unless the clock was set back, which that section's steps accept for every store.

| Scope | `known_apps` rows deleted | First-use episodes deleted, with their links | Tracker |
|---|---|---|---|
| One app, all time | that key's row, under every machine key | that app's with start < Requested | forgets the key |
| One app, older than 7 days | none | that app's with start < Before | unchanged |
| All apps, all time | every row, under every machine key | every one with start < Requested | forgets every key |
| All apps, older than 7 days | none | every one with start < Before | unchanged |

Defined here for *Don't record these apps and Clear history…*: a clear never changes the learning start or end, the seed markers, the Don't alert list or the rule.
- After an all-apps all-time clear the known set is empty and the learning start is kept, so there is no new learning period: after the learning end each app's next network use alerts once, grouped one balloon per 60 s; during the learning period apps are learned quietly. The one-time seed does not run again.
- An older than 7 days clear forgets no app, whether or not the app has history in the last 7 days.
- An app learned in the scope between the confirmation and the first step on the alert worker, usually milliseconds, is forgotten by an all-time clear too and alerts again at its next use; its earlier episode started after Requested and is kept.

**Recording switches:** detection does not depend on Usage or Connections recording. The seed reads the stores whether or not they are recording.

**Application resources and 0.16.0 storage:** no interaction. First-use alerts never read or write process-history.db (0.15.0's version 2 schema and its GPU values are untouched). Names in Connections history are unchanged by 0.16.0's per-minute merge.

**Rollback** (0.17 to 0.16 or older):
- alerts.db stays at version 1;
- builds from 0.8.0 to 0.16 ignore the new tables;
- their retention and eviction leave orphan links, which 0.17 removes;
- they show first-use episodes as their summaries with "peak 0", because their rule lookup fails (`AlertsPage.cs:247`);
- they never re-append those episodes, because the episodes are closed;
- they drop the rule and the Don't alert list from settings.json (Docs and release).

### Errors and limits

- **Store failure:** a store exception still fails the whole alert log for the session (`AlertService.cs:177-181`), and the rule line reads "Paused: the alert log is unavailable." Keys learned since the last flush are lost; such an app alerts at its next use after a restart, at most once more.
- **Unreadable files:**
  - an alert log that is corrupt or from a newer version fails at Initialize, so nothing is learned or alerted and the learning start waits;
  - an alert log at its cap whose new tables cannot be created after one eviction and retry fails at Initialize in the same way;
  - a history store that cannot be read, or holds a clear marker, is seeded at a later start;
  - an unreadable settings.json restores defaults, which include the rule.
- **Files at the cap:** at 64 MiB, `SQLITE_FULL` evicts the oldest closed episodes with their links, first-use episodes included; their known rows stay, so those apps stay known.
- **Crash:** a crash loses up to the last 15 s of learning and episodes, like the rest of the log. A learning start not yet saved is taken again at the next capture.
- **Clock:** a wall-clock change moves nothing stored. A clock set far ahead ends learning early.
- **Threads:** detection runs on the collector thread with no I/O; the tracker, the seed and every alerts.db write run on the alert worker; the seed's reads run on each store's own read path (Sources). The UI thread only reads the volatile `FirstUseStatus` and formats text.
- **Unchanged limits:** the 64 MiB alerts.db cap, the 8 MiB journal bound, the 400-day retention, the 512-slot queue and `AlertStore.MaximumBatch` (1,024).
- **Interruptions:** an interrupted seed runs at the next start, and an interrupted clear resumes there (Clear history).
- **New limits:**
  - 8,192 known apps (the least recently seen is forgotten to make room); 256 Don't alert entries;
  - 1,024 uses per work, 1,024 episodes per Append, and 1,024 known upserts plus 1,024 deletes per Append;
  - 64 names carried per group;
  - 65,536 names per seed read.
- **No admin:** a standard user never posts work, so nothing is learned and the learning start waits.

### Privacy

- Everything stays local.
- alerts.db stores upper-case app keys and first and last minutes, for up to 400 days after the last use, plus one episode per alerted app naming it. This continues while Usage and Connections recording are paused. Removing the rule stops it.
- No path, PID, address, port or byte count is stored for this rule.
- Endpoint details stay in Connections history, under its 30-day retention, reached through **View in Connections**.
- Windows notification history can keep balloon text naming apps; DeadWare cannot clear it.
- `--diagnose` is unchanged.

### Tests

**Core** (`tests/DeadWare.Tests`, every run, on Linux and on the Windows runner):

- `FirstNetworkUseTests.App_keys_are_upper_case_normalized_names_and_pid_and_unknown_names_are_never_learnable`
  - `AppKey.Of(" Chrome.exe\u0001 ")` is "CHROME.EXE".
  - IsLearnable is false for "PID 4", "pid 4294967295", "Unknown application" and "".
  - IsLearnable is true for "PID 4.exe", "PID.exe" and a 256-character name.
  - NormalizeIgnored drops duplicates by key and names that cannot be learned, and keeps at most 256.
- `FirstNetworkUseTests.Learning_ends_at_local_midnight_that_begins_the_third_day_after_the_start_day`
  - Monday 09:00 and Monday 23:50 both end on Thursday at 00:00 local.
  - A DST change inside the period still ends at local midnight.
  - In a custom zone whose midnight is invalid, the end is 01:00.
  - The end is stored in UTC and does not move when the zone changes.
- `FirstNetworkUseTests.Balloon_text_names_one_app_lists_up_to_three_and_counts_the_rest_within_250_characters`
  - Each of the four forms is exact.
  - A 256-character name shows as 39 text elements and "…", and a name with a surrogate pair at the cut is not split.
  - With 8,192 apps, three 256-character names and an "11:59 PM" time the text is 225 characters; with 999,999 apps it is 229.
- `FirstUseTrackerTests.Apps_seen_during_learning_are_learned_quietly_and_after_learning_alert_once`
  - Learning produces no episodes.
  - The first use after the end produces one closed episode with Kind 4, Start = End = FirstSeen, Peak 0, Application, Summary and the rule id.
  - An alerted key produces no separate upsert.
  - Repeated use produces no further episode and only advances last seen.
- `FirstUseTrackerTests.Seeded_names_merge_by_key_never_alert_never_evict_and_a_failed_store_stays_unseeded`
  - "chrome.exe" and "Chrome.exe" become one key, with the earliest first and the latest last.
  - PID, Unknown, excluded and over-400-day names are skipped.
  - 8,200 names add the 8,192 with the latest last seen, and no known key is forgotten.
  - A null result leaves that store's marker unset; an empty result sets it.
- `FirstUseTrackerTests.Learning_starts_at_the_first_available_capture_and_survives_rule_switch_removal_and_re_adding`
  - The start is set by the first Observe.
  - It is unchanged by Configure with a new rule id, with the rule switched off, with no rule, or with the rule added again.
- `FirstUseTrackerTests.A_switched_off_rule_learns_quietly_and_no_rule_learns_nothing`
- `FirstUseTrackerTests.Excluded_pid_and_unknown_names_are_never_learned_or_alerted_and_dont_alert_names_are_learned_without_an_episode`
  - A key excluded from the start and later removed from the set alerts at its next use after learning.
  - A key known before it was excluded stays known after removal, with its last seen unchanged while excluded.
- `FirstUseTrackerTests.Apps_within_60_seconds_of_the_first_share_one_group_and_an_app_at_the_due_time_opens_the_next`
  - An app at 59.999 s joins the group; an app at 60 s opens the next.
  - At close, entries that are now ignored, excluded or no longer known are dropped.
  - An emptied group returns null; with the rule switched off it returns null.
  - Names are in detection order, at most 64, with a full Count.
- `FirstUseTrackerTests.Forgetting_one_key_or_every_key_makes_the_next_use_alert_keeps_the_learning_start_and_leaves_the_open_group`
  - After `Forget(key)`, the key's next use after learning gives one episode.
  - `Forget(null)` empties the known set; the learning start and end and both seed markers are unchanged, and no seed is requested.
  - The forgotten keys' entries leave the open group at once, and a key observed again in the same window is listed once.
  - Pending upserts of forgotten keys are dropped, and a pending delete stays.
  - An episode whose FirstSeen is before `notBefore` starts at `notBefore`.
- `FirstUseTrackerTests.A_full_known_list_forgets_the_app_seen_least_recently_to_make_room`
  - The 8,193rd app records one delete (the oldest last seen, ties by key) and is handled as new.
- `FirstUseTrackerTests.Apps_unseen_for_400_days_are_forgotten_at_the_next_expire_and_alert_again_in_the_same_session`
- `FirstUseTrackerTests.Changes_are_taken_in_bounded_batches_and_a_key_never_has_both_an_upsert_and_a_delete`
- `AlertRuleTests.First_network_use_rules_normalize_to_one_rule_with_no_target_and_a_fixed_description`
  - Target "", Threshold 1, Minutes 1.
  - A list keeps the first FirstNetworkUse rule only.
  - Describe and TargetName texts.
- `AlertRuleTests.Settings_add_the_first_network_use_rule_once_and_normalize_the_dont_alert_list`
  - A fresh `UserSettings` has the "first-network-use" rule and the flag true.
  - A file without the flag gets the rule appended: 63 rules become 64, a file with 64 rules gets none, and the flag is true either way.
  - A flagged file without the rule stays without it.
  - A file that already has the rule gets no second one.
  - A missing or null `FirstUseIgnoredApps`, or one with dropped entries, reads with `Error` null.
  - A Write and Read round trip keeps everything.
- `AlertRuleTests.Settings_keep_rules_and_the_notification_switch_and_default_to_none_and_on` (`tests/DeadWare.Tests/AlertRuleTests.cs:68`) changes:
  - the fresh settings hold one rule;
  - the read ids become `a, c, first-network-use`;
  - `{"Alerts":null}` (`:83`) now reads as the one added rule.
  - `tests/DeadWare.App.Tests/BatteryUiTests.cs:614` is unchanged, because the saved file carries the flag.
- `AlertEvaluatorTests.First_network_use_rules_are_never_evaluated_by_minutes_or_battery_and_budget_episodes_carry_their_application`
- `AlertStoreTests.A_version_1_file_gains_the_first_use_tables_without_a_version_change`
  - A file written with the 0.16 schema initializes.
  - The three tables exist.
  - `user_version` is still 1.
  - The `episodes` definition in sqlite_master is byte-identical to 0.16's.
- `AlertStoreTests.Initialize_at_the_cap_evicts_closed_episodes_and_retries_the_first_use_tables_once`
  - A 0.16 file filled to its fixture cap with closed episodes initializes: the three tables exist, and the oldest tenth of the closed episodes is gone.
  - The same file with only open episodes fails Initialize, and no table is added.
- `AlertStoreTests.First_use_episodes_save_closed_with_their_link_and_known_row_in_one_transaction`
  - Read fills Application.
  - Open never returns kind 4.
  - A kind-4 episode writes its known row even with no upserts in the batch.
  - An invalid known row in the batch writes neither the episode nor the row.
- `AlertStoreTests.Known_rows_upsert_min_first_and_max_last_deletes_apply_first_and_metadata_is_written_once`
  - A second learning start or seed marker is ignored.
- `AlertStoreTests.Retention_and_eviction_remove_links_and_known_rows_unseen_for_400_days_and_initialize_sweeps_orphans`
- `AlertStoreTests.Invalid_first_use_rows_are_rejected_before_any_write`
  - Rejected:
    - a key that is not `AppKey.Of` of itself, a PID key, control characters;
    - first after last, a time that is not a whole minute;
    - kind 4 without Application, kind 4 with a PID Application, kind 4 with End ≠ Start;
    - a non-normalized Application, 1,025 upserts.
  - A budget episode whose Application is "PID 4" is accepted.
- `AlertStoreTests.Eight_thousand_known_apps_of_256_cjk_characters_stay_under_8_MiB`
  - 8,192 rows, written in chunks of 1,024, with machine key "DESKTOP-ABCDEFG".
- `AlertStoreTests.Eight_thousand_alerted_apps_of_256_cjk_characters_stay_under_32_MiB`
  - Each app has an episode, a link and a known row.
- `AlertStoreTests.Retention_drops_old_episodes_and_the_cap_evicts_the_oldest_closed_ones_first` (`tests/DeadWare.Tests/AlertStoreTests.cs:68`) still passes in its 64 KiB file with the three new tables.
- `UsageHistoryStoreTests.Application_names_return_each_name_with_its_first_and_last_minute_for_this_machine_only`
- `ConnectionHistoryStoreTests.Application_names_honour_expiry_and_floor_convert_ticks_and_report_a_clear_in_progress`
  - Per-poll rows and 0.16.0 per-minute rows of one name give one row with the earliest and latest minute.
  - With that section's `metadata` clear marker present, the read returns null.

**App** (`tests/DeadWare.App.Tests`, Windows CI):

- `AlertServiceTests.Network_use_is_posted_from_the_collector_thread_once_per_app_per_minute`
  - Readings carry ConnectionSamples. Assert, through PendingObservations, a capture fake and the saved log:
    - a sample that is not Available, or a poll with no rule, posts nothing;
    - loopback-only deltas post nothing;
    - an excluded app's deltas post nothing;
    - a second poll in the same minute posts nothing new;
    - the next minute re-posts;
    - an incremented `knownGeneration` re-posts within the minute;
    - one poll with 1,100 apps posts 1,024 uses, and the rest follow.
- `AlertServiceTests.A_work_with_1024_new_apps_saves_in_batches_of_at_most_1024_and_never_fails_the_log`
- `AlertServiceTests.A_new_app_after_learning_is_logged_closed_and_one_balloon_groups_apps_within_the_window`
  - The fixture alerts.db has both seed markers and learning dates in the past; the group window is 100 ms.
  - Two new apps produce one `FirstUseGroupClosed` with Count 2 and two saved kind-4 episodes with links and known rows.
  - Nothing reaches `EpisodeStarted`.
- `AlertServiceTests.First_use_groups_skip_the_hour_floor_and_muted_notifications_still_log`
  - Two groups 1 s apart both raise.
  - With notifications off, none raise and both episodes are logged.
- `AlertServiceTests.Seeding_reads_each_store_once_retries_a_failed_store_and_skips_a_store_with_a_clear_in_progress`
  - Names from a usage fixture and a fake connection delegate are known, with no episode.
  - A throwing connection delegate leaves its marker unset, and the next start reads it.
  - With `usage-history.clear.json` present, the usage read returns null and its marker stays unset.
  - A restart after both markers does not seed again.
  - A seed interrupted by StopAsync writes nothing.
- `AlertServiceTests.Restart_keeps_known_apps_and_learning_dates_and_a_group_open_at_quit_shows_no_balloon`
- `AlertServiceTests.Clearing_an_app_forgets_it_so_its_next_poll_alerts_again_and_keeps_the_dont_alert_list` (with the clear command of *Don't record these apps and Clear history…*)
  - A one-app all-time clear deletes that key's known row in the marker's transaction, and the app's next poll after learning gives one episode.
  - An app observed again between the known-row delete and the last episode chunk keeps its new row and episode, also when its FirstSeen is before Requested.
  - An all-apps all-time clear empties `known_apps` under every machine key and keeps the learning dates and seed markers.
  - An older than 7 days clear keeps every known row and the tracker's known set.
  - A marker transaction that fails deletes no known row and forgets no key.
  - A clear resumed after a restart deletes only episodes and keeps the known rows written after its first step.
- `AlertsUiChecks` additions, run in Windows CI inside the existing STA fact (`BatteryUiTests.Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows`, `tests/DeadWare.App.Tests/BatteryUiTests.cs:87`), in the three themes:
  - each of the nine rule-line states;
  - the rule row has no Edit button;
  - the editor hides the fields for the kind and refuses a second rule with the exact text;
  - log rows show **View in Connections** at 29 days and **View in Usage history** at 31 days, and **View in Connections** raises `ConnectionsHistoryRequested` with [minute(Start) − 5 min, minute(Start) + 10 min) and the bare Application;
  - **Don't alert for this app** is absent for listed apps; with a full list, a click shows the exact action-status text and changes nothing;
  - the automation names of **Alert again** and **Don't alert for this app**;
  - the second-line texts;
  - the Not alerted list with **Alert again**, shown also when no first-use rule exists.

  The existing counts at `tests/DeadWare.App.Tests/AlertsUiChecks.cs:72-73` hold for the fixture without first-use episodes.
- `ShellUiChecks` (same fixture, `BatteryUiTests.cs:246`): the Settings help line is still found by its prefix (`tests/DeadWare.App.Tests/ShellUiChecks.cs:89`) and ends with the new text.

**Real Windows** (elevated CI runner, `[ElevatedGitHubFact]` in `tests/DeadWare.App.Tests/RealWindows/`):

- `First_network_use_sees_this_process_send_one_udp_datagram`
  - Setup: a real Collector with ETW and an AlertService on a temporary alerts.db that has both seed markers and learning dates in the past. The test sends one UDP datagram to 192.0.2.1:9 (TEST-NET-1, a documentation address).
  - Assert: within 30 s, one kind-4 episode whose Application is the test host's image name.
  - When capture is unavailable, it records `capture: unavailable` through `MeasurementRecorder` and passes.

**Pending by eye** (Windows; listed as pending in `docs/validation.md`, never claimed verified):

- the balloon's appearance and click-through;
- one balloon for three apps started within a minute;
- a real three-day learning run on an administrator install;
- the standard-user rule line.

### Docs and release

**User guide, Alerts:**
- `docs/user-guide.md:183` (the paragraph that starts "Open **Alerts** in the sidebar"): "Four kinds are available." becomes "Five kinds are available.". Add the sentence "A **first network use** rule logs each app the first time DeadWare sees it use the network (below)."
- `docs/user-guide.md:187` (the paragraph that starts "Every crossing is one **episode**"): "it never notifies more than once an hour" becomes "it never notifies more than once an hour (first network use is the exception, below)".
- A new paragraph follows `docs/user-guide.md:189` (the paragraph that starts "Device and network rules evaluate the per-minute averages"):

  "DeadWare adds one **first network use** rule for you (unless you already have 64 rules, the most it keeps; then remove one and add the rule in the editor); switch it off or remove it like any rule (it has nothing to edit). It needs per-app capture, which runs only while DeadWare runs as administrator, and waits until that has run once. It then learns quietly for three calendar days (the day capture first ran and the next two, ending at local midnight). The days count whether or not DeadWare runs as administrator during them, so the fewer apps it sees then, the more alert afterwards. Every app already in Usage history or Connections history counts as known. After that, the first time an app DeadWare does not know sends or receives traffic, one episode names it. Apps seen within 60 seconds of the first share one tray balloon, shown when that minute ends; unlike other rules these balloons are not limited to one an hour, but there is at most one a minute. Each app alerts once. **Don't alert for this app** on its log row keeps it quiet if it ever counts as new again, such as after **Clear history…**; **Alert again** under the rules undoes that. Apps are told apart only by executable name, so a different program called chrome.exe counts as known, and a program whose file name changes with each build or install counts as new each time. Loopback traffic (127.0.0.0/8 and ::1) is not network use; traffic to this PC's own network address is. Apps in **Don't record these apps** and processes DeadWare can name only by PID, such as Windows' System process, are never learned or alerted; an app you take off that list is new unless DeadWare knew it before you added it. Clearing an app's history for all time removes it from the known list, so its next use alerts again, at its next poll if it is running. Clearing all apps for all time empties the known list without a new learning period, so each app alerts once at its next use, one balloon a minute. Clearing older than 7 days keeps every app known. The Don't alert list stays. An app unseen for 400 days is forgotten, and when 8,192 apps are known the one seen least recently is forgotten to make room. The rule works whether or not Usage or Connections recording is on. Switching it off keeps learning quietly. Removing it stops learning and keeps the list, so if you add it back later, each app first used meanwhile alerts once at its next use."

**User guide, privacy** (`docs/user-guide.md:207`, the paragraph that starts "Interface totals work independently of per-process attribution") gains: "While a first network use rule exists, alerts.db keeps the upper-case executable names of apps DeadWare has seen use the network, with the first and last minute it saw each, until 400 days after the last, even while Usage and Connections recording are paused; its episodes name the app. Removing the rule stops new names being kept; **Clear history…** for all apps and all time deletes them."

**`docs/releases/v0.17.0.md`** says, in one rollback paragraph shared with the Don't record sentence of *Don't record these apps and Clear history…*:
- returning to 0.16 or older drops the first network use rule and the Don't alert list from settings.json on that build's first save, because older builds drop unknown rule kinds (`AlertRules.cs:30`) and save at every launch (`UiSession.cs:95`);
- alerts.db keeps the known apps and the learning date, and older builds show first-use episodes with "peak 0";
- reinstalling 0.17 adds the rule again once, even if it had been removed, with no new learning period;
- apps first used meanwhile alert at their next use, and apps that were on the Don't alert list are no longer on it.

**`CHANGELOG.md`**, 0.17.0 section (shared with the other 0.17.0 section): first network use alerts, the rule added once and switched on (not when 64 rules already exist), the 3-day learning period after per-app capture first runs, one balloon per 60 s, the Don't alert list, and the rollback effect above.

**`docs/validation.md`**, 0.17.0 section: the result of `First_network_use_sees_this_process_send_one_udp_datagram` on the elevated runner as recorded (or `capture: unavailable`), and the by-eye checks above as pending.

**README:** nothing from this section beyond the release's one-line mention, shared with the other 0.17.0 section.

**Release:** 0.17.0 is one PR from main after 0.16.0 has merged; the order is 0.15.0, 0.16.0, 0.17.0.

### Out of scope

- Hostnames.
- Reputation or online lookups.
- Blocking.
- "App changed" alerts (file hash or signer).
- First contact with a new country or destination.
- A timed snooze.
- A per-rule log-only mode (the global **Show tray notifications for alerts** switch mutes balloons).
- TCP connect and accept events (detection uses the Send/Recv events already captured, `src/DeadWare.App/EtwNetwork.cs:49-57`).
- Traffic that DeadWare cannot attribute to a process.
- Seeding from Application resources (process-history.db).
- A new learning period after a clear.

### Depends on

- ***The traffic strip on Connections History and Usage history* (0.16.0):** `UiSession.RequestConnectionsHistory(from, to, application)` and `ConnectionsPage.ShowHistory`, keyed from the bare name, and its rules for an interval Connections no longer keeps (the "Not kept before {RetainedFrom}" band and hatched unrecorded minutes) and for minutes not yet saved.
- ***Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0):** keeps `traffic.name` and `traffic.minute` (UTC ticks) and the meaning of `Grouped`'s expiry and floor filters, so the seed query reads per-poll and per-minute rows alike.
- ***Don't record these apps and Clear history…* (0.17.0), One app key:** `AppKey.Of`, and the `app_key` SQLite function registered on alerts.db connections too, so its clear can match `episode_apps.name`.
- ***Don't record these apps and Clear history…* (0.17.0), Settings model:** `ExcludedApps` and its normalization, passed to every `AlertService.Configure` call as `excludedApps`, with the stored set read at worker start.
- ***Don't record these apps and Clear history…* (0.17.0), Clear history…:** the alert-log clear command `HistoryClear(AppKey, Before, Requested)` and its `pending_clear` row, written in the same transaction as this section's known-row delete; its first step in the order under Interactions (Flush, marker with the known delete, `EndScope`, `Forget`, generation, Flush), its later steps deleting in-scope first-use episodes with their links and other machine keys' known rows, and its last transaction removing only the marker; resuming after `Restore` and the seed.
- ***Don't record these apps and Clear history…* (0.17.0), Clear history…:** matching every budget episode by its summary, with or without a link.
- ***Don't record these apps and Clear history…* (0.17.0), Clear history…:** its clear markers in connections.db (`metadata`) and Usage history (`usage-history.clear.json`), which `ReadApplicationNames` checks.
- ***Don't record these apps and Clear history…* (0.17.0), the traffic strip after a clear:** the Cleared band and line and the Not saved slots, which View in Connections shows for cleared time.
- ***Don't record these apps and Clear history…* (0.17.0), Copy:** the Clear history dialog's note that Windows notification history is not cleared.
- ***Don't record these apps and Clear history…* (0.17.0), Rollback and release:** its Don't record sentence in the shared rollback paragraph of `docs/releases/v0.17.0.md`.


## 0.17.0 — Don't record these apps and Clear history…

**Don't record these apps** stops saving history for the apps the user lists, while live pages keep showing them. **Clear history…** deletes saved history for one app or all apps, older than 7 days or from all time, from Connections history, Usage history, Application resources and the alert log together.

- **Other sections are named by title:** *Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0, below *Connections storage*), *The traffic strip on Connections History and Usage history* (0.16.0, below *The traffic strip*), *The Destinations table* (0.16.0) and *First network use alerts* (0.17.0, which ships in the same release). What each must supply is under Depends on.
- **0.15.0 merges first.** process-history.db is in 0.15.0's format: `user_version` 2, the GPU columns (`app_minutes.gpu_seconds`, `app_minutes.gpu_memory_seconds`, `minutes.gpu_covered`), up to 40 listed apps plus Everything else a minute (41 rows) and a 192 MiB cap (`docs/superpowers/specs/2026-11-06-per-app-gpu-design.md`, Store format). Everything this section does to Application resources includes the GPU values.
- **Anchors** are `origin/main` at 0.14.1 (`181eb54`). 0.15.0, 0.16.0 and *First network use alerts* move some of them; recheck each after 0.16.0 merges.
- **House rules** apply where this section is silent:
  - null, never zero, for an unknown value, and every "—" has a tooltip naming its reason;
  - a store that cannot be read shows as unavailable, never as an empty period;
  - status text holds fixed copy, counts and error codes, never names or paths;
  - native and storage work runs off the UI thread;
  - colours come from theme resources only;
  - everything stays local, with no new network traffic and no new administrator requirement.

**Stores.** No store changes its format version.
- connections.db, usage-history.db and process-history.db (version 2 after 0.15.0) gain no tables, columns or indexes. connections.db gains only rows in its existing `metadata` table.
- alerts.db gains one table, `pending_clear`, beside *First network use alerts*' tables, and stays at `user_version` 1 (`src/DeadWare.Core/AlertStore.cs:33`).
- While a clear is pending, Usage history and Application resources each keep one sidecar marker file (Durable marker). Usage history also keeps `usage-history.cleared.json`, which holds times only (The traffic strip after a clear or with an excluded app).

### One app key

- **`AppKey.Of(string? name)`** (Core, new):
  - applies `UsageMetrics.NormalizeName` (`src/DeadWare.Core/UsageHistory.cs:33-38`);
  - then replaces each unpaired surrogate with U+FFFD and trims the end again, as `ProcessHistory.SavedName` does (`src/DeadWare.Core/ProcessHistory.cs:11-18`);
  - and finally calls `ToUpperInvariant()`.
  - It never returns null or empty: an empty name gives the key of "Unknown application". `AppKey.Of(AppKey.Of(x))` equals `AppKey.Of(x)` for every name.
- **`AppKey.IsPidName(string? name)`** (Core, new) is true when `AppKey.Of(name)` matches `^PID [0-9]{1,10}$` (ordinal): the "PID n" name DeadWare gives a process, from its 32-bit PID, when it cannot read the image path (`src/DeadWare.App/NativeProcessIdentity.cs:15`). `AppExclusions.Accepts` and *First network use alerts*' `IsLearnable` both use it. Defined here for that section.
- **`ProcessGrouping.Key`** (`src/DeadWare.Core/ProcessTable.cs:50`) becomes `AppKey.Of`. Defined here for *The traffic strip* and *The Destinations table*.
  - The two differed only for a name holding an unpaired surrogate, which now keys by its saved U+FFFD form, and for a name whose 256-character cut ends in white space (`UsageHistory.cs:35-37`), which an executable name (at most 255 characters) never reaches.
  - Everything keyed by it follows: the Processes grouping, `ProcessMinuteFolder.Fold` (`ProcessHistory.cs:110`), the strip's C# key (its folds, `Normalize`, `Matches` and `ShowHistory`) and the Destinations table's Live grouping. Live and History therefore group apps alike.
- For every saved name, the key equals `names.upper` in process-history.db (`KeyOf`, `src/DeadWare.Core/ProcessHistoryStore.cs:295`). So "Chrome.EXE", "chrome.exe" and "CHROME.EXE" are one app in all four stores and on live pages.
- **`app_key`.** *The Destinations table* registers the deterministic SQLite function `app_key(name)` on every connections.db connection, beside `sat_sum` (`src/DeadWare.Core/ConnectionHistoryStore.cs:474`). It now computes `AppKey.Of`. The private connection opener `AlertStore.Open(bool readOnly, bool create, bool configure)` (`src/DeadWare.Core/AlertStore.cs:162-176`) registers the same function on every alerts.db connection, read-only ones included, so the alert log's clear matches `episode_apps.name`. Defined here for *First network use alerts*.
- New matching in this section never uses SQLite's `upper()` or `COLLATE NOCASE`, which fold only ASCII. One inherited exception remains: the budget re-seed reuses `ReadApplicationMinutes`, whose `COLLATE NOCASE` (`src/DeadWare.Core/UsageHistoryStore.cs:226`) misses non-ASCII case variants, as the startup seed does today.

### Settings model

Defined here for *First network use alerts*: the settings list and its normalization to app keys.

- **`UserSettings`** (`src/DeadWare.Core/Monitoring.cs:374-412`) gains `List<string> ExcludedApps`, empty by default.
- **`SettingsCodec.Read`** (`Monitoring.cs:417-453`) sets the list to `AppExclusions.Normalize(s.ExcludedApps)`. A missing or null property reads as empty. Neither that nor a dropped entry sets `Error`, as with `AlertRules.Normalize`.
- **`AppExclusions.Accepts(name)`** (Core, new) is true when both hold:
  - `ProcessHistory.SavedName(name)` is not null. So the name is not empty, not "Unknown application", not Everything else, and holds no `\`, `/` or `*`.
  - `AppKey.IsPidName(name)` is false (One app key). That is the fallback name for a process without an image path (`src/DeadWare.App/NativeProcessIdentity.cs:15`). Such processes are saved under that name and can never be excluded.
- **`AppExclusions.Normalize`** (Core, new) keeps each accepted entry whose key no earlier entry has, in saved order. It stores each entry as `SavedName(entry)` and keeps at most `AppExclusions.MaximumApps` = 64.
- **`UiSession.SetExcludedApps(IEnumerable<string>)`**, in order:
  1. Normalizes the list, and returns if it is unchanged (ordinal, in order).
  2. Passes the key set (a `FrozenSet<string>` of `AppKey.Of` values, ordinal) to the new `SetExclusions` on `ConnectionService`, `UsageHistoryService` and `ProcessHistoryService`. It passes the list to `AlertService.Configure` through *First network use alerts*' `excludedApps` parameter, with the current rules, notification switch and Don't alert list.
  3. Saves, and raises `Changed`.
- **Startup.** The `UiSession` constructor passes the loaded set to each history service right after its `Configure` (`src/DeadWare.App/UiSession.cs:69-83`). Every `Alerts.Configure` call (`UiSession.cs:76`, `:231`, `:236`) passes `Settings.ExcludedApps`. Defined here for *First network use alerts*.
  - Each history service stores the set with a volatile write, and its worker reads it once per item.
  - `AlertService.Post` drops work before `Start` (`src/DeadWare.App/AlertService.cs:94-98`), so the alert worker reads `Configure`'s stored snapshot at startup (Where recording stops).
- **`UiSession.IsExcluded(string name)`** answers the live pages and the strip from the same set.

### Settings → History and alerts

A new block goes after Connections (`src/DeadWare.App/Windows.cs:178-180`), in the same card. It holds, in order:

- **Heading** `Don't record these apps` (16), followed by its help text (Copy).
- **Add field:** an editable ComboBox (automation name `App to stop recording`, at most 260 characters) and an `Add` button. Pressing Enter in the box also adds.
- **Suggestions:** the union, by key, of three sources, in this order:
  - the apps in the current reading: `Reading.Processes` names and the `ProcessActivity` app names;
  - Usage history's names over its 400 days: `ReadApplicationsAsync(now − 400 d, now + 1 min, maxNames: 1000)` (`src/DeadWare.Core/UsageHistoryStore.cs:191-215`);
  - Application resources' names over 30 days: `TryReadApplicationsAsync`, ranked by CPU time. It gains a `limit` parameter, set here to 1,000. The service passes none today (`src/DeadWare.App/ProcessHistoryService.cs:151-168`); the store accepts up to 1,000 (`ProcessHistoryStore.cs:177-180`).

  Rules for the suggestions:
  - Each key shows the spelling of the first source that has it.
  - Names that `Accepts` rejects, and names already in the list, are left out. The rest are sorted with `StringComparer.OrdinalIgnoreCase`.
  - The two history reads run off the UI thread each time the Settings page is shown, at most once a minute. Until they return, the suggestions hold only the current reading's apps.
  - A failed read leaves its names out, and the block shows no error of its own. The service still reports its read failure in its own status, as every read does.
  - While per-app capture (administrator) and Per-process monitoring are both off, the current reading has no apps. History names and typing still work.
- **Add result:** `Add` normalizes the text. When it fails, it shows an error under the box (Warning style):
  - `Enter an app's executable name, such as chrome.exe.` when `Accepts` is false;
  - `That app is already in the list.` when the key is already present;
  - `At most 64 apps can be listed.` when the list is full.

  On success, the box clears and the list updates. A muted note, `Saved history for this app is kept. Use Clear history… to remove it.`, shows until the next add or remove.
- **The list:** one row per entry, sorted with OrdinalIgnoreCase.
  - Each row shows the saved spelling and a `Remove` button (automation name `Remove <name> from Don't record these apps`).
  - An empty list shows `No apps are excluded.`
  - Remove takes effect at once and asks for no confirmation, because it deletes nothing.
- **Clear history:** heading `Clear history` (16), its help text, the `Clear history…` button and the progress block (Progress).
- **Keyboard and theme:**
  - Tab order is the box, `Add`, then each row's `Remove`, then `Clear history…`.
  - After a remove, focus moves to the next row's `Remove`, or to the box when the list becomes empty.
  - Every brush is a theme token (`DynamicResource`).

### Where recording stops

Adding an app stops recording it from the next item each worker folds. Removing it resumes recording from the next item. Nothing folded before the change changes, including the part of the open minute folded before it: that part is saved when the minute closes.

The filters sit where each worker folds data: after the live view is built and after baselines move. So live pages keep the app, and removing it later never books earlier traffic as one spike.

- **Connections history.** Defined here for *Connections storage*: the filter is that section's merge filter (Writer hooks for 0.17.0), immediately before the merge that replaces the 0.14.1 `records.Add` (`src/DeadWare.App/ConnectionService.cs:290`).
  - A delta whose `AppKey.Of(delta.Peer.Process.Name)` is in the set is skipped. As that section states, a skipped delta is not merged and not counted as dropped, does not clear the publication's `complete` flag or its coverage, and still counts in what the merge subtracts from `buffered`, so `Held` and `buffered` stay balanced.
  - The set is read at merge time only, never at a write. A frozen unit, and so a retried write, keeps its exact payload (manifests, `src/DeadWare.Core/ConnectionHistoryStore.cs:186-193`).
  - Frozen units never change: an app excluded mid-minute still appears in the part of that minute merged before the change.
  - Unchanged: the live view (`ConnectionService.cs:139`), lookups (`:140`, `:225`) and coverage, which records capture rather than apps.
- **Usage history.**
  - `UsageMinuteFolder` gains `IReadOnlySet<string> Excluded`, owned by the worker and set before each `Observe`.
  - In `FoldApplications` (`UsageHistory.cs:138-163`), the process's baseline is stored in `next` (`:147`) and its deltas are computed as today. Then an excluded name's bytes are skipped (`continue` after `:158`). The baseline still moves, so a later removal counts only new bytes.
  - From the first publication folded after the change, closed batches carry no bytes of the app. So neither `MinuteClosed` nor the alert evaluator sees them. The minute that was open at the change keeps what was folded before it.
- **Application resources.**
  - In `ProcessMinuteFolder.Fold` (`ProcessHistory.cs:100-121`, as changed by 0.15.0), the processes of an excluded key are grouped by key as today. Their app-level values for that activity then go into `other`, not into the app's totals:
    - the CPU, I/O and memory sums;
    - the app's GPU-seconds by 0.15.0's App rows rule (the busiest engine of the members' summed per-engine use);
    - its GPU memory byte-seconds.
  - That is exactly what an unlisted app adds to Everything else at minute close (`docs/superpowers/specs/2026-11-06-per-app-gpu-design.md`, History). So the app is never ranked or listed, and every minute's totals and the card's RankTotal for every rank, GPU included, equal what they would be without the list.
  - Routing the processes to `other` one by one, like an unknown name (`:107`), would not keep that equality. 0.15.0 adds an unknown name's per-process `GpuSeconds`, and their sum can exceed the app's busiest-engine value.
- **Application traffic budgets.**
  - `AlertEvaluator.SetExcluded(IReadOnlySet<string> keys, DateTimeOffset now)` (Core, new) runs on the alert worker when a `RulesWork` (`src/DeadWare.App/AlertService.cs:18,170`) carries an excluded set that differs from the evaluator's.
  - For each ApplicationDaily rule whose `AppKey.Of(Target)` is newly excluded, it ends the open episode at `now`, returned and saved like `Replace`'s (`AlertEvaluator.cs:38-51`), and sets the day total to 0.
  - While the app is excluded, `ObserveApplication` (`:165-185`) and `SeedDay` (`:65-97`) skip the rule, so it starts no episode.
  - **Removal and re-seed.** When the app leaves the list, the service re-seeds only that rule from today's saved minutes (`ReadApplicationMinutesAsync`, as at startup, `AlertService.cs:128-136`).
    - It does this through a new overload, `SeedDay(day, rows, IReadOnlySet<string> only)`, which leaves other rules' totals untouched.
    - The worker awaits the read inside its loop, which today handles work synchronously (`:165-171`).
    - A failed read leaves the rule's total at 0, as at startup (`:136`).
    - `FiredToday` is kept, so a budget that already fired today does not fire again today.
  - At startup, the worker applies the stored set after `Replace` and `Restore` and before the seed (`AlertService.cs:122-136`). A restored open episode of an excluded rule therefore ends at the start time, and the seed skips that rule.
- **First network use.** *First network use alerts* reads the same list through `Configure`:
  - its detector on the collector thread, and its tracker, skip excluded keys before learning or alerting;
  - its one-time seed from both history stores skips them, so history saved before an app was excluded never teaches it;
  - an app removed from the list is new at its next use unless it was already known, and is learned quietly during the learning period.
- **Unchanged:**
  - device-wide history (metric minutes, capture gaps, battery, Cooling Health, the live graph cache);
  - the live pages;
  - the in-memory Processes and Network data;
  - the CSV export formats.

### "not recorded" marks

Live pages show an excluded app with the suffix ` · not recorded` after its name, in Muted style where the cell allows it. Defined here for *The Destinations table*: the Don't record key set (`UiSession.IsExcluded`), `AppKey.Of` and this suffix.

- **Network page:** the Application cell (`src/DeadWare.App/Windows.cs:310-311`). Search still matches the bare name.
- **Connections, Live mode:**
  - the application text of each live `PeerRow` built in `ApplyLive` (`src/DeadWare.App/ConnectionsPage.cs:237`), which the visible-node list label (`:282`) and the detail box (`:306`) show;
  - map hover labels (`:241-242`, and the widget's at `src/DeadWare.App/ConnectionsWidget.cs:36`);
  - the Live Destinations table's APPS cell, APPS tooltip line and spoken text, as *The Destinations table* places them.

  Search (`:315`) still matches the bare name.
- **Processes page app rows** (`src/DeadWare.App/ProcessesPage.cs:383`):
  - a new `ProcessRowView.Mark` ("not recorded" or empty) shows after the name;
  - `Name`, sorting and search are unchanged;
  - the spoken summary (`:480-486`) becomes `<name>, not recorded, <lead>, …`;
  - process rows under an app carry no mark.
- **History views** (Connections History, Usage history, Application resources and Alerts) show what was saved and carry no mark on rows. The Connections strip's application filter is the one exception (The traffic strip after a clear or with an excluded app).
- **Footers:** each of these gains one sentence (Copy):
  - the Network page footer (`Windows.cs:100`);
  - the text under Connections' Recording, origin and storage (`ConnectionsPage.cs:124`);
  - the Processes footer (`ProcessCopy.Footer`, `src/DeadWare.Core/ProcessTable.cs:15`).

### Clear history…

**Entry points.** The `Clear history…` button appears in three places:

- Settings → History and alerts;
- the Connections page's Recording, origin and storage expander (`ConnectionsPage.cs:122-124`);
- Usage history's Keep a longer record card (`src/DeadWare.App/UsageHistoryPage.cs:113-117`).

All three open the same modal `ClearHistoryWindow`, owned by the window it was opened from (the dashboard or the Usage history pop-out).

Each button is disabled while a clear is active (One at a time). `ToolTipService.ShowOnDisabled` is set, so its tooltip shows:
- `A clear is in progress.` while any store's clear is queued, running or can be retried;
- `A clear is waiting to finish; it resumes when DeadWare next starts.` when a store whose worker has stopped still holds a marker.

**Window.**

- **Size and theme:** width 480 device-independent pixels, height sized to its wrapped content, so it scales with DPI. Every brush is a theme token (`DynamicResource`), so a theme change while it is open restyles it.
- **Keyboard:**
  - Tab order is the Apps group, the app box, the Range group, `Clear history`, then `Cancel`. Arrow keys move within a radio group.
  - Esc cancels (`IsCancel`). `Clear history` is not the default button, so Enter in the box never starts a clear.
  - In the progress view, focus moves to `Close`, and Esc closes.
- **Screen readers:** the progress header has `AutomationProperties.LiveSetting` Polite.
- **Quit and sign-out:** Quit or sign-out closes the window and any open confirmation. An unconfirmed clear sends nothing.

**Choices.**

- **Apps:** `One app` (the default) or `All apps`. One app has an editable ComboBox (automation name `App to clear`), offering the list's suggestions with excluded apps included.
- **Range:** `Older than 7 days` (the default) or `All time`.
- `Clear history` is enabled only when All apps is chosen or the box's text passes `Accepts`. Once the user has typed, failing text shows `Enter an app's executable name, such as chrome.exe.` under the box.
- One app accepts the same names as the list. Rows saved as "PID n" or "Unknown application" are cleared only by All apps, and the guide says so.

**Boundary.** When the user answers Yes, the UI takes T = UTC now and builds `HistoryClear(string? AppKey, DateTimeOffset Before, DateTimeOffset Requested)` (Core, new), with `Requested` = T.
- `AppKey` is `AppKey.Of` of the box's text, or null for all apps.
- The confirmation names the app by `SavedName` of that text.
- `Before` depends on the range:
  - **Older than 7 days:** `Before = Floor(T) − 7 days` (`UsageMetrics.Floor`, `UsageHistory.cs:26-27`). This covers every saved minute that started more than 7 × 24 hours before the minute of the confirmation.
  - **All time:** `Before = Floor(T) + 1 minute`. This covers everything saved, including the minute in progress. For the chosen apps, the rest of that minute is not saved either (Boundary rule), so the clear can lose up to 60 s of new history for them.

Each store clears in-scope rows whose minute, or whose episode start, is earlier than `Before`. The alert log uses `min(Before, Requested)` instead (Alert log). Usage history, Application resources and the alert log are keyed by machine, and each clears every machine key in its file, not only the current one. connections.db has no machine key.

**What it clears and keeps**, as the dialog states it (Copy):

| Store | One app | All apps |
|---|---|---|
| Connections history (connections.db) | The app's traffic rows, their dedup receipts and any manifest left without receipts. Coverage stays. | Every traffic and coverage row, with receipts and manifests. `expiry` hides them at once. |
| Usage history (usage-history.db) | The app's rows in `application_minutes`, in every spelling. | Every row in `application_minutes`. |
| Application resources (process-history.db) | In each minute, the app's row, CPU, I/O, memory and GPU included, is added into that minute's Everything else row, then deleted. The minute stays, so its totals stay true. | Whole minutes (`minutes` and `app_minutes`), GPU values included. |
| Alert log (alerts.db) | The app's application-budget and first-use episodes. All time also removes the app from the known apps. | Every application-budget and first-use episode. All time also empties the known apps; the learning start is kept. |

The clear keeps:

- device-wide history (`metric_minutes`, `capture_gaps`, battery, Cooling Health and the live graph cache);
- device-metric, network-rate and battery episodes;
- the alert rules, the Don't record list and the Don't alert entries.

It cannot reach the CSV files the user exported, or Windows notification history, which can keep alert balloons that name apps. Defined here for *First network use alerts*: the dialog says so (Copy).

**Confirmation.** `MessageBox.Show(owner, text, "DeadWare", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)`, through an internal `Confirm` hook as End task uses (`src/DeadWare.App/ProcessActions.cs:182-184`, `ProcessesPage.cs:99`). The four texts are under Copy. Answering No sends nothing.

**Progress.**

- After Yes, `UiSession.ClearHistory(HistoryClear)` posts the request to the four services, and the dialog switches to the progress block. Settings shows the same block whenever any store has a clear or a result.
- **Lines:** one per store, in this order: Connections history, Usage history, Application resources, Alert log. Each reads one of:
  - `waiting`;
  - `clearing (37 %)`;
  - `done`;
  - `failed (IOException); it resumes when DeadWare next starts`, when the store's marker is saved;
  - `failed (IOException); not cleared`, when the marker could not be saved, or an invalid marker was deleted at startup;
  - `unavailable (IOException); not cleared`, when the store is unavailable (Shared rules).

  For a clear resumed at startup, only the stores that hold a marker get a line.
- **Percent:** ⌊100 × done ÷ total⌋, capped at 99 until the store is done. The alert log shows no percent.
- **Header:**
  - `Clearing history…`;
  - `Finishing a clear that was interrupted…`, when a marker resumed at startup;
  - `History cleared.`, followed by the freed-space line;
  - `History partly cleared.`, followed by:
    - `<n> of 4 finished.`;
    - ` Failed histories resume when DeadWare next starts.`, when any line resumes;
    - ` Histories marked not cleared need Clear history again.`, when any line reads not cleared.

    Both sentences can appear. For a resumed clear, `4` is the number of lines.
- **Buttons:**
  - `Try again` appears when a store failed while its worker is still running, whether or not its marker was saved. It re-posts that store's kept request.
  - `Close` closes the dialog. The clear continues.
- The block refreshes on `UiSession.Changed`, which the session timer raises every second (`UiSession.cs:84-89`).
- The last result stays until a new clear starts or DeadWare quits.
- **Re-reading pages.** Defined here for *The traffic strip* and *The Destinations table*. The session timer raises `UiSession.HistoryClearChanged(HistoryClearStore)` on the UI thread when a store's `ClearState` becomes Clearing and when it leaves Clearing (Done or Failed). `HistoryClearStore` is Connections, Usage, ProcessHistory or Alerts. If the page that store feeds is open, it re-reads:
  - Connections history: `ConnectionsPage.ReloadHistory()` (*The traffic strip*), which re-reads the shown History view, its strip and its Destinations table, with the minute cursor on or off. The re-read when the clear starts drops the rows `BeginClear` hid at once. Live is unaffected.
  - Usage history and Application resources: Usage history's `RefreshAsync` (`UsageHistoryPage.cs:205-221`), which re-reads the strip, the Applications table, a listed slot and the Application resources card.
  - Alert log: the Alerts page's log.
- **Service state:** each service exposes `HistoryClearState ClearState`, with these members:
  - `Phase`: None, Queued, Clearing, Done, Failed or Unavailable;
  - `Percent` (int?);
  - `Error`: the exception type's name, or null;
  - `MarkerSaved` (bool);
  - `Resumed` (bool).
- **Status suffixes:** each service's status gains a suffix. The statuses are `ConnectionService.StorageStatus` (`ConnectionService.cs:101`), `UsageHistoryService.Status`, `ProcessHistoryService.Status` and `AlertService.Status`. The suffixes:
  - while clearing: ` · Clearing history (37 %).`, or ` · Clearing history.` for the alert log;
  - after a failure with the marker saved: ` · Clearing history failed (IOException); it resumes when DeadWare next starts.`;
  - after a failure without it: ` · Clearing history failed (IOException); not cleared.`

### How each store clears

**Shared rules.**

- **One at a time.** `UiSession.ClearHistory` refuses a request while any store's `ClearState` is Queued or Clearing, or Failed with `MarkerSaved`. The UI never offers one in that state.
- **Queued command on the owning worker.**
  - Each service gains a command channel of capacity 1 beside its data queue. The worker takes a command before its next data item.
  - While a clear is active, the worker alternates between two things:
    - It handles queued data as today, with its per-loop limits and flush timer unchanged.
    - It runs one clear step. A step runs delete transactions until 250 ms have passed on the monotonic clock (always at least one transaction, unless it yields), then returns.
  - The alert log follows the same rule (Alert log).
  - A step never starts after `StopAsync`. Quit and sign-out wait only for the step in progress (at most 250 ms plus one transaction), never for the rest of the clear. That keeps them inside Quit's 20 s history deadline and sign-out's 3 s deadline, in which Connections gets 1.5 s for its final drain (`UiSession.cs:300`, `:346`).
  - Restart as administrator and an update install quit the same way (`UiSession.cs:426-439` for Restart as administrator; `src/DeadWare.App/UpdatesPage.cs:97` for an update install). The new instance resumes the clear.
- **Durable marker first.**
  - Before it saves or deletes anything, the worker records the request:
    - Connections history: a row in connections.db's `metadata` table;
    - the alert log: a `pending_clear` row in alerts.db;
    - Usage history and Application resources: a sidecar file each.
  - The one exception: the alert log first flushes the work pending before the request (Alert log, First step 1). That saves nothing of the clear.
  - If the marker cannot be saved (SQLITE_FULL at the cap, a full disk or access denied), nothing in that store is cleared. It reports Failed with `MarkerSaved` false.
  - The marker is removed in, or right after, the last delete transaction.
  - **Resume.** A store that holds a marker at startup resumes the clear after `Initialize`, with `Resumed` true. Application resources resumes after its start-up expiry; the alert log resumes after `Restore` and the seed. A store that cannot open keeps its marker, and resumes once it opens again.
  - A request is never merged with another. A clear never depends on the current Don't record list. The request carries `AppKey`, `Before` and `Requested`. Each marker stores what its store uses: Connections history stores `AppKey` and `Before`; the others store all three.
- **Clear in progress.** Defined here for *First network use alerts*: `ConnectionHistoryStore.PendingClear` is non-null, and the new `UsageHistoryStore.ClearPending` is true, from the marker's write until its removal, and from `Initialize` while a marker exists. That section's `ReadApplicationNames` on each store returns null while it is set.
- **Flush first.**
  - **All time only:** after writing the marker, the worker removes the scope from its open minute, which is not yet a batch. That is the open minute for Usage history and Application resources, and the open units of *Connections storage*'s folder for Connections history.
  - For Older than 7 days, the open minute is newer than `Before` and is left alone.
  - The worker then saves every closed batch it holds, and only then deletes. Saving first means a retried write can never add deleted rows back.
- **Boundary rule (All time only).**
  - The two folders gain `HistoryClear? Boundary`, set and cleared by their worker. The connection writer holds the same value beside its exclusion set, and *Connections storage*'s merge filter applies it.
  - The rule lasts until the worker folds a minute at or after `Before`, or for at most 120 s on the monotonic clock after the command started, whichever comes first. A clear resumed at startup whose `Before` is still ahead of now installs the rule again, with the same cap.
  - Meanwhile, the worker treats in-scope data from minutes before `Before` as excluded:
    - Connections history and Usage history drop it.
    - Application resources folds one app into Everything else (as an excluded app, GPU included), and drops whole minutes for all apps.
  - Dropped data is not counted as dropped observations or as a capture gap.
  - The 120 s cap stops this in-memory rule from discarding new history after a backwards clock change. The all-apps `expiry` raise in Connections history is permanent, though (Errors and limits).
- **secure_delete.**
  - While a store holds a clear marker, every write connection it opens runs `PRAGMA secure_delete=ON`. Deleted content is then overwritten with zeros on every path that removes it: the clear's steps, Connections' routine purge, or eviction at the cap.
  - The bundled SQLite supports this: version 3.53.4, from `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5. It is built without `SQLITE_SECURE_DELETE`, so the pragma is off by default.
  - With secure_delete on, every freed page is written. So while a marker exists:
    - Usage history skips the retention `Expire` inside `Append` (`UsageHistoryStore.cs:113`, `:287-292`).
    - Application resources skips `ExpireByDay` at start and on a UTC day change (`ProcessHistoryService.cs:86`, `:129`), and inside `Append` (`ProcessHistoryStore.cs:111`), through a store flag set while the marker exists.

    Each of those deletes a whole day in one transaction. Application resources' reads already ignore expired minutes (`ProcessHistoryStore.cs:185-186`). Both stores expire normally once the marker is gone; Application resources calls `Expire` right after removing it.
  - Whole-day eviction on SQLITE_FULL (`EvictOldestDay`) still runs. With secure_delete on, it can write about one day of pages in one transaction: about 5.6 MiB at 0.15.0's worst case for Application resources (41 rows a minute with both GPU columns).
  - Each transaction is followed by `PRAGMA wal_checkpoint(TRUNCATE)`.
- **Yield to readers.**
  - Before each delete transaction, a step checks the store's `-wal` size. If it is over 4 MiB after a TRUNCATE checkpoint attempt (a reader is holding it), the step ends early without failing, and the next step tries again.
  - After 60 s of yielding without a transaction, the step fails with IOException.
  - This matters most outside Connections. Usage history, Application resources and the alert log check the journal only when a write connection opens (`UsageHistoryStore.cs:358-363`). Their workers also stop on any `Append` failure (`UsageHistoryService.cs:135-139`, `ProcessHistoryService.cs:142-147`). So a clear that grew the WAL past 8 MiB behind a reader would stop recording at the next save.
  - Connections' own reads are serialized under the store's lock (`ConnectionHistoryStore.cs:336`), so only an outside reader can hold its WAL. The same rule applies there.
- **No VACUUM, no `VACUUM INTO` and no file recreation.** Freed pages stay in the file and are reused.
  - VACUUM would write the whole database through the WAL, past the 8 MiB journal bound (`ConnectionHistoryStore.cs:13`, `UsageHistoryStore.cs:27`, `ProcessHistoryStore.cs:34`, `AlertStore.cs:15`).
  - Deleting and recreating a file would leave its old clusters unwiped and would skip secure_delete.
- **Chunks.** This SQLite build has no `DELETE … LIMIT` (`SQLITE_ENABLE_UPDATE_DELETE_LIMIT` is off). So each chunk deletes by key, with `WHERE (key columns) IN (SELECT … ORDER BY … LIMIT n)`.
- **Failure.**
  - A step's exception ends that store's clear as Failed, with the exception type's name and `MarkerSaved` true. The worker keeps recording, and `Try again` re-posts the kept request.
  - A failure in the flush is the worker's own failure, handled as today:
    - the usage and process workers stop (`UsageHistoryService.cs:135-139`, `ProcessHistoryService.cs:142-147`);
    - Connections keeps its unsent units for their normal retry.

    The clear then shows Failed, and its marker resumes at the next start.
  - Connections' write attempt catches `Append`'s exception itself, so the clear reads *Connections storage*'s `ConnectionFlushResult` instead.
- **Unavailable.**
  - A store is unavailable in two cases:
    - for Usage history, Application resources and the alert log, when its worker has stopped after a failure (their catch blocks);
    - for Connections history, when its initialization failed, even though its writer keeps running (`ConnectionService.cs:233-235`).
  - The services' `Failed` flag is not the test. Usage history and Application resources also set it after a failed read while their workers run (`UsageHistoryService.cs:141-153`, `ProcessHistoryService.cs:156-168`).
  - An unavailable store reports Unavailable with the type of its failure. Nothing is written for the request, and nothing resumes for it.

**Connections history** (`ConnectionHistoryStore` and the `ConnectionService` writer). Defined here for *Connections storage*: the marker, the writer command's order and the one-app hide predicate.

- **Marker.**
  - The `metadata` table (`ConnectionHistoryStore.cs:37`) has `value INTEGER NOT NULL`, so the key carries the app:
    - `('clear-app:<APP KEY>', Before ticks)` for one app;
    - `('clear-all', Before ticks)` for all apps.
  - At most one row has a key that starts with `clear-`. `Initialize` reads it into `PendingClear`.
  - It needs no schema change. `ReadSchema` compares only `sqlite_master` (`:79`), and 0.15 and 0.16 read only `floor` and `expiry` by key, so a file holding the row opens in both and they ignore it. A clear interrupted by a downgrade resumes when 0.17 starts again.
- **`BeginClear(HistoryClear)`** commits, in one bounded transaction (`CommitBounded`, `:257-265`):
  - the marker;
  - for all apps, `expiry = max(expiry, Before)` (the statement at `:165`). Every read path then hides the rows at once, and earlier builds hide them too;
  - the cleared mark `cleared-all` or `cleared-one`, set to the larger of its value and `Before` (The traffic strip after a clear or with an excluded app).
- **One app, hidden at once.**
  - *Connections storage*'s `GroupedWhere` (the 0.14.1 `Grouped` WHERE, `:29-32`) gains `AND NOT ($clearKey<>'' AND minute<$clearBefore AND app_key(name)=$clearKey)`. That reaches `Read`'s `temp.geo` and `temp.grouped` builds, so every reader of them (rows, summary, markers, the strip and the Destinations table), and `ExportCsv` (`:437-454`).
  - The earliest-retained query (`:371-379`) gains the same predicate on its traffic branch.
  - `$clearKey` is '' when no one-app marker exists.
  - The values come from the store's in-memory copy of the marker, read under its lock.
- **One-app step.**
  - A cursor on (minute, id) starts before the first row. Each round reads the next 2,048 rows with `minute < Before` in `(minute, id)` order, through the `traffic_time` index.
  - Of those rows, the ones whose `app_key(name)` matches are deleted in transactions of at most 512 rows. Each transaction deletes the rows, their `dedup` receipts and any `manifests` row of a touched batch left without receipts, as `Cleanup` does (`:316-322`). A round with no match writes nothing.
  - On `TransactionBudgetException`, the transaction size halves, and stays halved for the rest of the clear. At 1 row, the step throws IOException "Connection history clear exceeds the bounded transaction budget."
  - `GuardJournal` (`:494-500`) and the yield check run before each transaction.
  - The cursor lives in memory. A resumed clear starts again from the first row, which is correct because the predicate does not change.
  - The percent's total is `count(*)` of traffic rows with `minute < Before`, taken at the first step. `done` counts the rows scanned.
- **All-apps step.**
  - It deletes traffic rows with `minute < Before` first, then the coverage rows that `Cleanup`'s coverage rule now selects (`:305-313`).
  - It works in transactions of at most 512 rows, deleting receipts and manifests with the rows, and halves on the budget exception as above.
  - The percent's total is the count of those rows, taken at the first step. `done` also counts the rows the writer's routine purge deletes.
- **End.** The last transaction, which finds no rows left, deletes the marker. The cleared mark stays.
- **Routine cleanup becomes bounded.**
  - `Append` (`:131-185`) gains `int maintenanceTransactions = int.MaxValue`. One Append then purges at most that many `Cleanup` transactions of at most 64 rows each.
  - The service passes:
    - 64 from the writer's write attempts, including the tick's maintenance call;
    - 64 from `InitializeAsync`'s empty Append at every start (`ConnectionService.cs:28`). Otherwise an interrupted all-apps clear would purge its whole backlog before the writer loop starts, while `ReadViewAsync` waits on it (`:34`);
    - 0 from every `Append` of the final write on Quit or sign-out (*Connections storage*'s R6), so the shutdown deadlines never wait for the purge.
  - A backlog left by a raised `expiry` drains over later Appends and the clear's steps.
  - Direct callers and the existing store tests keep today's unbounded purge (`tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs:300-303`).
- **Full file.**
  - On SQLITE_FULL, `WriteChunk` (`:231-235`) first purges rows already hidden by `expiry`, one maintenance transaction (`Cleanup`'s statements, at most 64 rows) at a time, and retries the chunk after each.
  - It calls `EvictOldestBucket` (`:266-284`) only when no hidden row is left. `EvictOldestBucket` ignores rows below `expiry` (`:270-279`), so without this order a hidden backlog would cost visible history.
- **Writer order for a request.** The clear is a writer command:
  1. `BeginClear`.
  2. All time only: `ConnectionMinuteFolder.Forget(AppKey)` (*Connections storage*; null for all apps) removes the scope's entries from every open unit. Coverage runs stay, and frozen units are untouched.
  3. A write attempt. If its `ConnectionFlushResult.Saved` is false, the clear is Failed with its `FailureType` and `MarkerSaved` true, the unsent units keep their normal retry, and `Try again` or the next start runs the clear again.
  4. Every unit still held by the clock check that holds an in-scope record is retired whole, with `Dropped += max(1, Observations)`, as *Connections storage* defines. Such a unit is never trimmed: sent after the delete, it would add its rows back.
  5. The steps.

**Usage history** (`UsageHistoryStore` and the `UsageHistoryService` worker).

- **Marker:** `usage-history.clear.json`, beside the database: `{"Version":1,"AppKey":<string or null>,"Before":<ISO 8601 UTC>,"Requested":<ISO 8601 UTC>}`.
  - It is written to `usage-history.clear.json.tmp`, flushed to disk with `FileStream.Flush(true)`, then moved over the final name with `File.Move(…, overwrite: true)`. A leftover `.tmp` found at startup is deleted.
  - It is deleted after the last delete commits, right after the cleared marks are written (The traffic strip after a clear or with an excluded app).
  - A marker with invalid content found at startup (bad JSON, another `Version`, invalid fields) is deleted. The clear reports Failed (InvalidDataException) with `MarkerSaved` false and `Resumed` true, and nothing is deleted for it.
  - A marker that cannot be read (IOException) is kept, and resumes at the next start.
  - The store's minutes are Unix milliseconds (`UsageHistoryStore.cs:116`), so `Before` is compared in milliseconds.
- **Order:**
  1. Write the marker.
  2. All time only: remove the scope's names from the folder's open minute with `UsageMinuteFolder.Forget(string? appKey)` (new). Baselines are unchanged.
  3. `Flush()` (`UsageHistoryService.cs:89-94`).
  4. Run the steps.
- **One app:**
  - For each machine, the distinct stored names are read once (`SELECT DISTINCT name FROM application_minutes WHERE machine=$m`) and matched in C# with `AppKey.Of`.
  - For each matching name, transactions of `DELETE FROM application_minutes WHERE machine=$m AND name=$name AND minute IN (SELECT minute FROM application_minutes WHERE machine=$m AND name=$name AND minute<$before ORDER BY minute LIMIT 512)`. This uses the primary-key prefix (`UsageHistoryStore.cs:59-62`).
  - At most 512 rows per transaction, because `application_minutes_time` indexes (machine, minute, name) (`:63`; the WITHOUT ROWID key completes the index). One app's entries sit among every other app's entries for the same minute, so each deleted row can dirty its own index leaf page. 4,096 consecutive minutes of one always-on app would dirty about 45 index pages per app recorded in a minute: about 7 MiB with 40 apps. 512 rows dirty at most about 1,030 leaf pages (about 4 MiB).
- **All apps:**
  - For each machine, `DELETE FROM application_minutes WHERE (machine,name,minute) IN (SELECT machine,name,minute FROM application_minutes WHERE machine=$m AND minute<$before ORDER BY minute LIMIT 4096)`, through the `application_minutes_time` index.
  - Time-ordered deletes are contiguous in the time index, and touch about two table pages per app in the span: about 0.6 MiB with 50 apps a minute.
  - A minute holding more than about 500 names could exceed the bound. The store accepts up to 4,096 rows a batch (`:25`), and its routine one-day `Expire` (`:287-292`) already can.
- **Limits:**
  - A checkpoint and the yield check follow each transaction.
  - `metric_minutes` and `capture_gaps` are never touched.
  - The percent's total is the scope's row count, taken at the first step.

**Application resources** (`ProcessHistoryStore` and the `ProcessHistoryService` worker; process-history.db version 2).

- **Marker:** `process-history.clear.json`, with the same format and handling as Usage history's. Minutes are Unix milliseconds here too.
- **Order:**
  1. Write the marker.
  2. All time only, handle the open minute:
     - one app: move its app-level totals, GPU-seconds and GPU memory byte-seconds included, into `other` with `ProcessMinuteFolder.Forget(string appKey)` (new);
     - all apps: discard the open minute if it started before `Before`, with `ProcessMinuteFolder.Discard()` (new).
  3. `Flush()`.
  4. Run the steps.

  For all apps, closed batches with `Minute < Before` that arrive during the boundary window are discarded, not saved.
- **Windows:**
  - The clear works in 6-hour UTC windows, from the oldest saved minute up to `Before`. It runs one transaction per window and machine, so each transaction covers at most 360 minutes. It loops over machines itself, because `DeleteMinutes`' own loop would put every machine in one transaction (`ProcessHistoryStore.cs:315-326`).
  - A day per transaction, as `ExpireByDay` does (`:336-348`), would come close to the 8 MiB journal bound with secure_delete on, because every freed page is written: a whole day at 0.15.0's 41 rows a minute (40 listed apps plus Everything else) is about 5.6 MiB.
- **One app**, found by `names.upper` = key (if there is no such row, there is nothing to do). In each window:
  1. `UPDATE app_minutes AS e SET cpu=e.cpu+a.cpu, io_read=e.io_read+a.io_read, io_write=e.io_write+a.io_write, memory_seconds=e.memory_seconds+a.memory_seconds, gpu_seconds=<sum>, gpu_memory_seconds=<sum> FROM app_minutes AS a WHERE a.machine=e.machine AND a.minute=e.minute AND e.everything_else=1 AND a.name_id=$id AND a.minute>=$from AND a.minute<$to`. Each `<sum>` is null when both values are null; otherwise it is their sum, with null counted as 0. Everything else's GPU is a sum across apps, so it can exceed 100 %, as 0.15.0 already allows.
  2. For a minute without an Everything else row, insert one holding the app's values, under the name id of the reserved key (`INSERT OR IGNORE` into `names`, as `Append` does, `:140-142`). Every minute saved by `Close` has an Everything else row, so this is defensive.
  3. Delete the app's rows in the window.

  After the last window, the app's `names` row is deleted when no `app_minutes` row refers to it. Minutes, `covered`, `gpu_covered` and RankTotal are unchanged.
- **All apps:** in each window and machine, the clear runs `DeleteMinutes`' two statements (`:323-324`) for that machine with `minute < min(window end, Before)`. They remove `minutes` rows with their `gpu_covered` and `app_minutes` rows with their GPU columns. After the last window, `DeleteOrphanedNames` runs (`:329-330`).
- **Percent:** the total is the number of windows times machines, taken at the first step.

**Alert log** (`AlertStore` and the `AlertService` worker). Defined here for *First network use alerts*: the alert worker's clear command, its marker in its own table holding the app key as text, and the matching of older budget episodes by summary.

- **Table:**
  - `AlertStore.Initialize` (`AlertStore.cs:28-49`) also runs `CREATE TABLE IF NOT EXISTS pending_clear(id INTEGER PRIMARY KEY CHECK(id=1), app_key TEXT, before INTEGER NOT NULL, requested INTEGER NOT NULL)`, in the same transaction (`:36-47`) as *First network use alerts*' tables. `app_key` holds the key as text, or null for all apps; `before` and `requested` are UTC Unix milliseconds, as in `episodes`. `user_version` stays 1, and earlier builds ignore the table.
  - `Initialize` sets `max_page_count` before its CREATE (`:34-46`). New tables need pages, so at the 64 MiB cap the transaction can fail with SQLITE_FULL, which would make the whole log unavailable on upgrade.
  - On SQLITE_FULL there, `Initialize` rolls the transaction back, drops the oldest tenth of the closed episodes (`EvictOldestClosed`, `:134-143`) and retries once, as `Append` does (`:82-86`). The eviction also deletes the evicted episodes' `episode_apps` rows when that table exists; a 0.16 file has none yet. Because *First network use alerts*' tables are created in the same transaction, the retry covers them too. A second SQLITE_FULL fails `Initialize` as today.
- **The command.** The alert worker takes the clear from its command channel (Shared rules). Its scope is one app key or all apps, its range is `Before`, and its cutoff is `min(Before, Requested)`.
- **In scope** are closed episodes (`finish IS NOT NULL`) with `start < min(Before, Requested)`, on every machine key, of two kinds:
  - **First-use episodes**, found through *First network use alerts*' `episode_apps` link: for one app, links whose `app_key(name)` equals the key; for all apps, any link.
  - **ApplicationDaily episodes** (kind 2). The app is the text before the last " ≥ " of a summary that ends " in a day" (`AlertRules.Describe`, `src/DeadWare.Core/AlertRules.cs:111`), matched with `AppKey.Of`. This also covers episodes saved before 0.17.0, which have no link.

  Using `Requested` keeps any episode that starts after the confirmation, inside the same minute. An example is the first-use alert of a cleared app's next network use. Device-metric, network-rate and battery episodes are never touched.
- **First step**, in order, with no other work in between:
  1. `Flush()` (`AlertService.cs:137-142`), saving the work pending before the request. This saves nothing of the clear itself. It puts every pending first-use episode and its known row on disk before the delete below, so a later Flush cannot write a deleted key's row back (*First network use alerts*, Interactions).
  2. One transaction writes the `pending_clear` row and, for All time only, deletes the current machine's in-scope `known_apps` rows: the key's row for one app, every row for all apps. That is at most 8,192 rows, about 6.4 MiB of pages, inside the 8 MiB journal bound (`AlertStore.cs:15`), as *First network use alerts* measured it. Older than 7 days deletes no `known_apps` row.
  3. All time only: `AlertEvaluator.EndScope(string? appKey, DateTimeOffset at, bool resetDay)` (Core, new), with `at` = `Requested` and `resetDay` true. It ends each open in-scope ApplicationDaily episode at `at` and forgets it. Otherwise a later minute could update its peak (`AlertEvaluator.cs:179`) and upsert the deleted row back (`AlertStore.cs:69-77`). It sets those rules' day totals to 0 and keeps `FiredToday`. An Older than 7 days clear has no open in-scope budget episode, because an open daily episode started today.
  4. All time only: *First network use alerts*' `tracker.Forget(AppKey, Requested)`, which removes the key, or every key, from the in-memory known set together with its pending upserts, and removes their entries from the waiting balloon group at once.
  5. All time only: the `knownGeneration` increment.
  6. `Flush()`.

  Flushing first means no pending row of a cleared key is written after the delete. Writing the marker next means a crash after it resumes the clear. Deleting the known rows in the marker's transaction, after the pending work is flushed and before any later work, means a known row written after the clear began, such as that of a cleared app used again, is kept.
- **Later steps** delete the in-scope episodes, with their `episode_apps` rows, in transactions of at most 256 episodes. For All time they then delete other machine keys' in-scope `known_apps` rows, at most 1,024 rows a transaction.
- **Last transaction:** deletes the marker.
- **Resume.** A marker found at startup resumes after `Restore` and the seed. It runs steps 3 and 6 of the first step (`EndScope` for All time, and `Flush`) again, because a crash can lose them, then the later steps. The current machine's known rows went with the marker's transaction, so the tracker restored from the file already lacks them.
- **Known apps** (*First network use alerts*' `known_apps`):
  - One app, All time: that key's row is deleted, so the app's next network use alerts again.
  - All apps, All time: every row is deleted.
    - The learning start is kept. After the learning period, each app's next network use alerts again, grouped into one balloon per 60 s. During the learning period, apps are learned quietly.
    - The one-time seed from history does not run again.
  - Older than 7 days, one app or all apps: unchanged, because the apps' last 7 days of history remain. No key is forgotten.
  - Don't alert entries, the learning start and end, the seed markers and the rule are never touched.
- **Acknowledge:** `Acknowledge` (`AlertStore.cs:121-128`) is an UPDATE, so acknowledging a deleted episode changes nothing and returns false. The Alerts page re-reads when the clear finishes (Re-reading pages).

### The traffic strip after a clear or with an excluded app

Defined here for *The traffic strip*: how cleared time and an excluded application filter are drawn and worded. A one-app clear leaves Connections coverage and Usage history's metric minutes, an all-apps clear raises Connections' `expiry`, and an excluded app's minutes keep their coverage. Without these rules, the strip would draw that time as known zeros or as "Not kept".

- **Cleared marks.** A clear leaves times only, never an app name or key.
  - **connections.db:** `metadata` rows `('cleared-all', ticks)` and `('cleared-one', ticks)`, each the latest `Before` of an all-apps or a one-app clear. `BeginClear` writes the matching one. They stay after the clear, and a later clear raises them. 0.15 and 0.16 ignore them.
  - **Usage history:** `usage-history.cleared.json`, beside the database: `{"Version":1,"AllAppsBefore":<ISO 8601 UTC or null>,"OneAppBefore":<ISO 8601 UTC or null>}`, each the latest `Before` of that kind.
    - The clear's last step writes it, each value the larger of the old and the new, just before deleting the marker, with the marker's `.tmp`, `Flush(true)` and move.
    - `Initialize` reads it. A missing, invalid or unreadable file reads as no clear, and the next finished clear replaces it.
  - Application resources and the alert log keep none. A one-app clear leaves every minute's totals in Everything else, and an all-apps clear removes whole minutes, which the card already draws as gaps.
- **`TrafficStrip`** gains, trailing and optional, `bool RetainedByClear = false` and `DateTimeOffset? AppClearedBefore = null`.
  - **Connections.** The strip's retained-start step in `Read` also reads `cleared-all` and `cleared-one`, in the same read transaction as `floor` and `expiry`. `RetainedByClear` is true when `RetainedFrom` is set, `CapacityShortened` is false and `cleared-all` equals `expiry`, so the retained start is the clear's. Once the 30-day expiry passes it, `expiry` is larger and the strip says "Not kept" again.
  - **Usage history.** `ReadApplicationSeries` counts no minute before `AllAppsBefore` in any slot. `RetainedFrom` is then the first whole minute at or after `AllAppsBefore` when that is after `from`, and `RetainedByClear` is true. Usage history sets `RetainedFrom` only this way.
  - **Both.** `AppClearedBefore` is the one-app time (`cleared-one` or `OneAppBefore`) when it is after the origin and after `RetainedFrom`; otherwise null.
- **Drawing.**
  - With `RetainedByClear`, the Not kept band (*The traffic strip*, Drawing) is labelled `Cleared`, and the line under the legend reads `Cleared before {RetainedFrom in local time, "g"}` instead of "Not kept before …". On Usage history the band is drawn as on Connections.
  - On both pages the retained start (*The traffic strip*, Slot states) is `RetainedFrom` when it is set, so no minute inside the Cleared band is hatched.
  - With `AppClearedBefore` set, the line under the legend reads, or after another line adds, `Some application history before {AppClearedBefore in local time, "g"} was cleared.`
  - **Not saved** is a new slot state on both strips, checked just before No traffic. A slot is Not saved when its `RecordedSeconds` > 0, its measure bytes are 0, and either it starts before `AppClearedBefore`, with or without an application filter, or, on Connections, the application filter's key is in the Don't record set.
    - It is hatched over the full plot height, and stays a hover and keyboard target, as Usage history's No per-application capture slot does.
    - Its chip has no value line. Its status lines are `No saved traffic`, then `Some application history before {g} was cleared` or `Not recorded: in Don't record these apps`, or both when both apply.
    - When it is hovered or selected, the legend's values show "—", with the chip's reason line as the tooltip.
- **An excluded application filter.** While the filter's key is in the Don't record set (`UiSession.IsExcluded`, checked again on `UiSession.Changed`):
  - the filter button reads `Application: {applicationName} · not recorded ✕`, and its tooltip adds ` It is in Don't record these apps, so its traffic isn't saved.`;
  - the line under the legend reads, or adds, `Not recorded: this app is in Don't record these apps.`;
  - slots with saved bytes are drawn by their usual state, and every other recorded slot is Not saved, never a zero.

### Errors and limits

- One clear runs at a time, and at most 64 apps can be excluded.
- Existing caps, retention periods and journal bounds are unchanged: connections.db 256 MiB and 30 days, usage-history.db 256 MiB and 400 days, process-history.db 192 MiB and 30 days (0.15.0), alerts.db 64 MiB and 400 days, and the 8 MiB journal bound of each. One exception: while a clear marker exists, Usage history's `Expire` inside `Append` and Application resources' `ExpireByDay` wait until the clear ends, because secure_delete would make their day-sized deletes too large (Shared rules, secure_delete).
- Each worker step lasts 250 ms. The transaction sizes are:
  - Connections history: at most 512 rows, halving under its 1.5 MiB budget (`ConnectionHistoryStore.cs:17`);
  - Usage history: at most 512 rows for one app, and 4,096 for all apps;
  - Application resources: at most 6 hours of minutes for one machine;
  - the alert log: at most 256 episodes, or 1,024 known rows of other machine keys.
- Each store's journal guard is unchanged, and every step also yields when a reader holds more than 4 MiB of WAL. A guard failure, or 60 s of yielding, is a step failure (IOException). `Try again` retries it; otherwise the clear resumes at the next start.
- A clear still runs while Connections recording, Usage recording or Per-process monitoring is off, because the workers run while recording is paused (`ProcessHistoryService.cs:6-8`). A clear needs no administrator rights, and per-app capture being off does not matter.
- An All time clear can drop up to 60 s of new history for the chosen apps.
- Only Connections history hides the scope at once. Usage history, Application resources and the alert log can show partly cleared data until their clears finish, and their status lines say `Clearing history` meanwhile. CI records how long a full Usage history and a full Connections history take (Tests).
- **Clock.**
  - An all-apps clear raises Connections history's monotonic `expiry` to `Before`, and this is kept. If the clock was ahead at the confirmation and is later set back, Connections history recorded before the clock passes `Before` again is hidden and then purged, and the strip labels that time Cleared. That is what any forward clock jump does today (`docs/validation.md:785`). This limitation is accepted and documented in the user guide and `docs/validation.md`, not fixed.
  - The steps in the other stores also delete in-scope rows, with minutes before `Before`, that a clock set back mid-clear writes while they run.
  - A clock set back after an all-apps clear can write Usage history rows before `AllAppsBefore`. The Applications table counts them, and the strip keeps its Cleared band there until the clock passes it.
- **Interruptions.** Quit, sign-out, Restart as administrator, an update install and a crash all interrupt a clear in the same way, and the next start resumes it.
- **Markers.** A marker that cannot be saved means nothing is cleared in that store: it reports `failed (<Type>); not cleared`. An unavailable store reports `unavailable (<Type>); not cleared`.
- **Threads.** Every read, write, step and marker file operation runs on the owning service's worker or in its `Task.Run` reads. The UI thread only builds the request, shows the dialog and reads `ClearState`.

### Privacy

- Usage history's and Application resources' marker files hold the app key in plain text until the clear finishes. They are then deleted like any file. The connections.db and alerts.db markers are deleted with secure_delete on.
- The cleared marks (`cleared-all`, `cleared-one` and `usage-history.cleared.json`) hold times only, never a name or key.
- The TRUNCATE checkpoint empties each `-wal` file, but its old clusters on the drive are not wiped; drive remnants are out of scope.
- settings.json holds the list itself, as it already holds the alert rules' targets.
- `--diagnose` gains nothing from this section.

### Copy

Settings → History and alerts:

- `Don't record these apps`
- `These apps still show on live pages, marked "not recorded", but none of their history is saved: no Connections history, no per-app totals in Usage history and no alerts for them. In Application resources their CPU, memory, I/O and GPU count under Everything else, so device totals stay true. Application traffic budgets for them stop counting. History saved before you add an app is kept until you clear it. Processes that Windows saves as "PID n" can't be matched.`
- `Add` · `Remove` · `No apps are excluded.` · `Saved history for this app is kept. Use Clear history… to remove it.`
- `Enter an app's executable name, such as chrome.exe.` · `That app is already in the list.` · `At most 64 apps can be listed.`
- `Clear history`
- `Removes saved history for one app or all apps, older than 7 days or from all time, from Connections history, Usage history, Application resources and the alert log together. Device-wide history stays.`
- `Clear history…` · tooltips when disabled: `A clear is in progress.` · `A clear is waiting to finish; it resumes when DeadWare next starts.`

Clear history window:

- Title `Clear history`. Heading `Clear saved history`. Groups `Apps` (`One app`, `All apps`) and `Range` (`Older than 7 days`, `All time`).
- `Clears the chosen apps' saved records from Connections history, Usage history's per-app totals, Application resources and the alert log's application budget and first-use alerts. Clearing one app moves its Application resources numbers into Everything else; clearing all apps removes those minutes.`
- `Kept: device-wide history (CPU, GPU, memory, temperatures, network totals, battery and Cooling Health), other alerts in the log, your alert rules, and the Don't record and Don't alert lists.`
- `Not cleared: CSV files you exported, and Windows notification history, which can keep alert balloons that name apps. DeadWare can't reach them; delete them yourself.`
- `Freed space is reused for new history; the files don't shrink.`
- `Clearing doesn't stop recording; add the app to Don't record these apps for that.`
- Buttons: `Clear history` and `Cancel`; `Close` and `Try again` in the progress view.

Confirmation (`<before>` is `Before` in local time, format "g"):

- One app, All time: `Clear all saved history for <app>? This can't be undone.`
- One app, Older than 7 days: `Clear <app>'s saved history from before <before>? This can't be undone.`
- All apps, All time: `Clear every app's saved history? Device-wide history is kept. This can't be undone.`
- All apps, Older than 7 days: `Clear every app's saved history from before <before>? Device-wide history is kept. This can't be undone.`

Progress:

- Headers: `Clearing history…` · `Finishing a clear that was interrupted…` · `History cleared.` · `History partly cleared.`
- Lines, where `<store>` is one of `Connections history`, `Usage history`, `Application resources` or `Alert log`:
  - `<store> · waiting`
  - `<store> · clearing (<n> %)`
  - `<store> · done`
  - `<store> · failed (<Type>); it resumes when DeadWare next starts`
  - `<store> · failed (<Type>); not cleared`
  - `<store> · unavailable (<Type>); not cleared`
- After `History cleared.`: `Freed space is reused for new history; the files don't shrink. Exported CSV files and Windows notification history are not cleared.`
- After `History partly cleared.`: `<n> of 4 finished.`, followed by ` Failed histories resume when DeadWare next starts.` when any line resumes, and ` Histories marked not cleared need Clear history again.` when any line reads not cleared.
- Status suffixes: ` · Clearing history (<n> %).` · ` · Clearing history.` (alert log) · ` · Clearing history failed (<Type>); it resumes when DeadWare next starts.` · ` · Clearing history failed (<Type>); not cleared.`

Traffic strip:

- Band label `Cleared`; line `Cleared before {g}`; line `Some application history before {g} was cleared.`
- Not saved chip lines: `No saved traffic` · `Some application history before {g} was cleared` · `Not recorded: in Don't record these apps`
- Excluded filter: button `Application: {name} · not recorded ✕`; tooltip addition ` It is in Don't record these apps, so its traffic isn't saved.`; line `Not recorded: this app is in Don't record these apps.`

Marks and notes:

- Live suffix: ` · not recorded`.
- Network footer, appended: `Apps marked not recorded are in Settings → Don't record these apps; their traffic isn't saved.`
- Connections' Recording, origin and storage text, appended: `Apps in Don't record these apps show live, marked not recorded, and are never saved.`
- Processes footer, new line: `Apps marked not recorded are in Don't record these apps; Application resources counts them under Everything else.`
- Alerts rule line: a muted note under an ApplicationDaily rule whose target is excluded (`src/DeadWare.App/AlertsPage.cs:165-179`), and in the rule editor when its target is excluded: `Not counted: this app is in Don't record these apps.`
- The Application resources card's Everything else sentence (`src/DeadWare.App/ApplicationResourcesCard.cs:108`, as revised by 0.15.0 to "Each minute lists up to 40 of the busiest applications by CPU, memory, I/O and GPU; Everything else holds the rest.") ends: `…; Everything else holds the rest, including apps in Don't record these apps.`
- Usage history → Keep a longer record, new line before its `Clear history…` button: `Apps in Don't record these apps are not saved here; the Device charts still include their traffic.`

### Docs and release

- **User guide** (`docs/user-guide.md`; re-anchor after 0.15.0 and 0.16.0):
  - **Usage history** (165-179) gains a paragraph that covers:
    - the list and where it is;
    - what it stops: Connections history, Usage history's per-app totals, Application resources (counted under Everything else, GPU included, so device totals stay true) and alerts;
    - that budgets stop counting;
    - that saved history is kept until cleared;
    - "PID n";
    - Clear history's choices, what it clears and keeps, and that "PID n" and "Unknown application" rows are cleared only with All apps;
    - the 60-second rule for All time;
    - resuming after Quit or sign-out, and "not cleared";
    - that freed space is reused and the files don't shrink;
    - that exported CSVs and Windows notification history, which can keep alert balloons naming apps, are not cleared;
    - the strips' Cleared band and lines, and the Connections strip's "not recorded" filter.
  - **Connections** (129-145), **Alerts** (181-189) and **Network measurements and privacy** (205-215) each gain one sentence pointing to it.
    - The Connections sentence also states the clock limitation: after an all-apps clear made while the clock was ahead, setting it back hides new Connections history until the clock catches up.
    - The Alerts sentence also notes the exception for budgets of excluded apps.
    - The Alerts sentence also says that clearing an app for all time makes its next network use alert again, and that clearing older than 7 days keeps it known.
- **`docs/releases/v0.17.0.md`** says, in the rollback paragraph it shares with *First network use alerts* (defined here for that section):

  "Going back to 0.16 or earlier drops the Don't record these apps and Don't alert lists from settings.json on that version's first save, so those apps are recorded again. A clear interrupted by Quit or a crash finishes when 0.17.0 or later next runs. An earlier version leaves the rest of it in place, and shows any rows of a one-app clear that are not yet deleted. Connections history that a clear already hid for all apps stays hidden, and that version purges it in one long maintenance pass at its next save. On 0.16, the Connections strip shows time cleared for all apps as 'Not kept before …', and both strips show other cleared minutes as if the cleared apps had no traffic then."
- **`CHANGELOG.md`**, 0.17.0 section (shared with *First network use alerts*): the same points as the release notes.
- **README:** the 0.17.0 release has at most a one-line mention there, shared with *First network use alerts*.
- **`docs/validation.md`**, 0.17.0 section: the CI measurements as measured, with no threshold; the clock limitation above, beside the expiry-watermark finding at `:785`; and the manual checks as pending.
- **Release order:** 0.15.0, then 0.16.0, then 0.17.0, one PR each from main. This section ships in the 0.17.0 PR with *First network use alerts*, after 0.16.0 has merged.

### Out of scope

- Shrinking files (VACUUM or a separate Compact).
- Wiping exported CSVs, Windows notification history, backups, restore points or drive remnants.
- Wildcards, folders, publishers or paths in the list.
- Excluding by PID or a "PID n" name.
- Clearing by destination, by country or over a custom date range.
- Clearing saved history automatically when an app is added to the list.
- Scheduled or automatic clears.
- Restoring Connections history's `expiry` after an all-apps clear. The clock-ahead limitation is accepted (Errors and limits).
- Restarting the first-use learning period at a clear, or forgetting apps for first-use alerts on an Older than 7 days clear.
- Keeping which app a one-app clear removed, for the strip or anywhere else.

### Tests

**Core (`tests/DeadWare.Tests`, every run, on Linux and on the Windows runner, so both e_sqlite3 builds are covered):**

- `AppKey_folds_case_spelling_and_surrogates`:
  - "Chrome.EXE", "chrome.exe" and "CHROME.EXE " give one key.
  - A name holding an unpaired surrogate gives the key of its `SavedName` form, and `ProcessGrouping.Key` equals `AppKey.Of` for it and for every other fixture name.
  - The key is idempotent.
  - The key equals `names.upper` after a process-history Append of that name.
  - `app_key` on a connections.db connection and on an alerts.db connection returns `AppKey.Of`.
- `Exclusions_normalize_drop_invalid_and_duplicates_and_cap_at_64`:
  - null reads as empty;
  - "", "  ", "Unknown application", "everything ELSE", `C:\a.exe`, "a/b", "a*", "PID 123" and "pid 9" are dropped;
  - "Chrome.exe" after "chrome.exe" is dropped, and the first spelling is kept;
  - 70 valid entries give the first 64, in order.
- `Excluded_apps_round_trip_and_a_file_without_them_reads_empty`:
  - A Write/Read round trip keeps the list.
  - A settings file without the property, or with dropped entries, reads with `Error` null.
- `Usage_folder_skips_an_excluded_app_and_keeps_its_baseline`:
  - Fixture: cumulative bytes of 100, 200, 300, 400 and 500 over five publications. The app is excluded for publications 2–3 and included from publication 4.
  - Assert: the minute holds 200 for the app, never 300 or 400.
  - Assert: when the app is added mid-minute, the closed batch keeps the bytes folded before the change, and carries none after it.
  - Assert: `Forget` removes the app's open-minute bytes and leaves baselines alone.
- `Process_folder_counts_an_excluded_app_in_Everything_else`:
  - Everything else equals the unknown-name processes plus the excluded app's app-level CPU, I/O and memory.
  - Two processes of the excluded app on different engines add the app's busiest-engine GPU-seconds, not the per-process sum, and its GPU memory byte-seconds.
  - Every minute total and RankTotal for every rank, GPU included, equals the same minute without the list.
  - The excluded app is never a listed row, even when it is the busiest.
- `Excluded_budget_stops_ends_its_episode_and_reseeds_only_itself`:
  - `SetExcluded` ends the open episode at its time.
  - The app's bytes in later minutes add nothing, and `SeedDay` skips the rule.
  - On removal, the `only` seed restores the rule's total from the rows, and another rule's total is unchanged.
  - `FiredToday` is kept. A failed re-seed read leaves the total at 0.
- `EndScope_detaches_open_budget_episodes`: after `EndScope`, `OpenEpisodes` lacks the episode, and the next `ObserveMinute` emits nothing for its id. `resetDay` sets the total to 0.
- `Clear_boundaries`: for T = 2026-11-07T10:31:45Z:
  - Older than 7 days gives 2026-10-31T10:31:00Z.
  - All time gives 2026-11-07T10:32:00Z, and the alert log's cutoff is T.
- `One_app_clear_hides_at_once_then_deletes_rows_and_receipts`:
  - Fixture rows: "Chrome.exe" in two batches, "CHROME.EXE", "other.exe", and a Chrome row at `Before`.
  - After `BeginClear`: `Read` (rows, summary, `temp.grouped` and the strip), `ExportCsv` and the earliest-retained time omit the Chrome rows before `Before`, and keep the rest.
  - After the steps: those traffic rows and their `dedup` rows are gone. A manifest whose batch still has receipts remains; one without any is gone.
  - The coverage count is unchanged, and the marker and `PendingClear` are gone.
- `All_apps_clear_raises_expiry_and_purges_below_it`:
  - `expiry` equals `Before` after `BeginClear`, and a later Append at now does not lower it.
  - `Read` is empty before `Before`.
  - The steps purge traffic, coverage, dedup and manifests below `Before`, and rows at or after it stay.
- `Clear_marker_survives_reopen_and_resumes`:
  - After `BeginClear` and one step, a new store instance's `Initialize` passes (schema unchanged, `user_version` 1).
  - Its `PendingClear` equals the request, and its steps finish the clear.
  - `PendingClear` is non-null from `BeginClear` until the last step, and null after it.
- `Clear_steps_stay_inside_the_transaction_and_journal_budgets`:
  - Fixture: 20,000 rows, alternating two apps.
  - Every clear transaction deletes at most 512 rows, counted through a test seam.
  - With no reader, the WAL is at or under 8 MiB after each step.
  - With a pinned reader, the step yields once the WAL passes 4 MiB, never exceeds 10 MiB (the bound `ConnectionHistoryStoreTests.cs:294` uses), and fails with IOException only after 60 s of yielding.
  - A step with a 1 ms budget still commits one transaction.
- `Bounded_maintenance_drains_a_raised_expiry_over_several_appends`:
  - `Append(maintenanceTransactions: 2)` leaves hidden rows, later Appends purge them, and reads never show them.
  - `maintenanceTransactions: 0` purges nothing.
- `Full_file_purges_hidden_rows_before_evicting_a_bucket`:
  - On the fixture cap, with a hidden backlog, a write that hits SQLITE_FULL succeeds without raising `floor`.
  - Each purge before a retry is one maintenance transaction.
- `Clear_marker_that_cannot_be_saved_clears_nothing`:
  - connections.db at its fixture cap with an empty freelist, and a read-only sidecar directory, each report Failed with `MarkerSaved` false.
  - No rows are deleted.
- `Usage_one_app_clear_removes_every_spelling_on_every_machine`:
  - Fixture: "Chrome.exe" and "chrome.exe" rows under machine keys "A" and "B", with 64 apps a minute, plus metric rows and capture gaps.
  - After the clear: no Chrome row before `Before` remains, and Chrome rows at or after it stay.
  - The `metric_minutes` and `capture_gaps` counts are unchanged.
  - No transaction deletes more than 512 rows.
  - With no reader, the WAL is at or under 8 MiB after each transaction.
- `Usage_all_apps_clear_keeps_device_minutes`: no transaction deletes more than 4,096 rows, and the `metric_minutes` and `capture_gaps` counts are unchanged.
- `Usage_clear_marker_is_atomic_and_resumes`:
  - The JSON fields match the request, and no `.tmp` file remains. A leftover `.tmp` is deleted at startup.
  - `ClearPending` is true from the marker's write, and after a reopen, until the marker is deleted.
  - A new store instance resumes and finishes the clear.
  - Invalid JSON is deleted and reports Failed (InvalidDataException) with `MarkerSaved` false, and no rows are deleted.
- `Retention_expiry_waits_while_a_marker_exists`: Usage history's Append, and Application resources' `Expire` and `Append`, delete no expired day while a marker exists, and both delete it once the marker is gone.
- `Process_one_app_clear_moves_numbers_into_Everything_else`:
  - RankTotal for each rank, GPU included, is unchanged.
  - Everything else grows by the app's CPU, I/O, memory, GPU-seconds and GPU memory byte-seconds.
  - The app is gone from the rows and from `names`.
  - A minute without an Everything else row gains one.
  - A minute with null GPU values stays null, and `gpu_covered` is unchanged.
  - The file stays at version 2.
- `Process_all_apps_clear_deletes_whole_minutes_in_six_hour_windows`:
  - Minutes before `Before` are gone, with their GPU values, later minutes stay, and orphaned names are gone.
  - Each transaction spans at most 360 minutes of one machine, counted through a test seam.
- `Alert_clear_removes_app_episodes_and_keeps_device_episodes`:
  - Fixture episodes:
    - a pre-0.17 budget episode "chrome.exe ≥ 2.0 GB in a day" without a link;
    - a budget episode for "Chrome.EXE";
    - a first-use episode linked to CHROME.EXE;
    - one linked to another app;
    - a CPU-metric episode;
    - a battery episode;
    - a first-use episode for CHROME.EXE that starts after `Requested`, in the same minute.
  - Each combination of one app or all apps with Older than 7 days or All time deletes exactly the in-scope episodes. The episode after `Requested` is kept.
  - Older than 7 days keeps open episodes and every `known_apps` row.
  - One app, All time deletes that key's `known_apps` row in the marker's transaction; a row for the same key written after that transaction is kept.
  - All apps, All time empties `known_apps` on every machine key, and the learning start is unchanged.
  - `pending_clear` is empty afterwards, and `user_version` is 1.
  - A 0.16 file without the new tables gains them in place.
- `Alert_store_creates_new_tables_at_the_cap`:
  - An alerts.db filled to its fixture cap with an empty freelist initializes. It evicts the oldest tenth of the closed episodes and creates `pending_clear` and *First network use alerts*' tables in one transaction.
  - On a 0.16 file, which has no `episode_apps` table yet, the eviction deletes episodes only.
- `Strip_marks_time_a_clear_removed`:
  - Connections: after an all-apps Older than 7 days clear, `RetainedFrom` is `Before` and `RetainedByClear` is true; once the 30-day expiry passes it, `RetainedByClear` is false; with `CapacityShortened`, it is false.
  - `cleared-all` and `cleared-one` hold the latest `Before` after two clears of each kind, and a reopen still passes `Initialize`.
  - A one-app clear sets `AppClearedBefore` when its `Before` is after the origin and after `RetainedFrom`, and null otherwise.
  - Usage history: after an all-apps clear, minutes before `AllAppsBefore` are in no slot, `RetainedFrom` is the first whole minute at or after it and `RetainedByClear` is true; a one-app clear sets `AppClearedBefore`; an invalid or missing `usage-history.cleared.json` reads as no clear.
- `Cleared_names_leave_no_bytes_in_any_store_file`:
  - For each of the four stores: write rows for "ZQXSECRET.exe", clear that app for All time, and checkpoint.
  - The UTF-8 bytes of "ZQXSECRET" and "zqxsecret" (connections' `search` column is lower case) appear nowhere in the database, its `-wal` file or `usage-history.cleared.json`, and no sidecar marker remains.
  - `PRAGMA secure_delete` on the clearing connection returns 1.

**App (`tests/DeadWare.App.Tests`, every run):**

- `Excluded_app_shows_live_and_is_never_appended`: `Live` holds the app, appended records do not, and `DroppedObservations` and `Complete` are unchanged.
- `Retried_batch_keeps_its_payload_when_the_list_changes`: the append throws once, the list changes, and the retried payload is unchanged.
- `Connection_clear_flushes_first_and_drops_the_rest_of_the_minute`:
  - The app's frozen units are appended before the delete.
  - After an All time clear, the app's deltas from minutes before `Before` are not appended, and those at or after it are.
  - Older than 7 days leaves the open units alone.
  - A write attempt whose `Append` throws leaves the clear Failed (IOException), with the marker present.
  - `InitializeAsync` passes 64, the writer's write attempts pass 64, and the final write passes 0 (through a test seam).
- `Usage_clear_interleaves_with_recording_and_resumes_after_quit`:
  - Fixture: a store with 200,000 app rows.
  - While the clear runs, no publication is dropped.
  - `StopAsync` during the clear completes within 1 s, and the marker remains.
  - A new service resumes the clear with `Resumed` true and finishes it.
- `Process_clear_moves_the_open_minute_into_Everything_else`, GPU included, together with the all-apps discard.
- `Alert_clear_runs_in_steps_and_deletes_an_open_budget_episode_for_good`:
  - Later minutes do not recreate the row.
  - The known app is removed, and the Don't alert list is unchanged.
  - `StopAsync` mid-clear completes within 1 s, the marker remains, and a new service runs `EndScope` and `Flush` again and finishes the clear.
- `Excluded_budget_reseeds_when_the_app_is_removed`.
- `ClearHistory_refuses_a_second_request_and_reports_a_stopped_worker_as_unavailable`: a failed Usage read, with its worker still running, is not unavailable.
- `HistoryClearChanged_rereads_the_open_pages`: each store's clear raises the event when it starts clearing and when it ends; Connections History calls `ReloadHistory`, Usage history refreshes, and the Alerts page re-reads its log.
- `Quit_during_a_clear_meets_the_history_deadline` (fake slow steps, in the style of `CoolingShutdownTests`):
  - Shutdown finishes within 20 s with no stage outstanding.
  - `StopCollectorForSessionEnding` during a clear with a hidden Connections backlog returns within 3 s with no stage outstanding.

**UI checks** (Windows CI, inside the existing STA fact):

- `ShellUiChecks`, for the Settings block:
  - its exact strings;
  - adding from a suggestion and by typing;
  - the three errors, the note and Remove, with focus after a remove;
  - `Clear history…` disabled during a fake clear, with each tooltip shown on the disabled button.
- `HistoryClearUiChecks` (new):
  - the defaults: One app, Older than 7 days, and `Clear history` disabled;
  - the validation text;
  - the four confirmation texts through the `Confirm` hook, and No sending nothing;
  - each progress line and header sentence, including the 99 % cap, the not cleared lines and the mixed partly cleared text;
  - `Try again` visibility, and `Close` leaving the clear running;
  - the result staying in Settings until a new clear;
  - Tab order, Esc, and Enter in the box not starting a clear;
  - the header's live setting;
  - the window's tokens passing `ThemeTokenTests`.
- `ConnectionsPageUiChecks`, `ProcessesPageUiChecks` and the Network page block of the STA fact (`tests/DeadWare.App.Tests/BatteryUiTests.cs:464-480`, `destination == "network"`):
  - the ` · not recorded` marks and the footer sentences;
  - the Processes spoken summary;
  - search on the bare name, and sorting unchanged.
- `ConnectionsPageUiChecks.CheckStripNotRecordedAndClearedTime`:
  - an excluded application filter: the button text and tooltip, the line under the legend, Not saved slots hatched and focusable with their chip lines and "—" legend values, and slots with bytes drawn as usual;
  - a strip with `RetainedByClear`: the band labelled `Cleared` and the `Cleared before …` line; with `AppClearedBefore`: its line, and Not saved slots under a filter; with `AppClearedBefore` and no filter, a zero-byte slot before it is Not saved, not No traffic.
- `AlertsUiChecks`: the Not counted note on the rule line and in the editor.
- `ApplicationResourcesUiChecks` and `UsageHistoryUiChecks`: the copy above, the `Clear history…` entry points, and on Usage history the Cleared band and both strip lines, no hatching inside the band, and, with `AppClearedBefore`, a zero-byte slot before it Not saved, not No traffic.

**Real Windows (CI, measurement only):**

- `History_clear_timing_on_large_files`:
  - It runs in Windows CI as a measurement on every run, not behind an environment variable.
  - It seeds a full per-minute connections.db at its 256 MiB cap, built as *Connections storage*'s `Connection_history_rates_and_read_timings` builds its file, and a usage-history.db filled to its 256 MiB cap with 50 apps a minute.
  - It times, for each:
    - a one-app clear, with the app holding about 10 % of the rows;
    - an all-apps All time clear.
  - It records the total ms, the step count, the p95 step ms and the largest WAL size.
  - It asserts only that the WAL never exceeded 8 MiB with no reader, and that every clear finished.
  - It writes through `MeasurementRecorder` to the `DeadWare-measurements` artifact and the job summary.

**Pending by eye** (listed in `docs/validation.md`, never claimed as verified):

- All apps, All time on the user's full connections.db and usage-history.db: the time until done, dashboard responsiveness, and Quit and sign-out during the clear, followed by the resume.
- An excluded app with a day of traffic: absent from Connections History, from Usage history's Applications and from the card's rows, while device totals still match.
- The Cleared band and Not saved slots on both strips, and the excluded application filter, in a light, a dark and a high-contrast theme.

### Depends on

- ***Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0):** its merge filter hook immediately before the merge, which skips excluded and boundary deltas without counting them as dropped, keeps `Held` and `buffered` balanced and reads the set at merge time only.
- ***Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0):** `ConnectionMinuteFolder.Forget(string? appKey)`, the write attempt's `ConnectionFlushResult`, retiring held in-scope units whole before the first delete, and the R6 final write passing `maintenanceTransactions: 0`.
- ***Connections storage: per-minute merge, the grouped read and the Unlocated fix* (0.16.0):** `GroupedWhere`, which carries the one-app hide predicate to `temp.geo`, `temp.grouped` and `ExportCsv`, and a `Read` that keeps nothing between calls.
- ***The traffic strip on Connections History and Usage history* (0.16.0):** `ConnectionsPage.ReloadHistory()`, and Usage history's `RefreshAsync` re-reading the strip with the table.
- ***The traffic strip on Connections History and Usage history* (0.16.0):** the `TrafficStrip` record, its retained-start step and slot states, the Not kept band and line, and the application filter button, which this section extends with `RetainedByClear`, `AppClearedBefore`, the Cleared label and the Not saved state.
- ***The traffic strip on Connections History and Usage history* (0.16.0):** Show in Connections passing the bare stored name, never a key or a mark.
- ***The Destinations table* (0.16.0):** the `app_key` registration in `Open`, which this section changes to `AppKey.Of`, and its Live grouping through `ProcessGrouping.Key`.
- ***The Destinations table* (0.16.0):** the ` · not recorded` mark in the Live APPS cell, its tooltip line and the spoken text.
- ***First network use alerts* (0.17.0):** keys `known_apps`, `episode_apps` matching and the Don't alert entries by `AppKey.Of`, which `ProcessGrouping.Key` now is.
- ***First network use alerts* (0.17.0):** `Configure`'s `excludedApps` parameter, with the collector-side detector, the tracker and the one-time seed skipping excluded keys, and an app removed from the list counting as new unless already known.
- ***First network use alerts* (0.17.0):** `tracker.Forget(AppKey, Requested)`, the `knownGeneration` increment and `Forget` removing forgotten names from the waiting group at once, run in this section's alert-log order: `Flush`, then the marker with the current-machine known-row deletes, `EndScope`, `Forget`, the generation increment and `Flush`.
- ***First network use alerts* (0.17.0):** an Older than 7 days clear deletes no `known_apps` row and forgets no key, as the approved decision says; its clear table and guide paragraph say so.
- ***First network use alerts* (0.17.0):** its tables created in the same `Initialize` transaction as `pending_clear`, so the SQLITE_FULL retry covers them, and its eviction deleting the evicted episodes' links.
- ***First network use alerts* (0.17.0):** `ReadApplicationNames` on both stores returning null while `PendingClear` or `ClearPending` is set.
- ***First network use alerts* (0.17.0):** a clear never changes the seed markers, the learning start and end, the Don't alert list or the rule.
- ***First network use alerts* (0.17.0):** the shared `docs/releases/v0.17.0.md` rollback paragraph and the 0.17.0 CHANGELOG and README line.
- **0.15.0 per-app GPU** (`docs/superpowers/specs/2026-11-06-per-app-gpu-design.md`): process-history.db version 2 with `gpu_seconds`, `gpu_memory_seconds` and `gpu_covered`, the App rows rule for app-level GPU-seconds, its Everything else rule, 40 listed apps plus Everything else a minute and the 192 MiB cap, merged before 0.16.0.


## Errors, testing, docs and release (both releases)

The user approved this as section 7 on 2026-11-07 ("section 7 looks right"). These rules apply to every section above. A section's own Errors subsection gives its texts and limits; it adds detail, never an exception.

### Errors

- **Unknown is "—", never 0.** A value DeadWare does not know is null and shows "—" with a tooltip naming its reason. For example:
  - the strip's legend for a slot with no recorded time ("No per-application capture in this slot", "Under a second recorded in this slot");
  - the Destinations LOCATION reasons and stale-capture rates;
  - the first-use rule line, which shows "Starting: reading the known-app list." until the known apps are restored, never a count of 0.

  Unrecorded time is hatched, never drawn as zero traffic. The minute not yet saved stays empty and says "Not saved yet".

  Two 0.17.0 cases fall under the same rule, and *Don't record these apps and Clear history…* gives their drawing and copy:
  - with the application filter set to an app on the Don't record list, its recorded minutes are not drawn as known zeros, and the filter is marked "not recorded";
  - minutes whose rows a one-app clear removed are not drawn as zero traffic, and an all-apps clear's raised `expiry` is not presented as a retention limit.

  One existing exception stays as it is: the ledger's 0 B/s on the first sample after a capture break, which the map, the detail box and the Destinations table all show (Out of scope).
- **Unreadable is "unavailable", never an empty period.** A read that fails returns no partial view:
  - **Connections:** any read failure, including the 64 MiB TEMP limit, shows "History unavailable; no saved observations shown.". The action status gives the reason: "History could not be read ({TypeName}).", or "This interval holds too many saved rows to show. Choose a shorter interval.". The strip shows "History unavailable" and the Destinations table "Destinations unavailable; no saved observations shown.".
    - The summary then shows "—" for every count, with the tooltip "History unavailable", never the zeros the read-error view gives today (`Empty`, `src/DeadWare.App/ConnectionsPage.cs:150`). The Destinations count line follows the same rule. This applies §7's rule that an unknown value is never 0.
  - **Usage history:** a failed strip read shows "History unavailable", and a failed slot read "History unavailable for this slot.".
  - **The alert log:** when it fails, the first-use rule line reads "Paused: the alert log is unavailable.".
  - **The first-use seed:** a store whose seed read fails, or that holds a clear marker, is seeded at a later start, never treated as empty.
  - **Clear history:** a store that is unavailable reads "unavailable ({Type}); not cleared". A store whose marker could not be saved reads "failed ({Type}); not cleared". Neither promises to resume.
- **Status text** holds fixed copy, counts and error codes (exception type names, as today), never app names, addresses or paths.
  - App names and addresses appear only where the user reads data: cells, legends, tooltips, spoken row text, the clipboard, CSV files, alert log rows and balloons.
  - Balloons name apps, as application-budget balloons already do (`2026-11-02-alerts-design.md:31`).
  - `--diagnose` gains nothing.
- **Threads.** All storage and native work runs off the UI thread:
  - the connection writer task merges, freezes and writes;
  - `ReadViewAsync`'s `Task.Run` runs every Connections read, and the services' `Task.Run` runs the Usage history reads and Export destinations;
  - first-use detection runs on the collector thread, with no I/O and no locks of its own;
  - the alert worker owns the tracker, the seed and every `alerts.db` write;
  - each clear runs on its own store's worker.

  The UI thread only builds geometry, groups the in-memory Live destinations (at most 8,192 peers), formats text and writes the clipboard, which `Clipboard.SetText` requires.
- **Existing caps and journal limits are unchanged:**

  | Store | Cap | Retention |
  |---|---|---|
  | `connections.db` | 256 MiB | 30 days |
  | `usage-history.db` | 256 MiB | 400 days |
  | `process-history.db` | 192 MiB (0.15.0) | 30 days |
  | `alerts.db` | 64 MiB | 400 days |

  Each store's 8 MiB journal bound, the transaction budgets, the queue capacities and `MaximumBufferedDeltas` are unchanged too. Retention has one exception: while a clear marker exists, Usage history's and Application resources' retention deletes wait until the clear ends (*Don't record these apps and Clear history…*).

  The new limits are each owned by their section:
  - *Connections storage:* 64 MiB of TEMP pages per History read;
  - *The traffic strip:* 1,500 strip slots;
  - *The Destinations table:* 1,000 rows per scope and 3,000 in all;
  - *First network use alerts:* 8,192 known apps and 256 Don't alert entries;
  - *Don't record these apps and Clear history…:* 64 excluded apps, 250 ms clear steps with per-store transaction sizes, and one clear at a time.
- **Interruptions resume or are redone:**
  - A failed Connections write is retried with identical units. A crash loses about the last minute, which reads as a coverage gap.
  - A strip or Destinations read dropped by hiding or unloading the page is made again when the page is shown.
  - An export cut off by an error, Quit or a crash is never resumed or reported as finished.
  - An interrupted first-use seed runs at the next start. A learning start not yet saved is taken again at the next capture.
  - An interrupted clear resumes at the next start from its marker.
- **Quit and sign-out.**
  - Quit keeps its 20 s history deadline and sign-out its 3 s limit (`UiSession.cs:300`, `:346`). A clear makes them wait for at most one step (250 ms plus one transaction). The final Connections write never purges (`maintenanceTransactions: 0`).
  - Two known risks are accepted.
    - (*Connections storage*) `Read` holds the store's lock for about 2.5 s on a full per-minute file. A History read in progress at sign-out can therefore make the final write miss the 3 s limit and lose the open minute.
    - (*The Destinations table*) Export destinations, like Export CSV, holds the store's lock for the whole export, so a Quit or sign-out during an export can miss the final write's deadline.
- **No new requirements.** Nothing needs administrator rights beyond per-app capture's existing need. Nothing makes a network request.

### Testing

**How each kind of test runs:**
- **Core** (`tests/DeadWare.Tests`) runs on every run: on Linux during development, and on the Windows runner in CI. These tests cover:
  - the pure policy objects (`ConnectionMinuteFolder`, `TrafficStrips`, `FirstUseTracker`, `AppKey`, `AppExclusions`), which have no timers, threads or storage;
  - the real SQLite stores on temporary files.

  Running on both builds covers both e_sqlite3 binaries.
- **App tests** (`tests/DeadWare.App.Tests`) run in Windows CI. They drive the services with fakes and test clocks.
- **Page checks** run in Windows CI inside the existing STA fact `BatteryUiTests.Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows` (`tests/DeadWare.App.Tests/BatteryUiTests.cs:38`), as phases in its list.
  - No new STA fact is added.
  - The new check classes (`TrafficStripUiChecks`, `HistoryClearUiChecks`) join that phase list.
  - The existing classes gain checks: `ConnectionsPageUiChecks`, `UsageHistoryUiChecks`, `ConnectionMapUiChecks`, `AlertsUiChecks`, `ShellUiChecks`, `ProcessesPageUiChecks`, `ApplicationResourcesUiChecks` and the Network page block of the STA fact (`tests/DeadWare.App.Tests/BatteryUiTests.cs:464-480`, `destination == "network"`).
- **Real Windows** tests run on GitHub's Windows runner, as `[GitHubWindowsFact]`, or `[ElevatedGitHubFact]` where per-app capture is needed, in `[Collection("RealWindows")]`.
  - They write through `MeasurementRecorder` to the `DeadWare-measurements` artifact and the job summary.
  - A measurement records and decides nothing; `docs/validation.md` keeps its numbers as measured, with no threshold.
  - The clear measurement also asserts that the WAL never exceeded 8 MiB with no reader and that every clear finished.
  - `First_network_use_sees_this_process_send_one_udp_datagram` is a check, not a measurement. When capture is unavailable it records that and passes.
- **Checks by eye** are done by the user on their PC. They are listed in `docs/validation.md` as pending and never claimed as verified until the user reports them (Pending by eye).

**New CI measurements** (Windows CI, every run, not behind an environment variable):
- **History read on a full per-minute file:**
  - `Connection_history_rates_and_read_timings` (*Connections storage*):
    - MiB/h for the idle, 1-pair, 20-pair and churn loads;
    - a `connections.db` filled to its 256 MiB cap with per-minute rows;
    - its 31-day, 7-day, 1-day and 1-minute `Read` times and SQLite's heap high-water mark, with `temp_store` MEMORY and with SQLite's default;
    - the Destinations table's store-step times for each read;
    - the 30-day `Read`, strip included, with the application filter empty and set to the busiest app, p50 and p95 of 5 runs (*The traffic strip*'s part).
  - `Usage_application_series_timing_for_a_month` (*The traffic strip*): `ReadApplicationSeries` over 30 days with 15 apps a minute, at 450 and 1,500 points.
- **Full Connections and Usage clear durations:** `History_clear_timing_on_large_files` (*Don't record these apps and Clear history…*).
  - It times a one-app clear (the app holding about 10 % of the rows) and an all-apps, all-time clear.
  - It runs on a `connections.db` at its 256 MiB cap and a `usage-history.db` at its 256 MiB cap.
  - It records the total time, the step count, the p95 step time and the largest WAL.
  - "Full" means each file at its production 256 MiB cap. The existing near-capacity fixture's 64 MiB default, which only `DEADWARE_FULL_STORAGE_STRESS=1` raises to 256 MiB (`tests/DeadWare.Tests/ConnectionHistoryStoreTests.cs:278-279`), does not meet it.
- **CI time.** CI's job limit is 20 minutes (`.github/workflows/build.yml:15`). The 0.16.0 plan's first measurement task times its 256 MiB per-minute fill on the Windows runner, and the 0.17.0 plan's first measurement task times the clear measurement's two fills. Each records the time in its PR description and in its release's `docs/validation.md` section. If the fills do not fit, that goes back to the user before either the file sizes or the limit changes.

**Test counts by section.** These count the test and check names listed in each section's Tests subsection.
- A new name counts once.
- An existing test whose assertions change counts as changed.
- Checks added to an existing check class without a name of their own are not counted.

| Section | Release | Core (every run) | App (Windows CI) | Page checks (STA fact) | Real Windows CI | New | Changed |
|---|---|---|---|---|---|---|---|
| Connections storage | 0.16.0 | 18 | 7 | 1 | 1 measurement | 27 | 9 (1 Core, 8 App) |
| The traffic strip | 0.16.0 | 22 | — | 18 | 1 measurement, and joins the storage measurement | 41 | 6 page checks |
| The Destinations table | 0.16.0 | 20 | — | 8 | joins the storage measurement | 28 | the two "no DataGrid" assertions, `CheckWholePageLayout`, `CheckThemes` |
| **0.16.0 total** | | **60** | **7** | **27** | **2** | **96** | |
| Connections storage, writer hooks | 0.17.0 | 1 | 2 | — | — | 3 | — |
| The Destinations table, not-recorded mark | 0.17.0 | — | — | 1 | — | 1 | — |
| First network use alerts | 0.17.0 | 26 | 7 | additions to `AlertsUiChecks` and `ShellUiChecks` | 1 check (elevated) | 34 | 1 Core |
| Don't record these apps and Clear history… | 0.17.0 | 25 | 10 | 2 (`HistoryClearUiChecks`, `ConnectionsPageUiChecks.CheckStripNotRecordedAndClearedTime`), plus additions to six classes and the Network page block | 1 measurement | 38 | — |
| **0.17.0 total** | | **52** | **19** | **3** | **2** | **76** | |

- **The changed tests:**
  - *Connections storage:* `Markers_are_bounded_while_full_summaries_include_local_unlocated_and_every_country`, which asserts Unlocated 1 instead of 2, and eight `ConnectionServiceTests` that assumed per-poll batches.
  - *The traffic strip:* `UsageHistoryUiChecks.Run` and `CheckChart`; `ConnectionsPageUiChecks.CheckHistoryQueries`, `CheckWholePageLayout`, `CheckThemes` and `CheckNavigation`.
  - *The Destinations table:* it updates `CheckWholePageLayout` and `CheckThemes` again, for its own card.
  - *First network use alerts:* `AlertRuleTests.Settings_keep_rules_and_the_notification_switch_and_default_to_none_and_on`.
- **Kept and named.** Two existing tests must pass unchanged: `Grouping_preserves_selection_keys_saturates_totals_and_bounds_markers` (*The Destinations table*) and `AlertStoreTests.Retention_drops_old_episodes_and_the_cap_evicts_the_oldest_closed_ones_first` (*First network use alerts*). They are not counted.

### Docs and release

**Release headings.** CHANGELOG and `docs/validation.md` use these headings. The release notes have none, because the release workflow publishes `docs/releases/<tag>.md` verbatim.
- 0.16.0:
  - CHANGELOG: `## 0.16.0 — Traffic strip and Destinations table`;
  - `docs/validation.md`: `## Traffic strip and Destinations table (0.16.0, <date>)`.
- 0.17.0:
  - CHANGELOG: `## 0.17.0 — First network use alerts and history controls`;
  - `docs/validation.md`: `## First network use alerts and history controls (0.17.0, <date>)`.
- They follow the house style of naming what shipped ("Application resource history", "Processes and history fixes"), rather than the working titles "See it" and "Watch and control".

**0.16.0**, in the 0.16.0 PR:
- **User guide, Connections** (`docs/user-guide.md:129-145`):
  - *The Destinations table:*
    - a new paragraph after the one that starts "Open **Connections** beside Network", on the table, the scope, the caps, the highlight, the hold and F5, copy and export, and that History lists saved minutes;
    - in that paragraph, "Private/LAN, special and unlocated addresses remain in summary totals and saved-history CSV exports." becomes the Destinations sentence;
    - one sentence on bare addresses after the IPv6 notation sentence.
  - *The traffic strip:* the History paragraph describes the strip, its cursor and the application filter.
  - *Connections storage and The traffic strip:* the retention paragraph (the one that starts "History normally retains 30 days") gains:
    - the per-minute saving text and the crash-loss figures;
    - the 20-pair example;
    - "Not kept before …";
    - the narrower "Capacity shortened retention".
- **User guide, Usage history** (`docs/user-guide.md:165-179`, *The traffic strip*):
  - The Applications paragraph replaces the share bar with the strip and adds Show in Connections. It says that strip colours identify apps, not direction.
  - The click-a-moment paragraph, as 0.15.0 leaves it, adds that the strip shares the cursor and that a click lists its slot in the Applications table.
- **CHANGELOG** and **`docs/releases/v0.16.0.md`:**
  - **Storage:** per-minute rows, about 60 times fewer than 0.15 at 1-second polling, with 30 days for idle and light use. Earlier rows stay until they expire, and the file does not shrink. A minute appears a few seconds after it ends. A crash can lose about a minute. A small backward clock correction no longer loses history.
  - **The Unlocated fix.**
  - **The strip** on both pages, Show in Connections and the application filter.
  - **The reworded Usage gap notice** and the narrower "Capacity shortened retention".
  - **Line charts** that connect fully recorded slots with one minute start fewer than their length.
  - **The Destinations table.**
  - **Rollback:** see Rollback.
- **`docs/validation.md`**, 0.16.0 section:
  - the retention and timing runs;
  - the two measurements' Windows numbers, as measured, including the strip's timings and the Destinations step times;
  - the Unlocated finding (`docs/validation.md:785`) marked resolved, and the expiry-watermark finding still open;
  - the checks pending by eye.
- **README:** at most one line.
- **Preview:** `tools/OverviewPreview` renders the Destinations table with its fixture. Its reserved 203.0.113.x addresses list as "Special · documentation" under All.

**0.17.0**, in the 0.17.0 PR:
- **User guide, Alerts** (`docs/user-guide.md:181-189`):
  - *First network use alerts:*
    - "Four kinds are available." becomes "Five kinds are available.", with a sentence naming the first network use rule;
    - "it never notifies more than once an hour" gains "(first network use is the exception, below)";
    - a new paragraph on the rule, the learning period, the grouped balloon, the Don't alert list, what is not counted, clears and forgetting. It includes the warning that a removed rule learns nothing, so adding it back alerts once for each app first used meanwhile.
  - *Don't record these apps and Clear history…:* one sentence pointing to Don't record and Clear history. It notes that budgets of excluded apps stop counting and that an all-time clear makes an app's next use alert again.
- **User guide, Usage history** (`docs/user-guide.md:165-179`, *Don't record these apps and Clear history…*): a new paragraph covering:
  - the list and what it stops, including that Application resources counts excluded apps under Everything else so device totals stay true;
  - that saved history is kept until cleared, and "PID n";
  - Clear history's choices, what it clears and keeps, and the 60-second rule for all time;
  - resuming and "not cleared";
  - that freed space is reused, and that exported CSVs and Windows notification history are not cleared.
- **User guide, Connections** (`docs/user-guide.md:129-145`): one sentence pointing to Don't record and Clear history. It also states the clock limitation: after an all-apps clear made while the clock was ahead, setting the clock back hides new Connections history until the clock catches up.
- **User guide, Network measurements and privacy** (`docs/user-guide.md:205-215`):
  - the sentence on what `alerts.db` keeps for the first network use rule (*First network use alerts*);
  - one sentence pointing to Don't record and Clear history (*Don't record these apps and Clear history…*).
- **CHANGELOG** and **`docs/releases/v0.17.0.md`:**
  - first network use alerts: added once and switched on (not when 64 rules already exist), 3 days of learning after per-app capture first runs, one balloon per 60 s, the Don't alert list and View in Connections;
  - Don't record these apps, with its live marks and what stops being saved;
  - Clear history…, with what it clears and keeps;
  - one rollback paragraph shared by both 0.17.0 sections (Rollback).
- **`docs/validation.md`**, 0.17.0 section:
  - the result of `First_network_use_sees_this_process_send_one_udp_datagram` as recorded, or `capture: unavailable`;
  - `History_clear_timing_on_large_files` as measured;
  - the accepted clock-ahead limitation of an all-apps clear, beside the expiry-watermark finding at `docs/validation.md:785`;
  - the checks pending by eye.
- **README:** at most one line. A mention of the new privacy controls, if any, is that line.

**Release.**
- **Order:** 0.15.0, then 0.16.0, then 0.17.0.
- **One PR each from `main`:** a feature branch opened after the previous release has merged, following `docs/development.md`, with CI as the gate.
- **Docs travel with their release.** Each PR carries that release's docs and its validation section.
- **Recheck anchors.** Before each plan starts, the anchors that 0.15.0, and for 0.17.0 also 0.16.0, moved are checked again.

### Rollback

**0.16 to 0.15:**
- **`connections.db`** is at `user_version` 1 with the same schema, so 0.15's schema check passes.
  - 0.15 reads the per-minute rows as ordinary rows and writes per-poll rows again; History shows both.
  - 0.15 goes back to its own Unlocated count, which counts local peers, and to its three-scan read.
- **The other stores.** `usage-history.db` and `process-history.db` are untouched by 0.16, so they are in 0.15's format.
- **Settings.** 0.15 drops `ConnectionsDestinationScope`, so returning to 0.16 opens the table on All.
- Neither the strip nor the Destinations table keeps anything else on disk. `docs/releases/v0.16.0.md` says that returning to 0.15 keeps reading the file and records per poll again.

**0.17 to 0.16 or older:**
- **`settings.json`.** That build's first save drops `ExcludedApps`, `FirstUseIgnoredApps`, `FirstUseRuleAdded` and the first network use rule. Older builds drop unknown rule kinds (`AlertRules.cs:30`) and save at every launch (`UiSession.cs:95`). As a result:
  - excluded apps are recorded again;
  - apps on the Don't alert list are no longer on it.
- **`alerts.db`** stays at `user_version` 1, and builds from 0.8.0 to 0.16 ignore the four new tables.
  - Their retention and eviction leave orphan `episode_apps` rows, which 0.17 removes at its next start.
  - They show first-use episodes as their summaries with "peak 0".
  - They never re-append those episodes, which are saved closed.
- **A clear in progress.**
  - The older build ignores the `connections.db` `metadata` marker, because it reads only `floor` and `expiry` by key. It also ignores the sidecar files.
  - It shows any rows of a one-app clear that are not yet deleted.
  - Connections history that an all-apps clear already hid by raising `expiry` stays hidden, and that build purges it in one long maintenance pass at its next save.
  - The clear finishes when 0.17 or later next runs.
- **Cleared time.** On 0.16, the Connections strip shows time cleared for all apps as "Not kept before …", and both strips show other cleared minutes as if the cleared apps had no traffic then.
- **Reinstalling 0.17.**
  - It adds the rule again once, even if it had been removed. There is no new learning period, because the learning dates stay in `alerts.db`.
  - Apps first used meanwhile alert at their next use.
- **The notes.** `docs/releases/v0.17.0.md` states all of this in one rollback paragraph, the first-use and Don't record sentences together.

**0.17 to 0.15:** both of the above apply.

### Out of scope (both releases)

- **Excluded for both releases (§1):**
  - hostnames, reverse DNS, passive DNS capture and port service labels such as "443 → HTTPS";
  - blocking or firewall rules of any kind;
  - the billing-period data plan (dropped);
  - cloud, reputation and online lookups;
  - per-app speed limits.
- **Storage and files:**
  - rewriting existing per-poll rows;
  - VACUUM, `VACUUM INTO`, a Compact command or recreating a file to shrink it;
  - new tables or indexes in `connections.db`, or any change to the formats of `usage-history.db` and `process-history.db`;
  - caching TEMP tables between reads;
  - changing CSV output or `ExportCsv`'s temp store;
  - releasing the store's lock during a read or an export, or interrupting a read at sign-out;
  - a configurable close grace or clock hold window;
  - fixing Connections history's monotonic `expiry` watermark (`docs/validation.md:785`), including restoring `expiry` after an all-apps clear (§6).
- **Traffic strip:**
  - a strip in Live mode, on the widget or on the Network page;
  - restacking one app by country or destination;
  - a 100 %-stacked or log-scale view;
  - separate download and upload bands;
  - exporting the strip as CSV;
  - highlighting a Destinations row in the strip;
  - any change to the Application resources card's ranking, or any read of `process-history.db` by the strip.
- **Destinations table:**
  - grouping by app or by country, and per-app child rows;
  - applying the scope to the map, the summary, the node list, the strip or Export CSV;
  - a Live export, and saving the sort;
  - destinations in the desktop widget;
  - synthetic map nodes for History locations outside the 256 markers;
  - changing the ledger's 0 B/s on the first sample after a capture break.
- **First network use:**
  - "app changed" alerts by file hash or signer;
  - first contact with a new country or destination;
  - a timed snooze, or a per-rule log-only mode (the global **Show tray notifications for alerts** switch mutes balloons);
  - TCP connect and accept events;
  - traffic DeadWare cannot attribute to a process;
  - seeding from Application resources;
  - a new learning period after a clear (§6).
- **Don't record and Clear history:**
  - wildcards, folders, publishers or paths in the list;
  - excluding by PID or by a "PID n" name;
  - clearing by destination, by country or over a custom date range;
  - clearing saved history automatically when an app is added to the list;
  - scheduled or automatic clears;
  - wiping exported CSVs, Windows notification history, backups, restore points or drive remnants.

### Pending by eye

Each item is listed in its release's `docs/validation.md` section as pending, and is never claimed as verified until the user has checked it on their PC.

**0.16.0:**
- *Connections storage:*
  - a 7-day History view after a week of per-minute recording shows "Reading saved history…" and then the view;
  - the newest minute appears a few seconds after it ends.
- *The traffic strip:*
  - the strip is readable in all nine themes and in high contrast, including Mage's and Skyrim's closest pairs;
  - the bottom band agrees with the top row of the Applications table;
  - a large download shows as one tall band;
  - the current minute at the right edge of Today is empty, not hatched, and its hover says "Not saved yet";
  - Show in Connections from the separate Usage history window brings the dashboard forward on Connections, with the slot and the app;
  - on a file whose cap has shortened retention, a 30-day Connections view shows "Not kept before … · Capacity shortened retention";
  - Week and Month line charts connect across fully recorded fractional buckets.
- *The Destinations table:*
  - Live with a busy browser: the table stays calm while the pointer rests on it;
  - History over 30 days: a selected destination's node lights up;
  - a LAN device appears under Local.

**0.17.0:**
- *First network use alerts:*
  - the balloon's appearance and click-through;
  - one balloon for three apps started within a minute;
  - a real three-day learning run on an administrator install;
  - the rule line for a standard user.
- *Don't record these apps and Clear history…:*
  - all apps, all time on the user's full `connections.db` and `usage-history.db`: the time until done, dashboard responsiveness, and Quit and sign-out during the clear, followed by the resume;
  - an excluded app with a day of traffic is absent from Connections History, from Usage history's Applications and from the Application resources rows, while device totals still match;
  - the Cleared band and Not saved slots on both strips, and the excluded application filter, in a light, a dark and a high-contrast theme.


