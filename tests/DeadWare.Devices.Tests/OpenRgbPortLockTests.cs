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

    // F6 item 3 (whole-branch review D2): Program takes its port guard from GuardFor. An elevated host gets the production lock to hold
    // and that lock's Guard, its filters and then its self-check; a host without administrator rights gets neither.
    [Fact]
    public void GuardFor_gives_an_elevated_host_the_locks_guard_and_others_none()
    {
        Assert.Null(GuardFor(privileged:false,null,out var none));
        Assert.Null(none);
        var guard=GuardFor(privileged:true,null,out var held);
        using var _=held;
        Assert.NotNull(held);
        Assert.NotNull(guard);
        Assert.Same(held,guard.Target);
        Assert.Equal(nameof(OpenRgbPortLock.Guard),guard.Method.Name);
        Assert.Equal(OpenRgbEngine.Port,held.Port);
        Assert.Equal(Environment.ProcessPath,held.AppPath);
        Assert.Equal(AdministratorsOnly,held.UserSddl);
    }

    // F6 item 1 (Ruling BH): the self-check's refusals, word for word, with the real port in the second.
    [Fact]
    public void The_self_check_texts_are_exact()
    {
        Assert.Equal("DeadWare's limit on OpenRGB's network port would also block its own lighting helper (Windows refused its test connection), so it won't run OpenRGB as administrator. Turn lighting off and on in Settings to try again.",SelfBlockedText);
        Assert.Equal("Another program is using OpenRGB's network port 6744, so DeadWare won't run OpenRGB as administrator. Quit that program, then turn lighting off and on in Settings.",PortTakenText(6744));
    }

    // Each outcome of the self-check's connect: refused passes, a connect that succeeds is another program on the port (named by the lock's
    // own port), and access denied, any other error or no answer in time is the lock blocking the host. Each check logs its outcome once.
    [Theory]
    [InlineData(SocketError.ConnectionRefused,null,"ConnectionRefused (10061)")]
    [InlineData(SocketError.AccessDenied,"blocked","AccessDenied (10013)")]
    [InlineData(SocketError.Success,"taken","Success (0)")]
    [InlineData(SocketError.TimedOut,"blocked","TimedOut (10060)")]
    [InlineData(SocketError.NetworkUnreachable,"blocked","NetworkUnreachable (10051)")]
    public void The_self_check_maps_each_connect_outcome(SocketError outcome,string? refusal,string logged)
    {
        var lines=new List<string>();
        var asked=new List<int>();
        using var l=new OpenRgbPortLock(50123,Environment.ProcessPath!,AdministratorsOnly,x64:true,connect:port=>{asked.Add(port);return outcome;},log:lines.Add);
        Assert.Equal(refusal switch{"blocked"=>SelfBlockedText,"taken"=>PortTakenText(50123),_=>null},l.SelfCheck());
        Assert.Equal([50123],asked);
        Assert.Equal(["openrgb port self-check: "+logged],lines);
    }

    // A connect that throws something other than a socket error couldn't show the host gets through either.
    [Fact]
    public void A_self_check_whose_connect_throws_is_the_blocked_refusal()
    {
        var lines=new List<string>();
        using var l=new OpenRgbPortLock(50123,Environment.ProcessPath!,AdministratorsOnly,x64:true,connect:_=>throw new InvalidOperationException("broken"),log:lines.Add);
        Assert.Equal(SelfBlockedText,l.SelfCheck());
        Assert.Equal(["openrgb port self-check failed: broken"],lines);
    }

    // The self-check runs only once the filters are in place: a lock that refuses doesn't connect at all, and its refusal stands.
    [Fact]
    public void The_guard_self_checks_only_after_ensure_succeeds()
    {
        var connects=0;
        using var l=new OpenRgbPortLock(6744,Environment.ProcessPath!,AdministratorsOnly,x64:false,connect:_=>{connects++;return SocketError.ConnectionRefused;});
        Assert.Equal(RefusalText(50,new Win32Exception(50).Message),l.Guard());
        Assert.Equal(0,connects);
    }

    // The real connect, without a lock: refused where nothing listens, success where something does, well inside its timeout.
    [Fact]
    public void ConnectOnce_reports_refused_and_success()
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(SocketError.ConnectionRefused,ConnectOnce(FreePort()));
        Assert.True(clock.Elapsed<SelfCheckTimeout,$"{clock.Elapsed}");
        using var server=new Listener();
        Assert.Equal(SocketError.Success,ConnectOnce(server.Port));
    }

    // Whole-branch review D7/D14: the host's connects to OpenRGB, the port probe's, the self-check's and OpenRGB.NET's, use a parameterless
    // TcpClient or socket: a dual-mode IPv6 socket wherever IPv6 is on, so a connect to 127.0.0.1 goes out as ::ffff:127.0.0.1 and meets
    // the V6 filters as well as the V4 ones.
    [Fact]
    public void A_parameterless_TcpClient_is_a_dual_mode_ipv6_socket()
    {
        if(!Socket.OSSupportsIPv6) return;
        using var client=new TcpClient();
        Assert.Equal(AddressFamily.InterNetworkV6,client.Client.AddressFamily);
        Assert.True(client.Client.DualMode);
    }

    // F6 item 6 (whole-branch review D6): Ensure's retry. A failed attempt closes the session and tries once more with a fresh one; the
    // second attempt's code is the answer.
    [Fact]
    public void A_failed_attempt_is_retried_once_after_closing_the_session()
    {
        var calls=new List<string>();
        var codes=new Queue<int>([5,0]);
        Assert.Equal(0,Retried(()=>{calls.Add("attempt");return codes.Dequeue();},()=>calls.Add("close")));
        Assert.Equal(["attempt","close","attempt"],calls);
        calls.Clear();
        codes=new Queue<int>([5,6,0]);
        Assert.Equal(6,Retried(()=>{calls.Add("attempt");return codes.Dequeue();},()=>calls.Add("close")));
        Assert.Equal(["attempt","close","attempt"],calls);
        calls.Clear();
        Assert.Equal(0,Retried(()=>{calls.Add("attempt");return 0;},()=>calls.Add("close")));
        Assert.Equal(["attempt"],calls);
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

    // F6 item 2 (whole-branch review D7/D14): with the lock permitting this image, a dual-mode connect to ::ffff:127.0.0.1 gets through,
    // both a dual-mode socket's and a parameterless TcpClient's to 127.0.0.1, as the host's own connects are.
    [ElevatedFact]
    public void The_permitted_image_connects_through_a_dual_mode_socket()
    {
        using var server=new Listener();
        using var l=new OpenRgbPortLock(server.Port,Environment.ProcessPath!);
        Assert.Null(l.Ensure());
        Assert.Equal(SocketError.Success,Connect(server.Port,dualModeMapped:true));
        using var client=new TcpClient();
        Assert.True(client.Client.DualMode);
        client.Connect(IPAddress.Loopback,server.Port);
        Assert.True(client.Connected);
    }

    // F6 item 1 (a): a lock permitting this image, with nothing listening: the self-check's connect is refused, so it passes.
    [ElevatedFact]
    public void The_self_check_passes_when_the_permit_lets_the_host_through()
    {
        var lines=new List<string>();
        using var l=new OpenRgbPortLock(FreePort(),Environment.ProcessPath!,log:lines.Add);
        Assert.Null(l.Guard());
        Assert.Equal(["openrgb port self-check: ConnectionRefused (10061)"],lines);
    }

    // F6 item 1 (b): a lock permitting another image blocks this one too, so the self-check gives the first refusal.
    [ElevatedFact]
    public void The_self_check_refuses_when_the_lock_blocks_the_host()
    {
        var lines=new List<string>();
        using var l=new OpenRgbPortLock(FreePort(),Cmd,log:lines.Add);
        Assert.Equal(SelfBlockedText,l.Guard());
        Assert.Equal(["openrgb port self-check: AccessDenied (10013)"],lines);
    }

    // F6 item 1 (c): a lock permitting this image, with a listener on the port: another program holds it, the second refusal.
    [ElevatedFact]
    public void The_self_check_refuses_when_another_program_holds_the_port()
    {
        using var server=new Listener();
        using var l=new OpenRgbPortLock(server.Port,Environment.ProcessPath!);
        Assert.Equal(PortTakenText(server.Port),l.Guard());
    }

    // F6 item 6 (whole-branch review D6): what Ensure added, read back from the filter engine by key. The sublayer weighs 0xFFFF. The blocks
    // carry FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT and the permits don't; the permits weigh more. Every filter matches TCP, the port and the
    // loopback address (::ffff:127.0.0.1 at the V6 layer), and the permits add this image's app ID and the Administrators-only descriptor.
    [ElevatedFact]
    public void The_filters_and_sublayer_read_back_as_designed()
    {
        using var server=new Listener();
        using var l=new OpenRgbPortLock(server.Port,Environment.ProcessPath!);
        Assert.Null(l.Ensure());
        Assert.Equal(0u,FwpmSubLayerGetByKey0(l.Engine!,l.SubLayerKey,out var sub));
        try{Assert.Equal(0xFFFF,Marshal.PtrToStructure<FWPM_SUBLAYER0>(sub).weight);}
        finally{FwpmFreeMemory0(ref sub);}
        var v4=new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82");   // FWPM_LAYER_ALE_AUTH_CONNECT_V4, fwpmu.h
        var v6=new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");   // FWPM_LAYER_ALE_AUTH_CONNECT_V6, fwpmu.h
        var mapped=IPAddress.Parse("::ffff:127.0.0.1").GetAddressBytes();
        foreach(var (key,layer,permit) in new[]{(l.BlockV4,v4,false),(l.PermitV4,v4,true),(l.BlockV6,v6,false),(l.PermitV6,v6,true)})
        {
            var f=ReadFilter(l,key);
            Assert.Equal(layer,f.Layer);
            Assert.Equal(l.SubLayerKey,f.SubLayer);
            Assert.Equal(permit?0x1002u:0x1001u,f.Action);                            // FWP_ACTION_PERMIT / FWP_ACTION_BLOCK, terminating
            Assert.Equal(!permit,(f.Flags&0x8)!=0);                                     // FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT
            Assert.Equal(permit?15ul:1ul,f.Weight);
            Guid[] fields=permit?[ConditionIpProtocol,ConditionIpRemotePort,ConditionIpRemoteAddress,ConditionAleAppId,ConditionAleUserId]
                :[ConditionIpProtocol,ConditionIpRemotePort,ConditionIpRemoteAddress];
            Assert.Equal(fields.Order(),f.Conditions.Keys.Order());
            Assert.Equal((FwpUint8,(object)(byte)6),f.Conditions[ConditionIpProtocol]);
            Assert.Equal((FwpUint16,(object)(ushort)server.Port),f.Conditions[ConditionIpRemotePort]);
            var address=f.Conditions[ConditionIpRemoteAddress];
            if(layer==v6){Assert.Equal(FwpByteArray16Type,address.Type);Assert.Equal(mapped,(byte[])address.Value);}
            else Assert.Equal((FwpUint32,(object)0x7F000001u),address);
            if(!permit) continue;
            var appId=f.Conditions[ConditionAleAppId];
            Assert.Equal(FwpByteBlobType,appId.Type);
            var image=System.Text.Encoding.Unicode.GetString((byte[])appId.Value).TrimEnd('\0');
            Assert.StartsWith(@"\device\",image,StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(@"\"+Path.GetFileName(Environment.ProcessPath!),image,StringComparison.OrdinalIgnoreCase);
            var user=f.Conditions[ConditionAleUserId];
            Assert.Equal(FwpSecurityDescriptorType,user.Type);
            var sddl=new System.Security.AccessControl.RawSecurityDescriptor((byte[])user.Value,0).GetSddlForm(System.Security.AccessControl.AccessControlSections.Access);
            Assert.Matches(@"^D:[A-Z]*\(A;;CC;;;BA\)$",sddl);
        }
        // The higher weight is what lets the permit win inside the lock's own sublayer.
        Assert.True(ReadFilter(l,l.PermitV4).Weight>ReadFilter(l,l.BlockV4).Weight);
        Assert.True(ReadFilter(l,l.PermitV6).Weight>ReadFilter(l,l.BlockV6).Weight);
    }

    sealed record Filter(Guid Layer,Guid SubLayer,uint Action,uint Flags,ulong Weight,Dictionary<Guid,(uint Type,object Value)> Conditions);

    // One of the lock's filters as the filter engine returns it, decoded before WFP's copy is freed.
    static Filter ReadFilter(OpenRgbPortLock l,Guid key)
    {
        Assert.Equal(0u,FwpmFilterGetByKey0(l.Engine!,in key,out var p));
        try
        {
            var f=Marshal.PtrToStructure<FWPM_FILTER0>(p);
            var conditions=new Dictionary<Guid,(uint,object)>();
            var size=Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
            for(var i=0;i<f.numFilterConditions;i++)
            {
                var c=Marshal.PtrToStructure<FWPM_FILTER_CONDITION0>(f.filterCondition+i*size);
                Assert.Equal(0u,c.matchType); // FWP_MATCH_EQUAL
                var v=c.conditionValue;
                object value=v.type switch
                {
                    FwpUint8=>v.uint8,
                    FwpUint16=>v.uint16,
                    FwpUint32=>v.uint32,
                    FwpByteArray16Type=>Bytes(v.pointer,16),
                    FwpByteBlobType or FwpSecurityDescriptorType=>Blob(v.pointer),
                    _=>throw new InvalidOperationException($"condition type {v.type}"),
                };
                conditions.Add(c.fieldKey,(v.type,value));
            }
            return new(f.layerKey,f.subLayerKey,f.action.type,f.flags,Weight(f.weight),conditions);
        }
        finally{FwpmFreeMemory0(ref p);}
    }
    // FWP_VALUE0 of the integer types a filter weight may come back as (fwptypes.h: FWP_UINT8 1, FWP_UINT16 2, FWP_UINT32 3, FWP_UINT64 4,
    // the last by pointer).
    static ulong Weight(FWP_VALUE0 v)=>v.type switch
    {
        1=>v.uint8,2=>v.uint16,3=>v.uint32,4=>(ulong)Marshal.ReadInt64(v.pointer),
        _=>throw new InvalidOperationException($"weight type {v.type}"),
    };
    static byte[] Bytes(IntPtr p,int n){var b=new byte[n];Marshal.Copy(p,b,0,n);return b;}
    static byte[] Blob(IntPtr p){var blob=Marshal.PtrToStructure<FWP_BYTE_BLOB>(p);return Bytes(blob.data,(int)blob.size);}

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
