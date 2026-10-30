using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DeadWare.Core;

namespace DeadWare.App;

internal sealed class ConnectionsWidget : Grid
{
    private readonly UiSession session;
    private readonly ConnectionMap map=new() {Flat=true,MapOnly=true,AnimationEnabled=false,ToolTip="Drag to move · wheel or +/− to zoom · Home to reset this region"};
    private readonly Button region=new() {Content="NA",Width=32,Height=24,Padding=new(4,2,4,2),Margin=new(6),FontSize=10,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};
    private bool? previousNorthAmerica;

    public ConnectionsWidget(UiSession session)
    {
        this.session=session;ClipToBounds=true;
        map.ResetView();
        Children.Add(map);
        Children.Add(region);
        map.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Home){ResetRegion();e.Handled=true;}};
        region.Click+=(_,_)=>{session.SetWidgetConnectionsNorthAmerica(!session.Settings.WidgetConnectionsNorthAmerica);Refresh();};
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
        bool northAmerica=session.Settings.WidgetConnectionsNorthAmerica;
        if(previousNorthAmerica!=northAmerica)
        {
            previousNorthAmerica=northAmerica;
            ResetRegion();
            region.SetResourceReference(Button.BorderBrushProperty,northAmerica?"Accent":"Line");
            region.ToolTip=northAmerica?"Return to World view":"Zoom to North America";
            AutomationProperties.SetName(region,northAmerica?"North America view. Switch to World view.":"World view. Zoom to North America.");
            AutomationProperties.SetItemStatus(region,northAmerica?"North America":"World");
        }
        map.AnimationEnabled=IsLoaded&&IsVisible&&session.Settings.ConnectionsAnimation;
        AutomationProperties.SetName(this,$"Connections live. {map.StatusSummary}");
    }

    private void ResetRegion()
    {
        if(session.Settings.WidgetConnectionsNorthAmerica)map.SetView(new(43,-103),2.5);else map.ResetView();
    }
}
