# Refined Glass Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give DeadWare's WPF dashboard, pop-outs, widget and Connections map a refined-glass restyle with rewritten graph and map renderers, keeping every feature, setting, history and window behaviour unchanged.

**Architecture:** Presentation-only change on the existing WPF app. New pure geometry helpers in `DeadWare.Core` (monotone spline, time ticks, nearest sample, limb clamping, particle placement) are unit-tested without a window; the WPF renderers (`HistoryChart`, `ConnectionMap`, the three history charts) consume them through `DrawingContext`. Shared tokens derive from the nine existing palettes in `Theme.Apply`, and `App.xaml` restyles the stock controls. Visual evidence comes from the OverviewPreview offscreen renderer, which CI runs and uploads.

**Tech Stack:** .NET 10 (SDK 10.0.100 locally per `global.json`, 10.0.402 on CI), WPF, xUnit 2.9.3, OverviewPreview offscreen renderer, GitHub Actions on `windows-latest`. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-11-02-refined-glass-redesign-design.md`

## Global Constraints

- Branch `feat/refined-glass` from `origin/main` (0.8.0). `Dev` is stale and is not used. Pull request into `main`. Never force-push. Push every checkpoint so CI renders it.
- Build/test command is `dotnet test DeadWare.slnx -c Release --nologo` (what `scripts/publish.ps1` runs). The existing 721 tests stay green. Nothing in tests or the preview tool may start hardware, ETW, history stores, startup tasks or network requests.
- Stay on WPF; no framework migration; no SkiaSharp unless a task here is proven impossible, in which case stop and re-plan.
- All nine themes (`Theme.Names`: Midnight, Ember, Glacier, Amethyst, Neon, Blackout, Skyrim, Mage, Daylight) keep their eleven palette roles and fonts. Skyrim/Mage keep `RuneTrace` and the stone texture. Blackout keeps pure-black window, panel and plot surfaces with no highlight wash or recess but gets trace under-glow, arc glow, particles and node halos. High Contrast and disabled transparency stay opaque and flat with no glow.
- `MainWindow` keeps `MinWidth=900; MinHeight=640` and the 205 px rail. Preview checks stay: Network table ≥ 70 px and chart ≥ 65 px at 900×640; every shell capture contains "SYNTHETIC SAMPLE DATA"; no native window; no `*.db*` created.
- Version stays `0.8.0` in `Directory.Build.props` and `scripts/publish.ps1`; the version bump is the user's call and is not part of this plan.
- Commit messages use the repo's prefixes (`feat:`, `fix:`, `test:`, `ci:`, `docs:`, `chore:`) and end with:
  `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_017AZ6FVS8LZoZ75kSTTDyb9`.
- Spec pixel/alpha values are the defaults; an implementer may tune ±20% for legibility after seeing a render, and records the final value in the preview note (Task 11).

## Review Focus

1. **High-DPI hairlines (125%/150%)** — 1 px hairlines and gridlines must stay crisp, not blur to 2 px. Pinned in Task 6: gridline pens have thickness 1 and the chart pushes a `GuidelineSet` at each gridline y.
2. **Hover over a gap or empty series** — moving the mouse over a region with no readings must show no dot and no chip, never a stale value. Pinned in Task 5 (`NearestSample` returns `-1` for gap markers and empty series) and Task 6 (no popup geometry when the index is `-1`).
3. **Theme switch while a chart is cached** — after `Theme.Apply` to another theme the next frame must use the new brushes; a stale cache would paint old colors. Pinned in Task 6: the geometry cache key includes `Theme.CurrentName` and `Theme.GlassEnabled`.
4. **Map with nothing to draw** — no origin, zero routes, or `ConnectionAssets.Land` empty (asset failed to load) must still paint the ocean, legend and status without throwing. Pinned in Task 9.
5. **Narrow plots and slow polling** — a 1-minute range in a 120 px compact plot, or 10 s polling, must not produce overlapping time labels. Pinned in Task 5: `TimeTicks.For` doubles its step until labels are ≥ 48 px apart and returns at most two labels under 200 px.

---

### Task 1: Repair the preview renderer and make CI upload renders

**Files:**
- Modify: `tools/OverviewPreview/Program.cs:25` (Themes), `:74-110` (CreateSession), `:235-239` (theme selection and routes)
- Modify: `tools/OverviewPreview/README.md`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: `Theme.Names` (`src/DeadWare.App/Theme.cs:37`), `ThemePicker.SetSelection(string)` (`ThemePicker.cs:36`), `MainWindow` private field `themePicker`, `UsageHistoryService(string path,string machineKey,TimeProvider?,TimeSpan?)`, `AlertService(string path,string machineKey,UsageHistoryService usage,Func<string?> gpuKey,TimeProvider?,…)`.
- Produces: workflow artifact `DeadWare-previews` holding `preview-overview/*.png`, `preview-overview/checks.json`, `preview-shell/*.png`, `preview-shell/shell-checks.json`. Every later task's visual verification reads this artifact.

- [ ] **Step 1: Make the renderer cover all nine themes and ten routes**

In `Program.cs` replace the `Themes` array with `Theme.Names` (read via reflection the way `Call("Theme",…)` already does, or `typeof(Collector).Assembly.GetType("DeadWare.App.Theme")!.GetField("Names")`). Replace the `ComboBox` lookup at line 235 with `Field<object>(dashboard,"themePicker")` and invoke its `SetSelection(theme)` before `Call("Theme","Apply",theme)`. Extend the route array to `"overview","network","connections","battery","usage","alerts","sensors","cooling","desktop","updates"`. In `CreateSession`, construct `UsageHistoryService(Path.Combine(Scratch,"usage.db"),"synthetic-preview")` and `AlertService(Path.Combine(Scratch,"alerts.db"),"synthetic-preview",usage,()=>"preview-gpu")` and set `<Usage>k__BackingField` and `<Alerts>k__BackingField`; keep the existing "no `*.db*` under Scratch" check passing by giving both services a `flushInterval`/start that never opens the store (if a constructor opens the file eagerly, set the field to `RuntimeHelpers.GetUninitializedObject` of the type instead, as `CreateSession` does for `UiSession`). `usage` and `alerts` routes render at 1220×900 only.

- [ ] **Step 2: Add the CI step and artifact**

In `build.yml` after the `Build, test and package` step add:

```yaml
      - name: Render previews
        shell: pwsh
        run: |
          dotnet run --project tools/OverviewPreview -c Release -- --render artifacts/preview-overview
          dotnet run --project tools/OverviewPreview -c Release -- --render-shell artifacts/preview-shell
      - uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7
        if: always()
        with:
          name: DeadWare-previews
          path: artifacts/preview-*/
          if-no-files-found: error
          retention-days: 14
```

- [ ] **Step 3: Update the README counts**

`tools/OverviewPreview/README.md`: nine themes, ten routes, `90` shell files plus `18` minimum-size frames (`network` and `desktop` × nine themes), and the CI artifact name.

- [ ] **Step 4: Commit and push; confirm the artifact**

```bash
git add tools/OverviewPreview .github/workflows/build.yml
git commit -m "ci: render preview captures for all themes and routes and upload them"
git push -u origin feat/refined-glass
```
Expected: the `Windows build` run is green and `gh run download -n DeadWare-previews -D artifacts/ci-previews` (or the Actions page) yields `preview-shell/glass-shell-Blackout-connections.png` among 108 shell PNGs. Open two renders to confirm the current (pre-redesign) look is captured. If the run fails inside the renderer, fix the renderer in this task before moving on — later tasks depend on it.

---

### Task 2: Derived theme tokens

**Files:**
- Create: `src/DeadWare.App/ThemeTokens.cs`
- Modify: `src/DeadWare.App/Theme.cs:66-105` (`Apply`), `:124-131` (`ApplyGlassResources` fallback branch)
- Test: `tests/DeadWare.App.Tests/ThemeTokenTests.cs`

**Interfaces:**
- Produces: `internal readonly record struct DerivedTokens(Color Hairline,Color AccentSoft,Color Surface1,Color Surface2,Color Glow)`; `internal static class ThemeTokens { public static DerivedTokens Derive(Color line,Color accent,Color panel,bool highContrast,bool blackout); }`; application resources `"Hairline"`, `"AccentSoft"`, `"Surface1"`, `"Surface2"`, `"Glow"` (all `SolidColorBrush`, frozen) set on every `Theme.Apply`.

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class ThemeTokenTests
{
    [Fact] public void Derived_tokens_use_spec_alphas_and_small_surface_shifts()
    {
        var t=ThemeTokens.Derive(Color.FromRgb(0x35,0x41,0x50),Color.FromRgb(0x91,0xE7,0xCA),Color.FromRgb(0x15,0x1B,0x24),false,false);
        Assert.Equal(115,t.Hairline.A);Assert.Equal(0x35,t.Hairline.R);
        Assert.Equal(31,t.AccentSoft.A);Assert.Equal(56,t.Glow.A);
        Assert.True(t.Surface1.R>0x15&&t.Surface1.R<=0x15+8);
        Assert.True(t.Surface2.R<0x15&&t.Surface2.R>=0x15-8);
        Assert.Equal(255,t.Surface1.A);Assert.Equal(255,t.Surface2.A);
    }
    [Fact] public void High_contrast_tokens_are_opaque_and_glow_free()
    {
        var t=ThemeTokens.Derive(Colors.White,Colors.Yellow,Colors.Black,true,false);
        Assert.Equal(255,t.Hairline.A);Assert.Equal(0,t.Glow.A);Assert.Equal(0,t.AccentSoft.A);
    }
    [Fact] public void Blackout_surfaces_stay_pure_black_but_keep_glow()
    {
        var t=ThemeTokens.Derive(Color.FromRgb(0x30,0x30,0x30),Color.FromRgb(0xD4,0xD4,0xD4),Colors.Black,false,true);
        Assert.Equal(Colors.Black,t.Surface1);Assert.Equal(Colors.Black,t.Surface2);Assert.Equal(56,t.Glow.A);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/DeadWare.App.Tests -c Release --nologo --filter ThemeTokenTests`
Expected: FAIL, `ThemeTokens` not defined.

- [ ] **Step 3: Implement `ThemeTokens.Derive`**

Hairline = line at alpha 115; AccentSoft = accent at 31; Glow = accent at 56; Surface1 = panel mixed 4% toward white; Surface2 = panel mixed 6% toward black; `highContrast` → Hairline opaque `line`, AccentSoft and Glow alpha 0, surfaces = panel; `blackout` → both surfaces `Colors.Black`, Hairline and Glow as normal.

- [ ] **Step 4: Register the resources in `Theme.Apply`**

After the palette loop and before `ApplyGlassResources()`, call `Derive` with `ColorOf("Line")`, `ColorOf("Accent")`, `ColorOf("Panel")`, `SystemParameters.HighContrast`, `CurrentName=="Blackout"` and assign the five frozen brushes to `Application.Current.Resources`. In the High Contrast branch use the system brush colors.

- [ ] **Step 5: Run tests, commit**

Run: `dotnet test DeadWare.slnx -c Release --nologo` → PASS.
```bash
git add src/DeadWare.App/ThemeTokens.cs src/DeadWare.App/Theme.cs tests/DeadWare.App.Tests/ThemeTokenTests.cs
git commit -m "feat: derive hairline, accent-soft, surface and glow tokens from each palette"
```

---

### Task 3: Type scale, control styles and the segmented range control

**Files:**
- Modify: `src/DeadWare.App/App.xaml` (Button `:141`, TextBox `:161`, ComboBox `:180`, DataGrid `:237-264`, CheckBox `:265`, ToolTip `:297`, ScrollBar `:81`)
- Modify: `src/DeadWare.App/Charts.cs:11-66` (`Ui.Text`, `Ui.Heading`, `Ui.Range`)
- Create: `src/DeadWare.App/RangeSegments.cs`
- Test: `tests/DeadWare.App.Tests/RangeSegmentsUiChecks.cs` (called from `BatteryUiTests` STA fact), `tests/DeadWare.App.Tests/UiTypographyTests.cs`

**Interfaces:**
- Consumes: resources from Task 2; `UiSession.SetRange(int)`, `UiSession.HistorySeconds`, `UiSession.Changed`, `HistoryRanges.Seconds`, `HistoryRanges.Label(int)`.
- Produces: `internal sealed class RangeSegments : ItemsControl` with `public RangeSegments(UiSession session)`; `internal static IReadOnlyList<(int Seconds,string Label)> RangeSegments.Choices`; `Ui.Range(UiSession)` now returns `RangeSegments` (return type `FrameworkElement`); `Ui.Metric(string value,double size)` returning a `TextBlock` with `Typography.NumeralAlignment=Tabular` and `MetricFont`; `Ui.Caption(string)` (10 px, `Muted`, letter spacing via `Typography.Capitals`/`FontStretch` is not needed — use `TextBlock.CharacterSpacing`-free approach: uppercase text with a 0.08 em `Margin` substitute is not possible in WPF, so captions use `FontSize=9.5` uppercase text only).

- [ ] **Step 1: Write the failing tests**

`UiTypographyTests` (plain `[Fact]`s that construct `TextBlock`s on an STA thread are not needed — `Ui.Metric` returns a `TextBlock` which can be created on the xUnit thread as long as no `Application` is required):

```csharp
[Fact] public void Metric_text_uses_tabular_numerals_and_the_metric_font()
{
    var text=Ui.Metric("31.9 %",34);
    Assert.Equal(FontNumeralAlignment.Tabular,Typography.GetNumeralAlignment(text));
    Assert.Equal(34,text.FontSize);Assert.Equal(FontWeights.SemiBold,text.FontWeight);
}
[Fact] public void Range_choices_match_history_ranges()
{
    Assert.Equal(HistoryRanges.Seconds,RangeSegments.Choices.Select(c=>c.Seconds));
    Assert.Equal(["1 min","5 min","15 min","1 h","3 h","6 h"],RangeSegments.Choices.Select(c=>c.Label));
}
```

`RangeSegmentsUiChecks.Run(Application app)` (STA, mirrors `RuneTraceUiChecks`): build the fake session as `BatteryUiTests` does with `Settings.HistorySeconds=300`; create two `RangeSegments`; assert `AutomationProperties.GetName(first)=="Graph history range"`, six `ToggleButton` descendants, exactly one `IsChecked==true` whose tag is `300`; set `session.Settings.HistorySeconds=3600` and raise `Changed` via reflection (`typeof(UiSession).GetField("Changed",NonPublic|Instance)`), pump, assert both controls now check `3600`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/DeadWare.App.Tests -c Release --nologo --filter "UiTypographyTests|BatteryUiTests"`
Expected: FAIL (`Ui.Metric`, `RangeSegments` not defined).

- [ ] **Step 3: Implement `RangeSegments`**

An `ItemsControl` whose panel is a horizontal `StackPanel` inside a `Border` (`CornerRadius=9`, `BorderBrush=Hairline`, `Background=GlassControl`, padding 2). Items are `ToggleButton`s (`Tag=seconds`, `Content=label`, height 26, padding 10/0, `CornerRadius 7` via template) in a `GroupName`-free mutual exclusion: on `Checked` call `session.SetRange(seconds)` unless it already equals `session.HistorySeconds`; `Refresh()` sets `IsChecked` from `session.HistorySeconds`; subscribe `session.Changed` on `Loaded`, unsubscribe on `Unloaded`. Checked style: `Background=AccentSoft`, `BorderBrush=Accent`. Keyboard: Left/Right move the check. Tooltip "Choose how much graph history to show". `Ui.Range` returns it; `CompactGraph.cs:50` and `Windows.cs:66` compile unchanged because they only add it to panels.

- [ ] **Step 4: Restyle the controls in `App.xaml`**

Button: `CornerRadius=9`, `BorderBrush={DynamicResource Hairline}`, `Background={DynamicResource GlassControl}`, hover `GlassHover`, pressed `GlassSelected`, keyboard focus a 2 px `Accent` border; transitions via `VisualStateManager`-free `Trigger`s plus a 120 ms `ColorAnimation` only where a `SolidColorBrush` is animatable (otherwise instant — High Contrast must stay instant). TextBox/ComboBox: same radius and hairline, focus `Accent`. CheckBox: 16 px box, radius 4, `Accent` fill when checked. DataGrid: `GridLinesVisibility=Horizontal`, `HorizontalGridLinesBrush=Hairline`, row hover `AccentSoft`, header `FontSize=10` uppercase (`CharacterCasing` is not on `DataGridColumnHeader`; set header strings uppercase where columns are built, in `Windows.cs` `Network()`/`Sensors()` and `ConnectionsPage`), header foreground `Muted`. ToolTip: `GlassPopup` fill, hairline border, radius 8. ScrollBar thumb width 6. Add a `Style x:Key="Card" TargetType="Border"`: `CornerRadius=14`, `BorderThickness=1`, `BorderBrush=GlassBorder`, `Background=GlassPanel`, and apply it in `Ui.Panel` and `ChartCard`.

- [ ] **Step 5: Add `Ui.Metric` and `Ui.Caption`, use them in `ChartCard`**

`ChartCard` big value → `Ui.Metric(…,overview?34:30)`; MIN/AVG/MAX labels → `Ui.Caption`; `UpdateStatistics` unchanged.

- [ ] **Step 6: Run all tests, commit, push; check the CI renders**

Run: `dotnet test DeadWare.slnx -c Release --nologo` → PASS.
```bash
git add src/DeadWare.App/App.xaml src/DeadWare.App/Charts.cs src/DeadWare.App/RangeSegments.cs tests/DeadWare.App.Tests
git commit -m "feat: restyle controls, add tabular metrics and a segmented history range control"
git push
```
Expected in `DeadWare-previews`: `glass-shell-Blackout-overview.png` shows the segmented range pill; `glass-shell-Daylight-network.png` has horizontal-only table lines; `shell-checks.json` has `"failures": []`.

---

### Task 4: Navigation rail, header toolbar and card chrome

**Files:**
- Modify: `src/DeadWare.App/Windows.cs:49-66` (shell, sidebar, header), `:92-165` (`Desktop()` Settings page)
- Modify: `src/DeadWare.App/Overview.cs` (`OverviewVisual.MetricGlyph`, add `NavGlyph`, `IconTile`)
- Modify: `src/DeadWare.App/Charts.cs:272-330` (`ChartCard` header)
- Test: `tests/DeadWare.App.Tests/ShellUiChecks.cs` (called from the STA fact)

**Interfaces:**
- Consumes: Task 2 resources, Task 3 styles and `Ui.Metric`/`Ui.Caption`.
- Produces: `OverviewVisual.NavGlyph(string route)` returning a 16 px `Path` for each of the ten routes; `OverviewVisual.IconTile(string kind)` returning a 24 px rounded `Border` (`Background` = graph color at 14% via a `SolidColorBrush` clone with `Opacity=.14`) holding `MetricGlyph`; `Ui.NavButton(string route,string label,Action click)` — a `Button` whose template shows a 3 px `Accent` bar and `AccentSoft` fill when its `Tag=="active"`; `MainWindow.Navigate` sets `Tag` on the active button.

- [ ] **Step 1: Write the failing check**

`ShellUiChecks.Run(Application app)`: construct `MainWindow` as the preview does (`Set(dashboard,"startupBusy",true)`, detach `Content`), measure 1220×900, then assert: ten nav buttons, each containing a `Path` and a `TextBlock`, none containing "▦" or "⇄"; exactly one with `Tag=="active"` after `Navigate("connections")` and it is the Connections button; the header row contains a `RangeSegments`, the GPU `ComboBox` and the "↗ Open widget" button in one `DockPanel`; `Desktop()` contains at least five `Border`s with `Style==Resources["Card"]` each with a heading from {"Monitoring","Appearance","Widget","Startup","Pop-outs","History and alerts"}.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/DeadWare.App.Tests -c Release --nologo --filter BatteryUiTests` → FAIL on the nav glyph assertion.

- [ ] **Step 3: Implement the rail**

Replace the `foreach(var item in new[]{("overview","▦  Overview"),…})` loop with `Ui.NavButton(key,label,()=>Navigate(key))` plus `NavGlyph(key)` as a `DockPanel` child; geometries are simple stroked paths (grid, arrows, globe, battery, bars, triangle-bang, three lines, fan, gear, down-arrow) in the same `Glyph` helper style as `MetricGlyph`. Brand block: `Ui.Logo()` 30 px + "DeadWare" 19 semibold `Heading`. Bottom cluster: caption "COLOR THEME", `ThemePicker`, then "Restart as administrator" (`FontSize=10`, transparent background) and "Quit DeadWare", then `trayNotice` and `monitoringInfo` unchanged. Keep every existing field name (`navigation`, `batteryNavigation`, `themePicker`, `trayNotice`, `monitoringInfo`) so the preview tool's reflection keeps working.

- [ ] **Step 4: Implement the header toolbar and card header**

Header panel becomes one `DockPanel`: left `StackPanel` with `heading` (26) and a new `subtitle` `TextBlock` (11.5, `Muted`) filled in `Refresh` with `"{CpuName} · {GpuName} · {total RAM} · last sample {age:0.0} s ago"` (fall back to the existing status text when `Current` is null); right: `gpuControls`, `Ui.Range(session)`, `widget` button. Keep `updateNotice` under it. `ChartCard` header: `IconTile(kind)` left, title 13 semibold, ghost cluster right (`PopOut` and `GraphColorPicker` buttons use the Task 3 button style at 26 px with `Muted` foreground). Card: `Style=Card`, padding 16/13/16/11, `tint` overlay stays.

- [ ] **Step 5: Group the Settings page**

In `Desktop()` wrap the existing controls, in their current order, into cards titled Monitoring (polling interval, extended sensors, smoothing), Appearance (glass level, graph fan/pump options), Widget (widget graph choices, connections map, always on top), Pop-outs (joined pop-outs), Startup (start with Windows + status + repair/disable/refresh), History and alerts (usage/alerts/connections summaries and their Open buttons). No control moves to another window; `startupStatus`, `startupResult`, `startupRepair`, `startupDisable`, `startupRefresh` keep their names.

- [ ] **Step 6: Run tests, commit, push, review renders**

Run: `dotnet test DeadWare.slnx -c Release --nologo` → PASS.
```bash
git add src/DeadWare.App/Windows.cs src/DeadWare.App/Overview.cs src/DeadWare.App/Charts.cs tests/DeadWare.App.Tests/ShellUiChecks.cs tests/DeadWare.App.Tests/BatteryUiTests.cs
git commit -m "feat: navigation rail with vector icons, toolbar header and grouped settings cards"
git push
```
Expected: `glass-shell-*-desktop-900x640.png` shows grouped cards without clipping; `glass-shell-Skyrim-overview.png` still shows Nordic lettering and stone texture.

---

### Task 5: Pure chart geometry helpers (Core)

**Files:**
- Create: `src/DeadWare.Core/ChartGeometry.cs`
- Test: `tests/DeadWare.Tests/ChartGeometryTests.cs`

**Interfaces:**
- Produces (all in `namespace DeadWare.Core`):
  - `public readonly record struct ChartPoint(double X,double Y);`
  - `public readonly record struct CubicSegment(ChartPoint Control1,ChartPoint Control2,ChartPoint End);`
  - `public static class MonotoneSpline { public static IReadOnlyList<CubicSegment> Build(IReadOnlyList<ChartPoint> points); }` — Fritsch–Carlson tangents; `points` is one gap-free run with strictly increasing X; fewer than 2 points → empty.
  - `public readonly record struct TimeTick(double MonotonicSeconds,string Label);`
  - `public static class TimeTicks { public static IReadOnlyList<TimeTick> For(int historySeconds,double nowMonotonic,DateTimeOffset nowWallClock,double plotWidth,bool compact); }` — base step: 60→15 s, 300→60 s, 900→300 s, 3600→900 s, 10800 and 21600→3600 s; ticks sit on wall-clock multiples of the step; label `HH:mm` (`HH:mm:ss` for the 15 s step); the step doubles while adjacent labels would be closer than 48 px; `compact` caps the result at two ticks (the two nearest "Now").
  - `public static class NearestSample { public static int Find(IReadOnlyList<SamplePoint> points,double monotonicSeconds,double toleranceSeconds); }` — binary search by `MonotonicSeconds`; returns `-1` when empty, when the nearest point has `Value==null`, or when it is farther than `toleranceSeconds`.

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class ChartGeometryTests
{
    [Fact] public void Monotone_spline_never_overshoots_its_inputs()
    {
        ChartPoint[] pts=[new(0,10),new(10,90),new(20,91),new(30,20),new(40,20)];
        var segments=MonotoneSpline.Build(pts);
        Assert.Equal(4,segments.Count);
        foreach(var s in segments){Assert.InRange(s.Control1.Y,10,91);Assert.InRange(s.Control2.Y,10,91);}
        Assert.Equal(pts[4],segments[3].End);
        // Flat run keeps flat control points.
        Assert.Equal(20,segments[3].Control1.Y);Assert.Equal(20,segments[3].Control2.Y);
    }
    [Fact] public void Spline_with_fewer_than_two_points_is_empty()
        =>Assert.Empty(MonotoneSpline.Build([new ChartPoint(1,1)]));
    [Fact] public void Five_minute_ticks_fall_on_minute_boundaries_with_hh_mm_labels()
    {
        var now=new DateTimeOffset(2026,11,2,14,32,40,TimeSpan.Zero);
        var ticks=TimeTicks.For(300,1000,now,330,false);
        Assert.Equal(["14:28","14:29","14:30","14:31","14:32"],ticks.Select(t=>t.Label));
        Assert.Equal(1000-40,ticks[^1].MonotonicSeconds);
    }
    [Fact] public void Narrow_plots_double_the_step_and_compact_keeps_two()
    {
        var now=new DateTimeOffset(2026,11,2,14,32,40,TimeSpan.Zero);
        Assert.True(TimeTicks.For(300,1000,now,120,false).Count<=3);
        Assert.Equal(2,TimeTicks.For(300,1000,now,330,true).Count);
    }
    [Fact] public void One_minute_range_uses_seconds()
        =>Assert.All(TimeTicks.For(60,1000,new DateTimeOffset(2026,11,2,14,32,40,TimeSpan.Zero),600,false),t=>Assert.Matches(@"^\d\d:\d\d:\d\d$",t.Label));
    [Fact] public void Nearest_sample_skips_gaps_and_respects_tolerance()
    {
        SamplePoint[] pts=[new(DateTimeOffset.UnixEpoch,10,5),new(DateTimeOffset.UnixEpoch,11,null),new(DateTimeOffset.UnixEpoch,12,7)];
        Assert.Equal(0,NearestSample.Find(pts,10.2,1));
        Assert.Equal(-1,NearestSample.Find(pts,11,0.4));
        Assert.Equal(2,NearestSample.Find(pts,12.3,1));
        Assert.Equal(-1,NearestSample.Find(pts,30,1));
        Assert.Equal(-1,NearestSample.Find([],1,1));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/DeadWare.Tests -c Release --nologo --filter ChartGeometryTests` → FAIL, types not defined.

- [ ] **Step 3: Implement `ChartGeometry.cs`**

Fritsch–Carlson: secants `d[i]`; tangents `m[0]=d[0]`, `m[n-1]=d[n-2]`, interior `m[i]=0` when `d[i-1]*d[i]<=0` else their mean; for each interval with `d[i]!=0`, `a=m[i]/d[i]`, `b=m[i+1]/d[i]`, if `a²+b²>9` scale both by `3/√(a²+b²)`; when `d[i]==0` set both tangents 0. Control points at ±`dx/3`. `TimeTicks`: wall-clock of a monotonic second `s` is `nowWallClock - (nowMonotonic - s)`; first tick is the latest wall-clock multiple of the step ≤ now; walk back while within `historySeconds`; pixel spacing is `step/historySeconds*plotWidth`.

- [ ] **Step 4: Run tests, commit**

Run: `dotnet test DeadWare.slnx -c Release --nologo` → PASS.
```bash
git add src/DeadWare.Core/ChartGeometry.cs tests/DeadWare.Tests/ChartGeometryTests.cs
git commit -m "feat: monotone spline, wall-clock tick and nearest-sample helpers"
```

---

### Task 6: HistoryChart renderer rewrite

**Files:**
- Modify: `src/DeadWare.App/Charts.cs:67-258` (`HistoryChart`)
- Modify: `src/DeadWare.App/GraphDepth.cs` (add `UnderGlow`, make `Surface` a no-op for Blackout, keep `Area` signature)
- Create: `src/DeadWare.App/ChartFrame.cs`
- Test: `tests/DeadWare.App.Tests/LiveRenderingUiChecks.cs` (extend), `tests/DeadWare.App.Tests/ChartFrameTests.cs`

**Interfaces:**
- Consumes: Task 5 helpers; Task 2 resources; `GraphStroke.Draw`, `RuneTrace.Draw`, `GraphDepth.Area`, `GraphDepth.PointHalo`.
- Produces:
  - `internal static class ChartFrame` with `public static void Grid(DrawingContext dc,Rect plot,IReadOnlyList<(double Y,string Label)> lines,IReadOnlyList<(double X,string Label)> ticks,Typeface typeface,double pixelsPerDip,bool compact)` — pushes a `GuidelineSet` for every line/tick coordinate, draws 1 px `Hairline` lines, value labels inside the plot right-aligned 6 px from the right edge and 2 px above each line (left-aligned at 6 px for an optional `leftLabels` overload used by the RPM scale), tick labels 4 px below the plot and "Now" right-aligned; `public static Rect Chip(DrawingContext dc,Point anchor,Rect plot,IReadOnlyList<(string Text,Brush Color)> lines,Typeface typeface,double pixelsPerDip,bool compact)` — draws the hover chip (`GlassPopup` fill, `Hairline` border, radius 8, 8 px padding, 12 px from the anchor, flipped left when it would cross `plot.Right`) and returns its rect; `public static StreamGeometry Spline(IReadOnlyList<IReadOnlyList<Point>> runs)` — converts each run with `MonotoneSpline.Build` into `BezierTo` figures.
  - `HistoryChart` internals used by tests: `internal void SetHover(Point? position)`; `internal Rect PlotRect {get;}`; `internal int HoverIndex {get;}`.
  - `GraphDepth.UnderGlow(DrawingContext dc,Geometry geometry,string color,double width)`: one stroke `width+4` at opacity .10 using the trace color; skipped under High Contrast.

- [ ] **Step 1: Write the failing tests**

`ChartFrameTests` (plain facts, `DrawingVisual` on an STA helper thread is required for `DrawingContext`; reuse the `BatteryUiTests` STA pattern by adding a `ChartFrameUiChecks.Run()` instead if a plain fact cannot open a `DrawingVisual`):
- `Chip_flips_left_near_the_right_edge`: anchor at `plot.Right-10` → returned rect `Right <= plot.Right`.
- `Spline_emits_one_figure_per_run_and_bezier_segments`: two runs → `GetFlattenedPathGeometry().Figures.Count==2`.

Extend `LiveRenderingUiChecks.Run()` (existing fixture renders `HistoryChart` with sample history): assert the drawing has no `Pen` with a non-solid `DashStyle` on the `Hairline`/`Line` brush; every `GlyphRunDrawing` for value labels lies inside `chart.PlotRect`; `chart.SetHover(new Point(chart.PlotRect.Left+chart.PlotRect.Width*.6,chart.PlotRect.Top+10))` then `VisualTreeHelper.GetDrawing` contains a `GeometryDrawing` whose `Brush` is `ReferenceEquals` to `Resources["GlassPopup"]` and `chart.HoverIndex>=0`; `SetHover` at an x that maps into a gap of the sample history → `HoverIndex==-1` and no `GlassPopup` drawing; after `Theme.Apply("Ember")` and `chart.Refresh()`, the trace pen brush equals `Resources["Graph/cpu"]` (cache invalidated); pens drawn with `Theme.Brush("Hairline")` have `Thickness==1` and the drawing group contains a `GuidelineSet` (walk `DrawingGroup.GuidelineSet`).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/DeadWare.App.Tests -c Release --nologo --filter BatteryUiTests` → FAIL (`ChartFrame`, `SetHover` undefined).

- [ ] **Step 3: Implement `ChartFrame` and `GraphDepth.UnderGlow`**

- [ ] **Step 4: Rewrite `HistoryChart.OnRender`**

Keep `CurrentQuery`, `Prepare`, `Refresh`, `PrepareLatest` as they are. Replace the drawing section:
1. Plot rect: `left = compact?3:8` (no RPM) or the RPM label width + 10 when `showRpm`; `right = 8` (labels are inside now); `top = compact?8:14`; `bottom = compact?14:18`.
2. `GraphDepth.Surface` (no-op for Blackout and when glass is off) then `ChartFrame.Grid` with three value lines (0, ½, max) and `TimeTicks.For(session.HistorySeconds, now, DateTimeOffset.Now, pw, compact)`; RPM labels on the left via the `leftLabels` overload.
3. Trace cache: `private (Query Query,int Revision,Size Size,string Theme,bool Glass,Dictionary<string,(StreamGeometry Path,List<IReadOnlyList<Point>> Runs,Point? Last)> Series)? cache;` rebuilt when any key part differs. Runs come from the existing `ReduceForChart`/gap logic; path = `ChartFrame.Spline(runs)` unless `query.Seconds>=3600 && !query.Smoothed` (raw dense → existing polyline). The scroll `TranslateTransform` animation stays.
4. Per series: `GraphDepth.Area` (unchanged strength), `DrawRange` band, `GraphDepth.UnderGlow` when `!style.Glow`, `GraphStroke.Draw`, `RuneTrace.Draw`, last-point halo + dot.
5. Hover: `hover` field; `OnMouseMove` → `SetHover(e.GetPosition(this))`, `OnMouseLeave` → `SetHover(null)`; `SetHover` computes the monotonic second from x, `NearestSample.Find` on the primary series with tolerance `max(1.5, pollingInterval*1.5)` (and the same index lookup on each secondary series), stores `HoverIndex`, and calls `InvalidateVisual()` only when the index or x changed. In `OnRender`, when `HoverIndex>=0`: vertical `Hairline` at the sample x, a 3 px dot per series, then `ChartFrame.Chip` with lines `[time "HH:mm:ss", "{value} {unit}" in the series color, …]` (compact: values only). `IsHitTestVisible=true` with a transparent background rectangle drawn first so mouse events arrive.
6. Remove the "N min ago" label; keep the "Waiting…"/"No readings" messages.

- [ ] **Step 5: Run tests, commit, push, review renders**

Run: `dotnet test DeadWare.slnx -c Release --nologo` → PASS.
```bash
git add src/DeadWare.App/Charts.cs src/DeadWare.App/ChartFrame.cs src/DeadWare.App/GraphDepth.cs tests/DeadWare.App.Tests
git commit -m "feat: spline traces, hairline grid with inside labels, wall-clock ticks and hover crosshair"
git push
```
Expected: `overview-Midnight-1000.png` shows curved traces with labels inside the plot; `overview-Blackout-660.png` has no recessed tray; `checks.json` passes (chart ≥ 160×65 with a stroked `StreamGeometry` wider than 15).

---

### Task 7: Shared chart treatment for Cooling Health, Battery and Usage charts

**Files:**
- Modify: `src/DeadWare.App/ThermalHistoryChart.cs:68-128`, `src/DeadWare.App/BatteryHistoryChart.cs:23-91`, `src/DeadWare.App/UsageHistoryChart.cs:34-100`
- Test: extend `tests/DeadWare.App.Tests/BatteryUiTests.cs` battery checks and `UsageHistoryUiChecks.cs`

**Interfaces:**
- Consumes: `ChartFrame.Grid`, `ChartFrame.Chip`, `ChartFrame.Spline`, `GraphDepth.UnderGlow`.
- Produces: each chart gains `internal void SetHover(Point?)` and `internal int HoverIndex`; hover lookups use the chart's own bucket/window lists (nearest by X, tolerance half a bucket width).

- [ ] **Step 1: Write the failing checks**

Battery: after `SetHover` at the x of a known bucket, drawing contains a `GlassPopup` geometry and the chip text contains that bucket's percentage; no dashed `Line`-brush pens; `25%`…`100%` labels inside the plot. Usage: same chip check plus `GetFlattenedPathGeometry` of the average line is a single figure per full-bucket run (spline). Thermal: temperature trace figures are Bézier-derived (flattened figure has more segments than windows).

- [ ] **Step 2: Run to verify they fail** → FAIL (`SetHover` undefined).

- [ ] **Step 3: Implement**

Replace each chart's dashed/dotted grid loops with `ChartFrame.Grid` (value labels inside; the existing `right` gutter shrinks to 8 and `left` stays). Thermal: build runs of connected windows (`CanConnect`) and draw them with `ChartFrame.Spline` + `UnderGlow` + `seriesPen`; keep spread lines and markers. Usage: runs of `CanConnect` buckets → spline; keep bands. Battery: polyline as today; grid/labels/hover only. Date range labels stay at the bottom corners.

- [ ] **Step 4: Run tests, commit, push, review renders**

```bash
git commit -am "feat: shared hairline grid, inside labels and hover chips on history charts"
git push
```
Expected: `glass-shell-*-battery.png`, `-usage.png`, `-cooling.png` match the Overview grid treatment.

---

### Task 8: Pure map geometry helpers (Core)

**Files:**
- Modify: `src/DeadWare.Core/ConnectionProjection.cs` (add `ProjectRing`, extend `ConnectionMapPoint`)
- Create: `src/DeadWare.Core/ArcParticles.cs`
- Test: `tests/DeadWare.Tests/MapGeometryTests.cs`

**Interfaces:**
- Produces:
  - `ConnectionMapPoint(string Key, GeoPoint Point, long ReceivedBytes, long SentBytes, bool Selected, bool ReceiveActive=true, bool SendActive=true, string? Label=null, string? Detail=null)` — two new optional positional parameters at the end so every existing call compiles.
  - `public static IReadOnlyList<MapCoordinate> ConnectionProjection.ProjectRing(IReadOnlyList<GeoPoint> ring,MapCamera camera)` — globe: every point projected; invisible points replaced by the unit-circle point at the same azimuth (`Visible=false` kept); flat: identical to projecting each point (seam handling is the renderer's ±360° copies).
  - `public static class ArcParticles { public static IReadOnlyList<double> Fractions(double time,double speed,int count,bool towardOrigin); public static int Count(double level); }` — `Fractions` returns `count` values in `[0,1)` spaced `1/count` apart advancing with `time*speed`, reversed when `towardOrigin`; `Count` = 0 for `level<=0`, 1 for `<0.34`, 2 for `<0.67`, else 3.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Back_hemisphere_ring_points_are_clamped_to_the_limb()
{
    var camera=new MapCamera(-55,28,1,false);
    GeoPoint[] ring=[new(40,-100),new(40,100),new(-40,100),new(-40,-100),new(40,-100)];
    var projected=ConnectionProjection.ProjectRing(ring,camera);
    Assert.Equal(ring.Length,projected.Count);
    var back=projected[1];Assert.False(back.Visible);Assert.Equal(1,Math.Sqrt(back.X*back.X+back.Y*back.Y),3);
    Assert.True(projected[0].Visible);
}
[Fact] public void Flat_ring_projection_matches_point_projection()
{
    var camera=new MapCamera(0,0,1,true);GeoPoint[] ring=[new(10,10),new(10,20),new(0,20),new(10,10)];
    Assert.Equal(ring.Select(p=>ConnectionProjection.Project(p,camera)),ConnectionProjection.ProjectRing(ring,camera));
}
[Fact] public void Particle_fractions_stay_in_range_and_advance()
{
    var a=ArcParticles.Fractions(0,.25,3,false);var b=ArcParticles.Fractions(1,.25,3,false);
    Assert.All(a,f=>Assert.InRange(f,0,1));Assert.Equal(3,a.Count);
    Assert.Equal(a[0]+.25,b[0],6);
    Assert.Equal(1-a[0],ArcParticles.Fractions(0,.25,3,true)[0],6);
}
[Fact] public void Particle_count_follows_activity_level()
{Assert.Equal(0,ArcParticles.Count(0));Assert.Equal(1,ArcParticles.Count(.2));Assert.Equal(2,ArcParticles.Count(.5));Assert.Equal(3,ArcParticles.Count(1));}
[Fact] public void Map_point_labels_default_to_null()
{var p=new ConnectionMapPoint("k",new GeoPoint(1,2),1,1,false);Assert.Null(p.Label);Assert.Null(p.Detail);}
```

- [ ] **Step 2: Run to verify they fail** → FAIL.

- [ ] **Step 3: Implement**

`ProjectRing` globe branch: reuse `Project`'s orthographic math; for `Visible==false`, azimuth = `atan2(y,x)` of the raw projection and emit `(cos a, sin a, depth, false)`. `Fractions`: `((i/count)+time*speed) mod 1`, reversed as `1-f` when `towardOrigin`.

- [ ] **Step 4: Run tests, commit**

```bash
git add src/DeadWare.Core tests/DeadWare.Tests/MapGeometryTests.cs
git commit -m "feat: limb-clamped ring projection, arc particle placement and map point labels"
```

---

### Task 9: ConnectionMap renderer rewrite

**Files:**
- Modify: `src/DeadWare.App/ConnectionMap.cs` (fields `:11-37`, `EnsureGeometry` `:176-202`, `DrawFlow`/`DrawConnection` `:204-285`, `OnRender` `:295-359`, mouse handlers `:361-385`)
- Modify: `src/DeadWare.App/ConnectionsPage.cs:235,243` and `src/DeadWare.App/ConnectionsWidget.cs:35` (supply `Label`/`Detail`)
- Test: `tests/DeadWare.App.Tests/ConnectionMapUiChecks.cs` (extend)

**Interfaces:**
- Consumes: Task 8 helpers; Task 2 resources (`Surface2`, `Hairline`, `Glow`); `ConnectionAssets.Land/Borders/StateBorders`; `ConnectionMotion.Level/Time`.
- Produces: `public string? HoveredKey {get;}`; `public event Action<string?>? PointHovered;`; `internal void SetHover(Point? position)`; `internal const double HitRadius=9, WidgetHitRadius=12`; land/ocean/atmosphere layers; three-stroke arcs; particles; glowing nodes; legend chips; `ConnectionMapPoint.Label` = `"{process name}"` and `Detail` = `"{city or country} · ↓ {rate} · ↑ {rate}"` from `ConnectionsPage` (live and history) and the widget.

- [ ] **Step 1: Write the failing checks**

Extend `ConnectionMapUiChecks.Run(app)`: render a 600×400 map with an origin and three points (one with both directions active) in Midnight; assert the drawing contains a `GeometryDrawing` filled with `Resources["Surface2"]` (land) and one with a `RadialGradientBrush` (ocean/atmosphere); with `ConnectionAssets` land empty (set via reflection on `Data` or by a `ConnectionMap.LandOverride` internal property) rendering still succeeds and the status text is drawn; no stroked geometry uses the old strand opacity pattern — assert at least one arc pen `Thickness==6` and one `==1.2`; particles: at least `ArcParticles.Count(1)` ellipses with `RadialGradientBrush` fill near the active arc; `SetHover` on a node position → `HoveredKey` equals its key, `Cursor==Cursors.Hand`, and a `GlassPopup` geometry exists; `SetHover` 20 px away → `HoveredKey==null`; with `MapOnly=true`, `SetHover` on a node within 12 px sets `HoveredKey` but draws no `GlassPopup` geometry; `SetData([],null,true,"")` renders without exception.

- [ ] **Step 2: Run to verify they fail** → FAIL.

- [ ] **Step 3: Implement the layers**

`EnsureGeometry` additionally caches `landFill` (a `StreamGeometry` from `ProjectRing` per land ring, closed figures, `FillRule.Nonzero`; flat mode draws it three times with `TranslateTransform` ±2·zoom·xScale under the existing clip), keeping `coast`/`borders`/`stateBorders`/`grid` strokes. `OnRender` order: background → ocean (`RadialGradientBrush` for globe: `Surface1` at 38%/32% toward `Background` at the limb; vertical `LinearGradientBrush` for flat) → graticule (`Hairline` at .2) → land fill (`Surface2`) → coast (`Hairline`) → borders (`Muted` .28) → state borders (existing fade) → limb darkening (`RadialGradientBrush` transparent→black .55 from 80%) → atmosphere outside the clip (`Glow` radial, 0 at 86%, peak at 90%, 0 at 100% of `1.11·radius`) → arcs → particles → nodes → origin → legend chips → status. Blackout: ocean is pure black with the limb ring only; High Contrast: solid `Panel` ocean, no gradients, no glow.

- [ ] **Step 4: Arcs, particles, nodes, origin**

`DrawConnection`: one `StreamGeometry` per route from `route.Segments` (no wave), cached on the route; strokes 6 px @ .06, 2.5 px @ .18, 1.2 px core @ `.35+.55*level` in the active direction's brush; both active → two 1.2 px hairlines offset ±1 px along the segment normal; selected/hovered → opacities .12/.30/.95 plus a .5 px `Ink` line. Particles: for the 40 routes with the largest `ReceivedBytes+SentBytes`, `ArcParticles.Count(level)` per direction (halved, min 1, when `MapOnly`), position from `Fractions(motion.Time, .33, count, received)` mapped through the existing `DrawFlow` sample interpolation (keep its seam/hemisphere guard), drawn as a 7 px `RadialGradientBrush` disc plus a 2.2 px `Ink` dot. Nodes: `RadialGradientBrush` disc at 3× radius @ .25, solid core, `.6 px Ink` ring; pulse ring at radius `+6·frac(phase/1.6)` @ `.35·(1-frac)` when `level>0`. Origin: `Glow` disc r 22, rings r 8 @ .6 and r 13 @ .3, expanding ring `8+10·frac(phase/2)` @ `.5·(1-frac)`, `Ink` core r 3.6. Legend: two chips at (8,4) and (92,4) with a 8 px colored dot and "Download"/"Upload" (`MapOnly` → none).

- [ ] **Step 5: Hover and hit targets**

`OnMouseMove` (when not dragging) → `SetHover(position)`: nearest `hitPoints` entry within `MapOnly?WidgetHitRadius:HitRadius`; store `HoveredKey`, raise `PointHovered`, set `Cursor` (`Hand` on a node, `SizeAll` while dragging, `Arrow` otherwise), `InvalidateVisual` on change. `SelectAt` uses the same radii. Chip via `ChartFrame.Chip` with `Label`, `Detail` lines, anchored at the node, clamped inside `MapBounds`; `MapOnly` draws none. `OnMouseLeave` clears hover.

- [ ] **Step 6: Supply labels from the pages**

`ConnectionsPage` live: `Label=c.Traffic.Peer.Process.Name`, `Detail=$"{Location(c.Traffic.Peer.RemoteAddress,c.Location)} · ↓ {Ui.Bytes(rate,true)} · ↑ {Ui.Bytes(rate,true)}"`; history: representative row's process name and `Location(...)` with minute-average rates. Widget: process name only (chip not drawn, used for automation name).

- [ ] **Step 7: Run tests, commit, push, review renders**

Run: `dotnet test DeadWare.slnx -c Release --nologo` → PASS.
```bash
git add src/DeadWare.App/ConnectionMap.cs src/DeadWare.App/ConnectionsPage.cs src/DeadWare.App/ConnectionsWidget.cs tests/DeadWare.App.Tests/ConnectionMapUiChecks.cs
git commit -m "feat: filled globe with atmosphere, glowing arcs, particles, node halos and hover chips"
git push
```
Expected: `glass-shell-Blackout-connections.png` shows filled continents (compare with the design preview); `glass-shell-Daylight-connections.png` keeps readable borders on the light ocean.

---

### Task 10: Pages, pop-outs and widget polish

**Files:**
- Modify: `src/DeadWare.App/ConnectionsPage.cs:82-120` (toolbar strip, cards), `src/DeadWare.App/CompactGraph.cs:26-50`, `src/DeadWare.App/WidgetGraph.cs:20-40`, `src/DeadWare.App/Windows.cs:76-91` (`Network()`, `Sensors()`), `src/DeadWare.App/AlertsPage.cs`, `src/DeadWare.App/UpdatesPage.cs` (card wrappers only)
- Test: `tests/DeadWare.App.Tests/ConnectionsPageUiChecks.cs`, `ConnectionsWidgetUiChecks.cs` (extend), `NetworkChartUiChecks.cs` (minimums)

**Interfaces:**
- Consumes: `Card` style, `Ui.Caption`, `Ui.Metric`, `RangeSegments`.
- Produces: no new public surface; `CompactGraph` header is one row (`value`, device `details`, `options`); `ConnectionsPage` controls sit in a `Card` strip above the map card; node list and detail box are `Card`s.

- [ ] **Step 1: Write the failing checks**

ConnectionsPage: the map's parent is a `Border` with `Style==Card`; the controls `WrapPanel` sits in a `Card` above it; the detail box is a `Card`. Network at 900×640: existing minimums (table ≥ 70, chart ≥ 65) still hold with the card padding. CompactGraph at 260×140: header height ≤ 34 px and the chart's `ActualHeight ≥ 80`. Widget: `WidgetGraph` uses `Card` style and its chart renders hover-free (no `GlassPopup` drawing after `SetHover(null)`).

- [ ] **Step 2: Run to verify they fail** → FAIL.

- [ ] **Step 3: Implement** — wrap, don't move. Keep every field name referenced by the preview tool (`apps`, `sensors`, `status`, `monitoringInfo`, `startupStatus`).

- [ ] **Step 4: Run tests, commit, push, review all 108 shell renders and the overview set**

```bash
git commit -am "feat: card chrome for pages, pop-outs and the widget"
git push
```
Expected: every `shell-checks.json` failure list empty; `glass-shell-Neon-network-900x640.png` has no clipped table.

---

### Task 11: Documentation and preview note

**Files:**
- Modify: `README.md` (Features list: replace the glass/graph/Connections bullets with the new behaviour; mention hover chips and wall-clock ticks), `CHANGELOG.md` (new top section `## Unreleased — Refined glass redesign`), `assets/maps/README.md` (note: land rings are now filled; inland lakes render as land because holes are omitted), `tools/OverviewPreview/README.md` (already updated in Task 1; re-check counts)
- Create: `docs/previews/2026-11-02-refined-glass.md` — what changed visually, final tuned values that differ from the spec defaults, the CI artifact name, and the "not verified from CI" list (mixed DPI, live acrylic, 60 fps animation, native input)
- Modify: `docs/validation.md` — new section `## Refined glass redesign (unreleased, 2026-11-xx)` listing test counts, render coverage (9 themes × 10 routes + 18 minimum frames + 21 overview frames) and what was not verified

- [ ] **Step 1: Write the docs**
- [ ] **Step 2: Commit, push, open the pull request**

```bash
git add README.md CHANGELOG.md assets/maps/README.md docs/previews/2026-11-02-refined-glass.md docs/validation.md tools/OverviewPreview/README.md
git commit -m "docs: refined glass redesign notes, changelog and validation coverage"
git push
gh pr create --base main --head feat/refined-glass --title "Refined glass redesign" --body-file docs/previews/2026-11-02-refined-glass.md
```
PR body ends with the attribution lines from Global Constraints and attaches four renders (Blackout overview, Blackout connections, Daylight overview, Skyrim overview) downloaded from the final CI run. Report the PR link and the CI result.

---

## Self-review notes

- Spec coverage: §1 tokens → Task 2; type/controls/segmented range → Task 3; rail/header/cards/Settings groups → Task 4; §2 spline/axes/ticks/hover/glow → Tasks 5–6; other charts → Task 7; §3 surface/arcs/particles/nodes/hover/legend → Tasks 8–9; §4 pages/pop-outs/widget → Task 10; Blackout rule → Tasks 2, 6, 9; verification/CI → Task 1; docs → Task 11. Version bump intentionally excluded.
- Names used across tasks: `ChartFrame.Grid/Chip/Spline`, `GraphDepth.UnderGlow`, `RangeSegments`, `Ui.Metric`, `Ui.Caption`, `Ui.NavButton`, `OverviewVisual.NavGlyph/IconTile`, `ThemeTokens.Derive`, `MonotoneSpline.Build`, `TimeTicks.For`, `NearestSample.Find`, `ConnectionProjection.ProjectRing`, `ArcParticles.Fractions/Count`, `ConnectionMapPoint.Label/Detail`, `HistoryChart.SetHover/HoverIndex/PlotRect`, `ConnectionMap.SetHover/HoveredKey/PointHovered` — each defined once in its task's Interfaces block.
- Execution prerequisite: pushing and downloading CI artifacts needs GitHub credentials. This session currently has none; either the user grants it repository access or pushes `feat/refined-glass` from Windows after each checkpoint and drops the downloaded `DeadWare-previews` folder into `I:\DeadWare\artifacts\ci-previews` for review.
