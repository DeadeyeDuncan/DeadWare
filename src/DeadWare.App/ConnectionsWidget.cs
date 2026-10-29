using System.Windows.Automation;
using System.Windows.Controls;
using DeadWare.Core;

namespace DeadWare.App;

internal sealed class ConnectionsWidget : Grid
{
    private readonly UiSession session;
    private readonly ConnectionMap map=new() {Flat=true,MapOnly=true,AnimationEnabled=false,IsHitTestVisible=false,Focusable=false,ToolTip=null};
    private GeoPoint? previousOrigin;

    public ConnectionsWidget(UiSession session)
    {
        this.session=session;ClipToBounds=true;
        map.ResetView();
        Children.Add(map);
        Loaded+=(_,_)=>Refresh();IsVisibleChanged+=(_,_)=>Refresh();
        Unloaded+=(_,_)=>map.AnimationEnabled=false;
        Refresh();
    }

    public void Refresh()
    {
        // This compact adapter has no dashboard filters, replay state or history dependency.
        var live=session.Connections.Live;
        string status=live.Status+(!live.Complete?" · Incomplete capture":"");
        var points=live.Connections.Where(c=>c.Location?.Point is { } point&&ConnectionProjection.IsValid(point))
            .Select(c=>new ConnectionMapPoint($"{c.Traffic.Peer.Process}:{c.Traffic.Peer.RemoteAddress}:{c.Traffic.Peer.RemotePort}:{c.Traffic.Peer.Protocol}",
                c.Location!.Point!,c.Traffic.ReceivedBytes,c.Traffic.SentBytes,false,live.Available&&c.Traffic.DownloadBytesPerSecond>0,live.Available&&c.Traffic.UploadBytesPerSecond>0)).ToArray();
        map.SetData(points,live.Origin,live.Available,status);
        if(previousOrigin!=live.Origin) {previousOrigin=live.Origin;map.ResetView();}
        map.AnimationEnabled=IsLoaded&&IsVisible&&session.Settings.ConnectionsAnimation;
        AutomationProperties.SetName(this,$"Connections live. {map.StatusSummary}");
    }
}
