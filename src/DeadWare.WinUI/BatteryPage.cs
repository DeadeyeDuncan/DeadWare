using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>The battery charge history drawn with Win2D from <see cref="BatteryChartModel"/>, with a hover readout.</summary>
internal sealed class BatteryChartView : UserControl
{
    private const double Left = 12, Right = 90, Top = 18, Bottom = 90;
    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat RightLabelFormat = new() { FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Right };
    private static readonly Color Amber = Color.FromArgb(255, 0xF2, 0x99, 0x4A), Green = Color.FromArgb(255, 0x27, 0xAE, 0x60);

    private readonly CanvasControl canvas = new();
    private BatteryHistoryView? view;
    private BatteryChartFrame? frame;
    private Point? pointer;

    public BatteryChartView()
    {
        canvas.Draw += Draw;
        var layers = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        layers.Children.Add(canvas);
        layers.PointerMoved += (_, e) => { pointer = e.GetCurrentPoint(layers).Position; canvas.Invalidate(); };
        layers.PointerExited += (_, _) => { pointer = null; canvas.Invalidate(); };
        Content = layers;
        MinHeight = 220;
        SizeChanged += (_, _) => Rebuild();
        ActualThemeChanged += (_, _) => canvas.Invalidate();
    }

    public void SetData(BatteryHistoryView data)
    {
        view = data;
        Rebuild();
    }

    private void Rebuild()
    {
        frame = view is null ? null : BatteryChartModel.Build(view, new ChartPlot(Left, Top, ActualWidth - Left - Right, ActualHeight - Top - Bottom));
        canvas.Invalidate();
    }

    private Color LookColor(BatterySaverLook look, Color ink) => look switch
    {
        BatterySaverLook.On => Amber,
        BatterySaverLook.Unknown => ink,
        _ => (Color)Application.Current.Resources["SystemAccentColor"],
    };

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (frame is not { } current || current.Lines.Count == 0) return;
        var session = args.DrawingSession;
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)0;
        Color ink = Color.FromArgb(170, c, c, c), line = Color.FromArgb(32, c, c, c);
        var plot = current.Plot;
        float left = (float)plot.Left, right = (float)(plot.Left + plot.Width), top = (float)plot.Top, bottom = (float)(plot.Top + plot.Height);
        session.FillRectangle(left, top, right - left, bottom - top, Color.FromArgb(10, c, c, c));
        foreach (var value in current.Lines)
        {
            session.DrawLine(left, (float)value.Y, right, (float)value.Y, line, 1);
            session.DrawText(value.Label, right - 4, (float)value.Y - 14, ink, RightLabelFormat);
        }

        using var dashes = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        using var dots = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dot };
        CanvasStrokeStyle? Style(BatterySaverLook look) => look switch { BatterySaverLook.On => dashes, BatterySaverLook.Unknown => dots, _ => null };
        foreach (var point in current.Points)
        {
            Color color = LookColor(point.Look, ink);
            if (point.Connector is { } start)
            {
                Color joined = LookColor(start.Look, ink);
                if (Style(start.Look) is { } style) session.DrawLine((float)start.X, (float)start.Y, (float)point.X, (float)point.Y, joined, 1.8f, style);
                else session.DrawLine((float)start.X, (float)start.Y, (float)point.X, (float)point.Y, joined, 1.8f);
            }
            if (point.Range is { } range) session.DrawLine((float)point.X, (float)range.Top, (float)point.X, (float)range.Bottom, color, 1);
            if (point.Certain) session.FillCircle((float)point.X, (float)point.Y, 2, color);
            else session.DrawCircle((float)point.X, (float)point.Y, 2, color, 1.3f);
        }
        foreach (var mark in current.Marks)
        {
            Color color = mark.Kind == BatteryMarkKind.Charging ? Green : Amber;
            float y = bottom + (mark.Kind == BatteryMarkKind.Charging ? 34 : 52);
            if (mark.Whole) session.FillRectangle((float)mark.X - 2, y, 4, 7, color);
            else session.DrawRectangle((float)mark.X - 2, y, 4, 7, color, 1);
        }
        session.DrawText(current.FromLabel, left, bottom + 8, ink, LabelFormat);
        session.DrawText(current.ToLabel, right, bottom + 8, ink, RightLabelFormat);
        session.DrawText("Charging", right + 8, bottom + 30, ink, LabelFormat);
        session.DrawText("Saver", right + 8, bottom + 48, ink, LabelFormat);
        if (!current.HasReadings) session.DrawText("No charge readings in this range yet", left + 10, (top + bottom) / 2 - 8, ink, LabelFormat);

        if (pointer is not { } position || BatteryChartModel.Hover(current, position.X, position.Y) is not { } hover) return;
        Color hovered = LookColor(hover.Look, ink);
        session.DrawLine((float)hover.X, top, (float)hover.X, bottom, ink, 1);
        session.FillCircle((float)hover.X, (float)hover.Y, 3, hovered);
        (string Text, Color Color)[] rows = [(hover.Time, ink), (hover.Text, hovered)];
        float width = 0;
        foreach (var row in rows)
        {
            using var layout = new CanvasTextLayout(sender, row.Text, LabelFormat, 0, 0);
            width = Math.Max(width, (float)layout.LayoutBounds.Width);
        }
        width += 12;
        float height = rows.Length * 16 + 12;
        float x = (float)hover.X + 10;
        if (x + width > right) x = (float)hover.X - 10 - width;
        float chipTop = (float)Math.Clamp(hover.Y - height / 2, top, Math.Max(top, bottom - height));
        session.FillRoundedRectangle(x, chipTop, width, height, 4, 4, dark ? Color.FromArgb(240, 32, 32, 32) : Color.FromArgb(240, 252, 252, 252));
        session.DrawRoundedRectangle(x, chipTop, width, height, 4, 4, line, 1);
        for (int i = 0; i < rows.Length; i++) session.DrawText(rows[i].Text, x + 6, chipTop + 6 + i * 16, rows[i].Color, LabelFormat);
    }
}

/// <summary>
/// Battery charge now and over time: the current charge and power state, the history chart for a chosen range, CSV
/// export, and the recording switch with what is kept. The pop-out window is not ported yet.
/// </summary>
internal sealed class BatteryPage : Page
{
    private static readonly (string Label, int Days)[] Ranges = [("Day", 1), ("Week", 7), ("Month", 30), ("Year", 365), ("All history", 0)];

    private readonly MonitorSession session;
    private readonly TextBlock percentage = new() { Text = "—", FontSize = 42 }, power = new() { Text = "Waiting for battery…", FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock summary = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap }, recordingStatus = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock storage = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap }, actionStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox range = new() { MinWidth = 120 };
    private readonly Button export = new() { Content = "Export CSV…" };
    private readonly CheckBox recording = new() { Content = "Record battery history on this device" };
    private readonly BatteryChartView chart = new() { Height = 360 };
    private readonly DispatcherQueueTimer timer;
    private int revision;
    private bool refreshing, refreshAgain, syncing;

    private int Days => range.SelectedIndex >= 0 ? Ranges[range.SelectedIndex].Days : 1;

    public BatteryPage(MonitorSession session)
    {
        this.session = session;
        foreach (var choice in Ranges) range.Items.Add(choice.Label);
        range.SelectedIndex = 0;
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => RequestRefresh();
        export.Click += async (_, _) => await ExportAsync();

        var now = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, Children = { percentage, power } };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { range, refresh, export } };
        var controlsCard = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { now, controls, actionStatus } });

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        legend.Children.Add(new TextBlock { Text = "━ Saver off", Foreground = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"]) });
        legend.Children.Add(new TextBlock { Text = "╌ Saver on", Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF2, 0x99, 0x4A)) });
        legend.Children.Add(new TextBlock { Text = "┈ State unknown", Opacity = 0.7 });
        var hint = new TextBlock
        {
            Text = "The charge line changes color and style at recorded saver changes. Hollow dots mean mixed or unknown saver state in a summarized time bucket; vertical ranges show minimum–maximum charge. Green marks: charging. Amber marks: saver enabled; outlined marks: mixed states. Gaps remain unconnected. Dates use local time.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
        };
        var historyCard = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { Heading("Battery charge"), chart, legend, summary, hint } });

        var notes = new StackPanel { Spacing = 8 };
        notes.Children.Add(new TextBlock
        {
            Text = "One observation per minute, plus charging and energy saver (battery saver) changes, retained indefinitely. Recording begins with this version while DeadWare runs; sleep and stopped monitoring leave gaps. Summarizing the chart keeps the original observations. Export the selected range as a new CSV file to keep a backup.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
        });
        notes.Children.Add(new TextBlock
        {
            Text = "History stays in this Windows profile and survives app upgrades. Copy battery-history.db while DeadWare is fully quit to back it up. A device reset or profile removal needs your own backup.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
        });
        var settingsCard = Pages.Card(new StackPanel
        {
            Spacing = 8, Padding = new Thickness(8),
            Children = { Heading("Keep a lasting record"), recording, recordingStatus, storage, new Expander { Header = "Retention and backup", Content = notes, HorizontalAlignment = HorizontalAlignment.Stretch } },
        });

        Content = new ScrollViewer { Content = new StackPanel { Padding = new Thickness(24), Spacing = 12, Children = { controlsCard, historyCard, settingsCard } } };

        recording.IsChecked = session.Settings.BatteryRecording;
        recording.Checked += (_, _) => ChangeRecording(true);
        recording.Unchecked += (_, _) => ChangeRecording(false);
        range.SelectionChanged += (_, _) => RequestRefresh();
        timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { session.Changed += RefreshCurrent; timer.Start(); RefreshCurrent(); await RefreshAsync(); };
        Unloaded += (_, _) => { session.Changed -= RefreshCurrent; timer.Stop(); revision++; };
    }

    private static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

    private void ChangeRecording(bool enabled)
    {
        if (syncing) return;
        session.SetBatteryRecording(enabled);
        recordingStatus.Text = session.Battery.Status;
    }

    private void RefreshCurrent()
    {
        var current = session.Stale ? BatteryReading.Unavailable : session.Current?.Battery ?? BatteryReading.Unavailable;
        percentage.Text = TableSync.Number(current.Percentage, " %");
        power.Text = BatteryPresenter.Power(current.PowerState, session.Stale);
        syncing = true;
        recording.IsChecked = session.Settings.BatteryRecording;
        syncing = false;
        recordingStatus.Text = session.Battery.Status;
    }

    private async void RequestRefresh()
    {
        revision++;
        await RefreshAsync();
    }

    /// <summary>Reads the chosen range off the UI thread; a range change while it was being read is read again, never shown stale.</summary>
    private async Task RefreshAsync()
    {
        if (!IsLoaded) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        int requested = revision;
        try
        {
            var view = await session.Battery.ReadViewAsync(Days);
            if (requested != revision || !IsLoaded) { refreshAgain = true; return; }
            chart.SetData(view);
            recordingStatus.Text = view.Status;
            summary.Text = BatteryPresenter.Summary(view);
            storage.Text = BatteryPresenter.Storage(view);
        }
        catch (Exception error)
        {
            summary.Text = "Battery history could not be read. " + error.Message;
        }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (IsLoaded) _ = RefreshAsync(); }
        }
    }

    private async Task ExportAsync()
    {
        int days = Days;
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"DeadWare-battery-{DateTime.Now:yyyyMMdd-HHmmss}" };
        picker.FileTypeChoices.Add("CSV files", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        export.IsEnabled = false;
        actionStatus.Text = "Exporting saved observations…";
        try
        {
            // The picker leaves an empty file at a new name, and the export only writes to a name that does not exist yet.
            // An existing file with content is left alone, so the export refuses it as the WPF app does.
            if (new FileInfo(file.Path) is { Exists: true, Length: 0 }) File.Delete(file.Path);
            await session.Battery.ExportCsvAsync(file.Path, days);
            actionStatus.Text = "Export saved: " + file.Path;
        }
        catch (Exception error) { actionStatus.Text = "Export could not be saved. Choose a new file name. " + error.Message; }
        finally { export.IsEnabled = true; }
    }
}
