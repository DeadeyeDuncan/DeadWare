using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using DeadWare.Core;

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
        ToolTip=$"Change {title} color and line style";AutomationProperties.SetName(this,(string)ToolTip);
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
        menu.Children.Add(Ui.Heading("Line style",12));
        // Four inline choices keep pattern selection within this popup.
        string[] patterns=["default","solid","dashed","dotted"];
        var patternPanel=new UniformGrid {Columns=2,Margin=new(0,5,0,5)};
        var patternChoices=new[]{"Default","Solid","Dashed","Dotted"}.Select(label=>new RadioButton {Content=label,Margin=new(0,3,8,3)}).ToArray();
        foreach(var choice in patternChoices){choice.SetResourceReference(ForegroundProperty,"Ink");patternPanel.Children.Add(choice);AutomationProperties.SetName(choice,$"{choice.Content} line pattern");}
        var glow=new CheckBox {Content="Neon glow",Margin=new(0,3,0,3)};
        patternPanel.ToolTip="Line pattern for this trace across graphs, pop-outs and widget";
        glow.ToolTip="A glow around this trace";
        menu.Children.Add(patternPanel);
        var thicknessLabel=Ui.Text("",12);menu.Children.Add(thicknessLabel);
        var thickness=new Slider {Minimum=50,Maximum=300,Value=100,TickFrequency=25,SmallChange=25,LargeChange=50,IsSnapToTickEnabled=true,MinWidth=170,Margin=new(0,4,0,8)};
        AutomationProperties.SetName(thickness,"Line thickness percent");
        thickness.ToolTip="50–300% of the normal trace width; 100% keeps the original thickness";
        menu.Children.Add(thickness);menu.Children.Add(glow);
        var strengthLabel=Ui.Text("",12);menu.Children.Add(strengthLabel);
        var strength=new Slider {Minimum=0,Maximum=300,Value=100,TickFrequency=10,SmallChange=10,LargeChange=50,IsSnapToTickEnabled=true,MinWidth=170,Margin=new(0,4,0,4)};
        AutomationProperties.SetName(strength,"Neon glow strength percent");
        strength.ToolTip="0–300% glow brightness and spread; 100% keeps the original glow";
        menu.Children.Add(strength);
        bool syncing=false;
        void SyncStyle()
        {
            syncing=true;var style=session.Settings.GraphStyles.GetValueOrDefault(kind,new());
            for(int i=0;i<patterns.Length;i++)patternChoices[i].IsChecked=patterns[i]==style.Pattern;
            glow.IsChecked=style.Glow;
            thickness.Value=style.Thickness*100;strength.Value=style.GlowStrength*100;syncing=false;UpdateControls();
        }
        void UpdateControls()
        {
            thicknessLabel.Text=$"Line thickness · {thickness.Value:0}%";strengthLabel.Text=$"Glow strength · {strength.Value:0}%";
            strength.IsEnabled=glow.IsChecked==true;strengthLabel.Opacity=strength.IsEnabled?1:.5;
        }
        void SaveStyle(){UpdateControls();int selected=Array.FindIndex(patternChoices,choice=>choice.IsChecked==true);if(!syncing&&selected>=0)session.SetGraphStyle(kind,patterns[selected],glow.IsChecked==true,thickness.Value/100,strength.Value/100);}
        thickness.ValueChanged+=(_,_)=>SaveStyle();strength.ValueChanged+=(_,_)=>SaveStyle();
        foreach(var choice in patternChoices)choice.Checked+=(_,_)=>SaveStyle();
        glow.Checked+=(_,_)=>SaveStyle();glow.Unchecked+=(_,_)=>SaveStyle();SyncStyle();
        popup.Child=Ui.Panel(menu,new Thickness(10));AddLogicalChild(popup);
        Click+=(_,e)=>
        {
            // Popup children are logical descendants. Ignore their bubbling Click events.
            if(!ReferenceEquals(e.OriginalSource,this))return;
            SyncStyle();popup.IsOpen=!popup.IsOpen;if(popup.IsOpen)menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        };
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
