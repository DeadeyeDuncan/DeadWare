using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeadWare.App;

internal sealed class GraphColorPicker : Button
{
    private readonly Popup popup;
    private static readonly ControlTemplate CircleTemplate=(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Frame" CornerRadius="13" Background="{DynamicResource Control}" BorderBrush="{DynamicResource Line}" BorderThickness="1">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private GraphColorPicker(UiSession session,string kind)
    {
        Width=26;Height=26;Padding=new(0);Margin=new(3);Template=CircleTemplate;Cursor=Cursors.Hand;
        string title=kind switch {"download"=>"Download","upload"=>"Upload",_=>Ui.Title(kind)};
        ToolTip=$"Change {title} color";AutomationProperties.SetName(this,(string)ToolTip);
        var dot=new Ellipse {Width=12,Height=12};dot.SetResourceReference(Shape.FillProperty,Theme.GraphKey(kind));Content=dot;
        popup=new Popup {PlacementTarget=this,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        var menu=new StackPanel();KeyboardNavigation.SetTabNavigation(menu,KeyboardNavigationMode.Cycle);
        menu.Children.Add(Ui.Heading(title+" color",12));
        var swatches=new UniformGrid {Columns=6,Margin=new(0,6,0,6)};
        foreach(string color in new[]{"#68E0BD","#52D6E8","#82ADFF","#B69CFF","#EF8FE0","#FF8899","#F0B76C","#F2D56B","#A9DE7A","#FFFFFF","#A6B4C6","#182A3B"})
        {
            var swatch=Ui.Button("",()=>{session.SetGraphColor(kind,color);popup.IsOpen=false;Focus();});
            swatch.Width=26;swatch.Height=26;swatch.Padding=new(0);swatch.Template=CircleTemplate;
            swatch.Content=new Ellipse {Width=16,Height=16,Fill=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))};
            swatch.ToolTip=color;AutomationProperties.SetName(swatch,$"Use {color}");swatches.Children.Add(swatch);
        }
        menu.Children.Add(swatches);
        menu.Children.Add(Ui.Button("More colors…",()=>
        {
            popup.IsOpen=false;
            var current=((SolidColorBrush)Application.Current.Resources[Theme.GraphKey(kind)]).Color;
            using var dialog=new System.Windows.Forms.ColorDialog {FullOpen=true,Color=System.Drawing.Color.FromArgb(current.R,current.G,current.B)};
            if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK)
                session.SetGraphColor(kind,$"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
            Focus();
        }));
        menu.Children.Add(Ui.Button("Theme default",()=>{session.SetGraphColor(kind,null);popup.IsOpen=false;Focus();}));
        popup.Child=Ui.Panel(menu,new Thickness(10));AddLogicalChild(popup);
        Click+=(_,_)=>{popup.IsOpen=!popup.IsOpen;if(popup.IsOpen)menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));};
        menu.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;Focus();e.Handled=true;}};
        Unloaded+=(_,_)=>popup.IsOpen=false;
    }

    public static FrameworkElement Create(UiSession session,string kind)
    {
        var row=new StackPanel {Orientation=Orientation.Horizontal};
        foreach(string series in kind=="network"?new[]{"download","upload"}:new[]{kind})row.Children.Add(new GraphColorPicker(session,series));
        return row;
    }
}
