# Refined glass redesign (0.9.0)

Spec: `docs/superpowers/specs/2026-11-02-refined-glass-redesign-design.md`. Plan: `docs/superpowers/plans/2026-11-02-refined-glass-redesign.md`. Pull requests #58 (foundation), #59 (shell and charts), #60 (globe and cards) and the documentation/release pull request. Presentation only: no setting, history, window or hardware behaviour changed.

## What changed visually

- **Tokens and controls.** Each of the nine palettes derives `Hairline`, `AccentSoft`, `Surface1`, `Surface2` and `Glow`. Buttons, text boxes, combo boxes, check boxes, tooltips and data grids use hairline borders, a 2 px accent focus ring, 10 px muted uppercase grid headers and a hover row tint. Panels are `Card` borders (radius 14).
- **Shell.** The rail shows a 16 px vector glyph per route with an accent bar and soft fill on the active page; the brand block is a 30 px logo and a 19 px semibold name; the theme picker sits under a small caption. The header is one row: title (26) and a subtitle (CPU · GPU · RAM · last sample age) on the left, GPU picker, segmented range pill and widget button on the right. Overview cards open with a tinted icon tile. Settings groups its controls into six cards.
- **Live graphs.** Monotone-spline traces (raw dense ranges keep the polyline), a 1 px hairline grid under a `GuidelineSet`, value labels inside the plot, `HH:mm` wall-clock ticks (seconds on the 1-minute range) with a right-aligned "Now", a faint under-glow in the trace color, and a hover crosshair with a `GlassPopup` chip only over real samples. Geometry is cached per poll, size and theme.
- **History charts.** Cooling Health, Battery and Usage share the grid and chip. Thermal windows and usage buckets that connect are splines; battery percent stays a polyline.
- **Connections.** Gradient ocean, filled land (limb-clamped on the globe, unwrapped with seam copies on the flat map), hairline coast and graticule, limb darkening, atmosphere glow; static arcs with 6 px and 2.5 px glow passes and a 1.2 px core per active direction (two lanes when both flow); particles on the forty busiest routes; glowing nodes with a pulse ring; a radar origin; legend chips; hover chips with process, location and rates. The widget map keeps a 12 px hit radius and draws no chips.
- **Pages, pop-outs, widget.** Connections controls in a card strip above a map card with a card detail box; Network and Sensors toolbar strips; compact pop-outs with one-row chrome; card-styled widget graphs.

## Values tuned from the spec defaults

- `Surface1` mixes the panel 3 % toward white (spec 4 %) so the derived-token test's bound holds.
- Plots under four label heights (the ~55 px overview card plots) label only the outer two value lines; chart bottom margin is the label height plus 6 px (compact) or 9 px.
- Core arc opacity is `.30 + .50 × level` (spec `.35 + .55`), keeping the existing activity-fade contract; glow passes are `.06`/`.18` (`.12`/`.30` selected or hovered) as specified.
- Blackout land uses `Ink` at 10 % (its `Surface2` is black); limb darkening is 55 % on dark themes and 27 % on Daylight; particle dots are drawn in the direction color with a radial halo only under glass; legend chips are measured rather than fixed at x = 92.
- Hover tolerance on history charts is `max(6 px, half a bucket)`; on live charts `max(1.5 × polling, one column of the range)`.
- The segmented pill hugs its content (`HorizontalAlignment=Left`); the GPU picker is capped at 240 px and the page title trims with an ellipsis at the 900 px minimum width.

## Evidence

- `dotnet test DeadWare.slnx -c Release --nologo`: 882 passed, 1 skipped (new: `ThemeTokenTests`, `ChartGeometryTests`, `MapGeometryTests`, `ShellUiChecks`, `ChartFrameUiChecks`, `ThermalChartUiChecks`, extended `RangeSegmentsUiChecks`, `LiveRenderingUiChecks`, `ConnectionMapUiChecks`, `ConnectionsPageUiChecks`, `ConnectionsWidgetUiChecks`, battery and usage checks).
- Preview renders: `--render` (21 overview frames plus `checks.json`) and `--render-shell` (9 themes × 10 routes = 90 frames, 18 frames at the 900 × 640 minimum, `shell-checks.json` with 108 checks and `"failures": []`). The Windows build workflow uploads both sets as the `DeadWare-previews` artifact.
- `Geometry.GetFlattenedPathGeometry()` returns no figures for open stroked figures; the glow pass strokes a `PathGeometry` copy and the old trace-shadow pass, which relied on that call and never drew, is no longer invoked by live charts.

## Not verified from CI

- Mixed or fractional DPI (125 %/150 %): hairlines are pinned with a `GuidelineSet`, but only 96 DPI renders were captured.
- Live acrylic/mica backdrops, the compositor, and 60 fps particle and pulse animation: offscreen renders are single frames with a fixed animation phase.
- Native pointer input: hover is exercised through `SetHover` in the offscreen checks, not with a real mouse.
- The preview tool's Usage history page renders empty charts (no synthetic usage buckets) and its theme picker label reads "Midnight" in every capture; both are preview-fixture limitations, not app behaviour.
