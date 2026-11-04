using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Colour and line style for the CPU total and each core. Not placed or restored with the saved windows.</summary>
internal sealed class CpuCoreStyleWindow : Window
{
    public CpuCoreStyleWindow(UiSession session)
    {
        Title="CPU core lines — DeadWare";MinWidth=520;MinHeight=420;Width=640;Height=760;Ui.Icon(this);
        int count=Math.Clamp(session.Current?.CpuCoreCount??0,0,GraphColorSettings.MaxCores);
        var panel=new DockPanel {Margin=new Thickness(16)};
        var header=new DockPanel {Margin=new Thickness(6,0,6,8)};
        var pin=new CheckBox {Content="Always on top",VerticalAlignment=VerticalAlignment.Center,IsChecked=Topmost};
        pin.Checked+=(_,_)=>Topmost=true;pin.Unchecked+=(_,_)=>Topmost=false;DockPanel.SetDock(pin,Dock.Right);header.Children.Add(pin);
        header.Children.Add(Ui.Heading("CPU core lines",24));DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        var rows=new List<LineStyleEditorView>();var dots=new List<Ellipse>();
        var total=LineStyleEditor.Build(session,"cpu","CPU total",null);
        var totalCard=Ui.Panel(total.Root,new Thickness(16));totalCard.Margin=new Thickness(0,0,0,8);AutomationProperties.SetName(totalCard,"CPU total");
        DockPanel.SetDock(totalCard,Dock.Top);panel.Children.Add(totalCard);
        void SyncAll(){total.Sync();foreach(var row in rows)row.Sync();}
        var actions=new WrapPanel {Margin=new Thickness(0,0,0,8)};
        foreach(var (label,action) in new (string,Action)[]
        {
            ("Apply style to all cores",()=>session.ApplyStyleToAllCores(count,false)),
            ("Apply style and color to all cores",()=>session.ApplyStyleToAllCores(count,true)),
            ("Reset all cores to auto",session.ResetCpuCoreStyles),
        })
        {var button=Ui.Button(label,()=>{action();SyncAll();});button.Margin=new Thickness(0,0,8,4);actions.Children.Add(button);}
        DockPanel.SetDock(actions,Dock.Top);panel.Children.Add(actions);
        if(count==0)panel.Children.Add(Ui.Text("Per-core CPU usage unavailable.",12,"Muted"));
        else
        {
            var list=new StackPanel();
            for(int i=0;i<count;i++)
            {
                var editor=LineStyleEditor.Build(session,GraphColorSettings.CoreKey(i),$"Core {i+1}",null,"Auto tint");rows.Add(editor);
                var dot=new Ellipse {Width=12,Height=12,Fill=Theme.CoreBrush(i,count),Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center};dots.Add(dot);
                var body=new StackPanel();body.Children.Add(Ui.Row(dot,Ui.Heading($"Core {i+1}",14)));body.Children.Add(editor.Root);
                var card=Ui.Panel(body,new Thickness(16));card.Margin=new Thickness(0,0,0,8);AutomationProperties.SetName(card,$"Core {i+1}");list.Children.Add(card);
            }
            panel.Children.Add(new ScrollViewer {Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        }
        Content=panel;
        // Core brushes are frozen and replaced when the CPU colour changes; re-fetch them.
        void Refresh(){for(int i=0;i<dots.Count;i++)dots[i].Fill=Theme.CoreBrush(i,count);}
        session.Changed+=Refresh;Closed+=(_,_)=>session.Changed-=Refresh;
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
    }
}
