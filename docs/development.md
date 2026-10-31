# Working on DeadWare across computers

GitHub is the shared source of truth: [DeadeyeDuncan/DeadWare](https://github.com/DeadeyeDuncan/DeadWare). Keep the source checkout separate from the folder containing the installed app.

Before starting on another computer, fetch and inspect the branches and local changes:

```powershell
git status --short --branch
git fetch origin
git branch -vv
```

Use `Dev` for active development on each computer. Published changes are integrated into `main` through a pull request:

```powershell
git switch Dev
git pull --ff-only
```

Fresh clones start on `main`; fetch before switching to `Dev`. For work that is still in a separate pull request, continue its feature branch instead. Update the target branch before beginning new work. If Git reports uncommitted changes or divergent history, preserve/commit the local work and reconcile it explicitly. Avoid overwriting or force-pushing a computer's changes.

Before moving to a different computer, commit the work to its feature branch and push it. Check that GitHub shows the latest commit; local commits alone do not transfer. Push useful checkpoints during longer tasks. Use a pull request to record the intended behavior, tests and remaining work.

```powershell
git add <changed-files>
git commit -m "Describe the change"
git push -u origin HEAD
git status --short --branch
```

Build/test with the .NET 10 SDK, then package with `pwsh -File scripts/publish.ps1`. CI tests and packages feature-branch and pull-request changes. The dashboard shows the installed version; package assemblies also contain the source commit in their informational version. Use the same versioned ZIP to upgrade multiple computers, choosing **Quit DeadWare** before replacing files. Windows startup registration follows the installed path, so keep that path stable.

Version 0.6.33's glass-panel refresh uses the existing WPF application. `scripts/publish.ps1` continues to publish `src/DeadWare.App/DeadWare.App.csproj`; no Raven runtime, Vulkan requirement or framework migration is part of this release. The final visual scope is recorded in [the glass refresh notes](previews/2026-10-30-raven-overview.md).

Source pushes preserve development progress; they do not themselves create a user-facing update. A normal version tag such as `v0.6.0` publishes the matching release and makes it visible to installed updaters. Include the helper and generated package manifest whenever distributing a build.

Release notes describe what changed in that version. Omit repeated installation steps and generic assurances that settings, histories and saved windows are preserved; installation and upgrade guidance belongs in README. The release workflow publishes `docs/releases/<tag>.md` verbatim, and the app displays the published GitHub text.

For another computer running 0.5.x or earlier, download the complete Windows x64 ZIP from [v0.6.33](https://github.com/DeadeyeDuncan/DeadWare/releases/tag/v0.6.33), choose **Quit DeadWare**, then extract/copy the entire `DeadWare` folder's contents into its existing app folder. Keep the existing executable path and include `DeadWare.Updater.exe` and `deadware-package.json`. Start DeadWare again. This one-time manual upgrade adds **Updates**. Version 0.6.0 or later can install 0.6.33 through **Check for updates** and **Update & restart**. Version 0.6.1 or later checks at each launch and notifies about newer normal releases; download and installation still require **Update & restart**. Current settings and local histories remain in the Windows profile.

Settings, battery history, Cooling Health history and live graph caches are local device data under `%LOCALAPPDATA%\DeadWare`. They do not belong in Git. Back up database files with DeadWare fully quit; do not cloud-sync an open SQLite database. Each computer starts its own battery history when the new version runs.
