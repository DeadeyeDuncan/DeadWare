# Feature image sources

Captured/generated on November 6, 2026 for the repository feature tour.

- `overview.jpg`: unedited live capture of the installed DeadWare v0.14.0 dashboard on Windows, using the Blackout theme. The desktop capture tool supplied JPEG bytes.
- `processes.png`, `connections.png`, `storage.png`, `cooling-health.png`: production WPF views rendered offscreen with the repository's synthetic fixtures and Midnight theme at 1220 × 900. These are labeled sample-data previews in the project README.

Generate the previews from the repository root:

```powershell
dotnet run --project tools/OverviewPreview -c Release -- --render-shell artifacts/readme-feature-previews
```

The four PNGs are copied without modification from `glass-shell-Midnight-processes.png`, `glass-shell-Midnight-connections.png`, `glass-shell-Midnight-storage.png`, and `glass-shell-Midnight-cooling.png`. Source baseline: `dd4fb74` (v0.14.0).

The installed app could be captured, but desktop automation did not successfully navigate it; the previews are not evidence of native input, hardware collection, or a hardware diagnosis.
