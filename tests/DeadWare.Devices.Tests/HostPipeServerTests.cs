using System.IO.Pipes;
using System.Runtime.InteropServices;
using DeadWare.Core;
using DeadWare.Devices;
using Xunit;

namespace DeadWare.Devices.Tests;

public sealed class HostPipeServerTests : IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-pipe-"+Guid.NewGuid().ToString("N"));
    readonly string name="DeadWare.Devices.Test."+Guid.NewGuid().ToString("N");
    readonly HostLog log;
    readonly CancellationTokenSource cts=new(TimeSpan.FromSeconds(20));
    public HostPipeServerTests(){log=new HostLog(dir);}
    public void Dispose(){cts.Cancel();log.Dispose();try{Directory.Delete(dir,true);}catch{}}

    static HostState Sample()=>new(HostStatus.Running,"",false,[],[],new());
    async Task<NamedPipeClientStream> Connect()
    {
        var c=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        await c.ConnectAsync(5000,cts.Token);
        return c;
    }

    [Fact]
    public async Task Pipe_rejects_wrong_version()
    {
        var handled=0;
        await using var server=new HostPipeServer(name,m=>{handled++;return Task.FromResult<HostMessage>(new Reply(true,null));},log);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Version=2,Id=7},cts.Token);
        var reply=Assert.IsType<Reply>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.False(reply.Ok);
        Assert.Equal("version",reply.Error);
        Assert.Equal(7,reply.Id);
        Assert.Equal(0,handled);
    }

    [Fact]
    public async Task Pipe_round_trips_request_and_state_event()
    {
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new StateEvent(Sample()){Id=m.Id}),log);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Id=42},cts.Token);
        var answer=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal(42,answer.Id);
        Assert.Equal(HostStatus.Running,answer.State.Status);
        var send=server.SendAsync(new StateEvent(Sample()){Id=0}); // the pipe is unbuffered: the write completes when the client reads
        var pushed=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        await send;
        Assert.Equal(0,pushed.Id);
    }

    [Fact]
    public async Task Pipe_serves_the_next_client_after_a_disconnect()
    {
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new Reply(true,null){Id=m.Id}),log);
        var run=server.RunAsync(cts.Token);
        var first=await Connect();
        await DeviceHostProtocol.WriteAsync(first,new GetStateRequest{Id=1},cts.Token);
        await DeviceHostProtocol.ReadAsync(first,cts.Token);
        await first.DisposeAsync();
        for(var attempt=0;;attempt++)
        {
            try
            {
                await using var second=await Connect();
                await DeviceHostProtocol.WriteAsync(second,new GetStateRequest{Id=2},cts.Token);
                Assert.Equal(2,(await DeviceHostProtocol.ReadAsync(second,cts.Token))!.Id);
                break;
            }
            catch(Exception e) when(attempt<20&&(e is TimeoutException||e is IOException)){await Task.Delay(50);}
        }
        Assert.False(run.IsFaulted);
    }

    [Fact]
    public async Task A_client_that_never_reads_is_dropped_and_the_next_client_is_served()
    {
        var big=new Dictionary<string,ZoneColorEntry>();
        for(var i=0;i<3000;i++) big[$"serial-{i}|location-{i}#0"]=new(){Color="#112233",ZoneName="Zone "+i};
        var settings=new LightingSettings{ZoneColors=big};
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(m.Id==1?new StateEvent(new(HostStatus.Running,"",false,[],[],settings)){Id=1}:new Reply(true,null){Id=m.Id}),log,elevated:false,writeTimeout:TimeSpan.FromMilliseconds(300));
        var run=server.RunAsync(cts.Token);
        await using var stuck=await Connect();
        await DeviceHostProtocol.WriteAsync(stuck,new GetStateRequest{Id=1},cts.Token); // the reply is larger than the pipe buffer and is never read
        await Task.Delay(1000);
        await using var second=await Connect();
        await DeviceHostProtocol.WriteAsync(second,new GetStateRequest{Id=2},cts.Token);
        Assert.Equal(2,(await DeviceHostProtocol.ReadAsync(second,cts.Token))!.Id);
        Assert.False(run.IsCompleted);
    }

    // A device of 16 zones with long ASCII names, a few KB once serialised.
    static DeviceView Device(int i)=>new($"SERIAL-{i:D5}|HID: {i:D5}","Device "+new string('d',200),0,default,"Direct",null,false,
        Enumerable.Range(0,16).Select(z=>new ZoneView($"SERIAL-{i:D5}|HID: {i:D5}#{z:D2}",z,"Zone "+new string('z',200),1,"#FF0000",false)).ToList());
    static async Task<long> Body(HostMessage message)
    {
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,message,default);
        return ms.Length-4;
    }
    // A state with as many devices as keep its message body at or under the given size.
    static async Task<HostState> StateUpTo(long bytes,LightingSettings settings)
    {
        HostState With(int n)=>new(HostStatus.Running,"",false,[],Enumerable.Range(0,n).Select(Device).ToList(),settings);
        var none=await Body(new StateEvent(With(0)));
        var each=await Body(new StateEvent(With(2)))-await Body(new StateEvent(With(1)));
        var state=With((int)((bytes-none)/each));
        Assert.InRange(await Body(new StateEvent(state)),bytes-2*each,bytes);
        return state;
    }

    // F5 item 3d (whole-branch review R4-D14): frames larger than the pipe's 64 KiB buffer, one of about 100 KB and one near 3 MiB, reach a
    // reading client through the real server, both as a reply and as a broadcast, which waits for the client to read.
    [Theory]
    [InlineData(100_000)]
    [InlineData(3_000_000)]
    public async Task A_frame_larger_than_the_pipe_buffer_reaches_a_reading_client(int bytes)
    {
        var state=await StateUpTo(bytes,new LightingSettings());
        Assert.True(await Body(new StateEvent(state))>65_536);
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new StateEvent(state){Id=m.Id}),log,elevated:false);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Id=5},cts.Token);
        var reply=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal(5,reply.Id);
        Assert.Equal(state.Devices.Count,reply.State.Devices.Count);
        var send=server.SendAsync(new StateEvent(state));
        var pushed=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        await send;
        Assert.Equal(state.Devices.Count,pushed.State.Devices.Count);
        Assert.Equal(state.Devices[^1].Zones[^1].Name,pushed.State.Devices[^1].Zones[^1].Name);
        Assert.False(run.IsCompleted);
    }

    // Settings whose written form, lighting.json's indented UTF-8, is just under its 1 MiB cap.
    static LightingSettings SettingsAtTheCap()
    {
        var colors=new Dictionary<string,ZoneColorEntry>();
        LightingSettings Settings()=>new(){DefaultColor="#123456",ZoneColors=colors};
        long Written()=>System.Text.Encoding.UTF8.GetByteCount(LightingSettingsCodec.Write(Settings()));
        ZoneColorEntry Entry()=>new(){Color="#FF0000",ZoneName=new string('n',LightingSettingsCodec.MaxKeyLength)};
        colors["zone-00000"]=Entry();var one=Written();
        colors["zone-00001"]=Entry();var each=Written()-one;
        var count=1+(LightingSettingsCodec.MaxFileBytes-one)/each;
        for(var i=2;i<count;i++) colors[$"zone-{i:D5}"]=Entry();
        Assert.InRange(Written(),LightingSettingsCodec.MaxFileBytes-each,LightingSettingsCodec.MaxFileBytes);
        Assert.Null(LightingSettingsCodec.Validate(Settings()));
        return Settings();
    }

    // F5 item 3d (whole-branch review R4-D10): the room the 4 MiB cap leaves. Settings at lighting.json's cap plus a device list of nearly
    // 3 MiB travel in one state message, within 128 KiB of the cap, and are read back; a state past 4 MiB is refused on the send side
    // before a byte is written.
    [Fact]
    public async Task Settings_at_the_cap_and_nearly_3_MiB_of_devices_fit_and_past_4_MiB_is_refused()
    {
        var settings=SettingsAtTheCap();
        const int FourMiB=4*1_048_576; // the room the spec claims, written out, so a smaller cap can't move the test with it
        var fits=new StateEvent(await StateUpTo(FourMiB,settings));
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,fits,default);
        Assert.InRange(ms.Length-4,FourMiB-131_072,FourMiB);
        Assert.True(ms.Length-4-await Body(new StateEvent(fits.State with{Settings=new()}))>LightingSettingsCodec.MaxFileBytes/2); // the settings' share
        ms.Position=0;
        var back=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(ms,default)).State;
        Assert.Equal(fits.State.Devices.Count,back.Devices.Count);
        Assert.Equal(settings.ZoneColors.Count,back.Settings.ZoneColors.Count);
        var past=new StateEvent(fits.State with{Devices=[..fits.State.Devices,..Enumerable.Range(100_000,40).Select(Device)]});
        var refused=new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(()=>DeviceHostProtocol.WriteAsync(refused,past,default));
        Assert.Equal(0,refused.Length);
    }

    // Task 7: a handler that holds request Id 1 until the test lets it go, so broadcasts sent meanwhile queue while the client is not reading.
    sealed class Held
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Go=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HostMessage> Handle(HostMessage m){if(m.Id==1){Entered.TrySetResult();await Go.Task;}return new Reply(true,null){Id=m.Id};}
    }
    static PreviewEvent Frame(string color)=>new(new Dictionary<string,string>{["zone#0"]=color});

    [Fact]
    public async Task Preview_never_displaces_a_pending_state()
    {
        var held=new Held();
        await using var server=new HostPipeServer(name,held.Handle,log,elevated:false);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Id=1},cts.Token);
        await held.Entered.Task.WaitAsync(cts.Token);
        var state=server.SendAsync(new StateEvent(Sample()));
        var preview=server.SendAsync(Frame("#FF0000"));
        held.Go.SetResult();
        Assert.Equal(1,Assert.IsType<Reply>(await DeviceHostProtocol.ReadAsync(client,cts.Token)).Id);
        Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal("#FF0000",Assert.IsType<PreviewEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token)).Colors["zone#0"]);
        await state;await preview;
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public async Task Only_the_latest_preview_is_kept()
    {
        var held=new Held();
        await using var server=new HostPipeServer(name,held.Handle,log,elevated:false);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Id=1},cts.Token);
        await held.Entered.Task.WaitAsync(cts.Token);
        foreach(var c in new[]{"#110000","#220000","#330000"}) _=server.SendAsync(Frame(c));
        held.Go.SetResult();
        Assert.Equal(1,Assert.IsType<Reply>(await DeviceHostProtocol.ReadAsync(client,cts.Token)).Id);
        Assert.Equal("#330000",Assert.IsType<PreviewEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token)).Colors["zone#0"]);
        var send=server.SendAsync(new StateEvent(Sample()));
        Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token)); // nothing else was queued ahead of it
        await send;
        Assert.False(run.IsCompleted);
    }

    // Lighting 5, Task 9 (amendment 5): hub readings have a latest-wins slot of their own, written after a waiting state and preview.
    [Fact]
    public async Task A_readings_event_never_replaces_a_waiting_state_or_preview()
    {
        var held=new Held();
        await using var server=new HostPipeServer(name,held.Handle,log,elevated:false);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Id=1},cts.Token);
        await held.Entered.Task.WaitAsync(cts.Token);
        var state=server.SendAsync(new StateEvent(Sample()));
        var preview=server.SendAsync(Frame("#FF0000"));
        var readings=server.SendAsync(new HubReadingsEvent(new HubReadings([new HubPartReading(13,"pump","iCUE LINK TITAN 360",2774,43.7)],1200)));
        held.Go.SetResult();
        Assert.Equal(1,Assert.IsType<Reply>(await DeviceHostProtocol.ReadAsync(client,cts.Token)).Id);
        Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal("#FF0000",Assert.IsType<PreviewEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token)).Colors["zone#0"]);
        var hub=Assert.IsType<HubReadingsEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal(1200,hub.Readings.AgeMs);
        Assert.Equal(2774,hub.Readings.Parts.Single().Rpm);
        await state;await preview;await readings;
        Assert.False(run.IsCompleted);
    }

    // Once for a client that hangs up, and once for a client dropped because a broadcast to it timed out (one larger than the pipe's buffer
    // that it never reads).
    [Fact]
    public async Task ClientGone_is_called_when_a_client_disconnects()
    {
        var gone=0;
        var big=new Dictionary<string,ZoneColorEntry>();
        for(var i=0;i<3000;i++) big[$"serial-{i}|location-{i}#0"]=new(){Color="#112233",ZoneName="Zone "+i};
        var bigState=new StateEvent(new(HostStatus.Running,"",false,[],[],new LightingSettings{ZoneColors=big}));
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new Reply(true,null){Id=m.Id}),log,elevated:false,writeTimeout:TimeSpan.FromMilliseconds(300),clientGone:()=>Interlocked.Increment(ref gone));
        var run=server.RunAsync(cts.Token);
        var first=await Connect();
        await DeviceHostProtocol.WriteAsync(first,new GetStateRequest{Id=1},cts.Token);
        await DeviceHostProtocol.ReadAsync(first,cts.Token);
        Assert.Equal(0,Volatile.Read(ref gone));
        await first.DisposeAsync();
        await Waits.Until(()=>Volatile.Read(ref gone)==1);
        await using var stuck=await ConnectRetrying();
        await DeviceHostProtocol.WriteAsync(stuck,new GetStateRequest{Id=2},cts.Token);
        Assert.Equal(2,(await DeviceHostProtocol.ReadAsync(stuck,cts.Token))!.Id);
        _=server.SendAsync(bigState); // never read
        await Waits.Until(()=>Volatile.Read(ref gone)==2);
        Assert.False(run.IsCompleted);
    }

    // The previous client's ClientGone comes before the next client's first request is handled, so a late one can never end the new
    // client's preview subscription.
    [Fact]
    public async Task A_new_clients_subscription_survives_the_previous_clients_disconnect()
    {
        var events=new List<string>();
        await using var server=new HostPipeServer(name,m=>{lock(events)events.Add("request "+m.Id);return Task.FromResult<HostMessage>(new Reply(true,null){Id=m.Id});},log,elevated:false,
            clientGone:()=>{Thread.Sleep(200);lock(events)events.Add("gone");}); // slow, as one waiting on the host's lock is
        var run=server.RunAsync(cts.Token);
        var first=await Connect();
        await DeviceHostProtocol.WriteAsync(first,new SubscribePreviewRequest(true){Id=1},cts.Token);
        await DeviceHostProtocol.ReadAsync(first,cts.Token);
        await first.DisposeAsync();
        await using var second=await ConnectRetrying();
        await DeviceHostProtocol.WriteAsync(second,new SubscribePreviewRequest(true){Id=2},cts.Token);
        Assert.Equal(2,(await DeviceHostProtocol.ReadAsync(second,cts.Token))!.Id);
        lock(events)Assert.Equal(["request 1","gone","request 2"],events);
        Assert.False(run.IsCompleted);
    }

    // A client connecting just after another hung up can find the pipe still busy with the old one; it tries again, as the window does.
    async Task<NamedPipeClientStream> ConnectRetrying()
    {
        for(var attempt=0;;attempt++)
        {
            try{return await Connect();}
            catch(Exception e) when(attempt<40&&(e is TimeoutException||e is IOException)){await Task.Delay(50);}
        }
    }

    [Fact]
    public void Sddl_denies_network_gives_user_read_write_and_admins_full()
    {
        var plain=HostPipeServer.Sddl("S-1-5-21-1-2-3-1001",null);
        Assert.Equal("D:(D;;GA;;;NU)(A;;GRGW;;;S-1-5-21-1-2-3-1001)(A;;GA;;;BA)",plain);
        Assert.DoesNotContain("S:",plain); // no SACL when not elevated
        Assert.Equal(plain+"S:(ML;;NWNR;;;HI)",HostPipeServer.Sddl("S-1-5-21-1-2-3-1001","HI"));
    }

    [DllImport("advapi32.dll",SetLastError=true)]
    static extern uint GetSecurityInfo(SafeHandle handle,int objectType,int securityInfo,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl,out IntPtr sd);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr sd,uint revision,uint securityInfo,out IntPtr str,out uint length);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static string KernelSddl(SafeHandle h,uint info)
    {
        Assert.Equal(0u,GetSecurityInfo(h,6,(int)info,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,out var sd));
        try
        {
            Assert.True(ConvertSecurityDescriptorToStringSecurityDescriptorW(sd,1,info,out var str,out _));
            try{return Marshal.PtrToStringUni(str)!;}finally{LocalFree(str);}
        }
        finally{LocalFree(sd);}
    }

    [Fact]
    public async Task Created_pipe_carries_the_label_and_the_dacl()
    {
        // Non-elevated tests cannot set a High label, so the level is parameterised: Medium proves the SACL path is applied.
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new Reply(true,null)),log,elevated:false,labelLevel:"ME");
        server.Open();
        var label=KernelSddl(server.PipeHandle!,0x10);
        Assert.Contains("ML;;NWNR;;;ME",label);
        var dacl=KernelSddl(server.PipeHandle!,0x4);
        Assert.StartsWith("D:",dacl);
        Assert.Contains("(D;;FA;;;NU)",dacl);
        Assert.Contains(";;;BA)",dacl);
    }

    [Fact]
    public async Task A_taken_pipe_name_fails_to_open()
    {
        await using var first=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new Reply(true,null)),log,elevated:false);
        first.Open();
        await using var second=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new Reply(true,null)),log,elevated:false);
        Assert.ThrowsAny<Exception>(()=>second.Open());
    }

    [Fact]
    public async Task Default_elevation_path_creates_a_working_pipe()
    {
        // On an elevated runner this applies the High label natively; non-elevated it applies none. Either way a same-process client connects.
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new Reply(true,null){Id=m.Id}),log);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Id=3},cts.Token);
        Assert.Equal(3,(await DeviceHostProtocol.ReadAsync(client,cts.Token))!.Id);
        if(Environment.IsPrivilegedProcess) Assert.Contains("ML;;NWNR;;;HI",KernelSddl(server.PipeHandle!,0x10));
    }
}
