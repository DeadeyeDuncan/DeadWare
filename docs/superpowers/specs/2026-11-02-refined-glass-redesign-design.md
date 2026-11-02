# Refined glass redesign

Status: proposed on 2026-11-02; design approved section by section in conversation on 2026-11-02. Written spec awaiting review before the implementation plan.

## Intent

DeadWare's 0.6.33 glass refresh gave the dashboard consistent cards and recessed plots. Since then the app has grown to ten pages, nine themes, pop-outs, a widget and a live Connections globe, and the visual language has not kept pace: traces are straight-segment polylines, axes use dashed gridlines and a wide label gutter, the map is a muted wireframe with oscillating strands, and the sidebar uses text glyphs for icons. This redesign gives the whole app a sleeker, more modern finish while keeping every feature, setting, saved history and window behaviour exactly as it is.

The direction chosen is **refined glass**: an evolution of the current glass-card language with more depth, better typography and spacing, rewritten graph and map renderers, and polished controls. It is not a flat "mission control" reset, not a neon restyle, and not a framework change. The app stays on WPF; a SkiaSharp canvas for the map alone is the fallback if WPF cannot deliver the map treatment, and is not planned.

Everything here is presentation. Data pipelines, statistics, settings, histories, projection math, window placement, tray behaviour, update flow, Elder Scrolls theme features and High Contrast fallbacks are unchanged.

## Scope

In scope: the dashboard shell and all ten pages (Overview, Network, Connections, Battery history, Usage history, Alerts, Sensors, Cooling Health, Settings, Updates), graph pop-outs, the compact widget including its map-only mode, the Connections map in both globe and flat projections, and all nine themes.

Out of scope: new colors or palettes, logo changes, moving controls between windows or pages, new settings, changes to the data model, and any framework migration.

## Section 1 — Shared visual system

### Tokens

The nine palettes keep their eleven roles (`Background`, `Panel`, `Control`, `Line`, `Ink`, `Muted`, `Accent`, `Cpu`, `Gpu`, `Download`, `Warning`) unchanged. `Theme.Apply` derives these additional resources from the active palette so every surface agrees:

- `Hairline`: `Line` at about 45% alpha; the only brush used for borders and gridlines.
- `AccentSoft`: `Accent` at about 12%; selected navigation items and focus tints.
- `Surface1` and `Surface2`: `Panel` lightened and darkened by a few percent; elevation layering inside cards and the filled land on the map.
- `Glow`: `Accent` at about 22%; halos and the globe atmosphere.

All derived tokens flow through `DynamicResource` as the existing ones do, so theme switches and glass-level changes refresh open windows immediately. Under High Contrast they map to the system brushes; under Blackout, disabled transparency and High Contrast the glass fallbacks stay opaque and flat as today.

### Typography

Per-theme font families stay (Segoe UI, Candara, Calibri, Corbel, Bahnschrift/Consolas, Nordic lettering for Skyrim and Mage). A single scale applies everywhere: page title 26 semibold, card title 13 medium, large metric 34 with tabular numerals (`Typography.NumeralAlignment="Tabular"`) so readings do not jitter as digits change, body 13, captions and axis labels 10–11 in `Muted`. Small uppercase labels (MIN / AVG / MAX, section headers, table headers) get slight letter spacing.

### Cards and chrome

Cards use a 14 px radius, a single `Hairline` border, the existing soft top-edge highlight, and an inner vertical gradient of 3–4% so they read as glass with depth. The sidebar becomes a navigation rail: brand block with the skull mark, items with Segoe MDL2 Assets glyphs (present on Windows 10 and 11) replacing the ▦ ⇄ ◎ text symbols, an `AccentSoft` pill and a 3 px accent bar on the active item, and the theme picker, administrator restart and Quit in a compact cluster at the bottom. The rail keeps its 205 px width and the window its 900×640 minimum.

The page header becomes one toolbar row: title and a one-line subtitle on the left; GPU selector and history range on the right. The range control is restyled as a segmented pill with the same six choices, the same saved setting and the same synchronization across windows and pop-out menus.

### Controls

`App.xaml` restyles Button (pill shape, hairline border, accent focus ring), ComboBox, CheckBox, TextBox, DataGrid (no vertical gridlines, hover row tint, 10 px uppercase headers), ScrollBar (thinner, still themed) and ToolTip. Hover and press transitions are 120–150 ms. Pop-up menus keep opaque backing.

## Section 2 — Graph renderer

`HistoryChart` (Overview cards, pop-outs, compact widget) gets a rewritten drawing pass. `Prepare`, `SampleHistory.ReduceForChart`, the minimum–maximum band, statistics, the smoothing toggle, custom colors, line patterns, thickness and glow settings are untouched.

### Smooth traces

Points become a monotone cubic Hermite spline (Fritsch–Carlson tangents) emitted as cubic Bézier segments through `StreamGeometryContext.BezierTo`. Monotone interpolation never overshoots a real reading. Gaps still break the curve; each continuous run is its own figure. Dense 1–6 hour raw traces (already one point per pixel) remain polylines; the smoothed long-range average line gets the spline. The translucent area fill (`GraphDepth.Area`) follows the curve at its current strength; gradient fills were offered and not chosen. Dashed and dotted secondary series, runes on the Elder Scrolls themes, custom colors and the glow sliders apply to the new curve unchanged.

Trace geometries are cached per prepared data revision, plot size and style, instead of being rebuilt on every `OnRender`, so hover redraws cost only a few draw calls. The retained scroll animation between polls is kept.

### Axes and grid

Three solid `Hairline` gridlines at 0 / 50 / 100 (or the scaled equivalent for temperature, RPM and network) replace the dashed lines. Value labels move inside the plot, right-aligned just above each line, which reclaims the 74 px right gutter. When the RPM scale is shown it sits inside on the left the same way. Vertical hairlines mark wall-clock boundaries, labelled HH:mm beneath the plot and sliding left with the trace: every minute at 5 min, every 5 min at 15 min, every 15 min at 1 h, hourly at 3 h and 6 h, every 15 s at 1 min. "Now" stays pinned at the right edge. Compact mode shows at most two time labels. The "N min ago" caption goes; the selected range is visible in the toolbar and pop-out menus. The "avg + range" / "raw" hint moves into the hover chip.

### Hover crosshair

Moving the mouse over the plot draws a vertical hairline, a dot on each visible trace at the nearest sample, and a chip (`GlassPopup` fill, `Hairline` border) showing the time and each series' value in its own color: primary, secondary (upload or the compared device), fan RPM and pump RPM as applicable. The chip flips sides near the plot edges and shrinks to values only in compact mode. Nearest-sample lookup is a binary search over the prepared series. The chart redraws on mouse movement only, never on a timer, and clears the crosshair when the pointer leaves.

### Current reading and glow

The end-of-trace marker keeps its halo. The default trace shadow becomes a single soft under-stroke (about 4 px at 10%); when a graph's Neon glow setting is on, that setting's strength and the existing `GraphStroke` behaviour win.

### Other charts

`ThermalHistoryChart`, `BatteryHistoryChart` and `UsageHistoryChart` share the surface, hairline grid, inside labels, time ticks and hover crosshair through a common helper. Cooling Health temperature traces and Usage history averages get the spline; battery percent stays linear because it is step-like and its state coloring depends on exact segments. Range bands, markers, state colors, Day/Week/Month/Year navigation and exports are unchanged.

## Section 3 — Connections map

`ConnectionMap` is rebuilt as layered drawing passes. Projection math in `ConnectionProjection`, the camera, drag, wheel zoom, keyboard navigation, globe/flat switching, selection, the saved history, `ConnectionsPage` controls and the widget's map-only mode are kept.

### Surface

The ocean is a radial gradient on the globe (lighter toward the upper left, darker toward the limb) and a vertical gradient on the flat map. Land is filled: the bundled 110m land data is already closed exterior rings, so each ring becomes a `Surface2` fill with a hairline coastline on top. On the globe, ring points on the back hemisphere are clamped to the limb at their azimuth, which closes partially visible continents along the rim without inland artifacts. The flat map draws the ring set at 0° and ±360° under the existing clip so the antimeridian seam closes. Country borders remain faint hairlines; state borders keep their zoom-dependent fade; the graticule drops to about 20% alpha. A soft atmosphere halo (`Glow` radial gradient fading outward over about 6% of the radius) surrounds the globe, with a faint inner limb darkening. Inland lakes stay filled as land because the asset has no holes; the asset README records this.

### Arcs

The oscillating strands are removed. Each connection is one great-circle arc drawn in three strokes: a 6 px glow at about 6%, a 2.5 px mid stroke at about 18%, and a 1.2 px core whose opacity tracks activity (idle 35%, busy 90%). Download and upload share the arc; the core takes the active direction's color, and when both directions are active the two colors are drawn as hairlines offset ±1 px either side. Selected and hovered arcs go to full opacity with an ink hairline, as today.

### Particles

Along each active arc, one to three particles (by activity level) travel at a steady speed: download toward the origin, upload away from it. Each is a 2.5 px disc with a 7 px radial glow in the direction color. The seam and hemisphere guard in `DrawFlow` is reused so particles never cross the back of the globe. Particles run on the 40 busiest routes, halved in the widget, and only while the map is subscribed and visible.

### Nodes and origin

Endpoints are a soft glow disc (three times the node radius at about 25%), a solid core and a hairline ring; nodes with activity emit an expanding pulse ring on the existing animation phase. The origin becomes a core dot with two concentric rings, one expanding radar-style.

### Hover

`ConnectionMapPoint` gains optional `Label` and `Detail` strings; `ConnectionsPage` supplies the application or host, city and country, and ↓/↑ rates. Hovering a node (hit radius at least 9 px, 12 px in the widget) highlights its arc, shows a hand cursor and draws a chip beside it, kept inside the map bounds. Clicking still selects and opens the detail box. The widget keeps the larger hit targets and shows no chip.

### Legend and status

The "↓ Download ∿ / ↑ Upload ∿" text becomes two small colored legend chips in the top-left corner. The status summary line stays at the bottom. The map-only widget shows neither.

### Performance

Land fill geometry is cached with the camera, viewport and presentation exactly as the stroke geometry is today. Pens and brushes are frozen and cached. No new timers.

## Section 4 — Pages, pop-outs and widget

- **Overview**: six cards in the 2×3 grid. Header: tinted rounded square with the metric icon, title, and a lighter ghost cluster (pop-out, color dots) on the right. Large metric at 34 tabular, device line, chart, then MIN / AVG / MAX as three columns above a hairline. Network keeps separate download and upload readings.
- **Network**: chart card above the process table with the restyled DataGrid. The minimum-size checks (chart at least 65 px, table at least 70 px at 900×640) remain enforced by the preview tool.
- **Connections**: the map in a card with legend chips overlaid; the existing controls become a toolbar strip above it; the visible-node list and detail box become cards. Settings and export stay where they are.
- **Battery history, Usage history, Cooling Health**: the chart treatment from Section 2 and restyled tables and controls; no layout change.
- **Alerts**: rules and recent alerts become cards; threshold inputs and the 7/30-day hints keep their positions.
- **Sensors, Updates**: inherit restyled tables, inputs and buttons.
- **Settings**: grouped cards (Monitoring, Appearance, Widget, Startup, Pop-outs, History and alerts) with a section title each. Every control stays; nothing moves between windows.
- **Pop-out graphs** (`GraphWindow`, `CompactGraph`): same chart; chrome slims to the current reading, device name and the ⋯ options button on one row with the chart filling the rest. Always-on-top and the options menu behave as now.
- **Widget**: same card and chart language at the small scale; the map-only widget gets the filled globe or flat map with particles at half density and no hover chips. Size, snapping, always-on-top and remembered placement are untouched.
- **Themes**: all nine keep their palettes and fonts; Skyrim and Mage keep the stone texture, Nordic lettering and runes along traces; Blackout stays pure black and flat; High Contrast and disabled transparency fall back to solid surfaces exactly as today.

## Implementation choices

These are the builder's proposed defaults, not additional user requirements: the specific alpha values and pixel sizes above; Segoe MDL2 Assets for navigation glyphs; Fritsch–Carlson tangents for the spline; limb clamping rather than true spherical polygon clipping for land fills; three-stroke arcs; a 40-route particle cap; binary-search nearest-sample lookup; per-revision geometry caching.

Reuse `Theme.Apply` and `ApplyGlassResources` for tokens, `GraphDepth`/`GraphStroke`/`RuneTrace` for fills, strokes and runes, `ConnectionProjection.GreatCircle`/`Project`/`ProjectPath` for all map geometry, `DrawFlow`'s seam guard for particles, `UiSession` for range synchronization, and the OverviewPreview tool for rendering evidence. New pure helpers (spline, tick selection, nearest sample, limb clamp, particle placement) live in `DeadWare.Core` or static classes in the app so they can be unit tested without a window.

## Verification and delivery

- Branch `feat/refined-glass` from `origin/main` (0.8.0). `Dev` is 132 commits behind `main` and is not used. Pull request into `main` when complete. No force pushes.
- CI: `build.yml` gains a step after `publish.ps1` that runs the OverviewPreview renderer (`--render` and `--render-shell`) and uploads the PNGs and `shell-checks.json` as a workflow artifact. The renderer's theme list grows from seven to all nine themes and its shell routes from eight to all ten pages. Its existing checks (no native windows, synthetic-data labels, minimum Network chart and table heights, no history databases) keep gating.
- Unit tests: the monotone spline stays within the input range and preserves gaps; tick selection per history range; nearest-sample lookup; limb clamping of back-hemisphere ring points; particle positions stay on the visible hemisphere and inside the flat seam; `ConnectionMapPoint` label plumbing; derived token alpha values; range control synchronization. The existing 721 tests must stay green.
- Visual review: offscreen captures across all nine themes at 1220×900 and 660/1000 px Overview widths plus the 900×640 minimum, reviewed from the CI artifact and attached to the pull request.
- Not verifiable from CI, called out in the pull request for a desktop check on real hardware: mixed-DPI layout, the acrylic backdrop on a live desktop, animation smoothness, native input on the map.
- Order of work, each a reviewable checkpoint: CI render step → tokens, type scale and control styles → graph renderer → Connections map → pages, pop-outs and widget → docs (README features, CHANGELOG, `docs/previews/` note, map asset README note on lakes).
- Version: unchanged at 0.8.0 until the user decides; the builder suggests 0.9.0 for release since it is an app-wide visual milestone.

## Inline builder check

- No placeholders remain; every section names its files and the behaviours it keeps.
- Section 2's "avg + range / raw" hint relocation and Section 4's Alerts/Usage history cards were added during the written pass to cover the 0.7.0 and 0.8.0 pages; they follow the same rules as the approved sections.
- Scope is one implementation plan with six checkpoints; no decomposition needed.
- Ambiguities resolved: gradient fills are out; battery percent stays linear; the widget shows no hover chips; particles are capped; `Dev` is not the base branch.
