# Review findings fixes (v0.6.39) — implementation plan

Source: full-repo code review of `8f88fa8` (2026-11-01, Claude + blind Codex cross-check). This plan is
also the spec: the four top findings below, plus the user's decisions, are the binding requirements.

User decisions (binding):
- Finding #1 uses the **protected-folder gate**: the administrator sign-in task is allowed only when DeadWare
  lives in a folder standard users cannot change (for example `C:\Program Files\DeadWare`). A portable copy in a
  user-writable folder gets a clear message instead. Elevated updates of a protected installation use an
  administrator-only update folder, and `helper.exe` is locked while it starts.
- The run ends with a published **v0.6.39** release so installed apps update through **Check for updates**.

## Global Constraints

- Match the surrounding code style exactly: dense C# lines, existing naming, comment density, file-scoped
  namespaces, `Volatile`/`Interlocked` idioms. No reformatting of untouched code.
- Build with `dotnet build DeadWare.slnx -c Release` → **0 warnings, 0 errors**. Windows-only APIs used from
  `net10.0` projects (`DeadWare.UpdateCore`, `DeadWare.Core`) must carry `[SupportedOSPlatform("windows")]`
  (or be guarded) so CA1416 produces no warnings.
- Full suite `dotnet test DeadWare.slnx -c Release` must pass (currently 745 tests). New behavior gets tests.
- Never weaken an existing update integrity check (hash, manifest, identity, reparse-point checks).
- **Update compatibility (load-bearing):** the 0.6.38 → 0.6.39 update is executed by the *installed 0.6.38*
  app and its 0.6.38 helper, which always use the job root `%LOCALAPPDATA%\DeadWare\updates\<key>`. The new
  0.6.39 app's `UpdateService.AcknowledgeStartup` must find and acknowledge a handoff written there, for every
  installation location (protected or not). If the ack fails, the old helper rolls the update back.
- Unprotected (user-writable) installations keep using `%LOCALAPPDATA%\DeadWare\updates` exactly as today.
- Git: commit only your task's files on branch `fix/review-findings`; never push, never switch branches.
- Commit messages end with: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`

## Task 1: Updates page must not crash on failed local verification

Finding: `UpdateService.ReadLocalAsync` (`src/DeadWare.App/UpdateService.cs:76`) catches only
`IOException or UnauthorizedAccessException or ArgumentException`. Package/rollback validation throws
`InvalidDataException` (a `SystemException`, not an `IOException`), which escapes the `async void`
`Loaded`/`IsVisibleChanged` handlers in `src/DeadWare.App/UpdatesPage.cs:40-42`; the app has no
`DispatcherUnhandledException` handler, so the process dies.

Requirements:
1. `ReadLocalAsync` also handles `InvalidDataException`, publishing the same disabled state as the existing catch
   (`InstallationVerified=false`, `Previous=null`, `LocalStatus` beginning with the existing
   "Install the complete updater-enabled package before updating from this folder. " text plus the message).
2. Defense in depth: the two `UpdatesPage` handlers that `await service.ReadLocalAsync()` must not let any
   exception escape; route an unexpected exception to `service.ReportFailure(error)`.
3. Tests (in `tests/DeadWare.App.Tests/UpdateServiceTests.cs`, reuse its existing package fixture helpers):
   - a verified package whose one managed file is then modified → `ReadLocalAsync` completes without throwing,
     `InstallationVerified` is false and `LocalStatus` mentions the changed file;
   - a corrupted rollback snapshot (valid install, `rollback.json` present, but a file inside the
     `previous/<id>` snapshot altered) → completes without throwing and update is disabled.

## Task 2: Update cache must not retain dead jobs or full payloads

Finding: `UpdateCache.CleanupCompletedJobs` (`src/DeadWare.UpdateCore/UpdateCache.cs`) only deletes job
directories containing `complete.json`, and keeps the newest completed job in full (`download.zip` ≈120 MB
plus extracted `package/` plus `helper.exe`; measured 384 MB per job on the user's machine). Jobs that never
get `complete.json` (helper launch blocked, helper failed before reading its job, app quit between prepare and
install) are kept forever.

Requirements (keep the existing guards: skip everything when `pending.json` exists; hold `install.lock`
exclusively; `SafeFiles.NoLinks` on every entry before deletion; IO/permission errors are swallowed):
1. Completed jobs other than the newest: deleted entirely (existing behavior).
2. The newest completed job: keep only its small metadata files (`complete.json`, `request.json`,
   `ready.json`, `started.json`); delete `download.zip`, the `package` directory and `helper.exe`.
   `helper.exe` is deleted only if an exclusive open succeeds (existing probe pattern).
3. Orphaned jobs — no `complete.json` — whose directory creation time is more than 24 hours old
   (`UpdateHandoff.ValidateJob` already rejects jobs older than one day, so they can never run) are deleted
   entirely, again only if their `helper.exe` (when present) can be opened exclusively.
4. Orphaned jobs younger than 24 hours are untouched (an install may be in progress).
5. Tests in `tests/DeadWare.Tests/UpdateCacheTests.cs` (use `Directory.SetCreationTimeUtc` like the existing
   tests): payload trimming of the newest completed job; old orphan removed; young orphan kept; locked
   `helper.exe` (open `FileShare.None` handle held by the test) keeps that job; `pending.json` still blocks all
   cleanup. Update the existing `Cleanup_only_removes_older_completed_owned_jobs` expectations only as far as
   the new rules require.

## Task 3: Bounded shutdown; connection history flushes before hardware stop

Finding: normal **Quit** awaits `UiSession.StopCollectorAsync` with no deadline
(`src/DeadWare.App/UiSession.cs:306`). It awaits `Collector.DisposeAsync`, which awaits the hardware polling
worker (`Collector.DisposeCoreAsync`, `src/DeadWare.App/Collector.cs`). A hung LibreHardwareMonitor provider
therefore freezes Quit forever (window disabled, process alive; a pending updater gives up after 60 s and cancels
the update). `Connections.StopAsync` and the final ETW connection publication (`StopEtwAsync`, which raises
`FinalConnectionsAvailable`) also only run after the hardware worker finishes, so at Windows sign-out (3 s
deadline) buffered connection history is lost.

Requirements:
1. `Collector`: stopping network capture must not wait for the hardware worker. Add an idempotent method that
   cancels collection and returns the shared `StopEtwAsync()` task (final ETW drain + `FinalConnectionsAvailable`
   + ETW disposal). `DisposeCoreAsync` must call it before awaiting the worker. Keep `StopEtwAsync`'s
   single-shot gate so the worker's own `finally` still reuses the same task.
2. `UiSession.StopCollectorAsync`: start provider disposal; stop the history services as today; in parallel,
   await the network-capture stop (bounded by the provider deadline), then unsubscribe and await
   `Connections.StopAsync()` — independent of the hardware worker. Finally await provider disposal bounded by a
   deadline; on timeout record a diagnostics event (`collector_stop_timeout`-style, matching existing
   `diagnostics?.Record` usage) and continue so the app can exit.
3. Deadline: normal Quit waits at most **5 seconds** for the hardware provider. The session-ending path keeps its
   existing overall 3-second bound (`StopCollectorForSessionEnding`). Make the provider deadline a parameter or
   internal constant so tests can use a short value.
4. Tests (extend `tests/DeadWare.App.Tests/CoolingShutdownTests.cs`, reusing its uninitialized-collector
   reflection pattern): with the hardware worker blocked forever, (a) the normal stop path completes within the
   provider deadline plus a small margin, and (b) a `ConnectionService` attached to the session is stopped
   (its `StopAsync` task completes) even though the worker never finishes. Existing shutdown tests keep passing.

## Task 4: Install-location protection policy + startup gate

Finding: `StartupTaskPolicy.CreateXml` (`src/DeadWare.Core/StartupLayout.cs:55`) registers a
`RunLevel=HighestAvailable` sign-in task for whatever folder DeadWare runs from. Portable installs live in
user-writable folders (the user's own: `C:\Users\<user>\AppData\Local\Programs\DeadWare`), so any unelevated
process of that user can replace `DeadWare.exe` or a DLL beside it and get administrator rights at the next
sign-in with no UAC prompt.

Requirements:
1. New file `src/DeadWare.UpdateCore/InstallLocation.cs` (UpdateCore is shared by the app and the updater):
   - Pure, platform-neutral policy over a model: `AccessRule(string Sid, int Rights, bool Allow, bool InheritOnly)`
     and a per-path record (path, kind, owner SID, rules, reparse flag). Evaluation returns protected/unprotected
     plus a short human-readable reason naming the first offending path.
   - Trusted SIDs: `S-1-5-18` (SYSTEM), `S-1-5-32-544` (Administrators),
     `S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464` (TrustedInstaller), and `S-1-3-4`
     (OWNER RIGHTS) only when the owner is trusted. Every other SID is untrusted. Inherit-only rules are ignored
     (they do not apply to the object). Deny rules are ignored (conservative). The owner must be trusted.
     Any reparse point in the checked chain makes the location unprotected.
   - Rights masks (untrusted Allow rules must grant none of these):
     - install directory and managed subdirectories: `0x2` add file, `0x4` add subdirectory, `0x40` delete child,
       `0x10000` DELETE, `0x40000` WRITE_DAC, `0x80000` WRITE_OWNER, `0x40000000` GENERIC_WRITE,
       `0x10000000` GENERIC_ALL;
     - managed files: `0x2` write data, `0x4` append data, `0x10000`, `0x40000`, `0x80000`, `0x40000000`,
       `0x10000000`;
     - each ancestor directory up to the volume root: `0x40` delete child, `0x10000`, `0x40000`, `0x80000`,
       `0x10000000`.
   - A Windows reader (`[SupportedOSPlatform("windows")]`) that builds the model from real ACLs via
     `System.Security.AccessControl` (`FileSystemAclExtensions.GetAccessControl`, rules as `SecurityIdentifier`)
     for: the install directory, its ancestors, and every managed file listed in the install's
     `deadware-package.json` (plus their parent directories inside the install). If the manifest is missing,
     check the install directory, its ancestors and every file under it. Read failures → unprotected with reason.
2. Startup gate (`src/DeadWare.App/StartupRegistration.cs`, plus `StartupTaskPolicy`/status record as needed):
   - `Apply(enable:true)` checks the executable's directory first; if unprotected it returns a new exit code
     **6** and registers nothing. `ConfigureAsync` maps 6 to: "DeadWare can start as administrator at sign-in
     only from a folder standard users can't change, such as C:\Program Files\DeadWare. Move the DeadWare
     folder there, then turn startup on." Disabling is always allowed.
   - `Query()`: when an owned task exists, evaluate the task executable's directory; if unprotected, the status
     is not `Ready` and its message reads: "Startup runs DeadWare as administrator from a folder other programs
     can change. Move DeadWare to a protected folder such as C:\Program Files\DeadWare and repair startup, or
     turn startup off." The Settings page keeps **Turn off** usable in that state. Never delete or disable an
     existing task automatically.
3. Tests: pure policy cases in `tests/DeadWare.Tests` (trusted-only rules → protected; Users with add-file on
   install dir → unprotected; untrusted inherit-only rule ignored; untrusted owner → unprotected; DELETE_CHILD
   on an ancestor → unprotected; untrusted write on one managed file → unprotected; reparse flag → unprotected;
   OWNER RIGHTS with untrusted owner → unprotected). One real-filesystem test: a fresh directory under
   `Path.GetTempPath()` is reported unprotected. Startup-policy tests for the new status/message mapping where
   the code is testable without Task Scheduler.

## Task 5: Protected update root, helper lock, cross-version acknowledgement

Requirements (builds on Task 4's `InstallLocation`):
1. `UpdatePaths` gains `ProtectedCacheBase` = `%ProgramData%\DeadWare\updates` and a factory that selects the
   base for an installation: protected installation → `ProtectedCacheBase`; otherwise today's
   `%LOCALAPPDATA%\DeadWare\updates`. Constructor with explicit `cacheBase` (used by tests) is unchanged.
   `UpdateService`'s default constructor path uses the factory.
2. Protected root lifecycle: before writing into the protected base, create `%ProgramData%\DeadWare` and
   `...\updates` with an explicit, inheritance-protected DACL (SYSTEM and Administrators full control,
   Users read & execute, owner Administrators), then verify the chain with the Task 4 policy (`DeadWare` and
   `updates` under install-directory rules, `%ProgramData%` and above under ancestor rules). If a directory
   already exists, verify instead of trusting it; if verification fails, refuse with: "The update folder
   %ProgramData%\DeadWare\updates is not restricted to administrators. Delete it, then try again." Creating it
   without administrator rights fails with a clear message that updating a protected installation needs
   **Restart as administrator**. Never take ownership of or delete an existing folder automatically.
3. Helper (`src/DeadWare.Updater/Program.cs`): derive the job root from its own executable path
   (`<root>\jobs\<id>\helper.exe`), require it to be the root for `--key` under one of the two bases, read the
   job there, then require `UpdatePaths` for `job.InstallDirectory` (via the factory) to have that same root;
   when it is the protected base, verify it with the policy before reading or applying anything.
4. Helper lock (`UpdateService.StartHelperAsync`): after copying `helper.exe`, open it with `FileShare.Read`
   only (no write/delete sharing), verify its SHA-256 from that open stream against the installed manifest
   entry, and keep the handle open until the helper reports ready or the attempt fails.
5. **Compatibility:** `UpdateService.AcknowledgeStartup` looks for `jobs/<id>/request.json` under the factory's
   root first and falls back to `%LOCALAPPDATA%\DeadWare\updates\<key>` (the 0.6.38 helper's root). All existing
   identity checks still apply to whichever root holds the job.
6. Tests: factory picks the LocalAppData base for an unprotected temp install; `AcknowledgeStartup` root search
   finds a handoff in the fallback root (structure the search so it is testable with injected candidate roots);
   the locked helper cannot be overwritten or deleted while its verified handle is held; verification rejects a
   protected-root candidate whose model grants Users write.

## Task 6: Release prep v0.6.39

Follow the v0.6.38 recipe (commit `19d9034`): bump `<Version>` in `Directory.Build.props` and the
`scripts/publish.ps1` default to `0.6.39`; add a `## 0.6.39 — Safer updates and startup` CHANGELOG entry;
create `docs/releases/v0.6.39.md` (user-facing, what changed, no generic installation boilerplate, per
`docs/development.md`); update README where it describes startup registration and the update cache so it states
the protected-folder requirement for starting as administrator at sign-in and that old update downloads are now
cleaned up. Mention that existing administrator startup from an unprotected folder now shows a warning in
Settings, and that the update cache trims itself after the next update check.
