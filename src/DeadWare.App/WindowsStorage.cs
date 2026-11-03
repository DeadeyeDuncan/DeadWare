using System.Diagnostics;
using System.Globalization;
using System.Management;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Per-poll storage fast path: drive enumeration plus throughput, activity and free space.</summary>
internal interface IStorageFastSource
{
    IReadOnlyList<StorageDrive> Drives(double now);
    void Read(IReadOnlyList<StorageDrive> drives,Dictionary<string,double?> values,out string? error);
}

internal sealed record WmiDisk(string? Serial,string Model,int Index,string BusType,long Size,bool Removable,IReadOnlyList<(string Letter,string Label,long Size,long Free)> Volumes);

/// <summary>One physical disk's rate counters (bytes/s read, bytes/s write, % idle).</summary>
internal sealed class DiskCounters(Func<double> read,Func<double> write,Func<double> idle,IDisposable? owner=null) : IDisposable
{
    public double Read()=>read();
    public double Write()=>write();
    public double Idle()=>idle();
    public void Dispose()=>owner?.Dispose();
}

/// <summary>WMI enumeration and PhysicalDisk counters, both retried at most every 60 s; never throws to the collector.</summary>
internal sealed class WindowsStorage : IStorageFastSource, IDisposable
{
    internal const double EnumerationSeconds=60,ErrorAfterSeconds=60;
    private readonly Func<IEnumerable<WmiDisk>> enumerate;
    private readonly Func<Dictionary<string,DiskCounters>> openCounters;
    private IReadOnlyList<StorageDrive> drives=[];
    private double now;
    private double? enumerationAttempted,counterAttempted,failingSince;
    private bool enumerationFailing,countersFailing;
    private string? enumerationError,counterError;
    private Dictionary<string,DiskCounters>? counters;

    public WindowsStorage():this(Enumerate,OpenCounters) {}
    internal WindowsStorage(Func<IEnumerable<WmiDisk>> enumerate,Func<Dictionary<string,DiskCounters>> openCounters)
    {this.enumerate=enumerate;this.openCounters=openCounters;}

    /// <summary>"0 C: D:" → "0", "2" → "2", "_Total" → null.</summary>
    internal static string? InstanceDriveIndex(string instanceName)
    {
        var first=instanceName.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null&&first.All(char.IsAsciiDigit)?first:null;
    }

    internal static IReadOnlyList<StorageDrive> Parse(IEnumerable<WmiDisk> disks)=>
        disks.OrderBy(d=>d.Index).Select(d=>new StorageDrive(StorageProjection.DriveId(d.Serial,d.Model,d.Index),StorageProjection.Normalize(d.Serial),d.Model,d.Index,d.BusType,d.Size,d.Removable,
            d.Volumes.Select(v=>new StorageVolume(v.Letter,v.Label,v.Size,v.Free)).ToList())).ToList();

    /// <summary>Also sets the clock that <see cref="Read"/> uses for its retry and error grace.</summary>
    public IReadOnlyList<StorageDrive> Drives(double now)
    {
        this.now=now;
        if(enumerationAttempted is {} last&&now-last<EnumerationSeconds)return drives;
        // The attempt time is recorded on failure too, so a broken WMI is retried once per interval, not every poll.
        enumerationAttempted=now;
        try {drives=Parse(enumerate());enumerationFailing=false;DisposeCounters();counterAttempted=null;}
        catch(Exception e) {enumerationFailing=true;enumerationError=$"Drive enumeration failed ({e.GetType().Name})";}
        return drives;
    }

    public void Read(IReadOnlyList<StorageDrive> list,Dictionary<string,double?> values,out string? error)
    {
        // Open at most once per Read, and after a failure only once per enumeration interval.
        if(counters is null&&list.Count>0&&(counterAttempted is not {} tried||now-tried>=EnumerationSeconds))
        {
            counterAttempted=now;
            try {counters=openCounters();countersFailing=false;}
            catch(Exception e) {countersFailing=true;counterError=$"Disk counters unavailable ({e.GetType().Name})";}
        }
        foreach(var drive in list)
        {
            double? read=null,write=null,active=null;
            if(counters is not null&&counters.TryGetValue(drive.Index.ToString(CultureInfo.InvariantCulture),out var c))
            {
                try {read=c.Read();write=c.Write();active=Math.Clamp(100-c.Idle(),0,100);}
                catch(Exception e) {read=write=active=null;countersFailing=true;counterError=$"Disk counters unavailable ({e.GetType().Name})";DisposeCounters();}
            }
            values[StorageMetrics.Key(drive.Id,"read")]=read;values[StorageMetrics.Key(drive.Id,"write")]=write;values[StorageMetrics.Key(drive.Id,"active")]=active;
            foreach(var volume in drive.Volumes)
            {
                double? free=null;
                try {free=new System.IO.DriveInfo(volume.Letter+":\\").TotalFreeSpace;} catch(Exception) {}
                values[StorageMetrics.VolumeKey(drive.Id,volume.Letter)]=free;
            }
        }
        if(!enumerationFailing&&!countersFailing){failingSince=null;error=null;return;}
        failingSince??=now;
        error=now-failingSince.Value>=ErrorAfterSeconds?(countersFailing?counterError:enumerationError):null;
    }

    public void Dispose()=>DisposeCounters();

    private static Dictionary<string,DiskCounters> OpenCounters()
    {
        var result=new Dictionary<string,DiskCounters>(StringComparer.Ordinal);
        try
        {
            foreach(var instance in new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames())
            {
                // "_Total" has no index and is ignored; a letterless "2" still maps to drive index 2.
                if(InstanceDriveIndex(instance) is not {} index||result.ContainsKey(index))continue;
                var set=new CounterSet();
                try
                {
                    var r=set.Add(new PerformanceCounter("PhysicalDisk","Disk Read Bytes/sec",instance,true));
                    var w=set.Add(new PerformanceCounter("PhysicalDisk","Disk Write Bytes/sec",instance,true));
                    var i=set.Add(new PerformanceCounter("PhysicalDisk","% Idle Time",instance,true));
                    r.NextValue();w.NextValue();i.NextValue();
                    result[index]=new(()=>r.NextValue(),()=>w.NextValue(),()=>i.NextValue(),set);
                }
                catch {set.Dispose();throw;}
            }
            return result;
        }
        catch
        {
            foreach(var c in result.Values)c.Dispose();
            throw;
        }
    }

    private sealed class CounterSet : IDisposable
    {
        private readonly List<PerformanceCounter> items=[];
        public PerformanceCounter Add(PerformanceCounter c){items.Add(c);return c;}
        public void Dispose(){foreach(var c in items)c.Dispose();}
    }

    private void DisposeCounters()
    {
        foreach(var c in counters?.Values??Enumerable.Empty<DiskCounters>())c.Dispose();
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
