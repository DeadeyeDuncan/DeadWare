using System.Net.NetworkInformation;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>One adapter that counts toward the interface totals, and how to read its byte counters. A read throws
/// <see cref="NetworkInformationException"/> once the adapter is gone.</summary>
internal sealed record NetworkAdapter(string Id,Func<(long Received,long Sent)> Read);

/// <summary>
/// The interface totals. Listing the adapters is the costly part: .NET rebuilds every interface's addresses, gateways and DNS
/// servers, and Windows lists filter layers, Hyper-V, WSL and VPN interfaces too (46 interfaces for 2 counted adapters on the
/// machine this was measured on, about 28 ms a poll). So the list is kept, and listed again only when Windows reports an address
/// change, when an adapter's counters fail, or every <see cref="RefreshSeconds"/>; each poll reads only the counters, which come
/// fresh from Windows on every read. One caller at a time.
/// </summary>
internal sealed class NetworkTotals : IDisposable
{
    internal const double RefreshSeconds=30;
    private readonly Func<IReadOnlyList<NetworkAdapter>> list;
    private readonly bool watch;
    private readonly Dictionary<string,(CounterRate Down,CounterRate Up)> rates=new(StringComparer.Ordinal);
    private IReadOnlyList<NetworkAdapter>? adapters;
    private double listedAt;
    // watching: 0 not subscribed, 1 subscribed, 2 disposed (never subscribes again).
    private int changed,watching;

    public NetworkTotals():this(ListAdapters,watch:true) {}
    internal NetworkTotals(Func<IReadOnlyList<NetworkAdapter>> list,bool watch=false) {this.list=list;this.watch=watch;}

    /// <summary>Windows also lists each adapter's filter layers (WFP, QoS Packet Scheduler) as interfaces of their own, repeating the
    /// adapter's byte counters; summing them counted the same traffic several times. Only an adapter itself holds an address.</summary>
    internal static bool Counted(OperationalStatus status,NetworkInterfaceType type,int unicastAddresses)=>
        status==OperationalStatus.Up&&type!=NetworkInterfaceType.Loopback&&unicastAddresses>0;

    /// <summary>The adapter list may have changed: the next read lists the adapters again. Safe from any thread.</summary>
    internal void MarkChanged()=>Interlocked.Exchange(ref changed,1);

    public (double? Down,double? Up,string Status) Read(double now)
    {
        double down=0,up=0;bool valid=true;int count=0;
        try
        {
            // Cleared before listing, so a change Windows reports while the list is being built lists again on the next poll.
            if(adapters is null||Interlocked.Exchange(ref changed,0)!=0||now-listedAt>=RefreshSeconds||now<listedAt)
            {
                Watch();
                adapters=list();listedAt=now;
                // An adapter that left the list starts from a new baseline if it comes back.
                foreach(var id in rates.Keys.Except(adapters.Select(a=>a.Id),StringComparer.Ordinal).ToArray())rates.Remove(id);
            }
            foreach(var adapter in adapters)
            {
                count++;
                if(!rates.TryGetValue(adapter.Id,out var rate))rates[adapter.Id]=rate=(new(),new());
                try
                {
                    var (received,sent)=adapter.Read();var d=rate.Down.Update(received,now);var u=rate.Up.Update(sent,now);
                    if(d is null||u is null)valid=false;else{down+=d.Value;up+=u.Value;}
                }
                // A removed adapter: the next poll lists the adapters again.
                catch(NetworkInformationException){rates.Remove(adapter.Id);valid=false;MarkChanged();}
            }
            return count==0?(null,null,"No active network interface"):(valid?down:null,valid?up:null,$"{count} active interface{(count==1?"":"s")} · totals include virtual adapters; VPN traffic can be counted twice{(!valid?" · waiting for counter baseline / reset":"")}");
        }
        catch(Exception e) {adapters=null;return (null,null,$"Interface counters unavailable ({e.GetType().Name})");}
    }

    /// <summary>Idempotent; reads still work afterwards, listing again only on the timer.</summary>
    public void Dispose()
    {
        if(Interlocked.Exchange(ref watching,2)==1)NetworkChange.NetworkAddressChanged-=OnAddressChanged;
    }

    // Subscribed on the first listing, not at construction, so a collector that never polls leaves no handler behind.
    private void Watch()
    {
        if(!watch||Interlocked.CompareExchange(ref watching,1,0)!=0)return;
        try {NetworkChange.NetworkAddressChanged+=OnAddressChanged;}
        catch(Exception) {Interlocked.CompareExchange(ref watching,0,1);} // without notifications the list still refreshes on the timer
    }

    private void OnAddressChanged(object? sender,EventArgs e)=>MarkChanged();

    private static IReadOnlyList<NetworkAdapter> ListAdapters()=>NetworkInterface.GetAllNetworkInterfaces()
        .Where(n=>Counted(n.OperationalStatus,n.NetworkInterfaceType,n.GetIPProperties().UnicastAddresses.Count))
        .Select(n=>new NetworkAdapter(n.Id,()=>{var s=n.GetIPStatistics();return (s.BytesReceived,s.BytesSent);}))
        .ToArray();
}
