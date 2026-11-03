# Handoff: DeadWare refined-glass redesign → Claude Code

Give this file to Claude Code running in `I:\DeadWare` on Windows. It has everything the previous session knew that is not already in the repo.

## What this is

A presentation-only redesign of the DeadWare WPF app (dashboard, pop-outs, widget, Connections globe), executed as a superpowers plan. Design and plan are approved by the user; implementation is 3 of 11 tasks in. **Nothing has run on Windows yet** — every change so far compiled on Linux but no test, render or CI run has happened. Your first job is to make the branch green on a real Windows build before continuing.

- Spec (binding authority): `docs/superpowers/specs/2026-11-02-refined-glass-redesign-design.md`
- Plan (11 tasks, exact signatures and tests): `docs/superpowers/plans/2026-11-02-refined-glass-redesign.md`
- Ledger (progress, rulings): `.superpowers/sdd/2026-11-02-refined-glass-redesign/progress.md` (git-ignored, already in place)
- Visual target: the user's "DeadWare Redesign Preview" design canvas in Claude (Blackout palette, Overview + Connections artboards). The user prefers **Blackout**; the spec was amended so Blackout keeps pure-black surfaces but gets glow/particles/halos.

## Repo state

- Branch `feat/refined-glass`, checked out in `I:\DeadWare`, 6 commits ahead of `origin/main` (0.8.0), **not pushed**:
  1. `docs:` design spec · 2. `docs:` Blackout amendment · 3. `docs:` implementation plan
  4. `feat:` derived theme tokens (Task 2) · 5. `feat:` control restyle + `RangeSegments` (Task 3) · 6. `ci:` preview renders (Task 1)
  (Commit order differs from the plan's task order because they were applied as patches; content is complete.)
- `origin/Dev` is 132 commits **behind** `origin/main`. Do not base work on `Dev` despite `docs/development.md`; the PR targets `main`.
- `core.autocrlf=true` was set in this repo's local config (Windows checkout read through a Linux mount showed every file modified without it). Harmless on Windows.
- Worktrees listed by `git worktree list` are stale (`prunable`); leave them.
- Working tree should be clean. `artifacts/sync/*.patch` are leftover transfer files; delete them.

## Resume

1. `git fetch origin` and confirm `main` is still `0e20e78` (v0.8.0). If `main` moved, rebase `feat/refined-glass` onto it first.
2. Build and test: `dotnet test DeadWare.slnx -c Release --nologo` (what CI runs via `scripts/publish.ps1`). Expected: all 721 existing tests plus the new ones pass. New tests to watch:
   - `ThemeTokenTests` (3 facts, pure)
   - `UiTypographyTests.Range_choices_match_history_ranges`
   - `BatteryUiTests.Real_battery_page_renders…` now also runs `RangeSegmentsUiChecks.Run(app)` (STA; asserts `Ui.Metric` tabular numerals, `Ui.Caption` uppercase, six `ToggleButton` segments, automation name "Graph history range", and that raising `UiSession.Changed` re-checks both controls). If `Ui.Caption("min").Text=="MIN"` or the Changed-event reflection fails, fix the test harness, not the control.
3. Render previews locally (fast, no CI needed):
   `dotnet run --project tools/OverviewPreview -c Release -- --render artifacts/preview-overview`
   `dotnet run --project tools/OverviewPreview -c Release -- --render-shell artifacts/preview-shell`
   These were repaired in Task 1 (nine themes from `Theme.Names`, ten routes incl. `usage`/`alerts`, `ThemePicker.SetSelection` instead of a ComboBox lookup, `UsageHistoryService`/`AlertService` set on the fake session). They have never been executed since; expect to debug. The `--render-shell` checks must end with `"failures": []` in `shell-checks.json` and no `*.db*` files under `%TEMP%\DeadWare-GlassPreview`. If the Usage/Alerts services open a store on construction, replace them with `RuntimeHelpers.GetUninitializedObject` as `CreateSession` does for `UiSession`.
4. Push: `git push -u origin feat/refined-glass`. CI (`.github/workflows/build.yml`) now also runs both render modes and uploads `DeadWare-previews`. Open the PNGs and check Task 3's visible changes: segmented range pill in the header and pop-out menus, hairline borders, DataGrid without alternating rows, 16 px checkboxes, `Card`-styled panels.
5. Mark Task 1 and Task 3 verified in the ledger (their lines say "CI verification pending"), then continue with **Task 4** using `superpowers:executing-plans` (the user chose Native execution; `task-start`/`task-done` scripts and the ledger are in place). TDD per task; the plan has the tests.

## Decisions already made (do not re-litigate)

- Stay on WPF. SkiaSharp only as a last-resort fallback for the map, and only after stopping to re-plan.
- Blackout: black window/panel/plot surfaces, no highlight wash, no plot recess; keeps trace under-glow, arc glow, particles, node halos.
- Gradient area fills: **not** wanted (user declined). Fill stays at current `GraphDepth.Area` strength following the new spline.
- Battery percent stays a polyline; Cooling Health and Usage history averages get the spline.
- Widget map: larger hit targets, no hover chips, particles at half density.
- Version stays 0.8.0; bump/tag is the user's call. CHANGELOG gets an "Unreleased" section in Task 11.
- Field names the preview tool reflects on (`navigation`, `themePicker`, `apps`, `sensors`, `status`, `monitoringInfo`, `startupStatus`, `startupBusy`, `pages`) must survive Tasks 4 and 10.

## Interfaces that now exist (Tasks 2–3)

- `ThemeTokens.Derive(Color line,Color accent,Color panel,bool highContrast,bool blackout) → DerivedTokens(Hairline,AccentSoft,Surface1,Surface2,Glow)`; registered as frozen `SolidColorBrush` resources `Hairline`, `AccentSoft`, `Surface1`, `Surface2`, `Glow` in `Theme.Apply` (via `ApplyDerivedTokens`, before `ApplyGlassResources`). Alphas 115/31/56; Surface1 = panel +3 % toward white, Surface2 = panel −6 % toward black.
- `App.xaml`: `Style x:Key="Card" TargetType="Border"` (radius 14, `GlassPanel`/`GlassBorder`). `Ui.Panel` and `ChartCard` use it. Buttons/TextBox/ComboBox/CheckBox/ToolTip borders use `Hairline`; keyboard focus = 2 px `Accent`; DataGrid headers 10 px `Muted`, rows hover `AccentSoft`, horizontal `Hairline` lines only.
- `Ui.Metric(string,double size=34,string color="Ink")` (MetricFont, SemiBold, tabular numerals), `Ui.Caption(string,string color="Muted")` (uppercase, 9.5 px).
- `RangeSegments : Border` with `internal static IReadOnlyList<(int Seconds,string Label)> Choices`; `Ui.Range(UiSession)` now returns `FrameworkElement` (a `RangeSegments`). `CompactGraph`'s Escape handler lost its `IsDropDownOpen` guard; `OverviewPreview.BuildView` casts `Ui.Range` to `FrameworkElement`.

## Remaining tasks (see the plan for steps, tests and exact values)

4. Nav rail with vector glyphs (`OverviewVisual.NavGlyph`, `Ui.NavButton`), one-row header with subtitle, `IconTile` card headers, Settings grouped into cards — `ShellUiChecks`.
5. Core helpers `MonotoneSpline.Build`, `TimeTicks.For`, `NearestSample.Find` in `src/DeadWare.Core/ChartGeometry.cs` — `ChartGeometryTests` (pure, run anywhere).
6. `HistoryChart` rewrite: `ChartFrame.Grid/Chip/Spline`, `GraphDepth.UnderGlow`, geometry cache keyed on query/revision/size/theme/glass, hover crosshair (`SetHover`, `HoverIndex`, `PlotRect`), inside labels, HH:mm ticks, `GuidelineSet` for crisp hairlines.
7. Same frame/hover on Thermal, Battery, Usage charts.
8. Core helpers `ConnectionProjection.ProjectRing` (limb clamp), `ArcParticles.Fractions/Count`, `ConnectionMapPoint.Label/Detail` — `MapGeometryTests`.
9. `ConnectionMap` rewrite: gradient ocean, filled land, atmosphere, three-stroke arcs, particles, glowing nodes, radar origin, hover chips, legend chips; `ConnectionsPage`/`ConnectionsWidget` supply labels.
10. Cards/toolbars on Connections, Network, Sensors, Alerts, Updates; `CompactGraph` one-row chrome; `WidgetGraph` card style.
11. README, CHANGELOG (Unreleased), `docs/previews/2026-11-02-refined-glass.md`, `docs/validation.md`, map README note on lakes, PR into `main` with four renders attached.
Then the final whole-branch review (fresh reviewer), fix pass, and `superpowers:finishing-a-development-branch`.

## Gotchas learned so far

- The preview tool is **not** in `DeadWare.slnx`; CI builds it only through the new workflow step.
- `BatteryUiTests` is the single STA fact that hosts every `*UiChecks.Run(app)`; add new checks there with a `Phase("…")` label.
- `UiSession.SetRange` calls `SaveAsync`, which dereferences `layout` on a fake session — in tests, set `Settings.HistorySeconds` and raise `Changed` via reflection instead of clicking.
- `Typography.SetNumeralAlignment` is on `TextBlock` via the attached `Typography` class; `Control.ForegroundProperty` is needed for `SetResourceReference` on a `ToggleButton` from a non-Control host.
- In the design-canvas preview, a `clip-path` placed inside a translated SVG group hid the land; the WPF renderer clips with `PushClip` before transforms, so it is unaffected — just don't copy the SVG structure.
- Commit trailer used so far: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` + `Claude-Session: https://claude.ai/code/session_017AZ6FVS8LZoZ75kSTTDyb9`. Use your own session's attribution from here on.
