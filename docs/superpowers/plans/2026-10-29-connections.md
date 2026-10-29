# Connections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show this computer's observed network traffic on an interactive Connections globe with local history, plus an optional live-only 2D widget map.

**Architecture:** Extend the existing single ETW session with a bounded endpoint ledger and one immutable publication per collector sample. A shared background service enriches those observations from a local MMDB and records minute deltas to SQLite; history queries never replace its live state. A native WPF renderer uses the same geographic projection/data for the dashboard globe and flat widget.

**Tech Stack:** Existing .NET 10/WPF, Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0, Microsoft.Data.Sqlite 10.0.10; add pinned MaxMind.Db 5.2.0 (Apache-2.0) for local MMDB reading. Public-domain Natural Earth land/city assets. Existing xUnit and offscreen STA WPF fixture.

**Spec:** `docs/superpowers/specs/2026-10-29-connections-design.md`, approved 2026-10-29.

## Global Constraints

- Tab name is **Connections**. Keep the existing Network page and graphs working.
- Widget map is **live information only**, enabled by a separate default-false setting in Settings -> Widget graphs; it can run alone or with existing graphs.
- One ETW session, shared collector and resolver. No payloads, URLs, external per-IP lookup, reverse DNS, browser runtime, map-tile requests or firewall/driver changes.
- Local rolling **30 days**, **256 MiB primary database**, **15-second** target flush interval, bounded journal and explicit data gaps. Preserve incompatible/corrupt databases.
- All six themes, existing **900 x 640** minimum dashboard, **300 DIP** minimum widget width. Widget map row target **140-160 DIPs** including header.
- Public IP location is approximate; local/special/unknown addresses remain in tables without geographic invention. Origin is optional manually chosen approximate home city.
- Preserve original I:/DeadWare edits. Continue attached clean worktree/branch `codex/connections`, draft PR #26. Check, commit and push verified checkpoints; verify remote commit. No merge/tag/install is included in implementation approval.

## Review Focus

1. A pre-loss accepted ETW event races with a reset or a reused PID: process and endpoint identity must remain the same atomic decision. Task 1/5 pin this with interleaved/reset tests.
2. A widget-only launch while the dashboard is hidden or viewing history: recording and the live map continue with no historical values leaking into the widget. Task 5/7 exercise this path.
3. Wall-clock changes, sleep, a final event just before quit, or a minute-boundary publication: deltas must not duplicate, disappear silently, or bridge gaps. Task 1/2/5 cover event-time buckets, drains and coverage boundaries.
4. High-cardinality peers, a full/locked database, or a cancelled/malformed download: bound memory/storage/work, retain previous valid data, surface missing coverage. Task 1/2/3/5 use small injected limits.
5. The globe horizon, antimeridian and a very short high-DPI work area: paths must clip correctly and every widget selection remain accessible. Task 4/7 use projection and layout fixtures.

## File responsibilities and shared contracts

Names below are the integration contracts; implementation details remain with the owner. Use UTC DateTimeOffset values, monotonic seconds for rates, immutable/read-only published collections, checked/saturating byte arithmetic, and bound externally sourced strings before persistence/display.

**Core new files:** `ConnectionModels.cs` (shared records/enums), `NetworkAddress.cs` (address normalization/classification), `ConnectionLedger.cs` (bounded live counters/event-minute deltas), `ConnectionHistoryStore.cs` (SQLite/query/export), `ConnectionProjection.cs` (pure geographic math/grouping).

**App new files:** `ConnectionGeolocation.cs` (MMDB/cache), `ConnectionDatabaseDownload.cs` (bounded download/install), `ConnectionService.cs` (shared background enrichment/writer/lifecycle), `ConnectionMap.cs` (native drawing/input/animation), `ConnectionAssets.cs` (pack-resource geography/cities), `ConnectionsPage.cs` (live/history controls/table), `ConnectionsWidget.cs` (live-only compact adapter). Assets live in `assets/maps/`, embedded as WPF resources; `scripts/prepare-map-assets.py` records reproducible public-source transformation.

Core shared data types:

```csharp
public enum ConnectionProtocol { Tcp, Udp }
public enum ConnectionDirection { Both, Received, Sent }
public enum NetworkAddressKind { Public, Local, Special }
public sealed record GeoPoint(double Latitude, double Longitude);
public sealed record ConnectionLocation(string CountryCode, string Country,
    string? Region, string? City, GeoPoint? Point, string Source);
public sealed record ConnectionPeer(ProcessIdentity Process, string LocalAddress,
    int LocalPort, string RemoteAddress, int RemotePort, ConnectionProtocol Protocol);
public sealed record ConnectionDelta(DateTimeOffset Minute, ConnectionPeer Peer,
    long ReceivedBytes, long SentBytes, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);
public sealed record ConnectionTraffic(ConnectionPeer Peer, DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen, long ReceivedBytes, long SentBytes,
    double DownloadBytesPerSecond, double UploadBytesPerSecond);
public sealed record ConnectionSample(DateTimeOffset Timestamp, double MonotonicSeconds,
    long Sequence, string Segment, IReadOnlyList<ConnectionTraffic> Live,
    IReadOnlyList<ConnectionDelta> Deltas, DateTimeOffset? CoverageFrom,
    bool Available, bool Complete, long DroppedEvents, long LostEvents,
    long UnattributedEvents, string Status);
public sealed record LocatedConnection(ConnectionTraffic Traffic, ConnectionLocation? Location);
public sealed record ConnectionLiveView(DateTimeOffset Timestamp,
    IReadOnlyList<LocatedConnection> Connections, GeoPoint? Origin,
    bool Available, bool Complete, string Status);
public sealed record ConnectionHistoryRecord(string BatchId, string Segment,
    ConnectionDelta Delta, ConnectionLocation? Location, GeoPoint? Origin, bool Complete);
public sealed record ConnectionCoverage(string BatchId, string Segment,
    DateTimeOffset From, DateTimeOffset To, bool Complete);
public sealed record ConnectionHistoryQuery(DateTimeOffset From, DateTimeOffset To,
    string Search = "", string CountryCode = "", ConnectionDirection Direction = ConnectionDirection.Both,
    int Offset = 0, int Limit = 200);
```

`ConnectionHistoryRow` contains peer identity, first/last timestamps, byte totals, interval-average rates, saved location/origin and completeness. `ConnectionHistoryView` contains query, up to Limit rows, total matching rows, full-filtered-range byte/country/destination/unlocated/local summaries, up to 256 grouped geographic markers, recorded coverage/gaps, earliest retained time, storage bytes and status. Define these records in `ConnectionModels.cs` during Task 2; do not calculate summaries from the visible table page. `BatchId` is stable segment+sequence+recording-generation identity, so retries cannot add bytes twice. Never persist local-interface addresses; the storage/export representation intentionally omits `ConnectionPeer.LocalAddress`/LocalPort.

## Task 1: Honest endpoint observations and bounded accounting

**Own:** new Core `ConnectionModels.cs`, `NetworkAddress.cs`, `ConnectionLedger.cs`; modify `Recovery.cs`; tests `ConnectionLedgerTests.cs`, `NetworkAddressTests.cs`, `RecoveryTests.cs`.

**Consumes:** existing `ProcessIdentity`, process-ledger rate conventions. **Produces:** shared types above; `NetworkAddress.Normalize(IPAddress): IPAddress`, `NetworkAddress.Classify(IPAddress): NetworkAddressKind`; `ConnectionLedger(int maximumPeers=8192, int maximumDeltaKeys=32768)`, `Record(ConnectionPeer peer, DateTimeOffset timestamp, int bytes, bool send)`, `Take(DateTimeOffset timestamp,double monotonicSeconds): ConnectionSample`, `Break(long lostEvents=0)`. A break changes segment and clears unsafe live/rate state, reports dropped pending deltas, and never fabricates an interval of coverage.

- [ ] Write named regressions: `Receive_and_send_choose_remote_source_and_destination`; `Mapped_ipv6_matches_ipv4`; `Special_addresses_never_classify_public` (loopback, RFC1918, CGNAT, link-local, multicast, unspecified, documentation, benchmarking/reserved ranges; IPv6 ULA/multicast/link-local/documentation); `Peer_expires_after_60_seconds`; `Rates_use_actual_elapsed_seconds_and_zero_without_new_bytes`; `Event_minute_survives_publication_across_boundary`; `Limits_report_drops_without_unbounded_growth`; `Break_and_clock_discontinuity_do_not_bridge_rates_or_coverage`.
- [ ] Add `ProcessAttribution.Record(int pid,long eventTime,int down,int up,out ProcessIdentity? acceptedIdentity)` and preserve the bool-only overload by delegation. Test that identity is null for rejected/stale events and is the same generation that received bytes, even on PID reuse.
- [ ] Run `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~ConnectionLedgerTests|FullyQualifiedName~NetworkAddressTests|FullyQualifiedName~RecoveryTests"`; confirm missing behavior fails, implement narrowly, then require all filtered tests to pass. Retain valid zero bytes; reject negative/invalid ports, nonfinite time, impossible byte sizes and future event timestamps relative to a bounded sample without inventing rows. Publish read-only copies and expire by observation time.
- [ ] Review the diff and run the full core suite before committing `feat: account for observed network peers`; push this verified checkpoint.

## Task 2: Bounded durable minute history and filtered export

**Own:** Core `ConnectionHistoryStore.cs`, history models in `ConnectionModels.cs`; tests `ConnectionHistoryStoreTests.cs`. **Consumes:** Task 1 deltas and coverage. **Produces:** `ConnectionHistoryStore(string path,long maximumDatabaseBytes=268435456)`, `Initialize()`, `Append(IReadOnlyList<ConnectionHistoryRecord> records,IReadOnlyList<ConnectionCoverage> coverage,DateTimeOffset now)`, `Read(ConnectionHistoryQuery query): ConnectionHistoryView`, `ExportCsv(TextWriter writer,ConnectionHistoryQuery query)`, `StorageBytes`.

- [ ] Write failing tests for: restart round-trip and same-name/new-generation process distinction; idempotent BatchId replay; distinct minute/location/origin preservation; zero-traffic coverage versus missing/paused interval; full-filter summaries with a 200-row page; search/country/direction filters with half-open UTC intervals; CSV quoting and leading `= + - @`/control-character neutralization; no local address in stored columns or exports; 30-day pruning; small injected database cap pruning oldest complete buckets; corrupt/locked/newer-schema files left intact.
- [ ] Run `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~ConnectionHistoryStoreTests`, observe failures, then implement schema v1 with indexed timestamps/filter keys and one writer. Keep batch identity in a bounded dedup table pruned with data; persist deltas exactly once and minute coverage separately. Use parameterized SQL, UTC integer timestamps, transactional validate-before-write, and query-side aggregation/pagination.
- [ ] Copy the existing graph-store capacity pattern: max_page_count limits the primary database, 8 MiB journal target and periodic checkpoints; prune expired/oldest minute groups before retrying a capacity-limited append. Bound each append to 32768 records and process larger drains in chunks. Preserve known rows if a later write fails. Show earliest available history when the cap shortens retention. Query limits clamp to 1..500, map summaries cap at 256; reject invalid intervals.
- [ ] Rerun filtered and full core tests; review and commit/push `feat: save bounded local connection history`.

## Task 3: Local geolocation database and explicit download/update

**Own:** App `ConnectionGeolocation.cs`, `ConnectionDatabaseDownload.cs`, csproj package reference; app tests `ConnectionGeolocationTests.cs`, `ConnectionDatabaseDownloadTests.cs`; third-party dependency notice. **Consumes:** Task 1 location/address types. **Produces:** `IConnectionGeolocation.Lookup(string address): ConnectionLocation?`, implemented by `ConnectionGeolocation(string databasePath)` with `Reload()`/`Dispose()` and `DatabaseDate`/`Status`; `ConnectionDatabaseDownload.UpdateAsync(string databasePath,IProgress<double>? progress,CancellationToken cancellationToken): Task` with an injected HttpClient constructor for tests. This resolver has no HttpClient and never performs per-IP network calls.

- [ ] Write failing tests using deterministic tiny MMDB fixtures or an injected parsed-record seam: special addresses skip lookup, missing/invalid/NaN/out-of-range coordinates yield unknown, country-only survives, malformed strings are bounded, IPv4/IPv6 work, repeated lookup is cached and cache is bounded to 8192. Verify the reader is never disposed while an active lookup uses it.
- [ ] Write download tests with fake handlers/streams for cancellation, 404 current-month fallback to previous month, redirects outside the official HTTPS download host, oversized/truncated gzip, decompression bound, invalid/wrong-type MMDB, a failed replacement leaving old data usable, and success atomic swap. Use official `https://download.db-ip.com/free/dbip-city-lite-YYYY-MM.mmdb.gz`; cap compressed input at 256 MiB and expanded file at 512 MiB, expose progress and allow only one update at a time. Validate MMDB metadata/type and actual record structure before replacement; never infer authenticity from MD5/SHA1 alone.
- [ ] Run `dotnet test tests/DeadWare.App.Tests -c Release --filter "FullyQualifiedName~ConnectionGeolocationTests|FullyQualifiedName~ConnectionDatabaseDownloadTests"`, observe failures, add pinned MaxMind.Db 5.2.0 and implement with temporary unique files, cancellation cleanup and atomic old-reader swap. Load an existing valid database on a worker, not a WPF constructor. Show database build date and outdated state after 45 days. Update/first download remains user initiated and does not run in tests.
- [ ] Review dependencies, licenses and outbound destinations; pass filtered tests, then commit/push `feat: resolve network locations locally`.

## Task 4: Native globe and flat map with shared projection

**Own:** Core `ConnectionProjection.cs`; App `ConnectionMap.cs`, `ConnectionAssets.cs`; `assets/maps/land.json`, `cities.json`, `README.md`; `scripts/prepare-map-assets.py`; new `ConnectionProjectionTests.cs` and `ConnectionMapUiChecks.cs`; map resources in csproj and Natural Earth notices. **Consumes:** `GeoPoint`, `ConnectionLocation`, `ConnectionDirection`. **Produces:** `MapCamera(double Longitude,double Latitude,double Zoom,bool Flat)`, `MapCoordinate(double X,double Y,double Depth,bool Visible)`, `ConnectionProjection.Project(GeoPoint point,MapCamera camera): MapCoordinate`; `ConnectionMapPoint(string Key,GeoPoint Point,long ReceivedBytes,long SentBytes,bool Selected)`; `ConnectionMap.SetData(IReadOnlyList<ConnectionMapPoint> points,GeoPoint? origin,bool available,string status)`, `Flat`, `AnimationEnabled`, `ResetView()`, `ZoomBy(double factor)`, and `event Action<string>? PointSelected`. `ConnectionAssets` exposes immutable land rings and `HomeCity(string Id,string Name,string Country,GeoPoint Point)` entries.

- [ ] Write failing pure tests for center, rear hemisphere, horizon, ±180 wrapping, polar latitude, finite zoom/clamping, great-circle arc sampling and flat-map dateline splitting. Group matching city/coordinate destinations, maintain stable selection keys and cap draw paths/markers at 256 (overflow reflected in a visible count).
- [ ] Prepare simplified world land from Natural Earth 1:110m and city choices from its populated-places data; use a pinned official-source revision, record upstream URL/version/SHA-256/transformation and preserve public-domain notices. Embed compact coordinate/city JSON as WPF Resource, independent of current directory. Reject invalid asset shape/coordinates at load without crashing other monitoring. Asset-preparation script is a development-only fetch/transform, not a runtime dependency.
- [ ] Run `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~ConnectionProjectionTests`, observe failure and implement pure spherical/flat math. Draw the map in one FrameworkElement.OnRender using cached/frozen geometry, hemisphere clipping and bounded great-circle arcs. Native drag/zoom/hit testing invokes camera/selection changes; use Theme.GraphKey download/upload roles, distinct solid/dashed or direction cues, optional static mode, and transparent counts/unknown origin indicator. Never put destinations behind an incorrect front-face fill or connect flat-map edges across the full screen.
- [ ] Add offscreen checks to the EXISTING STA Application fixture via `ConnectionMapUiChecks.Run(...)`; do not instantiate a second WPF Application. Render globe/flat, empty/missing/special-only data, selected paths, all six themes, 284x145 widget viewport and narrow/wide dashboard sizes. Animation subscribes only while loaded/visible and enabled, caps to 30 FPS, unsubscribes when hidden/unloaded, and never increments synthetic traffic counters. Pure fixture PNGs may be saved under ignored artifacts; no real endpoint samples.
- [ ] Pass focused core/map UI checks, review screenshot outputs, commit/push `feat: render native network activity maps`.

## Task 5: Single capture publication and shared connection lifecycle

**Own:** App `ConnectionService.cs`, `EtwNetwork.cs`, `Collector.cs`, `UiSession.cs`; Core settings fields/normalization in `Monitoring.cs`; tests `ConnectionCaptureTests.cs`, `ConnectionServiceTests.cs`, `ConnectionSettingsTests.cs`, and existing shutdown tests if required. **Consumes:** Tasks 1-3 models/store/geolocation. **Produces:** `[JsonIgnore] public ConnectionSample? Reading.Connections {get;init;}`, `ConnectionService.Live: ConnectionLiveView`, `Start()`, `TryRecord(Reading reading)`, `Configure(bool recording,GeoPoint? origin)`, `ReadViewAsync(ConnectionHistoryQuery query)`, `ExportCsvAsync(string path,ConnectionHistoryQuery query)`, `StopAsync()`. Expose the one instance as `UiSession.Connections`. Settings add `ConnectionsRecording=true`, `WidgetConnectionsMap=false`, `ConnectionsHomeCityId=null`, `ConnectionsAnimation=true`; validate city ID against bundled choices at service/UI integration.

- [ ] Write failing tests for send/receive endpoint normalization across all eight TraceEvent event variants; accepted identity/loss atomicity; cumulative loss resetting once (not every later sample); diagnostics JSON excludes all connection endpoint metadata; Reading publication remains backward compatible with existing positional constructors. Reflect real TraceEvent endpoint property types and UDP IPv6's upstream `UpdIpV6TraceData` spelling before subscribing.
- [ ] Introduce one accounting gate in EtwNetwork around attribution plus endpoint updates/loss/reconciliation/publication. Use Task 1's accepted identity overload, with no second PID resolution. Keep existing `Take(double now)` compatibility where needed; collector uses a combined `TakeSnapshot(DateTimeOffset timestamp,double now)` once, publishing process totals and ConnectionSample together. Mark failed/disabled/stale samples unavailable; views never call Take.
- [ ] Write failing service tests for bounded queue/caches; shared live snapshot; zero new bytes at quiet polls; enabled recording with dashboard hidden; pause/resume generation changes and coverage gaps; minute-boundary/event-time deltas; duplicate/old publication rejection; final sample plus queued data drained on normal quit; ETW/loss/overflow/sleep gaps; storage/resolver failure with live counters still updating; stored geographic/origin values never rewritten on later lookup updates. Use injected clock, resolver and small limits, with a local temp database.
- [ ] Implement the background pipeline with a 120-publication bounded channel and 32768-record maximum buffered delta batch, 15-second PeriodicTimer flush, independent immutable Live publication, visible dropped observations and separate storage/lookup statuses. Resolve/cache away from callbacks and the UI; split valid coverage at UTC minute boundaries. Check recording generation on acceptance, not only at delayed dequeue. Preserve accepted pre-pause writes, omit paused observations, and keep a resume gap. Bound late clock-skew events without moving them into invented current buckets.
- [ ] Wire UiSession subscriptions/startup/shutdown in existing ownership order. Subscribe once before collector start; on shutdown stop/await collector, capture final buffered endpoint deltas before ETW disposal, complete/drain ConnectionService, then dispose its resolver. Continue writes when the main window hides. Keep live source independent of history-query state. Configure serialization excludes endpoints in --diagnose output; do not change opt-in diagnostic privacy accidentally.
- [ ] Run focused app/core service/settings/capture/shutdown tests, then full `dotnet test DeadWare.slnx -c Release --nologo`. Commit/push `feat: connect live endpoint capture and history service`.

## Task 6: Connections page, filters, history playback and controls

**Own:** App `ConnectionsPage.cs`, sidebar/page-routing changes in `Windows.cs`; app test `ConnectionsPageUiChecks.cs` wired into existing STA fixture. **Consumes:** Tasks 4/5 map, city list and UiSession service. **Produces:** `ConnectionsPage(UiSession session)` plus internal `ApplyLive(ConnectionLiveView view)` and `ApplyHistory(ConnectionHistoryView view)` seams for deterministic UI tests.

- [ ] Write fixture checks that the sidebar reads Connections; the inherited GPU/history toolbar is hidden there; globe/table fit ~671 DIP page width at a 900x640 shell and expand in wider layouts; live/history labels and rates/totals differ correctly; local/unlocated/incomplete observations remain visible and included in summary counts.
- [ ] Build page controls: Live/History; Today/7 days/30 days; editable from/to interval; application/IP search, country and Both/Received/Sent filter; paginated peer table (200 per page) and totals; draggable map/table split; globe/2D, zoom/reset/pause controls; origin-city picker with no-location default; recording pause/resume, storage/earliest-time status and CSV export to a new file. Keep database download/update/cancel and date/attribution visible with clear vendor-contact text. Use local-day boundaries converted to UTC and half-open query intervals.
- [ ] Add the minute cursor and play/pause within History only. Query one selected minute at a time during playback, with generation+cancel guards, one request in flight and empty intervals shown empty. Do not prefetch 30 days of peer rows to animate. Historical controls cannot mutate ConnectionService.Live. During playback/table selection, use saved point/origin values and label averaged historical rates explicitly. Return to live cancels/discards stale query results.
- [ ] Run focused offscreen checks for empty/permission-denied/no-database/downloading/cancel/storage-failure states, pagination/selection/filter synchronization, stale query replies, date validation and bounded playback. Call services through async handlers; avoid unobserved exceptions or native collectors in fixture constructors. Use the existing theme resources and accessibility names.
- [ ] Review six-theme/minimum/wide screenshots and focused tests; commit/push `feat: add Connections globe and history page`.

## Task 7: Optional live-only widget map and Settings integration

**Own:** App `ConnectionsWidget.cs`, widget/Settings changes in `Windows.cs`, settings setters in `UiSession.cs`; sizing overloads in `WidgetGraphSelection.cs`; core `ConnectionWidgetSettingsTests.cs`, app `ConnectionsWidgetUiChecks.cs` and `NativePlacementMinimumTests.cs`. **Consumes:** Task 4 map and Task 5 live service/settings. **Produces:** `ConnectionsWidget(UiSession session)` with `Refresh()` reading ONLY `session.Connections.Live`; `UiSession.SetWidgetConnectionsMap(bool enabled)`; `WidgetGraphSelection.MinimumHeight(int count,bool connectionsMap)`/`DefaultHeight(int count,bool connectionsMap)` overloads preserving old false-map results.

- [ ] Write failing settings/layout checks: absent/null legacy WidgetGraphs retains existing graph choices and map=false; setting toggles persist immediately without restart; map-only works; both-off shows the empty message; map+four graphs preserves every card; history playback/filters never change widget data; loading only remembered widget while dashboard is hidden still updates live data.
- [ ] Add Settings -> Widget graphs checkbox **Connections map (live)**, separately from graph kinds. Include map state in widget layout signature. Add a dedicated 150-DIP row, compact 2D ConnectionMap and current observed byte-rate/direction legend; no history table, range or replay controls. `Refresh` uses current Live freshness/status and resolved points only. Pause map animation when not visible; it owns no resolver, ETW session, history read or recording policy.
- [ ] Preserve native saved placement/pin behavior. Extend minimum/default height calculation by the map row only when enabled; for short work areas, allow the widget content to scroll instead of forcing an unreachable height or clipping cards. Verify existing non-map sizing unchanged and 96/144/192 DPI placement clamping on a short work area. Respect selected-theme and configured line-color changes.
- [ ] Run focused settings/widget/placement/offscreen tests and compare 300-wide map-only, mixed and all-selected layouts across themes. Commit/push `feat: add optional live Connections widget map`.

## Task 8: Whole-feature verification, documentation and GitHub gate

**Own:** README.md, THIRD-PARTY-NOTICES.md final reconciliation, docs/validation.md, PR body/checklist; fix only relevant discovered defects in their owning modules. **Consumes:** Tasks 1-7. **Produces:** verified source implementation and packaged local review artifact, with explicit native/performance evidence limits.

- [ ] Check the approved spec section by section, resolve any gap in the owning task, and run a fresh independent whole-diff review focused on concurrency/loss/privacy/storage/projection/widget independence. Repair findings and rerun affected checks. Ensure network-data fixtures are synthetic and no user database/download is tracked.
- [ ] Run `dotnet build DeadWare.slnx -c Release --nologo`, `dotnet test DeadWare.slnx -c Release --nologo`, `git diff --check`. Run the existing publishing script with a validation-only artifact version consistent with the untagged branch; verify bundled map resources/MMDB reader/license inventory. Do not tag or claim a published update.
- [ ] Set DEADWARE_UI_OUTPUT to ignored artifacts and run the focused WPF fixture. Inspect globe/flat/page/widget PNGs in all six themes, minimum/default/wide sizes, unknown origin, no-data/denied/history states and selected paths. Measure native synthetic map frame preparation/rendering with 256 markers and worst-case paths; record actual timings, hardware/context and animation visibility behavior, not an unmeasured performance claim.
- [ ] If host permits elevation, run a short real ETW smoke using a temporary local test harness and non-sensitive local traffic, with no hardware collector or persistent user settings. Verify IPv4/IPv6/UDP/TCP observations and cleanup of only the unique trace owned by the test. If not possible, report that limitation explicitly. Do not upload observed endpoints/logs; delete only test-owned temporary files if cleanup is needed.
- [ ] Update README network/privacy/history/widget sections to reflect actual behavior, DB-IP download contact and approximate geography; document 30-day/size-limited history, maximum unsaved window, no initial backfill and no final-storage inference. Record validation counts/screenshots/synthetic-vs-native limits in docs/validation.md.
- [ ] Update plan checkboxes and PR #26 around final behavior, commit and push. Verify GitHub head SHA equals local HEAD and wait for relevant CI checks; fix failures within scope. Queue a unique chronicler BRIEF with actual thread ID at the verified completed implementation gate. Report PR, evidence and remaining release/native limitations. Keep worktree attached for review; merge/tag/install require their separate authorized gate.

## Scheduling and self-review

Task 1 establishes shared models. Tasks 2, 3 and 4 can then proceed independently with disjoint ownership (coordinate csproj/notices changes); Task 5 integrates them. Tasks 6 and 7 share Windows.cs/UiSession.cs, so serialize shell edits or give those files to one integrator. Task 8 is the final gate. For subagent execution, require a fresh reviewer for each deliverable before dependent integration; workers must accommodate other edits and never revert unrelated work.

Self-review completed: every approved spec section maps to Tasks 1-8; shared types/signatures have one definition; publication/coverage/BatchId semantics cover restart and idempotency; the five Review Focus risks each have an owning test step; database/map/network input bounds are explicit. Native projection internals, SQL layout and cached drawing representation remain builder choices constrained by the tests. Baseline at c1fbcc3 is 481 passing tests (380 core, 101 app); recheck after any implementation mutation.

Sources checked 2026-10-29: https://www.nuget.org/packages/MaxMind.Db/5.2.0 ; https://github.com/maxmind/MaxMind-DB-Reader-dotnet ; https://db-ip.com/db/format/ip-to-city-lite/mmdb.html ; https://www.naturalearthdata.com/about/terms-of-use/ . These primary references informed dependency and asset choices; the approved spec remains the behavioral contract.
