using System.Diagnostics.CodeAnalysis;
using System.Globalization;
namespace DeadWare.Core;
public enum GpuCounterKind{Engine,Dedicated,Shared}
public sealed record GpuCounterInstance(int Pid,long Luid,int Phys,int? Engine,string? Type)
{
    public static bool TryParse(string? name,GpuCounterKind kind,[NotNullWhen(true)] out GpuCounterInstance? instance)
    {
        instance=null;
        if(name is null)return false;
        var rest=name.AsSpan();
        if(!Field(ref rest,"pid_","_luid_0x",out var pidText)||!Decimal(pidText,out var pid))return false;
        if(!Field(ref rest,"","_0x",out var hiText)||!Hex(hiText,out var hi))return false;
        if(!Field(ref rest,"","_phys_",out var loText)||!Hex(loText,out var lo))return false;
        var luid=((long)hi<<32)|lo;
        if(kind!=GpuCounterKind.Engine)
        {
            if(!Decimal(rest,out var memoryPhys))return false;
            instance=new(pid,luid,memoryPhys,null,null);
            return true;
        }
        if(!Field(ref rest,"","_eng_",out var physText)||!Decimal(physText,out var phys))return false;
        if(!Field(ref rest,"","_engtype_",out var engineText)||!Decimal(engineText,out var engine))return false;
        instance=new(pid,luid,phys,engine,rest.ToString());
        return true;
    }
    static bool Field(ref ReadOnlySpan<char> text,string prefix,string suffix,out ReadOnlySpan<char> field)
    {
        field=default;
        if(!text.StartsWith(prefix,StringComparison.Ordinal))return false;
        var body=text[prefix.Length..];
        var end=body.IndexOf(suffix,StringComparison.Ordinal);
        if(end<0)return false;
        field=body[..end];
        text=body[(end+suffix.Length)..];
        return true;
    }
    static bool Decimal(ReadOnlySpan<char> text,out int value)=>int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out value);
    static bool Hex(ReadOnlySpan<char> text,out uint value)
    {
        value=0;
        return text.Length is >=1 and <=8&&uint.TryParse(text,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out value);
    }
}
// pdhmsg.h values
public static class GpuPdhStatus
{
    public const uint CStatusValidData=0x00000000,CStatusNewData=0x00000001,CStatusNoInstance=0x800007D1,MoreData=0x800007D2,NoData=0x800007D5,CalcNegativeDenominator=0x800007D6,CalcNegativeValue=0x800007D8,CStatusNoObject=0xC0000BB8,CStatusNoCounter=0xC0000BB9,CStatusInvalidData=0xC0000BBA,InvalidData=0xC0000BC6;
}
/// <summary>Percent for engines, bytes for memory (PDH_FMT_LARGE values as double, exact below 2^53).</summary>
public readonly record struct GpuRawItem(string Name,uint CStatus,double Value);
public sealed record GpuRawArray(uint Status,IReadOnlyList<GpuRawItem> Items);
public sealed record GpuAdapterInfo(long Luid,string Description,uint Flags)
{
    public const uint SoftwareFlag=2;
    public bool Software=>(Flags&SoftwareFlag)!=0;
}
public interface IGpuCounterApi
{
    /// <summary>The first failing status of PdhOpenQueryW and the three adds, or 0. After a failing add the query is already closed.</summary>
    uint Open();
    uint Collect();
    GpuRawArray Read(GpuCounterKind kind);
    /// <summary>Null on a DXGI failure.</summary>
    IReadOnlyList<GpuAdapterInfo>? Adapters();
    /// <summary>Idempotent.</summary>
    void Close();
}
public sealed record GpuAdapterName(int? Number,string? Description)
{
    public static GpuAdapterName Unknown{get;}=new(null,null);
    public string Name=>Number is{} n?$"GPU {n}":"GPU ?";
    public string Label(string type)=>$"{Name} · {(type.Length==0?"Engine":type)}";
}
public sealed record GpuEngineItem(GpuCounterInstance Instance,bool Ready,double Percent);
public sealed record GpuMemoryItem(GpuCounterInstance Instance,bool Ready,long Bytes);
public sealed record GpuCounterRead(ProcessColumnState State,DateTimeOffset UtcTime,double MonotonicSeconds,DateTimeOffset? PreviousUtcTime,double? IntervalSeconds,IReadOnlyList<GpuEngineItem> Engines,IReadOnlyList<GpuMemoryItem> Dedicated,IReadOnlyList<GpuMemoryItem> Shared,IReadOnlyDictionary<long,GpuAdapterName> Adapters,int Instances,int Skipped)
{
    public bool EnginesReady{get;init;}=true;
    public bool DedicatedReady{get;init;}=true;
    public bool SharedReady{get;init;}=true;
    public bool Credit{get;init;}=true;
    public static GpuCounterRead Unavailable(string status,double monotonicSeconds,DateTimeOffset utcTime)=>
        new(new ProcessColumnState(false,status),utcTime,monotonicSeconds,null,null,[],[],[],new Dictionary<long,GpuAdapterName>(),0,0);
}
public static class GpuCounterStatus
{
    public const string Unavailable="GPU counters unavailable",Unreadable="GPU counters unreadable",TooMany="Too many GPU counter instances";
    public static string Error(uint status)=>$"GPU counters unavailable (error 0x{status:X8})";
}
public sealed class GpuCounterReader(IGpuCounterApi api)
{
    public const double RetrySeconds=60,NotReadySeconds=60,AdapterRereadSeconds=60;
    public const int MaximumItems=65536;
    sealed record Collection(DateTimeOffset UtcTime,double MonotonicSeconds,double PollingIntervalSeconds);
    string failure=GpuCounterStatus.Unavailable;
    double? retryAt,notReadyStart,adaptersReadAt;
    bool adaptersFailed;
    Collection? previous;
    Dictionary<long,GpuAdapterName> adapters=[];
    public bool IsOpen{get;private set;}
    public bool LastReadOpened{get;private set;}
    public GpuCounterRead Read(double monotonicSeconds,DateTimeOffset utcTime,double pollingIntervalSeconds)
    {
        LastReadOpened=false;
        if(!IsOpen)
        {
            if(retryAt is{} due&&monotonicSeconds<due)return GpuCounterRead.Unavailable(failure,monotonicSeconds,utcTime);
            uint openStatus;
            try{openStatus=api.Open();}
            catch(Exception){return Fail(GpuCounterStatus.Unavailable,monotonicSeconds,utcTime);}
            if(openStatus!=0)return Fail(openStatus is GpuPdhStatus.CStatusNoObject or GpuPdhStatus.CStatusNoCounter?GpuCounterStatus.Unavailable:GpuCounterStatus.Error(openStatus),monotonicSeconds,utcTime);
            IsOpen=true;
            LastReadOpened=true;
            retryAt=null;
            ReadAdapters(monotonicSeconds);
        }
        uint collectStatus;
        try{collectStatus=api.Collect();}
        catch(Exception){return Fail(GpuCounterStatus.Unavailable,monotonicSeconds,utcTime);}
        if(collectStatus is GpuPdhStatus.NoData or GpuPdhStatus.CStatusNoInstance)return GpuCounterRead.Unavailable(GpuCounterStatus.Unavailable,monotonicSeconds,utcTime);
        if(collectStatus is not (GpuPdhStatus.CStatusValidData or GpuPdhStatus.CStatusNewData))return Fail(GpuCounterStatus.Error(collectStatus),monotonicSeconds,utcTime);
        DateTimeOffset? previousUtc=null;
        double? interval=null;
        if(previous is{} last)
        {
            var elapsed=monotonicSeconds-last.MonotonicSeconds;
            if(elapsed>0&&elapsed<=PollingPolicy.FreshnessSeconds(Math.Max(last.PollingIntervalSeconds,pollingIntervalSeconds))){previousUtc=last.UtcTime;interval=elapsed;}
        }
        previous=new(utcTime,monotonicSeconds,pollingIntervalSeconds);
        var arrays=new GpuRawArray[3];
        try{for(var i=0;i<arrays.Length;i++)arrays[i]=api.Read((GpuCounterKind)i);}
        catch(Exception){return Fail(GpuCounterStatus.Unavailable,monotonicSeconds,utcTime);}
        var ready=new bool[3];
        uint? benign=null;
        for(var i=0;i<arrays.Length;i++)
        {
            ready[i]=true;
            var status=arrays[i].Status;
            if(status is 0 or GpuPdhStatus.NoData or GpuPdhStatus.CStatusNoInstance)continue;
            if(status is GpuPdhStatus.CStatusInvalidData or GpuPdhStatus.InvalidData or GpuPdhStatus.CalcNegativeDenominator or GpuPdhStatus.CalcNegativeValue){ready[i]=false;benign??=status;continue;}
            return Fail(GpuCounterStatus.Error(status),monotonicSeconds,utcTime);
        }
        if(benign is{} benignStatus)
        {
            notReadyStart??=monotonicSeconds;
            if(monotonicSeconds-notReadyStart>=NotReadySeconds)return Fail(GpuCounterStatus.Error(benignStatus),monotonicSeconds,utcTime);
        }
        else notReadyStart=null;
        var engineArray=arrays[0];
        var instances=arrays[0].Items.Count+arrays[1].Items.Count+arrays[2].Items.Count;
        ProcessColumnState state;
        var skipped=0;
        var engines=new List<GpuEngineItem>();
        var dedicated=new List<GpuMemoryItem>();
        var shared=new List<GpuMemoryItem>();
        if(arrays.Any(a=>a.Items.Count>MaximumItems))state=new(false,GpuCounterStatus.TooMany);
        else if(engineArray.Status is GpuPdhStatus.NoData or GpuPdhStatus.CStatusNoInstance||(engineArray.Status==0&&engineArray.Items.Count==0))state=new(false,GpuCounterStatus.Unavailable);
        else
        {
            foreach(var item in engineArray.Items)
            {
                if(GpuCounterInstance.TryParse(item.Name,GpuCounterKind.Engine,out var instance))engines.Add(new(instance,IsReady(item),IsReady(item)?item.Value:0));
                else skipped++;
            }
            skipped+=Memory(arrays[1],GpuCounterKind.Dedicated,dedicated)+Memory(arrays[2],GpuCounterKind.Shared,shared);
            state=skipped*2>instances?new(false,GpuCounterStatus.Unreadable):ProcessColumnState.Ready;
        }
        RereadAdaptersIfNeeded(monotonicSeconds,engines,dedicated,shared);
        return new GpuCounterRead(state,utcTime,monotonicSeconds,previousUtc,interval,engines,dedicated,shared,adapters,instances,skipped)
        {
            EnginesReady=ready[0],DedicatedReady=ready[1],SharedReady=ready[2]
        };
    }
    public void Close()
    {
        CloseApi();
        retryAt=null;
        LastReadOpened=false;
    }
    static bool IsReady(GpuRawItem item)=>item.CStatus is GpuPdhStatus.CStatusValidData or GpuPdhStatus.CStatusNewData;
    static int Memory(GpuRawArray array,GpuCounterKind kind,List<GpuMemoryItem> into)
    {
        var skipped=0;
        foreach(var item in array.Items)
        {
            if(GpuCounterInstance.TryParse(item.Name,kind,out var instance))into.Add(new(instance,IsReady(item),IsReady(item)?(long)item.Value:0));
            else skipped++;
        }
        return skipped;
    }
    GpuCounterRead Fail(string status,double monotonicSeconds,DateTimeOffset utcTime)
    {
        CloseApi();
        failure=status;
        retryAt=monotonicSeconds+RetrySeconds;
        return GpuCounterRead.Unavailable(status,monotonicSeconds,utcTime);
    }
    void CloseApi()
    {
        try{api.Close();}catch(Exception){}
        IsOpen=false;
        previous=null;
        notReadyStart=null;
        adaptersReadAt=null;
        adaptersFailed=false;
        adapters=[];
    }
    void RereadAdaptersIfNeeded(double monotonicSeconds,List<GpuEngineItem> engines,List<GpuMemoryItem> dedicated,List<GpuMemoryItem> shared)
    {
        if(monotonicSeconds-(adaptersReadAt??double.NegativeInfinity)<AdapterRereadSeconds)return;
        if(adaptersFailed||engines.Any(e=>!adapters.ContainsKey(e.Instance.Luid))||dedicated.Any(m=>!adapters.ContainsKey(m.Instance.Luid))||shared.Any(m=>!adapters.ContainsKey(m.Instance.Luid)))ReadAdapters(monotonicSeconds);
    }
    void ReadAdapters(double monotonicSeconds)
    {
        adaptersReadAt=monotonicSeconds;
        IReadOnlyList<GpuAdapterInfo>? list;
        try{list=api.Adapters();}
        catch(Exception){list=null;}
        adaptersFailed=list is null;
        var map=new Dictionary<long,GpuAdapterName>();
        var number=0;
        foreach(var adapter in list??[])map[adapter.Luid]=adapter.Software?new(null,adapter.Description):new(number++,adapter.Description);
        adapters=map;
    }
}
