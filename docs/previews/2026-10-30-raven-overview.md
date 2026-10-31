# Glass panels and graph depth — 2026-10-30

Version 0.6.33 refreshes the existing WPF application. Palette-tinted panels, soft edge highlights and layered controls extend across the dashboard, pop-outs and widget. Layered gradients create the appearance inside the app without desktop blur or another rendering dependency.

Live graphs have recessed plot surfaces, translucent fills, shallow trace shadows and point halos. Fills close each continuous segment independently, preserving missing-data gaps. Battery and Cooling Health charts use the recessed surface and connected-trace shadows while retaining their state colors, range bands and markers. Coordinates, scales, graph styles and statistics are unchanged.

All seven palettes remain available. Blackout keeps pure-black backgrounds and flat plots. Windows High Contrast and disabled Windows transparency use solid surfaces. Pop-up menus have opaque backing. Resources refresh existing windows when appearance settings change.

The Overview has six consistent metric cards, larger readings, compact pop-out actions and separated min/average/max statistics. Network download/upload values have independent columns. The Network page allocates room to both chart and process table at small window sizes.

## Verification

- Release package checks: 721 tests passed (519 core and 202 Windows app).
- Preview build: zero warnings and errors.
- Overview: 17 offscreen scenarios cover all seven themes at 660 and 1000 pixels, plus unavailable, stale and discovery states.
- Full shell: 70 offscreen captures cover all eight production pages across seven themes at 1220×900, plus Network and Settings at 900×640. Minimum Network charts are at least 74.85 pixels high with visible sample traces; tables are at least 78.34 pixels high.
- Independent source and screenshot reviews found no blocking issues after fixes to high-contrast selection, popup opacity, input padding and minimum-window layout.

Reproduce the labeled synthetic previews with [the preview tool](../../tools/OverviewPreview/README.md). Evidence is generated under `artifacts/glass-overview/` and `artifacts/glass-shell/`. Fixtures do not start hardware/ETW capture, open history stores, use installed settings, change startup tasks or make update requests.

These checks cover WPF layout and rendering at 96 DPI. They do not constitute a physical-hardware soak, mixed-DPI session, final native-input check or performance benchmark. The earlier Overview prototype had a native smoke check of theme switching, appearance-popup dismissal and graph pop-out; the final shared styling was verified through the automated suite and offscreen renders above.
