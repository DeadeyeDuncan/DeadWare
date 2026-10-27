# Manual Updates and Rollback Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline, preserving the user's standing execution preference.

**Goal:** Check for normal GitHub releases on demand and update or restore the previous app version without replacing local history/settings.

**Architecture:** A pure .NET update library validates releases/packages and performs recoverable file transactions. A separately published self-contained helper, copied outside the install directory, validates the original process, waits for normal Quit, applies the prepared package and checks startup acknowledgment. WPF exposes Check, Update & restart, Cancel preparation and Roll back & restart.

**Tech Stack:** Existing .NET 10/WPF/xUnit; built-in HTTP/JSON/ZIP/SHA-256/process APIs. No new NuGet dependencies.

**Spec:** `docs/superpowers/specs/2026-10-27-in-app-updates-design.md`.

## Global constraints and rulings

- Manual checks only; reject drafts/prereleases and automatic downgrades.
- Preserve `%LOCALAPPDATA%\DeadWare` histories/settings; mutations touch only manifest-listed app files and owned update-cache directories.
- Keep install paths stable. Use journaled file replacement and a verified backup, avoiding cross-volume directory swaps and executable locks.
- The helper never terminates monitoring forcibly. If a newly launched process fails to acknowledge but remains alive, record a recoverable failure and preserve its backup; never overwrite its running files.
- Initial updater installation may have no registered rollback; clearly disable rollback until a previous compatible packaged installation exists.
- Work on `feat/in-app-updates` based on the battery feature; keep battery PR #3 independent. No main merge or public release publication is implied by implementation approval.

## Review focus

Malformed versions/assets, archive traversal/collisions/size abuse, tampered rollback/installation, PID reuse and helper timeout, and interrupted apply/startup failure must fail without changing user data or overwriting active app files.

### Task 1: Release/package policy

Files: create `src/DeadWare.UpdateCore/{Releases,Packages}.cs` and its project; reference it from existing core tests; add `UpdatePolicyTests.cs` and `UpdatePackageTests.cs`.

- [x] Write tests for stable-only version/asset selection, latest older than installed, digest/checksum agreement, and bounded/path-safe manifest extraction.
- [x] Watch focused tests fail for missing behavior.
- [x] Implement fixed-repository latest release metadata and validated ZIP preparation, using manifest format 1 and version/hash checks.
- [x] Run focused tests and existing core suite; commit tested policy.

### Task 2: Transaction/helper and handshake

Files: create `src/DeadWare.UpdateCore/{Transactions,Handoff}.cs`, `src/DeadWare.Updater/{Program.cs,DeadWare.Updater.csproj}`; add transaction, rollback and handoff tests.

- [x] Write failing file-fixture tests for successful update/rollback, current/stage tampering, partial-copy recovery, outside-root/reparse rejection and prior backup validation.
- [x] Implement one verified previous snapshot, durable pending/result/rollback metadata, process identity validation, helper-ready handshake and startup acknowledgment. Copy only manifest files; preserve unrelated files and reject collisions with unmanaged target files.
- [x] Verify interrupted pending transactions restore before another operation, with no force termination. Commit passing transaction tests.

### Task 3: WPF integration

Files: create `src/DeadWare.App/{UpdateService,UpdatesPage}.cs`; modify `App.xaml.cs`, `UiSession.cs`, `Windows.cs`, app project and app tests.

- [x] Add failing service/UI tests for check/update/rollback state, cancellation, no normal release, and startup acknowledgment validation.
- [x] Add Updates navigation and manual controls; prepare off-thread, launch ready helper, then use normal Quit. Signal startup only after the normal session starts. Show recoverable last operation results.
- [x] Render actual controls in dark/light themes and verify existing battery/history/window shutdown behavior with the full suite. Commit integration.

### Task 4: Package, validate, deliver

Files: modify publish/release workflows, version metadata, README/changelog/validation and release notes.

- [x] Package the self-contained single-file helper and `deadware-package.json` listing all shipped file hashes; bump version to 0.6.0 and publish normal releases from version tags.
- [x] Run full tests/build/package; exercise the real helper's update, rollback and failed-start recovery in disposable directories with a standalone acknowledgment fixture, never the tablet's live histories.
- [x] Install the checked updater at the existing tablet path with the old version backed up. Verify native Updates UI and real stable-release check without downgrading the tablet.
- [x] Push all work, open/attach a stacked PR, verify CI, and document the one-time manual upgrade needed on other computers. Public publication remains a separate reviewed action.

## Inline self-review

The proposed interfaces are separate from the existing sensor/SQLite layer. Packages are verified before the current app exits; apply/rollback always use a helper outside the install directory. A missing first rollback and a hung new process have explicit UI/result states. Manual-only behavior and normal-release publication implement the user's final correction. Implementation approval covers routine plan execution; no additional plan checkpoint is needed.
