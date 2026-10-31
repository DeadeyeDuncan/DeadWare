# Glass UI visual preview

This standalone WPF playground uses the production views and glass styles with clearly labeled synthetic readings. It never starts hardware monitoring or reads the installed app's settings/history. Interactive preview controls save only to a unique `%TEMP%/DeadWare-GlassPreview/` directory.

From the repository root:

```powershell
dotnet run --project tools/OverviewPreview -c Release
```

Try the theme selector, history range, graph appearance buttons, pop-outs, fan toggle, and sample-state selector. Closing the preview closes its pop-outs.

Render all seven themes at two widths plus missing/stale/discovery states without displaying windows:

```powershell
dotnet run --project tools/OverviewPreview -c Release -- --render artifacts/overview-preview
```

The existing 17 scenarios remain: seven themes at two widths, plus missing, stale and discovery states. The frame uses the theme's `WindowSurface`, including its opaque Blackout/high-contrast/transparency-disabled fallback.

Render the actual MainWindow shell and all eight pages at 1220 × 900 in all seven themes:

```powershell
dotnet run --project tools/OverviewPreview -c Release -- --render-shell artifacts/glass-shell-preview
```

This produces 56 `glass-shell-<theme>-<route>.png` files, 14 additional Network/Settings frames at the supported 900 × 640 minimum, and `shell-checks.json`. Minimum-size files end in `-900x640.png`. Overview, Network, Connections, Battery history, Sensors, Cooling Health, Settings and Updates receive in-memory sample readings, traffic, geography, history and release notes. Every frame labels its data as synthetic. Services remain unstarted and the shell is never shown. Only the display-only Overview/Network routes briefly use the existing offscreen presentation fixture so production cards populate their values; history/update pages remain unloaded, preventing their Loaded handlers from reading stores or starting timers. The Settings startup-status query is suppressed, and the updater's HTTP handler rejects network requests. Rendering invokes navigation and fixture display methods, never command actions; theme selection can save only the isolated scratch settings. Checks reject native window creation, pages left loaded during capture, missing synthetic card values or unexpected history database files. The report records Network table/chart heights and requires a table of at least 70 pixels, a chart of at least 65 pixels and a visible continuous sample trace, while retaining captures for inspection on failure.

Both render modes cover actual WPF layout at 96 DPI. They do not measure native input, compositor performance, physical hardware, or a long-running soak. Fixture reflection is confined to this development tool; the shipped app never creates synthetic readings.
