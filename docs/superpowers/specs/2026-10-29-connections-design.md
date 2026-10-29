# Connections: geographic network activity

Status: proposed design, awaiting user approval before implementation.

Design gate evidence (2026-10-29): a fresh builder review returned "nothing important left". The unchanged latest-main baseline passed `dotnet test DeadWare.slnx -c Release --nologo`: 380 core tests and 101 app tests, zero failed/skipped. This verifies the implementation starting point, not the proposed Connections feature; no feature code or native network capture has been run.

## Intent and requested behavior

Help a person see where this computer exchanges network data and which applications account for it, with country/city context when available. Add a sidebar page named **Connections** beside Network. The user requested live traffic plus saved history, supplied https://cybermap.kaspersky.com/ as visual inspiration, and requested an optional small 2D map in the desktop widget, enabled in Settings. The widget must show **live information only**, independently of any history selection in Connections. Keep GitHub updated during the project.

The following defaults are proposed by Codex rather than decisions already made by the user: native WPF rendering, a local DB-IP City Lite database, 30-day local history retention with a storage cap, minute-resolution historical playback, and an optional manually selected approximate home city. These may be revised during design review.

## Screen and interaction

Connections opens with a large interactive globe, inspired by the reference's dark globe, luminous locations, and curved animated paths. Fit it into DeadWare's existing navigation, typography and six themes. Use DeadWare's download/upload colors; support a readable light-theme rendition. Do not embed the reference site or its traffic feed.

- Drag to rotate; provide zoom, reset view, pause animation and a 2D world-map toggle.
- Mark public remote locations. In live mode, directional moving highlights distinguish sent and received traffic; activity size/intensity conveys relative observed volume. Aggregate crowded locations and cap rendered paths to preserve responsiveness. A selected app or destination highlights its own activity.
- A synchronized searchable table lists application, remote IP and port, TCP/UDP, country/region/city where known, sent/received bytes, live rates, and last observed activity. Support filtering by application, country, direction and time. Show location confidence as approximate, with country-only and unknown fallbacks.
- Summary values cover observed sent/received bytes, destinations and countries for the selected time window. Unlocated and local-network traffic remain included in the relevant totals and table, with explicit counts beside the map.
- Provide **Live** and **History** modes. History offers Today, 7 days and 30 days, a selected interval and a minute-resolution time cursor with play/pause. Clearly distinguish historical interval totals from current rates. Only animate recorded intervals during playback; empty intervals stay empty.
- Keep map controls and the table usable at the existing minimum dashboard size. Allow the map/table divider to adapt to available room. Table access remains available with animation disabled.

The source point for geographic arcs is an optional approximate home location selected by the user from an offline city list. No Windows precise-location permission or automatic external public-IP discovery is needed. Until a home location is set, show destination activity and a clearly nongeographic **This PC** indicator; do not invent a geographic origin. Changes to the home city affect the visual origin and carry an approximate-location label, not measured routing claims. Historical rows retain the origin choice that applied when recorded, or unknown.

## Optional live widget map

Add **Connections map (live)** to Settings -> Widget graphs, off by default. Persist this choice through the existing settings codec and window restoration. Store it as a separate default-false setting: the existing graph-kind list defaults legacy/null selections to all kinds, so extending that list must not silently enable the map. It can be combined with the existing CPU/GPU, temperature, RAM and network graphs or used on its own.

Use a simplified 2D world map with current destination activity, direction colors, and a compact legend. It has no history mode, replay controls, or historical totals. It always reads the latest live connection snapshot even while the main Connections page displays or plays history. No separate collector or database query is created for the widget. When the dashboard is hidden, the live map continues through the existing shared collector. When the widget map is not visible, suspend its animations.

Respect compact sizing and existing widget graph-selection behavior; do not squeeze the full Connections table or globe controls into the widget. Give the map its own bounded-height row (target 140-160 pixels including its header), update minimum-height/placement logic, and handle small work areas without clipping the remaining selected graphs. The widget's no-selection message appears only when both the existing graphs and map are off. Show a short honest empty/unavailable state when there is no observed traffic or capture is unavailable. A static rendering and readable labels remain usable with animation paused/reduced.

## What the map means

Capture traffic metadata, byte counts and remote peers, never packet payloads, URLs, messages or file content. Sent/received describes observed byte direction; it does not establish which side initiated a connection. UDP entries are observed peer activity. An idle TCP connection with no observed traffic need not appear; this is not a complete socket inventory.

IP geography is approximate. Cloud services, CDNs, proxies and VPNs can make the observed endpoint differ from the final service location or storage location. Paths are illustrative links between endpoints, not measured internet routes. Country or city does not establish whether activity is safe, unsafe, or an attack. Do not copy the reference's threat labels or fabricate traffic to make the display busy.

Loopback, private/LAN, link-local, multicast, unspecified and other special addresses are classified locally and excluded from geographic lookup and globe placement. Unknown public locations remain visible in the table. Treat IPv4-mapped IPv6 addresses consistently and support both IP families.

## Data collection and architecture

Extend the existing ETW TCP/UDP IPv4/IPv6 byte callbacks to retain normalized local/remote addresses and ports, protocol, event timestamp, direction and process identity. Reuse the existing single trace session and administrator capability state. Do not introduce packet capture drivers, firewall changes or a second network sampler.

Preserve PID plus process-creation generation attribution; events predating the current process or with unresolved identities must not be assigned to a guessed application. Account for unatttributed observations separately where possible. Process exit removes live rows after expiry but never reassigns saved rows to a new process using the same PID.

Use a bounded, synchronized in-memory ledger and immutable published connection snapshots. ETW callbacks perform inexpensive normalization/counter updates only. Cap unique peer keys and caches, expire stale live peers, expose dropped/overflow observations, and bound the work per publication. Define live activity as traffic observed within the latest 60 seconds; calculate rates from actual sample timing, avoiding frozen old rates after sleep or stopped capture.

Geolocation, history writes/queries and expensive map preparation run in background services, outside ETW callbacks, hardware polling and the WPF UI thread. Publication cadence follows the shared collector without coupling globe animation frequency to hardware polling. Both the main page and widget consume the same live snapshots. Main-page history queries have their own selected interval and stale-result guard; they never replace shared live state. History recording continues while the dashboard is hidden, independent of widget visibility. Any demand-based optimization must keep endpoint collection/geolocation active while recording is enabled or a live Connections/widget surface needs it; closing one surface cannot stop another or stop recording.

ETW loss, capture stop, overflow, sleep/resume and process identity failures propagate an honest completeness/freshness state. Preserve already saved valid observations, mark affected intervals incomplete or missing, and reset affected live counters/rates at recovery boundaries. Do not stretch observations through downtime or pretend discarded data was recorded. Existing interface totals and per-app monitoring remain available under their current capability rules.

## Local geolocation and map assets

Use a downloaded local MMDB database for destination lookups; proposed source is **DB-IP City Lite**. The vendor offers a monthly IPv4/IPv6 city dataset with approximate coordinates under CC BY 4.0. Include attribution in Connections and third-party notices. Resolve each unique public IP through a bounded cache and reuse the result for both maps. No observed remote IP is submitted to an online geolocation or DNS service; reverse DNS and hostname inference are outside this version.

Offer a clearly labeled **Download location database** action when no database is available and **Update location database** afterward. Download directly from the official vendor over HTTPS, with cancel/progress and bounded compressed/decompressed sizes. Validate the MMDB structure, metadata and coordinate fields before replacing the old file atomically. Show database date and an outdated-data message; failed/cancelled updates leave the previous database working. Show IP/app traffic without a location database. No account, API key or paid service is required for this proposed source. Do not place the database in Git or commit a user traffic sample.

Bundle appropriately licensed, simplified world geography and an offline city-selection dataset. Prefer public-domain Natural Earth data with a recorded source/version and notices. Render locally in native WPF; no WebView, external map tiles, Electron or browser runtime. A renderer may use projected drawing or WPF 3D, at the builder's discretion, provided it delivers a recognizable rotatable globe with correct front/back occlusion, geographic placement and responsive controls. Do not reproduce Kaspersky assets or code.

Network activity from a user-triggered database download is ordinary traffic and may itself appear in Connections. Clearly describe what the download contacts; local lookups work offline afterward. Pause animation when the relevant surface is hidden, cap visible destinations/paths and support reduced/static motion. Actual native performance must be measured before claiming the implementation is lightweight.

## Saved history

Store a separate SQLite database under %LOCALAPPDATA%/DeadWare, independent of battery, thermal and graph history. Recording is enabled by default for this requested feature and has a visible pause/resume switch. Record minute buckets of observed sent/received byte deltas with application identity/name, remote peer and port, protocol, first/last seen, source session, completeness state and geographic result when available. Rates come from bucket duration and are labeled interval averages in History. No packet payloads or URLs are collected.

Historical geography is a snapshot of the lookup that was available at recording time; a database update must not silently relocate older traffic. Missing geography can remain unknown. Retain the selected approximate home origin or unknown with the recording interval. Do not store local interface IPs unnecessarily; persist only the endpoint metadata needed for the agreed display.

Proposed retention is a rolling 30 days, with a 256 MiB primary-database cap and bounded journal. Use bounded queues and batched writes, with a normal-quit drain and a documented maximum unsaved interval (target 15 seconds). Prune the oldest complete buckets to maintain the configured bound and show the actual earliest available time if volume forces retention below 30 days. A full/locked/corrupt/newer-schema database reports unavailable recording while live monitoring continues; never delete or reset an incompatible database automatically. Recording pause and gaps remain observable. Queries use indexed bounded aggregation/pagination so history size does not freeze the UI.

Settings expose recording state, retention/storage information, and CSV export of the current filtered history to a new local file. Export has clear columns and neutralizes spreadsheet formula-like application/location strings. A destructive history-reset action is outside this initial version. Document that histories begin only when this feature records and remain device-local. Never upload history, application names or endpoints to GitHub, diagnostics or a cloud service by default.

## Integration, GitHub and delivery

Start from GitHub main c1fbcc3 (v0.6.20 merge). The original I:/DeadWare checkout is behind and contains existing local edits; preserve those edits. Use the attached managed worktree and branch codex/connections. Keep the design and subsequent tested implementation checkpoints pushed to GitHub, with a draft PR tracking intended behavior, validation and remaining work. A branch push is not an app release. Merge/tag/install remain separate from this proposed design gate.

Keep changes concentrated in connection-domain models/aggregation/storage and services, ETW/collector publication, a Connections page/map renderer, widget map integration, settings, and documentation. Reuse the existing SQLite, shutdown, theme and shared-collector patterns without refactoring unrelated graph/sensor logic. Update README privacy and network-history claims to match actual behavior.

## Verification and acceptance

Before implementation, verify the latest-main build/test baseline. During implementation, prove:

1. IPv4/IPv6 TCP/UDP send/receive correctly normalize endpoints and retain byte direction; process reuse, stale events and loss do not misattribute traffic.
2. Special addresses never reach the geolocation resolver; malformed/missing database data remains unknown and safe to display; local lookup has no per-address outbound network calls.
3. Live snapshots, caps, expiry, zero-traffic rates and sleep/resume behave honestly. Main history selection never changes live widget output, including when only the widget remains visible.
4. History survives restart, records deltas without double counting, respects gaps/pause, preserves historical locations, and enforces retention/queue/storage bounds. Locked/corrupt/newer-schema failures do not interrupt live collection.
5. Globe and flat-map projection handle horizon occlusion, antimeridian, poles, rotation and zoom; marker/table selection agrees, and no-data/unlocated/capture-denied states are readable.
6. Render all six themes and compact/wide layouts, including a map-only widget and mixed selected graphs. Exercise widget setting persistence, hidden-page animation suspension and theme changes.
7. Run the Release build, appropriate automated suite, focused rendered UI checks, and a real Windows elevated ETW smoke check if the host permits it. Report any native capture, responsiveness, long-duration history, hardware, or live geographic accuracy limits explicitly.
8. Verify pushed commit/PR state and CI. Complete a release only after the normal release gate and authorization; do not claim a user-visible update merely from a source push.

## Source references

- Existing source: EtwNetwork.cs, Collector.cs, UiSession.cs, Windows.cs, Core/Monitoring.cs, Core/Recovery.cs, settings/widget selection, history services, README.md and docs/development.md.
- Local wiki: [[concepts/deadware-hardware-monitoring-project]], [[concepts/deadware-monitoring-reliability-hardening]], [[concepts/deadware-release-and-validation]].
- Visual inspiration: https://cybermap.kaspersky.com/ (inspected 2026-10-29).
- Proposed database and terms: https://db-ip.com/db/download/ip-to-city-lite (inspected 2026-10-29).
- IP geolocation limits: https://support.maxmind.com/knowledge-base/articles/maxmind-geolocation-accuracy (inspected 2026-10-29).
