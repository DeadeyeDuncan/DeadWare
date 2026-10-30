# Overview visual preview

This standalone WPF playground uses the production Overview view and styles with clearly labeled synthetic readings. It never starts hardware monitoring or reads the installed app's settings/history. Preview controls save only to a unique `%TEMP%/DeadWare-OverviewPreview/` directory.

From the repository root:

```powershell
dotnet run --project tools/OverviewPreview -c Release
```

Try the theme selector, history range, graph appearance buttons, pop-outs, fan toggle, and sample-state selector. Closing the preview closes its pop-outs.

Render all seven themes at two widths plus missing/stale/discovery states without displaying windows:

```powershell
dotnet run --project tools/OverviewPreview -c Release -- --render artifacts/overview-preview
```

The PNGs and `checks.json` cover actual WPF layout and basic chart bounds at 96 DPI. They do not measure native input, compositor performance, physical hardware, or a long-running soak. Fixture reflection is deliberately confined to this development tool; the shipped app never creates synthetic readings.
