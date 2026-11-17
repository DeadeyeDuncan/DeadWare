using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>
/// One trace's colour and line style: twelve swatches, More colours…, Default colour, the line pattern, its thickness, Neon
/// glow and the glow's strength. Settings → Appearance → Graph lines and the colour dots on Overview cards share it. It edits
/// the trace named by <see cref="Key"/>, saving each change as it is made; <see cref="Sync"/> reads that trace's saved style
/// back into the controls without saving it again.
/// </summary>
internal sealed class LineStyleEditor
{
    /// <summary>Each fixed trace's graph colour key and name, in the order Settings lists them. The colour dots are named from it too.</summary>
    public static readonly (string Key, string Label)[] Lines =
    [
        ("cpu", "CPU usage"), ("cputemp", "CPU temperature"), ("cpupump", "CPU pump speed"), ("gpu", "GPU usage"), ("gputemp", "GPU temperature"),
        ("gpufan", "GPU fan speed"), ("memory", "RAM usage"), ("download", "Download"), ("upload", "Upload"), ("diskread", "Disk read"), ("diskwrite", "Disk write"),
    ];
    private static readonly string[] Swatches = ["#68E0BD", "#52D6E8", "#82ADFF", "#B69CFF", "#EF8FE0", "#FF8899", "#F0B76C", "#F2D56B", "#A9DE7A", "#FFFFFF", "#A6B4C6", "#182A3B"];
    private static readonly string[] Patterns = ["default", "solid", "dashed", "dotted"];
    // A trace with no saved style; the record never changes, so one serves every read.
    private static readonly GraphLineStyle Plain = new();

    private readonly MonitorSession session;
    private readonly ComboBox pattern = new() { MinWidth = 140 };
    private readonly TextBlock thicknessLabel = new(), strengthLabel = new();
    private readonly Slider thickness = new() { Minimum = 50, Maximum = 300, StepFrequency = 25, SmallChange = 25, LargeChange = 50, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider strength = new() { Minimum = 0, Maximum = 300, StepFrequency = 10, SmallChange = 10, LargeChange = 50, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox glow = new() { Content = "Neon glow" };
    private ColorPicker? picker;
    private bool syncing;

    /// <param name="columns">Swatches per row; 0 keeps them in one row that scrolls sideways, as Settings shows them.</param>
    public LineStyleEditor(MonitorSession session, int columns = 0)
    {
        this.session = session;

        var swatches = new Button[Swatches.Length];
        for (int i = 0; i < Swatches.Length; i++)
        {
            string color = Swatches[i];
            var swatch = new Button { Padding = new Thickness(4), Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Parse(color)) } };
            ToolTipService.SetToolTip(swatch, color);
            AutomationProperties.SetName(swatch, $"Use {color}");
            swatch.Click += (_, _) => { if (Key is { } key) session.SetGraphColor(key, color); Picked?.Invoke(); };
            swatches[i] = swatch;
        }
        int perRow = columns > 0 ? columns : swatches.Length;
        var rows = new StackPanel { Spacing = 4 };
        for (int start = 0; start < swatches.Length; start += perRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            for (int i = start; i < Math.Min(start + perRow, swatches.Length); i++) row.Children.Add(swatches[i]);
            rows.Children.Add(row);
        }
        // Settings' single row scrolls sideways when the window is narrow.
        FrameworkElement palette = columns > 0 ? rows : new ScrollViewer
        {
            Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled,
        };

        var more = new Flyout();
        var moreContent = new StackPanel { Spacing = 8 };
        var use = new Button { Content = "Use this colour", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        use.Click += (_, _) =>
        {
            if (picker is not null && Key is { } key)
            {
                var chosen = picker.Color;
                session.SetGraphColor(key, $"#{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}");
            }
            more.Hide();
            Picked?.Invoke();
        };
        moreContent.Children.Add(use);
        more.Content = moreContent;
        more.Opening += (_, _) =>
        {
            // The picker is a large control, so it is made the first time it is opened, above Use this colour.
            if (picker is null)
            {
                picker = new ColorPicker { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false, IsMoreButtonVisible = true };
                moreContent.Children.Insert(0, picker);
            }
            if (Key is { } key && session.Settings.GraphColors.TryGetValue(key, out string? saved) && GraphColorSettings.IsColor(saved)) picker.Color = Parse(saved);
        };
        var automatic = new Button { Content = "Default colour" };
        automatic.Click += (_, _) => { if (Key is { } key) session.SetGraphColor(key, null); Picked?.Invoke(); };
        ColourControls = new StackPanel
        {
            Spacing = 8,
            Children = { palette, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new Button { Content = "More colours…", Flyout = more }, automatic } } },
        };

        foreach (string label in new[] { "Default", "Solid", "Dashed", "Dotted" }) pattern.Items.Add(label);
        AutomationProperties.SetName(pattern, "Line pattern");
        AutomationProperties.SetName(thickness, "Line thickness percent");
        AutomationProperties.SetName(strength, "Neon glow strength percent");
        ToolTipService.SetToolTip(thickness, "50–300% of the normal trace width; 100% keeps the original thickness");
        ToolTipService.SetToolTip(strength, "0–300% glow brightness and spread; 100% keeps the original glow");
        pattern.SelectionChanged += (_, _) => SaveStyle();
        thickness.ValueChanged += (_, _) => SaveStyle();
        strength.ValueChanged += (_, _) => SaveStyle();
        glow.Checked += (_, _) => SaveStyle();
        glow.Unchecked += (_, _) => SaveStyle();
        StyleControls = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = "Line pattern", VerticalAlignment = VerticalAlignment.Center }, pattern } },
                thicknessLabel, thickness, glow, strengthLabel, strength,
            },
        };
    }

    /// <summary>The trace being edited: a key from <see cref="Lines"/> or a core's key. Call <see cref="Sync"/> after changing it.</summary>
    public string? Key { get; set; }

    /// <summary>The swatches, then More colours… and Default colour.</summary>
    public StackPanel ColourControls { get; }

    /// <summary>The line pattern, thickness, Neon glow and glow strength.</summary>
    public StackPanel StyleControls { get; }

    /// <summary>Raised after a swatch, Use this colour or Default colour sets the colour, so a flyout can close itself.</summary>
    public event Action? Picked;

    /// <summary>A trace's name: "CPU usage", "Disk write", or "CPU core 3" for a core's line.</summary>
    public static string Label(string key)
    {
        if (GraphColorSettings.TryCoreIndex(key, out int core)) return $"CPU core {core + 1}";
        foreach (var (line, label) in Lines) if (line == key) return label;
        return key;
    }

    /// <summary>A colour written "#RRGGBB", as settings save them.</summary>
    public static Color Parse(string hex) =>
        Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));

    /// <summary>
    /// The buttons that copy the CPU line's style, or its style and colour, to every core, and the one that drops the cores'
    /// own colours and styles. <paramref name="cores"/> gives how many cores are listed; <paramref name="done"/> runs after each.
    /// </summary>
    public static Button[] CoreActions(MonitorSession session, Func<int> cores, Action? done = null) =>
    [
        CoreAction("Apply style to all cores", "Copy the CPU line's pattern, thickness and glow to every core; colours keep their tints", () => session.ApplyStyleToAllCores(cores(), false), done),
        CoreAction("Make all cores the CPU colour", "Copy the CPU line's style and give every core the same colour as the CPU line", () => session.ApplyStyleToAllCores(cores(), true), done),
        CoreAction("Reset all cores to auto", "Drop every core's own colour and style", session.ResetCpuCoreStyles, done),
    ];

    private static Button CoreAction(string label, string tip, Action run, Action? done)
    {
        var button = new Button { Content = label };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => { run(); done?.Invoke(); };
        return button;
    }

    /// <summary>Reads the trace's saved line style into the controls. The guard keeps their change events from saving it back.</summary>
    public void Sync()
    {
        if (Key is not { } key) return;
        bool was = syncing; syncing = true;
        try
        {
            var style = session.Settings.GraphStyles.GetValueOrDefault(key, Plain);
            pattern.SelectedIndex = Math.Max(0, Array.IndexOf(Patterns, style.Pattern));
            glow.IsChecked = style.Glow;
            thickness.Value = style.Thickness * 100;
            strength.Value = style.GlowStrength * 100;
        }
        finally { syncing = was; }
        UpdateStyleLabels();
    }

    private void UpdateStyleLabels()
    {
        thicknessLabel.Text = $"Line thickness · {thickness.Value:0}%";
        strengthLabel.Text = $"Glow strength · {strength.Value:0}%";
        strength.IsEnabled = glow.IsChecked == true;
        strengthLabel.Opacity = strength.IsEnabled ? 1 : 0.5;
    }

    private void SaveStyle()
    {
        UpdateStyleLabels();
        if (syncing || Key is not { } key || pattern.SelectedIndex < 0) return;
        session.SetGraphStyle(key, Patterns[pattern.SelectedIndex], glow.IsChecked == true, thickness.Value / 100, strength.Value / 100);
    }
}

/// <summary>
/// The flyout the colour dots on Overview cards open. The Overview page shares one among its cards: the dot that opens it
/// names the trace, and nothing in it is made until a dot is first clicked. A swatch, Use this colour or Default colour
/// closes it, as the WPF app's popup did; the pattern, thickness and glow leave it open. While per-core CPU usage is shown,
/// the CPU dot's flyout also chooses between the CPU line and each core's line and carries Settings' core actions, in place
/// of the WPF app's separate core style window.
/// </summary>
internal sealed class LineStyleFlyout
{
    private readonly MonitorSession session;
    private readonly Flyout flyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly LineStyleEditor editor;
    private readonly ComboBox lines = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock heading = Heading("", 0);
    private readonly StackPanel coreActions = new() { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
    private string[] lineKeys = [];
    private int lineCores;
    private string key = "cpu";
    private bool syncing;

    public LineStyleFlyout(MonitorSession session)
    {
        this.session = session;
        editor = new LineStyleEditor(session, columns: 6);
        editor.Picked += flyout.Hide;
        AutomationProperties.SetName(lines, "Graph line");
        lines.SelectionChanged += (_, _) =>
        {
            if (syncing || lines.SelectedIndex < 0 || lines.SelectedIndex >= lineKeys.Length) return;
            editor.Key = lineKeys[lines.SelectedIndex];
            Sync();
        };
        // A core action can change the core being edited, so the editor reads it again afterwards.
        foreach (var action in LineStyleEditor.CoreActions(session, () => lineCores, Sync)) coreActions.Children.Add(action);
        flyout.Content = new StackPanel
        {
            Spacing = 8,
            Children = { lines, heading, editor.ColourControls, Heading("Line style", 4), editor.StyleControls, coreActions },
        };
        flyout.Opening += (_, _) => Open();
    }

    /// <summary>How many core lines the CPU usage graphs draw: none unless per-core usage is on and has a reading.</summary>
    public static int CoreCount(MonitorSession session) =>
        session.Settings.ShowCpuCoreUsage ? Math.Clamp(session.Current?.CpuCoreCount ?? 0, 0, GraphColorSettings.MaxCores) : 0;

    /// <summary>Opens the flyout under a dot, for that dot's trace.</summary>
    public void ShowAt(FrameworkElement dot, string key)
    {
        this.key = key;
        flyout.ShowAt(dot);
    }

    /// <summary>Sets the flyout up for the dot that opens it and reads that trace's saved style.</summary>
    private void Open()
    {
        // The CPU dot reaches the core lines too while they are drawn. It starts on the CPU line each time, so a quick
        // swatch pick never recolours a core picked on an earlier visit.
        int cores = key == "cpu" ? CoreCount(session) : 0;
        if (cores > 0)
        {
            syncing = true;
            try
            {
                if (cores != lineCores)
                {
                    lineCores = cores;
                    lineKeys = ["cpu", .. Enumerable.Range(0, cores).Select(GraphColorSettings.CoreKey)];
                    lines.Items.Clear();
                    foreach (string line in lineKeys) lines.Items.Add(LineStyleEditor.Label(line));
                }
                lines.SelectedIndex = 0;
            }
            finally { syncing = false; }
        }
        lines.Visibility = coreActions.Visibility = cores > 0 ? Visibility.Visible : Visibility.Collapsed;
        editor.Key = key;
        Sync();
    }

    private void Sync()
    {
        heading.Text = $"{LineStyleEditor.Label(editor.Key ?? key)} colour";
        editor.Sync();
    }

    private static TextBlock Heading(string text, double top) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, top, 0, 0) };
}

/// <summary>
/// A round button in an Overview card's header holding a 12 px dot in one trace's colour. It opens that trace's colour and
/// line style; the CPU dot reaches the core lines too while per-core usage is shown.
/// </summary>
internal sealed class LineStyleDot
{
    private readonly MonitorSession session;
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse dot = new() { Width = 12, Height = 12 };
    private Color shown;
    private bool coloured, visible = true, cores;

    /// <param name="flyout">Gives the flyout to open; it is asked for on the first click, so nothing is made before then.</param>
    public LineStyleDot(MonitorSession session, string key, Func<LineStyleFlyout> flyout)
    {
        this.session = session;
        Key = key;
        Button = new Button
        {
            Width = 26, Height = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Content = dot,
        };
        Describe();
        Button.Click += (_, _) => flyout().ShowAt(Button, Key);
    }

    /// <summary>The trace's graph colour key.</summary>
    public string Key { get; }

    /// <summary>The round button, for the card's header.</summary>
    public Button Button { get; }

    /// <summary>The traces whose dots a card of this kind carries, in header order.</summary>
    public static string[] Keys(string kind) => kind switch
    {
        "cputemp" => ["cputemp", "cpupump"], "gputemp" => ["gputemp", "gpufan"], "network" => ["download", "upload"], "storage" => ["diskread", "diskwrite"],
        _ => [kind],
    };

    /// <summary>
    /// Shows the pump and fan dots only while their lines are drawn, recolours the dot only when its trace's colour changed and
    /// words the CPU dot for the core lines. <paramref name="known"/> is the trace's colour when the card has already looked it up.
    /// </summary>
    public void Refresh(HistoryChartView chart, Color? known = null)
    {
        bool show = Key switch { "cpupump" => session.Settings.ShowCpuPumpSpeed, "gpufan" => session.Settings.ShowGpuFanSpeed, _ => true };
        if (show != visible)
        {
            visible = show;
            Button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!show) return;
        var color = known ?? chart.ColorOf(Key);
        if (!coloured || !shown.Equals(color))
        {
            (coloured, shown) = (true, color);
            dot.Fill = new SolidColorBrush(color);
        }
        if (Key == "cpu" && (LineStyleFlyout.CoreCount(session) > 0) != cores)
        {
            cores = !cores;
            Describe();
        }
    }

    private void Describe()
    {
        string text = cores ? "Change CPU and core line colours and styles" : $"Change {LineStyleEditor.Label(Key)} colour and line style";
        ToolTipService.SetToolTip(Button, text);
        AutomationProperties.SetName(Button, text);
    }
}
