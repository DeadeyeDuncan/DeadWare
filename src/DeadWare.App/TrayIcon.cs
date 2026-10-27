using System.Windows;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
using DrawingIcon=System.Drawing.Icon;

namespace DeadWare.App;

internal sealed class TrayIcon : IDisposable
{
    private Forms.NotifyIcon? notify;
    private Forms.ContextMenuStrip? menu;
    private DrawingIcon? artwork;
    private bool disposed;
    private readonly Action? reviewUpdate;
    private Action? balloonClick;

    public TrayIcon(Dispatcher dispatcher,Action showDashboard,Action quit,Action? showUpdates=null)
    {
        reviewUpdate=showUpdates;
        try
        {
            using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/deadware.ico"))?.Stream??throw new InvalidOperationException("The tray icon resource is unavailable.");
            using var original=new DrawingIcon(stream);artwork=(DrawingIcon)original.Clone();
            menu=new Forms.ContextMenuStrip();
            menu.Items.Add("Show dashboard",null,(_,_)=>Dispatch(showDashboard));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit DeadWare",null,(_,_)=>Dispatch(quit));
            notify=new Forms.NotifyIcon {Icon=artwork,Text="DeadWare",ContextMenuStrip=menu};
            notify.DoubleClick+=(_,_)=>Dispatch(showDashboard);
            notify.BalloonTipClicked+=(_,_)=>{var action=balloonClick;if(action is not null)Dispatch(action);};
            notify.Visible=true;
        }
        catch {Dispose();throw;}

        void Dispatch(Action action)
        {
            if(disposed||dispatcher.HasShutdownStarted)return;
            dispatcher.BeginInvoke(()=>{if(!disposed&&!dispatcher.HasShutdownStarted)action();});
        }
    }

    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try
        {
            if(notify is not null)notify.Visible=false;
        }
        finally
        {
            try {notify?.Dispose();}
            finally {try {menu?.Dispose();}finally {artwork?.Dispose();}}
        }
    }

    public void ShowCoolingWarning(string message)
    {
        if(disposed||notify is null)return;
        balloonClick=null;
        try {notify.ShowBalloonTip(10000,"DeadWare · Cooling change",message.Length>250?message[..247]+"…":message,Forms.ToolTipIcon.Warning);}
        catch(Exception) { /* The warning remains available in the persistent in-app log. */ }
    }
    public void ShowUpdateAvailable(string version)
    {
        if(disposed||notify is null)return;
        balloonClick=reviewUpdate;
        try {notify.ShowBalloonTip(10000,"DeadWare update available",$"Version {version} is available. Open Updates to review it and install when you are ready.",Forms.ToolTipIcon.Info);}
        catch(Exception) { /* The dashboard retains the update notice. */ }
    }
}
