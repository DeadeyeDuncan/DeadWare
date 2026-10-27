# Working on DeadWare across computers

GitHub is the shared source of truth: [DeadeyeDuncan/DeadWare](https://github.com/DeadeyeDuncan/DeadWare). Keep the source checkout separate from the folder containing the installed app.

Before starting on another computer, fetch and inspect the branches and local changes:

```powershell
git status --short --branch
git fetch origin
git branch -vv
```

Continue the same feature branch when work is still in a pull request. For the battery feature this is `feat/battery-history`:

```powershell
git switch feat/battery-history
git pull --ff-only
```

On a fresh clone, use `git switch --track origin/feat/battery-history`. After a feature is merged, update `main` with `git switch main` and `git pull --ff-only` before beginning the next branch. If Git reports uncommitted changes or divergent history, preserve/commit the local work and reconcile it explicitly. Avoid overwriting or force-pushing a computer's changes.

Before moving to a different computer, commit the work to its feature branch and push it. Check that GitHub shows the latest commit; local commits alone do not transfer. Push useful checkpoints during longer tasks. Use a pull request to record the intended behavior, tests and remaining work.

```powershell
git add <changed-files>
git commit -m "Describe the change"
git push -u origin HEAD
git status --short --branch
```

Build/test with the .NET 10 SDK, then package with `pwsh -File scripts/publish.ps1`. CI tests and packages feature-branch and pull-request changes. The dashboard shows the installed version; package assemblies also contain the source commit in their informational version. Use the same versioned ZIP to upgrade multiple computers, choosing **Quit DeadWare** before replacing files. Windows startup registration follows the installed path, so keep that path stable.

Settings, battery history, Cooling Health history and live graph caches are local device data under `%LOCALAPPDATA%\DeadWare`. They do not belong in Git. Back up database files with DeadWare fully quit; do not cloud-sync an open SQLite database. Each computer starts its own battery history when the new version runs.
