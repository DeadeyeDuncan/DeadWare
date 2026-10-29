using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DeadWare.Core;

namespace DeadWare.App;

internal sealed class ConnectionsWidget : Grid
{
    private readonly UiSession session;
    private readonly ConnectionMap map=new() {Flat=true,AnimationEnabled=false,IsHitTestVisible=false,Focusable=false};
    private readonly TextBlock heading=Ui.Text("Connections · Live",12);
    private readonly TextBlock rates=Ui.Text("Waiting for endpoint capture",10,"Muted");
    private GeoPoint? previousOrigin;

    public ConnectionsWidget(UiSession session)
    {
        this.session=session;Height=150;MinHeight=150;ClipToBounds=true;
        map.ResetView();
        RowDefinitions.Add(new() {Height=new(20)});
        RowDefinitions.Add(new() {Height=new(1,GridUnitType.Star)});
        RowDefinitions.Add(new() {Height=new(16)});
        heading.Margin=new(6,0,6,0);rates.Margin=new(6,0,6,0);
        rates.TextTrimming=TextTrimming.CharacterEllipsis;
        Children.Add(heading);Grid.SetRow(map,1);Children.Add(map);Grid.SetRow(rates,2);Children.Add(rates);
        Loaded+=(_,_)=>Refresh();IsVisibleChanged+=(_,_)=>Refresh();
        Unloaded+=(_,_)=>map.AnimationEnabled=false;
        Refresh();
    }

    public void Refresh()
    {
        // This compact adapter has no dashboard filters, replay state or history dependency.
        var live=session.Connections.Live;
        double download=0,upload=0;
        foreach(var connection in live.Connections)
        {
            download=AddRate(download,connection.Traffic.DownloadBytesPerSecond);
            upload=AddRate(upload,connection.Traffic.UploadBytesPerSecond);
        }
        rates.Text=$"↓ {Ui.Bytes(live.Available?download:null,true)}   ↑ {Ui.Bytes(live.Available?upload:null,true)} · observed now";
        string status=live.Status+(!live.Complete?" · Incomplete capture":"");
        var points=live.Connections.Where(c=>c.Location?.Point is { } point&&ConnectionProjection.IsValid(point))
            .Select(c=>new ConnectionMapPoint($"{c.Traffic.Peer.Process}:{c.Traffic.Peer.RemoteAddress}:{c.Traffic.Peer.RemotePort}:{c.Traffic.Peer.Protocol}",
                c.Location!.Point!,c.Traffic.ReceivedBytes,c.Traffic.SentBytes,false)).ToArray();
        map.SetData(points,live.Origin,live.Available,status);
        if(previousOrigin!=live.Origin) {previousOrigin=live.Origin;map.ResetView();}
        map.AnimationEnabled=IsLoaded&&IsVisible&&session.Settings.ConnectionsAnimation;
        ToolTip=$"Connections · Live · {live.Timestamp.ToLocalTime():T}\n{rates.Text}\n{map.StatusSummary}";
        AutomationProperties.SetName(this,$"Connections live. {rates.Text}. {map.StatusSummary}");
    }

    private static double AddRate(double sum,double rate)=>double.IsFinite(rate)&&rate>0?Math.Min(double.MaxValue,sum+rate):sum;
}
