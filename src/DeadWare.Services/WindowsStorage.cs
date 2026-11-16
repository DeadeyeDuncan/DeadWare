using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Per-poll storage fast path: drive enumeration plus throughput, activity and free space.</summary>
internal interface IStorageFastSource
{
    IReadOnlyList<StorageDrive> Drives(double now);
    void Read(IReadOnlyList<StorageDrive> drives,Dictionary<string,double?> values,out string? error);
}

internal sealed record WmiDisk(string? Serial,string Model,int Index,string BusType,long Size,bool Removable,IReadOnlyList<(string Letter,string Label,long Size,long Free)> Volumes);

/// <summary>One physical disk's rate counters (bytes/s read, bytes/s write, % idle); NaN is no reading.</summary>
internal sealed class DiskCounters(Func<double> read,Func<double> write,Func<double> idle,IDisposable? owner=null) : IDisposable
{
    public double Read()=>read();
    public double Write()=>write();
    public double Idle()=>idle();
    public void Dispose()=>owner?.Dispose();
}

/// <summary>
/// The disks' counters from one open, by drive index. <see cref="Collect"/> runs once per poll before any drive is read, so counters
/// that share one sample (one PDH query for every disk) take it there. A set that <see cref="WatchesDrives"/> sets
/// <see cref="DrivesChanged"/> when the disks it sees differ from the previous collection's, which lets drive enumeration run rarely.
/// </summary>
internal class DiskCounterSet : Dictionary<string,DiskCounters>, IDisposable
{
    public DiskCounterSet():base(StringComparer.Ordinal) {}
    public virtual void Collect() {}
    public virtual bool WatchesDrives=>false;
    public bool DrivesChanged {get;protected set;}
    public virtual void Dispose() {foreach(var c in Values)c.Dispose();}
}

/// <summary>
/// WMI enumeration and PhysicalDisk counters; never throws to the collector. The first enumeration runs on the poll; later ones run
/// in the background and land on a later poll, because one takes about a second of WMI work (measured with 7 drives). They run every
/// <see cref="EnumerationSeconds"/>, or, while the counters watch the disks, when the disks change and every
/// <see cref="WatchedEnumerationSeconds"/>. Free space is read every <see cref="FreeSpaceSeconds"/> and keeps the drive list's volumes
/// current between enumerations.
/// </summary>
internal sealed class WindowsStorage : IStorageFastSource, IDisposable
{
    internal const double EnumerationSeconds=60,WatchedEnumerationSeconds=600,ErrorAfterSeconds=60,FreeSpaceSeconds=10;
    /// <summary>A disk change is enumerated once the disks have stayed the same this long (a new drive gets its letters a moment after
    /// it appears), and once more <see cref="ChangeRecheckSeconds"/> later in case WMI had not caught up.</summary>
    internal const double ChangeSettleSeconds=2,ChangeRecheckSeconds=10;
    private sealed record Enumeration(IReadOnlyList<StorageDrive>? Drives,string? Error);
    private readonly Func<IEnumerable<WmiDisk>> enumerate;
    private readonly Func<DiskCounterSet> openCounters;
    private readonly Action<Action> background;
    private readonly Func<string,long?> freeSpace;
    private IReadOnlyList<StorageDrive> drives=[];
    private double now;
    private double? enumerationAttempted,counterAttempted,failingSince,changeSeen,recheckAt,freeReadAt;
    private bool enumerationFailing,countersFailing;
    private string? enumerationError,counterError;
    private DiskCounterSet? counters;
    // A background enumeration's result, adopted by the next Drives call; running is 1 while one is in flight.
    private Enumeration? finished;
    private int running;
    // The last free space read per volume key; null where Windows could not say.
    private readonly Dictionary<string,long?> free=new(StringComparer.Ordinal);

    public WindowsStorage():this(Enumerate,()=>new PdhDiskCounterSet(),work=>_=Task.Run(work)) {}
    /// <param name="background">Runs a later enumeration off the poll; by default it runs at once, as the tests need.</param>
    /// <param name="freeSpace">A volume's free bytes by letter, null when unknown; by default Windows' figure.</param>
    internal WindowsStorage(Func<IEnumerable<WmiDisk>> enumerate,Func<DiskCounterSet> openCounters,Action<Action>? background=null,Func<string,long?>? freeSpace=null)
    {this.enumerate=enumerate;this.openCounters=openCounters;this.background=background??(work=>work());this.freeSpace=freeSpace??FreeSpace;}

    private static long? FreeSpace(string letter)
    {
        try {return new System.IO.DriveInfo(letter+":\\").TotalFreeSpace;}
        catch(Exception) {return null;}
    }

    /// <summary>"0 C: D:" → "0", "2" → "2", "_Total" → null.</summary>
    internal static string? InstanceDriveIndex(string instanceName)
    {
        var first=instanceName.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null&&first.All(char.IsAsciiDigit)?first:null;
    }

    internal static IReadOnlyList<StorageDrive> Parse(IEnumerable<WmiDisk> disks)=>
        disks.OrderBy(d=>d.Index).Select(d=>new StorageDrive(StorageProjection.DriveId(d.Serial,d.Model,d.Index),StorageProjection.Normalize(d.Serial),d.Model,d.Index,d.BusType,d.Size,d.Removable,
            d.Volumes.Select(v=>new StorageVolume(v.Letter,v.Label,v.Size,v.Free)).ToList())
        {IdentityNote=StorageProjection.Normalize(d.Serial) is null?"no serial number":d.Removable?"removable drive":""}).ToList();

    /// <summary>Also sets the clock that <see cref="Read"/> uses for its retry and error grace.</summary>
    public IReadOnlyList<StorageDrive> Drives(double now)
    {
        this.now=now;
        if(Interlocked.Exchange(ref finished,null) is {} done)Adopt(done);
        if(Volatile.Read(ref running)!=0||!EnumerationDue(now))return drives;
        // The attempt time is recorded on failure too, so a broken WMI is retried once per interval, not every poll.
        bool first=enumerationAttempted is null,byChange=changeSeen is {} seen&&now-seen>=ChangeSettleSeconds;
        enumerationAttempted=now;changeSeen=null;recheckAt=byChange?now+ChangeRecheckSeconds:null;
        if(first)Adopt(EnumerateDrives());
        else
        {
            Volatile.Write(ref running,1);
            background(()=>{var result=EnumerateDrives();Volatile.Write(ref finished,result);Volatile.Write(ref running,0);});
            // A background that ran at once has already finished.
            if(Interlocked.Exchange(ref finished,null) is {} ran)Adopt(ran);
        }
        return drives;
    }

    private bool EnumerationDue(double now)
    {
        if(enumerationAttempted is not {} last)return true;
        if(changeSeen is {} seen&&now-seen>=ChangeSettleSeconds||recheckAt is {} at&&now>=at)return true;
        return now-last>=(counters?.WatchesDrives==true?WatchedEnumerationSeconds:EnumerationSeconds);
    }

    private Enumeration EnumerateDrives()
    {
        try {return new(Parse(enumerate()),null);}
        catch(Exception e) {return new(null,$"Drive enumeration failed ({e.GetType().Name})");}
    }

    private void Adopt(Enumeration result)
    {
        if(result.Drives is not {} list){enumerationFailing=true;enumerationError=result.Error;return;}
        var previous=drives.Select(d=>d.Index).Order().ToArray();
        drives=list;enumerationFailing=false;freeReadAt=null;
        // Counters survive a re-enumeration; they are re-opened only when the set of physical disks changed.
        if(!previous.SequenceEqual(drives.Select(d=>d.Index).Order())){DisposeCounters();counterAttempted=null;}
    }

    public void Read(IReadOnlyList<StorageDrive> list,Dictionary<string,double?> values,out string? error)
    {
        // Open at most once per Read, and after a failure only once per enumeration interval.
        bool opened=false;
        if(counters is null&&list.Count>0&&(counterAttempted is not {} tried||now-tried>=EnumerationSeconds))
        {
            counterAttempted=now;
            try {counters=openCounters();countersFailing=false;opened=true;}
            catch(Exception e) {countersFailing=true;counterError=$"Disk counters unavailable ({e.GetType().Name})";}
        }
        // One sample of every disk per poll. A set opened in this same poll has only primed its rates, which over a near-zero interval
        // would read 0 or a spike: publish null instead.
        if(!opened&&counters is not null)
        {
            try {counters.Collect();if(counters.DrivesChanged)changeSeen=now;}
            catch(Exception e) {countersFailing=true;counterError=$"Disk counters unavailable ({e.GetType().Name})";DisposeCounters();}
        }
        bool readFree=freeReadAt is not {} at||now-at>=FreeSpaceSeconds||now<at;
        if(readFree)freeReadAt=now;
        foreach(var drive in list)
        {
            double? read=null,write=null,active=null;
            if(!opened&&counters is not null&&counters.TryGetValue(drive.Index.ToString(CultureInfo.InvariantCulture),out var c))
            {
                try {read=Finite(c.Read());write=Finite(c.Write());active=Finite(100-c.Idle()) is {} busy?Math.Clamp(busy,0,100):null;}
                catch(Exception e) {read=write=active=null;countersFailing=true;counterError=$"Disk counters unavailable ({e.GetType().Name})";DisposeCounters();}
            }
            values[StorageMetrics.Key(drive.Id,"read")]=read;values[StorageMetrics.Key(drive.Id,"write")]=write;values[StorageMetrics.Key(drive.Id,"active")]=active;
            foreach(var volume in drive.Volumes)
            {
                string key=StorageMetrics.VolumeKey(drive.Id,volume.Letter);
                if(readFree||!free.ContainsKey(key))free[key]=freeSpace(volume.Letter);
                values[key]=free[key];
            }
        }
        if(readFree)RefreshFreeSpace();
        if(!enumerationFailing&&!countersFailing){failingSince=null;error=null;return;}
        failingSince??=now;
        error=now-failingSince.Value>=ErrorAfterSeconds?(countersFailing?counterError:enumerationError):null;
    }

    /// <summary>Puts the free space just read into the drive list the next poll publishes. Free space unknown to Windows keeps the
    /// enumeration's figure. Keys of volumes no longer listed are dropped.</summary>
    private void RefreshFreeSpace()
    {
        var listed=new HashSet<string>(StringComparer.Ordinal);
        bool changed=false;
        var updated=new List<StorageDrive>(drives.Count);
        foreach(var drive in drives)
        {
            var volumes=new List<StorageVolume>(drive.Volumes.Count);bool driveChanged=false;
            foreach(var volume in drive.Volumes)
            {
                string key=StorageMetrics.VolumeKey(drive.Id,volume.Letter);listed.Add(key);
                if(free.GetValueOrDefault(key) is long bytes&&bytes!=volume.FreeBytes){volumes.Add(volume with {FreeBytes=bytes});driveChanged=true;}
                else volumes.Add(volume);
            }
            updated.Add(driveChanged?drive with {Volumes=volumes}:drive);changed|=driveChanged;
        }
        if(changed)drives=updated;
        foreach(var key in free.Keys.Where(k=>!listed.Contains(k)).ToArray())free.Remove(key);
    }

    private static double? Finite(double value)=>double.IsFinite(value)?value:null;

    public void Dispose()=>DisposeCounters();

    private void DisposeCounters()
    {
        counters?.Dispose();
        counters=null;
    }

    private static List<WmiDisk> Enumerate()
    {
        var disks=new List<WmiDisk>();
        using var searcher=new ManagementObjectSearcher("SELECT DeviceID,Index,Model,SerialNumber,InterfaceType,MediaType,Size FROM Win32_DiskDrive");
        using var results=searcher.Get();
        foreach(ManagementObject disk in results)
        {
            using(disk)
            {
                var volumes=new List<(string Letter,string Label,long Size,long Free)>();
                using var partitions=disk.GetRelated("Win32_DiskPartition");
                foreach(ManagementObject partition in partitions)
                {
                    using var ownedPartition=partition;
                    using var logicals=partition.GetRelated("Win32_LogicalDisk");
                    foreach(ManagementObject logical in logicals)
                    {
                        using(logical)
                        {
                            var letter=(logical["DeviceID"] as string??"").TrimEnd(':');
                            if(letter.Length==0)continue;
                            volumes.Add((letter,logical["VolumeName"] as string??"",ToLong(logical["Size"]),ToLong(logical["FreeSpace"])));
                        }
                    }
                }
                string bus=disk["InterfaceType"] as string??"";
                string media=disk["MediaType"] as string??"";
                bool removable=bus.Equals("USB",StringComparison.OrdinalIgnoreCase)||media.Contains("Removable",StringComparison.OrdinalIgnoreCase)||media.Contains("External",StringComparison.OrdinalIgnoreCase);
                disks.Add(new(disk["SerialNumber"] as string,(disk["Model"] as string??"Disk").Trim(),Convert.ToInt32(disk["Index"]??0,CultureInfo.InvariantCulture),bus,ToLong(disk["Size"]),removable,
                    volumes.OrderBy(v=>v.Letter,StringComparer.Ordinal).ToList()));
            }
        }
        return disks;
    }

    private static long ToLong(object? value)=>value is null?0:Convert.ToInt64(value,CultureInfo.InvariantCulture);
}

/// <summary>
/// Every disk's PhysicalDisk counters in one PDH query: three wildcard counters, added by their English paths so they work in any
/// display language, so one collection per poll serves every disk. System.Diagnostics.PerformanceCounter re-read the whole PhysicalDisk
/// object for each value of each disk: 21 reads a poll for 7 drives, about 20 times the CPU of this collection (measured). A disk's
/// entry appears with the first collection that lists it. One caller at a time.
/// </summary>
internal sealed class PdhDiskCounterSet : DiskCounterSet
{
    internal const string ReadPath=@"\PhysicalDisk(*)\Disk Read Bytes/sec",WritePath=@"\PhysicalDisk(*)\Disk Write Bytes/sec",IdlePath=@"\PhysicalDisk(*)\% Idle Time";
    private static readonly string[] Paths=[ReadPath,WritePath,IdlePath];
    // PDH_FMT_DOUBLE | PDH_FMT_NOSCALE: without NOSCALE PDH would apply the counters' display scale to the bytes.
    private const uint Format=0x00000200|0x00001000,MoreData=0x800007D2;
    // Statuses of a counter whose values are not ready yet, as right after the priming collection: no value for any disk this poll.
    private static readonly uint[] NotReady=[0xC0000BBA,0xC0000BC6,0x800007D6,0x800007D8,0x800007D5,0x800007D1];
    // Collections in a row with a counter not ready before the set reports a failure (and is opened again later).
    private const int NotReadyLimit=20;
    // x64 PDH_FMT_COUNTERVALUE_ITEM_W, read as PdhGpuApi reads it: the name pointer, then CStatus and the 8-byte-aligned value.
    private const int ItemBytes=24,StatusOffset=8,ValueOffset=16;
    private IntPtr query;
    private readonly IntPtr[] counters=new IntPtr[3],buffers=new IntPtr[3];
    private readonly uint[] bufferBytes=new uint[3];
    // The latest collection by drive index: read bytes/s, write bytes/s and % idle; NaN where a disk had no valid value.
    private readonly Dictionary<string,double[]> latest=new(StringComparer.Ordinal);
    private List<string> names=[],previousNames=[];
    private bool namesKnown;
    private int notReady;

    /// <summary>Opens the query and takes the priming collection; throws <see cref="InvalidOperationException"/> when PDH refuses.</summary>
    public PdhDiskCounterSet()
    {
        try
        {
            Check(PdhOpenQueryW(null,IntPtr.Zero,out query));
            for(int i=0;i<Paths.Length;i++)Check(PdhAddEnglishCounterW(query,Paths[i],IntPtr.Zero,out counters[i]));
            Check(PdhCollectQueryData(query));
        }
        catch {Close();throw;}
    }

    public override bool WatchesDrives=>true;

    public override void Collect()
    {
        ObjectDisposedException.ThrowIf(query==IntPtr.Zero,this);
        DrivesChanged=false;
        Check(PdhCollectQueryData(query));
        foreach(var values in latest.Values)Array.Fill(values,double.NaN);
        bool ready=true;
        (names,previousNames)=(previousNames,names);names.Clear();
        for(int i=0;i<Paths.Length;i++)
        {
            uint status=FormattedArray(i,out uint count);
            if(Array.IndexOf(NotReady,status)>=0){ready=false;continue;}
            Check(status);
            for(int n=0;n<count;n++)
            {
                var item=buffers[i]+n*ItemBytes;
                string name=Marshal.PtrToStringUni(Marshal.ReadIntPtr(item))??"";
                if(WindowsStorage.InstanceDriveIndex(name) is not {} index)continue;
                if(i==0)names.Add(name);
                if(!latest.TryGetValue(index,out var values))
                {
                    latest[index]=values=[double.NaN,double.NaN,double.NaN];
                    this[index]=new DiskCounters(()=>values[0],()=>values[1],()=>values[2]);
                }
                // CStatus 0 and 1 are valid and new data; any other status is no reading for that disk.
                values[i]=(uint)Marshal.ReadInt32(item,StatusOffset)<=1?BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item,ValueOffset)):double.NaN;
            }
        }
        if(!ready)
        {
            // The names were not all read, so this collection says nothing about which disks exist.
            (names,previousNames)=(previousNames,names);
            if(++notReady>=NotReadyLimit)throw new InvalidOperationException("PhysicalDisk counters stayed unavailable.");
            return;
        }
        notReady=0;
        // The instance names carry each disk's letters, so a new or removed disk or a changed letter shows here.
        DrivesChanged=namesKnown&&!names.SequenceEqual(previousNames,StringComparer.Ordinal);
        namesKnown=true;
    }

    public override void Dispose()
    {
        base.Dispose();
        Close();
    }

    /// <summary>Idempotent: closes the query and frees each buffer.</summary>
    private void Close()
    {
        if(query!=IntPtr.Zero)PdhCloseQuery(query);
        query=IntPtr.Zero;
        for(int i=0;i<buffers.Length;i++)
        {
            counters[i]=IntPtr.Zero;
            if(buffers[i]!=IntPtr.Zero)Marshal.FreeHGlobal(buffers[i]);
            buffers[i]=IntPtr.Zero;bufferBytes[i]=0;
        }
    }

    /// <summary>One counter's formatted array into its reused buffer. As in PdhGpuApi: the size a size-0 call returns is trusted;
    /// after a too-small buffer the returned size is not, so it asks again with size 0; a third MoreData is returned as a failure.</summary>
    private uint FormattedArray(int i,out uint count)
    {
        uint size=bufferBytes[i];
        for(int call=1;;call++)
        {
            uint bytes=size;
            uint status=PdhGetFormattedCounterArrayW(counters[i],Format,ref bytes,out count,size==0?IntPtr.Zero:buffers[i]);
            if(status!=MoreData)return status;
            if(size==0&&bytes==0){count=0;return 0;}
            if(call==3)return status;
            if(size!=0){size=0;continue;}
            // Zeroed before allocating, so a failed allocation leaves nothing for Close to free twice.
            Marshal.FreeHGlobal(buffers[i]);buffers[i]=IntPtr.Zero;bufferBytes[i]=0;
            buffers[i]=Marshal.AllocHGlobal((nint)bytes);bufferBytes[i]=size=bytes;
        }
    }

    private static void Check(uint status) {if(status!=0)throw new InvalidOperationException($"PhysicalDisk counters unavailable (error 0x{status:X8}).");}

    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhOpenQueryW(string? source,IntPtr user,out IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhAddEnglishCounterW(IntPtr query,string path,IntPtr user,out IntPtr counter);
    [DllImport("pdh.dll",ExactSpelling=true)] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter,uint format,ref uint bytes,out uint count,IntPtr buffer);
    [DllImport("pdh.dll",ExactSpelling=true)] private static extern uint PdhCloseQuery(IntPtr query);
}
