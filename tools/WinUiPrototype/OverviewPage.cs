using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinUiPrototype;

/// <summary>Six chart cards in two columns, sampled once a second while the page is loaded.</summary>
internal sealed class OverviewPage : Page
{
    /// <summary>Canvas draws since start-up, across all cards; read by the workflow's frames check.</summary>
    public static long FramesDrawn;

    /// <summary>One-second sample ticks since start-up; fewer than expected means the UI thread was starved.</summary>
    public static long Ticks;

    private static readonly (string Title, string Unit)[] Metrics =
        [("CPU", "%"), ("GPU", "%"), ("Memory", "%"), ("Disk", "%"), ("Download", "Mb/s"), ("Upload", "Mb/s")];

    private readonly List<ChartCard> cards = [];
    private readonly DispatcherQueueTimer timer;
    private bool drawing = true;

    public OverviewPage()
    {
        var grid = new Grid { Padding = new Thickness(24), RowSpacing = 12, ColumnSpacing = 12 };
        for (int column = 0; column < 2; column++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < 3; row++) grid.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < Metrics.Length; i++)
        {
            var card = new ChartCard(Metrics[i].Title, Metrics[i].Unit, seed: i + 1);
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            grid.Children.Add(card);
            cards.Add(card);
        }
        Content = grid;

        timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => { Interlocked.Increment(ref Ticks); foreach (var card in cards) card.Tick(); };
        Loaded += (_, _) =>
        {
            timer.Start();
            if (App.EveryFrame) CompositionTarget.Rendering += OnRendering;
        };
        Unloaded += (_, _) =>
        {
            timer.Stop();
            CompositionTarget.Rendering -= OnRendering;
        };
    }

    /// <summary>With <c>--every-frame</c>: redraw every card each frame, unless the window is hidden.</summary>
    private void OnRendering(object? sender, object args)
    {
        if (!drawing) return;
        foreach (var card in cards) card.Animate();
    }

    /// <summary>Turns chart redraws off while the window is hidden; sampling carries on either way.</summary>
    public void SetDrawing(bool enabled)
    {
        drawing = enabled;
        foreach (var card in cards)
        {
            card.Drawing = enabled;
            if (enabled) card.Refresh();
        }
    }
}
