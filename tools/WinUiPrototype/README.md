# WinUI 3 measured prototype

A throwaway app that answers one question from [the migration spec](../../docs/superpowers/specs/2026-11-11-winui3-migration-prototype-design.md): does a WinUI 3 + Win2D DeadWare start, idle and draw at an acceptable cost when shipped as a self-contained folder?

It is not part of `DeadWare.slnx`, is never packaged by `scripts/publish.ps1`, and will be deleted. It shows six charts on made-up data; it reads no sensors and opens no history.

| Folder | Holds |
|---|---|
| `tools/WinUiPrototype` | The app: Mica window, navigation, six Win2D charts, a Win32 tray icon. |
| `tools/WinUiPrototype.Logic` | Sample buffer, synthetic data and chart geometry, with no UI dependency. |
| `tools/WinUiPrototype.Tests` | Unit tests for the logic. |

The `WinUI prototype` workflow builds it, checks that it starts, draws and hides to the tray, and measures it on a GitHub runner. Those runners have no GPU, so the chart figures from CI are pessimistic. The figures that count come from real hardware.

## Measuring on your own computer

You need PowerShell 7 (`pwsh`), the .NET 10 SDK, and this branch checked out.

1. Download the `WinUiPrototype-win-x64` artifact from the latest **WinUI prototype** run in the Actions tab and extract it somewhere, for example `C:\Temp\DeadWare.WinUiPrototype`.
2. Choose **Quit DeadWare** in the installed app, so it does not share the machine with the measurement.
3. From the repository root, run these in turn. Each takes about six and a half minutes: three launches, a minute with the window shown and a minute hidden. Leave the computer alone while they run.

   ```powershell
   pwsh -File scripts/measure-ui.ps1 -Exe 'C:\Temp\DeadWare.WinUiPrototype\DeadWare.WinUiPrototype.exe' -Label winui-prototype -Out artifacts/measurements/ui-winui-prototype.json

   pwsh -File scripts/measure-ui.ps1 -Exe 'C:\Temp\DeadWare.WinUiPrototype\DeadWare.WinUiPrototype.exe' -Arguments '--smooth' -Label winui-prototype-smooth -Out artifacts/measurements/ui-winui-prototype-smooth.json

   dotnet build tools/OverviewPreview -c Release
   pwsh -File scripts/measure-ui.ps1 -Exe tools/OverviewPreview/bin/Release/net10.0-windows/DeadWare.OverviewPreview.exe -Label wpf-overview-preview -Out artifacts/measurements/ui-wpf-overview-preview.json

   pwsh -File scripts/measure-ui.ps1 -Exe 'C:\path\to\installed\DeadWare.exe' -Label wpf-installed -Out artifacts/measurements/ui-wpf-installed.json
   ```

4. Send back the four files from `artifacts/measurements/`. They hold the executable's file name, the Windows build, processor and video adapter names, the display scale and the measurements; no paths or user names.

The script ends each launch by killing the process it started. For the installed app that is an abrupt stop three times over: its SQLite histories are crash-safe, but the last minute or so of readings from those launches can be lost, as after any crash. Skip the last command if you would rather not; the comparison that matters is the first three.

The installed-app figures are a reference, not a like-for-like comparison: that app does real sensor work, while the prototype and the preview only draw made-up data.

## Checks only a person can make

Run the extracted `DeadWare.WinUiPrototype.exe` directly and note yes or no for each:

1. The window has the Mica backdrop (the desktop wallpaper tints it) and the title reads `DeadWare prototype (Mica)`.
2. A DeadWare icon appears in the notification area.
3. Closing the window hides it, and a left click on the icon brings it back.
4. A right click on the icon shows **Open** and **Quit**, and the menu goes away when you click elsewhere.
5. **Quit** removes the icon and ends the process (nothing named `DeadWare.WinUiPrototype` is left in Task Manager).

Also worth a glance: run it once more with `--smooth` and see whether the six charts scroll smoothly, and how the window feels to resize.

## Command line

| Argument | Effect |
|---|---|
| `--no-mica` | Forces the solid background used where Mica is unavailable. The title then reads `DeadWare prototype (solid)`. |
| `--smooth` | Redraws the charts every frame and scrolls the trace between samples, as the WPF charts do. Without it the charts redraw once a second. This is the like-for-like mode for comparing CPU with the WPF preview. |
| `--error-file <path>` | Where an unhandled error, or a tray that could not be added, is written. |
| `--exit-after <seconds>` | Writes the number of chart draws to `<error-file>.frames` and exits. Used by the workflow. |
