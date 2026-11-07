using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.App;
/// <summary>Per-process GPU counters, one read per call; one caller at a time.</summary>
internal interface IGpuCounterSource
{
    /// <summary>Never throws.</summary>
    GpuCounterRead Read(double monotonicSeconds,double pollingIntervalSeconds);
    /// <summary>True when the last <see cref="Read"/> opened the query.</summary>
    bool LastReadOpened {get;}
    bool IsOpen {get;}
    /// <summary>Closes the query; the next read reopens it. Idempotent.</summary>
    void Close();
}

/// <summary>The real GPU counters: <see cref="GpuCounterReader"/> over PDH and DXGI. The reader turns an API exception into a failure with its 60 s retry.</summary>
internal sealed class WindowsGpuCounters : IGpuCounterSource
{
    private readonly GpuCounterReader reader=new(new PdhGpuApi());
    public bool LastReadOpened=>reader.LastReadOpened;
    public bool IsOpen=>reader.IsOpen;
    public GpuCounterRead Read(double monotonicSeconds,double pollingIntervalSeconds)=>reader.Read(monotonicSeconds,DateTimeOffset.UtcNow,pollingIntervalSeconds);
    public void Close()=>reader.Close();
}

/// <summary>
/// One PDH query holding the three wildcard GPU counters, added by their English paths so it works in any display language,
/// with one reused unmanaged buffer per counter; and the adapter list from a new DXGI factory per call. One caller at a time.
/// </summary>
internal sealed class PdhGpuApi : IGpuCounterApi
{
    internal const string EnginePath=@"\GPU Engine(*)\Utilization Percentage",DedicatedPath=@"\GPU Process Memory(*)\Dedicated Usage",SharedPath=@"\GPU Process Memory(*)\Shared Usage";
    internal const uint FormatDouble=0x00000200,FormatLarge=0x00000400;
    // x64 PDH_FMT_COUNTERVALUE_ITEM_W: the szName pointer, then PDH_FMT_COUNTERVALUE's CStatus and its 8-byte-aligned value union.
    private const int ItemBytes=24,StatusOffset=8,ValueOffset=16;
    // At most three PdhGetFormattedCounterArrayW calls per read; a third MoreData is a failure.
    private const int MaximumArrayCalls=3;
    private const int DxgiErrorNotFound=unchecked((int)0x887A0002);
    private static readonly string[] Paths=[EnginePath,DedicatedPath,SharedPath];
    private static readonly Guid FactoryId=typeof(IDXGIFactory1).GUID;
    private IntPtr query;
    private readonly IntPtr[] counters=new IntPtr[3],buffers=new IntPtr[3];
    private readonly uint[] bufferBytes=new uint[3];

    public uint Open()
    {
        var status=PdhOpenQueryW(null,IntPtr.Zero,out query);
        if(status!=0) {query=IntPtr.Zero;return status;}
        for(var i=0;i<Paths.Length;i++)
        {
            status=PdhAddEnglishCounterW(query,Paths[i],IntPtr.Zero,out counters[i]);
            if(status!=0) {Close();return status;}
        }
        return 0;
    }

    public uint Collect()=>PdhCollectQueryData(query);

    public GpuRawArray Read(GpuCounterKind kind)
    {
        var i=(int)kind;
        var format=kind==GpuCounterKind.Engine?FormatDouble:FormatLarge;
        // A data call passes the current buffer; a sizing call passes size 0 (the first read's first call is one).
        var size=bufferBytes[i];
        for(var call=1;;call++)
        {
            var bytes=size;
            var status=PdhGetFormattedCounterArrayW(counters[i],format,ref bytes,out var count,size==0?IntPtr.Zero:buffers[i]);
            if(status==GpuPdhStatus.MoreData)
            {
                if(call==MaximumArrayCalls)return new(GpuPdhStatus.MoreData,[]);
                // PDH's documentation: after a too-small non-zero buffer the returned size is unreliable, so ask again with
                // size 0; the size a size-0 call returns is trusted.
                if(size!=0) {size=0;continue;}
                // Zeroed before allocating, so a failed allocation leaves nothing for Close to free twice.
                Marshal.FreeHGlobal(buffers[i]);
                buffers[i]=IntPtr.Zero;
                bufferBytes[i]=0;
                buffers[i]=Marshal.AllocHGlobal((nint)bytes);
                bufferBytes[i]=bytes;
                size=bytes;
                continue;
            }
            return status==0?new(0,Items(buffers[i],count,format)):new(status,[]);
        }
    }

    /// <summary>Idempotent: closes a non-zero query, frees each buffer, and leaves every handle and pointer zero for the next <see cref="Open"/>.</summary>
    public void Close()
    {
        if(query!=IntPtr.Zero)PdhCloseQuery(query);
        query=IntPtr.Zero;
        for(var i=0;i<buffers.Length;i++)
        {
            counters[i]=IntPtr.Zero;
            if(buffers[i]!=IntPtr.Zero)Marshal.FreeHGlobal(buffers[i]);
            buffers[i]=IntPtr.Zero;
            bufferBytes[i]=0;
        }
    }

    public IReadOnlyList<GpuAdapterInfo>? Adapters()
    {
        object? created=null;
        try
        {
            if(CreateDXGIFactory1(in FactoryId,out created)<0||created is not IDXGIFactory1 factory)return null;
            var adapters=new List<GpuAdapterInfo>();
            for(uint index=0;;index++)
            {
                var result=factory.EnumAdapters1(index,out var adapter);
                try
                {
                    if(result==DxgiErrorNotFound)return adapters;
                    if(result<0||adapter is null||adapter.GetDesc1(out var description)<0)return null;
                    adapters.Add(new(((long)description.LuidHigh<<32)|description.LuidLow,description.Description,description.Flags));
                }
                finally
                {
                    if(adapter is not null)Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            if(created is not null)Marshal.ReleaseComObject(created);
        }
    }

    // Names point into the buffer, so each is copied before the next call reuses it.
    private static GpuRawItem[] Items(IntPtr buffer,uint count,uint format)
    {
        var items=new GpuRawItem[count];
        for(var n=0;n<items.Length;n++)
        {
            var item=buffer+n*ItemBytes;
            var raw=Marshal.ReadInt64(item,ValueOffset);
            items[n]=new(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item))??"",(uint)Marshal.ReadInt32(item,StatusOffset),format==FormatDouble?BitConverter.Int64BitsToDouble(raw):raw);
        }
        return items;
    }

    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhOpenQueryW(string? source,IntPtr user,out IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhAddEnglishCounterW(IntPtr query,string path,IntPtr user,out IntPtr counter);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter,uint format,ref uint bytes,out uint count,IntPtr buffer);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(in Guid riid,[MarshalAs(UnmanagedType.Interface)] out object factory);

    // dxgi.h vtables in order after IUnknown: IDXGIObject, then IDXGIFactory or IDXGIAdapter, then the "1" methods. Slots
    // before the methods used are declared only to keep the order; [PreserveSig] keeps every HRESULT a return value.
    [ComImport,Guid("770aae78-f26f-4dba-a829-253c83d1b387"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(IntPtr name,uint size,IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(IntPtr name,IntPtr unknown);
        [PreserveSig] int GetPrivateData(IntPtr name,IntPtr size,IntPtr data);
        [PreserveSig] int GetParent(IntPtr riid,IntPtr parent);
        [PreserveSig] int EnumAdapters(uint adapter,IntPtr result);
        [PreserveSig] int MakeWindowAssociation(IntPtr window,uint flags);
        [PreserveSig] int GetWindowAssociation(IntPtr window);
        [PreserveSig] int CreateSwapChain(IntPtr device,IntPtr description,IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module,IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint adapter,out IDXGIAdapter1? result);
    }
    [ComImport,Guid("29038f61-3839-4626-91fd-086879011a05"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(IntPtr name,uint size,IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(IntPtr name,IntPtr unknown);
        [PreserveSig] int GetPrivateData(IntPtr name,IntPtr size,IntPtr data);
        [PreserveSig] int GetParent(IntPtr riid,IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output,IntPtr result);
        [PreserveSig] int GetDesc(IntPtr description);
        [PreserveSig] int CheckInterfaceSupport(IntPtr name,IntPtr version);
        [PreserveSig] int GetDesc1(out AdapterDescription1 description);
    }
    // DXGI_ADAPTER_DESC1: 312 bytes on x64, Flags at offset 304.
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct AdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;
        public uint VendorId,DeviceId,SubSysId,Revision;
        public nuint DedicatedVideoMemory,DedicatedSystemMemory,SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
