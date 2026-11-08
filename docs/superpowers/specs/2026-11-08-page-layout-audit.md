# Page layout audit — design

Status: audit of every page except Connections, written 2026-11-08 after the Connections layout rework (PR #88, 0.17.1). Per-page designs are proposals; each page gets its own approval, release and pull request. The top three are presented for approval first.

Date: 2026-11-08. Anchors: `origin/main` at afe4a88 (0.17.0) plus PR #88. Evidence lines are against those.

## Intent

**The outcome.** The user said the Connections globe had been squeezed by stacked information and that "a lot of the pages honestly need a much better GUI layout". Connections was reworked first; this audit applies the same questions to the other eleven pages and ranks them, so the rework proceeds page by page with a shared vocabulary instead of eleven separate redesigns.

**The patterns** (all shipped in Connections, 0.17.1; reuse, do not reinvent):
- **P1 Primary visual first.** The page's chart, map or table sits in a Star row with a raised minimum height and grows into spare viewport height; the page scrolls instead of crushing it. A Star row inside an unbounded `StackPanel` does nothing: the main region needs a finite allocation (a body `Grid` with `MeasureOverride`, as Connections, or the Overview's explicit grid height).
- **P2 Overlay toolbar.** View-only controls (zoom, 2D/globe, animate, range for one chart) sit in a small glass `Ui.Panel` at the visual's top-right, topmost in Z-order; popups and hover chips avoid its rect.
- **P3 Side column.** A secondary list sits in a 280 DIP column beside the visual at page width ≥ 1100 DIP (measured before scrollbar subtraction) and falls back under it when narrower, re-parented by one guarded method, inserted before the next card so Tab order follows the visual order.
- **P4 Tabbed lower card.** Two or more stacked lower cards become one card with a `TabControl` (styles in `App.xaml`). Only the selected tab's content is in the visual tree: tests and `BringIntoView` navigation select the tab first.
- **P5 Status inside the controls card.** Summary and operation status lines live in the controls card (long text as tooltip), not loose between cards.
- **P6 Folded settings.** Recording, database, storage, retention and repair controls go in one collapsed `Expander`; warnings, progress and operation results stay visible above it.
- **P7 Shared helpers.** A titled-section helper over `Ui.Panel` (16 DIP padding, 12 DIP bottom gap, used five times today), a controls+status container, and a wrapping replacement for `Ui.Row` (a horizontal `StackPanel`, so it never wraps) where field groups must survive 1000 DIP.

**Assumptions.** The main window reserves about 231 DIP for sidebar and margins, so a 1220-wide window gives roughly 990 DIP of page width; "wide" means the window is 1330 DIP or more. Existing UI checks fix page widths at 620, 1000 or 1050; none asserts a 1400 layout, so each rework adds a wide and a narrow check.

## Ranking

| # | Page | Main problem (evidence) | Patterns | Effort | Priority |
|---|------|-------------------------|----------|--------|----------|
| 1 | Storage | Every drive selector (model, health, capacity bar, free text) stacks above the detail; the activity chart is fixed at 260 DIP (`StoragePage.cs:129`) and is pushed down one selector per drive. | P1 P3 P4 P5 | M | 1 |
| 2 | Usage history | Six fixed 170 DIP charts in a 2×3 grid, then two large application cards (tables `MaxHeight` 420), then an always-open settings card and a trailing export status (`UsageHistoryPage.cs:103`, `:158`). Longest page in the app. | P4 P5 P6 | L | 2 |
| 3 | Cooling Health | 220 DIP chart (`CoolingHealthPage.cs:31`) competes with always-open reference/setup forms and warnings. | P1 P2 P4 P6 | M | 3 |
| 4 | Battery history | 320 DIP fixed chart (`BatteryHistoryPage.cs:18`) cannot use spare height; recording/storage prose always open; also hosted detached in a pop-out. | P1 P2 P6 | M | 4 |
| 5 | Settings (Desktop) | Six expanded prose-heavy categories in one vertical form; title size and padding vary per section. | P4 P6 P7 | M | 5 |
| 6 | Alerts | Rule editor and ignored-app management grow and push the episode log down; action status detached. | P4 P5 P6 | M | 6 |
| 7 | Network | Chart row capped at 265 DIP (`Windows.cs:97`) so it never becomes the primary visual; footer explanation prose under the table. | P1 P5 | M | 7 |
| 8 | Overview | Already bounded (explicit grid height, `Overview.cs:48`). Permanent two columns at every width makes a deep page; footer lines loose. Raising minimums here would worsen it. | P5, responsive columns | M | 8 |
| 9 | Processes | Table already fills; statuses detached and definitions prose always visible. | P5 P6 | S | 9 |
| 10 | Updates | Actions and status detached from the version lines; full explanation card before notes. | P5 P6 | S | 10 |
| 11 | Sensors | Only the count/status line is loose under the table. | P5 | S | 11 |

Efforts: S = one sitting, one UI-check file touched; M = Connections-sized (one page file, one check file, docs, release note); L = Usage's three check files (757 + 1,369 lines plus Alerts cross-checks) and `BringIntoView` navigation.

## Designs, in priority order

### 1. Storage (0.17.2)

Body `Grid` with `MeasureOverride` as Connections. Row 0: status line (collapses when empty). Row 1 (Star, min 360): the selected drive's card with the activity `ChartCard` filling it; drive heading, Open in window and chart colour controls in a P2 toolbar. The drive selectors become a P3 side column (280 DIP) listing one compact row per drive (model · bus · capacity · health glyph, capacity bar kept) at ≥ 1100 DIP, falling back above the detail card when narrower. Row 2: one P4 card with **Volumes** and **Health** tabs (the volume table and the health/identity block). The unavailable/identity explanations stay in Health; health warnings that repeat the top status are shown once, in the status line. Tests: `StoragePageUiChecks.cs` gets a 1300 and a 900 layout, selector selection from the side list, tab contents. No sensor or data changes.

### 2. Usage history (0.17.3)

Keep the toolbar as the controls card and move the export result status into it (P5). The device charts card stays on top but its six charts share one bounded region: a 2×3 grid with a Star height of at least 3 × 170 that grows into spare viewport, and a **Charts** / **Applications** / **Application resources** P4 card below it is the alternative if the user prefers one visible section at a time; default proposal keeps the chart grid visible and tabs only the two application cards (**Applications** · **Application resources**). The "Keep a longer record" card becomes a P6 expander; its recording checkbox and Clear history… stay inside, status and storage lines above it visible. `BringIntoView` navigation (slot → resources, Show in Connections) selects the tab first. Tests: `UsageHistoryUiChecks.cs`, `ApplicationResourcesUiChecks.cs` (four-card order and below-the-fold assertions change), `AlertsUiChecks.cs` cross-navigation.

### 3. Cooling Health (0.17.4)

Body `Grid`; the thermal chart card is the Star row (min 360) with range and series toggles in a P2 toolbar. The assessment (status, reference comparison) is the controls card's status (P5), with a P3 side column for the assessment detail list at ≥ 1100 DIP if it is longer than four lines. Reference capture, setup and warnings become a P4 card (**Warnings** · **Reference**); recording/storage/retention fold into a P6 expander with the current warning visible above. Tests: `CoolingHealthUiChecks.cs` wide/narrow, tab selection before asserting reference controls.

### 4. Battery history

Chart to a Star row (min 360) in both the page and the detached pop-out host; range/refresh/export in a compact controls card with status (P5); recording and storage prose into a P6 expander. Tests cover the dashboard host and the pop-out host.

### 5. Settings

One card with P4 tabs per category (Monitoring · Appearance · Widget · Pop-outs · Startup · History and alerts), each tab a titled section (P7) with its advanced/repair rows in a P6 expander. Active warnings and operation results stay above the tabs. Keyboard: tabs are one Tab stop with arrows, as the Clear history window's groups.

### 6. Alerts

Episode log is the primary region (P1, bounded Star with min 300). Rules editor and ignored-app list become a P4 card (**Rules** · **Don't alert**); add/remove results into the controls card status (P5).

### 7. Network

Chart row cap 265 → Star with min 320 and no max; search strip gains the accounting status (already there) and the footer explanation prose moves to a tooltip or a P6 expander under the table.

### 8. Overview

Responsive column count: one column below 900 DIP, two to 1500, three above, with card height following the row count as today. Memory and the unavailable-reading note move into a thin footer card. No minimum raise.

### 9. Processes

Status lines into the search strip (P5); the definitions prose into a P6 expander under the table. Table unchanged.

### 10. Updates

Version lines, check/install actions and progress in one controls card (P5); explanation and recovery details in a P6 expander below the release notes; failures visible above.

### 11. Sensors

Count/status line into the search strip (P5). Nothing else.

## Shared helpers (first use pays for them)

- `Ui.Section(string title, UIElement content)`: titled glass card, 16 DIP padding, 12 DIP bottom margin, replacing the five private `Card` helpers.
- `Ui.ControlsCard(params UIElement[] rows, TextBlock status, TextBlock detail)`: wrap rows plus the P5 status pair.
- `Ui.Wrap(params UIElement[] children)`: a `WrapPanel` with the `Ui.Row` spacing, for field groups that must survive 1000 DIP.
- A `FoldedSettings` expander factory with the "status above, content inside" contract.

## Out of scope

Behaviour, data, history, exports, alerts and recording are unchanged by every design above. Colours come from theme resources only. No page changes its automation names; moved controls keep theirs.

## Process

Each page: bounded in-chat design from its paragraph above → user approval → implementation in the chosen posture → UI checks at a wide and a narrow width → build and both suites → release note and user guide → PR. One page per release number.
