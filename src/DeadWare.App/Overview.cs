using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Markup;

namespace DeadWare.App;

/// <summary>The dashboard layout is shared by the live window and visual preview.</summary>
internal sealed class OverviewPage : ScrollViewer
{
    public OverviewPage(UiSession session,TextBlock memory)
    {
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
        var stack=new StackPanel();
        var cards=new UniformGrid {Columns=2,Rows=3,Height=810};
        foreach(string kind in new[]{"cpu","gpu","cputemp","gputemp","memory","network"})
            cards.Children.Add(new ChartCard(session,kind,true,true));
        stack.Children.Add(cards);
        memory.Margin=new(9,10,9,4);stack.Children.Add(memory);
        var note=Ui.Text("— indicates an unavailable reading.",11,"Muted");
        note.Margin=new(9,3,9,12);stack.Children.Add(note);Content=stack;
    }
}

internal static class OverviewVisual
{
    private static readonly ControlTemplate IconButtonTemplate=(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Frame" Background="{DynamicResource Panel}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="7">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource Control}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource Control}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    public static Button PopOut(UiSession session,string kind)
    {
        var button=Ui.Button("",()=>session.OpenDetached(kind));
        button.Width=28;button.Height=28;button.Padding=new(0);button.Margin=new(5,0,0,0);button.Template=IconButtonTemplate;
        button.Content=Glyph("M 3,8 L 3,14 14,14 14,10 M 8,3 L 16,3 16,11 M 16,3 L 8,11",14,"Muted");
        button.ToolTip="Pop out "+Ui.Title(kind);
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
            _=>"M 6,2 L 6,16 M 2,12 L 6,16 10,12 M 14,18 L 14,4 M 10,8 L 14,4 18,8"
        };
        return Glyph(geometry,18,Theme.GraphKey(kind));
    }

    private static FrameworkElement Glyph(string data,double size,string color)
    {
        var path=new System.Windows.Shapes.Path {Data=Geometry.Parse(data),StrokeThickness=1.35,Stretch=Stretch.Uniform,Width=size,Height=size,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,color);return path;
    }
}
