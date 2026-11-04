using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using DeadWare.Core;

namespace DeadWare.App;

internal sealed class GraphColorPicker : Button
{
    private readonly Popup popup;

    private GraphColorPicker(UiSession session,string kind)
    {
        Width=26;Height=26;Padding=new(0);Margin=new(3);Template=LineStyleEditor.CircleTemplate;Cursor=Cursors.Hand;
        string title=kind switch {"download"=>"Download","upload"=>"Upload",_=>Ui.Title(kind)};
        bool Cores()=>kind=="cpu"&&session.Settings.ShowCpuCoreUsage;
        void Describe(){ToolTip=Cores()?"Change CPU and core line colors and styles":$"Change {title} color and line style";AutomationProperties.SetName(this,(string)ToolTip);}
        Describe();
        var dot=new Ellipse {Width=12,Height=12};dot.SetResourceReference(Shape.FillProperty,Theme.GraphKey(kind));Content=dot;
        popup=new Popup {PlacementTarget=this,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        var editor=LineStyleEditor.Build(session,kind,title,()=>{popup.IsOpen=false;Focus();});
        var menu=editor.Root;
        var menuPanel=Ui.Panel(menu,new Thickness(10));menuPanel.SetResourceReference(Border.BackgroundProperty,"GlassPopup");popup.Child=menuPanel;AddLogicalChild(popup);
        Click+=(_,e)=>
        {
            // Popup children are logical descendants. Ignore their bubbling Click events.
            if(!ReferenceEquals(e.OriginalSource,this))return;
            if(kind=="cpu")Describe();
            if(Cores()){session.OpenCpuCoreStyles();return;}
            editor.Sync();popup.IsOpen=!popup.IsOpen;if(popup.IsOpen)menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        };
        menu.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;Focus();e.Handled=true;}};
        if(kind=="cpu"){Unloaded+=(_,_)=>session.Changed-=Describe;Loaded+=(_,_)=>{session.Changed-=Describe;session.Changed+=Describe;Describe();};}
        Unloaded+=(_,_)=>popup.IsOpen=false;
    }

    public static FrameworkElement Create(UiSession session,string kind)
    {
        var row=new StackPanel {Orientation=Orientation.Horizontal};
        foreach(string series in kind switch {"network"=>new[]{"download","upload"},"storage"=>new[]{"diskread","diskwrite"},"gputemp"=>new[]{"gputemp","gpufan"},"cputemp"=>new[]{"cputemp","cpupump"},_=>new[]{kind}})row.Children.Add(new GraphColorPicker(session,series));
        return row;
    }
}
