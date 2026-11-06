using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Markup;

namespace DeadWare.App;

/// <summary>The dashboard layout is shared by the live window and visual preview. Seven fixed cards are followed by one
/// Disk card per present drive (by disk index), added and removed as drives come and go.</summary>
internal sealed class OverviewPage : ScrollViewer
{
    internal const double RowHeight=270;
    internal const double MinimumGridHeight=4*RowHeight;
    private readonly UiSession session;
    private readonly UniformGrid cards=new() {Columns=2,Rows=4,Height=MinimumGridHeight};
    private readonly TextBlock memory,note;
    private readonly List<ChartCard> driveCards=[];
    private string[] driveIds=[];
    /// <summary>Two columns: the row count that holds <paramref name="count"/> cards.</summary>
    internal static int Rows(int count)=>Math.Max(1,(count+1)/2);
    public OverviewPage(UiSession session,TextBlock memory)
    {
        this.session=session;this.memory=memory;
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
        var stack=new StackPanel();
        foreach(string kind in new[]{"cpu","gpu","cputemp","gputemp","memory","network","storage"})
            cards.Children.Add(new ChartCard(session,kind,true,true));
        stack.Children.Add(cards);
        memory.Margin=new(9,10,9,4);stack.Children.Add(memory);
        note=Ui.Text("— indicates an unavailable reading.",11,"Muted");
        note.Margin=new(9,3,9,12);stack.Children.Add(note);Content=stack;
        // Charts take the star row of each card, so growing the grid into spare viewport height enlarges
        // the plots; the floor (270 px per card row, the old 3-row 810 px) makes short windows scroll instead of crushing the plots.
        ScrollChanged+=(_,e)=>{if(e.ViewportHeightChange!=0||e.ExtentHeightChange!=0)Fit();};
        Loaded+=(_,_)=>{session.Changed+=OnChanged;SyncDrives();};
        Unloaded+=(_,_)=>session.Changed-=OnChanged;
        SyncDrives();
    }
    private void OnChanged()
    {
        if(Dispatcher.HasShutdownStarted)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.InvokeAsync(OnChanged);return;}
        SyncDrives();
    }
    /// <summary>Sizes the grid to its rows, growing into spare viewport height.</summary>
    private void Fit()
    {
        int rows=Rows(cards.Children.Count);
        if(cards.Rows!=rows)cards.Rows=rows;
        double footer=memory.ActualHeight+memory.Margin.Top+memory.Margin.Bottom+note.ActualHeight+note.Margin.Top+note.Margin.Bottom;
        double height=Math.Max(rows*RowHeight,Math.Floor(ViewportHeight-footer));
        if(Math.Abs(cards.Height-height)>=1)cards.Height=height;
    }
    /// <summary>One card per present drive after the fixed cards; the set is rebuilt only when the drive ids change.</summary>
    private void SyncDrives()
    {
        var drives=(session.Current?.Storage.Drives??[]).OrderBy(d=>d.Index).ToList();
        var ids=drives.Select(d=>d.Id).ToArray();
        if(ids.SequenceEqual(driveIds,StringComparer.Ordinal))return;
        driveIds=ids;
        foreach(var card in driveCards)cards.Children.Remove(card);
        driveCards.Clear();
        foreach(var drive in drives){var card=new ChartCard(session,"storage",true,true,drive.Id);driveCards.Add(card);cards.Children.Add(card);}
        Fit();
    }
}

internal static class OverviewVisual
{
    private static readonly ControlTemplate IconButtonTemplate=Sealed((ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Frame" Background="{DynamicResource GlassControl}" BorderBrush="{DynamicResource GlassBorder}" BorderThickness="1" CornerRadius="7">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource GlassHover}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource GlassHover}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));

    private static ControlTemplate Sealed(ControlTemplate template){template.Seal();return template;}

    /// <summary>The header pop-out button; <paramref name="open"/> and <paramref name="label"/> replace the kind's window and tooltip (a drive card).</summary>
    public static Button PopOut(UiSession session,string kind,Action? open=null,string? label=null)
    {
        var button=Ui.Button("",open??(()=>session.OpenDetached(kind)));
        button.Width=26;button.Height=26;button.Padding=new(0);button.Margin=new(5,0,0,0);button.Template=IconButtonTemplate;
        button.Content=Glyph("M 3,8 L 3,14 14,14 14,10 M 8,3 L 16,3 16,11 M 16,3 L 8,11",14,"Muted");
        button.ToolTip=label??"Pop out "+Ui.Title(kind);
        System.Windows.Automation.AutomationProperties.SetName(button,(string)button.ToolTip);
        return button;
    }

    public static FrameworkElement MetricGlyph(string kind)
    {
        string geometry=kind switch
        {
            "cpu" or "gpu"=>"M 5,5 L 15,5 15,15 5,15 Z M 8,8 L 12,8 12,12 8,12 Z M 7,2 L 7,5 M 13,2 L 13,5 M 7,15 L 7,18 M 13,15 L 13,18 M 2,7 L 5,7 M 2,13 L 5,13 M 15,7 L 18,7 M 15,13 L 18,13",
            "cputemp" or "gputemp"=>"M 8,11 L 8,4 C 8,1 12,1 12,4 L 12,11 C 17,15 11,21 7,17 C 5,15 6,12 8,11 M 10,7 L 10,14 M 15,5 L 18,5 M 15,9 L 17,9",
            "memory"=>"M 2,5 L 18,5 18,15 2,15 Z M 5,8 L 8,8 8,12 5,12 Z M 12,8 L 15,8 15,12 12,12 Z M 5,15 L 5,18 M 10,15 L 10,18 M 15,15 L 15,18",
            "storage"=>"M 2,4 L 18,4 18,16 2,16 Z M 2,12 L 18,12 M 14,14 L 15,14 M 5,14 L 9,14",
            _=>"M 6,2 L 6,16 M 2,12 L 6,16 10,12 M 14,18 L 14,4 M 10,8 L 14,4 18,8"
        };
        return Glyph(geometry,18,Theme.GraphKey(kind));
    }

    /// <summary>A 16 px stroked glyph for a navigation route. Its stroke follows the hosting button's foreground.</summary>
    public static FrameworkElement NavGlyph(string route)
    {
        string geometry=route switch
        {
            "overview"=>"M 2,2 L 9,2 9,9 2,9 Z M 11,2 L 18,2 18,9 11,9 Z M 2,11 L 9,11 9,18 2,18 Z M 11,11 L 18,11 18,18 11,18 Z",
            "network"=>"M 2,6 L 15,6 M 12,3 L 15,6 12,9 M 18,14 L 5,14 M 8,11 L 5,14 8,17",
            "storage"=>"M 3,4 L 17,4 17,16 3,16 Z M 3,11 L 17,11 M 13.5,13.5 L 14.5,13.5 M 5,13.5 L 10,13.5",
            "processes"=>"M 2,5 L 8,5 M 2,10 L 8,10 M 2,15 L 8,15 M 10,10 L 12,10 L 13.5,5 L 15.5,15 L 17,10 L 18,10",
            "connections"=>"M 10,2 A 8,8 0 1,0 10,18 A 8,8 0 1,0 10,2 M 2,10 L 18,10 M 10,2 C 6,6 6,14 10,18 M 10,2 C 14,6 14,14 10,18",
            "battery"=>"M 2,6 L 16,6 16,14 2,14 Z M 16,8 L 18,8 18,12 16,12 M 4,8 L 11,8 11,12 4,12 Z",
            "usage"=>"M 3,17 L 3,10 M 8,17 L 8,5 M 13,17 L 13,8 M 18,17 L 18,3",
            "alerts"=>"M 10,2 L 18,17 2,17 Z M 10,8 L 10,12 M 10,14.4 L 10,14.6",
            "sensors"=>"M 3,5 L 17,5 M 3,10 L 17,10 M 3,15 L 17,15",
            "cooling"=>"M 10,10 C 7,3 13,3 10,10 M 10,10 C 17,7 16,14 10,10 M 10,10 C 4,13 6,18 10,10 M 10,8.5 A 1.5,1.5 0 1,0 10,11.5 A 1.5,1.5 0 1,0 10,8.5",
            "desktop"=>"M 10,7 A 3,3 0 1,0 10,13 A 3,3 0 1,0 10,7 M 10,2 L 10,4.5 M 10,15.5 L 10,18 M 2,10 L 4.5,10 M 15.5,10 L 18,10 M 4.3,4.3 L 6.1,6.1 M 13.9,13.9 L 15.7,15.7 M 4.3,15.7 L 6.1,13.9 M 13.9,6.1 L 15.7,4.3",
            _=>"M 10,2 L 10,14 M 5,9 L 10,14 15,9 M 3,18 L 17,18"
        };
        var path=(System.Windows.Shapes.Path)Glyph(geometry,16,"Muted");
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground") {RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(Button),1)});
        return path;
    }

    /// <summary>A 24 px rounded tile tinted with the metric's graph color (14 %) holding its glyph. The tint is a resource reference so theme changes follow.</summary>
    public static FrameworkElement IconTile(string kind)
    {
        var tint=new Border {CornerRadius=new CornerRadius(7),Opacity=.14};tint.SetResourceReference(Border.BackgroundProperty,Theme.GraphKey(kind));
        var glyph=MetricGlyph(kind);glyph.Width=15;glyph.Height=15;glyph.HorizontalAlignment=HorizontalAlignment.Center;glyph.VerticalAlignment=VerticalAlignment.Center;
        var layers=new Grid();layers.Children.Add(tint);layers.Children.Add(glyph);
        return new Border {Width=24,Height=24,Child=layers};
    }

    private static FrameworkElement Glyph(string data,double size,string color)
    {
        var path=new System.Windows.Shapes.Path {Data=Geometry.Parse(data),StrokeThickness=1.35,Stretch=Stretch.Uniform,Width=size,Height=size,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,color);return path;
    }
}
