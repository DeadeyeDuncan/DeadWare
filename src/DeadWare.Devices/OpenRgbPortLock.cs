using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeadWare.Devices;

// F5 item 2 (whole-branch review D15, Ruling BG): OpenRGB 1.0rc3's SDK has no authentication, and its profile requests hand the client's
// string to ProfileManager, which appends .orp and joins it to the config folder unchecked (NetworkServer.cpp:881-930,
// ProfileManager.cpp:35-69, :436-446). So an elevated host lets only itself, while it has administrator rights, connect to the OpenRGB it
// runs as administrator. It does that with a Windows Filtering Platform session opened with FWPM_SESSION_FLAG_DYNAMIC: Windows deletes
// everything the session added when its engine handle closes, the host exiting or crashing included, so nothing here outlives the host.
// In one transaction it adds a sublayer of its own at weight 0xFFFF and, at ALE_AUTH_CONNECT for IPv4 and IPv6:
//   - a block filter for TCP to 127.0.0.1 (::ffff:127.0.0.1 at the V6 layer) and the port, with FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT so no
//     filter in a lower-priority sublayer can turn it into a permit;
//   - a permit filter of higher weight with the same conditions plus ALE_APP_ID equal to the host's own image and ALE_USER_ID matching a
//     descriptor that grants only BUILTIN\Administrators FWP_ACTRL_MATCH_FILTER (D:(A;;CC;;;BA)). A token without administrator rights
//     holds Administrators deny-only and fails that match, and so does a copy of DeadWare.Devices.exe such a program starts and controls.
// The filters match by remote address: FWP_CONDITION_FLAG_IS_LOOPBACK isn't used (Ruling BG).
internal sealed class OpenRgbPortLock : IDisposable
{
    // CC is FWP_ACTRL_MATCH_FILTER (0x1, fwptypes.h), the right WFP checks when it evaluates a FWP_SECURITY_DESCRIPTOR_TYPE condition.
    public const string AdministratorsOnly="D:(A;;CC;;;BA)";
    public int Port{get;}
    public string AppPath{get;}
    public string UserSddl{get;}
    readonly bool x64;
    readonly object gate=new();
    readonly Guid subLayerKey=Guid.NewGuid();
    // Fresh keys for each lock, so two locks (two users' elevated hosts, or parallel tests) never collide.
    internal readonly Guid BlockV4=Guid.NewGuid(),PermitV4=Guid.NewGuid(),BlockV6=Guid.NewGuid(),PermitV6=Guid.NewGuid();
    EngineHandle? engine;
    bool disposed;

    public OpenRgbPortLock(int port,string appPath,string userSddl=AdministratorsOnly):this(port,appPath,userSddl,Environment.Is64BitProcess){}
    // x64: the structure layouts below are x64's (DeadWare ships win-x64 only); a 32-bit process refuses rather than guess them.
    internal OpenRgbPortLock(int port,string appPath,string userSddl,bool x64){Port=port;AppPath=appPath;UserSddl=userSddl;this.x64=x64;}

    // The lock an elevated host holds for its whole life; none for a host without administrator rights, which can't add filters and whose
    // OpenRGB has only the user's rights. Nothing touches Windows until the first Ensure.
    public static OpenRgbPortLock? ForHost(bool privileged)=>privileged?new(OpenRgbEngine.Port,Environment.ProcessPath??"",AdministratorsOnly):null;

    public static string RefusalText(int code,string message)
        =>$"DeadWare couldn't limit OpenRGB's network port to its lighting helper (error {Code(code)}: {message}), so it won't run OpenRGB as administrator. Turn lighting off and on in Settings to try again.";
    // WFP's own errors are HRESULTs (FWP_E_* 0x8032xxxx), shown in hex as Microsoft documents them; Win32 errors in decimal.
    static string Code(int code)=>(uint)code>0xFFFF?"0x"+((uint)code).ToString("X8",CultureInfo.InvariantCulture):code.ToString(CultureInfo.InvariantCulture);
    static string Message(int code)=>new Win32Exception(code).Message;

    // Called before every launch of OpenRGB: confirms the filters still exist and adds any that are missing. Null once they are all in
    // place, else the refusal. A failure closes the session, which removes what it added, and tries once more with a fresh one (the
    // filter engine may have restarted under it); a second failure is the refusal.
    public string? Ensure()
    {
        lock(gate)
        {
            if(disposed) return RefusalText(ErrorInvalidHandle,Message(ErrorInvalidHandle));
            if(!x64) return RefusalText(ErrorNotSupported,Message(ErrorNotSupported));
            var code=TryEnsure();
            if(code!=0){Close();code=TryEnsure();}
            if(code==0) return null;
            Close();
            return RefusalText(code,Message(code));
        }
    }

    public void Dispose(){lock(gate){disposed=true;Close();}}

    void Close(){engine?.Dispose();engine=null;}

    // The engine handle, for the elevated tests that delete a filter behind the lock's back.
    internal EngineHandle? Engine{get{lock(gate)return engine;}}

    int TryEnsure()
    {
        if(engine is null)
        {
            var opening=Open(out var opened);
            if(opening!=0) return opening;
            engine=opened;
        }
        var e=engine!;
        var code=Present(e,out var subLayerThere,out var missing);
        if(code!=0) return code;
        if(subLayerThere&&missing.Count==0) return 0;
        code=Add(e,!subLayerThere,missing);
        if(code!=0) return code;
        // Confirmed after the commit, not assumed from it.
        code=Present(e,out subLayerThere,out missing);
        if(code!=0) return code;
        return subLayerThere&&missing.Count==0?0:FwpEFilterNotFound;
    }

    static int Open(out EngineHandle? opened)
    {
        opened=null;
        var name=Marshal.StringToHGlobalUni("DeadWare OpenRGB port lock");
        try
        {
            var session=new FWPM_SESSION0{displayData=new(){name=name},flags=FwpmSessionFlagDynamic,txnWaitTimeoutInMSec=10_000};
            var code=(int)FwpmEngineOpen0(IntPtr.Zero,RpcCAuthnDefault,IntPtr.Zero,in session,out var handle);
            if(code!=0){handle.Dispose();return code;}
            opened=handle;
            return 0;
        }
        finally{Marshal.FreeHGlobal(name);}
    }

    // Which of the lock's objects the session still has. Not found is an answer; any other error is a failure.
    int Present(EngineHandle e,out bool subLayerThere,out List<Guid> missing)
    {
        missing=new();
        subLayerThere=false;
        var code=(int)FwpmSubLayerGetByKey0(e,in subLayerKey,out var sub);
        if(code==0){subLayerThere=true;FwpmFreeMemory0(ref sub);}
        else if(code!=FwpESubLayerNotFound) return code;
        foreach(var key in new[]{BlockV4,PermitV4,BlockV6,PermitV6})
        {
            code=(int)FwpmFilterGetByKey0(e,in key,out var filter);
            if(code==0) FwpmFreeMemory0(ref filter);
            else if(code==FwpEFilterNotFound) missing.Add(key);
            else return code;
        }
        return 0;
    }

    int Add(EngineHandle e,bool addSubLayer,List<Guid> missing)
    {
        using var mem=new NativeMemory();
        var name=mem.String("DeadWare OpenRGB port lock");
        IntPtr appId=IntPtr.Zero,sd=IntPtr.Zero;
        var code=(int)FwpmTransactionBegin0(e,0);
        if(code!=0) return code;
        var committed=false;
        try
        {
            if(addSubLayer)
            {
                var sub=new FWPM_SUBLAYER0{subLayerKey=subLayerKey,displayData=new(){name=name},weight=0xFFFF};
                code=(int)FwpmSubLayerAdd0(e,in sub,IntPtr.Zero);
                if(code!=0) return code;
            }
            if(missing.Contains(PermitV4)||missing.Contains(PermitV6))
            {
                code=(int)FwpmGetAppIdFromFileName0(AppPath,out appId);
                if(code!=0) return code;
                if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(UserSddl,SddlRevision1,out sd,out var sdSize)) return Marshal.GetLastWin32Error();
                // The user condition's descriptor must be self-relative, as ConvertStringSecurityDescriptorToSecurityDescriptorW returns it;
                // an absolute one gets ERROR_INVALID_SECURITY_DESCR from FwpmFilterAdd0.
                var sdBlob=mem.Struct(new FWP_BYTE_BLOB{size=sdSize,data=sd});
                foreach(var (key,layer,v6) in new[]{(PermitV4,LayerAleAuthConnectV4,false),(PermitV6,LayerAleAuthConnectV6,true)})
                {
                    if(!missing.Contains(key)) continue;
                    var conditions=Conditions(mem,v6,appId,sdBlob);
                    code=AddFilter(e,key,layer,name,FwpActionPermit,15,0,conditions);
                    if(code!=0) return code;
                }
            }
            foreach(var (key,layer,v6) in new[]{(BlockV4,LayerAleAuthConnectV4,false),(BlockV6,LayerAleAuthConnectV6,true)})
            {
                if(!missing.Contains(key)) continue;
                code=AddFilter(e,key,layer,name,FwpActionBlock,1,FwpmFilterFlagClearActionRight,Conditions(mem,v6,IntPtr.Zero,IntPtr.Zero));
                if(code!=0) return code;
            }
            code=(int)FwpmTransactionCommit0(e);
            committed=code==0;
            return code;
        }
        finally
        {
            if(!committed) FwpmTransactionAbort0(e);
            if(appId!=IntPtr.Zero) FwpmFreeMemory0(ref appId);
            if(sd!=IntPtr.Zero) LocalFree(sd);
        }
    }

    // TCP to the loopback address and the port; the permit adds the host's app ID and the user descriptor.
    (IntPtr array,uint count) Conditions(NativeMemory mem,bool v6,IntPtr appId,IntPtr sdBlob)
    {
        var list=new List<FWPM_FILTER_CONDITION0>
        {
            new(){fieldKey=ConditionIpProtocol,matchType=FwpMatchEqual,conditionValue=new(){type=FwpUint8,uint8=IpProtoTcp}},
            new(){fieldKey=ConditionIpRemotePort,matchType=FwpMatchEqual,conditionValue=new(){type=FwpUint16,uint16=(ushort)Port}},
            v6
                ?new(){fieldKey=ConditionIpRemoteAddress,matchType=FwpMatchEqual,conditionValue=new(){type=FwpByteArray16Type,pointer=mem.Bytes(MappedLoopback)}}
                :new(){fieldKey=ConditionIpRemoteAddress,matchType=FwpMatchEqual,conditionValue=new(){type=FwpUint32,uint32=LoopbackV4}},
        };
        if(appId!=IntPtr.Zero)
        {
            list.Add(new(){fieldKey=ConditionAleAppId,matchType=FwpMatchEqual,conditionValue=new(){type=FwpByteBlobType,pointer=appId}});
            list.Add(new(){fieldKey=ConditionAleUserId,matchType=FwpMatchEqual,conditionValue=new(){type=FwpSecurityDescriptorType,pointer=sdBlob}});
        }
        return (mem.Array(list),(uint)list.Count);
    }

    int AddFilter(EngineHandle e,Guid key,Guid layer,IntPtr name,uint action,byte weight,uint flags,(IntPtr array,uint count) conditions)
    {
        var filter=new FWPM_FILTER0
        {
            filterKey=key,displayData=new(){name=name},flags=flags,layerKey=layer,subLayerKey=subLayerKey,
            weight=new(){type=FwpUint8,uint8=weight},numFilterConditions=conditions.count,filterCondition=conditions.array,action=new(){type=action},
        };
        return (int)FwpmFilterAdd0(e,in filter,IntPtr.Zero,out _);
    }

    // Unmanaged memory for one transaction's structures, freed together.
    sealed class NativeMemory:IDisposable
    {
        readonly List<IntPtr> blocks=new();
        IntPtr Track(IntPtr p){blocks.Add(p);return p;}
        public IntPtr String(string s)=>Track(Marshal.StringToHGlobalUni(s));
        public IntPtr Bytes(byte[] bytes){var p=Track(Marshal.AllocHGlobal(bytes.Length));Marshal.Copy(bytes,0,p,bytes.Length);return p;}
        public IntPtr Struct<T>(T value) where T:struct{var p=Track(Marshal.AllocHGlobal(Marshal.SizeOf<T>()));Marshal.StructureToPtr(value,p,false);return p;}
        public IntPtr Array<T>(List<T> items) where T:struct
        {
            var size=Marshal.SizeOf<T>();
            var p=Track(Marshal.AllocHGlobal(size*items.Count));
            for(var i=0;i<items.Count;i++) Marshal.StructureToPtr(items[i],p+i*size,false);
            return p;
        }
        public void Dispose(){foreach(var p in blocks) Marshal.FreeHGlobal(p);blocks.Clear();}
    }

    internal sealed class EngineHandle():SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle()=>FwpmEngineClose0(handle)==0;
    }

    // Constants from the Windows SDK 10.0.26100.0 headers: fwptypes.h, fwpmtypes.h, fwpmu.h (GUIDs), winerror.h, sddl.h.
    const uint FwpmSessionFlagDynamic=0x1;              // fwpmtypes.h:125
    const uint FwpmFilterFlagClearActionRight=0x8;      // fwpmtypes.h:474
    const uint FwpActionBlock=0x1|0x1000,FwpActionPermit=0x2|0x1000; // fwptypes.h:308-309, FWP_ACTION_FLAG_TERMINATING
    const uint FwpMatchEqual=0;                          // fwptypes.h:195
    const uint FwpUint8=1,FwpUint16=2,FwpUint32=3,FwpByteArray16Type=11,FwpByteBlobType=12,FwpSecurityDescriptorType=14; // fwptypes.h:113-128
    const byte IpProtoTcp=6;
    const uint LoopbackV4=0x7F000001;                    // 127.0.0.1, host byte order as FWP_UINT32 addresses are
    static readonly byte[] MappedLoopback=[0,0,0,0,0,0,0,0,0,0,0xFF,0xFF,127,0,0,1]; // ::ffff:127.0.0.1, network byte order
    const uint RpcCAuthnDefault=0xFFFFFFFF;              // RPC_C_AUTHN_DEFAULT
    const uint SddlRevision1=1;
    const int ErrorInvalidHandle=6,ErrorNotSupported=50;
    internal const int FwpEFilterNotFound=unchecked((int)0x80320003),FwpESubLayerNotFound=unchecked((int)0x80320007); // winerror.h
    static readonly Guid LayerAleAuthConnectV4=new("c38d57d1-05a7-4c33-904f-7fbceee60e82");   // fwpmu.h:457
    static readonly Guid LayerAleAuthConnectV6=new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");   // fwpmu.h:475
    static readonly Guid ConditionIpRemoteAddress=new("b235ae9a-1d64-49b8-a44c-5ff3d9095045"); // fwpmu.h:1417
    static readonly Guid ConditionIpProtocol=new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");      // fwpmu.h:1704
    static readonly Guid ConditionIpRemotePort=new("c35a604d-d22b-4e1a-91b4-68f674ee674b");    // fwpmu.h:1724
    static readonly Guid ConditionAleAppId=new("d78e1e87-8644-4ea5-9437-d809ecefc971");        // fwpmu.h:1865
    static readonly Guid ConditionAleUserId=new("af043a0a-b34d-4f86-979c-c90371af6e66");       // fwpmu.h:1887

    // x64 layouts, field for field from the SDK headers; OpenRgbPortLockTests pins each size and offset.
    [StructLayout(LayoutKind.Sequential)] internal struct FWPM_DISPLAY_DATA0{public IntPtr name,description;} // fwptypes.h:408, 16 bytes
    [StructLayout(LayoutKind.Sequential)] internal struct FWP_BYTE_BLOB{public uint size;public IntPtr data;}  // fwptypes.h:151, 16 bytes
    // fwptypes.h:165: a FWP_DATA_TYPE and a union of 8-byte pointers and smaller values, 16 bytes.
    [StructLayout(LayoutKind.Explicit)] internal struct FWP_VALUE0
    {[FieldOffset(0)] public uint type;[FieldOffset(8)] public byte uint8;[FieldOffset(8)] public ushort uint16;[FieldOffset(8)] public uint uint32;[FieldOffset(8)] public IntPtr pointer;}
    // fwptypes.h:232: the same shape as FWP_VALUE0, with more pointer arms, 16 bytes.
    [StructLayout(LayoutKind.Explicit)] internal struct FWP_CONDITION_VALUE0
    {[FieldOffset(0)] public uint type;[FieldOffset(8)] public byte uint8;[FieldOffset(8)] public ushort uint16;[FieldOffset(8)] public uint uint32;[FieldOffset(8)] public IntPtr pointer;}
    [StructLayout(LayoutKind.Sequential)] internal struct FWPM_FILTER_CONDITION0{public Guid fieldKey;public uint matchType;public FWP_CONDITION_VALUE0 conditionValue;} // fwpmtypes.h:463, 40 bytes
    [StructLayout(LayoutKind.Sequential)] internal struct FWPM_ACTION0{public uint type;public Guid filterType;} // fwpmtypes.h:453: the union is two GUIDs, 20 bytes
    // fwpmtypes.h:499-503: the union of a UINT64 and a GUID, 16 bytes aligned to 8.
    [StructLayout(LayoutKind.Explicit)] internal struct FWPM_FILTER_CONTEXT{[FieldOffset(0)] public ulong rawContext;[FieldOffset(0)] public Guid providerContextKey;}
    [StructLayout(LayoutKind.Sequential)] internal struct FWPM_FILTER0 // fwpmtypes.h:486, 200 bytes
    {
        public Guid filterKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FWP_VALUE0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition;
        public FWPM_ACTION0 action;
        public FWPM_FILTER_CONTEXT context;
        public IntPtr reserved;
        public ulong filterId;
        public FWP_VALUE0 effectiveWeight;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct FWPM_SUBLAYER0 // fwpmtypes.h:355, 72 bytes
    {public Guid subLayerKey;public FWPM_DISPLAY_DATA0 displayData;public uint flags;public IntPtr providerKey;public FWP_BYTE_BLOB providerData;public ushort weight;}
    [StructLayout(LayoutKind.Sequential)] internal struct FWPM_SESSION0 // fwpmtypes.h:129, 72 bytes
    {public Guid sessionKey;public FWPM_DISPLAY_DATA0 displayData;public uint flags;public uint txnWaitTimeoutInMSec;public uint processId;public IntPtr sid;public IntPtr username;public int kernelMode;}

    // fwpmu.h:3333-4261
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineOpen0(IntPtr serverName,uint authnService,IntPtr authIdentity,in FWPM_SESSION0 session,out EngineHandle engineHandle);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineClose0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionBegin0(EngineHandle engineHandle,uint flags);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionCommit0(EngineHandle engineHandle);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionAbort0(EngineHandle engineHandle);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmSubLayerAdd0(EngineHandle engineHandle,in FWPM_SUBLAYER0 subLayer,IntPtr sd);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmSubLayerGetByKey0(EngineHandle engineHandle,in Guid key,out IntPtr subLayer);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterAdd0(EngineHandle engineHandle,in FWPM_FILTER0 filter,IntPtr sd,out ulong id);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterGetByKey0(EngineHandle engineHandle,in Guid key,out IntPtr filter);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterDeleteByKey0(EngineHandle engineHandle,in Guid key);
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)] static extern uint FwpmGetAppIdFromFileName0(string fileName,out IntPtr appId);
    [DllImport("fwpuclnt.dll")] static extern void FwpmFreeMemory0(ref IntPtr p);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl,uint revision,out IntPtr sd,out uint size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
}
