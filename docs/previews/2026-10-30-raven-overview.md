# Raven-inspired Overview preview — 2026-10-30

This first visual pass keeps the existing native WPF rendering and monitoring stack. The Overview now uses six consistent cards with metric glyphs, larger values, compact pop-out actions, a faint theme-colored surface, a short hover transition, and separated min/average/max statistics. Network download and upload readings have independent columns. Existing graph colors, patterns, glow, range selection, and optional GPU-fan trace remain available.

The styling is opt-in for Overview cards. The graph renderer and other chart-card surfaces retain their existing behavior. Blackout and Windows High Contrast omit the card tint; hover transitions honor the system client-area animation setting. Long device labels trim with full tooltips. Unknown and stale current readings continue to display an em dash.

Use [the standalone preview](../../tools/OverviewPreview/README.md) to try the actual Overview view with labeled sample data, seven themes, graph controls and pop-outs. This is a visual review candidate; no release tag, installed-app replacement, or Raven dependency was added.

## Verification

- .NET 10 Release solution tests: 721 passed (519 core + 202 Windows app), zero failures.
- Preview build: zero warnings and errors.
- Offscreen preview: seven themes at 660px and 1000px widths, plus unavailable, stale and discovering states (17 scenarios). Checks cover six charts, continuous fixture traces, chart bounds, minimum plot height and theme resource updates. Screenshots use a WPF Window owner so dynamic resources update correctly.
- Native smoke: changed Midnight to Neon, opened appearance controls, dismissed them with Escape, and opened a live sample CPU graph from the compact pop-out button. Closing the preview also closed its detached graph.
- Independent source review found no blocking issues. Download statistics were given visible down-arrow labels after review.

The native smoke used synthetic readings. No physical hardware soak, multi-monitor/mixed-DPI session, native custom-color dialog, or performance benchmark is claimed. Screenshot evidence is generated locally under `artifacts/raven-overview-preview/renders/`, and can be recreated with the preview tool's `--render` option.
