using System.Net.Sockets;
using DeadWare.Core;

namespace DeadWare.Devices;

// The OpenRGB connection is gone: a call outlived its bound or the transport failed. The host marks its session dead and reconnects.
internal sealed class OpenRgbSessionLostException(string message,Exception? inner=null):Exception(message,inner);

// Ruling AO: every OpenRGB call the host makes is bounded in time. OpenRGB.NET 3.1.1 waits for each reply with no timeout (its timeout
// covers only the TCP connect), and the host makes these calls under its lock, so a silent or dying OpenRGB would otherwise freeze the
// host, its conflict gate and Stop. Each call runs on the caller's thread under a watchdog: at the bound the watchdog aborts the inner
// session, which fails the waiting call (disposing OpenRGB.NET's client cancels its wait and closes its socket), and the call reports the
// session lost. A broken connection is reported the same way. Once lost, every call fails at once until a Connect succeeds, so a dead
// OpenRGB costs one bound per operation, not one per device. Other failures, such as a device refusing a frame, pass through untouched.
internal sealed class BoundedSession : IOpenRgbSession
{
    public static readonly TimeSpan CallBound=TimeSpan.FromSeconds(3);
    readonly IOpenRgbSession inner;
    readonly TimeProvider clock;
    readonly TimeSpan bound;
    volatile bool lost=true;
    // The watchdog runs on real time unless a test supplies a clock, so a test moving its fake clock can never trip it.
    public BoundedSession(IOpenRgbSession inner,TimeProvider? clock=null,TimeSpan? bound=null)
    {this.inner=inner;this.clock=clock??TimeProvider.System;this.bound=bound??CallBound;inner.DeviceListUpdated+=OnUpdated;}
    public event Action? DeviceListUpdated;
    void OnUpdated()=>DeviceListUpdated?.Invoke();
    public bool Connected=>!lost&&inner.Connected;
    public void Connect(){lost=true;Run("connect",()=>{inner.Connect();return 0;},connecting:true);lost=false;}
    public IReadOnlyList<OpenRgbController> ReadControllers()=>Run("controller read",inner.ReadControllers);
    public void UpdateLeds(int index,ReadOnlySpan<Rgb> colors){var copy=colors.ToArray();Run("LED update",()=>{inner.UpdateLeds(index,copy);return 0;});}
    public void UpdateMode(int index,int modeIndex,IReadOnlyList<Rgb>? colors)=>Run("mode change",()=>{inner.UpdateMode(index,modeIndex,colors);return 0;});
    public void ResizeZone(int index,int zoneIndex,int size)=>Run("zone resize",()=>{inner.ResizeZone(index,zoneIndex,size);return 0;});
    // Task 9, ruling P3: outside the watchdog. The request runs on a connection of its own within its own limits (OpenRgbSession), so its
    // failure passes through untouched and never drops this session; the watchdog's abort couldn't end it anyway.
    public void RequestHardwareRescan()=>inner.RequestHardwareRescan();
    public void Abort(){lost=true;inner.Abort();}
    public void Dispose(){lost=true;inner.DeviceListUpdated-=OnUpdated;inner.Dispose();}

    const int Running=0,Done=1,Expired=2;
    T Run<T>(string what,Func<T> call,bool connecting=false)
    {
        if(lost&&!connecting) throw new OpenRgbSessionLostException("OpenRGB isn't connected.");
        var state=Running;
        using var watchdog=clock.CreateTimer(_=>{if(Interlocked.CompareExchange(ref state,Expired,Running)==Running) inner.Abort();},null,bound,Timeout.InfiniteTimeSpan);
        T result;
        try{result=call();}
        catch(Exception e) when(e is not OpenRgbSessionLostException)
        {
            if(Interlocked.CompareExchange(ref state,Done,Running)==Expired) throw Lost(TooSlow(what),e,aborted:true);
            if(Transport(e)) throw Lost($"The connection to OpenRGB failed during a {what}: {e.Message}",e);
            throw;
        }
        // Expired just as the call returned: the watchdog has already dropped the client, so the session is gone too.
        if(Interlocked.CompareExchange(ref state,Done,Running)==Expired) throw Lost(TooSlow(what),null,aborted:true);
        return result;
    }
    string TooSlow(string what)=>$"OpenRGB didn't answer a {what} within {bound.TotalSeconds:0.#} s.";
    // A cancelled wait, a closed or failed socket, or a timeout. Anything else a call throws concerns one device (a frame it refuses,
    // say) and leaves the session up.
    static bool Transport(Exception e)=>e is SocketException or IOException or ObjectDisposedException or OperationCanceledException or TimeoutException||e.InnerException is SocketException or IOException;
    // aborted: the watchdog has already dropped the inner session.
    OpenRgbSessionLostException Lost(string message,Exception? cause,bool aborted=false)
    {
        lost=true;
        if(!aborted) inner.Abort();
        return new(message,cause);
    }
}
