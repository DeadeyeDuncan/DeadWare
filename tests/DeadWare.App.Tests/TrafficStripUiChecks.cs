using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Offscreen checks of the traffic strip chart and view, run inside BatteryUiTests' single STA Application fixture.</summary>
internal static class TrafficStripUiChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);

    internal static void Run(Application app)
    {
        Theme.SetGraphColors(new Dictionary<string, string>());
        CheckStackOrderAndColours(app);
        Theme.Apply("Midnight");
        CheckGapsZerosAndPartials();
        CheckBandsAndSeparators();
        CheckChipAndLegend();
        CheckKeysAndClicks();
        CheckNotKeptAndEmptyTexts();
        CheckHoverDoesNotRebuildGeometry();
        CheckThemeRefresh(app);
        Theme.SetGraphColors(new Dictionary<string, string>());
        Theme.Apply("Midnight");
    }

    private static void CheckStackOrderAndColours(Application app)
    {
        foreach (string theme in Theme.Names)
        {
            Theme.Apply(theme);
            var chart = new TrafficStripChart(TrafficStripPage.Connections);
            chart.SetData(Strip(1), T0.AddHours(1), T0.AddHours(1));
            var drawings = Draw(chart);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, chart.Bands.Select(b => b.Series));
            Assert.All(chart.Bands, b => Assert.True(b.Full));
            // The largest series sits at the bottom, so each band's top is above the one below it.
            for (int k = 1; k < 7; k++) Assert.True(chart.Bands[k].Geometry.Bounds.Top < chart.Bands[k - 1].Geometry.Bounds.Top, $"{theme} band {k}");
            for (int k = 0; k < 7; k++)
            {
                var drawing = drawings.Single(d => ReferenceEquals(d.Geometry, chart.Bands[k].Geometry));
                Assert.Null(drawing.Pen);
                Assert.Equal(k < 6 ? Theme.StripBrush(k).Color : ((SolidColorBrush)app.Resources["Muted"]).Color, ((SolidColorBrush)drawing.Brush).Color);
            }
            var panel = ((SolidColorBrush)app.Resources["Panel"]).Color;
            for (int i = 0; i < 6; i++)
            {
                Assert.True(DeltaE76(Theme.StripColor(i, false), panel) >= 30, $"{theme} series {i} against Panel: {DeltaE76(Theme.StripColor(i, false), panel):0.0}");
                for (int j = i + 1; j < 6; j++)
                    Assert.True(DeltaE76(Theme.StripColor(i, false), Theme.StripColor(j, false)) >= 20, $"{theme} {i}/{j}: {DeltaE76(Theme.StripColor(i, false), Theme.StripColor(j, false)):0.0}");
            }
            var greys = Enumerable.Range(0, 6).Select(i => Theme.StripColor(i, true)).ToArray();
            Assert.Equal(6, greys.Distinct().Count());
            Assert.All(greys, c => Assert.True(c.R == c.G && c.G == c.B, $"{c} is not grey"));
        }
        Theme.SetGraphColors(new Dictionary<string, string> { ["download"] = "#123456" });
        Assert.Equal(Hex("#123456"), Theme.StripBrush(0).Color);
        Assert.Equal(ThemeTokens.CoreTint(Hex("#123456"), 1, 2, false), Theme.StripBrush(3).Color);
        Assert.NotEqual(Hex("#123456"), Theme.StripBrush(1).Color);
        Theme.SetGraphColors(new Dictionary<string, string>());
    }

    private static void CheckGapsZerosAndPartials()
    {
        // 36 one-minute slots: 5 missing; 10 zero (60 s, no bytes); 15 partial (45 s); 20 incomplete (60 s); 25 no capture. 34 and 35 are unlisted.
        var strip = Strip(36, build: (k, slot) => k is 5 or > 33 ? null : k switch
        {
            10 => slot with { Received = Zeros(7), Sent = Zeros(7) },
            15 => slot with { RecordedSeconds = 45 },
            20 => slot with { Incomplete = true },
            25 => slot with { RecordedSeconds = 0, Received = Zeros(7), Sent = Zeros(7) },
            _ => slot,
        });
        var chart = new TrafficStripChart(TrafficStripPage.UsageHistory);
        chart.SetData(strip, T0.AddMinutes(34), T0.AddMinutes(34.5));
        Draw(chart);
        Point Centre(int k) => At(chart, T0.AddMinutes(k + .5));
        Assert.NotNull(chart.Hatch);
        Assert.True(chart.Hatch!.FillContains(Centre(5)), "a missing minute is hatched");
        Assert.True(chart.Hatch.FillContains(Centre(25)), "a slot without capture is hatched");
        Assert.All(new[] { 0, 10, 15, 20, 30, 34, 35 }, k => Assert.False(chart.Hatch.FillContains(Centre(k)), $"minute {k} is not hatched"));

        chart.HoverAt(Centre(34));
        Assert.Null(chart.HoverIndex);
        Assert.Equal(new[] { Span(T0.AddMinutes(34), T0.AddMinutes(35)), "Not saved yet", "Minutes are saved after they end" }, chart.ChipLines);
        chart.HoverAt(Centre(35));
        Assert.Empty(chart.ChipLines);
        chart.HoverAt(Centre(25));
        Assert.Equal(24, chart.HoverIndex);   // slot 5 is missing, so minute 25 is the 25th listed slot
        Assert.Equal(new[] { Span(T0.AddMinutes(25), T0.AddMinutes(26)), "No per-application capture" }, chart.ChipLines);
        chart.HoverAt(null);

        Assert.NotNull(chart.Baseline);
        Assert.True(chart.Baseline!.Bounds.Left <= Centre(10).X && Centre(10).X <= chart.Baseline.Bounds.Right);
        Assert.Equal(chart.PlotRect.Bottom, chart.Baseline.Bounds.Top, 3);
        Draw(chart);
        Assert.Equal(0.45, BandBrush(chart, Centre(15).X).Opacity, 3);
        Assert.Equal(0.45, BandBrush(chart, Centre(20).X).Opacity, 3);
        Assert.Equal(0.85, BandBrush(chart, Centre(0).X).Opacity, 3);
        Assert.DoesNotContain(chart.Bands, b => b.Geometry.FillContains(new Point(Centre(10).X, chart.PlotRect.Bottom - 1)));
        // Full runs [0–4], [6–9], [11–14], [16–19], [21–24], [26–33]; partial runs [15] and [20].
        Assert.Equal(14, chart.Bands.Count);
        Assert.All(chart.Bands.Where(b => b.Full), b => Assert.Equal(6, Figures(b.Geometry)));
        Assert.All(chart.Bands.Where(b => !b.Full), b => Assert.Equal(2, Figures(b.Geometry)));

        // A fully recorded 6.72-minute Usage slot with 6 minute starts is full.
        var from = T0.AddSeconds(1); var to = from.AddMilliseconds(403_200);
        Assert.Equal(6, TrafficStrips.MinuteStarts(from, to));
        var fractional = new TrafficStrip(from, to, 403_200, TrafficMeasure.Total, Apps(6, 1), 1000, 1000, 2,
            [new(from, to, 6, 360, false, Series(7), Series(7))]);
        var single = new TrafficStripChart(TrafficStripPage.UsageHistory);
        single.SetData(fractional, to, to.AddHours(1));
        Draw(single);
        Assert.Equal(0.85, BandBrush(single, single.PlotRect.Left + single.PlotRect.Width / 2).Opacity, 3);
    }

    private static void CheckBandsAndSeparators()
    {
        var chart = new TrafficStripChart(TrafficStripPage.Connections);
        chart.SetData(Strip(36), T0.AddMinutes(36), T0.AddHours(1));
        var drawings = Draw(chart);
        Assert.Equal(7, chart.Bands.Count);
        Assert.All(chart.Bands, b => Assert.Null(drawings.Single(d => ReferenceEquals(d.Geometry, b.Geometry)).Pen));
        // Every band but the top one (Other) gets its separator, one horizontal segment per slot, drawn with a 1 px Panel pen.
        Assert.Equal(6, chart.Separators.Count);
        var panel = ((SolidColorBrush)Theme.Brush("Panel")).Color;
        foreach (var separator in chart.Separators)
        {
            var figures = PathGeometry.CreateFromGeometry(separator).Figures;
            Assert.Equal(36, figures.Count);
            Assert.All(figures, f =>
            {
                var segment = Assert.IsType<LineSegment>(Assert.Single(f.Segments));
                Assert.Equal(f.StartPoint.Y, segment.Point.Y);
                Assert.True(segment.Point.X > f.StartPoint.X);
            });
            var stroke = drawings.Single(d => ReferenceEquals(d.Geometry, separator));
            Assert.Null(stroke.Brush); Assert.Equal(1, stroke.Pen.Thickness); Assert.Equal(panel, ((SolidColorBrush)stroke.Pen.Brush).Color);
        }
        var dense = new TrafficStripChart(TrafficStripPage.Connections);
        dense.SetData(Strip(1440), T0.AddMinutes(1440), T0.AddDays(1));
        Draw(dense);
        Assert.Equal(7, dense.Bands.Count);
        Assert.Empty(dense.Separators);
    }

    private static void CheckChipAndLegend()
    {
        // 0 full; 1 partial (45 s); 2 incomplete; 3 under a second; 4 no traffic.
        var strip = Strip(5, build: (k, slot) => k switch
        {
            1 => slot with { RecordedSeconds = 45 },
            2 => slot with { Incomplete = true },
            3 => slot with { RecordedSeconds = 0 },
            4 => slot with { Received = Zeros(7), Sent = Zeros(7) },
            _ => slot,
        });
        var view = new TrafficStripView(Session(), TrafficStripPage.Connections);
        view.Apply(strip, strip.To, T0.AddHours(1));
        Layout(view, 900, 400);
        var chart = view.Chart;
        Point Centre(int k) => At(chart, T0.AddMinutes(k + .5));
        string SlotSpan(int k) => Span(T0.AddMinutes(k), T0.AddMinutes(k + 1));
        const long total = 28 * 4000;
        string Value(double seconds) => $"{Ui.Bytes(total)} · {Ui.Bytes(total / seconds, true)} average";
        chart.HoverAt(Centre(0)); Assert.Equal(new[] { SlotSpan(0), Value(60) }, chart.ChipLines);
        chart.HoverAt(Centre(1)); Assert.Equal(new[] { SlotSpan(1), Value(45), "Recorded 1 of 1 min" }, chart.ChipLines);
        chart.HoverAt(Centre(2)); Assert.Equal(new[] { SlotSpan(2), Value(60), "Recorded 1 of 1 min", "Incomplete capture" }, chart.ChipLines);
        chart.HoverAt(Centre(3)); Assert.Equal(new[] { SlotSpan(3), "Recorded 0 of 1 min" }, chart.ChipLines);
        chart.HoverAt(Centre(4)); Assert.Equal(new[] { SlotSpan(4), "No traffic" }, chart.ChipLines);
        Layout(view, 900, 400);   // the hover chip renders without throwing
        chart.HoverAt(null);

        // Totals with shares: series i carries (7 − i) of every 28 parts, so the largest holds 25 %.
        Assert.Equal(8, view.Legend.Count);
        Assert.Equal(("Totals for this interval", (string?)null, (Brush?)null, false), view.Legend[0]);
        Assert.Equal($"app0.exe · {Ui.Bytes(5L * 7 * 4000)} · 25%", view.Legend[1].Text);
        Assert.Equal($"app0.exe · ↓ {Ui.Bytes(5L * 7 * 3000)} · ↑ {Ui.Bytes(5L * 7 * 1000)}", view.Legend[1].Tip);
        Assert.Equal($"Other (3 more) · {Ui.Bytes(5L * 4000)} · {(100d / 28).ToString("0.#", CultureInfo.CurrentCulture)}%", view.Legend[7].Text);
        for (int i = 0; i < 6; i++) { Assert.Same(Theme.StripBrush(i), view.Legend[i + 1].Swatch); Assert.True(view.Legend[i + 1].IsButton); }
        Assert.Equal(((SolidColorBrush)Theme.Brush("Muted")).Color, ((SolidColorBrush)view.Legend[7].Swatch!).Color);
        Assert.False(view.Legend[7].IsButton);

        // Hover, then selection: the header is the slot's span and the values are the slot's, without shares.
        // The entries are updated in place, so a focused legend button keeps its focus.
        var legendPanel = (WrapPanel)view.Children[2]; var firstButton = (Button)legendPanel.Children[1];
        chart.HoverAt(Centre(0));
        Assert.Equal(SlotSpan(0), view.Legend[0].Text);
        Assert.Equal($"app0.exe · {Ui.Bytes(7 * 4000)}", view.Legend[1].Text);
        Assert.Equal($"app0.exe · ↓ {Ui.Bytes(7 * 3000)} · ↑ {Ui.Bytes(7 * 1000)}", view.Legend[1].Tip);
        Assert.Same(firstButton, legendPanel.Children[1]);
        Assert.Equal(view.Legend[1].Tip, firstButton.ToolTip);
        Assert.Equal(view.Legend[1].Text, ((DockPanel)firstButton.Content).Children.OfType<TextBlock>().Single().Text);
        chart.HoverAt(null);
        Assert.Equal("Totals for this interval", view.Legend[0].Text);
        chart.SelectedIndex = 1;
        Assert.Equal(SlotSpan(1), view.Legend[0].Text);
        Assert.Equal($"Other (3 more) · {Ui.Bytes(4000)}", view.Legend[7].Text);
        chart.SelectedIndex = 3;
        Assert.All(view.Legend.Skip(1), e => { Assert.EndsWith(" · —", e.Text); Assert.Equal("Under a second recorded in this slot", e.Tip); });
        chart.SelectedIndex = null;
        Assert.Equal("Totals for this interval", view.Legend[0].Text);

        // A legend button chooses its application; Other is plain text.
        var buttons = Descendants(view).OfType<Button>().ToArray();
        Assert.Equal(6, buttons.Length);
        Assert.Equal("app0.exe: show only this application", AutomationProperties.GetName(buttons[0]));
        (string Key, string Name)? chosen = null;
        view.ApplicationChosen += (key, name) => chosen = (key, name);
        buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(("APP0.EXE", "app0.exe"), chosen);

        // Usage history: a slot without capture shows "—" with its reason; the whole span is a period.
        var usageStrip = Strip(3, build: (k, slot) => k == 1 ? slot with { RecordedSeconds = 0, Received = Zeros(7), Sent = Zeros(7) } : slot);
        var usage = new TrafficStripView(Session(), TrafficStripPage.UsageHistory);
        usage.Apply(usageStrip, usageStrip.To, T0.AddHours(1));
        Layout(usage, 900, 400);
        Assert.Equal("Totals for this period", usage.Legend[0].Text);
        Assert.All(usage.Legend, e => Assert.False(e.IsButton));
        Assert.Empty(Descendants(usage).OfType<Button>());
        usage.Chart.HoverAt(At(usage.Chart, T0.AddMinutes(1.5)));
        Assert.Equal(new[] { Span(T0.AddMinutes(1), T0.AddMinutes(2)), "No per-application capture" }, usage.Chart.ChipLines);
        Assert.All(usage.Legend.Skip(1), e => { Assert.EndsWith(" · —", e.Text); Assert.Equal("No per-application capture in this slot", e.Tip); });

        // Fifteen applications: six and "Other (9 more)". Other 0: no Other entry.
        var many = new TrafficStripView(Session(), TrafficStripPage.Connections);
        many.Apply(Strip(5, otherApplications: 9), T0.AddMinutes(5), T0.AddHours(1));
        Assert.StartsWith("Other (9 more) · ", many.Legend[^1].Text);
        var five = Strip(5, otherApplications: 0);
        var noOther = five with
        {
            OtherReceived = 0, OtherSent = 0,
            Slots = five.Slots.Select(x => x with { Received = x.Received.Take(6).Append(0L).ToArray(), Sent = x.Sent.Take(6).Append(0L).ToArray() }).ToArray(),
        };
        many.Apply(noOther, T0.AddMinutes(5), T0.AddHours(1));
        Layout(many, 900, 400);
        Assert.Equal(7, many.Legend.Count);
        Assert.DoesNotContain(many.Legend, e => e.Text.StartsWith("Other", StringComparison.Ordinal));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, many.Chart.Bands.Select(b => b.Series));
        Assert.Equal(5, many.Chart.Separators.Count);   // Other is not drawn, so series 5 is the top band

        // No application bytes.
        var silent = five with
        {
            Applications = five.Applications.Select(a => a with { Received = 0, Sent = 0 }).ToArray(), OtherReceived = 0, OtherSent = 0,
            Slots = five.Slots.Select(x => x with { Received = Zeros(7), Sent = Zeros(7) }).ToArray(),
        };
        many.Apply(silent, T0.AddMinutes(5), T0.AddHours(1));
        Assert.Equal("No application traffic recorded in this interval.", Assert.Single(many.Legend).Text);
        usage.Apply(silent, T0.AddMinutes(5), T0.AddHours(1));
        Assert.Equal("No application traffic recorded in this period.", Assert.Single(usage.Legend).Text);

        // A 256-character name trims within 240 px, with the full name in the tooltip.
        string longName = new string('a', 252) + ".exe";
        var plain = Strip(5);
        var named = plain with { Applications = plain.Applications.Select((a, i) => i == 0 ? a with { Name = longName, Key = longName.ToUpperInvariant() } : a).ToArray() };
        view.Apply(named, T0.AddMinutes(5), T0.AddHours(1));
        Layout(view, 900, 400);
        var label = Descendants(view).OfType<TextBlock>().Single(t => t.Text.StartsWith(longName, StringComparison.Ordinal));
        Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
        Assert.Equal(TextWrapping.NoWrap, label.TextWrapping);
        DependencyObject entry = label;
        while (VisualTreeHelper.GetParent(entry) is not WrapPanel) entry = VisualTreeHelper.GetParent(entry)!;
        var element = (FrameworkElement)entry;
        Assert.InRange(element.ActualWidth, 1, 240);
        Assert.True(label.ActualWidth < 240);
        Assert.Equal($"{longName} · ↓ {Ui.Bytes(5L * 7 * 3000)} · ↑ {Ui.Bytes(5L * 7 * 1000)}", view.Legend[1].Tip);
        Assert.Equal(view.Legend[1].Tip, element.ToolTip);
    }

    private static void CheckKeysAndClicks()
    {
        // Listed minutes 0–33 except 5; 34 is the begun not-yet minute.
        var strip = Strip(36, build: (k, slot) => k is 5 or > 33 ? null : slot);
        var chart = new TrafficStripChart(TrafficStripPage.Connections) { Selectable = true };
        Assert.True(chart.Focusable);
        chart.SetData(strip, T0.AddMinutes(34), T0.AddMinutes(34.5));
        Draw(chart);
        int count = strip.Slots.Count;
        Assert.Equal(33, count);
        var moved = new List<int>(); var activated = new List<(int Index, DateTimeOffset At)>();
        chart.SelectionMoved += moved.Add;
        chart.SlotActivated += (index, at) => activated.Add((index, at));

        Assert.True(chart.HandleKey(Key.Left));
        Assert.Equal(count - 1, chart.SelectedIndex); Assert.Equal(new[] { count - 1 }, moved);
        Assert.True(chart.HandleKey(Key.Right));   // at the end the selection stays and nothing is raised
        Assert.Equal(count - 1, chart.SelectedIndex); Assert.Single(moved);
        chart.SelectedIndex = null;
        Assert.True(chart.HandleKey(Key.Right));
        Assert.Equal(0, chart.SelectedIndex); Assert.Equal(0, moved[^1]);
        Assert.True(chart.HandleKey(Key.Left));
        Assert.Equal(0, chart.SelectedIndex); Assert.Equal(2, moved.Count);
        Assert.True(chart.HandleKey(Key.Right));
        Assert.Equal(1, chart.SelectedIndex);
        Assert.True(chart.HandleKey(Key.Enter));
        Assert.Equal((1, strip.Slots[1].Start), Assert.Single(activated));
        activated.Clear();

        // With MarkTime in the gap at minute 5, Left goes to minute 4's slot and Right to minute 6's.
        chart.MarkTime = T0.AddMinutes(5.5);
        chart.SelectedIndex = null;
        Assert.True(chart.HandleKey(Key.Left));
        Assert.Equal(T0.AddMinutes(4), strip.Slots[chart.SelectedIndex!.Value].Start); Assert.Equal(chart.SelectedIndex, moved[^1]);
        chart.SelectedIndex = null;
        Assert.True(chart.HandleKey(Key.Right));
        Assert.Equal(T0.AddMinutes(6), strip.Slots[chart.SelectedIndex!.Value].Start); Assert.Equal(chart.SelectedIndex, moved[^1]);
        chart.MarkTime = null;

        // A click's time lies inside its slot; a gap or the not-yet target raises nothing.
        chart.HandleClick(At(chart, T0.AddMinutes(3).AddSeconds(20)));
        var click = Assert.Single(activated);
        Assert.Equal(3, click.Index);
        Assert.True(click.At >= strip.Slots[3].Start && click.At < strip.Slots[3].End, $"{click.At} outside slot 3");
        Assert.Equal(3, chart.SelectedIndex);
        chart.HandleClick(At(chart, T0.AddMinutes(5.5)));
        chart.HandleClick(At(chart, T0.AddMinutes(34.5)));
        Assert.Single(activated);
        Assert.Null(chart.SlotAt(At(chart, T0.AddMinutes(5.5))));
        Assert.Equal(5, chart.SlotAt(At(chart, T0.AddMinutes(6.5))));   // minute 5 is missing, so minute 6 is the sixth listed slot

        // The keyboard focus frame is an Accent pen around the plot.
        var accent = ((SolidColorBrush)Theme.Brush("Accent")).Color;
        int Frames() => Draw(chart).Count(d => d.Geometry is RectangleGeometry r && d.Pen?.Brush is SolidColorBrush b && b.Color == accent && r.Rect.Contains(chart.PlotRect));
        Assert.Equal(0, Frames());
        KeyboardFocus(chart, true);
        Assert.Equal(1, Frames());
        KeyboardFocus(chart, false);
        Assert.Equal(0, Frames());
    }

    private static void CheckNotKeptAndEmptyTexts()
    {
        var chart = new TrafficStripChart(TrafficStripPage.Connections);
        chart.SetData(Strip(36, build: (k, slot) => k < 10 ? null : slot) with { RetainedFrom = T0.AddMinutes(10) }, T0.AddMinutes(36), T0.AddHours(1));
        Draw(chart);
        var notKept = chart.NotKept!.Value;
        Assert.Equal(chart.PlotRect.Left, notKept.Left, 6);
        Assert.Equal(At(chart, T0.AddMinutes(10)).X, notKept.Right, 6);
        Assert.Equal(chart.PlotRect.Top, notKept.Top, 6); Assert.Equal(chart.PlotRect.Bottom, notKept.Bottom, 6);
        Assert.True(notKept.Width >= 60);
        Assert.Contains("Not kept", DrawnText(chart));
        Assert.True(chart.Hatch is null || !chart.Hatch.FillContains(At(chart, T0.AddMinutes(5.5))), "not-kept minutes are not hatched");

        var old = new TrafficStripChart(TrafficStripPage.Connections);
        old.SetData(Strip(36, build: (_, _) => null) with { RetainedFrom = T0.AddMinutes(40) }, T0.AddMinutes(36), T0.AddHours(1));
        Draw(old);
        Assert.Equal(old.PlotRect, old.NotKept);
        Assert.Contains("Not kept", DrawnText(old));
        Assert.DoesNotContain("No saved history in this interval", DrawnText(old));
        Assert.Null(old.Hatch);

        var view = new TrafficStripView(Session(), TrafficStripPage.Connections);
        var retained = T0.AddMinutes(10);
        view.Apply(Strip(36) with { RetainedFrom = retained }, T0.AddMinutes(36), T0.AddHours(1));
        Assert.Equal($"Not kept before {retained.ToLocalTime().ToString("g")}", view.NotKeptLine);
        Assert.Equal("Traffic by application · 1-minute slots", view.Caption);
        view.Apply(Strip(36) with { RetainedFrom = retained, CapacityShortened = true, Measure = TrafficMeasure.Received }, T0.AddMinutes(36), T0.AddHours(1));
        Assert.Equal($"Not kept before {retained.ToLocalTime().ToString("g")} · Capacity shortened retention", view.NotKeptLine);
        Assert.Equal("Traffic by application · 1-minute slots · received", view.Caption);
        view.Apply(Strip(36) with { Measure = TrafficMeasure.Sent }, T0.AddMinutes(36), T0.AddHours(1));
        Assert.Equal("", view.NotKeptLine);
        Assert.Equal("Traffic by application · 1-minute slots · sent", view.Caption);

        string Shown(TrafficStripView target) { Layout(target, 900, 400); return DrawnText(target.Chart); }
        view.ShowText("Reading saved history…"); Assert.Contains("Reading saved history…", Shown(view)); Assert.Null(view.Chart.Strip);
        view.Apply(null, T0, T0); Assert.Contains("History unavailable", Shown(view)); Assert.Empty(view.Legend);
        view.ShowText("Choose a valid interval"); Assert.Contains("Choose a valid interval", Shown(view));
        view.Apply(Strip(36, build: (_, _) => null), T0.AddMinutes(36), T0.AddHours(1)); Assert.Contains("No saved history in this interval", Shown(view));

        var usage = new TrafficStripView(Session(), TrafficStripPage.UsageHistory);
        usage.Apply(Strip(36, build: (_, _) => null), T0.AddMinutes(36), T0.AddHours(1));
        Assert.Contains("No history in this range yet", Shown(usage));
        usage.Apply(Strip(36, build: (_, slot) => slot with { RecordedSeconds = 0 }), T0.AddMinutes(36), T0.AddHours(1));
        Assert.Contains("No per-application capture in this range", Shown(usage));
        usage.Apply(Strip(36), T0.AddMinutes(36), T0.AddHours(1));
        Assert.DoesNotContain("No per-application capture in this range", Shown(usage));
        usage.Apply(null, T0, T0); Assert.Contains("History unavailable", Shown(usage));
    }

    private static void CheckHoverDoesNotRebuildGeometry()
    {
        var chart = new TrafficStripChart(TrafficStripPage.Connections);
        chart.SetData(Strip(1500), T0.AddMinutes(1500), T0.AddDays(2));
        Draw(chart);
        var before = chart.Bands.Select(b => b.Geometry).ToArray();
        Assert.Equal(7, before.Length);
        for (int i = 0; i < 100; i++)
        {
            chart.HoverAt(new Point(chart.PlotRect.Left + chart.PlotRect.Width * (i + .5) / 100, chart.PlotRect.Top + 20));
            Assert.NotNull(chart.HoverIndex);
            Assert.All(chart.Bands.Zip(before), pair => Assert.Same(pair.Second, pair.First.Geometry));
        }
        Draw(chart);
        Assert.All(chart.Bands.Zip(before), pair => Assert.Same(pair.Second, pair.First.Geometry));
        Draw(chart, 700);
        Assert.Equal(7, chart.Bands.Count);
        Assert.All(chart.Bands.Zip(before), pair => Assert.NotSame(pair.Second, pair.First.Geometry));
    }

    private static void CheckThemeRefresh(Application app)
    {
        var session = Session();
        var view = new TrafficStripView(session, TrafficStripPage.Connections);
        view.Apply(Strip(36), T0.AddMinutes(36), T0.AddHours(1));
        Layout(view, 900, 400);
        Color Swatch(int series) => ((SolidColorBrush)view.Legend[series + 1].Swatch!).Color;
        void RaiseChanged() => ((Action?)typeof(UiSession).GetField("Changed", All)!.GetValue(session))?.Invoke();
        try
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Theme.SetGraphColors(new Dictionary<string, string> { ["download"] = "#123456" });
            RaiseChanged();
            Assert.Equal(Hex("#123456"), Swatch(0));
            Assert.Equal(Theme.StripBrush(0).Color, Swatch(0));
            Assert.Equal(Theme.StripBrush(3).Color, Swatch(3));

            // A Windows high-contrast switch re-applies the theme without Changed; the view refreshes at Background priority.
            Theme.SetGraphColors(new Dictionary<string, string> { ["download"] = "#654321" });
            Assert.Equal(Hex("#123456"), Swatch(0));
            RaiseHighContrastChanged();
            Pump();
            Assert.Equal(Hex("#654321"), Theme.StripBrush(0).Color);
            for (int i = 0; i < 6; i++) Assert.Equal(Theme.StripBrush(i).Color, Swatch(i));

            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Theme.SetGraphColors(new Dictionary<string, string> { ["download"] = "#ABCDEF" });
            RaiseHighContrastChanged();
            Pump();
            RaiseChanged();
            Assert.Equal(Hex("#654321"), Swatch(0));
        }
        finally
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Theme.SetGraphColors(new Dictionary<string, string>());
        }
    }

    // ---- Fixtures and helpers ----

    private static UiSession Session() => (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
    private static long[] Zeros(int count) => new long[count];
    /// <summary>Series s carries (count − s) parts of every slot's bytes, so the largest is at the bottom and Other (the last) is smallest.</summary>
    private static long[] Series(int count, long part = 1000) => Enumerable.Range(0, count).Select(s => (count - s) * part).ToArray();
    private static TrafficStripApplication[] Apps(int count, long slots)
        => Enumerable.Range(0, count).Select(i => new TrafficStripApplication($"app{i}.exe", $"APP{i}.EXE", slots * (count + 1 - i) * 3000, slots * (count + 1 - i) * 1000)).ToArray();

    /// <summary>n one-minute full slots from T0 with every series non-zero (received 3,000 and sent 1,000 per part); build may change or drop a slot.</summary>
    private static TrafficStrip Strip(int slots, Func<int, TrafficStripSlot, TrafficStripSlot?>? build = null, long otherApplications = 3)
    {
        var listed = new List<TrafficStripSlot>();
        for (int k = 0; k < slots; k++)
        {
            var slot = new TrafficStripSlot(T0.AddMinutes(k), T0.AddMinutes(k + 1), 1, 60, false, Series(7, 3000), Series(7, 1000));
            if ((build is null ? slot : build(k, slot)) is { } kept) listed.Add(kept);
        }
        return new TrafficStrip(T0, T0.AddMinutes(slots), 60_000, TrafficMeasure.Total, Apps(6, slots), slots * 3000L, slots * 1000L, otherApplications, listed);
    }

    private static string Span(DateTimeOffset start, DateTimeOffset end) => $"{start.ToLocalTime():HH:mm}–{end.ToLocalTime():HH:mm}";
    private static Point At(TrafficStripChart chart, DateTimeOffset time)
    {
        var strip = chart.Strip!; var plot = chart.PlotRect;
        return new(plot.Left + plot.Width * (time - strip.From).TotalSeconds / (strip.To - strip.From).TotalSeconds, plot.Top + plot.Height / 2);
    }
    /// <summary>The fill of series 0 (the bottom band) at x.</summary>
    private static SolidColorBrush BandBrush(TrafficStripChart chart, double x)
    {
        var band = chart.Bands.Single(b => b.Series == 0 && b.Geometry.FillContains(new Point(x, chart.PlotRect.Bottom - 1)));
        return (SolidColorBrush)Drawings(VisualTreeHelper.GetDrawing(chart)!).Single(d => ReferenceEquals(d.Geometry, band.Geometry)).Brush;
    }
    private static int Figures(Geometry geometry) => PathGeometry.CreateFromGeometry(geometry).Figures.Count;
    private static GeometryDrawing[] Draw(TrafficStripChart chart, double width = 600, double height = 130)
    {
        chart.Measure(new Size(width, height)); chart.Arrange(new Rect(0, 0, width, height)); chart.UpdateLayout();
        Assert.Equal(width, chart.ActualWidth);
        return Drawings(VisualTreeHelper.GetDrawing(chart)!).ToArray();
    }
    private static void Layout(FrameworkElement element, double width, double height)
    { element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout(); }
    private static IEnumerable<GeometryDrawing> Drawings(Drawing? drawing)
        => drawing is GeometryDrawing geometry ? [geometry] : drawing is DrawingGroup group ? group.Children.SelectMany(Drawings) : [];
    private static string DrawnText(Visual visual) => VisualTreeHelper.GetDrawing(visual) is { } drawing ? string.Concat(Glyphs(drawing).Select(g => new string(g.GlyphRun.Characters.ToArray()))) : "";
    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph] : drawing is DrawingGroup group ? group.Children.SelectMany(Glyphs) : [];
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    /// <summary>Tells an element its keyboard focus changed, as the keyboard does; offscreen nothing can take keyboard focus itself.</summary>
    private static void KeyboardFocus(UIElement element, bool focused)
        => typeof(UIElement).GetMethod("OnIsKeyboardFocusedChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(DependencyPropertyChangedEventArgs)])!
            .Invoke(element, [new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusedProperty, !focused, focused)]);
    /// <summary>Raises SystemParameters.StaticPropertyChanged for HighContrast through its backing delegate, as a Windows switch does.</summary>
    private static void RaiseHighContrastChanged()
    {
        var field = typeof(SystemParameters).GetField(nameof(SystemParameters.StaticPropertyChanged), BindingFlags.Static | BindingFlags.NonPublic)
            ?? typeof(SystemParameters).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(PropertyChangedEventHandler));
        ((PropertyChangedEventHandler?)field.GetValue(null))?.Invoke(null, new PropertyChangedEventArgs(nameof(SystemParameters.HighContrast)));
    }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);

    /// <summary>CIE76 ΔE between two sRGB colours, through CIE Lab with the D65 white point.</summary>
    private static double DeltaE76(Color a, Color b)
    {
        static (double L, double A, double B) Lab(Color c)
        {
            static double Linear(byte v) { double x = v / 255d; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            double r = Linear(c.R), g = Linear(c.G), bl = Linear(c.B);
            double x = (0.4124 * r + 0.3576 * g + 0.1805 * bl) / 0.95047, y = 0.2126 * r + 0.7152 * g + 0.0722 * bl, z = (0.0193 * r + 0.1192 * g + 0.9505 * bl) / 1.08883;
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16d / 116;
            return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
        }
        var (l1, a1, b1) = Lab(a); var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }
}
