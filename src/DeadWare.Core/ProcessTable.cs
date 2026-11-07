namespace DeadWare.Core;
public static class ProcessCopy
{
    public const string OffTitle="Per-process monitoring is off.";
    public const string FirstSample="first sample";
    public const string Stale="stale reading";
    public const string NetworkNotMatched="traffic not matched to this process";
    public const string DiskNotMatched="disk activity not matched to this process";
    /// <summary>What turning on Per-process monitoring costs; <paramref name="history"/> adds what it saves while usage history is recorded (0.14.0).</summary>
    public static string ScanSentence(bool elevated,bool diskColumns,bool scanLoop,bool history=false)=>
        "Turning it on adds one Windows process scan per poll"+(scanLoop?" (at most one a second)":"")+(elevated&&diskColumns?", one GPU counter read per poll and a disk trace":" and one GPU counter read per poll")+"."+
        (history?" While usage history is recorded, it also keeps the busiest apps for 30 days.":"");
    /// <summary>The engine cell's tooltip: the adapter description, when there is one, and "&lt;type&gt; engine" ("engine" for an empty type).</summary>
    public static string EngineTip(GpuEngineUse engine)
    {
        string kind=engine.Type.Length==0?"engine":engine.Type+" engine";
        return string.IsNullOrEmpty(engine.Adapter)?kind:engine.Adapter+" · "+kind;
    }
    public static string? Banner(bool elevated,bool diskColumns)=>elevated?null:diskColumns?"Disk and network need administrator":"Network needs administrator";
    public static string CapNote(int total)=>$"Showing 4,096 of {total:N0} processes";
    public static IReadOnlyList<string> Footer(bool diskColumns)
    {
        var lines=new List<string>
        {
            "CPU is the share of all logical processors, as in Task Manager's Details tab, so it sums to at most 100 %.",
            "Memory is the private working set, Task Manager's \"Memory (private working set)\".",
            "I/O counts all file, pipe and device transfers, including cached reads, and is not disk activity.",
        };
        if(diskColumns)lines.Add("Disk is physical drive transfers. Delayed cache writes are credited to System.");
        lines.Add("GPU is the app's busiest GPU engine, as in Task Manager; GPU memory is dedicated video memory.");
        return lines;
    }
}
/// <summary>One app: every process sharing an image name. Each rate is the sum of the members that know it, null only when none does.</summary>
public sealed record ProcessAppRow(string Key,string Name,IReadOnlyList<ProcessSample> Members)
{
    public double? Cpu=>Sum("cpu");
    public long Memory=>Members.Sum(m=>m.PrivateBytes);
    public double? Io=>Sum("io");
    public double? IoRead=>Sum("ioread");
    public double? IoWrite=>Sum("iowrite");
    public double? Disk=>Sum("disk");
    public double? DiskRead=>Sum("diskread");
    public double? DiskWrite=>Sum("diskwrite");
    public double? Network=>Sum("network");
    public double? Download=>Sum("download");
    public double? Upload=>Sum("upload");
    sealed record GpuUseCache(IReadOnlyList<ProcessSample> Members,double? Percent,GpuEngineUse? Busiest);
    // GpuUse runs once per member list rather than on each of the several reads per row per render and per sort comparison. Keyed by the list,
    // so a copy made with a new list works its own out; Equals and GetHashCode below leave it out, so equality stays the positional members'.
    GpuUseCache? gpuUse;
    /// <summary>The busiest engine across members, with the clamped sum as its Percent; see <see cref="ProcessGrouping.GpuUse"/>.</summary>
    GpuUseCache Use
    {
        get
        {
            if(gpuUse is{} cached&&ReferenceEquals(cached.Members,Members))return cached;
            var (percent,busiest)=ProcessGrouping.GpuUse(Members);
            return gpuUse=new(Members,percent,busiest);
        }
    }
    public double? Gpu=>Use.Percent;
    public GpuEngineUse? GpuBusiest=>Use.Busiest;
    public bool Equals(ProcessAppRow? other)=>other is not null&&Key==other.Key&&Name==other.Name&&EqualityComparer<IReadOnlyList<ProcessSample>>.Default.Equals(Members,other.Members);
    public override int GetHashCode()=>HashCode.Combine(Key,Name,Members);
    public double? GpuMemory=>Sum("gpumemory");
    public double? GpuShared=>Sum("gpushared");
    double? Sum(string key)
    {
        double? total=null;
        foreach(var m in Members)if(ProcessGrouping.Value(m,key) is{} v)total=(total??0)+v;
        return total;
    }
}
public static class ProcessGrouping
{
    public static string Key(string name)=>UsageMetrics.NormalizeName(name).ToUpperInvariant();
    public static IReadOnlyList<ProcessAppRow> Group(IReadOnlyList<ProcessSample> processes)
    {
        var order=new List<string>();
        var groups=new Dictionary<string,(string Name,List<ProcessSample> Members)>(StringComparer.Ordinal);
        foreach(var p in processes)
        {
            var key=Key(p.Name);
            if(!groups.TryGetValue(key,out var g)){g=(UsageMetrics.NormalizeName(p.Name),[]);groups[key]=g;order.Add(key);}
            g.Members.Add(p);
        }
        return order.Select(k=>new ProcessAppRow(k,groups[k].Name,groups[k].Members)).ToList();
    }
    /// <summary>Like the Network page: name contains, or PID text contains; the query is trimmed and empty matches all.</summary>
    public static bool Matches(ProcessAppRow row,string query)
    {
        query=(query??"").Trim();
        return query.Length==0||row.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||row.Members.Any(m=>m.Pid.ToString().Contains(query));
    }
    /// <summary>Each engine's Percent summed across members (grouped by LUID, phys, engine); the busiest sum wins, ties in engine order, and is clamped to 100.
    /// Both are null when every member's list is null; (0, null) when some list is non-null but no member has an entry.</summary>
    public static (double? Percent,GpuEngineUse? Busiest) GpuUse(IReadOnlyList<ProcessSample> members)
    {
        bool known=false;
        var sums=new Dictionary<(long,int,int),(GpuEngineUse Entry,double Sum)>();
        foreach(var m in members)
        {
            if(m.GpuEngines is not{} list)continue;
            known=true;
            foreach(var e in list)
            {
                var k=(e.Luid,e.Phys,e.Engine);
                sums[k]=sums.TryGetValue(k,out var t)?(t.Entry,t.Sum+e.Percent):(e,e.Percent);
            }
        }
        if(!known)return (null,null);
        (GpuEngineUse Entry,double Sum)? best=null;
        foreach(var t in sums.Values)
            if(best is null||t.Sum>best.Value.Sum||(t.Sum==best.Value.Sum&&GpuProcessJoin.CompareEngineOrder(t.Entry,best.Value.Entry)<0))best=t;
        if(best is null)return (0,null);
        double percent=Math.Min(100,best.Value.Sum);
        return (percent,best.Value.Entry with{Percent=percent});
    }
    /// <summary>The GPU engine column's text: null when the GPU value is unknown, "" for a known zero.</summary>
    public static string? EngineText(ProcessSample p)=>p.GpuPercent is null?null:p.GpuEngine??"";
    public static string? EngineText(ProcessAppRow r)=>r.Gpu is null?null:r.GpuBusiest?.Label??"";
    public static double? Value(ProcessSample p,string key)=>key switch
    {
        "pid"=>p.Pid,
        "cpu"=>p.CpuPercent,
        "memory"=>p.PrivateBytes,
        "io"=>Add(p.IoReadPerSecond,p.IoWritePerSecond),
        "ioread"=>p.IoReadPerSecond,
        "iowrite"=>p.IoWritePerSecond,
        "disk"=>Add(p.DiskReadPerSecond,p.DiskWritePerSecond),
        "diskread"=>p.DiskReadPerSecond,
        "diskwrite"=>p.DiskWritePerSecond,
        "network"=>Add(p.DownloadPerSecond,p.UploadPerSecond),
        "download"=>p.DownloadPerSecond,
        "upload"=>p.UploadPerSecond,
        "gpu"=>p.GpuPercent,
        "gpumemory"=>p.GpuDedicatedBytes,
        "gpushared"=>p.GpuSharedBytes,
        _=>null,
    };
    public static double? Value(ProcessAppRow r,string key)=>key switch
    {
        "pid"=>r.Members.Count==0?null:r.Members.Min(m=>m.Pid),
        "cpu"=>r.Cpu,
        "memory"=>r.Memory,
        "io"=>r.Io,
        "ioread"=>r.IoRead,
        "iowrite"=>r.IoWrite,
        "disk"=>r.Disk,
        "diskread"=>r.DiskRead,
        "diskwrite"=>r.DiskWrite,
        "network"=>r.Network,
        "download"=>r.Download,
        "upload"=>r.Upload,
        "gpu"=>r.Gpu,
        "gpumemory"=>r.GpuMemory,
        "gpushared"=>r.GpuShared,
        _=>null,
    };
    static double? Add(double? a,double? b)=>a is null||b is null?null:a+b;
}
public sealed record ProcessColumnChoice(string Key,string Header,bool Checked,bool Enabled,string Label);
public static class ProcessTableColumns
{
    static readonly string[] PickerOrder=["cpu","memory","io","disk","network","gpu","gpumemory","pid","ioread","iowrite","diskread","diskwrite","download","upload","gpuengine","gpushared"];
    public static string Header(string key)=>key switch
    {
        "name"=>"NAME","pid"=>"PID","cpu"=>"CPU","memory"=>"MEMORY","io"=>"I/O","ioread"=>"I/O READ","iowrite"=>"I/O WRITE",
        "disk"=>"DISK","diskread"=>"DISK READ","diskwrite"=>"DISK WRITE","network"=>"NETWORK","download"=>"↓ DOWNLOAD","upload"=>"↑ UPLOAD",
        "gpu"=>"GPU","gpuengine"=>"GPU ENGINE","gpumemory"=>"GPU MEMORY","gpushared"=>"SHARED GPU MEMORY",
        _=>key.ToUpperInvariant(),
    };
    /// <summary>Text columns sort ascending on the first header click; the numeric ones start descending.</summary>
    public static bool AscendingFirst(string key)=>key is "name" or "gpuengine";
    public static IReadOnlyList<string> Visible(IReadOnlyList<string> saved,bool elevated,bool diskColumns)=>
        ProcessColumnKeys.All.Where(k=>saved.Contains(k)&&Allowed(k,elevated,diskColumns)).ToList();
    public static IReadOnlyList<ProcessColumnChoice> Picker(IReadOnlyList<string> saved,bool elevated,bool diskColumns)=>
        PickerOrder.Where(k=>diskColumns||!ProcessColumnKeys.DiskKeys.Contains(k)).Select(k=>
        {
            bool admin=ProcessColumnKeys.AdministratorOnly.Contains(k);
            return new ProcessColumnChoice(k,Header(k),saved.Contains(k),!admin||elevated,admin&&!elevated?Header(k)+" (administrator)":Header(k));
        }).ToList();
    public static (string Key,bool Descending) EffectiveSort(string? key,bool descending,IReadOnlyList<string> visible)
    {
        if(key is not null&&(key=="name"||visible.Contains(key)))return (key,descending);
        return visible.Contains("cpu")?("cpu",true):("name",false);
    }
    static bool Allowed(string k,bool elevated,bool diskColumns)=>(elevated||!ProcessColumnKeys.AdministratorOnly.Contains(k))&&(diskColumns||!ProcessColumnKeys.DiskKeys.Contains(k));
}
public static class ProcessSort
{
    public static int Compare(ProcessAppRow a,ProcessAppRow b,string key,bool descending)=>
        Core(key,descending,ProcessGrouping.Value(a,key),ProcessGrouping.Value(b,key),a.Name,b.Name,a.Members.Count==0?0:a.Members.Min(m=>m.Pid),b.Members.Count==0?0:b.Members.Min(m=>m.Pid),
            key=="gpuengine"?ProcessGrouping.EngineText(a):null,key=="gpuengine"?ProcessGrouping.EngineText(b):null);
    public static int Compare(ProcessSample a,ProcessSample b,string key,bool descending)=>
        Core(key,descending,ProcessGrouping.Value(a,key),ProcessGrouping.Value(b,key),UsageMetrics.NormalizeName(a.Name),UsageMetrics.NormalizeName(b.Name),a.Pid,b.Pid,
            key=="gpuengine"?ProcessGrouping.EngineText(a):null,key=="gpuengine"?ProcessGrouping.EngineText(b):null);
    static int Core(string key,bool descending,double? x,double? y,string nameA,string nameB,int pidA,int pidB,string? textA,string? textB)
    {
        int c;
        if(key=="name")c=descending?-StringComparer.OrdinalIgnoreCase.Compare(nameA,nameB):StringComparer.OrdinalIgnoreCase.Compare(nameA,nameB);
        else if(key=="gpuengine")
        {
            // "" sorts after every named engine and null after "", in both directions; only the comparison of two names flips.
            int rank(string? t)=>t is null?2:t.Length==0?1:0;
            c=rank(textA).CompareTo(rank(textB));
            if(c==0&&textA is{Length:>0}){c=StringComparer.OrdinalIgnoreCase.Compare(textA,textB);if(descending)c=-c;}
        }
        else if(x is null||y is null)c=x is null?(y is null?0:1):-1;
        else{c=x.Value.CompareTo(y.Value);if(descending)c=-c;}
        if(c!=0)return c;
        c=StringComparer.OrdinalIgnoreCase.Compare(nameA,nameB);
        return c!=0?c:pidA.CompareTo(pidB);
    }
}
