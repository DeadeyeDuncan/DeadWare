using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>The shared graph history range as a segmented pill. One setting, every window follows it.</summary>
internal sealed class RangeSegments : Border
{
    internal static readonly IReadOnlyList<(int Seconds,string Label)> Choices=
        HistoryRanges.Seconds.Select(seconds=>(seconds,HistoryRanges.Label(seconds))).ToArray();

    // Sealed so the shared template is free-threaded: whichever thread first touches this class owns an unsealed one.
    private static readonly ControlTemplate SegmentTemplate=Sealed((ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
          <Border x:Name="Frame" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="7" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource GlassHover}"/></Trigger>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource AccentSoft}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/><Setter Property="Foreground" Value="{DynamicResource Ink}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));

    // Defaults live in a style so the template's checked trigger outranks them (a local value would win).
    private static readonly Style SegmentStyle=Sealed(new Style(typeof(ToggleButton))
    {
        Setters={new Setter(Control.TemplateProperty,SegmentTemplate),new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Muted")),new Setter(Control.FontFamilyProperty,new DynamicResourceExtension("BodyFont"))}
    });

    private static ControlTemplate Sealed(ControlTemplate template){template.Seal();return template;}
    private static Style Sealed(Style style){style.Seal();return style;}

    private readonly UiSession session;
    private readonly List<ToggleButton> segments=[];
    private bool syncing;

    public RangeSegments(UiSession session)
    {
        this.session=session;
        CornerRadius=new(9);BorderThickness=new(1);Padding=new(2);VerticalAlignment=VerticalAlignment.Center;HorizontalAlignment=HorizontalAlignment.Left;

        SetResourceReference(BackgroundProperty,"GlassControl");SetResourceReference(BorderBrushProperty,"Hairline");
        ToolTip="Choose how much graph history to show";
        AutomationProperties.SetName(this,"Graph history range");
        var row=new StackPanel {Orientation=Orientation.Horizontal};Child=row;
        foreach(var (seconds,label) in Choices)
        {
            var segment=new ToggleButton {Content=label,Tag=seconds,Height=26,Padding=new(10,0,10,0),Margin=new(1,0,1,0),FontSize=12,Style=SegmentStyle,Cursor=Cursors.Hand};
            AutomationProperties.SetName(segment,label+" history");
            segment.Checked+=(_,_)=>Choose(seconds);
            // A segment never unchecks itself; the next choice replaces it.
            segment.Unchecked+=(_,_)=>{if(!syncing&&session.HistorySeconds==seconds)segment.IsChecked=true;};
            segment.PreviewKeyDown+=(_,e)=>
            {
                int index=segments.IndexOf(segment);
                if(e.Key==Key.Left&&index>0){segments[index-1].IsChecked=true;segments[index-1].Focus();e.Handled=true;}
                else if(e.Key==Key.Right&&index<segments.Count-1){segments[index+1].IsChecked=true;segments[index+1].Focus();e.Handled=true;}
            };
            segments.Add(segment);row.Children.Add(segment);
        }
        Refresh();
        Loaded+=(_,_)=>{session.Changed+=Refresh;Refresh();};
        Unloaded+=(_,_)=>session.Changed-=Refresh;
    }

    /// <summary>Puts focus on the checked segment (the pop-out menu does this on open) so arrow keys and Escape work at once.</summary>
    internal bool FocusChecked()=>segments.Count>0&&(segments.FirstOrDefault(s=>s.IsChecked==true)??segments[0]).Focus();

    private void Choose(int seconds)
    {
        if(syncing)return;
        syncing=true;
        try {foreach(var other in segments)if((int)other.Tag!=seconds)other.IsChecked=false;}
        finally {syncing=false;}
        if(seconds!=session.HistorySeconds)session.SetRange(seconds);
    }

    private void Refresh()
    {
        syncing=true;
        try {foreach(var segment in segments)segment.IsChecked=(int)segment.Tag==session.HistorySeconds;}
        finally {syncing=false;}
    }
}
