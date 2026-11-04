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
    private readonly UiSession session;
    private readonly DockPanel panel;
    private readonly LineStyleEditorView total;
    private readonly List<LineStyleEditorView> rows=new();
    private readonly List<Ellipse> dots=new();
    private UIElement? list;
    private int count=-1;

    public CpuCoreStyleWindow(UiSession session)
    {
        this.session=session;
        Title="CPU core lines — DeadWare";MinWidth=520;MinHeight=420;Width=640;Height=760;Ui.Icon(this);
        panel=new DockPanel {Margin=new Thickness(16)};
        var header=new DockPanel {Margin=new Thickness(6,0,6,8)};
        var pin=new CheckBox {Content="Always on top",VerticalAlignment=VerticalAlignment.Center,IsChecked=Topmost};
        pin.Checked+=(_,_)=>Topmost=true;pin.Unchecked+=(_,_)=>Topmost=false;DockPanel.SetDock(pin,Dock.Right);header.Children.Add(pin);
        header.Children.Add(Ui.Heading("CPU core lines",24));DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        total=LineStyleEditor.Build(session,"cpu","CPU total",null);
        var totalCard=Ui.Panel(total.Root,new Thickness(16));totalCard.Margin=new Thickness(0,0,0,8);AutomationProperties.SetName(totalCard,"CPU total");
        DockPanel.SetDock(totalCard,Dock.Top);panel.Children.Add(totalCard);
        var actions=new WrapPanel {Margin=new Thickness(0,0,0,8)};
        foreach(var (label,tip,action) in new (string,string,Action)[]
        {
            ("Apply style to all cores","Copy the CPU line's pattern, thickness and glow to every core; colours keep their tints",()=>session.ApplyStyleToAllCores(count,false)),
            ("Make all cores the CPU color","Copy the CPU line's style and give every core the same colour as the CPU line",()=>session.ApplyStyleToAllCores(count,true)),
            ("Reset all cores to auto","Drop every core's own colour and style",session.ResetCpuCoreStyles),
        })
        {var button=Ui.Button(label,()=>{action();Refresh();});button.ToolTip=tip;button.Margin=new Thickness(0,0,8,4);actions.Children.Add(button);}
        DockPanel.SetDock(actions,Dock.Top);panel.Children.Add(actions);
        Rebuild();Content=panel;
        session.Changed+=Refresh;Closed+=(_,_)=>session.Changed-=Refresh;
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
    }

    /// <summary>One card per live core; rebuilt when the core count changes (first sample, source fallback).</summary>
    private void Rebuild()
    {
        int now=Math.Clamp(session.Current?.CpuCoreCount??0,0,GraphColorSettings.MaxCores);
        if(now==count)return;
        count=now;if(list is not null)panel.Children.Remove(list);rows.Clear();dots.Clear();
        if(count==0)list=Ui.Text(session.Current is null?"Waiting for the first CPU reading…":"Per-core CPU usage unavailable.",12,"Muted");
        else
        {
            var cards=new StackPanel();
            for(int i=0;i<count;i++)
            {
                var editor=LineStyleEditor.Build(session,GraphColorSettings.CoreKey(i),$"Core {i+1}",null,"Auto tint");rows.Add(editor);
                var dot=new Ellipse {Width=12,Height=12,Fill=Theme.CoreBrush(i,count),Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center};dots.Add(dot);
                var body=new StackPanel();body.Children.Add(Ui.Row(dot,Ui.Heading($"Core {i+1}",14)));body.Children.Add(editor.Root);
                var card=Ui.Panel(body,new Thickness(16));card.Margin=new Thickness(0,0,0,8);AutomationProperties.SetName(card,$"Core {i+1}");cards.Children.Add(card);
            }
            list=new ScrollViewer {Content=cards,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        }
        panel.Children.Add(list);
    }

    /// <summary>Core brushes are frozen and replaced when colours change, and the editors re-read their styles after any outside edit.</summary>
    private void Refresh()
    {
        Rebuild();
        for(int i=0;i<dots.Count;i++)dots[i].Fill=Theme.CoreBrush(i,count);
        total.Sync();foreach(var row in rows)row.Sync();
    }
}
