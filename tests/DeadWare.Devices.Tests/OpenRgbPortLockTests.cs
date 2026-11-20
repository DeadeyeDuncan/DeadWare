using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DeadWare.Devices;
using Xunit;
using static DeadWare.Devices.OpenRgbPortLock;

namespace DeadWare.Devices.Tests;

// F5 item 2 (whole-branch review D15, Ruling BG): the WFP lock on OpenRGB's SDK port. The layout pins and the refusals run anywhere; the
// integration tests add real filters, so they need administrator rights and run on CI's elevated runner. Each locks an ephemeral port of
// its own with a TcpListener on 127.0.0.1 in place of OpenRGB, and everything they add goes when the lock is disposed or the run ends.
public sealed class OpenRgbPortLockTests
{
    // An elevated host gets the production lock: OpenRGB's port, its own image and the Administrators-only descriptor. A host without
    // administrator rights gets none.
    [Fact]
    public void A_host_without_administrator_rights_has_no_lock()=>Assert.Null(ForHost(privileged:false));

    [Fact]
    public void An_elevated_host_locks_openrgbs_port_to_its_own_image_and_administrators()
    {
        using var l=ForHost(privileged:true)!;
        Assert.Equal(6744,l.Port);
        Assert.Equal(OpenRgbEngine.Port,l.Port);
        Assert.Equal(Environment.ProcessPath,l.AppPath);
        Assert.Equal("D:(A;;CC;;;BA)",l.UserSddl);
    }

    [Fact]
    public void The_refusal_text_is_exact()
    {
        Assert.Equal("DeadWare couldn't limit OpenRGB's network port to its lighting helper (error 5: Access is denied.), so it won't run OpenRGB as administrator. Turn lighting off and on in Settings to try again.",RefusalText(5,"Access is denied."));
        Assert.StartsWith("DeadWare couldn't limit OpenRGB's network port to its lighting helper (error 0x80320003: ",RefusalText(unchecked((int)0x80320003),"x"));
    }

    // Interop: DeadWare ships win-x64 only, and a 32-bit process refuses with the lock's text rather than guess WFP's layouts.
    [Fact]
    public void A_32_bit_process_refuses_without_touching_windows()
    {
        using var l=new OpenRgbPortLock(6744,Environment.ProcessPath!,AdministratorsOnly,x64:false);
        Assert.Equal(RefusalText(50,new Win32Exception(50).Message),l.Ensure());
        Assert.Null(l.Engine);
    }

    [Fact]
    public void A_disposed_lock_refuses()
    {
        var l=new OpenRgbPortLock(6744,Environment.ProcessPath!);
        l.Dispose();
        Assert.Equal(RefusalText(6,new Win32Exception(6).Message),l.Ensure());
    }

    // Without administrator rights the filter engine refuses the additions, so the lock refuses, and it holds no session afterwards.
    [NotElevatedFact]
    public void Without_administrator_rights_the_lock_refuses()
    {
        using var l=new OpenRgbPortLock(FreePort(),Environment.ProcessPath!);
        var refusal=l.Ensure();
        Assert.NotNull(refusal);
        Assert.StartsWith("DeadWare couldn't limit OpenRGB's network port to its lighting helper (error ",refusal);
        Assert.Null(l.Engine);
    }

    // x64 sizes and offsets, field for field from the Windows SDK 10.0.26100.0 headers (fwptypes.h, fwpmtypes.h). A wrong layout makes
    // WFP read garbage, so each is pinned.
    [Fact]
    public void The_interop_layouts_match_the_sdk_headers_on_x64()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(16,Marshal.SizeOf<FWPM_DISPLAY_DATA0>());
        Assert.Equal(8,(int)Marshal.OffsetOf<FWPM_DISPLAY_DATA0>("description"));
        Assert.Equal(16,Marshal.SizeOf<FWP_BYTE_BLOB>());
        Assert.Equal(8,(int)Marshal.OffsetOf<FWP_BYTE_BLOB>("data"));
        Assert.Equal(16,Marshal.SizeOf<FWP_VALUE0>());
        Assert.Equal(8,(int)Marshal.OffsetOf<FWP_VALUE0>("pointer"));
        Assert.Equal(16,Marshal.SizeOf<FWP_CONDITION_VALUE0>());
        Assert.Equal(8,(int)Marshal.OffsetOf<FWP_CONDITION_VALUE0>("pointer"));
        Assert.Equal(40,Marshal.SizeOf<FWPM_FILTER_CONDITION0>());
        Assert.Equal(16,(int)Marshal.OffsetOf<FWPM_FILTER_CONDITION0>("matchType"));
        Assert.Equal(24,(int)Marshal.OffsetOf<FWPM_FILTER_CONDITION0>("conditionValue"));
        Assert.Equal(20,Marshal.SizeOf<FWPM_ACTION0>());
        Assert.Equal(4,(int)Marshal.OffsetOf<FWPM_ACTION0>("filterType"));
        Assert.Equal(72,Marshal.SizeOf<FWPM_SESSION0>());
        (string field,int offset)[] session=[("displayData",16),("flags",32),("txnWaitTimeoutInMSec",36),("processId",40),("sid",48),("username",56),("kernelMode",64)];
        foreach(var (field,offset) in session) Assert.Equal((field,offset),(field,(int)Marshal.OffsetOf<FWPM_SESSION0>(field)));
        Assert.Equal(72,Marshal.SizeOf<FWPM_SUBLAYER0>());
        (string field,int offset)[] sub=[("displayData",16),("flags",32),("providerKey",40),("providerData",48),("weight",64)];
        foreach(var (field,offset) in sub) Assert.Equal((field,offset),(field,(int)Marshal.OffsetOf<FWPM_SUBLAYER0>(field)));
        Assert.Equal(200,Marshal.SizeOf<FWPM_FILTER0>());
        (string field,int offset)[] filter=[("displayData",16),("flags",32),("providerKey",40),("providerData",48),("layerKey",64),("subLayerKey",80),("weight",96),
            ("numFilterConditions",112),("filterCondition",120),("action",128),("context",152),("reserved",168),("filterId",176),("effectiveWeight",184)];
        foreach(var (field,offset) in filter) Assert.Equal((field,offset),(field,(int)Marshal.OffsetOf<FWPM_FILTER0>(field)));
    }

    static readonly string Cmd=Path.Combine(Environment.SystemDirectory,"cmd.exe");
    const string UnrelatedSidOnly="D:(A;;CC;;;LS)"; // LocalService: not this test run's account

    static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();var p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}

    sealed class Listener:IDisposable
    {
        readonly TcpListener listener=new(IPAddress.Loopback,0);
        public Listener(){listener.Start();}
        public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        public void Dispose()=>listener.Stop();
    }

    // The socket error of one connect from this process; Success when it connected.
    static SocketError Connect(int port,bool dualModeMapped=false)
    {
        using var socket=dualModeMapped?new Socket(AddressFamily.InterNetworkV6,SocketType.Stream,ProtocolType.Tcp){DualMode=true}:new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        var target=dualModeMapped?IPAddress.Parse("::ffff:127.0.0.1"):IPAddress.Loopback;
        try{socket.Connect(target,port);return SocketError.Success;}
        catch(SocketException e){return e.SocketErrorCode;}
    }

    // 1: the lock permits this test process's own image, with the Administrators descriptor: the connect succeeds.
    [ElevatedFact]
    public void The_permitted_image_with_administrator_rights_connects()
    {
        using var server=new Listener();
        using var l=new OpenRgbPortLock(server.Port,Environment.ProcessPath!);
        Assert.Null(l.Ensure());
        Assert.Equal(SocketError.Success,Connect(server.Port));
    }

    // 2: the lock permits another image: this process is refused with WSAEACCES (10013), which proves the block applies to loopback with
    // Windows Firewall's own filters in place.
    [ElevatedFact]
    public void Another_image_is_refused()
    {
        using var server=new Listener();
        Assert.Equal(SocketError.Success,Connect(server.Port));
        using var l=new OpenRgbPortLock(server.Port,Cmd);
        Assert.Null(l.Ensure());
        Assert.Equal(SocketError.AccessDenied,Connect(server.Port));
    }

    // 3: the lock permits this image, but with a descriptor that lets only an unrelated SID match: refused. The user condition is applied;
    // this stands in for a token without administrator rights, in which Administrators is deny-only.
    [ElevatedFact]
    public void The_permitted_image_without_the_user_match_is_refused()
    {
        using var server=new Listener();
        using var l=new OpenRgbPortLock(server.Port,Environment.ProcessPath!,UnrelatedSidOnly);
        Assert.Null(l.Ensure());
        Assert.Equal(SocketError.AccessDenied,Connect(server.Port));
    }

    // 4: a dual-mode IPv6 socket connecting to ::ffff:127.0.0.1 is refused too when the lock doesn't permit it.
    [ElevatedFact]
    public void A_dual_mode_ipv4_mapped_connect_is_refused()
    {
        using var server=new Listener();
        Assert.Equal(SocketError.Success,Connect(server.Port,dualModeMapped:true));
        using var l=new OpenRgbPortLock(server.Port,Cmd);
        Assert.Null(l.Ensure());
        Assert.Equal(SocketError.AccessDenied,Connect(server.Port,dualModeMapped:true));
    }

    // 5: the filters are scoped to their port: another listener stays reachable.
    [ElevatedFact]
    public void Another_port_stays_reachable()
    {
        using var locked=new Listener();
        using var other=new Listener();
        using var l=new OpenRgbPortLock(locked.Port,Cmd);
        Assert.Null(l.Ensure());
        Assert.Equal(SocketError.AccessDenied,Connect(locked.Port));
        Assert.Equal(SocketError.Success,Connect(other.Port));
    }

    // 6: disposing the lock closes its dynamic session, and Windows removes its filters: the refused connect succeeds again.
    [ElevatedFact]
    public void After_dispose_the_refused_connect_succeeds()
    {
        using var server=new Listener();
        var l=new OpenRgbPortLock(server.Port,Cmd);
        Assert.Null(l.Ensure());
        Assert.Equal(SocketError.AccessDenied,Connect(server.Port));
        l.Dispose();
        Assert.Equal(SocketError.Success,Connect(server.Port));
    }

    // Before every launch Ensure confirms the filters still exist: one deleted behind the lock's back is added again, and the block holds.
    [ElevatedFact]
    public void Ensure_adds_a_deleted_filter_again()
    {
        using var server=new Listener();
        using var l=new OpenRgbPortLock(server.Port,Cmd);
        Assert.Null(l.Ensure());
        Assert.Equal(0u,FwpmFilterDeleteByKey0(l.Engine!,in l.BlockV4));
        Assert.Equal(SocketError.Success,Connect(server.Port));
        Assert.Null(l.Ensure());
        Assert.Equal(SocketError.AccessDenied,Connect(server.Port));
    }
}
