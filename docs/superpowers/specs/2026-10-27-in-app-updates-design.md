# Manual GitHub updates and rollback

Status: proposed design for approval. No updater product code has been written.

## Intent

The user wants the ability to update or roll back DeadWare from within the app and expects normal releases rather than preview channels. The revised proposal uses manual checks and explicit update/rollback actions. This follows the requirement to keep development work on GitHub so different computers can continue from the same source. Published, versioned packages supply application updates; source commits and open pull requests remain development work.

## Proposed experience

- Add an **Updates** view showing the installed version, latest eligible published version, last check result, release notes and **Check now**.
- **Check for updates** runs on demand in this view. There is no automatic polling, background download or automatic installation in this simpler proposal. Offline/rate-limit errors are readable here and must not interrupt monitoring.
- Show an update result when a newer eligible release exists. **Update & restart** is an explicit user action.
- Use normal published releases only, with no preview-channel setting. Drafts and prereleases are excluded. An installed newer build never automatically downgrades to an older published version.
- Show download progress and permit cancellation before installation starts. Check the package before quitting. Flush settings and all history stores through the existing normal-Quit path, apply the update, and reopen the app and remembered windows at the same installed path.
- Preserve settings and battery, Cooling Health and graph databases under `%LOCALAPPDATA%\DeadWare`. Keep a rollback of the previous app files and restore it if replacement or startup verification fails. A failed check/download leaves the running app untouched.
- Add **Roll back & restart**, displaying the previous installed version when a verified backup is available. This explicitly restores that app version, flushes and preserves current histories/settings, and reopens the app. Disable the action with a clear explanation when no previous installation is available. This proposal keeps one previous installed version, rather than selecting any historical GitHub version.

## Release source and eligibility

- Fixed public repository: `DeadeyeDuncan/DeadWare`. No GitHub login/token is needed for ordinary checks.
- Retrieve normal published releases, filter drafts/prereleases, and compare parsed version tags with the installed version. The GitHub latest-release endpoint may be used for this normal-release-only policy, with version/assets still validated explicitly.
- Accept only a complete matching Windows x64 release ZIP and its SHA-256 information. Verify the bytes against GitHub's asset digest and the release checksum before staging/applying. Handle incomplete publication as temporarily unavailable rather than offering an unusable update.
- Select versioned release assets, not source-code ZIPs, branch snapshots, open PR builds or unversioned CI artifacts. GitHub commits alone must not trigger app replacement.
- Keep tag-based publication, change the workflow to publish normal releases instead of passing `--prerelease`, and update the release documentation accordingly. Do not silently relabel existing historical preview releases. Releasing a tested package is what makes an update visible to other computers. Existing installed versions without this feature need one manual upgrade to gain the updater.

## Implementation boundaries and choices

These are the revised proposed defaults: manual checks, normal releases only, an Updates view, explicit update and rollback buttons, progress/cancellation before apply and one previous-version rollback. The user requested update/rollback and no preview-release workflow; the interpretation that checking should also be manual is stated for confirmation.

Use the existing .NET 10/WPF application and release packaging. Prefer built-in HTTP, JSON, hashing and ZIP APIs over a new updater framework. Windows holds running executables open, so a small separately staged helper must apply files only after the original process has exited. The helper must run independently of files it replaces, preserve the install path used by shortcuts/startup registration, and report a recoverable failure.

Before implementation, the plan must settle the helper's packaging and handoff, write-access/elevation behavior, transaction/rollback format and startup acknowledgment. Verify the parent process identity/user/session/start time rather than trusting a PID alone. Refuse an unsuitable/shared application directory, ambiguous install metadata, another active instance, or a parent that did not exit cleanly. Never force-terminate monitoring to make replacement proceed.

Treat release metadata and archive paths as untrusted input. Pin the repository and expected asset names, require HTTPS, bound download/extraction sizes, reject traversal/absolute/alternate-stream paths, symlinks, duplicate/case-colliding entries and unexpected package layouts. Stage and verify the entire package before replacing installed files. Do not execute downloaded shell instructions or release-note content. Keep update descriptors/staging under the current user's control and validate every operation's resolved target stays within the intended install/staging/rollback directories.

Manual update checks contact GitHub for release metadata and requested package downloads. Do not upload sensor readings, databases, settings or application/process traffic details. Do not add a scheduled OS task or a background service.

## Source and integration

Continue from the battery-history candidate so the updater preserves its store/window lifecycle. Keep updater changes on a distinct GitHub feature branch; leave battery PR #3 independently reviewable. The installed source/product build is 0.5.0, and all currently published releases are flagged as previews. Until a normal release is published, the checker must correctly report that no eligible published update exists.

## Verification before delivery

- Test version ordering, draft/prerelease rejection, no normal release, malformed/incomplete releases, missing/wrong assets, an installed build ahead of publication, rate limits and offline/cancelled checks.
- Test hash mismatches, bounded downloads and archive path/layout validation before any installed file can change.
- Exercise the helper in isolated fixture directories for clean shutdown, stale/reused PIDs, locked/non-writable files, interrupted replacement, explicit rollback, unavailable/tampered rollback backups, wrong package versions, preserved local data and startup acknowledgment failure.
- Verify settings and battery/Cooling Health/graph history flush and survive an update, along with existing window restoration, startup registration and single-instance behavior.
- Run the full suite, render the real Updates UI, package a versioned portable build and perform an isolated end-to-end update/rollback test. Do not test by downgrading or overwriting the working tablet install with an older public release.
- Commit/push source, tests and delivery evidence; create a reviewable PR and check CI. Publishing a public release remains a separate concrete step after validation.

## Inline builder check and references

The current package already has versioned ZIPs/checksums, and GitHub exposes SHA-256 asset digests. Existing normal Quit flushes three history workers and settings, and the app already validates process identity during administrator restart. A staged helper and acknowledgment protocol are necessary to bridge executable file locks and recover failed replacement. No product-code or dependency installation is authorized by this proposed design alone.

- [GitHub Releases API](https://docs.github.com/en/rest/releases/releases)
- [GitHub asset digests](https://github.blog/changelog/2025-06-03-releases-now-expose-digests-for-release-assets/)
- Local `App.xaml.cs`, `UiSession.cs`, `GuiInstance.cs`, `scripts/publish.ps1` and `.github/workflows/release.yml` supplied the current integration evidence.
