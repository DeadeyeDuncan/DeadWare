# DeadWare startup and layout restoration

**Goal:** Start DeadWare at this user's Windows sign-in with administrator privileges and restore the graphs/widget left open, at their saved positions.

**Authority:** User directly requested startup and restoration, then explicitly chose administrator startup. Existing request to build and manage the public DeadWare repository continues. Normal UAC is retained for initial task registration. Computer Use stays off because it correlated with host lag during earlier validation.

**Architecture:** Extend existing per-user settings and native physical-pixel placement. Persist a validated set of open detached-window IDs separately from stored positions. A current-user interactive Task Scheduler logon task launches this installed executable with highest privileges; registration helpers do not start collectors. A single-instance guard prevents duplicate collectors and cooperates with administrator restart.

## Scope and decisions

- Allowed detached IDs: cpu, cputemp, gpu, gputemp, network, widget. Restore only explicitly remembered open windows; do not infer open state from historical positions in old settings.
- Restore windows by default, with an option to disable restoration. Explicitly closing a detached window removes it; closing the main app or Windows session preserves the remaining set. Reuse monitor clamping and physical pixel storage.
- Checkpoint position/size/topmost and open-set changes with debounced writes, with a final saved snapshot on shutdown/session ending. Avoid stale queued saves overwriting the newest state or UI-thread shutdown deadlocks.
- Desktop layout exposes start-with-Windows (administrator), current registration status/errors, and reopen-previous-windows controls in a scrollable page.
- Startup is per Windows user, interactive token, highest run level, logon trigger (short delay acceptable), unlimited execution duration, allowed on battery, IgnoreNew. No SYSTEM, stored passwords, security-policy changes or unrelated task modification.
- Only an explicitly owned task (stable per-SID name plus ownership marker and matching principal) may be replaced/removed. Query actual registration; settings must not falsely claim startup is enabled after denial/failure. Re-register if the portable executable moves.
- Registration requires an elevated helper when necessary and reports cancellation. CLI configuration/status supports bounded parent verification with Computer Use off. Helpers/diagnostics bypass GUI singleton and do not start monitoring. Reject accidental registration under a different account after credential-based elevation.
- One GUI instance per user/session. Repeated scheduled launch exits quietly; normal duplicate launch gives useful feedback. Administrator restart saves first and allows old instance shutdown before replacement loads settings/acquires the guard. Cancellation leaves the old app usable.
- Version 0.2.0 preview. Update the stable local artifacts/current directory only when the old app is closed, preserve rollback and desktop shortcut flags, then enable startup using the installed executable path. Do not reboot or sign the user out to test.

## Review focus

1. Main shutdown must not erase remembered open windows.
2. Queued saves, session end and elevation handoff must preserve the latest snapshot without deadlock or duplicate collectors.
3. Malformed/unknown/duplicate open IDs must not crash startup or create arbitrary windows.
4. Task registration must match the current user's interactive identity, exact executable and owned task; UAC denial must remain visibly disabled.
5. Removed monitors, minimized windows, battery operation and long uptime must not strand windows or silently stop the monitor.

## Task 1 — app, core and tests

Owner: implementation worker. Files: src/**, tests/**, Directory.Build.props only. Use existing project-local SDK at I:/DeadWare/.tools/dotnet/dotnet.exe; no dependency/runtime installation needed.

- [x] Add meaningful failing tests for open-set normalization/roundtrip, explicit-close vs main-shutdown state, stale-save protection where extracted, and startup task policy/identity/path escaping.
- [x] Implement layout persistence/restoration through existing UiSession and NativePlacement.
- [x] Implement owned per-user elevated Task Scheduler registration, status, disable, CLI helper and Desktop controls.
- [x] Add single-instance and safe elevation handoff, preserving diagnostics behavior.
- [x] Build/test Release, normal-user diagnostics only, self-review, commit owned files and report evidence. Never use Computer Use or run elevated tests in worker.

## Task 2 — integration, review, deployment

Owner: parent. Files: docs/**, README.md, CHANGELOG.md, scripts/** and .github/**; no overlapping source edits while worker active.

- [x] Update documentation, 0.2.0 packaging defaults and version-specific release notes.
- [x] Independent review, fix important findings, package with all tests passing.
- [x] Install verified local build with rollback; preserve shortcut target/admin flag.
- [x] Register current-user startup using one-time Windows approval; inspect actual task principal, trigger, action, runlevel and settings. Exercise start-on-demand without sign-out, check duplicate guard and ask user to arrange/reopen windows manually without Computer Use.
- [x] Commit/publish the tested update through existing GitHub workflows and record bounded validation. Queue a canonical chronicler brief at verified completion.

## Follow-up scope — native title bars and scrollbars

The user additionally requested dark title bars and scrollbars for the app and widget. Reuse existing palettes across the main app, graphs and widget, with live updates and a light Daylight preset. Preserve native window controls and ordinary scrolling. Use supported DWM behavior where available and safe native fallback on older Windows. An opt-in bounded layout trace will resolve the outstanding save discrepancy before delivery; it records local settings operations only and remains disabled in normal use.
